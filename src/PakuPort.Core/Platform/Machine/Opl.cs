namespace PakuPort.Core.Platform;

/// <summary>
/// Adlib / OPL2 (Yamaha YM3812). Puertos 0x388 (indice) y 0x389 (dato).
/// Almacena los 256 registros (sin sintesis de audio) y emula los DOS TIMERS del chip
/// (reg 2 = timer1, unidades de 80 µs; reg 3 = timer2, 320 µs; reg 4 = control), porque la
/// deteccion clasica de AdLib arranca timer1 y espera ver sus flags en el status
/// ((st & 0xE0) == 0xC0) — con un status fijo el driver se queda esperando para siempre.
/// Base de tiempo: Cpu.TickCount (1 paso ≈ 1 tick PIT ≈ 0.838 µs).
/// Adaptado de dosbox-x src/hardware/adlib.cpp (Chip::Write reg 4 / Chip::Read).
/// </summary>
public sealed class Opl
{
    private byte _index = 0;
    private readonly byte[] _regs = new byte[256];

    private const double UsPerTick = 1_000_000.0 / 1_193_182.0; // ≈0.838 µs por tick

    private long _t1Start = -1, _t2Start = -1; // TickCount al arrancar cada timer (-1 = parado)
    private bool _f1, _f2;                     // flags de desbordamiento
    private bool _m1, _m2;                     // masks (reg4 bits 6/5): enmascarado = no pone flag

    // ---- síntesis FM (OplChip, porte OPL2 mono de dbopl.cpp) ----
    // Init PEREZOSO: el chip SOLO existe tras AttachAudio(hostAudio) desde la plantilla del host.
    // Sin HostAudio (todos los gates corren el Core sin audio) → _chip==null → Out no reenvía y
    // Generate no hace nada → estado de CPU/memoria IDÉNTICO al de hoy (null-op por construcción).
    private OplChip? _chip;
    private HostAudio? _audio;
    private HostAudio.Voice? _voice;
    private double _accumMs;
    private int[] _genBuf = System.Array.Empty<int>();
    private short[] _pcm = System.Array.Empty<short>();

    // Ganancia aplicada al int32 de GenerateBlock2 antes de clampar a int16 (§4.3). 1.0 = fiel a
    // DOSBox sin adlib_pcm_boost. Calibrable por env (PAKU_OPL_GAIN) contra el RMS del oráculo.
    private static readonly double OplGain =
        double.TryParse(Environment.GetEnvironmentVariable("PAKU_OPL_GAIN"),
            System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 1.0;

    /// <summary>PAKU_LOG_OPL=1: cada escritura al sintetizador FM. CERO escrituras con el juego
    /// sonando significa que su musica no va por AdLib — asi se descarto en un juego de prueba.</summary>
    private static readonly bool _log =
        Environment.GetEnvironmentVariable("PAKU_LOG_OPL") == "1";

    /// <summary>Crea el chip dbopl a la rate del host y lo enchufa al ring. Lo llama la plantilla
    /// del host junto a InstallSoundBlaster (Program.cs). Sin esta llamada el OPL queda mudo.</summary>
    public void AttachAudio(HostAudio audio)
    {
        _audio = audio;
        _voice = audio.CreateVoice();
        _chip = new OplChip((uint)audio.SampleRate);
    }

    /// <summary>Genera las muestras OPL vencidas por el TIEMPO REAL transcurrido y las suma al
    /// ring del host. Lo llama MachineTimer.DeliverDueTicks EN EL HILO DEL JUEGO (mismo hilo que
    /// las escrituras de registro de Out) → lock-free sobre el estado del chip. Null-op sin chip.</summary>
    public void Generate(double deltaMs)
    {
        if (_chip == null || _audio == null) return;
        _accumMs += deltaMs;
        int rate = _audio.SampleRate;
        int n = (int)(_accumMs * rate / 1000.0);
        if (n <= 0) return;
        _accumMs -= n * 1000.0 / rate;
        if (_genBuf.Length < n) { _genBuf = new int[n]; _pcm = new short[n]; }
        _chip.GenerateBlock2((uint)n, _genBuf);
        for (int i = 0; i < n; i++)
        {
            int s = (int)(_genBuf[i] * OplGain);
            _pcm[i] = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
        }
        _audio.AddSamples(_voice!, _pcm, n);
    }

    private void UpdateTimers()
    {
        long now = Cpu.TickCount;
        if (_t1Start >= 0 && !_m1)
        {
            double periodUs = (256 - _regs[2]) * 80.0;
            if ((now - _t1Start) * UsPerTick >= periodUs) _f1 = true;
        }
        if (_t2Start >= 0 && !_m2)
        {
            double periodUs = (256 - _regs[3]) * 320.0;
            if ((now - _t2Start) * UsPerTick >= periodUs) _f2 = true;
        }
    }

    public byte In(ushort port)
    {
        if (port == 0x388)
        {
            // Status: bit 7 = IRQ (t1|t2), bit 6 = timer1, bit 5 = timer2. Los bits bajos
            // se mantienen en 0x06 (valor historico de esta clase; la deteccion mira 0xE0).
            UpdateTimers();
            byte st = 0x06;
            if (_f1) st |= 0x40;
            if (_f2) st |= 0x20;
            if (_f1 || _f2) st |= 0x80;
            return st;
        }
        if (port == 0x389)
        {
            return _regs[_index];
        }
        return 0xFF;
    }

    public void Out(ushort port, byte v)
    {
        if (port == 0x388)
        {
            _index = v;
            return;
        }
        if (port == 0x389)
        {
            _regs[_index] = v;
            // PASO 5 remakes (PAKU_LOG_AUDIO_EV=1): feed the register write to the sound-event
            // catalog, which groups the stream between silences into fingerprinted events.
            // Deliberately BEFORE the _chip null check: the register program is the identity of
            // the sound whether or not the host is synthesizing it. Off = one bool check.
            if (AudioEventLog.Enabled) AudioEventLog.OplWrite(_index, v);
            if (_index == 0x04)
            {
                if ((v & 0x80) != 0)
                {
                    // IRQ reset: limpia los flags, no toca los timers
                    _f1 = _f2 = false;
                }
                else
                {
                    _m1 = (v & 0x40) != 0;
                    _m2 = (v & 0x20) != 0;
                    _t1Start = (v & 0x01) != 0 ? Cpu.TickCount : -1;
                    _t2Start = (v & 0x02) != 0 ? Cpu.TickCount : -1;
                }
            }
            // Síntesis en paralelo: la detección/timers de arriba NO cambian; el chip solo LEE
            // los registros. Sin chip (gates) es null-op total.
            if (_chip != null)
            {
                if (_log) Console.Error.WriteLine($"[OPL] reg={_index:X2} val={v:X2} t={Cpu.TickCount}");
                _chip.WriteReg(_index, v);
            }
        }
    }
}
