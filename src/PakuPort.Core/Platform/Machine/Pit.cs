namespace PakuPort.Core.Platform;

/// <summary>
/// 8253/8254 Programmable Interval Timer. 3 canales en puertos 0x40–0x42; control en 0x43.
/// Canal 0 → IRQ0 (timer de sistema). Canal 1 → DRAM refresh (ignorado). Canal 2 → speaker.
/// Modos implementados: 0 (one-shot), 2 (rate generator) y 3 (square wave), latch counter.
/// Adaptado de dosbox-x src/hardware/timer.cpp.
/// </summary>
public sealed class Pit
{
    private const double OscHz = 1_193_182.0; // frecuencia del oscilador del PIT

    private readonly PitChannel[] _ch = { new(), new(), new() };

    /// <summary>Frecuencia de desbordamiento del canal 0 (Hz). Usada por MachineTimer.</summary>
    public double Channel0FrequencyHz
    {
        get
        {
            uint reload = _ch[0].Reload == 0 ? 65536u : _ch[0].Reload;
            return OscHz / reload;
        }
    }

    /// <summary>Acceso directo al canal 2 para el Speaker.</summary>
    public PitChannel Channel2 => _ch[2];

    /// <summary>Valor VIVO del contador del canal: cuenta atrás continua derivada del reloj
    /// de pasos de la CPU (~1 paso ≈ 1 tick PIT, una máquina de ~1.2 MIPS, clase 286 de la
    /// época). El 8253 real decrementa a 1.193182 MHz; lo que importa a los juegos es que
    /// dos lecturas separadas por un delay loop difieran proporcionalmente al trabajo hecho
    /// (calibración de velocidad, p.ej. el driver AdLib de un juego de prueba: 20800/Δticks).</summary>
    private static uint CurrentCount(PitChannel ch)
    {
        uint reload = ch.Reload == 0 ? 65536u : ch.Reload;
        return (uint)(reload - 1 - (uint)((ulong)Cpu.TickCount % reload));
    }

    public byte In(ushort port)
    {
        int idx = port - 0x40;
        if (idx < 0 || idx > 2) return 0xFF;
        return _ch[idx].Read(CurrentCount(_ch[idx]));
    }

    public void Out(ushort port, byte v)
    {
        if (port == 0x43)
        {
            // Control word: bits 7-6 = canal, 5-4 = R/W, 3-1 = modo, 0 = BCD
            int idx  = (v >> 6) & 0x03;
            int rw   = (v >> 4) & 0x03;
            int mode = (v >> 1) & 0x07;
            bool bcd = (v & 0x01) != 0;

            if (idx == 3)
            {
                // Read-back command (8254 only) — ignorar
                return;
            }

            var ch = _ch[idx];

            if (rw == 0)
            {
                // Latch counter command
                ch.LatchCounter(CurrentCount(ch));
            }
            else
            {
                ch.SetMode(rw, mode, bcd);
            }
            return;
        }

        int chIdx = port - 0x40;
        if (chIdx < 0 || chIdx > 2) return;
        _ch[chIdx].Write(v);
    }

    // ---- clase interna de canal ----

    public sealed class PitChannel
    {
        public uint  Reload      = 0;       // valor de recarga (0 = 65536)
        public int   Mode        = 3;       // modo del canal
        public bool  Bcd         = false;
        public int   Rw          = 3;       // 1=LSB, 2=MSB, 3=LSB+MSB

        private bool  _latchActive = false;
        private uint  _latch       = 0;
        private bool  _hiByteNext  = false; // para R/W=3: false=LSB primero

        // estado de escritura (carga del reload)
        private bool  _writeHi  = false;
        private uint  _writeBuf = 0;

        public void SetMode(int rw, int mode, bool bcd)
        {
            Rw   = rw;
            Mode = mode & 0x03; // normalizar modos 4/5 → 2/3
            if (mode == 6) Mode = 2;
            if (mode == 7) Mode = 3;
            Bcd  = bcd;
            _latchActive = false;
            _hiByteNext  = false;
            _writeHi     = false;
            _writeBuf    = 0;
        }

        public void LatchCounter(uint current)
        {
            if (!_latchActive)
            {
                _latch = current;
                _latchActive = true;
            }
        }

        public void Write(byte v)
        {
            switch (Rw)
            {
                case 1: // solo LSB
                    Reload = v;
                    break;
                case 2: // solo MSB
                    Reload = (uint)(v << 8);
                    break;
                case 3: // LSB luego MSB
                    if (!_writeHi)
                    {
                        _writeBuf = v;
                        _writeHi  = true;
                    }
                    else
                    {
                        Reload   = _writeBuf | (uint)(v << 8);
                        _writeHi = false;
                    }
                    break;
            }
        }

        public byte Read(uint current)
        {
            uint val = _latchActive ? _latch : current;

            switch (Rw)
            {
                case 1: // solo LSB
                    if (_latchActive) _latchActive = false;
                    return (byte)(val & 0xFF);
                case 2: // solo MSB
                    if (_latchActive) _latchActive = false;
                    return (byte)(val >> 8);
                case 3: // LSB luego MSB
                    if (!_hiByteNext)
                    {
                        _hiByteNext = true;
                        return (byte)(val & 0xFF);
                    }
                    else
                    {
                        _hiByteNext  = false;
                        _latchActive = false;
                        return (byte)(val >> 8);
                    }
                default:
                    return (byte)(val & 0xFF);
            }
        }
    }
}
