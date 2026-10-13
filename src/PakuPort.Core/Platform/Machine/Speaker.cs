namespace PakuPort.Core.Platform;

/// <summary>
/// Puerto 0x61 — PC Speaker y gate del canal 2 del PIT, CON generación de audio.
/// Bits: 0 = gate canal 2 PIT, 1 = speaker data enable.
/// Adaptado de dosbox-x src/hardware/pcspeaker.cpp.
///
/// El altavoz suena cuando ambos bits están a 1: el canal 2 del PIT, programado en modo 3
/// (onda cuadrada), conmuta la salida a 1193182/reload Hz. Es el único sonido que tienen
/// montones de juegos de los 80 y una de las opciones de sonido de muchos de los 90
/// (la opción "IBM" o "PC Speaker" de sus menús).
///
/// Desviación consciente respecto a DOSBox: allí la onda va de 0 a +SPKR_VOLUME y el nivel
/// continuo se drena con la rampa volcur/volwant; aquí se genera CENTRADA en cero (±mitad de
/// amplitud), que suena igual y evita el chasquido de continua al arrancar y parar sin
/// necesidad de la rampa. Los modos 0/1 del PIT NO se modelan.
///
/// MODO DIRECTO: con la puerta del PIT cerrada (bit 0 = 0) la salida del altavoz
/// sigue el bit 1 a pelo — así reproducen audio digitalizado/por pulsos los juegos de los
/// 80 (un juego de prueba, en su EXE @0x1ce48: or al,2 / out 61 / loop / and al,FD / out 61 leyendo anchos de una
/// tabla). Los flancos se timestampean con el RELOJ VIRTUAL (Cpu.TickCount / CyclesPerMs),
/// no con el de pared: el governor ejecuta ráfagas de 4096 instrucciones y el tiempo real
/// de cada OUT llega a grumos; el virtual es liso por instrucción (truco de InputTape).
/// Sin governor (CyclesPerMs=0) no hay reloj y el modo directo queda mudo como antes — a
/// velocidad libre los retardos por bucle de CPU tampoco significarían nada.
/// </summary>
public sealed class Speaker
{
    private const double PitClockHz = 1193181.666;
    private const double Amplitude = 10000.0 / 2;   // SPKR_VOLUME de pcspeaker.cpp, centrada

    private byte _port61 = 0x00;
    private HostAudio? _audio;
    private HostAudio.Voice? _voice;
    private double _phase;      // posición dentro del periodo actual, en [0,1)
    private double _accumMs;    // tiempo real pendiente de convertir en muestras
    private short[] _buf = new short[1024];

    /// <summary>Instrucciones/ms del governor; lo refresca MachineTimer en cada tick.
    /// Es el reloj del modo directo (0 = sin reloj, modo directo mudo).</summary>
    public int CyclesPerMs;
    private readonly System.Collections.Generic.List<(double vMs, bool level)> _dirEvents = new();
    private bool _dirLevel;     // nivel actual de la membrana en modo directo
    private double _hpLp;       // paso-alto de 1 polo: bloquea la continua sin rampa
    private double _dirCursorMs = double.NaN;   // cursor virtual del flujo directo emitido

    /// <summary>True si el gate del canal 2 del PIT está activo (bit 0).</summary>
    public bool PitGate => (_port61 & 0x01) != 0;

    /// <summary>True si el speaker está habilitado (bit 1).</summary>
    public bool SpeakerEnabled => (_port61 & 0x02) != 0;

    /// <summary>Enchufa el ring del host. Sin esta llamada el altavoz queda mudo (y el resto
    /// del emulador se comporta igual que antes: sólo estado de puerto).</summary>
    public void AttachAudio(HostAudio audio) { _audio = audio; _voice = audio.CreateVoice(); }

    /// <summary>Genera las muestras vencidas por el TIEMPO REAL transcurrido y las suma al ring,
    /// igual que <see cref="Opl.Generate"/> y desde el mismo punto (MachineTimer, hilo del juego).
    /// La fase se conserva entre llamadas: sin eso, cada bloque reempezaría la onda y el tono
    /// saldría troceado.</summary>
    public void Generate(double deltaMs, Pit pit)
    {
        // PASO 5 remakes (PAKU_LOG_AUDIO_EV=1): audio tick for the sound-event catalog. Goes
        // BEFORE the no-audio early return on purpose — the fingerprints must be capturable
        // with or without a host ring attached. Off = one static bool check.
        if (AudioEventLog.Enabled)
            AudioEventLog.Tick(deltaMs, PitGate && SpeakerEnabled, pit.Channel2.Reload);
        if (_audio == null || _voice == null) return;
        int rate = _audio.SampleRate;
        _accumMs += deltaMs;
        int n = (int)(_accumMs * rate / 1000.0);
        if (n <= 0) return;
        _accumMs -= n * 1000.0 / rate;
        if (_buf.Length < n) _buf = new short[n];

        uint reload = pit.Channel2.Reload;
        double freq = reload == 0 ? 0 : PitClockHz / reload;
        // Fuera del rango audible no se emite: un reload minúsculo daría alias en vez de tono.
        bool on = PitGate && SpeakerEnabled && freq >= 20 && freq <= 20000;
        if (!on)
        {
            if (_dirEvents.Count > 0 || _dirLevel || System.Math.Abs(_hpLp) > 0.5)
            {
                GenerateDirect(n, rate);
                return;
            }
            System.Array.Clear(_buf, 0, n);
            _audio.AddSamples(_voice, _buf, n);
            return;
        }
        _dirEvents.Clear();   // el tono manda; los flancos sueltos previos no se reproducen

        double step = freq / rate;              // avance de fase por muestra
        for (int i = 0; i < n; i++)
        {
            _buf[i] = (short)(_phase < 0.5 ? Amplitude : -Amplitude);
            _phase += step;
            if (_phase >= 1.0) _phase -= (int)_phase;
        }
        _audio.AddSamples(_voice, _buf, n);
    }

    public byte In(ushort port)
    {
        if (port != 0x61) return 0xFF;
        // Bit 4/5 = estado de refresh/timer (algunos programas los comprueban)
        // Devolvemos el byte almacenado con bit 5 alternado para simular actividad
        return _port61;
    }

    public void Out(ushort port, byte v)
    {
        if (port != 0x61) return;
        byte old = _port61;
        bool edge = ((old ^ v) & 0x03) != 0;
        if (_log && edge)
            Console.Error.WriteLine($"[SPK] 0x61: {old:X2} -> {v:X2} (gate={(v & 1)} enable={(v >> 1) & 1})");
        _port61 = v;
        // Modo directo: flanco del bit 1 con la puerta cerrada → evento con reloj virtual.
        // Solo con audio enchufado (sin ring la lista crecería sin consumidor: gates/verifier).
        if (_audio != null && CyclesPerMs > 0 && (v & 0x01) == 0 && ((old ^ v) & 0x02) != 0)
            _dirEvents.Add((Cpu.TickCount / (double)CyclesPerMs, (v & 0x02) != 0));
        // PASO 5 remakes: report the gate edge to the sound-event catalog right here. The tick
        // sampling alone loses beeps that end a few instructions before the program exits.
        if (edge && AudioEventLog.Enabled) AudioEventLog.SpeakerGate(PitGate && SpeakerEnabled);
    }

    /// <summary>Sintetiza el bloque en modo directo: onda por niveles a partir de los flancos
    /// timestampeados en tiempo virtual, mapeados linealmente a la ventana de este bloque.
    /// El paso-alto drena un nivel sostenido a silencio (equivalente funcional de la rampa
    /// volcur/volwant de pcspeaker.cpp, coherente con la onda centrada del modo tono).</summary>
    private void GenerateDirect(int n, int rate)
    {
        // Cursor VIRTUAL persistente: avanza al ritmo de las muestras emitidas y consume los
        // flancos a su paso. No se puede anclar la ventana a "ahora" (TickCount): el governor
        // ejecuta a ráfagas y duerme el resto del ms con el reloj virtual congelado — anclando
        // a ahora, los flancos de cada ráfaga colapsan en un bloque diminuto (medido: 1000 Hz
        // salían a 375). La latencia queda acotada por el resync de 100 ms.
        double winMs = n * 1000.0 / rate;
        double vNow = CyclesPerMs > 0 ? Cpu.TickCount / (double)CyclesPerMs : 0.0;
        if (double.IsNaN(_dirCursorMs))
            _dirCursorMs = (_dirEvents.Count > 0 ? _dirEvents[0].vMs : vNow) - winMs;
        if (vNow - _dirCursorMs > 100.0)
            _dirCursorMs = vNow - winMs;   // pausa/clamp: re-anclar, no sprintar el atraso
        int e = 0;
        for (int i = 0; i < n; i++)
        {
            double t = _dirCursorMs + (i + 1) * 1000.0 / rate;
            while (e < _dirEvents.Count && _dirEvents[e].vMs <= t)
            {
                _dirLevel = _dirEvents[e].level;
                e++;
            }
            double x = _dirLevel ? Amplitude : -Amplitude;
            _hpLp += (x - _hpLp) * 0.002;
            _buf[i] = (short)(x - _hpLp);
        }
        _dirCursorMs += winMs;
        if (e > 0) _dirEvents.RemoveRange(0, e);   // los futuros (vMs > cursor) esperan su bloque
        _audio!.AddSamples(_voice!, _buf, n);
    }

    /// <summary>PAKU_LOG_SPK=1: el altavoz interno — puerta del canal 2 y flancos del bit 1 del
    /// 0x61 (audio bit-bangeado). Con el modo directo, los flancos son la senal.</summary>
    private static readonly bool _log =
        Environment.GetEnvironmentVariable("PAKU_LOG_SPK") == "1";
}
