using System.Diagnostics;

namespace PakuPort.Core.Platform;

/// <summary>
/// Temporizador de maquina: avanza el tiempo simulado y dispara IRQ0/INT8 segun la
/// frecuencia programada en el canal 0 del PIT.
/// Modelo DOSBox (src/hardware/pit.cpp): el temporizador vive en el HILO DEL JUEGO,
/// gobernado por tiempo real, con granularidad de instrucciones. Poll() es el camino
/// vivo (lo llama Cpu.OnPeriodicPoll cada 4096 Steps); Advance() se conserva para
/// callers antiguos pero la plantilla del host ya no lo usa.
/// </summary>
public sealed class MachineTimer
{
    // En Windows, Thread.Sleep(1) sin esto duerme ~15,6 ms (la resolución del timer del
    // sistema), y el governor duerme en ESE cuanto. El batido entre 15,625 ms y el periodo del
    // retrace VGA (14,286 ms) hacía que un juego que espera el vsync con busy-wait sólo cazara
    // un flanco cada ~172 ms: un juego 3D con governor corría a 5,4 fps de motor IGUAL a 27000 que
    // a 60000 ciclos (medido contando frames nuevos en los volcados de presentación), y a esa
    // cadencia la imagen se percibía "plana, sin profundidad". Es el mismo batido 15,6-vs-14,3
    // que ya costó "uno de cada diez frames de todos los juegos" en la presentación. DOSBox llama
    // a timeBeginPeriod(1) por lo mismo.
    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);
    static MachineTimer()
    {
        if (System.OperatingSystem.IsWindows())
            try { timeBeginPeriod(1); } catch { }
    }

    // Tiempo acumulado en milisegundos que aun no ha producido un tick IRQ0 (camino Advance)
    private double _accumMs = 0.0;

    // ---- estado de Poll (camino vivo, tiempo real en el hilo del juego) ----
    private readonly Stopwatch _sw = new();
    private long   _lastSwTicks = 0;   // Stopwatch.ElapsedTicks del último Poll
    private double _pitAccumMs   = 0.0; // ms acumulados hacia el próximo tick IRQ0 (PIT ch0)
    private double _bdaAccumMs   = 0.0; // ms acumulados hacia el próximo tick BDA (18,2065 Hz)
    private double _vgaAccumMs   = 0.0; // ms acumulados hacia el próximo refresco de pantalla (VGA scan-out)

    // ---- estado del governor de ciclos (modelo DOSBox cycles=N; Cpu.CyclesPerMs) ----
    // Ancla de tiempo real (Stopwatch) y de Steps (Cpu.TickCount) fijada en el primer Poll y
    // RE-anclada tras cada clamp/pausa larga (>100 ms) para no acumular ni "sprintar" deuda.
    private long _cycleAnchorTicks = 0;
    private long _cycleAnchorSteps = 0;

    // La cuenta de ticks de la BDA (0x40:0x6C) SIEMPRE avanza a 18,2065 Hz de TIEMPO REAL,
    // independiente del divisor que el juego programe en el PIT. Es la fuente única de verdad
    // (el handler C# BIOS de INT8 NO la suma; ver BiosInterrupts.cs).
    private const double BdaTickHz   = 18.2065;
    private const double BdaPeriodMs = 1000.0 / BdaTickHz;

    // Refresco de pantalla del VGA (scan-out del CRTC): 70 Hz de TIEMPO REAL para los modos
    // estándar (mismo reloj que la fase de retrace de VgaPortHandler.StatusRead → coherentes).
    // El monitor real refresca a esta cadencia independientemente de lo que haga el juego; aquí
    // capturamos UN frame por refresco al front buffer (VideoState.CaptureFrontBuffer). Inerte si
    // el host no gobierna la pantalla (headless/verifier: CaptureFrontBuffer es no-op).
    private const double VgaRefreshHz  = 70.0;
    private const double VgaPeriodMs   = 1000.0 / VgaRefreshHz;

    /// <summary>
    /// Poll periódico desde el HILO DEL JUEGO (Cpu.OnPeriodicPoll). Deriva el tiempo real
    /// transcurrido desde el Poll anterior (Stopwatch, clamp 100 ms) y:
    ///   - Genera ticks del PIT canal 0 a la frecuencia PROGRAMADA (leída en cada Poll: el
    ///     juego la reprograma en caliente). Por tick vencido: RaiseIrq(0) si no está
    ///     enmascarado y drena INMEDIATAMENTE (ISR completa síncrona) — nada colapsa en el
    ///     IRR mientras IF=1; con IF=0 queda latcheado y entra en el siguiente Poll.
    ///   - Avanza BDA.TimerTicks a 18,2065 Hz reales SIEMPRE (flag de medianoche a 0x1800B0).
    ///   - Drena al final cualquier IRQ pendiente de otras fuentes (teclado que el host
    ///     levantó con RaiseIrq(1)) → latencia de teclado ≤ ~0,3 ms.
    /// </summary>
    public void Poll(DosPlatform p, Cpu c, Memory m)
    {
        // El video necesita el mismo factor instrucciones->ms que el temporizador para poder
        // servir el retrace por el reloj virtual bajo una cinta (VgaPortHandler.StatusRead).
        p.Video.CyclesPerMs = c.CyclesPerMs;
        p.Cmos.CyclesPerMs = c.CyclesPerMs;
        if (!_sw.IsRunning)
        {
            _sw.Start();
            _lastSwTicks = _sw.ElapsedTicks;
            _cycleAnchorTicks = _lastSwTicks;
            _cycleAnchorSteps = Cpu.TickCount;
            return;
        }

        // Entrada del host serializada con el flujo de instrucciones (hilo del juego), ANTES de
        // entregar ticks/IRQs/callbacks: así los polls INT33 (AH=03/05/06) y las lecturas del
        // teclado (puerto 0x60 / INT 9) del juego ven estado coherente, y el callback del ratón
        // (AX=000C) se dispara en este mismo Poll sobre los eventos recién aplicados.
        //  - Ratón: Int33hHandler.DrainHostEvents (cableado en DosPlatform.HostInputDrain).
        //  - Teclado: los scancodes crudos que el host encoló se meten AHORA en el buffer del 8042
        //    y se levanta IRQ1 en el hilo del juego (antes el hilo de render mutaba el Queue<byte>
        //    del controlador → carrera). El lock fino del Pic se mantiene (spec §3).
        // La cinta se reproduce AQUI, en el hilo del juego y por el reloj del invitado, antes de
        // drenar: los eventos que acaban de vencer se aplican en este mismo poll (ver
        // InputTape.Reproduce). Con la cinta puesta el anfitrion no inyecta nada.
        if (InputTape.Replaying) InputTape.Reproduce(p);
        p.HostInputDrain?.Invoke();
        // La IRQ1 la levanta el propio 8042 al transferir cada byte al latch del puerto 0x60
        // (KeyboardController.Pump vía PicRaiseIrq1): una IRQ por byte ENTREGADO, no por byte
        // encolado — si se levantara aquí, dos bytes seguidos coalescen en el PIC y el segundo
        // INT 9 no llegaría a dispararse con el modelo de latch.
        if (p.Keyboard.PicRaiseIrq1 == null)
            p.Keyboard.PicRaiseIrq1 = () => p.Pic.RaiseIrq(1);
        while (p.Keyboard.TryDequeueHostScancode(out byte sc))
            p.Keyboard.PushScancode(sc);

        DeliverDueTicks(p, c, m);

        // Governor de ciclos (modelo DOSBox cycles=N): frenar la VM a N Steps por ms de tiempo
        // real. 0 = sin governor (velocidad libre, comportamiento previo idéntico). Los juegos
        // CPU-paced de la época corren "a velocidad de CPU" y sin esto se desbocan.
        if (c.CyclesPerMs > 0 && !(SinFreno && InputTape.ClockLocked))
        {
            SyncGovernorAnchor(c);
            double nowMs = (_sw.ElapsedTicks - _cycleAnchorTicks) * 1000.0 / Stopwatch.Frequency;
            double aheadSteps = (Cpu.TickCount - _cycleAnchorSteps) - nowMs * c.CyclesPerMs;
            while (aheadSteps > c.CyclesPerMs)   // adelantados >1 ms de trabajo
            {
                System.Threading.Thread.Sleep(1);
                // Seguir entregando ticks del PIT/BDA durante la espera: el sonido/reloj NO se
                // congela mientras el governor frena.
                DeliverDueTicks(p, c, m);
                // La velocidad puede cambiar EN CALIENTE (Ctrl+F1/F2/F3 del host) desde el hilo
                // de render mientras este hilo duerme: re-anclar y salir, o el bucle nunca
                // terminaría (con el número nuevo la deuda pasada se reevalúa entera).
                if (c.CyclesPerMs != _governorCycles) { SyncGovernorAnchor(c); break; }
                nowMs = (_sw.ElapsedTicks - _cycleAnchorTicks) * 1000.0 / Stopwatch.Frequency;
                aheadSteps = (Cpu.TickCount - _cycleAnchorSteps) - nowMs * c.CyclesPerMs;
            }
        }
    }

    /// <summary>`PAKU_SIN_FRENO=1` — el governor no duerme, SOLO con una cinta puesta.
    ///
    /// <para>Con cinta, el reloj del invitado es el contador de instrucciones (ticks del PIT, la
    /// cinta misma, el retrace y la IRQ del Sound Blaster: ver <see cref="InputTape"/>), asi que
    /// frenar a <c>cyclesPerMs</c> solo cuesta tiempo de pared y no cambia nada de lo que el
    /// juego ve. Una corrida de medida de 40 M de instrucciones de un juego de prueba pasa de 2 minutos
    /// a lo que tarde el interprete. Sin cinta se ignora: ahi el freno ES el ritmo del juego.</para></summary>
    static readonly bool SinFreno = Environment.GetEnvironmentVariable("PAKU_SIN_FRENO") == "1";

    /// <summary>El freno esta quitado ahora mismo: la palanca puesta Y una cinta que manda el reloj.
    /// Lo mira tambien <see cref="Cpu.Step"/>, que en ese caso tampoco espera al fotograma del
    /// anfitrion (el otro freno: el hilo del juego se para en cada frontera de frame hasta que la
    /// ventana pinta, y eso deja el interprete en presupuesto x 60 instrucciones por segundo).</summary>
    public static bool SinFrenoAhora => SinFreno && InputTape.ClockLocked;

    // Último CyclesPerMs con el que se fijó el ancla del governor. El ancla mide una DEUDA
    // acumulada (Steps ejecutados menos los debidos) y esa cuenta solo es válida a velocidad
    // constante: al cambiarla hay que reanclar, porque si no todo el pasado se reevalúa con el
    // número nuevo. Bajando de 3000 a 100 tras dos minutos de juego, el governor concluía que
    // iba adelantado una hora de trabajo y dormía hasta que el reloj real le alcanzase — el
    // "cuelgue al cambiar de velocidad" que se vio en varios juegos de prueba.
    private int _governorCycles = -1;

    private void SyncGovernorAnchor(Cpu c)
    {
        if (c.CyclesPerMs == _governorCycles) return;
        _governorCycles    = c.CyclesPerMs;
        _cycleAnchorTicks  = _sw.ElapsedTicks;
        _cycleAnchorSteps  = Cpu.TickCount;
    }

    /// <summary>Entrega los ticks vencidos del PIT canal 0 (IRQ0) y avanza la cuenta de la BDA
    /// según el tiempo real transcurrido desde el Poll anterior. Factorizado del cuerpo de Poll
    /// para poder invocarlo también dentro del bucle de espera del governor (el reloj/sonido no
    /// se congela durante el throttle). Tras una pausa larga (clamp >100 ms) re-ancla el governor
    /// para no "sprintar" recuperando deuda.</summary>
    // PASO 3 remakes (InputTape): with a tape active the tick sources (PIT/BDA/VGA/OPL/SPK)
    // run on the VIRTUAL clock TickCount/CyclesPerMs instead of the Stopwatch, so IRQ0 lands
    // on the same instruction-stream points every run (record and replay alike). The cycle
    // governor below keeps pacing on REAL time — pacing does not affect determinism.
    private double _lastVirtMs;

    private void DeliverDueTicks(DosPlatform p, Cpu c, Memory m)
    {
        double deltaMs;
        if (InputTape.ClockLocked && c.CyclesPerMs > 0)
        {
            double nowVirtMs = Cpu.TickCount / (double)c.CyclesPerMs;
            deltaMs = nowVirtMs - _lastVirtMs;
            _lastVirtMs = nowVirtMs;
            _lastSwTicks = _sw.ElapsedTicks; // keep the real anchor fresh for the governor clamp
        }
        else
        {
            long now = _sw.ElapsedTicks;
            deltaMs = (now - _lastSwTicks) * 1000.0 / Stopwatch.Frequency;
            _lastSwTicks = now;
        }
        if (deltaMs <= 0.0) return;
        if (deltaMs > 100.0)
        {
            deltaMs = 100.0; // clamp: pausa de ventana / FrameGate
            // Re-anclar el governor: real time saltó adelante sin Steps; sin esto la VM
            // correría desbocada recuperando la deuda acumulada durante la pausa.
            _cycleAnchorTicks = _lastSwTicks;
            _cycleAnchorSteps = Cpu.TickCount;
        }

        // BDA.TimerTicks: 18,2065 Hz de tiempo real, SIEMPRE (fuente única de verdad).
        _bdaAccumMs += deltaMs;
        while (_bdaAccumMs >= BdaPeriodMs)
        {
            _bdaAccumMs -= BdaPeriodMs;
            const uint MidnightTicks = 0x1800B0u;
            uint ticks = p.Bda.TimerTicks + 1;
            if (ticks >= MidnightTicks)
            {
                ticks = 0;
                p.Bda.MidnightFlag = 1;
            }
            p.Bda.TimerTicks = ticks;
        }

        // PIT canal 0 → IRQ0 a la frecuencia PROGRAMADA, entrega síncrona tick a tick.
        double periodMs = 1000.0 / p.Pit.Channel0FrequencyHz;
        _pitAccumMs += deltaMs;
        while (_pitAccumMs >= periodMs)
        {
            _pitAccumMs -= periodMs;
            if (!p.Pic.IsMasked(0))
            {
                p.Pic.RaiseIrq(0);
                p.ServicePendingInterrupts(c, m);
            }
        }

        // Refresco de pantalla (VGA scan-out): captura UN frame al front buffer a 70 Hz reales.
        // Único punto de captura de VRAM para presentación (modelo hardware): el hilo de render
        // del host presenta siempre el último frame capturado, sin competir con lecturas de 0x3DA
        // ni con la VRAM viva. No-op headless (CaptureFrontBuffer comprueba HostDrivenRetrace).
        _vgaAccumMs += deltaMs;
        if (_vgaAccumMs >= VgaPeriodMs)
        {
            // Colapsar refrescos atrasados (tras un clamp/pausa): solo interesa el frame actual.
            _vgaAccumMs = _vgaAccumMs % VgaPeriodMs;
            p.Video.CaptureFrontBuffer();
        }

        // SoundBlaster: entrega el IRQ del bloque DMA en curso cuando vence su tiempo real
        // (bytes/rate). Null-op si no hay SB instanciado. Mismo hilo/punto seguro que el PIT.
        p.Sb?.PollDueIrq(p, c, m);

        // OPL/AdLib: genera la música FM vencida por el tiempo real transcurrido y la suma al
        // ring del host. Mismo hilo que las escrituras de registro (Opl.Out) → lock-free.
        // Null-op si el chip no está enchufado (sin HostAudio; todos los gates).
        p.Opl.Generate(deltaMs);

        // PC Speaker: onda cuadrada del canal 2 del PIT mientras el gate y el enable de 0x61
        // estén activos. Mismo punto y mismas condiciones que el OPL (null-op sin HostAudio).
        // CyclesPerMs = reloj virtual del modo directo (flancos del bit 1 con la puerta cerrada).
        p.Speaker.CyclesPerMs = c.CyclesPerMs;
        p.Speaker.Generate(deltaMs, p.Pit);

        // Drenar IRQs pendientes de otras fuentes (teclado). ServicePendingInterrupts solo
        // trabaja si hay algo pendiente e IF=1; barato en vacío.
        p.ServicePendingInterrupts(c, m);
    }

    /// <summary>
    /// Avanza el tiempo simulado en <paramref name="milliseconds"/> ms.
    /// Por cada desbordamiento del canal 0 del PIT que corresponda:
    ///   1. Incrementa BDA.TimerTicks (con flag de medianoche a las 0x1800B0 ticks).
    ///   2. Llama p.FireInterrupt(0x08, c, m) para disparar INT 8 (timer ISR del juego).
    /// </summary>
    public void Advance(double milliseconds, DosPlatform p, Cpu c, Memory m)
    {
        double freqHz  = p.Pit.Channel0FrequencyHz;
        double periodMs = 1000.0 / freqHz; // ms por tick IRQ0

        _accumMs += milliseconds;

        while (_accumMs >= periodMs)
        {
            _accumMs -= periodMs;

            // Actualizar contador BDA
            const uint MidnightTicks = 0x1800B0u;
            uint ticks = p.Bda.TimerTicks + 1;
            if (ticks >= MidnightTicks)
            {
                ticks = 0;
                p.Bda.MidnightFlag = 1;
            }
            p.Bda.TimerTicks = ticks;

            // Marcar IRQ0 pendiente. El DISPARO real de la ISR (INT 8) lo hace el hilo de
            // lógica del juego en el límite de frame de Cpu.Step (vía ServicePendingInterrupts),
            // NO aquí: Advance corre en el hilo del host y disparar la ISR aquí provocaría una
            // carrera de datos sobre cpu/mem con el hilo del juego.
            if (!p.Pic.IsMasked(0))
                p.Pic.RaiseIrq(0);
        }
    }
}
