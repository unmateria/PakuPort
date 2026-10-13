using System.Diagnostics;

namespace PakuPort.Core.Platform;

/// <summary>
/// SoundBlaster 2.0 (DSP + DMA single-cycle 8-bit) — clase GENÉRICA de audio digital SB.
/// Portado de dosbox-x src/hardware/sblaster.cpp (DSP_DoWrite:1763, DSP_DoCommand:1894,
/// DSP_DoReset:1490, DSP_AddData:1300, DSP_ReadData:1311, DSP_PrepareDMA_Old:1322,
/// read_sb/write_sb:3129/3221). Alcance mínimo: reset, versión, set-time-constant, enable/
/// disable speaker, halt, y reproducción single-cycle 8-bit por DMA. Fuera de alcance
/// (otras clases): OPL/FM, SB16/auto-init/ADPCM/16-bit/stereo, MIDI, grabación (ADC).
///
/// La entrega del IRQ 7 va acompasada a TIEMPO REAL (PollDueIrq, llamado por MachineTimer en
/// el hilo del juego): si se disparara de inmediato el driver encadenaría bloques tan rápido
/// como la CPU y el audio iría acelerado; programándolo a bytes/rate segundos el ritmo del
/// driver queda anclado al reloj (idéntico a END_DMA_Event de DOSBox).
/// </summary>
public sealed class SoundBlaster
{
    // ---- offsets de puerto (relativos a la base, sblaster.cpp #define) ----
    private const int MIXER_INDEX = 0x04, MIXER_DATA = 0x05;
    private const int DSP_RESET = 0x06, DSP_READ_DATA = 0x0A;
    private const int DSP_WRITE_DATA = 0x0C, DSP_WRITE_STATUS = 0x0C, DSP_READ_STATUS = 0x0E;
    private const int DSP_ACK_IRQ16 = 0x0F;

    // ---- estados del DSP (sblaster.cpp: enum DSP_S_*) ----
    private const int S_RESET = 0, S_RESET_WAIT = 1, S_NORMAL = 2;

    /// <summary>PAKU_LOG_SB=1: comandos del DSP, transferencias de DMA e IRQ de la tarjeta. Lo
    /// primero que hay que mirar cuando un juego se planta con el sonido instalado.</summary>
    private static readonly bool _log =
        Environment.GetEnvironmentVariable("PAKU_LOG_SB") == "1";

    /// <summary>Lo que un juego le ha pedido a la tarjeta y NO está modelado. Existe para que
    /// aparezca en el informe de la partida: si a alguien le falta un sonido, la respuesta tiene
    /// que estar en un fichero de log, no en que se le ocurra encender una traza.</summary>
    public static readonly List<string> NoModelado = new();

    private readonly ushort _base;
    private readonly int _irq;
    private readonly int _dmaChannel;
    private readonly Dma8237 _dma;
    private readonly HostAudio? _audio;
    private readonly HostAudio.Voice? _voice;

    private int _state = S_NORMAL;
    private byte _cmd;              // comando en curso (0 = ninguno)
    private int _cmdLen;           // bytes de parámetro que faltan
    private readonly byte[] _in = new byte[8];
    private int _inPos;
    private readonly Queue<byte> _out = new();
    private byte _outLast;         // último byte leído (el HW lo repite si la cola está vacía)
    private bool _speakerOn;
    private byte _timeConst = 45;   // reset value de DOSBox
    private int _sampleRate = 22050;

    // Registro de test (comandos 0xE4/0xE8): un byte que se escribe y se relee tal cual. Es LA
    // prueba de presencia de casi todo driver SB — sin él la tarjeta "no existe".
    private byte _testRegister;

    // Acumulador de la identificación por DMA (0xE2). Valores de reset en sblaster.cpp:1475.
    private byte _e2ValAdd = 0xAA;
    private byte _e2ValXor = 0x96;
    private readonly byte[] _e2Buf = new byte[1];

    // ---- transferencia DMA en curso + IRQ acompasado ----
    private long _irqDueTicks;      // Stopwatch.GetTimestamp() en que vence el IRQ del bloque
    private long _irqDueSteps;      // Cpu.TickCount en que vence (bloques cortos, reloj invitado)
    private bool _irqOnGuestClock;  // el plazo de este bloque va por el reloj del invitado
    private bool _irqArmed;         // hay un bloque esperando disparar su IRQ
    private bool _irqPending;       // latch del SB (se limpia al leer 0x22E, como pending_8bit)
    private long _irq7Count;        // nº de IRQ 7 disparados (= nº de bloques single-cycle)
    private readonly byte[] _dmaBuf = new byte[65536];
    private short[] _pcm = new short[65536];   // bloque completo ya reamuestreado al host
    private short[] _feed = new short[8192];   // trozo que se entrega al anillo en cada dosis
    private int _pendingLen;                   // muestras válidas en _pcm
    private int _pendingPos;                   // cuántas de ellas ya se han entregado

    public long Irq7Count => _irq7Count;
    public ushort BasePort => _base;

    public SoundBlaster(ushort basePort, int irq, int dmaChannel, Dma8237 dma, HostAudio? audio)
    {
        _base = basePort;
        _irq = irq;
        _dmaChannel = dmaChannel;
        _dma = dma;
        _audio = audio;
        _canalEnUso = _dmaChannel;
        _voice = audio?.CreateVoice();
        if (_log)
            Console.Error.WriteLine($"[SB] init base=0x{_base:X4} irq={_irq} dma={_dmaChannel} audio={(_audio != null ? _audio.SampleRate + "Hz" : "off")}");
    }

    // ---- puertos ----

    private int Offset(ushort port)
    {
        int off = (port - _base) & 0xF;
        // En una SB16, base+0x0F es el RECONOCIMIENTO DE LA IRQ DE 16 BITS: un puerto propio, no
        // un alias de base+0x0E. En una SB 2.0 sí es alias, y así se comportaba esto para todos.
        // Se separa sólo cuando hay un flujo de 16 bits de por medio, que es cuando el puerto
        // significa otra cosa: los siete juegos con sonido ya validados no pasan por aquí.
        if (off == DSP_ACK_IRQ16 && _irqDeBloqueEs16) return DSP_ACK_IRQ16;
        // Aliasing SB 2.0: todos los puertos salvo el índice/dato del mixer descartan bit 0.
        if (off < MIXER_INDEX || off > MIXER_DATA) off &= ~1;
        return off;
    }

    // ---- mezclador (CT1745 del SB16). Portado de sblaster.cpp CTMIXER_Read/Write ----
    //
    // Antes no existía y el dato del mezclador devolvía 0xFF a todo. Eso NO es inocuo: la rutina
    // canónica para detectar una Sound Blaster es escribir un valor en un registro del mezclador
    // y leerlo de vuelta, así que una tarjeta que nunca devuelve lo escrito NO SE DETECTA. Es lo
    // que le pasaba al driver de HMI de un juego DOS/4GW ("Detection System Failed To Find Hardware"):
    // sondeaba el registro 0x3F en cada base posible y siempre recibía 0xFF.
    //
    // DOSBox-X guarda los índices no modelados en un array `mixer.unhandled` y los devuelve tal
    // cual en una SB16 — que es exactamente ese contrato de escribir-y-releer.
    private byte _mixerIndex;
    private readonly byte[] _mixer = new byte[256];

    private byte MixerRead() => _mixerIndex switch
    {
        // 0x80 selección de IRQ: IRQ2->1, IRQ5->2, IRQ7->4, IRQ10->8
        0x80 => (byte)(_irq switch { 2 => 0x01, 5 => 0x02, 7 => 0x04, 10 => 0x08, _ => 0x00 }),
        // 0x81 selección de DMA: canal de 8 bits en los bits bajos, canal de 16 en 5/6/7
        0x81 => (byte)((_dmaChannel switch { 0 => 0x01, 1 => 0x02, 3 => 0x08, _ => 0x00 })
                     | (_dmaCanal16 switch { 5 => 0x20, 6 => 0x40, 7 => 0x80, _ => 0x00 })),
        // 0x82 estado de IRQ: bit 0 = la de 8 bits, bit 1 = la de 16; el bit 5 identifica a la
        // SB16. Sin el bit 1, el manejador de un driver de 16 bits no reconoce su propia IRQ y
        // decide que no es suya.
        0x82 => (byte)((_irqPending ? 0x01 : 0x00) | (_irqPending16 ? 0x02 : 0x00) | 0x20),
        _ => _mixer[_mixerIndex],
    };

    private void MixerWrite(byte val)
    {
        if (_mixerIndex == 0x00)
        {
            // Reset del mezclador: volúmenes a su valor de fábrica.
            System.Array.Clear(_mixer);
            _mixer[0x22] = _mixer[0x30] = _mixer[0x31] = 0xC0;   // maestro
            _mixer[0x04] = _mixer[0x32] = _mixer[0x33] = 0xC0;   // DAC
            _mixer[0x26] = _mixer[0x34] = _mixer[0x35] = 0xC0;   // FM
            return;
        }
        if (_mixerIndex == 0x81)
        {
            // Asignación de canales DMA (sblaster.cpp:2920). El de 16 bits sólo puede ser 5, 6
            // o 7; que no se asigne ninguno es lo normal, y es lo que deja el modo "aliased".
            if ((val & 0x20) != 0) _dmaCanal16 = 5;
            else if ((val & 0x40) != 0) _dmaCanal16 = 6;
            else if ((val & 0x80) != 0) _dmaCanal16 = 7;
            else _dmaCanal16 = -1;
            if (_log) Console.Error.WriteLine($"[SB] mezclador 0x81: canal de 16 bits -> {_dmaCanal16}");
        }
        _mixer[_mixerIndex] = val;
    }

    public byte PortIn(ushort port)
    {
        switch (Offset(port))
        {
            case MIXER_INDEX:
                return _mixerIndex;
            case MIXER_DATA:
                return MixerRead();
            case DSP_READ_DATA:
                return ReadData();
            case DSP_READ_STATUS: // bit7=1 (0xAA) si hay dato; ack del IRQ 8-bit al leer
                if (_irqPending) _irqPending = false;
                return (byte)(_out.Count > 0 ? 0xAA : 0x2A);
            case DSP_ACK_IRQ16:   // reconocimiento de la IRQ de 16 bits (SB16)
                _irqPending16 = false;
                return 0xFF;
            case DSP_WRITE_STATUS: // bit7=0 (0x2A) = listo para escribir (modelo instantáneo)
                return (byte)(_state == S_NORMAL ? 0x2A : 0xFF);
            case DSP_RESET:
                return 0xFF;
        }
        return 0xFF;
    }

    public void PortOut(ushort port, byte val)
    {
        switch (Offset(port))
        {
            case DSP_RESET: DoReset(val); break;
            case DSP_WRITE_DATA: DoWrite(val); break;
            case MIXER_INDEX: _mixerIndex = val; break;
            case MIXER_DATA: MixerWrite(val); break;
        }
    }

    // ---- reset (sblaster.cpp DSP_DoReset:1490) ----

    private void DoReset(byte val)
    {
        if ((val & 1) != 0 && _state != S_RESET)
        {
            Reset();
            _state = S_RESET;
        }
        else if ((val & 1) == 0 && _state == S_RESET)
        {
            // DOSBox programa el ack 0xAA a 20 µs (evento PIC). Aquí lo hacemos síncrono:
            // el driver poll-ea 0x22E en bucle, así que basta con que aparezca de inmediato.
            _out.Clear();
            _out.Enqueue(0xAA);
            _state = S_NORMAL;
            if (_log) Console.Error.WriteLine("[SB] reset -> 0xAA");
        }
    }

    private void Reset()
    {
        _out.Clear();
        _cmd = 0; _cmdLen = 0; _inPos = 0;
        _speakerOn = false;
        DisarmIrq();
        _irqPending = false; _irqPending16 = false;
        FormatoSb2();
        _sampleRate = 22050; _timeConst = 45;
        // sblaster.cpp DSP_Reset:1475 — semilla del acumulador 0xE2 y baja del callback de DMA
        // (un reset a media identificación no debe dejar el canal enganchado, sblaster.cpp:1455).
        _e2ValAdd = 0xAA; _e2ValXor = 0x96;
        if (_dma.Channel(_dmaChannel).Callback == E2DmaCallback)
            _dma.RegisterCallback(_dmaChannel, null);
    }

    /// <summary>
    /// Respuesta del comando 0xE2: en cuanto el canal DMA queda desenmascarado la tarjeta
    /// deposita UN byte (el acumulador) en memoria y se desengancha
    /// (sblaster.cpp DSP_E2_DMA_CallBack:3822).
    /// </summary>
    private void E2DmaCallback(Dma8237.DmaChannel ch, Dma8237.DmaEvent ev)
    {
        if (ev != Dma8237.DmaEvent.Unmasked || _mem == null) return;
        ch.Callback = null;
        _e2Buf[0] = _e2ValAdd;
        int w = _dma.Write(_dmaChannel, _mem, _e2Buf, 1);
        if (_log) Console.Error.WriteLine($"[SB]   0xE2 -> DMA escribe 0x{_e2ValAdd:X2} ({w} byte)");
    }

    // ---- escritura de comando/dato (sblaster.cpp DSP_DoWrite:1763) ----

    // Bytes de parámetro por comando, portado VERBATIM de DSP_cmd_len_sb (sblaster.cpp:179).
    // Tenerlo completo no es lujo: si un comando declara menos parámetros de los que el driver
    // escribe, los bytes sobrantes se interpretan como COMANDOS y el DSP se desincroniza (era
    // el caso de 0xE2/0xE4, cuyos parámetros aparecían en el log como comandos fantasma).
    private static readonly byte[] CmdLenSb = new byte[256]
    {
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,  // 0x00
        1,0,0,0, 2,2,2,2, 0,0,0,0, 0,0,0,0,  // 0x10
        0,0,0,0, 2,0,0,0, 0,0,0,0, 0,0,0,0,  // 0x20
        0,0,0,0, 0,0,0,0, 1,0,0,0, 0,0,0,0,  // 0x30
        1,2,2,0, 0,0,0,0, 2,0,0,0, 0,0,0,0,  // 0x40
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,  // 0x50
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,  // 0x60
        0,0,0,0, 2,2,2,2, 0,0,0,0, 0,0,0,0,  // 0x70
        2,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,  // 0x80
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,  // 0x90
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,  // 0xA0
        // 0xB0-0xCF llevan 3 parámetros (modo, lo, hi) — fila tomada de DSP_cmd_len_sb16
        // (sblaster.cpp:248), no de la de SB Pro, que los da como cero. Con cero, los tres bytes
        // del driver de ese juego se leian como COMANDOS: en el log salian `cmd 0x00`,
        // `cmd 0xFF` y `cmd 0x0F` fantasma, y el DSP se quedaba desincronizado.
        3,3,3,3, 3,3,3,3, 3,3,3,3, 3,3,3,3,  // 0xB0
        3,3,3,3, 3,3,3,3, 3,3,3,3, 3,3,3,3,  // 0xC0
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,  // 0xD0
        1,0,1,0, 1,0,0,0, 0,0,0,0, 0,0,0,0,  // 0xE0
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0   // 0xF0
    };

    private static int CmdLen(byte cmd) => CmdLenSb[cmd];

    private void DoWrite(byte val)
    {
        if (_cmd == 0)
        {
            _cmd = val;
            _cmdLen = CmdLen(val);
            _inPos = 0;
            if (_cmdLen == 0) DoCommand();
        }
        else
        {
            _in[_inPos++] = val;
            if (_inPos >= _cmdLen) DoCommand();
        }
    }

    // ---- ejecución de comando (sblaster.cpp DSP_DoCommand:1894) ----

    private void DoCommand()
    {
        byte cmd = _cmd;
        if (_log)
            Console.Error.WriteLine($"[SB] cmd 0x{cmd:X2}" + (_cmdLen > 0 ? " params=" + BitConverter.ToString(_in, 0, _cmdLen) : ""));
        switch (cmd)
        {
            case 0x10: // Direct DAC (1 muestra) — trivial
                FormatoSb2();
                EnqueuePcm(new[] { (byte)_in[0] }, 1, _sampleRate);
                break;
            case 0x14: // Single-Cycle 8-bit DMA DAC — PLAYBACK
                StartSingleCycle(cmd);
                break;
            case 0x48: // Set DMA Block Size (para auto-init) — sblaster.cpp:2210
                _blockSize = 1 + _in[0] + (_in[1] << 8);
                if (_log) Console.Error.WriteLine($"[SB]   block size = {_blockSize}");
                break;
            case 0x1C: // Auto-Init 8-bit DMA — el DMA RECIRCULA el buffer y el DSP entrega una
            case 0x2C: // IRQ por cada `block size` bytes hasta el halt (0xD0/0xDA). Antes estaba
                // "modelado como single": UNA IRQ y stream muerto — un juego de prueba encadena así sus
                // efectos digitales (0x40+0xD1+0x48+0x1C ... 0xD0) y cada efecto sonaba 92 ms y
                // se cortaba (una prueba manual lo oyó como "ni efectos, solo la música").
                // Se reutiliza el consumo continuo del camino SB16 (AvanzarDmaSb16) más el
                // contador de bloque para las IRQ.
                {
                    var chAi = _dma.Channel(_dmaChannel);
                    if (chAi.Masked || chAi.TransferMode != 2)
                    {
                        if (_log) Console.Error.WriteLine($"[SB]   0x{cmd:X2} auto-init descartado (canal enmascarado/no-READ)");
                        break;
                    }
                    FormatoSb2();
                    AbortaPendiente();   // transferencia nueva: la anterior queda abortada
                    _aiRate = _sampleRate > 0 ? _sampleRate : 8000;
                    _aiActivo = true;
                    _dmaPausado = false;   // programar un DMA nuevo levanta la pausa del 0xD0
                    _aiUltimoTick = Cpu.TickCount;
                    _aiUltimoReloj = Stopwatch.GetTimestamp();
                    _aiRestoBytes = 0;
                    _ai8Bloque = _blockSize > 0 ? _blockSize : (chAi.CurrCount + 1) / 2;
                    _ai8HastaIrq = _ai8Bloque;
                    _irqPending = false;
                    DisarmIrq();
                    Cpu.RequestFinePoll(true);
                    if (_log)
                        Console.Error.WriteLine($"[SB]   auto-init 8-bit bloque={_ai8Bloque} rate={_aiRate} " +
                                                $"count={chAi.CurrCount} buffer=0x{chAi.PageBase | chAi.CurrAddr:X5}");
                }
                break;
            // ---- SB16 (DSP 4.xx). Portado de dosbox-x sblaster.cpp:2127 (0x41) y :2215
            // (0xB0-0xCF). No cambiamos la version que declara `0xE1`: una SB16 de verdad
            // acepta estos comandos y el resto de juegos probados, que habla SB Pro, sigue igual.
            case 0x41: // Set Output Sample Rate — el rate viene BIG-ENDIAN, al reves que todo
            case 0x42: // Set Input Sample Rate
                _sampleRate = (_in[0] << 8) | _in[1];
                if (_log) Console.Error.WriteLine($"[SB]   rate SB16 -> {_sampleRate} Hz");
                break;

            case >= 0xB0 and <= 0xCF: // Program 8/16-bit DMA mode digitized sound I/O
                StartDmaSb16(cmd);
                break;

            // 0xD0/0xD5 son una PAUSA, no un final: DOSBox-X pone `mode=MODE_DMA_PAUSE`
            // (sblaster.cpp:2289) y CONSERVA direccion, contador, bloque y auto-init, para que
            // 0xD4/0xD6 (:2334) reanuden donde iban. Aqui se trataban como el 0xDA (Exit
            // auto-init): mataban el stream y ponian `_ai8Bloque = 0`, y ademas 0xD4 era un
            // no-op declarado ("comando 0xD4 no soportado"). Consecuencia medida en un juego de prueba:
            // el primer par pausa/continua del juego dejaba el sonido digital MUDO PARA SIEMPRE
            // — musica intacta (va por el OPL) y ni efectos ni voces en toda la partida.
            case 0xD5: // Halt 16-bit DMA  -> pausa
            case 0xD0: // Halt 8-bit DMA   -> pausa
                _dmaPausado = true;
                break;
            case 0xD6: // Continue DMA 16-bit
            case 0xD4: // Continue DMA 8-bit
                if (_dmaPausado) { _dmaPausado = false; ReanclarRelojDma(); }
                break;

            case 0x40: // Set Time Constant
                _timeConst = _in[0];
                _sampleRate = (int)(256000000L / (65536 - (_timeConst << 8)));
                if (_log) Console.Error.WriteLine($"[SB]   tc={_timeConst} -> {_sampleRate} Hz");
                break;
            case 0xD9: // Exit auto-init 16-bit (sblaster.cpp:2347) — mismo final que el 0xDA
            case 0xDA: // Exit auto-init 8-bit: el auto-init SI termina (sblaster.cpp:2349)
                _aiActivo = false;
                _irqArmed = false;
                _ai8Bloque = 0;
                _ai8IrqsPendientes = 0;
                break;
            case 0xD1: _speakerOn = true; break;   // Enable Speaker
            case 0xD3: _speakerOn = false; break;  // Disable Speaker
            case 0x04: // DSP Status (SB 2.0/Pro; NO SB16) — sblaster.cpp:2411 (rama else)
                FlushData(); AddData(0x88); // SBT_2
                break;
            case 0xD8: // Speaker status — sblaster.cpp:2326
                FlushData();
                AddData((byte)(_speakerOn ? 0xFF : 0x00));
                break;
            case 0xE0: // DSP Identification: encola ~inData[0] — sblaster.cpp:2355
                FlushData();
                AddData((byte)~_in[0]);
                break;
            case 0xE1: // Get DSP Version -> 2.01 (SB 2.0; el driver exige >= 1.03)
                FlushData();
                AddData(0x02); AddData(0x01);
                break;
            case 0xE2: // "Weird DMA identification write routine" — sblaster.cpp:2398
                // El driver escribe una o más veces con un byte semilla y espera recuperar el
                // acumulador POR DMA (no por el puerto de datos): es su prueba de que el canal
                // DMA configurado es de verdad el de la tarjeta.
                _e2ValAdd += (byte)(_in[0] ^ _e2ValXor);
                _e2ValXor = (byte)((_e2ValXor >> 2) | (_e2ValXor << 6));
                _dma.RegisterCallback(_dmaChannel, E2DmaCallback);
                break;
            case 0xE3: // DSP Copyright — sblaster.cpp:2407: la SB 2.0 real NO responde nada.
                break;
            case 0xE4: // Write Test Register — sblaster.cpp:2436
                _testRegister = _in[0];
                break;
            case 0xE8: // Read Test Register — sblaster.cpp:2457
                FlushData();
                AddData(_testRegister);
                break;
            case 0xF2: // Trigger 8-bit IRQ (detección de la línea de IRQ) — sblaster.cpp:2463
                // DOSBox lo retrasa 0,01 ms por evento del PIC para emular la lentitud del DSP;
                // aquí va por el reloj del invitado, igual que los bloques de una muestra.
                _irqDeBloqueEs16 = false;
                ArmIrqOnGuestClock(0.01);
                break;
            case 0xF3: // Trigger 16-bit IRQ (sblaster.cpp:2471) — el driver de una SB16 comprueba
                // así la línea, y espera reconocerla por 0x22F.
                _irqDeBloqueEs16 = true;
                ArmIrqOnGuestClock(0.01);
                break;
            case 0x38: // Write to SB MIDI Output (sblaster.cpp:2117): la tarjeta reenvía el byte
                // a su puerto MIDI. Casi nadie lo usa —los juegos hablan con el MPU-401 en 0x330
                // directamente— así que se dice en vez de tirarlo en silencio.
                NoModelado.Add("DSP 0x38 (MIDI por la Sound Blaster; usa el MPU-401 en 0x330)");
                break;
            case 0xF8: // Undocumentado, pre-SB16 — sblaster.cpp:2478
                FlushData(); AddData(0);
                break;
            default:
                if (_log) Console.Error.WriteLine($"[SB]   comando 0x{cmd:X2} no soportado (no-op)");
                break;
        }
        _cmd = 0; _cmdLen = 0; _inPos = 0;
    }

    // ---- reproducción single-cycle (sblaster.cpp DSP_PrepareDMA_Old + DSP_DoDMATransfer) ----

    /// <summary>Programar una transferencia NUEVA aborta la que hubiera en vuelo: el juego
    /// reprograma la direccion y el contador del 8237, asi que lo que quedaba del bloque anterior
    /// no lo reproduce ninguna tarjeta de verdad. Aqui, en cambio, lo pendiente se conservaba
    /// DELANTE de lo nuevo (hasta 2 s), y eso convierte cada sonido en una cola: medido en
    /// un juego de prueba, que mete el audio en bloques de un solo tiro de 32.100 bytes a 6.024 Hz = 5,33 s,
    /// el efecto del choque salia detras de lo que quedara sonando — una prueba manual lo oyo como
    /// "un toque en el suelo suena casi un segundo despues de tocar el suelo".
    ///
    /// OJO, no es lo mismo que tirar el resto en CADA EnqueuePcm: eso se probo y era peor (el
    /// camino de goteo —SB16 y auto-init— llama a EnqueuePcm con unos pocos bytes, y ahi el resto
    /// SI es la cola legitima del mismo flujo; machacarlo sonaba a microcorte). Se tira solo aqui,
    /// que es donde el hardware tambien lo tiraria.</summary>
    private void AbortaPendiente()
    {
        if (_sinAborto) return;
        _pendingPos = _pendingLen = 0;
        _fase = 0;
    }

    /// <summary>PAKU_SB_SIN_ABORTO=1: una transferencia nueva NO aborta lo que quedara pendiente
    /// de la anterior, que es como estaba antes. Es el CONTROL del A/B: sin poder medir la
    /// conducta vieja en las mismas condiciones, "ahora no se pierde sonido" es una impresion.
    /// Y es tambien la escotilla si algun juego resultara depender de la cola.</summary>
    static readonly bool _sinAborto =
        Environment.GetEnvironmentVariable("PAKU_SB_SIN_ABORTO") == "1";

    /// <summary>Vuelve al formato de la SB 2.0 (8 bits sin signo, mono). Lo llaman los comandos
    /// clásicos: si antes hubo un flujo de 16 bits, su formato NO puede quedarse pegado.</summary>
    private void FormatoSb2()
    {
        _fmt16 = false; _fmtConSigno = false; _fmtEstereo = false; _fmtBytesPorFrame = 1;
        _irqDeBloqueEs16 = false;
        _canalEnUso = _dmaChannel;
    }

    private void StartSingleCycle(byte cmd)
    {
        FormatoSb2();
        AbortaPendiente();
        // Programar una transferencia nueva levanta la pausa del 0xD0, como en DOSBox-X, donde
        // DSP_DoDMATransfer saca el canal de MODE_DMA_PAUSE. Sin esto la deteccion de la tarjeta
        // —que es un 0x14 de 16 bytes esperando su IRQ— se quedaba sin IRQ para siempre si el
        // juego habia pausado antes, y el driver reintentaba el reset en bucle (medido: 16 resets
        // y 10 intentos de 0x14 en una corrida, contra 11 y 3 del original).
        _dmaPausado = false;
        int blockBytes = 1 + _in[0] + (_in[1] << 8);
        var chan = _dma.Channel(_dmaChannel);
        if (chan.Masked || chan.TransferMode != 2)
        {
            if (_log) Console.Error.WriteLine($"[SB]   0x{cmd:X2} bloque descartado (canal enmascarado/no-READ)");
            return;
        }
        int bytes = Math.Min(blockBytes, chan.CurrCount + 1);
        int rate = _sampleRate > 0 ? _sampleRate : 8000;
        if (_log)
            Console.Error.WriteLine($"[SB]   DMA start page=0x{chan.PageNum:X2} addr=0x{chan.CurrAddr:X4} count={chan.CurrCount} block={blockBytes} rate={rate}");

        int read = _dma.Read(_dmaChannel, _mem!, _dmaBuf, bytes);
        _irqPending = false; // DSP_DoDMATransfer limpia pending antes de arrancar
        EnqueuePcm(_dmaBuf, read, rate);

        // Programar el IRQ 7 a bytes/rate segundos de tiempo real (acompasado).
        double delayMs = (double)read / rate * 1000.0;
        DisarmIrq();
        _irqDueTicks = Stopwatch.GetTimestamp() + (long)(delayMs / 1000.0 * Stopwatch.Frequency);
        // DOSBox programa este IRQ en el reloj EMULADO (PIC_AddEvent -> END_DMA_Event), así que
        // el invitado nunca puede adelantarlo. Aquí el reloj de la máquina es tiempo real —
        // es lo que acompasa el audio digital —, pero un bloque de menos de un milisegundo no
        // acompasa nada: es el bloque de UNA muestra con el que todo driver SB autodetecta su
        // IRQ (un juego de prueba espera el flag de su ISR en un bucle de 0x800 vueltas y si no llega aborta
        // el init). Para esos, el plazo va al reloj del invitado, como DOSBox.
        // CON CINTA PUESTA va SIEMPRE por el reloj del invitado, dure lo que dure el bloque: el
        // instante en que vence esta IRQ decide cuando el juego entra en su ISR de audio, y con
        // el reloj de pared cae en una instruccion distinta en cada corrida. Medido en
        // un juego de prueba: un bloque de 4.257 ms armado en la instruccion 113.208.164 vencia en
        // la 164.120.640 de una corrida y en otra no, y ahi es donde dos corridas de la MISMA
        // cinta dejaban de ser la misma corrida (el desarme suelta ademas el poll fino, asi que
        // hasta la granularidad del temporizador cambiaba a partir de ese punto).
        _irqOnGuestClock = delayMs < 1.0 || InputTape.RelojDelInvitado("sb");
        if (_irqOnGuestClock)
        {
            int cyc = _cyclesPerMs > 0 ? _cyclesPerMs : DosboxDefaultCyclesPerMs;
            _irqDueSteps = Cpu.TickCount + (long)(delayMs * cyc);
        }
        _irqArmed = true;
        // El poll de eventos hardware corre cada 4096 Steps: una ventana así se traga un plazo
        // de unos cientos de instrucciones. Afinarlo mientras el bloque esté en vuelo.
        Cpu.RequestFinePoll(true);
        if (_log)
            Console.Error.WriteLine($"[SB]   leidos {read} bytes -> ring; IRQ7 en {delayMs:F2} ms steps={Cpu.TickCount}{(_irqOnGuestClock ? $" (reloj invitado, vence en {_irqDueSteps - Cpu.TickCount})" : "")}");
    }

    // u8 unsigned PCM -> s16, reamuestreo nearest-neighbor de rate a la frecuencia del host.
    // NO se vuelca al anillo de golpe: un bloque DMA de .VOC dura segundos (25 KB a 6 kHz son
    // 4,2 s) y el anillo del host es de UN segundo — volcarlo entero hacía que AddSamples
    // descartara lo más viejo y sólo sonara el último segundo de cada efecto. Se deja
    // preparado y lo dosifica FeedPending al ritmo real del drenaje, que es lo que hace
    // DOSBox al consumir el canal DMA desde su callback de mezcla (sblaster.cpp:1196).
    /// <summary>Convierte UN frame del formato del flujo en curso a la muestra de 16 bits con
    /// signo que usa el anillo. El anillo del anfitrión es MONO, así que un frame estéreo se
    /// promedia: subir los dos canales y quedarse con uno sonaría a la mitad de volumen en los
    /// pasajes donde el juego separa la imagen estéreo.</summary>
    private short MuestraDelFrame(byte[] src, int frame)
    {
        int b = frame * _fmtBytesPorFrame;
        if (b + _fmtBytesPorFrame > src.Length) return 0;
        int izq, der;
        if (_fmt16)
        {
            izq = (short)(src[b] | (src[b + 1] << 8));
            if (!_fmtConSigno) izq -= 32768;
            if (!_fmtEstereo) return (short)izq;
            der = (short)(src[b + 2] | (src[b + 3] << 8));
            if (!_fmtConSigno) der -= 32768;
        }
        else
        {
            izq = _fmtConSigno ? (sbyte)src[b] << 8 : (src[b] - 128) << 8;
            if (!_fmtEstereo) return (short)izq;
            der = _fmtConSigno ? (sbyte)src[b + 1] << 8 : (src[b + 1] - 128) << 8;
        }
        return (short)((izq + der) / 2);
    }

    /// <summary><paramref name="n"/> son FRAMES, no bytes: con 8 bits mono coinciden (y por eso
    /// esto no cambia nada para el resto de juegos probados), pero con 16 bits estéreo un frame son
    /// cuatro bytes. La fase persistente del remuestreo cuenta en frames desde siempre — es lo
    /// que hay que respetar para no repetir el 16 % de audio perdido (ver EnqueuePcm).</summary>
    private void EnqueuePcm(byte[] src, int n, int rate)
    {
        if (_audio == null || n <= 0) return;
        int hostRate = _audio.SampleRate;
        // FASE DE LECTURA PERSISTENTE. Antes esto era `outN = n * hostRate / rate`, recalculado
        // en CADA llamada y truncado a entero — y las llamadas son diminutas: AvanzarDmaSb16
        // consume `ms * rate / 1000` bytes por poll, y con polls cada pocas décimas de ms eso son
        // 3 o 4 bytes. Con 3 bytes a 11111 Hz hacia 22050, lo exacto son 5,95 muestras y se
        // emitían 5: **se perdía el 16 % del audio, bloque a bloque**. Medido en un juego de prueba: el DMA
        // entregaba 11.090 B/s (su nominal) y al anillo llegaban 18.700 muestras/s de las 22.050
        // que el host consume, así que el host rellenaba el resto con SILENCIO. Con música
        // sonando encima eso se oye exactamente como lo describió una prueba manual: "la música va bien
        // y los efectos se entrecortan".
        //
        // Arrastrando la fase entre llamadas no se pierde ni una muestra y además no se repite ni
        // se salta ninguna en los bordes de bloque, que es de donde salían los chasquidos.
        double paso = (double)rate / Math.Max(hostRate, 1);
        int outN = 0;
        for (double q = _fase; q < n; q += paso) outN++;
        if (outN <= 0) { _fase -= n; if (_fase < 0) _fase = 0; return; }
        // Lo que quedaba por entregar del bloque anterior NO se tira: se conserva delante y lo
        // nuevo va detras. Antes se machacaba, y todo lo que no hubiera cabido en el colchon era
        // audio PERDIDO — que es exactamente como suena un microcorte. Con bloques pequeños (el
        // camino SB16, unos pocos bytes por sondeo) nunca sobraba nada y no se notaba; con uno
        // grande tras un paron, si.
        int resto = _pendingLen - _pendingPos;
        if (resto < 0) resto = 0;
        // Tope del atraso acumulado: si el anfitrion no drena, no crecer sin limite ni meter
        // segundos de latencia. Se queda con lo MAS NUEVO, que es lo que el jugador espera oir.
        int maxResto = hostRate * 2;
        if (resto > maxResto) { _pendingPos += resto - maxResto; resto = maxResto; }
        if (_pcm.Length < resto + outN) Array.Resize(ref _pcm, resto + outN);
        if (resto > 0 && _pendingPos > 0) Array.Copy(_pcm, _pendingPos, _pcm, 0, resto);
        double pos = _fase;
        for (int j = 0; j < outN; j++)
        {
            int srcIdx = (int)pos;
            if (srcIdx >= n) srcIdx = n - 1;
            _pcm[resto + j] = MuestraDelFrame(src, srcIdx);
            pos += paso;
        }
        _fase = pos - n;
        if (_fase < 0 || _fase >= 1) _fase = 0;   // sólo la fracción sobrante viaja al bloque siguiente
        _pendingLen = resto + outN; _pendingPos = 0;
        FeedPending();
    }

    // Colchón que se mantiene por delante del cursor de reproducción. Ha de ser holgado frente
    // al bloque de drenaje del render y quedar MUY por debajo de la capacidad del anillo, que
    // comparte con el OPL y el speaker.
    //
    // TODO LO QUE HAY EN ESTE COLCHON ES LATENCIA: son muestras ya decididas, esperando turno,
    // así que el sonido de un evento se oye `colchon` segundos después de que el juego lo lance.
    // Estaba en 0,5 s = 5,4 bloques de drenaje, y una prueba manual lo oyó: "una explosión suena como
    // 500 ms después de un choque". El bloque del host son 2048 muestras a 22.050 Hz = 92,9 ms
    // (Cli/Program.cs, SetAudioStreamBufferSizeDefault), y el render lo drena a 60 fps, así que
    // con DOS bloques de margen sobra para absorber un frame largo sin quedarse en seco.
    // PAKU_SB_COLCHON_MS reabre el valor: es el CONTROL del A/B (con el número viejo tienen que
    // volver la latencia y el `anillo=` de [AUDIO]) y la salida si algún equipo lento mete huecos.
    private static readonly double FeedCushionSeconds =
        double.TryParse(Environment.GetEnvironmentVariable("PAKU_SB_COLCHON_MS"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var _colMs) && _colMs > 0
            ? _colMs / 1000.0
            : 0.19;

    /// <summary>
    /// Entrega al anillo el siguiente trozo del bloque preparado, sólo hasta completar el
    /// colchón. Como el anillo se drena en tiempo real, esto alimenta exactamente al ritmo de
    /// reproducción. Idempotente y barato cuando no hay nada pendiente.
    /// </summary>
    private void FeedPending()
    {
        if (_audio == null || _pendingPos >= _pendingLen) return;
        int cushion = (int)(_audio.SampleRate * FeedCushionSeconds);
        int room = cushion - (int)_audio.Backlog(_voice!);
        if (room <= 0) return;
        int n = Math.Min(room, _pendingLen - _pendingPos);
        if (_feed.Length < n) _feed = new short[n];
        Array.Copy(_pcm, _pendingPos, _feed, 0, n);
        _audio.AddSamples(_voice!, _feed, n);
        _pendingPos += n;
        _diagMuestras += n;
    }

    private void AddData(byte v) { _out.Enqueue(v); }

    /// <summary>
    /// Vacía la cola de salida (sblaster.cpp DSP_FlushData:775). Todo comando que RESPONDE lo
    /// hace antes de encolar: así el driver lee su respuesta y no restos de la anterior.
    /// </summary>
    private void FlushData() { _out.Clear(); }

    /// <summary>
    /// Programa el IRQ 8-bit a <paramref name="delayMs"/> del reloj del INVITADO. Para retardos
    /// de microsegundos (0xF2) el tiempo real no sirve: el driver espera el IRQ dentro de un
    /// bucle acotado de instrucciones, así que el plazo tiene que correr con la CPU emulada.
    /// </summary>
    private void ArmIrqOnGuestClock(double delayMs)
    {
        DisarmIrq();
        int cyc = _cyclesPerMs > 0 ? _cyclesPerMs : DosboxDefaultCyclesPerMs;
        _irqOnGuestClock = true;
        _irqDueSteps = Cpu.TickCount + Math.Max(1L, (long)(delayMs * cyc));
        _irqArmed = true;
        Cpu.RequestFinePoll(true);
    }

    private byte ReadData()
    {
        if (_out.Count > 0) _outLast = _out.Dequeue();
        return _outLast;
    }

    // Velocidad nominal de CPU emulada cuando el juego corre sin governor (cyclesPerMs=0):
    // es el `cycles=3000` de serie de DOSBox, la referencia con la que se calibró el proyecto.
    private const int DosboxDefaultCyclesPerMs = 3000;
    private int _cyclesPerMs;       // último Cpu.CyclesPerMs visto (PollDueIrq lo refresca)

    /// <summary>Suelta el bloque en vuelo y su petición de poll fino (idempotente).</summary>
    private void DisarmIrq()
    {
        if (!_irqArmed) return;
        _irqArmed = false;
        Cpu.RequestFinePoll(false);
    }

    // ---- entrega del IRQ 7 acompasada a tiempo real ----

    // La memoria se fija al instalar el SB (el StartSingleCycle la necesita para leer el DMA).
    private Memory? _mem;
    public void AttachMemory(Memory m) => _mem = m;

    /// <summary>
    /// Comprueba, en el hilo del juego (lo llama MachineTimer), si el bloque en curso ha
    /// cumplido su tiempo de reproducción; si es así dispara el IRQ 7 (mismo camino que las
    /// demás IRQs hardware: RaiseIrq + ServicePendingInterrupts -> ISR del juego en INT 0x0F).
    /// De paso dosifica el bloque en curso al anillo: es el mismo punto y el mismo hilo desde
    /// el que generan el OPL y el speaker.
    /// </summary>
    public void PollDueIrq(DosPlatform p, Cpu c, Memory m)
    {
        if (LevantarIrqSiVence(p, c)) p.ServicePendingInterrupts(c, m);
    }

    // ---- SB16: transferencia DMA CONTINUA, consumida al ritmo de reproducción ----
    //
    // El camino de SB Pro (`StartSingleCycle`) se traga el bloque entero de golpe y programa la
    // IRQ para cuando "toque". A un driver que espera la IRQ eso le vale; al de un juego DOS/4GW no,
    // porque NO usa la IRQ —la deja enmascarada en el PIC— sino que VIGILA el contador del canal
    // leyendo el puerto 0x003 para saber cuánto se ha reproducido y rellenar por delante. Con el
    // bloque consumido de una vez, ese contador no le dice nada: se quedaba clavado y el juego no
    // pasaba de su logo. Aquí el DMA avanza por trozos, al ritmo real del muestreo.
    /// <summary>Formato del flujo digital en curso. Por defecto, el de la SB 2.0: 8 bits sin
    /// signo, mono — así los caminos de 0x14 y 0x1C se comportan exactamente igual que antes, que
    /// es lo que protege a los siete juegos con sonido ya validado por prueba manual.
    /// Sólo los comandos 0xB0-0xCF lo cambian.</summary>
    private bool _fmt16, _fmtConSigno, _fmtEstereo;
    private int _fmtBytesPorFrame = 1;

    /// <summary>El latch de IRQ de 16 bits es OTRO: en una SB16 se reconoce leyendo 0x22F, no
    /// 0x22E. Un driver que hace DMA de 16 bits y reconoce por el puerto de 8 bits deja la línea
    /// levantada y no vuelve a recibir nada.</summary>
    private bool _irqPending16;
    private bool _irqDeBloqueEs16;

    /// <summary>Canal DMA de 16 bits de la tarjeta. El estándar universal es el 5 —es lo que
    /// dice la variable BLASTER que PakuPort le da al invitado (`H5`) y el valor de fábrica de
    /// DOSBox—, y el registro 0x81 del mezclador puede cambiarlo. -1 = sin asignar, que fuerza
    /// el modo "aliased" por el canal de 8 bits.</summary>
    private int _dmaCanal16 = 5;

    /// <summary>Canal por el que va el flujo en curso: el de 8 bits, o el alto si el comando es
    /// de 16 y la tarjeta lo tiene asignado.</summary>
    private int _canalEnUso;

    private bool _aiActivo;
    private int _aiRate;
    /// <summary>Fracción de muestra de ORIGEN que sobra al final de un bloque y entra en el
    /// siguiente. Sin ella el reamuestreo trunca por bloque y pierde audio (ver EnqueuePcm).</summary>
    double _fase;
    long _diagMuestras;   // muestras entregadas al anillo en la ventana de diagnóstico
    private long _aiUltimoTick;
    private long _aiUltimoReloj;
    private double _aiRestoBytes;

    /// <summary>El DMA avanza con el reloj de PARED en vez de con el del invitado.
    ///
    /// Lo enciende el mundo PLANO de 32 bits, que no tiene governor: alli el hilo del juego
    /// ejecuta lo que le da la maquina, asi que el tiempo virtual (instrucciones/cyclesPerMs) se
    /// queda POR DETRAS del real en cuanto se pide mas velocidad de la que el interprete alcanza
    /// —a cyclesPerMs=60000 pidiendo 60 M instrucciones/s y dando 20, el tiempo virtual va a un
    /// tercio—. La tarjeta consumia su bufer a ese ritmo lento mientras el altavoz del anfitrion
    /// lo vaciaba en tiempo real: se quedaba seco, y eso son los MICROCORTES (medidos: peores
    /// cuanto mas alta la velocidad, limpios a cyclesPerMs bajo).
    ///
    /// Y es lo FIEL: una tarjeta de sonido es hardware aparte y reproduce a su frecuencia de
    /// muestreo pase lo que pase con la CPU. El mundo de 16 bits se queda con el reloj del
    /// invitado a proposito: alli el governor de <see cref="MachineTimer"/> mantiene el tiempo
    /// virtual pegado al real, los 21 juegos estan validados asi, y el modo determinista
    /// (PAKU_IRQ_DET) necesita que el audio cuente en instrucciones para ser reproducible.</summary>
    public bool DmaEnTiempoReal;

    /// <summary>Comandos 0xB0-0xCF de la SB16: la familia entera de "programar sonido digital",
    /// que sustituye a los 0x14/0x1C de la SB 2.0. Portado de sblaster.cpp:2215 y
    /// DSP_PrepareDMA_New (:1366).
    ///
    /// COMANDO   1 0 1 B | A/D | A/I | FIFO | 0     B: 1 = 16 bits (0xBx), 0 = 8 bits (0xCx)
    /// BYTE DE MODO      | estéreo (bit 5) | con signo (bit 4)
    ///
    /// La longitud que el DSP recibe está en MUESTRAS, no en bytes: un bloque de 4096 a 16 bits
    /// estéreo son 16 KiB de DMA. Contarla en bytes —que es lo que hacía el camino de 8 bits, y
    /// ahí coincidían— entregaría la cuarta parte del audio y una IRQ de bloque cuatro veces más
    /// rápida de lo debido.</summary>
    private void StartDmaSb16(byte cmd)
    {
        bool bits16 = (cmd & 0x10) != 0;
        bool grabacion = (cmd & 0x08) != 0;
        bool autoInit = (cmd & 0x04) != 0;
        int muestras = 1 + _in[1] + (_in[2] << 8);
        bool conSigno = (_in[0] & 0x10) != 0;
        bool estereo = (_in[0] & 0x20) != 0;

        if (grabacion)
        {
            // No hay entrada de audio, y fabricar silencio no es lo mismo que grabar: un juego
            // que mida el nivel del micrófono creería que hay señal.
            if (_log) Console.Error.WriteLine($"[SB]   0x{cmd:X2} GRABACION no modelada");
            NoModelado.Add($"DSP 0x{cmd:X2} (grabacion)");
            return;
        }

        // ¿Por qué canal? sblaster.cpp:1396 — un flujo de 16 bits va por el canal ALTO si la
        // tarjeta lo tiene asignado (el `H5` de la variable BLASTER, que es lo normal); si no lo
        // tiene, o coincide con el de 8 bits, la tarjeta real cae en el modo "aliased" y lo hace
        // por el canal de 8 bits contando el doble de bytes.
        _canalEnUso = bits16 && _dmaCanal16 >= 0 && _dmaCanal16 != _dmaChannel
                      ? _dmaCanal16 : _dmaChannel;
        bool aliased = bits16 && _canalEnUso == _dmaChannel;

        // OJO: aqui NO se descarta por canal enmascarado, al reves que en el camino de 8 bits.
        // El hardware no lo hace —DSP_DoDMATransfer arranca igual— y el orden importa: el driver
        // de HMI manda el 0xB4 ANTES de desenmascarar el canal, asi que descartarlo mataba el
        // flujo para siempre. Con el canal cerrado el 8237 no entrega nada y ya esta; en cuanto
        // el driver lo abre, empieza a sonar.
        var chan = _dma.Channel(_canalEnUso);

        AbortaPendiente();
        _fmt16 = bits16; _fmtConSigno = conSigno; _fmtEstereo = estereo;
        _irqDeBloqueEs16 = bits16;
        _fmtBytesPorFrame = (bits16 ? 2 : 1) * (estereo ? 2 : 1);
        _aiRate = _sampleRate > 0 ? _sampleRate : 8000;
        _aiActivo = true;
        _dmaPausado = false;   // programar un DMA nuevo levanta la pausa del 0xD0
        _aiUltimoTick = Cpu.TickCount;
        _aiUltimoReloj = Stopwatch.GetTimestamp();
        _aiRestoBytes = 0;
        _irqPending = false; _irqPending16 = false;
        // La IRQ de bloque va en BYTES, que es la unidad en la que se cuenta lo consumido.
        _ai8Bloque = autoInit ? muestras * _fmtBytesPorFrame : 0;
        _ai8HastaIrq = _ai8Bloque;
        if (!autoInit)
        {
            // Single-cycle: una sola IRQ al acabar el bloque, con el MISMO criterio de reloj que
            // el 0x14 de la SB 2.0 — los bloques largos van por el reloj de pared (es lo que
            // acompasa el audio) y sólo los de menos de un milisegundo, que son las sondas de
            // detección de IRQ, van por el del invitado. Mandarlo todo por el del invitado
            // encendería el sondeo fino durante los 185 ms de un bloque entero.
            double msBloque = (double)muestras / Math.Max(1, _aiRate) * 1000.0;
            // Con cinta puesta, al reloj del invitado tambien los bloques largos: ver ArmIrq.
            if (msBloque < 1.0 || InputTape.RelojDelInvitado("sb")) ArmIrqOnGuestClock(msBloque);
            else
            {
                DisarmIrq();
                _irqDueTicks = Stopwatch.GetTimestamp() + (long)(msBloque / 1000.0 * Stopwatch.Frequency);
                _irqOnGuestClock = false;
                _irqArmed = true;
            }
        }
        if (_log)
            Console.Error.WriteLine($"[SB]   DMA SB16 0x{cmd:X2} {(bits16 ? 16 : 8)} bits " +
                                    $"{(estereo ? "estereo" : "mono")} {(conSigno ? "con signo" : "sin signo")} " +
                                    $"muestras={muestras} ({muestras * _fmtBytesPorFrame} B) rate={_aiRate} " +
                                    $"auto-init={autoInit} canal={_canalEnUso}{(aliased ? " (aliased)" : "")} " +
                                    $"count={chan.CurrCount} buffer=0x{chan.PageBase | chan.CurrAddr:X5}");
    }

    /// <summary>Consume del canal DMA lo que la tarjeta habría reproducido desde la última vez.
    /// El reloj es el del invitado (instrucciones/cyclesPerMs), no el de pared: así el contador
    /// que el driver vigila avanza igual en cada ejecución.</summary>
    private void AvanzarDmaSb16()
    {
        if (!_aiActivo || _mem == null) return;
        // En pausa (0xD0) la tarjeta no consume nada. Reanclar el reloj en vez de salir a secas:
        // si no, al reanudar `ms` valdria TODA la pausa y el primer avance se tragaria el buffer
        // entero de golpe.
        if (_dmaPausado)
        {
            if (_log)
            {
                long _p = Stopwatch.GetTimestamp();
                if (_diagLastPause != 0) _diagMsPausa += (_p - _diagLastPause) * 1000.0 / Stopwatch.Frequency;
                _diagLastPause = _p;
            }
            ReanclarRelojDma();
            return;
        }
        _diagLastPause = 0;
        double ms;
        // SIN GOVERNOR NO HAY RELOJ DE INVITADO QUE VALGA. Contar el tiempo del DMA en
        // instrucciones/cyclesPerMs sólo es correcto mientras el governor mantiene el tiempo
        // virtual pegado al real; con velocidad libre (cyclesPerMs = 0) se caía a un valor por
        // defecto que no tiene nada que ver con lo que ejecuta la máquina, y el reloj del audio
        // se disparaba. Medido en un juego de prueba, que corre libre a ~27.000 instrucciones/ms contra
        // ese defecto: vaciábamos el buffer del juego a 90.000 B/s con un nominal de 22.222 —
        // cuatro vueltas al búfer circular por cada una suya, así que sólo el 24 % de lo leído
        // tenía sonido y el resto era el silencio que el juego aún no había mezclado. Se oía como
        // un sonido entrecortado (120 ms mudos cada 170), y el A/B de PAKU_SB_RELOJ_INVITADO no
        // lo movía porque este mundo YA estaba en el reloj del invitado.
        // El modo determinista (PAKU_IRQ_DET) fija cyclesPerMs, así que sigue por el invitado y
        // conserva su reproducibilidad.
        if (DmaEnTiempoReal || _cyclesPerMs <= 0)
        {
            long ahoraReloj = Stopwatch.GetTimestamp();
            ms = (ahoraReloj - _aiUltimoReloj) * 1000.0 / Stopwatch.Frequency;
            _aiUltimoReloj = ahoraReloj;
            _aiUltimoTick = Cpu.TickCount;
            // Tras un paron del hilo del juego (una lectura grande de disco durante la intro son
            // decenas de ms) no se recupera el tiempo perdido de golpe: eso metia un bloque enorme
            // que no cabia en el colchon y se perdia. La tarjeta real se limita a seguir sonando.
            if (ms > 50) ms = 50;
        }
        else
        {
            int cyc = _cyclesPerMs > 0 ? _cyclesPerMs : DosboxDefaultCyclesPerMs;
            long ahora = Cpu.TickCount;
            ms = (double)(ahora - _aiUltimoTick) / cyc;
            _aiUltimoTick = ahora;
        }
        if (ms <= 0) return;
        // El reloj manda FRAMES por segundo; los bytes salen de multiplicar por el tamaño del
        // frame. Contar el tiempo en bytes sería contar 16 bits estéreo cuatro veces más rápido.
        _aiRestoBytes += ms * _aiRate * _fmtBytesPorFrame / 1000.0;
        int n = (int)_aiRestoBytes;
        if (n <= 0) return;
        if (n > _dmaBuf.Length) n = _dmaBuf.Length;
        n -= n % _fmtBytesPorFrame;   // nunca medio frame: partirlo desalinea todo lo que sigue
        if (n <= 0) return;
        _aiRestoBytes -= n;
        int leidos = _dma.Read(_canalEnUso, _mem, _dmaBuf, n);
        leidos -= leidos % _fmtBytesPorFrame;
        _diagBytes += leidos;
        int frames = leidos / _fmtBytesPorFrame;
        _diagFrames += frames;
        // "Con sonido" es lo que NO es silencio, y el silencio depende del formato: 0x80 en 8
        // bits sin signo, 0 en cualquiera con signo. Mirar el byte crudo daba una cifra sin
        // sentido en cuanto el flujo era de 16 bits.
        for (int i = 0; i < frames; i++) if (MuestraDelFrame(_dmaBuf, i) != 0) _diagBytesConSonido++;
        if (leidos > 0)
        {
            EnqueuePcm(_dmaBuf, frames, _aiRate);
            // Auto-init 8-bit (0x1C): una IRQ por cada `block size` bytes consumidos — es el
            // latido con el que el driver rellena las mitades del buffer circular.
            if (_ai8Bloque > 0)
            {
                _ai8HastaIrq -= leidos;
                while (_ai8HastaIrq <= 0)
                {
                    _ai8HastaIrq += _ai8Bloque;
                    _ai8IrqsPendientes++;
                }
            }
        }
    }

    // Telemetria de PAKU_LOG_SB, un resumen por segundo de reloj real. Responde la pregunta que
    // un WAV no puede responder: un tramo mudo, ¿es que la tarjeta no entrego nada (pausa larga,
    // emulador que no llega) o es que el JUEGO tenia silencio en su buffer (0x80) porque no le
    // dio tiempo a mezclar? Las dos cosas suenan igual y se arreglan distinto.
    // _diagBytes son BYTES de DMA (para el B/s) y _diagFrames son MUESTRAS (para el % con
    // sonido): mezclarlos daba un 25,0 % clavado en un flujo de 16 bits estéreo, que son cuatro
    // bytes por muestra — el numerito parecía un problema de audio y era de la regla de tres.
    private long _diagBytes, _diagBytesConSonido, _diagFrames;
    private long _diagLastPause;
    private double _diagMsPausa;
    private long _diagIrq;
    private double _diagVentanaMs;
    private long _diagUltimoReloj;

    private void DiagPorSegundo()
    {
        if (!_log) return;
        long ahora = Stopwatch.GetTimestamp();
        if (_diagUltimoReloj == 0) { _diagUltimoReloj = ahora; return; }
        double ms = (ahora - _diagUltimoReloj) * 1000.0 / Stopwatch.Frequency;
        _diagUltimoReloj = ahora;
        _diagVentanaMs += ms;
        if (_diagVentanaMs < 1000.0) return;
        double seg = _diagVentanaMs / 1000.0;
        Console.Error.WriteLine(
            $"[SB] {seg:F2}s: dma={_diagBytes / seg:F0} B/s (nominal {_aiRate}) " +
            $"con_sonido={(_diagFrames > 0 ? 100.0 * _diagBytesConSonido / _diagFrames : 0):F1}% " +
            $"pausa={_diagMsPausa:F0} ms irq={_diagIrq} " +
            // Muestras que ESTA tarjeta pone en el anillo por segundo, frente a las que el host
            // consume (su SampleRate). Es lo único que dice si el reamuestreo cubre el hueco:
            // con `dma` correcto y esto a la mitad, el anillo se queda sin datos del SB y el
            // llamante rellena con silencio — y eso, con música sonando encima, se oye
            // exactamente como "los efectos se entrecortan y la música va bien".
            $"al_anillo={_diagMuestras / seg:F0}/s (host {(_audio?.SampleRate ?? 0)})" +
            $" pendiente={_pendingLen - _pendingPos}");
        _diagVentanaMs = 0; _diagBytes = 0; _diagBytesConSonido = 0; _diagFrames = 0;
        _diagMsPausa = 0; _diagIrq = 0;
        _diagMuestras = 0;
    }

    /// <summary>DSP 0xD0/0xD5 (pausa) hasta que llegue 0xD4/0xD6. Es una PAUSA: el estado del
    /// DMA se conserva entero, a diferencia del 0xDA, que termina el auto-init.</summary>
    private bool _dmaPausado;

    /// <summary>Vuelve a anclar el reloj del DMA al instante actual (y tira el resto fraccionario).
    /// Lo usan la pausa y la reanudacion para que el tiempo parado no se reproduzca de golpe.</summary>
    private void ReanclarRelojDma()
    {
        _aiUltimoTick = Cpu.TickCount;
        _aiUltimoReloj = Stopwatch.GetTimestamp();
        _aiRestoBytes = 0;
    }

    int _blockSize;            // DSP 0x48 (bytes por bloque del auto-init)
    int _ai8Bloque;            // bloque activo del auto-init 8-bit (0 = no activo)
    double _ai8HastaIrq;       // bytes que faltan para la próxima IRQ de bloque
    int _ai8IrqsPendientes;

    /// <summary>Levanta la IRQ del bloque en el PIC si ya ha vencido, y NADA MÁS: no despacha la
    /// ISR. Lo que sigue —quién entrega la interrupción y cuándo— es del mundo que llame.
    ///
    /// El mundo plano necesita esta mitad suelta. Despachar por <see cref="DosPlatform.
    /// ServicePendingInterrupts"/> es el camino de 16 bits, y desde el intérprete de 32 se comía
    /// la IRQ antes de que el juego la viera — el mismo defecto que ya tuvo el temporizador.
    /// Un juego DOS/4GW engancha el vector 0Fh para su driver de sonido y no recibía NINGUNA.</summary>
    public bool LevantarIrqSiVence(DosPlatform p, Cpu c)
    {
        _cyclesPerMs = c.CyclesPerMs;
        AvanzarDmaSb16();
        FeedPending();
        DiagPorSegundo();
        // En pausa la tarjeta tampoco interrumpe: DOSBox-X retira los eventos END_DMA/DMA_DAC
        // al entrar en MODE_DMA_PAUSE y los vuelve a registrar en el 0xD4.
        if (_dmaPausado) return false;
        // IRQs de bloque del auto-init 8-bit: una por poll (a 92 ms por bloque y polls cada
        // pocos ms, nunca se acumulan más de una salvo tras un parón del hilo).
        if (_ai8IrqsPendientes > 0)
        {
            _ai8IrqsPendientes--;
            if (_irqDeBloqueEs16) _irqPending16 = true; else _irqPending = true;
            _irq7Count++; _diagIrq++;
            if (_log) Console.Error.WriteLine($"[SB] IRQ{_irq} #{_irq7Count} (auto-init) steps={Cpu.TickCount}");
            if (!p.Pic.IsMasked(_irq)) { p.Pic.RaiseIrq(_irq); return true; }
            return false;
        }
        if (!_irqArmed) return false;
        _cyclesPerMs = c.CyclesPerMs;
        bool due = _irqOnGuestClock ? Cpu.TickCount >= _irqDueSteps
                                    : Stopwatch.GetTimestamp() >= _irqDueTicks;
        if (!due) return false;
        DisarmIrq();
        if (_irqDeBloqueEs16) _irqPending16 = true; else _irqPending = true;
        _irq7Count++;
        if (_log) Console.Error.WriteLine($"[SB] IRQ{_irq} #{_irq7Count} steps={Cpu.TickCount}");
        if (p.Pic.IsMasked(_irq)) return false;
        p.Pic.RaiseIrq(_irq);
        return true;
    }
}
