namespace PakuPort.Core.Platform;

/// <summary>
/// Los DOS 8237 del PC/AT: el controlador 0 (canales 0-3, transferencias de BYTE, puertos
/// 0x00-0x0F) y el controlador 1 (canales 4-7, transferencias de WORD, puertos 0xC0-0xDF).
///
/// El segundo llegó el 2026-08-24 con el SB16: el driver de sonido de un juego de prueba programa la página
/// del canal 5 (puerto 0x8B) y luego consulta su contador en bucle para saber por dónde va la
/// reproducción. Sin ese controlador, la escritura de página caía en OTRO canal (el índice se
/// enmascaraba con &3, o sea 0x8B acababa en el canal 3) y las lecturas del contador devolvían
/// 0xFF: el driver esperaba un puntero que no se movía nunca. No se modela el auto-bank de PC-98.
///
/// Las tres diferencias del controlador de 16 bits, y las tres importan:
///   1. Sus registros están al DOBLE de separación (canal 5: dirección 0xC4, cuenta 0xC6).
///   2. Dirección y cuenta se cuentan en WORDS, no en bytes: la dirección física sale de
///      `(pagina &amp; 0xFE) &lt;&lt; 16 | (addr &lt;&lt; 1)` y cada unidad de cuenta son DOS bytes.
///   3. Tiene su PROPIO flip-flop de byte alto/bajo, independiente del otro controlador.
/// Portado de dosbox-x src/hardware/dma.cpp (WriteControllerReg:285, ReadControllerReg:365,
/// DmaChannel::Read:442, registros de página:211/263) e include/dma.h (SetPage/ReachedTC).
/// </summary>
public sealed class Dma8237
{
    private const int DmatWrite = 1; // enum DMATransfer::DMAT_WRITE (I/O -> mem)
    private const int DmatRead = 2;  // enum DMATransfer::DMAT_READ (mem -> I/O)

    /// <summary>Eventos de canal (dma.h enum DMAEvent:23). Solo modelamos los de máscara.</summary>
    public enum DmaEvent { Masked, Unmasked }

    public delegate void DmaCallback(DmaChannel ch, DmaEvent ev);

    /// <summary>Un canal del 8237 (estado de dirección/cuenta/modo/máscara).</summary>
    public sealed class DmaChannel
    {
        /// <summary>
        /// Callback del dispositivo (dma.h DmaChannel::callback:108). Lo usa el DSP para los
        /// comandos que RESPONDEN por DMA (SB 0xE2): se registra y dispara al desenmascarar.
        /// </summary>
        public DmaCallback? Callback;

        public void DoCallBack(DmaEvent ev) => Callback?.Invoke(this, ev);

        /// <summary>dma.h DmaChannel::SetMask:111 — cambiar la máscara avisa al dispositivo.</summary>
        public void SetMask(bool mask)
        {
            Masked = mask;
            DoCallBack(mask ? DmaEvent.Masked : DmaEvent.Unmasked);
        }

        public uint PageBase;     // pagenum << 16 (canal de 8 bits)
        public byte PageNum;
        public ushort BaseAddr, CurrAddr;
        public ushort BaseCount, CurrCount;
        public byte Mode;
        public int TransferMode;  // (mode >> 2) & 3: 2 = READ (mem -> I/O)
        public bool Increment = true;
        public bool AutoInit;
        public bool Masked = true;
        public bool TCount;       // terminal count alcanzado (clear-on-read en status)
        public bool Request;

        /// <summary>Canal de 16 bits (4-7): cuenta y direcciona en WORDS.</summary>
        public bool Word16;

        public void SetPage(byte v)
        {
            PageNum = v;
            // En un canal de 16 bits el bit 0 de la página NO cuenta: la dirección física se
            // forma con la página par y la dirección desplazada un bit. Dejarlo puesto mete un
            // desplazamiento de 64 KiB y el buffer que se lee es otro.
            PageBase = Word16 ? (uint)(v & 0xFE) << 16 : (uint)v << 16;
        }

        /// <summary>Dirección física del puntero actual.</summary>
        public uint Fisica => Word16 ? PageBase | ((uint)CurrAddr << 1) : PageBase | CurrAddr;
    }

    private readonly DmaChannel[] _ch = CreaCanales();
    private bool _flipflop;      // controlador 0
    private bool _flipflop16;    // controlador 1: es OTRO, no el mismo

    private static DmaChannel[] CreaCanales()
    {
        var v = new DmaChannel[8];
        for (int i = 0; i < 8; i++) v[i] = new DmaChannel { Word16 = i >= 4 };
        return v;
    }

    public DmaChannel Channel(int n) => _ch[n & 7];

    // ---- puertos ----

    public byte PortIn(ushort port)
    {
        if (port <= 0x0F) return ReadControllerReg(port);
        if (port is >= 0xC0 and <= 0xDF) return ReadControllerReg16(port);
        // registros de página (lectura devuelve pagenum)
        switch (port)
        {
            case 0x87: return _ch[0].PageNum;
            case 0x83: return _ch[1].PageNum;
            case 0x81: return _ch[2].PageNum;
            case 0x82: return _ch[3].PageNum;
            case 0x8F: return _ch[4].PageNum;
            case 0x8B: return _ch[5].PageNum;
            case 0x89: return _ch[6].PageNum;
            case 0x8A: return _ch[7].PageNum;
        }
        return 0xFF;
    }

    /// <summary>PAKU_LOG_DMA=1: cada escritura a un registro de los dos controladores DMA, con su
    /// puerto y su valor. Es la telemetria que separa "el juego no programa el DMA" de "lo
    /// programa donde no miramos": el driver de un juego de prueba escribia en 0x8B —la pagina del canal 5,
    /// el de 16 bits— y esa escritura acababa en el canal 3 porque el indice se enmascaraba con
    /// &amp;3. Sin ver los puertos crudos, el sintoma era un contador que no se movia nunca.</summary>
    static readonly bool LogPuertos = Environment.GetEnvironmentVariable("PAKU_LOG_DMA") == "1";

    public void PortOut(ushort port, byte val)
    {
        if (LogPuertos)
            Console.Error.WriteLine($"[DMA] out 0x{port:X2} = 0x{val:X2}");
        if (port <= 0x0F) { WriteControllerReg(port, val); return; }
        if (port is >= 0xC0 and <= 0xDF) { WriteControllerReg16(port, val); return; }
        switch (port)
        {
            case 0x87: _ch[0].SetPage(val); break;
            case 0x83: _ch[1].SetPage(val); break;
            case 0x81: _ch[2].SetPage(val); break;
            case 0x82: _ch[3].SetPage(val); break;
            case 0x8F: _ch[4].SetPage(val); break;
            case 0x8B: _ch[5].SetPage(val); break;   // canal 5: el DMA alto de la SB16
            case 0x89: _ch[6].SetPage(val); break;
            case 0x8A: _ch[7].SetPage(val); break;
        }
    }

    // ---- registros del controlador (dma.cpp WriteControllerReg) ----

    private void WriteControllerReg(int reg, byte val)
    {
        DmaChannel c;
        switch (reg)
        {
            case 0x0: case 0x2: case 0x4: case 0x6: // dirección base+current
                c = _ch[reg >> 1];
                _flipflop = !_flipflop;
                if (_flipflop)
                {
                    c.BaseAddr = (ushort)((c.BaseAddr & 0xFF00) | val);
                    c.CurrAddr = (ushort)((c.CurrAddr & 0xFF00) | val);
                }
                else
                {
                    c.BaseAddr = (ushort)((c.BaseAddr & 0x00FF) | (val << 8));
                    c.CurrAddr = (ushort)((c.CurrAddr & 0x00FF) | (val << 8));
                }
                break;
            case 0x1: case 0x3: case 0x5: case 0x7: // cuenta base+current
                c = _ch[reg >> 1];
                _flipflop = !_flipflop;
                if (_flipflop)
                {
                    c.BaseCount = (ushort)((c.BaseCount & 0xFF00) | val);
                    c.CurrCount = (ushort)((c.CurrCount & 0xFF00) | val);
                }
                else
                {
                    c.BaseCount = (ushort)((c.BaseCount & 0x00FF) | (val << 8));
                    c.CurrCount = (ushort)((c.CurrCount & 0x00FF) | (val << 8));
                }
                break;
            case 0x8: break; // command reg — sin uso
            case 0x9: break; // request reg — sin uso
            case 0xA: // single mask register
                _ch[val & 3].SetMask((val & 0x4) > 0);
                break;
            case 0xB: // mode register
                c = _ch[val & 3];
                c.Mode = val;
                c.AutoInit = (val & 0x10) > 0;
                c.Increment = (val & 0x20) == 0;
                c.TransferMode = (val >> 2) & 3;
                break;
            case 0xC: _flipflop = false; break; // clear flip-flop
            case 0xD: // master clear/reset
                foreach (var ch in _ch) { ch.TCount = false; ch.SetMask(true); }
                _flipflop = false;
                break;
            case 0xE: // clear mask register (unmask 4)
                foreach (var ch in _ch) ch.SetMask(false);
                break;
            case 0xF: // multiple mask register (bit por canal)
                for (int i = 0; i < 4; i++) { _ch[i].SetMask((val & 1) != 0); val >>= 1; }
                break;
        }
    }

    // ---- controlador 1 (canales 4-7): mismos registros, al doble de separación ----
    //
    // 0xC0/0xC2 canal 4 · 0xC4/0xC6 canal 5 · 0xC8/0xCA canal 6 · 0xCC/0xCE canal 7
    // 0xD0 status/command · 0xD2 request · 0xD4 single mask · 0xD6 mode
    // 0xD8 clear flip-flop · 0xDA master clear · 0xDC clear mask · 0xDE all mask
    //
    // El canal 4 existe pero no se usa: en el AT es la cascada del controlador 0.

    private void WriteControllerReg16(int port, byte val)
    {
        int reg = (port - 0xC0) >> 1;   // 0..15, el mismo índice que en el controlador 0
        DmaChannel c;
        switch (reg)
        {
            case 0x0: case 0x2: case 0x4: case 0x6:
                c = _ch[4 + (reg >> 1)];
                _flipflop16 = !_flipflop16;
                if (_flipflop16)
                {
                    c.BaseAddr = (ushort)((c.BaseAddr & 0xFF00) | val);
                    c.CurrAddr = (ushort)((c.CurrAddr & 0xFF00) | val);
                }
                else
                {
                    c.BaseAddr = (ushort)((c.BaseAddr & 0x00FF) | (val << 8));
                    c.CurrAddr = (ushort)((c.CurrAddr & 0x00FF) | (val << 8));
                }
                break;
            case 0x1: case 0x3: case 0x5: case 0x7:
                c = _ch[4 + (reg >> 1)];
                _flipflop16 = !_flipflop16;
                if (_flipflop16)
                {
                    c.BaseCount = (ushort)((c.BaseCount & 0xFF00) | val);
                    c.CurrCount = (ushort)((c.CurrCount & 0xFF00) | val);
                }
                else
                {
                    c.BaseCount = (ushort)((c.BaseCount & 0x00FF) | (val << 8));
                    c.CurrCount = (ushort)((c.CurrCount & 0x00FF) | (val << 8));
                }
                break;
            case 0x8: break; // command
            case 0x9: break; // request
            case 0xA: _ch[4 + (val & 3)].SetMask((val & 0x4) > 0); break;
            case 0xB:
                c = _ch[4 + (val & 3)];
                c.Mode = val;
                c.AutoInit = (val & 0x10) > 0;
                c.Increment = (val & 0x20) == 0;
                c.TransferMode = (val >> 2) & 3;
                break;
            case 0xC: _flipflop16 = false; break;
            case 0xD:
                for (int i = 4; i < 8; i++) { _ch[i].TCount = false; _ch[i].SetMask(true); }
                _flipflop16 = false;
                break;
            case 0xE:
                for (int i = 4; i < 8; i++) _ch[i].SetMask(false);
                break;
            case 0xF:
                for (int i = 0; i < 4; i++) { _ch[4 + i].SetMask((val & 1) != 0); val >>= 1; }
                break;
        }
    }

    private byte ReadControllerReg16(int port)
    {
        int reg = (port - 0xC0) >> 1;
        DmaChannel c;
        switch (reg)
        {
            case 0x0: case 0x2: case 0x4: case 0x6:
                c = _ch[4 + (reg >> 1)];
                _flipflop16 = !_flipflop16;
                return _flipflop16 ? (byte)(c.CurrAddr & 0xFF) : (byte)((c.CurrAddr >> 8) & 0xFF);
            case 0x1: case 0x3: case 0x5: case 0x7:
                c = _ch[4 + (reg >> 1)];
                _flipflop16 = !_flipflop16;
                return _flipflop16 ? (byte)(c.CurrCount & 0xFF) : (byte)((c.CurrCount >> 8) & 0xFF);
            case 0x8:
            {
                int ret = 0;
                for (int ct = 0; ct < 4; ct++)
                {
                    if (_ch[4 + ct].TCount) ret |= 1 << ct;
                    _ch[4 + ct].TCount = false;
                    if (_ch[4 + ct].Request) ret |= 1 << (4 + ct);
                }
                return (byte)ret;
            }
            case 0xC: _flipflop16 = false; return 0xFF;
            case 0xF:
            {
                int ret = 0;
                for (int ct = 0; ct < 4; ct++) if (_ch[4 + ct].Masked) ret |= 1 << ct;
                return (byte)ret;
            }
        }
        return 0xFF;
    }

    private byte ReadControllerReg(int reg)
    {
        DmaChannel c;
        switch (reg)
        {
            case 0x0: case 0x2: case 0x4: case 0x6:
                c = _ch[reg >> 1];
                _flipflop = !_flipflop;
                return _flipflop ? (byte)(c.CurrAddr & 0xFF) : (byte)((c.CurrAddr >> 8) & 0xFF);
            case 0x1: case 0x3: case 0x5: case 0x7:
                c = _ch[reg >> 1];
                _flipflop = !_flipflop;
                return _flipflop ? (byte)(c.CurrCount & 0xFF) : (byte)((c.CurrCount >> 8) & 0xFF);
            case 0x8: // status: TC (clear-on-read) + request por canal
            {
                int ret = 0;
                for (int ct = 0; ct < 4; ct++)
                {
                    if (_ch[ct].TCount) ret |= 1 << ct;
                    _ch[ct].TCount = false;
                    if (_ch[ct].Request) ret |= 1 << (4 + ct);
                }
                return (byte)ret;
            }
            case 0xC: _flipflop = false; return 0xFF;
            case 0xF: // readback de máscaras (no documentado)
            {
                int ret = 0;
                for (int ct = 0; ct < 4; ct++) if (_ch[ct].Masked) ret |= 1 << ct;
                return (byte)ret;
            }
        }
        return 0xFF;
    }

    /// <summary>
    /// Lee un bloque DMA del canal <paramref name="channel"/> a <paramref name="buffer"/>
    /// (equivalente mínimo de DmaChannel::Read, dma.cpp:442). Devuelve nº de bytes leídos.
    /// NO lee si el canal está enmascarado o no está en modo READ. Modelo single-cycle sin
    /// troceo de 4KB (los bloques del SB no cruzan página): dirección de 16 bits que envuelve
    /// dentro de la página de 64K. Marca TC y (single-cycle) enmascara el canal al terminar.
    /// </summary>
    public int Read(int channel, Memory mem, byte[] buffer, int want)
    {
        var c = _ch[channel & 7];
        if (c.Masked || c.TransferMode != DmatRead) return 0;
        int paso = c.Word16 ? 2 : 1;   // una unidad de cuenta son DOS bytes en el controlador 1
        int done = 0;
        while (want >= paso)
        {
            uint phys = c.Fisica;
            if (phys + paso > (uint)Memory.Size) break; // guarda: fuera del megabyte
            buffer[done] = mem.Raw[phys];
            if (paso == 2) buffer[done + 1] = mem.Raw[phys + 1];
            if (c.Increment) c.CurrAddr = (ushort)(c.CurrAddr + 1);
            else c.CurrAddr = (ushort)(c.CurrAddr - 1);
            c.CurrCount = (ushort)(c.CurrCount - 1);
            want -= paso; done += paso;
            if (c.CurrCount == 0xFFFF) // desbordó por debajo de 0 -> terminal count
            {
                c.TCount = true;
                if (c.AutoInit) { c.CurrCount = c.BaseCount; c.CurrAddr = c.BaseAddr; }
                else { c.Masked = true; } // el 8237 enmascara el canal al TC (single-cycle)
                break;
            }
        }
        return done;
    }

    /// <summary>
    /// Escribe un bloque del dispositivo a memoria (I/O -> mem), equivalente mínimo de
    /// DmaChannel::Write (dma.cpp:558). Lo usan los comandos del DSP que responden por DMA
    /// (SB 0xE2). Mismas guardas que Read: canal enmascarado o no configurado en modo WRITE
    /// no transfiere. Devuelve nº de bytes escritos.
    /// </summary>
    public int Write(int channel, Memory mem, byte[] buffer, int want)
    {
        var c = _ch[channel & 7];
        if (c.Masked || c.TransferMode != DmatWrite) return 0;
        int paso = c.Word16 ? 2 : 1;
        int done = 0;
        while (want >= paso)
        {
            uint phys = c.Fisica;
            if (phys + paso > (uint)Memory.Size) break;
            mem.Raw[phys] = buffer[done];
            if (paso == 2) mem.Raw[phys + 1] = buffer[done + 1];
            if (c.Increment) c.CurrAddr = (ushort)(c.CurrAddr + 1);
            else c.CurrAddr = (ushort)(c.CurrAddr - 1);
            c.CurrCount = (ushort)(c.CurrCount - 1);
            want -= paso; done += paso;
            if (c.CurrCount == 0xFFFF)
            {
                c.TCount = true;
                if (c.AutoInit) { c.CurrCount = c.BaseCount; c.CurrAddr = c.BaseAddr; }
                else { c.Masked = true; }
                break;
            }
        }
        return done;
    }

    /// <summary>
    /// Registra el callback del dispositivo en un canal (dma.h Register_Callback:125). Como
    /// en DOSBox, registrar reevalúa la máscara al vuelo: si el canal YA está desenmascarado
    /// el dispositivo recibe su evento en el acto.
    /// </summary>
    public void RegisterCallback(int channel, DmaCallback? cb)
    {
        var c = _ch[channel & 7];
        c.Callback = cb;
        c.SetMask(c.Masked);
    }
}
