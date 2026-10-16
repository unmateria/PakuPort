using Iced.Intel;

namespace PakuPort.Core;

/// <summary>
/// Intérprete de respaldo: cuando el flujo lifteado cae en un hueco
/// (stub "camino no ejercitado", fallthrough no decodificado, salto de cola sin destino,
/// opcode UNSUPPORTED, miss de FarDispatch), ejecuta los bytes ORIGINALES que ya están en
/// <see cref="Memory"/> (el snapshot es la imagen entera, incluidos parches VROOMM en
/// caliente) hasta que el frame de entrada termina, y devuelve el control al C# lifteado.
///
/// Semántica: espejo del <see cref="Lifter"/> — para cada opcode se ejecuta en runtime lo
/// que el Lifter emite como string, usando los MISMOS helpers de <see cref="Cpu"/> (flags
/// validados por corpus). Decodifica con iced-x86 16-bit (el mismo motor que Decoder16) —
/// no hay un segundo decoder. Un opcode no soportado LANZA con la dirección, nunca calla.
///
/// Coordenadas: el intérprete es dueño de su (cs, ip) local y lee código por dirección
/// lineal (cs*16+ip); el estado arquitectónico (registros/flags/memoria/SP) vive en Cpu.
/// Solo convierte a key-space (fk) en las fronteras: intento CallKey en call near, y
/// FarDispatch para transferencias far (ahí vive toda la lógica VROOMM/drivers).
///
/// Terminación: sp0 = SP a la entrada; tras un ret/retf/iret, si
/// (short)(SP - sp0) &gt; 0 el frame del hueco terminó → return (true si fue retf: el
/// caller lifteado debe hacer c.FarRet = true y su guard de _spEntry propaga).
/// </summary>
public static class Interp
{
    /// <summary>Interruptor global. false por defecto: el juez/survey (compilación en
    /// memoria) conserva la semántica de Fase 0 (registrar hueco + return) y los corpus
    /// no cambian. El host exportado lo enciende salvo PAKU_INTERP=0.</summary>
    public static bool Enabled;

    /// <summary>Config de la ventana de overlays VROOMM del juego exportado (0 = sin
    /// overlays). Un hueco en una función de overlay se ejecuta donde VIVE en runtime:
    /// cs = loadSeg + OverlayWindowSegDelta, ip = OverlayWindowIpBase + offsetDeSegmento.
    /// Las fija el host exportado al arrancar (constantes del game.json).</summary>
    public static ushort OverlayWindowSegDelta;
    public static ushort OverlayWindowIpBase;

    /// <summary>Directorio de overlays VROOMM (ovno → file key del inicio del código),
    /// para resolver thunks INT 3F sin parchear — espejo del lowering estático del Lifter.
    /// Lo fija el host exportado desde el game.json.</summary>
    public static Dictionary<int, int>? OverlayStarts;

    static readonly bool _traceOn = Environment.GetEnvironmentVariable("PAKU_INTERP_TRACE") == "1";
    // PAKU_INTERP_TRACE_FROM: first guest-clock tick (TickCount) the trace logs, so studying one
    // key press does not mean logging the whole boot.
    static readonly long _traceFrom =
        long.TryParse(Environment.GetEnvironmentVariable("PAKU_INTERP_TRACE_FROM"), out var tf) ? tf : 0;
    // PAKU_INTERP_TRACE_TO: last guest-clock tick the trace logs.
    static readonly long _traceTo =
        long.TryParse(Environment.GetEnvironmentVariable("PAKU_INTERP_TRACE_TO"), out var tt) ? tt : long.MaxValue;
    public static bool Trace => _traceOn && Cpu.TickCount >= _traceFrom && Cpu.TickCount <= _traceTo;

    /// <summary>PAKU_LOG_FUERA32=1 (la misma palanca que en Interp32): cada escritura que se sale
    /// del espacio, con la instrucción, los selectores y sus descriptores crudos.</summary>
    public static readonly bool LogFuera = Environment.GetEnvironmentVariable("PAKU_LOG_FUERA32") == "1";

    static long _perdidasPrevias;
    static int _avisosFuera;
    static int _avisosPg, _avisosPf;

    /// <summary>Los ocho bytes crudos del descriptor de un selector, más la base que sale de
    /// ellos. Sólo para diagnóstico: es lo único que distingue "el programa escribió una base
    /// rara" de "PakuPort la lee mal".</summary>
    static string DescDump(Cpu c, Memory m, ushort sel)
    {
        if ((c.Msw & 1) == 0 || !Memory.SegmentosSonSelectores) return "";
        uint tabla = (sel & 4) != 0
            ? m.RdLin16(c.GdtBase + (uint)(c.Ldtr & 0xFFF8) + 2)
              | ((uint)m.RdLin8(c.GdtBase + (uint)(c.Ldtr & 0xFFF8) + 4) << 16)
              | ((uint)m.RdLin8(c.GdtBase + (uint)(c.Ldtr & 0xFFF8) + 7) << 24)
            : c.GdtBase;
        uint lin = tabla + (uint)(sel & 0xFFF8);
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < 8; i++) sb.Append($"{m.RdLin8(lin + (uint)i):X2}");
        return sb.Append($"|base=0x{BaseDeSelectorReal(c, m, sel):X8}]").ToString();
    }

    // PAKU_LOG_BLIT=1: second-port telemetry (second-port doctrine, phase 1).
    // Logs every movs/stos whose destination falls in the active VRAM window to logs/blit.log:
    // source address (the sprite's identity BEFORE composition), destination, length and an
    // FNV-1a hash of the source bytes. Off = one bool check per string op; never alters execution.
    // PAKU_LOG_BLIT=2 additionally records the loose VRAM writes (mov/xor/..., NOT movs/stos)
    // to logs/blitraw.bin via BlitRawLog — the hook lives in Memory.WrU8; here level 2 only
    // suppresses double-logging of string-op writes (Memory.SuppressRawBlit).
    static readonly int _blitLevel =
        int.TryParse(Environment.GetEnvironmentVariable("PAKU_LOG_BLIT"), out var _bl) ? _bl : 0;
    static readonly bool _logBlit = _blitLevel >= 1;

    /// <summary>Last interpreted cs:ip (top of the black-box ring) — provenance tag for the
    /// raw VRAM writes of BlitRawLog. Zero-cost: reads what the main loop already stores.</summary>
    static bool _avisadoVram;

    /// <summary>La caja negra, para quien la necesite desde fuera del interprete.</summary>
    public static string HistDumpPublico() => HistDump();

    internal static uint CurrentPc => _hist[(_histIdx - 1) & (_hist.Length - 1)];
    static StreamWriter? _blitLog;
    static int _blitLines;
    static long _blitFrame = -1;   // fotograma del último blit logueado (ver LogBlit)
    static int _blitOrden;         // cuántos blits van ya en ESTE fotograma

    // PAKU_SAMPLE_PC=1: on process exit, dump the black-box ring (last 64 interpreted cs:ip)
    // to stderr — localizes a busy loop that never faults (hangs have no exception to dump on).
    static readonly bool _samplePc = Environment.GetEnvironmentVariable("PAKU_SAMPLE_PC") == "1";

    // PAKU_SAMPLE_PC_MS=<guest ms>: PERIODIC PC sampling to logs/pcsample.log, the raw input of
    // the offline scene segmenter (tools/scene_hist; requirement 10 of REMAKE-IA-REQUISITOS.md:
    // "escenas detectadas por histograma de PC"). Fully independent of PAKU_SAMPLE_PC above,
    // which stays exactly as it was (one-shot ring dump for hangs).
    static readonly int _sampleMs =
        int.TryParse(Environment.GetEnvironmentVariable("PAKU_SAMPLE_PC_MS"), out var _sms) && _sms > 0 ? _sms : 0;
    /// <summary>Periodic PC sampling requested. Read once per periodic poll, never per
    /// instruction — see <see cref="SamplePc"/> for why the hook is free when off.</summary>
    public static readonly bool SamplePcEnabled = _sampleMs > 0;
    static StreamWriter? _pcLog;
    static long _pcNextTick;
    static long _pcPeriodTicks;
    static int _pcLines;
    // No governor (cyclesPerMs=0, free speed) means guest ms is undefined; fall back to the
    // DOSBox default the rest of the codebase already assumes (SoundBlaster.cs
    // DosboxDefaultCyclesPerMs = 3000) so the flag still yields a usable sampling rate.
    const int SampleDefaultCyclesPerMs = 3000;

    /// <summary>Periodic PC sample. Hooked in <c>Cpu.Step</c> INSIDE the branch that already
    /// fires every <c>Cpu.PollMask</c> steps (the only periodic counter the CPU has): the hot
    /// path pays nothing when the flag is off, because the added check is a static bool read
    /// inside a branch taken once per 4096 steps, not per instruction. Sampling on the poll
    /// branch is correct for scene telemetry: its 4096-step granularity is ~0,2 ms of guest
    /// time at a 20000 cycles/ms governor, three orders of magnitude finer than a scene.
    /// The period is kept in GUEST time (<c>Cpu.TickCount</c>), the same clock blit.log and
    /// audio-events.log use, so the offline tools correlate without converting anything.
    /// PC source: <see cref="CurrentPc"/>, the cs:ip the interpreter is about to execute (the
    /// main loop stores it right before calling Step). The lifted world only puts labels in that
    /// ring, so this telemetry is meaningful under PAKU_INTERP_ONLY=1.</summary>
    internal static void SamplePc(Cpu c)
    {
        long t = Cpu.TickCount;
        if (t < _pcNextTick) return;
        if (_pcLog == null)
        {
            int cyc = c.CyclesPerMs > 0 ? c.CyclesPerMs : SampleDefaultCyclesPerMs;
            _pcPeriodTicks = (long)_sampleMs * cyc;
            _pcLog = OpenPcLog(cyc);
        }
        _pcNextTick = t + _pcPeriodTicks;
        uint pc = CurrentPc;
        // El estado de IF viaja con la muestra: en un juego que "corre y no avanza", saber si el
        // invitado esta con las interrupciones deshabilitadas separa dos causas que se ven igual
        // —va lento— pero se arreglan en sitios opuestos: o falta velocidad, o hay un tramo que
        // no deja entrar al temporizador. Medido en un juego DOS/4GW Pro: el 97 % de las oportunidades de
        // entregar una IRQ caian con IF=0.
        _pcLog.WriteLine($"t={t} pc={pc >> 16:X4}:{pc & 0xFFFF:X4} if={(c.IF ? 1 : 0)}");
        if (++_pcLines % 256 == 0) _pcLog.Flush();
    }

    static StreamWriter OpenPcLog(int cyclesPerMs)
    {
        Directory.CreateDirectory("logs");
        var w = new StreamWriter(Path.Combine("logs", "pcsample.log")) { AutoFlush = false };
        w.WriteLine($"# pcsample v1 sample_ms={_sampleMs} cycles_per_ms={cyclesPerMs}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { w.Flush(); } catch { } };
        return w;
    }

    static Interp()
    {
        if (_samplePc)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 1; i <= _hist.Length; i++)
                {
                    uint v = _hist[(_histIdx - i) & (_hist.Length - 1)];
                    if (sb.Length > 0) sb.Append('<');
                    sb.Append($"{v >> 16:X4}:{v & 0xFFFF:X4}");
                }
                Console.Error.WriteLine($"[SAMPLE_PC] insns={InsnCount} histIdx={_histIdx} ring={sb}");
                // Con PAKU_FATAL_MEMDUMP=1 además se vuelca la imagen de memoria: un cuelgue no
                // lanza excepción, así que este es el único momento en que se puede desensamblar
                // el código VIVO del bucle (los segmentos de overlay se reutilizan).
                if (FatalMemDump && Memory.Last != null)
                    try
                    {
                        System.IO.Directory.CreateDirectory("logs");
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine("logs", "hang-mem.bin"), Memory.Last.Raw);
                    }
                    catch { }
            };
    }

    /// <summary>Instrucciones interpretadas en total (telemetría barata para gaps.log).</summary>
    public static long InsnCount;

    /// <summary>Quien tiene IRQ grabadas que entregar al ritmo del interprete. La interfaz vive
    /// aqui —y no en <c>Verify/</c>— porque un export es self-contained y no se lleva el juez:
    /// con el tipo concreto, el bucle del interprete no compilaria fuera del repo.</summary>
    public interface IRelojIrq { void PasoIrq(long insnsDelCaso, Memory m); }

    /// <summary>Plataforma de replay que tiene IRQ grabadas que entregar, y el valor de
    /// <see cref="InsnCount"/> con el que arranco el caso. Lo pone y lo quita quien ejecuta un
    /// caso del corpus (<c>BancoCiego.EjecutaCaso</c>); fuera de ahi es null y no cuesta nada.
    /// Ver <c>EffectsReplayPlatform.PasoIrq</c>.</summary>
    public static IRelojIrq? ReplayIrq;
    public static long ReplayIrqBase;

    // ===== Sondas OPCIONALES del "unpack dinámico" (dump-image, TAREA A). =====
    // Todas null/false por defecto: cuando lo están, el intérprete NO cambia de
    // comportamiento (los gates y el host exportado no las tocan). Solo CmdDumpImage
    // las activa temporalmente para ejecutar el stub del packer y observar cuándo el
    // flujo salta al programa descomprimido.

    /// <summary>Modo "programa entero": ret/retf/iret NO terminan el frame de entrada,
    /// siguen ejecutando el flujo (el intérprete corre desde el entry del EXE hasta que
    /// una sonda lo corte o se agote el presupuesto). false por defecto.</summary>
    public static bool WholeProgram;

    /// <summary>Modo "despacho de ISR" (frontera FireInterrupt/IsrDispatch): un ISR se
    /// ejecuta como un Run anidado que debe terminar en su IRET terminal, NO en un ret/retf
    /// interno. La clase PSTK ("pila privada de driver") se repite aquí: un ISR de timer
    /// conmuta a su pila privada (SP MAYOR que el sp0 de entrada), y el heurístico de fin de
    /// frame <c>(short)(SP - sp0) &gt; 0</c> confunde el primer ret sobre esa pila con "el
    /// frame acabó", abortando el ISR a medias con SP en la pila privada (cuelgue del AdLib).
    /// Con IsrMode los ret/retf CONTINÚAN (como WholeProgram) y solo el IRET devuelve el
    /// control. Lo activa FireInterrupt solo cuando el contexto es WholeProgram (interpOnly/
    /// hijos EXEC, cuyos ISR acaban en IRET); en el mundo lifteado queda inerte (false) para
    /// no tocar el fin por retf/miss de un ISR no lifteado.</summary>
    public static bool IsrMode;

    /// <summary>Observador por instrucción (cs, ip a punto de ejecutarse). La sonda del
    /// unpack detecta aquí el primer transfer de control far DESCENDENTE (CS bajó al
    /// programa descomprimido) y lanza su propia excepción para cortar. null = sin coste.</summary>
    public static Action<ushort, ushort>? OnStep;

    /// <summary>Observador de INT antes del despacho (intNo, cpu). La sonda del unpack
    /// aborta aquí si el stub hace una INT de "programa real" (10h/16h/33h, 21h AH=3C/3D/4B):
    /// eso significa que el EXE NO estaba empaquetado. null = sin coste.</summary>
    public static Action<byte, Cpu>? OnInt;

    public delegate bool CallKeyFn(Cpu c, Memory m, int fk);
    /// <summary>Despacho far. Devuelve true si lo manejó (código lifteado / efecto grabado /
    /// miss contabilizado); false → el intérprete emula la transferencia como hardware
    /// (sigue interpretando en el destino). El FarDispatch generado SIEMPRE maneja.</summary>
    public delegate bool FarDispatchFn(Cpu c, Memory m, ushort seg, ushort off);

    // ── CACHE DE DECODIFICACION DEL MUNDO DE 16 BITS ─────────────────────────────────────────
    //
    // Hermano del de Interp32 (ver alli el porque y la invalidacion por generacion de pagina),
    // con UNA diferencia que lo hace seguro: solo cachea codigo por encima del megabyte. La
    // memoria baja la escriben mil caminos del mundo real que no pasan por WrLin8 y vigilarlos
    // todos costaria mas que el cache; ademas, en modo real la lineal NUNCA llega ahi, asi que
    // todos los juegos probados corren exactamente como antes.
    //
    // No es un caso raro: en un juego DOS/4GW Pro el 99,7 % de las instrucciones de 16 bits se buscan por
    // encima del megabyte (218 M de 218,9 M medidas en 30 s), porque el kernel del extensor vive
    // en selectores con base alta. Sin cache, cada byte del opcode se lee con RdLin8 y paga su
    // traduccion de pagina: 3-5 traducciones por instruccion solo para leerla.
    public static readonly bool CacheDecode16 =
        Environment.GetEnvironmentVariable("PAKU_INTERP_CACHE") != "0";
    const int CacheBits16 = 18;                     // 262144 entradas
    const uint CacheMask16 = (1u << CacheBits16) - 1;
    static readonly Instruction[] _c16Insn = new Instruction[1 << CacheBits16];
    static readonly uint[] _c16Lin = new uint[1 << CacheBits16];   // 0 = entrada vacia
    static readonly uint[] _c16Ip = new uint[1 << CacheBits16];
    static readonly long[] _c16Gen = new long[1 << CacheBits16];

    static long GenCombinada16(uint lin) =>
        Memory.GenPagina[(lin >> 12) & Memory.GenPaginaMask]
        + Memory.GenPagina[((lin + 14) >> 12) & Memory.GenPaginaMask];

    sealed class MemoryCodeReader : CodeReader
    {
        public byte[] Raw = System.Array.Empty<byte>();
        /// <summary>Sólo hace falta en modo protegido de 16 bits, donde el código puede vivir por
        /// encima del megabyte y el `& 0xFFFFF` de abajo —que en modo real ES el wrap fiel del
        /// 8086— lo mandaría al principio de la memoria.</summary>
        public Memory? M;
        public int Position;
        public override int ReadByte() =>
            Memory.SegmentosSonSelectores && M != null
                ? M.RdLin8((uint)Position++)
                : Raw[Position++ & 0xFFFFF];
    }

    /// <summary>Punto de entrada desde un hueco del código lifteado: (baseFile, offset de
    /// segmento) — las mismas coordenadas del key-space del emitter (fk = baseFile + segOff).
    /// Resuelve dónde viven esos bytes en runtime y ejecuta.</summary>
    public static bool RunGap(Cpu c, Memory m, int baseFile, ushort segOff,
                              CallKeyFn? callKey, FarDispatchFn? farDispatch)
    {
        ushort cs, ip;
        if (baseFile == OverlayDirectory.ResidentBase)
        {
            // residente: linear = LoadBase + ImageBias + segOff (mundo memimage/snapshot).
            // El residente es >64K (multi-segmento): el CS bajo el que corre este código es
            // el CS ACTUAL (un near-call no cruza segmento), úsalo si el offset cuadra;
            // si no (CS de ISR/driver ajeno), cae al mapeo plano desde loadSeg.
            int lin = c.LoadBase + c.ImageBias + segOff;
            int rel = lin - c.CS * 16;
            if (rel >= 0 && rel <= 0xFFFF)
            {
                cs = c.CS;
                ip = (ushort)rel;
            }
            else
            {
                cs = (ushort)(c.LoadBase >> 4);
                ip = (ushort)(segOff + c.ImageBias);
            }
        }
        else if (baseFile >= OverlayDirectory.ResidentThreshold &&
                 Platform.DriverRegistry.TryGetSegOffForKey(baseFile + segOff, out ushort dseg, out ushort doff))
        {
            cs = dseg;
            ip = doff;
        }
        else if (OverlayWindowIpBase != 0)
        {
            // overlay VROOMM: si estamos ejecutándolo, ocupa la ventana ahora mismo
            cs = (ushort)((c.LoadBase >> 4) + OverlayWindowSegDelta);
            ip = (ushort)(OverlayWindowIpBase + segOff);
        }
        else
        {
            throw new NotSupportedException($"interp: no sé mapear el hueco base=0x{baseFile:X5}+0x{segOff:X4} a memoria");
        }
        return Run(c, m, cs, ip, callKey, farDispatch);
    }

    /// <summary>PARADA DE UNA VUELTA DE BUCLE. La arma <c>BancoCiego.EjecutaCaso</c> para los
    /// casos que el corpus grabo como VUELTA y no como llamada (ver
    /// <see cref="CorpusRecorder"/>): la ejecucion termina al volver a la cabecera del bucle con
    /// la pila de entrada. La primera visita —la de arranque— no cuenta, y por eso se recuerda el
    /// contador de instrucciones con el que se armo: sin eso el replay pararia antes de ejecutar
    /// nada y todo caso de bucle saldria verde sin haber corrido.</summary>
    public static bool ParadaVueltaActiva;
    static ushort _paradaCs, _paradaIp, _paradaSp, _paradaFin;
    static long _paradaInsn0;

    /// <summary>Arma la parada de vuelta. <paramref name="cuerpo"/> son los bytes del bucle: la
    /// vuelta tambien termina si el programa SE SALE de ese rango con la pila de entrada, que es
    /// como el original abandona el bucle. Se desarma con <see cref="DesarmaParadaVuelta"/>, y hay
    /// que hacerlo en un <c>finally</c>: es un estatico que lee el bucle del interprete.</summary>
    public static void ArmaParadaVuelta(ushort cs, ushort ip, ushort sp, int cuerpo)
    {
        _paradaCs = cs; _paradaIp = ip; _paradaSp = sp;
        _paradaFin = ip + cuerpo > 0xFFFF ? (ushort)0xFFFF : (ushort)(ip + cuerpo);
        _paradaInsn0 = InsnCount;
        ParadaVueltaActiva = true;
    }

    public static void DesarmaParadaVuelta() => ParadaVueltaActiva = false;

    /// <summary>Donde paro la vuelta final: la cabecera si dio otra vuelta, o el sitio por el
    /// que salio del bucle. Es lo que una vuelta TRADUCIDA tiene que devolver, y el juez lo
    /// compara: una traduccion que deja la memoria y los registros bien pero sigue por otro
    /// sitio descarrila el juego igual.</summary>
    public static int IpParada = -1;

    /// <summary>Cobra <paramref name="n"/> instrucciones del original sin ejecutarlas: el mismo
    /// trabajo de reloj que hace el bucle de <see cref="Run"/> por cada instruccion (el reloj de
    /// las IRQ grabadas, <c>Cpu.Step</c> y el contador). Lo usan las traducciones, via
    /// <c>Original.Instrucciones</c>, para que el tiempo del invitado corra igual que en el
    /// original. Ver alli por que hace falta.</summary>
    public static void Cobra(Cpu c, Memory m, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (ReplayIrq != null) ReplayIrq.PasoIrq(InsnCount - ReplayIrqBase, m);
            // Sin FrameGate (el juez), Step dice false al agotar el presupuesto. El interprete
            // para ahi y el caso sale `SinFin`; una traduccion no tiene a donde volver, asi que
            // se corta con una excepcion en vez de dejar al juez colgado para siempre.
            if (!c.Step())
                throw new InvalidOperationException(
                    "la traduccion agoto el presupuesto de instrucciones: un bucle que no termina?");
            InsnCount++;
        }
    }

    /// <summary>Ejecuta bytes originales desde cs:ip hasta agotar el frame de entrada.
    /// Devuelve true si salió con retf (el caller debe hacer c.FarRet = true).</summary>
    public static bool Run(Cpu c, Memory m, ushort cs, ushort ip,
                           CallKeyFn? callKey, FarDispatchFn? farDispatch)
    {
        ushort sp0 = c.SP;
        ushort ss0 = c.SS;   // nivel de pila de ENTRADA del Run: juez del IRET terminal en IsrMode
        c.CS = cs;   // el código interpretado puede leer CS (push cs / mov r,cs)
        // Depth of software INTs we vectored IN-INTERP within THIS Run (INT 3F overlay thunks,
        // and game-installed vectors like a game's INT 91h graphics VM). Their matching IRET must
        // return in-interp (pop IP:CS:FLAGS and continue), NOT end the Run — even when
        // WholeProgram is false (e.g. inside the INT 33h mouse callback, which sets it false).
        // Without this, the first IRET of such a handler ends the Run and aborts the caller
        // mid-way (measured on a test game: the cursor-erase INT 91h returns "out", so the cursor is erased but
        // never redrawn — the mouse cursor vanishes while moving).
        int softIntDepth = 0;
        // ESP al empezar la instrucción en curso, para el catch de #PF: un fallo restaura la
        // CPU al INICIO de la instrucción (386, instrucciones reiniciables). Sin esto, un
        // pusha/pushad/marco far cuyo N-ésimo push falla deja SP corrido y el reintento
        // vuelve a empujar lo ya empujado (medido en un juego DOS/4GW Pro, ver el espejo en Interp32).
        uint espAntesPf16 = c.ESP;
        bool espAntesValido = false;   // true sólo mientras se ejecuta el switch de la instrucción
        if (Trace) Console.Error.WriteLine($"[INTERP] enter {c.CS:X4}:{ip:X4} SP={sp0:X4}");
        var reader = new MemoryCodeReader { Raw = m.Raw, M = m };
        var decoder = Iced.Intel.Decoder.Create(16, reader, ip, DecoderOptions.None);

        try
        {
        // FALLOS DE PÁGINA: el bucle va dentro de un reintento, no dentro de un try por
        // instrucción. Cuando un acceso toca una página ausente, `Memory` lanza; aquí se entrega
        // la excepción 14 del invitado por su IDT y se REANUDA el bucle con `ip` intacto — al
        // volver del manejador (que trae la página del .SWP) la instrucción se repite, como en el
        // hardware. Sin la paginación encendida esto no cuesta nada: un try que nunca se dispara.
        while (true)
        {
        try
        {
        while (true)
        {
            espAntesValido = false;   // la maquinaria de cabecera no está cubierta por el snapshot
            _hist[_histIdx++ & (_hist.Length - 1)] = (uint)((c.CS << 16) | ip);
            if (_histLlenas < _hist.Length) _histLlenas++;
            // DESCARRILAMIENTO: nadie EJECUTA en la ventana de vídeo ni en la IVT. Si el código
            // acaba ahí es que un retorno se ha llevado un CS basura, y el sitio donde revienta
            // (si revienta) está ya lejísimos de la causa. Se avisa UNA vez, con la caja negra
            // todavía caliente — que es lo único que apunta a quién saltó.
            // El bool va DELANTE: en cuanto avisa una vez, esto es una lectura de un estatico por
            // instruccion y nada mas — el bucle de 16 bits es el camino mas caliente que hay.
            if (!_avisadoVram
                && ((c.CS & 0xF800) == 0xA800 || (c.CS & 0xF000) == 0xB000
                    || (c.CS == 0 && ip < 0x400)))
            {
                {
                    _avisadoVram = true;
                    Console.Error.WriteLine(
                        $"[DESCARRILA] ejecutando datos (ventana de video o IVT) @ {c.CS:X4}:{ip:X4} " +
                        $"SS:SP={c.SS:X4}:{c.SP:X4} hist={HistDump()}");
                }
            }
            // Camino recorrido, para `oracle-run` y la cobertura de ramas: qué direcciones
            // pisó DE VERDAD esta ejecución. Se rellena sólo si alguien puso la lista (una
            // comprobación de null por instrucción, nada al lado de decodificar), y hasta ahora
            // sólo lo registraba el código lifteado — el intérprete, que es el motor desde el
            // pivote a intérprete puro, no lo contaba.
            m.LogIP(ip | (c.CS << 16));
            OnStep?.Invoke(c.CS, ip);   // unpack probe: detecta el corte far descendente
            // Banco de pruebas por función (PAKU_CORPUS): abre un caso al arrancar una función de
            // interés y lo cierra cuando su pila se desmonta. Aquí y no en el `case Call` porque
            // CS/IP/SP bastan, y así vale igual para call near, far, indirecto o un jmp de cola.
            if (CorpusRecorder.Activo) CorpusRecorder.Paso(c, m, ip);
            if (GrabadorFotogramas.Activo) GrabadorFotogramas.Paso(c, m, ip);
            // PARADA DE UNA VUELTA DE BUCLE (casos `bucle` del corpus). Un caso que no es una
            // llamada no termina en un `ret`: termina cuando el programa vuelve a la cabecera del
            // bucle con la misma pila. El replay tiene que parar justo ahi o seguiria dando
            // vueltas hasta agotar el presupuesto, y el juez compararia el estado de la vuelta
            // equivocada. Apagado cuesta la lectura de un bool, como las dos lineas de al lado.
            if (ParadaVueltaActiva && c.SP == _paradaSp && c.CS == _paradaCs
                && InsnCount != _paradaInsn0
                && (ip == _paradaIp || ip < _paradaIp || ip > _paradaFin))
            { IpParada = ip; return false; }
            // FUNCIONES YA TRADUCIDAS (el tablero del remake). Si en esta direccion hay una
            // traduccion en C#, se ejecuta ELLA y el interprete continua donde ella diga: la
            // direccion de retorno si es una funcion, o la que devuelva si es una VUELTA de bucle.
            // Todo lo demas lo sigue haciendo el binario original. Va aqui, en la direccion de
            // ENTRADA, para que de igual como se haya llegado: call near, call far, salto de cola
            // o puntero indirecto. Apagado es una comparacion contra null.
            if (FuncionesPortadas.Activo && FuncionesPortadas.Ejecuta(c, m, ref ip))
            {
                // La traduccion ya consumio su propio `ret`. Si eso sube por encima del marco de
                // ESTE Run —que es lo que pasa cuando una traduccion llama con `o.Llama` a otra
                // que tambien esta traducida—, el Run se ha acabado, igual que tras un `ret`
                // interpretado. Sin esto el interprete seguia ejecutando el codigo del LLAMADOR
                // desde la direccion de retorno, y ese codigo lo iba a ejecutar despues el C# del
                // llamador: todo dos veces.
                if (!WholeProgram && !IsrMode && (short)(c.SP - sp0) > 0) return FuncionesPortadas.RecienteFueLejana;
                continue;
            }
            // EL RELOJ DE LAS IRQ GRABADAS (efecto "q" del corpus). Una funcion que espera una
            // interrupcion de hardware no retorna nunca en el replay: aqui se le entrega el
            // efecto NETO de aquella IRQ en la misma instruccion en que ocurrio. Apagado cuesta
            // una lectura de estatico y un salto, igual que la linea de arriba.
            if (ReplayIrq != null) ReplayIrq.PasoIrq(InsnCount - ReplayIrqBase, m);
            // mismo housekeeping de presupuesto/FrameGate/IRQs que las etiquetas lifteadas
            if (!c.Step()) return false;
            InsnCount++;

            // Entrada del stub BIOS en F000:00nn ejecutada COMO CÓDIGO. El idioma clásico para
            // encadenar al handler anterior es un salto lejano DIRECTO al punto de entrada de la
            // BIOS (`jmp far F000:0008` — un juego de prueba lo hace literal en su ISR de INT 8, 0824:0202), no
            // un INT ni un IRET. Ahí solo hay un 0xCF cosmético, así que se ejecutaba como un
            // IRET pelado y el trabajo de la BIOS NO se hacía nunca (el decremento del contador
            // de motor 0x40:0x40, el INT 1Ch, el EOI). Modelo DOSBox (callback en F000:xxxx
            // seguido de IRET): se ejecuta el handler C# y se sigue con el 0xCF, que hace el
            // IRET por el camino de siempre. Acotado al área de stubs y a vectores registrados.
            // El área nueva son 2 bytes por vector (NOP; IRET) y solo se despacha en el primero:
            // el NOP existe para que la detección de driver por vector funcione (Cpu.IvtStubBase).
            // EL MUNDO HA PASADO A 32 BITS. El cliente DPMI se fabrica un selector de código con
            // el bit D/B puesto y salta a él: a partir de ahí su código YA NO es de 16 bits y este
            // intérprete lo decodificaría mal (silenciosamente, que es lo peor). Se cede el control
            // a Interp32, que es el mismo motor que corre los juegos LE, con la MISMA tabla de
            // descriptores para resolver selectores.
            if (Memory.SegmentosSonSelectores
                && c.Platform is Platform.DosPlatform dp32 && dp32.Dpmi != null
                && dp32.Dpmi.EsCodigo32(c.CS))
                return CederA32(c, m, dp32, ip);

            // MUNDO MIXTO DOS/16M: el extensor real corre DENTRO del invitado (kernel de 16
            // bits) y el programa que carga puede tener segmentos de código de 32 bits (medido en
            // un juego DOS/16M: runtime de 16 en 0080/0088 y juego de 32 en 0098). El bit D del descriptor REAL
            // dice cómo decodificar; sin esto el intérprete de 16 leía el código de 32 mal y EN
            // SILENCIO (movzx repetidos, saltos a la nada). La ejecución rebota entre los dos
            // intérpretes: aquí se cede a Interp32, y su Fin.CambioA16 (o un INT que vectoriza
            // a una puerta de 16 bits) devuelve el control a este bucle.
            if (Memory.SegmentosSonSelectores && (c.Msw & 1) != 0
                && c.Platform is Platform.DosPlatform dpMix
                && (dpMix.Dpmi == null || !dpMix.Dpmi.EsCodigo32(c.CS))
                && EsCodigo32Pm16(c, m, c.CS))
            {
                uint eipMix = _eip32Pendiente != 0 ? _eip32Pendiente : ip;
                _eip32Pendiente = 0;
                var (seguirMix, ipMix) = Pm16MundoMixto(c, m, dpMix, eipMix);
                if (!seguirMix) return false;
                ip = ipMix;
                continue;
            }

            if (c.CS == Cpu.IvtStubSeg)
            {
                // CAMBIO A MODO PROTEGIDO: el cliente DPMI ha llegado al punto de entrada que le
                // dimos en INT 2Fh AX=1687h. Se engancha por EJECUTAR aquí y no por detectar una
                // llamada a propósito: un extensor embebido no llega con un `call far` sino con
                // un `retf` que se AUTOMODIFICA (parchea el byte de su `jmp` a 0xCB en runtime),
                // así que cualquier gancho basado en la forma de la transferencia se lo perdería.
                // Detrás hay un RETF de verdad, que es el que lleva el control adonde el cliente
                // quiera — ya con los selectores puestos.
                // Los cuatro puntos de entrada que publica el host DPMI: cuando el cliente entra
                // en uno, se anota con QUÉ registros. El contrato del cambio de modo directo no se
                // adivina — se lee de lo que el cliente pone al llamarlo.
                if (ip >= Cpu.DpmiSaveRealOff && ip <= Cpu.DpmiRawSwitchPmOff
                    && Platform.FlatServices.LogDpmi)
                    Console.Error.WriteLine(
                        $"[DPMI-STUB] entrada F000:{ip:X4}  AX={c.AX:X4} BX={c.BX:X4} CX={c.CX:X4} " +
                        $"DX={c.DX:X4} SI={c.SI:X4} DI={c.DI:X4} BP={c.BP:X4} " +
                        $"EBX={c.EBX:X8} ESI={c.ESI:X8} EDI={c.EDI:X8} " +
                        $"DS={c.DS:X4} ES={c.ES:X4} SS={c.SS:X4} SP={c.SP:X4} " +
                        $"pila=[{m.RdU16(c.SS, c.SP):X4} {m.RdU16(c.SS, (ushort)(c.SP + 2)):X4} " +
                        $"{m.RdU16(c.SS, (ushort)(c.SP + 4)):X4} {m.RdU16(c.SS, (ushort)(c.SP + 6)):X4}]");

                // CAMBIO DE MODO DIRECTO (DPMI AX=0306). El contrato de registros NO se dedujo de
                // la especificación: se MIDIÓ en el propio cliente, que lo prepara así justo antes
                // de saltar aquí (un extensor embebido, 08A2:14D7):
                //     AX = selector de DS   CX = el de ES   DX = el de SS
                //     EBX = ESP nuevo       SI:DI = destino al que hay que continuar
                // Aquí los dos modos comparten mundo —un selector que no esté en la tabla vale
                // `valor×16`, o sea lo mismo que un segmento real—, así que "cambiar de modo" es
                // exactamente esto: cargar los segmentos que pide y seguir donde dice.
                if (ip == Cpu.DpmiRawSwitchRealOff || ip == Cpu.DpmiRawSwitchPmOff)
                {
                    c.DS = c.AX; c.ES = c.CX; c.SS = c.DX; c.ESP = c.EBX;
                    c.CS = c.SI; ip = c.DI;
                    if (Platform.FlatServices.LogDpmi)
                        Console.Error.WriteLine($"[DPMI] cambio de modo directo -> {c.CS:X4}:{ip:X4} " +
                                                $"DS={c.DS:X4} SS={c.SS:X4} ESP={c.ESP:X8}");
                    continue;
                }

                if (ip == Cpu.DpmiSwitchOff
                    && c.Platform is Platform.DosPlatform dpmiDp && dpmiDp.Dpmi != null
                    && !Memory.SegmentosSonSelectores)
                {
                    dpmiDp.Dpmi.EntrarEnModoProtegido(c, m);
                    c.CF = false;          // la spec: CF=0 = el cambio salió bien
                }
                // El callback del ratón ha vuelto por su cadena de retorno (Cpu.CbRatonUirOff):
                // es el `UIR_Handler` del original, y levanta el guardia de reentrada. El stub en
                // sí es un RETF de verdad en memoria, así que aquí sólo se anota el paso.
                if (ip == Cpu.CbRatonUirOff) Platform.Int33hHandler.FinCallbackRaton(c, m);
                int stubInt = Cpu.IvtStubInt(ip);
                if (stubInt < 0 && ip <= 0xFF) stubInt = ip;   // área histórica de 1 byte
                if (stubInt >= 0
                    && c.Platform is Platform.DosPlatform stubDp && stubDp.HasIntHandler((byte)stubInt))
                    c.Platform.Int((byte)stubInt, c, m);
            }

            // Una salida de modo protegido se completa cuando CS se recarga, no cuando se apaga
            // PE (ver ModoProtegido16). Apagado, es una lectura de un bool estático por
            // instrucción, que es lo que ya cuesta cualquiera de las palancas de traza.
            if (_saliendoDePm) CierraSalidaDePm(c, m);

            uint linInsn = Memory.LinDeSegmento(c.CS, ip);
            Instruction insn;
            int kIns = (int)(linInsn & CacheMask16);
            long genIns = 0;
            bool cacheable = CacheDecode16 && linInsn >= Memory.Size;
            if (cacheable && _c16Lin[kIns] == linInsn && _c16Ip[kIns] == ip
                && _c16Gen[kIns] == (genIns = GenCombinada16(linInsn)))
            {
                insn = _c16Insn[kIns];
            }
            else
            {
                reader.Position = (int)linInsn;
                decoder.IP = ip;
                decoder.Decode(out insn);
                if (insn.Code == Code.INVALID)
                    throw new NotSupportedException($"interp: opcode inválido @ {c.CS:X4}:{ip:X4} (byte {m.Raw[((c.CS << 4) + ip) & 0xFFFFF]:X2}) hist={HistDump()}{FatalInfo(c, m, softIntDepth)}");
                if (cacheable)
                {
                    _c16Insn[kIns] = insn; _c16Lin[kIns] = linInsn; _c16Ip[kIns] = ip;
                    _c16Gen[kIns] = genIns != 0 ? genIns : GenCombinada16(linInsn);
                }
            }
            ushort next = (ushort)(ip + insn.Length);
            // LA FRONTERA DE LO DESCONOCIDO (PAKU_COBERTURA): aqui y no en la cabecera del bucle
            // porque hace falta insn.Length — marcando solo el primer byte, los de en medio de cada
            // instruccion saldrian como "nunca ejecutados" y la frontera vendria llena de agujeros
            // que no existen. Apagado es una lectura de un estatico y un salto.
            if (Cobertura.Activo) Cobertura.Marca(linInsn, insn.Length);
            if (Trace) Console.Error.WriteLine($"[INTERP] t={Cpu.TickCount} {c.CS:X4}:{ip:X4} {insn} AX={c.AX:X4} BX={c.BX:X4} CX={c.CX:X4} DX={c.DX:X4} SP={c.SP:X4} DS={c.DS:X4} ES={c.ES:X4} SS={c.SS:X4} F={c.FlagsWord():X4}");

            espAntesPf16 = c.ESP; espAntesValido = true;
            switch (insn.Mnemonic)
            {
                // --- transferencia de datos ---
                case Mnemonic.Mov:
                {
                    // Registros de control. `mov cr0, eax` es la OTRA forma de cambiar de modo
                    // (la que usa el extensor de Rational para volver a real; lmsw sólo enciende
                    // PE, no lo apaga), así que escribir CR0 pasa por el mismo sitio que lmsw.
                    if (insn.Op0Register is >= Register.CR0 and <= Register.CR15)
                    {
                        uint vCr = (uint)Read32(c, m, in insn, 1);
                        if (insn.Op0Register == Register.CR0)
                        {
                            // PAGINACIÓN: PakuPort trata lineal = física. Mientras el programa
                            // mapee identidad no se nota, y por eso hay que DECIRLO en vez de
                            // seguir en silencio: lo que falla después son direcciones altas que
                            // sólo existen a través de las tablas (un juego DOS/4GW Pro mapea su ejecutable
                            // en 0xFF000000 y sus escrituras se van al vacío).
                            if (((vCr ^ c.Msw) & 0x80000000u) != 0 && (_avisosPg++ < 12 || Memory.LogPaginas))
                                Console.Error.WriteLine(
                                    $"[PAGING] paginación {((vCr & 0x80000000u) != 0 ? "ON" : "OFF")} " +
                                    $"(cr0=0x{vCr:X8}, cr3=0x{c.Cr3:X8}) en {c.CS:X4}:{ip:X4}");
                            Memory.ConfiguraPaginacion((vCr & 0x80000000u) != 0, c.Cr3);
                            ModoProtegido16(c, m, vCr);
                        }
                        else if (insn.Op0Register == Register.CR2)
                        {
                            Memory.Cr2 = vCr;   // writable like on hardware (rare, but cheap)
                        }
                        else if (insn.Op0Register == Register.CR3)
                        {
                            // Recargar CR3 es la forma canónica de decir "mis tablas han
                            // cambiado": vacía el TLB entero, y por eso el extensor lo hace
                            // después de tocarlas.
                            c.Cr3 = vCr;
                            Memory.ConfiguraPaginacion(Memory.Paginacion, vCr);
                        }
                        break;
                    }
                    if (insn.Op1Register is >= Register.CR0 and <= Register.CR15)
                    {
                        // CR3 se guarda tal cual: el extensor lo LEE para localizar su propio
                        // directorio de páginas, y contestarle 0 le decía que estaba en la
                        // dirección física 0 — o sea, encima de la tabla de interrupciones.
                        SetR32(c, insn.Op0Register, insn.Op1Register switch
                        {
                            Register.CR0 => c.Msw,
                            // CR2 es la lineal del último #PF: el VMM de DOS/4GW Professional
                            // la lee para decidir si la página es de su arena o el fallo es
                            // fatal — con 0 siempre era "error 2001: unexpected page fault".
                            Register.CR2 => Memory.Cr2,
                            Register.CR3 => c.Cr3,
                            _ => 0u,
                        });
                        break;
                    }
                    int size = OpSize(in insn, 0);
                    var d = DstOf(c, in insn, 0);
                    if (size == 32) Write32(c, m, in d, Read32(c, m, in insn, 1));
                    else if (size == 16) Write16(c, m, in d, Read16(c, m, in insn, 1));
                    else Write8(c, m, in d, Read8(c, m, in insn, 1));
                    break;
                }
                case Mnemonic.Xchg:
                {
                    int size = OpSize(in insn, 0);
                    var d0 = DstOf(c, in insn, 0);
                    var d1 = DstOf(c, in insn, 1);
                    if (size == 32)
                    {
                        uint t0 = Read32D(c, m, in d0), t1 = Read32D(c, m, in d1);
                        Write32(c, m, in d0, t1); Write32(c, m, in d1, t0);
                    }
                    else if (size == 16)
                    {
                        ushort t0 = Read16D(c, m, in d0), t1 = Read16D(c, m, in d1);
                        Write16(c, m, in d0, t1); Write16(c, m, in d1, t0);
                    }
                    else
                    {
                        byte t0 = Read8D(c, m, in d0), t1 = Read8D(c, m, in d1);
                        Write8(c, m, in d0, t1); Write8(c, m, in d1, t0);
                    }
                    break;
                }
                case Mnemonic.Lea:
                    // LEA no toca memoria, así que con direccionamiento de 32 bits el resultado NO
                    // se recorta al límite del segmento (por eso MemEa32 y no MemEa).
                    if (RegSize(insn.Op0Register) == 32) SetR32(c, insn.Op0Register, MemEa32(c, in insn));
                    else SetR16(c, insn.Op0Register, MemEa(c, in insn));
                    break;
                case Mnemonic.Push:
                    if (SegStack32(in insn))
                    {
                        // El registro tiene 2 bytes pero la pila se mueve 4, con la mitad alta
                        // a cero (ver SegStack32).
                        Push32(c, m, GetR16(c, insn.Op0Register));
                    }
                    else if (StackOpSize(in insn) == 32)
                    {
                        // 386: `push esp` apila el ESP de ANTES (medido contra Unicorn), y la
                        // dirección efectiva de `push dword [esp+n]` también se calcula con el
                        // ESP previo. Por eso la fuente se evalúa PRIMERO.
                        Push32(c, m, Read32(c, m, in insn, 0));
                    }
                    else
                    {
                        // `push sp` apila el SP de ANTES de decrementar, que es la semántica del
                        // 286 en adelante; el 8086 apilaba el nuevo. NO es un detalle académico:
                        // `push sp / pop bx / cmp bx, sp` es el segundo escalón de la sonda de CPU
                        // canónica, y contestar como un 8086 mientras FLAGS ya contesta como un 386
                        // (Cpu._ioplNt) es incoherente. Medido en un juego DOS/16M y otro DOS/4GW Pro: la sonda del
                        // extensor de Rational lo tomaba por un 8086/186 y abortaba con
                        // "DOS/16M error: [15] protected mode available only with 386 or 486".
                        // La fuente se evalúa PRIMERO, igual que ya hacía el camino de 32 bits.
                        ushort _v16 = Read16(c, m, in insn, 0);
                        SpA(c, SpDe(c) - 2);
                        PilaWr16(c, m, SpDe(c), _v16);
                    }
                    break;
                case Mnemonic.Pop:
                {
                    if (SegStack32(in insn))
                    {
                        // Sólo la palabra baja llega al selector; la pila avanza 4 (ver SegStack32).
                        SetR16(c, insn.Op0Register, PilaRd16(c, m, SpDe(c))); SpA(c, SpDe(c) + 4); break;
                    }
                    var d = DstOf(c, in insn, 0);
                    if (StackOpSize(in insn) == 32) Write32(c, m, in d, Pop32(c, m));
                    else Write16(c, m, in d, Pop16(c, m));
                    break;
                }
                case Mnemonic.Pushf: SpA(c, SpDe(c) - 2); PilaWr16(c, m, SpDe(c), c.FlagsWord()); break;
                case Mnemonic.Popf: c.SetFlagsWord(Pop16(c, m)); break;
                case Mnemonic.Pushfd: Push32(c, m, c.EFlagsWord()); break;
                case Mnemonic.Popfd: c.SetEFlagsWord(Pop32(c, m)); break;
                case Mnemonic.Xlatb:
                {
                    ushort xseg = insn.SegmentPrefix != Register.None ? GetR16(c, insn.SegmentPrefix) : c.DS;
                    c.AL = m.RdU8(xseg, (ushort)(c.BX + c.AL));
                    break;
                }
                case Mnemonic.Cbw: c.AX = (ushort)(short)(sbyte)c.AL; break;
                case Mnemonic.Cwd: c.DX = (ushort)(((c.AX & 0x8000) != 0) ? 0xFFFF : 0); break;
                case Mnemonic.Cwde: c.EAX = (uint)(int)(short)c.AX; break;
                case Mnemonic.Cdq: c.EDX = (c.EAX & 0x80000000u) != 0 ? 0xFFFFFFFFu : 0u; break;
                case Mnemonic.Lahf: c.AH = (byte)c.FlagsWord(); break;
                case Mnemonic.Sahf: c.Sahf(); break;
                case Mnemonic.Salc: c.AL = (byte)(c.CF ? 0xFF : 0x00); break;
                case Mnemonic.Daa: c.Daa(); break;
                case Mnemonic.Das: c.Das(); break;
                case Mnemonic.Aaa: c.Aaa(); break;
                case Mnemonic.Aas: c.Aas(); break;
                case Mnemonic.Aam:
                    // aam imm divide AL/imm; imm=0 -> #DE, que el 8086 convierte en INT 0 (igual que
                    // div/idiv por cero, más abajo). Sin esto la excepción mata el hilo en vez de dejar
                    // que el trampolín de INT 0 del juego decida. aad NO divide (multiplica), no puede #DE.
                    try { c.Aam(insn.Immediate8); }
                    catch (DivideByZeroException)
                    {
                        if (Trace || Cpu.GapTrace) Console.Error.WriteLine($"[INTERP] #DE->INT0 @ {c.CS:X4}:{ip:X4} (aam {insn.Immediate8})");
                        c.Platform?.Int(0x00, c, m);
                    }
                    break;
                case Mnemonic.Aad: c.Aad(insn.Immediate8); break;
                case Mnemonic.Lds: LoadFar(c, m, in insn, seg => c.DS = seg); break;
                case Mnemonic.Les: LoadFar(c, m, in insn, seg => c.ES = seg); break;
                case Mnemonic.Lfs: LoadFar(c, m, in insn, seg => c.FS = seg); break;
                case Mnemonic.Lgs: LoadFar(c, m, in insn, seg => c.GS = seg); break;
                case Mnemonic.Lss: LoadFar(c, m, in insn, seg => c.SS = seg); break;

                // --- aritmética binaria ---
                case Mnemonic.Add: Bin(c, m, in insn, (x, y) => c.Add16(x, y), (x, y) => c.Add8(x, y), (x, y) => c.Add32(x, y)); break;
                case Mnemonic.Adc: Bin(c, m, in insn, (x, y) => c.Adc16(x, y), (x, y) => c.Adc8(x, y), (x, y) => c.Adc32(x, y)); break;
                case Mnemonic.Sub: Bin(c, m, in insn, (x, y) => c.Sub16(x, y), (x, y) => c.Sub8(x, y), (x, y) => c.Sub32(x, y)); break;
                case Mnemonic.Sbb: Bin(c, m, in insn, (x, y) => c.Sbb16(x, y), (x, y) => c.Sbb8(x, y), (x, y) => c.Sbb32(x, y)); break;
                case Mnemonic.And: Bin(c, m, in insn, (x, y) => c.And16(x, y), (x, y) => c.And8(x, y), (x, y) => c.And32(x, y)); break;
                case Mnemonic.Or:  Bin(c, m, in insn, (x, y) => c.Or16(x, y),  (x, y) => c.Or8(x, y),  (x, y) => c.Or32(x, y)); break;
                case Mnemonic.Xor: Bin(c, m, in insn, (x, y) => c.Xor16(x, y), (x, y) => c.Xor8(x, y), (x, y) => c.Xor32(x, y)); break;
                case Mnemonic.Cmp:
                {
                    int size = OpSize(in insn, 0);
                    if (size == 32) c.Cmp32(Read32(c, m, in insn, 0), Read32(c, m, in insn, 1));
                    else if (size == 16) c.Cmp16(Read16(c, m, in insn, 0), Read16(c, m, in insn, 1));
                    else c.Cmp8(Read8(c, m, in insn, 0), Read8(c, m, in insn, 1));
                    break;
                }
                case Mnemonic.Test:
                {
                    int size = OpSize(in insn, 0);
                    if (size == 32) c.Test32(Read32(c, m, in insn, 0), Read32(c, m, in insn, 1));
                    else if (size == 16) c.Test16(Read16(c, m, in insn, 0), Read16(c, m, in insn, 1));
                    else c.Test8(Read8(c, m, in insn, 0), Read8(c, m, in insn, 1));
                    break;
                }

                // --- unarias ---
                case Mnemonic.Inc: Unary(c, m, in insn, x => c.Inc16(x), x => c.Inc8(x), x => c.Inc32(x)); break;
                case Mnemonic.Dec: Unary(c, m, in insn, x => c.Dec16(x), x => c.Dec8(x), x => c.Dec32(x)); break;
                case Mnemonic.Neg: Unary(c, m, in insn, x => c.Neg16(x), x => c.Neg8(x), x => c.Neg32(x)); break;
                case Mnemonic.Not: Unary(c, m, in insn, Cpu.Not16, Cpu.Not8, Cpu.Not32); break;

                // --- desplazamientos / rotaciones ---
                case Mnemonic.Shl: case Mnemonic.Sal: Shift(c, m, in insn, (v, n) => c.Shl16(v, n), (v, n) => c.Shl8(v, n), (v, n) => c.Shl32(v, n)); break;
                case Mnemonic.Shr: Shift(c, m, in insn, (v, n) => c.Shr16(v, n), (v, n) => c.Shr8(v, n), (v, n) => c.Shr32(v, n)); break;
                case Mnemonic.Sar: Shift(c, m, in insn, (v, n) => c.Sar16(v, n), (v, n) => c.Sar8(v, n), (v, n) => c.Sar32(v, n)); break;
                case Mnemonic.Rol: Shift(c, m, in insn, (v, n) => c.Rol16(v, n), (v, n) => c.Rol8(v, n), (v, n) => c.Rol32(v, n)); break;
                case Mnemonic.Ror: Shift(c, m, in insn, (v, n) => c.Ror16(v, n), (v, n) => c.Ror8(v, n), (v, n) => c.Ror32(v, n)); break;
                case Mnemonic.Rcl: Shift(c, m, in insn, (v, n) => c.Rcl16(v, n), (v, n) => c.Rcl8(v, n), (v, n) => c.Rcl32(v, n)); break;
                case Mnemonic.Rcr: Shift(c, m, in insn, (v, n) => c.Rcr16(v, n), (v, n) => c.Rcr8(v, n), (v, n) => c.Rcr32(v, n)); break;

                // --- mul / div ---
                case Mnemonic.Mul:
                    switch (OpSize(in insn, 0))
                    {
                        case 32: c.Mul32(Read32(c, m, in insn, 0)); break;
                        case 16: c.Mul16(Read16(c, m, in insn, 0)); break;
                        default: c.Mul8(Read8(c, m, in insn, 0)); break;
                    }
                    break;
                case Mnemonic.Div:
                case Mnemonic.Idiv:
                    try
                    {
                        bool sgn = insn.Mnemonic == Mnemonic.Idiv;
                        switch (OpSize(in insn, 0))
                        {
                            case 32: { uint s = Read32(c, m, in insn, 0); if (sgn) c.Idiv32(s); else c.Div32(s); break; }
                            case 16: { ushort s = Read16(c, m, in insn, 0); if (sgn) c.Idiv16(s); else c.Div16(s); break; }
                            default: { byte s = Read8(c, m, in insn, 0); if (sgn) c.Idiv8(s); else c.Div8(s); break; }
                        }
                    }
                    catch (ArithmeticException)
                    {
                        // #DE fiel al 8086: INT 0 con retorno a la instrucción que sigue.
                        // Cubre las DOS caras del fault: divisor cero (DivideByZeroException;
                        // un juego de prueba divide por cero en rutas reales — tempo de una
                        // fanfarria con buffer sin cargar) y cociente desbordado (OverflowException;
                        // otro juego en el arranque). El 8086 vectoriza ambas por INT 0 — matar el
                        // hilo aquí es lo infiel, no el div.
                        if (Trace || Cpu.GapTrace) Console.Error.WriteLine($"[INTERP] #DE->INT0 @ {c.CS:X4}:{ip:X4} ({insn})");
                        c.Platform?.Int(0x00, c, m);
                    }
                    break;
                case Mnemonic.Imul:
                    switch (insn.OpCount)
                    {
                        case 1:
                            switch (OpSize(in insn, 0))
                            {
                                case 32: c.Imul32(Read32(c, m, in insn, 0)); break;
                                case 16: c.Imul16(Read16(c, m, in insn, 0)); break;
                                default: c.Imul8(Read8(c, m, in insn, 0)); break;
                            }
                            break;
                        case 2:
                            if (RegSize(insn.Op0Register) == 32)
                                SetR32(c, insn.Op0Register, c.Imul32Two(GetR32(c, insn.Op0Register), Read32(c, m, in insn, 1)));
                            else
                                SetR16(c, insn.Op0Register, c.Imul16Two(GetR16(c, insn.Op0Register), Read16(c, m, in insn, 1)));
                            break;
                        case 3:
                            if (RegSize(insn.Op0Register) == 32)
                                SetR32(c, insn.Op0Register, c.Imul32Two(Read32(c, m, in insn, 1), Read32(c, m, in insn, 2)));
                            else
                                SetR16(c, insn.Op0Register, c.Imul16Two(Read16(c, m, in insn, 1), Read16(c, m, in insn, 2)));
                            break;
                        default:
                            throw new NotSupportedException($"interp: imul forma desconocida @ {c.CS:X4}:{ip:X4}");
                    }
                    break;

                // --- instrucciones de cadena ---
                case Mnemonic.Movsb: StringOp(c, m, in insn, 8, 'm'); break;
                case Mnemonic.Movsw: StringOp(c, m, in insn, 16, 'm'); break;
                case Mnemonic.Movsd: StringOp(c, m, in insn, 32, 'm'); break;
                case Mnemonic.Stosb: StringOp(c, m, in insn, 8, 's'); break;
                case Mnemonic.Stosw: StringOp(c, m, in insn, 16, 's'); break;
                case Mnemonic.Stosd: StringOp(c, m, in insn, 32, 's'); break;
                case Mnemonic.Lodsb: StringOp(c, m, in insn, 8, 'l'); break;
                case Mnemonic.Lodsw: StringOp(c, m, in insn, 16, 'l'); break;
                case Mnemonic.Lodsd: StringOp(c, m, in insn, 32, 'l'); break;
                case Mnemonic.Scasb: StringOp(c, m, in insn, 8, 'z'); break;
                case Mnemonic.Scasw: StringOp(c, m, in insn, 16, 'z'); break;
                case Mnemonic.Scasd: StringOp(c, m, in insn, 32, 'z'); break;
                case Mnemonic.Cmpsb: StringOp(c, m, in insn, 8, 'c'); break;
                case Mnemonic.Cmpsw: StringOp(c, m, in insn, 16, 'c'); break;
                case Mnemonic.Cmpsd: StringOp(c, m, in insn, 32, 'c'); break;

                // --- control de flujo ---
                case Mnemonic.Jmp:
                    if (insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                    {
                        // Un salto HACIA ATRAS es la definicion operativa de "aqui empieza una
                        // vuelta de bucle": no se infiere del binario, se observa. Lo usa el modo
                        // auto del grabador de corpus para dar casos al codigo que no vive dentro
                        // de ninguna llamada — ver CorpusRecorder.NotaBucle.
                        if (CorpusRecorder.Activo && insn.NearBranchTarget < ip)
                            CorpusRecorder.NotaBucle(c, c.CS, (ushort)insn.NearBranchTarget, ip);
                        ip = (ushort)insn.NearBranchTarget; continue;
                    }
                    if (insn.Op0Kind == OpKind.FarBranch16)
                    {
                        // salto de cola far: espejo del lifteado (FarDispatch + return). El código
                        // despachado consume la dirección de retorno del frame que estamos
                        // interpretando — el frame termina aquí. Sin FarDispatch (verificación
                        // pura): emular como hardware, seguir interpretando en el destino.
                        if (farDispatch != null && farDispatch(c, m, insn.FarBranchSelector, insn.FarBranch16)) return false;
                        c.CS = insn.FarBranchSelector; ip = insn.FarBranch16;
                        continue;
                    }
                    if (insn.Op0Kind == OpKind.Memory)
                    {
                        if (insn.MemorySize is MemorySize.UInt16 or MemorySize.WordOffset)
                        {
                            // near indirecto: mismo CS, solo mueve ip — emulación directa
                            ip = m.RdU16(MemSeg(c, in insn), MemEa(c, in insn));
                            continue;
                        }
                        // Puntero far de 32 bits (`fword ptr`, con prefijo 0x66): el offset ocupa
                        // CUATRO bytes y el selector va en +4, no en +2. Leerlo siempre en +2 sacaba
                        // el selector de la mitad ALTA DEL OFFSET —normalmente cero— y la
                        // transferencia acababa en el segmento 0, a ejecutar la tabla de
                        // interrupciones. Lo usa cualquier código de 16 bits en modo protegido: el
                        // extensor embebido llama así a su rutina de bajada a modo real.
                        uint fOffCompleto = insn.MemorySize == MemorySize.SegPtr32
                            ? m.RdU32(MemSeg(c, in insn), MemEa(c, in insn))
                            : m.RdU16(MemSeg(c, in insn), MemEa(c, in insn));
                        ushort fOff = (ushort)fOffCompleto;
                        ushort fSeg = m.RdU16(MemSeg(c, in insn),
                            (ushort)(MemEa(c, in insn) + (insn.MemorySize == MemorySize.SegPtr32 ? 4 : 2)));
                        // Mundo mixto: offset entero si el destino es código de 32 bits.
                        if ((c.Msw & 1) != 0 && EsCodigo32Pm16(c, m, fSeg))
                            _eip32Pendiente = fOffCompleto;
                        // Threaded-runtime entry via far-indirect jmp (e.g. Turbo Basic's VM
                        // dispatch entry `jmp dword cs:[00D4]` in a Turbo Basic game, whose target begins with
                        // `int ECh`): keep interpreting in-place. farDispatch would hand the
                        // p-code entry to lifted code whose nested int-lowering seeds a bogus
                        // IP=0 return frame, so the dispatcher reads the INT opcode itself as a
                        // p-code operand and derails. Same failure the in-interp INT vectoring
                        // (case Int below) already avoids for the interpreted path.
                        if (IsThreadedIntEntry(c, m, fSeg, fOff))
                        { c.CS = fSeg; ip = fOff; continue; }
                        if (farDispatch != null && farDispatch(c, m, fSeg, fOff)) return false;
                        c.CS = fSeg; ip = fOff;
                        continue;
                    }
                    if (insn.Op0Kind == OpKind.Register)
                    { ip = GetR16(c, insn.Op0Register); continue; }
                    // `jmp far` DIRECTO con operando de 32 bits (prefijo 0x66): el selector va
                    // igual en FarBranchSelector, sólo cambia el ancho del offset. Lo emite el
                    // código de 16 bits en modo protegido — medido en un juego DOS/4GW, 0070:431D, ya con
                    // CS siendo un selector.
                    if (insn.Op0Kind == OpKind.FarBranch32)
                    {
                        if (farDispatch != null && farDispatch(c, m, insn.FarBranchSelector, (ushort)insn.FarBranch32))
                            return false;
                        c.CS = insn.FarBranchSelector; ip = (ushort)insn.FarBranch32;
                        // Mundo mixto: offset entero si el destino es código de 32 bits.
                        if ((c.Msw & 1) != 0 && EsCodigo32Pm16(c, m, c.CS))
                            _eip32Pendiente = insn.FarBranch32;
                        continue;
                    }
                    throw new NotSupportedException(
                        $"interp: jmp forma desconocida ({insn.Op0Kind}) @ {c.CS:X4}:{ip:X4}");

                case Mnemonic.Loop:
                    c.CX--; if (c.CX != 0) { NotaAtras(c, in insn, ip); ip = (ushort)insn.NearBranchTarget; continue; }
                    break;
                case Mnemonic.Loope:
                    c.CX--; if (c.CX != 0 && c.ZF) { NotaAtras(c, in insn, ip); ip = (ushort)insn.NearBranchTarget; continue; }
                    break;
                case Mnemonic.Loopne:
                    c.CX--; if (c.CX != 0 && !c.ZF) { NotaAtras(c, in insn, ip); ip = (ushort)insn.NearBranchTarget; continue; }
                    break;
                case Mnemonic.Jcxz:
                    if (c.CX == 0) { NotaAtras(c, in insn, ip); ip = (ushort)insn.NearBranchTarget; continue; }
                    break;
                case Mnemonic.Jecxz:
                    // Con prefijo 0x67 el contador es ECX entero, no CX: un ECX de 0x00010000
                    // NO salta aunque su mitad baja sea cero.
                    if (c.ECX == 0) { ip = (ushort)insn.NearBranchTarget; continue; }
                    break;

                case Mnemonic.Call:
                {
                    if (insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                    {
                        ushort tgt = (ushort)insn.NearBranchTarget;
                        if (CorpusRecorder.Activo) CorpusRecorder.NotaFuncion(c, c.CS, tgt);
                        Push16(c, m, next);
                        ushort spRet = c.SP;   // celda de la dir. de retorno (el callee puede ajustarla: inline params)
                        int fk = KeyOf(c, c.CS, tgt);
                        ushort csCall = c.CS;  // near call: CS must be unchanged on return (lifted callee may leave its own)
                        if (callKey != null && fk >= OverlayDirectory.ResidentBase && callKey(c, m, fk))
                        {
                            // corrió lifteado; su ret consumió (y quizá ajustó) el retorno:
                            // el valor final popeado sigue en la celda (nada lo pisó aún)
                            if (c.FarRet)
                            {
                                if ((short)(c.SP - sp0) > 0) { c.NoteFarRetUnwind(); return true; }
                                c.FarRet = false;
                                if (c.FarRetIp != m.RdU16(c.SS, spRet))
                                {
                                    // near co-routine (mirror of the lifted CallGuard redirect,
                                    // gate NCORA): a consumed mismatch popped IMMEDIATELY ABOVE
                                    // this call's return cell (hand-pushed continuation at this
                                    // site) with the call-site's CS — continue AT it in this same
                                    // frame (loop-friendly, no recursion)
                                    if (c.FarRetSs == c.SS && c.FarRetSp == (ushort)(spRet + 2) &&
                                        c.ShouldFarRetRedirectTo(csCall))
                                    { c.CS = c.FarRetCs; ip = c.FarRetIp; continue; }
                                    c.NoteFarRetUnwind();
                                }
                            }
                            c.CS = csCall;
                            ip = m.RdU16(c.SS, spRet);
                            continue;
                        }
                        ip = tgt;   // interpretar el callee (el retorno ya está empujado)
                        continue;
                    }
                    if (insn.Op0Kind == OpKind.FarBranch16)
                    {
                        if (CorpusRecorder.Activo) CorpusRecorder.NotaFuncion(c, insn.FarBranchSelector, insn.FarBranch16);
                        Push16(c, m, c.CS);
                        Push16(c, m, next);
                        if (farDispatch == null)
                        {
                            // verificación pura: emular como hardware (el retf del callee vuelve)
                            c.CS = insn.FarBranchSelector; ip = insn.FarBranch16;
                            continue;
                        }
                        ushort spRetF = c.SP;
                        ushort pCsF = m.RdU16(c.SS, (ushort)(spRetF + 2));   // pushed CS, by value (clase #8)
                        // Si el despachador NO se hace cargo, la llamada se INTERPRETA como la haría
                        // el hardware. Antes se ignoraba el bool aquí (y sólo aquí: las otras ramas
                        // de far-call ya lo miran) y se simulaba el retorno, o sea que el callee no
                        // corría y nadie lo decía. En producción el FarDispatch generado devuelve
                        // true siempre, así que esto no cambia los juegos probados; lo que arregla es el
                        // intérprete puro — medido en el corpus de un juego de prueba, `b5a4` hacía
                        // `call far 1615:0000` y volvía sin ejecutar el thunk que hay ahí.
                        if (!farDispatch(c, m, insn.FarBranchSelector, insn.FarBranch16))
                        {
                            c.CS = insn.FarBranchSelector; ip = insn.FarBranch16;
                            continue;
                        }
                        if (c.FarRet)
                        {
                            if ((short)(c.SP - sp0) > 0)
                            {
                                // By-value genuine return (clase #8, gate PSTK): the retf popped EXACTLY
                                // the pair this call pushed — a driver with a relocated private stack makes
                                // the sp0 delta a cross-stack compare. Hardware continues at the pair.
                                if (c.FarRetIp == next && c.FarRetCs == pCsF) { c.FarRet = false; c.CS = pCsF; ip = next; continue; }
                                // Intra-segment co-routine continuation: resume at the FIRST discard (parte J)
                                if (c.ShouldFarRetRedirect()) { c.FarRet = false; c.CS = c.FarRetCs; ip = c.FarRetIp; continue; }
                                c.NoteFarRetUnwind(); return true;
                            }
                            c.FarRet = false;
                            // co-routine: retf returned to an IP other than this call's inline pair →
                            // continue AT the real destination (far call pushed CS, so FarRetCs is reliable)
                            if (c.FarRetIp != m.RdU16(c.SS, spRetF))
                            {
                                if (c.ShouldFarRetRedirect()) { c.CS = c.FarRetCs; ip = c.FarRetIp; continue; }
                                c.NoteFarRetUnwind();
                            }
                        }
                        // retf semantics: restore the CALLER's CS from the pushed slot — the lifted
                        // callee may leave c.CS pointing at its own segment (bug seen in a test game: fetch at 2B14:2B67)
                        c.CS = m.RdU16(c.SS, (ushort)(spRetF + 2));
                        ip = m.RdU16(c.SS, spRetF);
                        continue;
                    }
                    if (insn.Op0Kind == OpKind.Memory)
                    {
                        ushort mseg = MemSeg(c, in insn), mea = MemEa(c, in insn);
                        if (insn.MemorySize is MemorySize.UInt16 or MemorySize.WordOffset)
                        {
                            // call near indirecto: espejo del lifteado (push ret + FarDispatch a c.CS:tgt)
                            ushort tgt = m.RdU16(mseg, mea);
                            if (CorpusRecorder.Activo) CorpusRecorder.NotaFuncion(c, c.CS, tgt);
                            Push16(c, m, next);
                            ushort spRet = c.SP;
                            int fk = KeyOf(c, c.CS, tgt);
                            ushort csCall = c.CS;  // near call: CS unchanged on return
                            if (callKey != null && fk >= OverlayDirectory.ResidentBase && callKey(c, m, fk))
                            {
                                if (c.FarRet)
                                {
                                    if ((short)(c.SP - sp0) > 0) { c.NoteFarRetUnwind(); return true; }
                                    c.FarRet = false;
                                    if (c.FarRetIp != m.RdU16(c.SS, spRet))
                                    {
                                        // near co-routine mirror (gate NCORA) — see the direct case
                                        if (c.FarRetSs == c.SS && c.FarRetSp == (ushort)(spRet + 2) &&
                                            c.ShouldFarRetRedirectTo(csCall))
                                        { c.CS = c.FarRetCs; ip = c.FarRetIp; continue; }
                                        c.NoteFarRetUnwind();
                                    }
                                }
                                c.CS = csCall;
                                ip = m.RdU16(c.SS, spRet);
                                continue;
                            }
                            ip = tgt;
                            continue;
                        }
                        // Puntero far de 32 bits (`fword ptr`, con prefijo 0x66): el offset ocupa
                        // CUATRO bytes y el selector va en +4, no en +2. Leerlo siempre en +2 sacaba
                        // el selector de la mitad ALTA DEL OFFSET —normalmente cero— y la
                        // transferencia acababa en el segmento 0, a ejecutar la tabla de
                        // interrupciones. Lo usa cualquier código de 16 bits en modo protegido: el
                        // extensor embebido llama así a su rutina de bajada a modo real.
                        bool ptr32 = insn.MemorySize == MemorySize.SegPtr32;
                        uint fOffCall = ptr32 ? m.RdU32(mseg, mea) : m.RdU16(mseg, mea);
                        ushort fOff = (ushort)fOffCall;
                        ushort fSeg = m.RdU16(mseg, (ushort)(mea + (ptr32 ? 4 : 2)));
                        if (CorpusRecorder.Activo) CorpusRecorder.NotaFuncion(c, fSeg, fOff);
                        if (ptr32)
                        {
                            // Mundo mixto: offset entero si el destino es código de 32 bits.
                            if ((c.Msw & 1) != 0 && EsCodigo32Pm16(c, m, fSeg))
                                _eip32Pendiente = fOffCall;
                            // Llamada far de 32 bits: el marco son CS y EIP de CUATRO bytes cada
                            // uno, ocho en total — es lo que el `retfd` del destino va a desapilar.
                            // Empujar el marco de 16 dejaba la pila descuadrada y el retorno se iba
                            // al segmento 0. Aquí no pasa por FarDispatch: esto sólo ocurre en modo
                            // protegido, donde no hay código lifteado al que despachar.
                            Push32(c, m, c.CS);
                            Push32(c, m, next);
                            c.CS = fSeg; ip = fOff;
                            continue;
                        }
                        Push16(c, m, c.CS);
                        Push16(c, m, next);
                        ushort spRetFi = c.SP;
                        ushort pCsFi = m.RdU16(c.SS, (ushort)(spRetFi + 2));   // pushed CS, by value (clase #8)
                        if (farDispatch != null && farDispatch(c, m, fSeg, fOff))
                        {
                            if (c.FarRet)
                            {
                                if ((short)(c.SP - sp0) > 0)
                                {
                                    // By-value genuine return (clase #8, gate PSTK) — see the far-direct case
                                    if (c.FarRetIp == next && c.FarRetCs == pCsFi) { c.FarRet = false; c.CS = pCsFi; ip = next; continue; }
                                    // Intra-segment co-routine continuation: resume at the FIRST discard (parte J)
                                    if (c.ShouldFarRetRedirect()) { c.FarRet = false; c.CS = c.FarRetCs; ip = c.FarRetIp; continue; }
                                    c.NoteFarRetUnwind(); return true;
                                }
                                c.FarRet = false;
                                // co-routine (boot-real locus in a test game: interp->lifted far indirect): continue
                                // at the real retf destination instead of resuming at the call pair
                                if (c.FarRetIp != m.RdU16(c.SS, spRetFi))
                                {
                                    if (c.ShouldFarRetRedirect()) { c.CS = c.FarRetCs; ip = c.FarRetIp; continue; }
                                    c.NoteFarRetUnwind();
                                }
                            }
                            // retf semantics: restore the CALLER's CS (see the far-direct case above)
                            c.CS = m.RdU16(c.SS, (ushort)(spRetFi + 2));
                            ip = m.RdU16(c.SS, spRetFi);
                            continue;
                        }
                        c.CS = fSeg; ip = fOff;
                        continue;
                    }
                    if (insn.Op0Kind == OpKind.Register)
                    {
                        ushort tgt = GetR16(c, insn.Op0Register);
                        if (CorpusRecorder.Activo) CorpusRecorder.NotaFuncion(c, c.CS, tgt);
                        Push16(c, m, next);
                        ip = tgt;
                        continue;
                    }
                    throw new NotSupportedException($"interp: call forma desconocida @ {c.CS:X4}:{ip:X4}");
                }

                case Mnemonic.Ret:
                {
                    int extra = insn.OpCount >= 1 ? insn.Immediate16 : 0;
                    ushort retIp = PilaRd16(c, m, SpDe(c));
                    SpA(c, SpDe(c) + (uint)(2 + extra));
                    if (!WholeProgram && !IsrMode && (short)(c.SP - sp0) > 0)
                    {
                        // Probe (NCOR-D hypothesis, menus front): a ret escaping ABOVE the entry
                        // frame by more than its own cell discards retIp with no telemetry — the
                        // lifted caller resumes inline (silent divergence). +2 = the normal exit.
                        if (Cpu.GapTrace && (short)(c.SP - sp0) != 2)
                            Console.Error.WriteLine($"[NRET-ESC] at {c.CS:X4}:{(ushort)insn.IP:X4} retIp={retIp:X4} spdelta=+{(short)(c.SP - sp0)}");
                        if (Trace) Console.Error.WriteLine($"[INTERP] exit ret SP={c.SP:X4}");
                        return false;
                    }
                    ip = retIp;   // retorno de una llamada anidada interpretada
                    continue;
                }
                case Mnemonic.Retf:
                {
                    int extra = insn.OpCount >= 1 ? insn.Immediate16 : 0;
                    // `retfd` (operando de 32 bits, prefijo 0x66): desapila EIP de CUATRO bytes y
                    // el CS en otros cuatro — ocho en total, no cuatro. Tratarlo como el de 16
                    // sacaba el CS de la MITAD ALTA del offset (casi siempre cero) y la vuelta
                    // acababa en el segmento 0. Es la instrucción con la que el extensor de
                    // un extensor embebido entrega el control a su juego de 32 bits.
                    bool ret32 = insn.Code is Code.Retfd or Code.Retfd_imm16;
                    ushort retIp, retCs;
                    uint retEipCompleto = 0;
                    if (ret32)
                    {
                        retEipCompleto = PilaRd32(c, m, SpDe(c));
                        retIp = (ushort)retEipCompleto;
                        retCs = (ushort)PilaRd32(c, m, SpDe(c) + 4);
                    }
                    else
                    {
                        retIp = PilaRd16(c, m, SpDe(c));
                        retCs = PilaRd16(c, m, SpDe(c) + 2);
                    }
                    c.FarRetCs = retCs; c.FarRetIp = retIp;   // telemetry: destination the unwind may discard
                    c.FarRetSs = c.SS; c.FarRetSp = c.SP;     // pop-site (near-guard redirect filter)
                    SpA(c, SpDe(c) + (uint)((ret32 ? 8 : 4) + extra));
                    if (!WholeProgram && !IsrMode && (short)(c.SP - sp0) > 0)
                    {
                        if (Trace) Console.Error.WriteLine($"[INTERP] exit retf SP={c.SP:X4}");
                        return true;   // el caller hace c.FarRet = true
                    }
                    c.CS = retCs; ip = retIp;   // idiom push cs / call near ... retf
                    // Mundo mixto: un retfd hacia un CS de código de 32 bits lleva EIP entero
                    // (el despachador de IRQ del runtime de DOS/4GW-Pro entra así al handler
                    // de 32 bits del juego) — la cesión de la cabecera del bucle lo consume.
                    if (ret32 && (c.Msw & 1) != 0 && EsCodigo32Pm16(c, m, retCs))
                        _eip32Pendiente = retEipCompleto;
                    continue;
                }
                case Mnemonic.Iret:
                case Mnemonic.Iretd:
                    // IRET con NT=1 en modo protegido: NO hay marco en la pila — es el retorno
                    // de una tarea anidada (entrada por task gate, p.ej. el #PF del VMM de
                    // DOS/4GW Professional) y se conmuta de vuelta por el backlink del TSS.
                    if ((c.Msw & 1) != 0 && c.NT)
                    {
                        uint eipVuelta = IretDeTarea(c, m, next);
                        ip = (ushort)eipVuelta;
                        if (EsCodigo32Pm16(c, m, c.CS))
                            _eip32Pendiente = eipVuelta;   // la cesión del bucle lo consume
                        continue;
                    }
                    // IRET that closes a software INT we vectored in-interp within this Run
                    // (INT 3F / game vector like INT 91h): return IN-INTERP regardless of
                    // WholeProgram, so the caller (e.g. the mouse callback) resumes instead of
                    // the whole Run ending on the handler's first IRET.
                    // En IsrMode, el IRET TERMINAL de la ISR ejecuta con la pila al nivel de
                    // entrada del Run (solo queda el frame FLAGS/CS/IP del despacho). Un IRET
                    // con la pila más honda es un IDIOMA del juego (pushf/push cs/call ... iret
                    // = retorno far que restaura FLAGS — el driver de un juego dentro de su ISR de
                    // timer) y debe ejecutarse fiel en-interp, no terminar el Run a mitad de la
                    // ISR (dejaba las palabras de la ISR sin desapilar: el retf del código
                    // interrumpido saltaba a la IVT — clase IIDM, crash de sonido de un juego de prueba).
                    if (softIntDepth > 0 || WholeProgram || (IsrMode && (c.SS != ss0 || c.SP != sp0)))
                    {
                        if (softIntDepth > 0) softIntDepth--;
                        // programa entero / INT anidado: desapila IP:CS:FLAGS y sigue en el destino
                        ushort iip, ics;
                        if (insn.Mnemonic == Mnemonic.Iretd)
                        {
                            uint eipCompleto = Pop32(c, m);
                            iip = (ushort)eipCompleto;
                            ics = (ushort)Pop32(c, m);
                            c.SetEFlagsWord(Pop32(c, m));
                            // Mundo mixto DOS/16M: si el iretd vuelve a un CS de CÓDIGO DE 32
                            // BITS, el EIP no cabe en `ip` — se guarda entero y la cesión al
                            // intérprete de 32 bits (cabecera del bucle) lo consume.
                            if ((c.Msw & 1) != 0 && EsCodigo32Pm16(c, m, ics))
                                _eip32Pendiente = eipCompleto;
                        }
                        else
                        {
                            iip = Pop16(c, m);
                            ics = Pop16(c, m);
                            c.SetFlagsWord(Pop16(c, m));
                        }
                        c.CS = ics; ip = iip;
                        continue;
                    }
                    // espejo del lifteado: c.Iret(m) (maneja trampolines IVT) + return
                    if (insn.Mnemonic == Mnemonic.Iretd) c.Iretd(m); else c.Iret(m);
                    if (Trace) Console.Error.WriteLine($"[INTERP] exit iret SP={c.SP:X4}");
                    return false;

                case Mnemonic.Int:
                {
                    int n = insn.GetOpKind(0) == OpKind.Immediate8 ? insn.Immediate8 : -1;
                    // Perfil de capas: la INT se apunta a quien la EJECUTA, antes de decidir por
                    // donde va (IDT, vector del juego o plataforma) — lo que clasifica a la
                    // funcion es que haya pedido el servicio, no quien acabe atendiendolo.
                    if (PerfilCapas.Activo && n >= 0) PerfilCapas.Nota(PerfilCapas.TipoInt, (ushort)n);
                    OnInt?.Invoke((byte)n, c);   // unpack probe: aborta si es INT de "programa real"
                    CorteDeterminista.EnInterrupcion(c);

                    // MODO PROTEGIDO DE 16 BITS: los INT software van por la IDT que el programa
                    // cargó con lidt, no por los handlers C#. El extensor (DOS/16M) engancha ahí
                    // INT 21h para TRADUCIR la llamada (copia lo que apunta el puntero de modo
                    // protegido a un buffer bajo antes de bajar a modo real) e INT 31h para su API
                    // de selectores (AX=0006/0007 get/set segment base — la que luego estandarizó
                    // DPMI). Atenderlos aquí con el handler integrado deja esa traducción sin
                    // correr — medido en un juego DOS/16M: el kernel abría un nombre de fichero VACÍO y
                    // moría con "fatal error (1008)", y sus int 31h eran no-ops silenciosos.
                    // SOLO con PE activo y puerta PRESENTE: vectorizar también en modo real
                    // (probado) rompe el arranque en 10.681 instrucciones, porque los stubs de la
                    // IVT (F000) terminan en RETF y el frame de INT de 6 bytes descuadra la pila.
                    if (n >= 0 && (c.Msw & 1) != 0 && c.IdtLimit >= (uint)(n * 8 + 7))
                    {
                        uint pGate = c.IdtBase + (uint)(n * 8);
                        byte gAttr = m.RdLin8(pGate + 5);
                        ushort gSel = m.RdLin16(pGate + 2);
                        if ((gAttr & 0x80) != 0 && gSel != 0)
                        {
                            if (Trace) Console.Error.WriteLine(
                                $"[PM16] int {n:X2} via IDT -> {gSel:X4}:{m.RdLin16(pGate):X4} attr={gAttr:X2}");
                            // El TIPO de la puerta manda el TAMAÑO del marco: una de 386
                            // (0x0E/0x0F) empuja EFLAGS/CS/EIP como DWORDS y el handler termina
                            // en `iretd` (12 bytes); una de 286 (0x06/0x07), words (6 bytes).
                            // Medido en un juego DOS/16M: sus puertas son 0x8E y con marco de 16 bits el
                            // `iretd` del kernel desapilaba 12 bytes de un marco de 6 y volvía
                            // a un CS de basura (0002) — el descarrilamiento de las 10.681.
                            if ((gAttr & 0x08) != 0)
                            {
                                Push32(c, m, c.EFlagsWord());
                                Push32(c, m, c.CS);
                                Push32(c, m, next);
                            }
                            else
                            {
                                Push16(c, m, c.FlagsWord());
                                Push16(c, m, c.CS);
                                Push16(c, m, next);
                            }
                            if ((gAttr & 1) == 0) c.IF = false;  // interrupt gate; trap gate keeps IF
                            c.TF = false;
                            c.NT = false;   // gates clear NT: only a task's terminal iret sees NT=1
                            c.CS = gSel; ip = m.RdLin16(pGate);
                            softIntDepth++;   // its IRET returns in-interp (see Iret case)
                            continue;
                        }
                    }
                    if (n == 0x3F)
                    {
                        // espejo del lowering del Lifter para INT 3F: thunk de 5 bytes
                        // CD 3F <ovno> <off16> → push CS:ret5 + dispatch a la fn del overlay
                        int lin3f = ((c.CS << 4) + ip) & 0xFFFFF;
                        int ovno = m.Raw[(lin3f + 2) & 0xFFFFF];
                        int ovOff = m.Raw[(lin3f + 3) & 0xFFFFF] | (m.Raw[(lin3f + 4) & 0xFFFFF] << 8);
                        if (OverlayStarts != null && callKey != null &&
                            OverlayStarts.TryGetValue(ovno, out int ovStart))
                        {
                            ushort ret5 = (ushort)(ip + 5);
                            Push16(c, m, c.CS);
                            Push16(c, m, ret5);
                            ushort spRet3f = c.SP;
                            if (callKey(c, m, ovStart + ovOff))
                            {
                                if (c.FarRet)
                                {
                                    if ((short)(c.SP - sp0) > 0) { c.NoteFarRetUnwind(); return true; }
                                    c.FarRet = false;
                                    if (c.FarRetIp != m.RdU16(c.SS, spRet3f)) c.NoteFarRetUnwind();
                                }
                                // far frame (pushed CS above): restore caller CS like the far-call cases
                                c.CS = m.RdU16(c.SS, (ushort)(spRet3f + 2));
                                ip = m.RdU16(c.SS, spRet3f);
                                continue;
                            }
                            SpA(c, SpDe(c) + 4);   // no resuelto: deshacer el frame
                        }
                        // Modo intérprete-puro / boot real: el gestor de overlays DEL JUEGO
                        // (VROOMM instala su propio INT 3F) vive en la IVT — vectorizar FIEL como
                        // cualquier INT software: el hardware no sabe de thunks, empuja
                        // FLAGS/CS/IP+2 y el handler real lee los 3 bytes de datos él mismo.
                        var (v3s, v3o) = c.Platform != null && !c.Platform.AtiendeInt(0x3F)
                            ? VectorDelJuego(m, 0x3F) : ((ushort)0, (ushort)0);
                        if (v3s != 0 || v3o != 0)
                        {
                            Push16(c, m, c.FlagsWord());
                            Push16(c, m, c.CS);
                            Push16(c, m, next);
                            c.IF = false;
                            c.CS = v3s; ip = v3o;
                            softIntDepth++;   // its IRET returns in-interp (see Iret case)
                            continue;
                        }
                        throw new NotSupportedException($"interp: int 3F ov={ovno:X2} off={ovOff:X4} sin resolver @ {c.CS:X4}:{ip:X4}");
                    }
                    // Software INT to a GAME-INSTALLED vector with no built-in C# handler:
                    // vector through the IVT faithfully — push FLAGS/CS/next and JUMP in-interp.
                    // Threaded-code runtimes (Turbo Basic p-code via INT ECh; a game's
                    // INT 91h graphics driver) read the RETURN IP off the stack as an inline
                    // operand pointer, so a synthetic nested-dispatch frame (IP=0) corrupts
                    // them; in-interp vectoring is exact and needs no nested Run.
                    if (n >= 0 && c.Platform != null && !c.Platform.AtiendeInt((byte)n))
                    {
                        var (vSeg, vOff) = VectorDelJuego(m, (byte)n);
                        if (vSeg != 0 || vOff != 0)
                        {
                            AnotaIntJuego((byte)n, c, m, ip, next, vSeg, vOff);
                            Push16(c, m, c.FlagsWord());
                            Push16(c, m, c.CS);
                            Push16(c, m, next);
                            c.IF = false;
                            c.CS = vSeg; ip = vOff;
                            softIntDepth++;   // its IRET returns in-interp (see Iret case)
                            continue;
                        }
                    }
                    // Banco de pruebas: el efecto de esta INT se confirma en el paso siguiente,
                    // cuando ya tiene registros de salida (ver CorpusRecorder.MarcaInt). Se marca
                    // AQUI y no al entrar en el `case`: los caminos de arriba (IDT, INT 3F,
                    // vectorizacion a la IVT del juego) no producen efecto —su codigo se
                    // interpreta y sus escrituras se ven una a una—, asi que marcarlos era pagar
                    // la foto del DGROUP para tirarla despues.
                    if (CorpusRecorder.Activo && n >= 0) CorpusRecorder.MarcaInt(n, c.CS, next, c, m);
                    byte ahEntrada = c.AH;
                    c.Platform?.Int((byte)n, c, m);
                    if (GrabadorFotogramas.Activo) GrabadorFotogramas.NotaInt(n, ahEntrada, c);
                    break;
                }
                case Mnemonic.Into:
                    if (c.OF) c.Platform?.Int(0x04, c, m);
                    break;
                // `int 3` (0xCC) e `int 1` NO son no-ops: vectorizan por la IVT como cualquier
                // otra interrupción software. Ignorarlos parecía inofensivo —quien los usa suele
                // ser un depurador— pero es justo al revés: un programa PROTEGIDO instala su
                // propio manejador de INT 3 y lo llama como parte del descifrado, contando con
                // que un depurador presente lo rompería. Medido en dos juegos protegidos: el arranque
                // intercambia los 4 primeros vectores de la IVT por los suyos (26A4:014D, con
                // DS=0), hace `int 3` en 26A4:00EB y descifra a continuación con
                // `sub [bx],ax`. Saltándose el INT 3, el manejador no corre, el descifrado sale
                // mal y el código resultante muere en un byte inválido — que es exactamente el
                // "opcode inválido byte FE" con el que fallaban tres juegos protegidos.
                case Mnemonic.Int3:
                case Mnemonic.Int1:
                {
                    byte vec = insn.Mnemonic == Mnemonic.Int3 ? (byte)3 : (byte)1;
                    OnInt?.Invoke(vec, c);
                    if (c.Platform != null && !c.Platform.AtiendeInt(vec))
                    {
                        var (vSeg, vOff) = VectorDelJuego(m, vec);
                        if (vSeg != 0 || vOff != 0)
                        {
                            Push16(c, m, c.FlagsWord());
                            Push16(c, m, c.CS);
                            Push16(c, m, next);
                            c.IF = false;
                            c.CS = vSeg; ip = vOff;
                            softIntDepth++;   // su IRET vuelve dentro del intérprete (case Iret)
                            continue;
                        }
                    }
                    break;
                }

                case Mnemonic.In:
                {
                    // `in eax, dx` caía en el camino de 8 bits y leía un byte EN SILENCIO. La
                    // plataforma solo expone puertos de 8/16 (ningún dispositivo DOS usa 32), así
                    // que aquí se lanza con la dirección en vez de inventar la mitad alta.
                    if (insn.Op0Register == Register.EAX)
                        throw new NotSupportedException($"interp: in de 32 bits (puerto de 32 no modelado)");
                    bool wide = insn.Op0Register == Register.AX;
                    ushort port = insn.Op1Kind == OpKind.Immediate8 ? insn.Immediate8 : c.DX;
                    if (PerfilCapas.Activo) PerfilCapas.Nota(PerfilCapas.TipoIn, port);
                    if (c.Platform != null)
                    {
                        if (wide) c.AX = c.Platform.PortIn16(port);
                        else c.AL = c.Platform.PortIn8(port);
                        if (CorpusRecorder.Activo) CorpusRecorder.NotaIn(wide ? c.AX : c.AL);
                        if (GrabadorFotogramas.Activo) GrabadorFotogramas.NotaIn(port, wide ? c.AX : c.AL);
                    }
                    break;
                }
                case Mnemonic.Out:
                {
                    if (insn.Op1Register == Register.EAX)
                        throw new NotSupportedException($"interp: out de 32 bits (puerto de 32 no modelado)");
                    bool wide = insn.Op1Register == Register.AX;
                    ushort port = insn.Op0Kind == OpKind.Immediate8 ? insn.Immediate8 : c.DX;
                    if (PerfilCapas.Activo) PerfilCapas.Nota(PerfilCapas.TipoOut, port);
                    if (wide) c.Platform?.PortOut16(port, c.AX);
                    else c.Platform?.PortOut8(port, c.AL);
                    if (GrabadorFotogramas.Activo) GrabadorFotogramas.NotaOut(port, wide ? c.AX : c.AL);
                    break;
                }
                case Mnemonic.Outsb: StringIo(c, m, in insn, 8, isOut: true); break;
                case Mnemonic.Outsw: StringIo(c, m, in insn, 16, isOut: true); break;
                case Mnemonic.Insb:  StringIo(c, m, in insn, 8, isOut: false); break;
                case Mnemonic.Insw:  StringIo(c, m, in insn, 16, isOut: false); break;

                // --- flags / misc ---
                case Mnemonic.Clc: c.CF = false; break;
                case Mnemonic.Stc: c.CF = true; break;
                case Mnemonic.Cmc: c.CF = !c.CF; break;
                case Mnemonic.Cld: c.DF = false; break;
                case Mnemonic.Std: c.DF = true; break;
                case Mnemonic.Cli: c.IF = false; break;
                case Mnemonic.Sti: c.IF = true; break;
                case Mnemonic.Nop: break;

                // `smsw` (store machine status word) = los 16 bits bajos de CR0. Es LEGAL en modo
                // real y no es privilegiada, así que la usa cualquiera que quiera saber si ya está
                // en modo protegido o en V86 sin arriesgarse a una excepción: el extensor embebido
                // de un juego con extensor embebido hace `smsw ax` + `rcr ax,1` + `jb` para mirar el bit PE.
                //
                // Aquí PakuPort SIEMPRE está en modo real desde el punto de vista del código de 16
                // bits (el mundo de 32 vive en Interp32, con su propio intérprete), así que PE=0.
                // Se devuelve 0 pelado: los demás bits de CR0 (ET, EM, MP) sólo los mira código de
                // control de la FPU, que no pasa por aquí.
                // `lar`/`lsl`: derechos y límite de un SELECTOR. Sólo existen en modo protegido,
                // y aquí sólo hay modo protegido cuando un cliente DPMI ha hecho el cambio, así
                // que la tabla de descriptores es la del host DPMI. ZF dice si el selector vale:
                // es el idioma con el que un extensor comprueba los suyos antes de usarlos.
                case Mnemonic.Lar:
                case Mnemonic.Lsl:
                {
                    // El selector es una WORD, pero el operando puede venir en un registro de 32
                    // (`lsl ebx,ebx` con prefijo 0x66): se lee del tamaño que toque y se recorta.
                    ushort selLar = (ushort)(OpSize(in insn, 1) == 32
                        ? Read32(c, m, in insn, 1) : Read16(c, m, in insn, 1));
                    uint limLar = 0, derLar = 0;
                    bool okLar;
                    if ((c.Msw & 1) != 0)
                    {
                        // Modo protegido de 16 bits (DOS/16M): la verdad es la GDT/LDT que el
                        // programa escribió, no la tabla del host DPMI. Medido en un juego DOS/16M: su
                        // runtime valida con `lar` + `test ah,10h` el selector que le devuelve
                        // el kernel tras un AH=48; contestando con la tabla DPMI (vacía aquí)
                        // el selector "no existía" y el juego moría con el fatal 1005.
                        okLar = DescriptorReal(c, m, selLar, out limLar, out derLar);
                    }
                    else
                    {
                        okLar = c.Platform is Platform.DosPlatform larDp && larDp.Dpmi != null
                                && larDp.Dpmi.Descriptores(selLar, out _, out _, out _);
                        if (okLar) ((Platform.DosPlatform)c.Platform!).Dpmi!
                            .Descriptores(selLar, out limLar, out derLar, out _);
                    }
                    c.ZF = okLar;
                    if (okLar)
                    {
                        uint vLar = insn.Mnemonic == Mnemonic.Lar ? derLar : limLar;
                        var dLar = DstOf(c, in insn, 0);
                        if (OpSize(in insn, 0) == 32) Write32(c, m, in dLar, vLar);
                        else Write16(c, m, in dLar, (ushort)vLar);
                    }
                    break;
                }

                // ---- Modo protegido de 16 bits: las tablas son del PROGRAMA ----
                // Un extensor estilo DOS/16M no pide descriptores a nadie: escribe su GDT en su
                // propia memoria, la carga con lgdt, enciende PE con lmsw y sigue ejecutando
                // código de 16 bits. Así que aquí no se inventa un descriptor: se LEE el que él
                // escribió. Ver Cpu.GdtBase y BaseDeSelectorReal.
                case Mnemonic.Lgdt:
                case Mnemonic.Lidt:
                {
                    var dTbl = DstOf(c, in insn, 0);
                    uint linTbl = LinDe(in dTbl);
                    ushort lim = m.RdLin16(linTbl);
                    uint bas = m.RdLin32(linTbl + 2);
                    // Con tamaño de operando de 16 la base son 24 bits (formato 286); con 32, los
                    // 32 completos. Iced ya distingue lgdt de lgdtd por el prefijo.
                    if (OpSize(in insn, 0) != 32) bas &= 0x00FFFFFF;
                    if (insn.Mnemonic == Mnemonic.Lgdt) { c.GdtBase = bas; c.GdtLimit = lim; }
                    else { c.IdtBase = bas; c.IdtLimit = lim; }
                    break;
                }

                case Mnemonic.Sgdt:
                case Mnemonic.Sidt:
                {
                    var dTbl = DstOf(c, in insn, 0);
                    uint linTbl = LinDe(in dTbl);
                    bool esGdt = insn.Mnemonic == Mnemonic.Sgdt;
                    m.WrLin16(linTbl, esGdt ? c.GdtLimit : c.IdtLimit);
                    m.WrLin32(linTbl + 2, esGdt ? c.GdtBase : c.IdtBase);
                    break;
                }

                case Mnemonic.Lmsw:
                    ModoProtegido16(c, m, (c.Msw & 0xFFFFFFF0u) | (Read16(c, m, in insn, 0) & 0xFu));
                    break;

                case Mnemonic.Smsw:
                    Write16(c, m, DstOf(c, in insn, 0), (ushort)c.Msw);
                    break;

                case Mnemonic.Lldt: c.Ldtr = Read16(c, m, in insn, 0); break;
                case Mnemonic.Sldt: Write16(c, m, DstOf(c, in insn, 0), c.Ldtr); break;
                case Mnemonic.Ltr:  c.TaskReg = Read16(c, m, in insn, 0); break;
                case Mnemonic.Str:  Write16(c, m, DstOf(c, in insn, 0), c.TaskReg); break;
                case Mnemonic.Clts: c.Msw &= ~8u; break;

                case Mnemonic.Hlt:
                    if (Trace) Console.Error.WriteLine($"[INTERP] exit hlt");
                    return false;
                case Mnemonic.Bound: break;   // no-op, igual que el Lifter

                // enter/leave se quedan con SP y BP de 16 bits A PROPÓSITO: son marcos de pila de
                // código de 16 bits, ningún compilador los emite con ESP/EBP en un mundo de 32,
                // y migrarlos exigiría decidir a la vez el ancho de BP sin un solo caso que lo
                // mida. Si algún día aparece uno, aquí está la nota.
                case Mnemonic.Enter:
                {
                    int frameSize = insn.Immediate16;
                    int nestLevel = insn.Immediate8_2nd & 0x1F;
                    c.SP -= 2; m.WrU16(c.SS, c.SP, c.BP);
                    ushort fp = c.SP;
                    for (int i = 1; i < nestLevel; i++)
                    {
                        c.BP -= 2; c.SP -= 2;
                        m.WrU16(c.SS, c.SP, m.RdU16(c.SS, c.BP));
                    }
                    if (nestLevel > 0) { c.SP -= 2; m.WrU16(c.SS, c.SP, fp); }
                    c.BP = fp;
                    c.SP -= (ushort)frameSize;
                    break;
                }
                case Mnemonic.Leave:
                    c.SP = c.BP;
                    c.BP = m.RdU16(c.SS, c.SP);
                    c.SP += 2;
                    break;

                case Mnemonic.Pusha:
                {
                    ushort t = c.SP;
                    Push16(c, m, c.AX);
                    Push16(c, m, c.CX);
                    Push16(c, m, c.DX);
                    Push16(c, m, c.BX);
                    Push16(c, m, t);
                    Push16(c, m, c.BP);
                    Push16(c, m, c.SI);
                    Push16(c, m, c.DI);
                    break;
                }
                case Mnemonic.Popa:
                    c.DI = Pop16(c, m);
                    c.SI = Pop16(c, m);
                    c.BP = Pop16(c, m);
                    SpA(c, SpDe(c) + 2);                        // SP apilado: se descarta
                    c.BX = Pop16(c, m);
                    c.DX = Pop16(c, m);
                    c.CX = Pop16(c, m);
                    c.AX = Pop16(c, m);
                    break;

                case Mnemonic.Pushad:
                {
                    // Mismo orden que PUSHA (EAX ECX EDX EBX ESP-original EBP ESI EDI) con
                    // palabras de 4 bytes. El ESP apilado es el de ANTES de la instrucción.
                    uint t = c.ESP;
                    Push32(c, m, c.EAX);
                    Push32(c, m, c.ECX);
                    Push32(c, m, c.EDX);
                    Push32(c, m, c.EBX);
                    Push32(c, m, t);
                    Push32(c, m, c.EBP);
                    Push32(c, m, c.ESI);
                    Push32(c, m, c.EDI);
                    break;
                }
                case Mnemonic.Popad:
                    c.EDI = Pop32(c, m);
                    c.ESI = Pop32(c, m);
                    c.EBP = Pop32(c, m);
                    SpA(c, SpDe(c) + 4);                        // ESP apilado: se descarta
                    c.EBX = Pop32(c, m);
                    c.EDX = Pop32(c, m);
                    c.ECX = Pop32(c, m);
                    c.EAX = Pop32(c, m);
                    break;

                // --- 386 (subset del Lifter) ---
                case Mnemonic.Movzx:
                {
                    int srcSize = insn.GetOpKind(1) == OpKind.Register ? RegSize(insn.Op1Register)
                        : (insn.MemorySize is MemorySize.UInt8 or MemorySize.Int8 ? 8 : 16);
                    ushort v = srcSize == 8 ? Read8(c, m, in insn, 1) : Read16(c, m, in insn, 1);
                    if (RegSize(insn.Op0Register) == 32) SetR32(c, insn.Op0Register, v);
                    else if (RegSize(insn.Op0Register) == 16) SetR16(c, insn.Op0Register, v);
                    else SetR8(c, insn.Op0Register, (byte)v);
                    break;
                }
                case Mnemonic.Movsx:
                {
                    int srcSize = insn.GetOpKind(1) == OpKind.Register ? RegSize(insn.Op1Register)
                        : (insn.MemorySize is MemorySize.UInt8 or MemorySize.Int8 ? 8 : 16);
                    short v = srcSize == 8 ? (sbyte)Read8(c, m, in insn, 1) : (short)Read16(c, m, in insn, 1);
                    if (RegSize(insn.Op0Register) == 32) SetR32(c, insn.Op0Register, (uint)(int)v);
                    else if (RegSize(insn.Op0Register) == 16) SetR16(c, insn.Op0Register, (ushort)v);
                    else SetR8(c, insn.Op0Register, (byte)v);
                    break;
                }
                case Mnemonic.Shld: DoubleShift(c, m, in insn, (d0, s, n) => c.Shld16(d0, s, n), (d0, s, n) => c.Shld8(d0, s, n), (d0, s, n) => c.Shld32(d0, s, n)); break;
                case Mnemonic.Shrd: DoubleShift(c, m, in insn, (d0, s, n) => c.Shrd16(d0, s, n), (d0, s, n) => c.Shrd8(d0, s, n), (d0, s, n) => c.Shrd32(d0, s, n)); break;
                case Mnemonic.Bt: case Mnemonic.Bts: case Mnemonic.Btr: case Mnemonic.Btc:
                    BitTest(c, m, in insn);
                    break;
                case Mnemonic.Bsf:
                {
                    int size = OpSize(in insn, 1);
                    uint src = size == 32 ? Read32(c, m, in insn, 1)
                             : size == 16 ? Read16(c, m, in insn, 1)
                             : Read8(c, m, in insn, 1);
                    c.ZF = src == 0;
                    if (c.ZF) break;
                    if (size == 32) SetR32(c, insn.Op0Register, c.Bsf32(src));
                    else SetR16(c, insn.Op0Register, size == 16 ? c.Bsf16((ushort)src) : c.Bsf8((byte)src));
                    break;
                }
                case Mnemonic.Bsr:
                {
                    int size = OpSize(in insn, 1);
                    uint src = size == 32 ? Read32(c, m, in insn, 1)
                             : size == 16 ? Read16(c, m, in insn, 1)
                             : Read8(c, m, in insn, 1);
                    c.ZF = src == 0;
                    if (c.ZF) break;
                    if (size == 32) SetR32(c, insn.Op0Register, c.Bsr32(src));
                    else SetR16(c, insn.Op0Register, size == 16 ? c.Bsr16((ushort)src) : c.Bsr8((byte)src));
                    break;
                }

                default:
                    // x87 (coma flotante): espejo de Lifter.LiftFpu sobre c.Fpu
                    string mn = insn.Mnemonic.ToString();
                    if (insn.Mnemonic == Mnemonic.Wait || (mn.Length > 1 && mn[0] == 'F' && char.IsLower(mn[1])))
                    {
                        ExecFpu(c, m, in insn);
                        break;
                    }
                    // saltos condicionales (forma común)
                    if (TryCond(c, insn.Mnemonic, out bool taken))
                    {
                        if (taken) { NotaAtras(c, in insn, ip); ip = (ushort)insn.NearBranchTarget; continue; }
                        break;
                    }
                    // SETcc
                    if (TrySetcc(c, insn.Mnemonic, out bool scVal))
                    {
                        var d = DstOf(c, in insn, 0);
                        Write8(c, m, in d, (byte)(scVal ? 1 : 0));
                        break;
                    }
                    throw new NotSupportedException($"interp: opcode no soportado {insn.Mnemonic} @ {c.CS:X4}:{ip:X4} ({insn}) hist={HistDump()}{FatalInfo(c, m, softIntDepth)}");
            }

            if (LogFuera && Memory.EscriturasPerdidas != _perdidasPrevias)
            {
                // Hermana de la de Interp32: Memory sólo sabe la dirección final, y una base mal
                // leída de un descriptor se ve igual que un puntero corrupto. En modo protegido se
                // imprimen además los OCHO BYTES CRUDOS del descriptor, que es donde se distingue
                // un descriptor de estilo 286 (bytes 6-7 con basura) de uno de 386.
                _perdidasPrevias = Memory.EscriturasPerdidas;
                if (_avisosFuera++ < 12)
                    Console.Error.WriteLine(
                        $"[FUERA16] {c.CS:X4}:{ip:X4} ({insn}) gdt=0x{c.GdtBase:X8}+{c.GdtLimit:X4} " +
                        $"DS={c.DS:X4}{DescDump(c, m, c.DS)} ES={c.ES:X4}{DescDump(c, m, c.ES)} " +
                        $"GS={c.GS:X4}{DescDump(c, m, c.GS)} FS={c.FS:X4}{DescDump(c, m, c.FS)} " +
                        $"SS={c.SS:X4}{DescDump(c, m, c.SS)} " +
                        $"AX={c.AX:X4} BX={c.BX:X4} CX={c.CX:X4} DX={c.DX:X4} " +
                        $"SI={c.SI:X4} DI={c.DI:X4} BP={c.BP:X4} EDI={c.EDI:X8} ESI={c.ESI:X8}");
            }

            ip = next;

            // TF: paso a paso. La CPU dispara INT 1 DESPUÉS de la instrucción, con CS:IP ya
            // apuntando a la siguiente, y al entrar en el manejador limpia TF e IF (que el IRET
            // restaura). Apagado cuesta la lectura de un bool. Lo usan las protecciones que
            // descifran desde su propio manejador de INT 1 — ver el case Int3/Int1.
            if (c.TF && c.Platform is Platform.DosPlatform dpTf && !dpTf.HasIntHandler(1))
            {
                var (tfSeg, tfOff) = dpTf.GetGameVector(m, 1);
                if (tfSeg != 0 || tfOff != 0)
                {
                    Push16(c, m, c.FlagsWord());
                    Push16(c, m, c.CS);
                    Push16(c, m, ip);
                    c.TF = false; c.IF = false;
                    c.CS = tfSeg; ip = tfOff;
                    softIntDepth++;   // su IRET vuelve dentro del intérprete (case Iret)
                }
            }
        }
        }
        catch (FalloDePaginaException pf)
        {
            if (espAntesValido) c.ESP = espAntesPf16;   // fault: la instrucción se reinicia desde cero
            ip = (ushort)EntregaFalloDePagina(c, m, pf, ip, out bool porTarea);
            // Por puerta: su iret desapila el marco dentro de este intérprete (case Iret).
            // Por TAREA no hay marco: su iret conmuta de vuelta por el backlink (rama NT).
            if (!porTarea) softIntDepth++;
            continue;         // se reanuda el bucle: la instrucción se repetirá al volver
        }
        }
        }
        catch (DivideByZeroException e)
        {
            // Localización del fallo: sin cs:ip un #DE interpretado es indepurable
            // (la pila C# solo llega hasta Interp.Run). Los registros dicen QUÉ variable
            // divide (DS:operando) — sin ellos no se puede vigilar la dirección real.
            throw new DivideByZeroException(
                $"{e.Message} @interp {c.CS:X4}:{ip:X4} (lin 0x{((c.CS << 4) + ip) & 0xFFFFF:X5})" +
                $" AX={c.AX:X4} BX={c.BX:X4} CX={c.CX:X4} DX={c.DX:X4} SI={c.SI:X4} DI={c.DI:X4}" +
                $" BP={c.BP:X4} DS={c.DS:X4} ES={c.ES:X4} SS={c.SS:X4} SP={c.SP:X4} hist={HistDump()}{FatalInfo(c, m, softIntDepth)}", e);
        }
        catch (NotSupportedException e) when (!e.Message.Contains(" @ "))
        {
            // Los helpers (GetR16/SetR16, DstOf…) lanzan sin coordenadas: añadir CS:IP, los
            // bytes de la instrucción y la caja negra — sin esto el fallo es inlocalizable.
            int lin = ((c.CS << 4) + ip) & 0xFFFFF;
            throw new NotSupportedException(
                $"{e.Message} @ {c.CS:X4}:{ip:X4} bytes={Convert.ToHexString(m.Raw.AsSpan(lin, Math.Min(8, m.Raw.Length - lin)))} hist={HistDump()}{FatalInfo(c, m, softIntDepth)}", e);
        }
    }

    // PAKU_FATAL_MEMDUMP=1: on any fatal interp error, dump the full 1 MiB memory image to
    // logs/fatal-mem.bin and append IF / IsrMode / softIntDepth and the raw IVT[8]/IVT[9]
    // vectors to the fatal message (post-mortem forensics). Zero cost when the flag is off.
    static readonly bool FatalMemDump =
        Environment.GetEnvironmentVariable("PAKU_FATAL_MEMDUMP") == "1";

    static string FatalInfo(Cpu c, Memory m, int softIntDepth)
    {
        if (!FatalMemDump) return "";
        try
        {
            System.IO.Directory.CreateDirectory("logs");
            System.IO.File.WriteAllBytes(System.IO.Path.Combine("logs", "fatal-mem.bin"), m.Raw);
        }
        catch { /* diagnostics must never mask the original fatal error */ }
        uint v8 = (uint)(m.Raw[0x20] | (m.Raw[0x21] << 8) | (m.Raw[0x22] << 16) | (m.Raw[0x23] << 24));
        uint v9 = (uint)(m.Raw[0x24] | (m.Raw[0x25] << 8) | (m.Raw[0x26] << 16) | (m.Raw[0x27] << 24));
        return $" IF={(c.IF ? 1 : 0)} IsrMode={(IsrMode ? 1 : 0)} softIntDepth={softIntDepth}" +
               $" IVT8={v8 >> 16:X4}:{v8 & 0xFFFF:X4} IVT9={v9 >> 16:X4}:{v9 & 0xFFFF:X4}";
    }

    /// <summary>`PAKU_LOG_INTJUEGO=&lt;n&gt;[,&lt;n&gt;...]` (numeros en HEX) — QUIEN LLAMA AL DRIVER
    /// DEL JUEGO, con que registros y desde donde.
    ///
    /// <para><b>Para que existe.</b> En DOS, un juego habla con su driver por una interrupcion
    /// propia: un juego de prueba tiene su driver grafico en <c>INT 91h</c> con el numero de funcion
    /// en AX, otro tiene el p-code de Turbo Basic en <c>INT ECh</c>. Cuando ese driver
    /// rechaza una llamada, el mensaje dice el numero malo y la direccion de retorno, y con eso
    /// hay que encontrar al llamante — que <b>no se puede buscar en el volcado de memoria</b>:
    /// medido en un juego de prueba, en el MiB entero hay cuatro <c>cd 91</c> y ninguno cae donde tendria que
    /// caer, porque la llamada la hace un envoltorio de libreria o un puntero calculado.</para>
    ///
    /// <para>Anota una linea por vectorizacion a <c>logs/intjuego.log</c>: el tick, el numero, de
    /// donde viene (CS:IP de la instruccion y direccion de retorno, que es la que imprimen los
    /// drivers), a donde va, los registros, <b>el tope de la pila</b> y <b>el camino recorrido</b>.
    /// Esas dos cosas no son un extra: un driver DOS recibe sus parametros por la pila (el
    /// envoltorio de C hace <c>mov ax,[bp+6]</c>), y el camino es lo que resolvio el caso del INT 91h,
    /// porque enseno que el programa no venia de la instruccion anterior sino atravesando los ceros
    /// del principio de otro segmento. Apagado no cuesta nada: una comparacion contra un array de
    /// 256 bools que se llena una vez.</para></summary>
    static readonly bool[] _logIntJuego = LeeIntsALoguear();

    static bool[] LeeIntsALoguear()
    {
        var v = new bool[256];
        string? s = Environment.GetEnvironmentVariable("PAKU_LOG_INTJUEGO");
        if (string.IsNullOrEmpty(s)) return v;
        foreach (var t in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(t.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int n) && n is >= 0 and < 256)
                v[n] = true;
        return v;
    }

    static StreamWriter? _wIntJuego;

    static void AnotaIntJuego(byte n, Cpu c, Memory m, ushort ip, ushort ret, ushort vSeg, ushort vOff)
    {
        if (!_logIntJuego[n]) return;
        if (_wIntJuego == null)
        {
            Directory.CreateDirectory("logs");
            _wIntJuego = new StreamWriter(Path.Combine("logs", "intjuego.log")) { AutoFlush = false };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _wIntJuego.Flush(); } catch { } };
        }
        // La pila va con la linea, y no es un extra: un driver DOS recibe sus parametros por ahi
        // (el envoltorio de C hace `mov ax,[bp+6]`), asi que sin ella se ve QUE numero llego pero
        // no DE DONDE — ni la direccion de retorno del far call que trajo el control aqui.
        var pila = new System.Text.StringBuilder(" pila=");
        for (int k = 0; k < 8; k++) pila.Append($"{m.RdU16(c.SS, (ushort)(c.SP + k * 2)):X4} ");
        _wIntJuego.WriteLine($"{Cpu.TickCount} int={n:X2} desde={c.CS:X4}:{ip:X4} ret={c.CS:X4}:{ret:X4} " +
                             $"-> {vSeg:X4}:{vOff:X4} ax={c.AX:X4} bx={c.BX:X4} cx={c.CX:X4} dx={c.DX:X4} " +
                             $"si={c.SI:X4} di={c.DI:X4} bp={c.BP:X4} sp={c.SP:X4} ss={c.SS:X4} ds={c.DS:X4} es={c.ES:X4}" +
                             pila.ToString().TrimEnd() + " hist=" + HistDump());
    }

    // Caja negra: últimas direcciones interpretadas (cs:ip), volcadas en cualquier fallo —
    // el rastro de CÓMO se llegó a la instrucción mala (salto calculado, ret corrupto...).
    /// <summary>El vector de la IVT SI apunta a código que instaló el JUEGO: ni el stub del BIOS
    /// (F000) ni el trampolín del explorador (9F00). Lee la IVT de la memoria, que es donde vive,
    /// y por eso no necesita plataforma: vale igual con el runtime real y con el replay del
    /// corpus. Espejo exacto de <c>DosPlatform.GetGameVector</c>, que sigue existiendo para el
    /// lifteado.</summary>
    internal static (ushort seg, ushort off) VectorDelJuego(Memory m, byte n)
    {
        int b = n * 4;
        // EL VECTOR ES CONTEXTO DEL CASO. Se lee del array crudo, asi que la nota de lectura de
        // fuera del DGROUP no lo veia y los 4 bytes NO viajaban con el caso: al reproducirlo, la
        // IVT estaba a cero, esta funcion contestaba "no hay handler del juego" y el `int` caia en
        // la plataforma de replay, que no tenia efecto grabado — porque en la grabacion aquel INT
        // habia vectorizado al juego, justo lo que aqui se decide. Medido en un juego de prueba:
        // `INT 10h @ 134C:C2A9: la cola grabada esta AGOTADA (0/0)` en cinco funciones.
        if (CorpusRecorder.Activo)
            for (int k = 0; k < 4; k++) CorpusRecorder.NotaLectura(b + k, m.Raw[b + k]);
        ushort off = (ushort)(m.Raw[b] | (m.Raw[b + 1] << 8));
        ushort seg = (ushort)(m.Raw[b + 2] | (m.Raw[b + 3] << 8));
        bool delJuego = (seg != 0 || off != 0) && seg != Cpu.IvtStubSeg && seg != 0x9F00;
        return delJuego ? (seg, off) : ((ushort)0, (ushort)0);
    }

    static readonly uint[] _hist = new uint[64];
    static int _histIdx;
    /// <summary>Entradas válidas del anillo. Ver HistDump: 0000:0000 es un valor legítimo.</summary>
    static long _histLlenas;

    /// <summary>CS:IP (packed high:low) of the instruction the interpreter is currently
    /// executing — diagnostics only (PAKU_MEMWATCH prints the writer's PC).</summary>
    public static uint LastPc => _hist[(_histIdx - 1) & (_hist.Length - 1)];

    static string HistDump()
    {
        var sb = new System.Text.StringBuilder();
        int shown = 0;
        int cap = FatalMemDump ? _hist.Length : 24; // full ring under PAKU_FATAL_MEMDUMP
        // Cuántas entradas tiene el anillo se lleva CONTADO, no se deduce de que la casilla
        // valga 0: la entrada se empaqueta como (CS<<16)|ip, así que 0000:0000 vale exactamente
        // 0. Cortar ahí dejaba la caja negra VACÍA justo en el caso que más importa —
        // "[DESCARRILA] ejecutando datos @ 0000:0000", donde lo único que se busca es quién
        // saltó ahí. Medido en un simulador de vuelo: `hist=` salía en blanco.
        int haber = (int)Math.Min(_histLlenas, _hist.Length);
        for (int i = 1; i <= haber && shown < cap; i++)
        {
            uint v = _hist[(_histIdx - i) & (_hist.Length - 1)];
            if (sb.Length > 0) sb.Append('<');
            sb.Append($"{v >> 16:X4}:{v & 0xFFFF:X4}");
            shown++;
        }
        return sb.ToString();
    }

    /// <summary>(cs, off) runtime → file key. La MISMA fórmula que el FarDispatch generado:
    /// drivers EXEC por registro, resto por coordenadas del residente.</summary>
    public static int KeyOf(Cpu c, ushort seg, ushort off)
    {
        if (Platform.DriverRegistry.TryGetDriverBase(seg, out int kb)) return kb + off;
        return seg * 16 + off - c.LoadBase - c.ImageBias + OverlayDirectory.ResidentBase;
    }

    /// <summary>True if seg:off begins with `int n` vectored to game-installed code with no
    /// C# handler (a threaded-runtime dispatch entry). Such a target must be interpreted, not
    /// handed to lifted code via farDispatch (which seeds an IP=0 nested frame and corrupts the
    /// runtime that reads the INT return IP as an inline operand pointer).</summary>
    static bool IsThreadedIntEntry(Cpu c, Memory m, ushort seg, ushort off)
    {
        int lin = ((seg << 4) + off) & 0xFFFFF;
        if (m.Raw[lin] != 0xCD) return false;
        if (c.Platform is not Platform.DosPlatform dp) return false;
        byte n = m.Raw[(lin + 1) & 0xFFFFF];
        if (dp.HasIntHandler(n)) return false;
        var (vSeg, vOff) = dp.GetGameVector(m, n);
        return vSeg != 0 || vOff != 0;
    }

    // ================= operandos (espejo de Lifter.Src/Dst/MemSegEa) =================

    readonly struct Dst
    {
        public readonly bool IsMem;
        public readonly Register Reg;
        public readonly ushort Seg;
        /// <summary>Offset dentro del segmento. Es de 32 bits porque con direccionamiento de 32
        /// (`[esp+1]`, `[edi+380h]`) puede pasar de 64 KB, y truncarlo devolvía datos del
        /// principio del segmento EN SILENCIO. En modo real sigue entrando recortado a 16, que es
        /// el wrap fiel del 8086.</summary>
        public readonly uint Ea;
        public Dst(Register r) { IsMem = false; Reg = r; Seg = 0; Ea = 0; }
        public Dst(ushort seg, uint ea) { IsMem = true; Reg = Register.None; Seg = seg; Ea = ea; }
    }

    // Acceso a un operando de memoria honrando el modo: en modo protegido la dirección se resuelve
    // linealmente (base del selector + offset de 32 bits); en modo real, por el camino de siempre.
    static uint LinDe(in Dst d) => Memory.LinDeSegmento(d.Seg, 0) + d.Ea;
    static byte RdMem8(Memory m, in Dst d)
        => Memory.SegmentosSonSelectores ? m.RdLin8(LinDe(in d)) : m.RdU8(d.Seg, (ushort)d.Ea);
    static ushort RdMem16(Memory m, in Dst d)
        => Memory.SegmentosSonSelectores ? m.RdLin16(LinDe(in d)) : m.RdU16(d.Seg, (ushort)d.Ea);
    static uint RdMem32(Memory m, in Dst d)
        => Memory.SegmentosSonSelectores ? m.RdLin32(LinDe(in d)) : m.RdU32(d.Seg, (ushort)d.Ea);
    static void WrMem8(Memory m, in Dst d, byte v)
    { if (Memory.SegmentosSonSelectores) m.WrLin8(LinDe(in d), v); else m.WrU8(d.Seg, (ushort)d.Ea, v); }
    static void WrMem16(Memory m, in Dst d, ushort v)
    { if (Memory.SegmentosSonSelectores) m.WrLin16(LinDe(in d), v); else m.WrU16(d.Seg, (ushort)d.Ea, v); }
    static void WrMem32(Memory m, in Dst d, uint v)
    { if (Memory.SegmentosSonSelectores) m.WrLin32(LinDe(in d), v); else m.WrU32(d.Seg, (ushort)d.Ea, v); }

    static Dst DstOf(Cpu c, in Instruction insn, int i)
    {
        var k = insn.GetOpKind(i);
        if (k == OpKind.Register) return new Dst(insn.GetOpRegister(i));
        // Offset COMPLETO: con direccionamiento de 32 bits no se trunca (ver Dst.Ea).
        if (k == OpKind.Memory)
            return new Dst(MemSeg(c, in insn),
                           Addr32(in insn) ? MemEa32(c, in insn) : MemEa(c, in insn));
        throw new NotSupportedException($"interp: destino no soportado {k} ({insn.Mnemonic})");
    }

    static uint Read32D(Cpu c, Memory m, in Dst d) => d.IsMem ? RdMem32(m, in d) : GetR32(c, d.Reg);
    static ushort Read16D(Cpu c, Memory m, in Dst d) => d.IsMem ? RdMem16(m, in d) : GetR16(c, d.Reg);
    static byte Read8D(Cpu c, Memory m, in Dst d) => d.IsMem ? RdMem8(m, in d) : GetR8(c, d.Reg);
    static void Write32(Cpu c, Memory m, in Dst d, uint v) { if (d.IsMem) WrMem32(m, in d, v); else SetR32(c, d.Reg, v); }
    static void Write16(Cpu c, Memory m, in Dst d, ushort v) { if (d.IsMem) WrMem16(m, in d, v); else SetR16(c, d.Reg, v); }
    static void Write8(Cpu c, Memory m, in Dst d, byte v) { if (d.IsMem) WrMem8(m, in d, v); else SetR8(c, d.Reg, v); }

    static uint Read32(Cpu c, Memory m, in Instruction insn, int i)
    {
        var k = insn.GetOpKind(i);
        return k switch
        {
            OpKind.Register => GetR32(c, insn.GetOpRegister(i)),
            OpKind.Immediate8 => insn.Immediate8,
            OpKind.Immediate32 => insn.Immediate32,
            OpKind.Immediate8to32 => (uint)insn.Immediate8to32,
            OpKind.Immediate8to16 => (uint)insn.Immediate8to16,
            OpKind.Immediate16 => insn.Immediate16,
            // Con direccionamiento de 32 bits el offset NO se trunca (misma regla que DstOf):
            // el kernel de DOS/16M lee su lista de memoria extendida con `mov ax,es:[ebx+esi]`
            // y EA=0x100000 — truncado a 16 leía ceros del principio del segmento EN SILENCIO
            // y el DPMI 0501 devolvía "sin memoria" (el fatal 1010 de un juego DOS/16M).
            OpKind.Memory => RdMem32(m, new Dst(MemSeg(c, in insn),
                Addr32(in insn) ? MemEa32(c, in insn) : MemEa(c, in insn))),
            _ => throw new NotSupportedException($"interp: fuente32 no soportada {k} ({insn.Mnemonic})"),
        };
    }

    static ushort Read16(Cpu c, Memory m, in Instruction insn, int i)
    {
        var k = insn.GetOpKind(i);
        return k switch
        {
            OpKind.Register => GetR16(c, insn.GetOpRegister(i)),
            OpKind.Immediate8 => insn.Immediate8,
            OpKind.Immediate16 => insn.Immediate16,
            OpKind.Immediate8to16 => (ushort)insn.Immediate8to16,
            OpKind.Memory => RdMem16(m, new Dst(MemSeg(c, in insn),
                Addr32(in insn) ? MemEa32(c, in insn) : MemEa(c, in insn))),   // ver Read32
            _ => throw new NotSupportedException($"interp: fuente no soportada {k} ({insn.Mnemonic})"),
        };
    }

    static byte Read8(Cpu c, Memory m, in Instruction insn, int i)
    {
        var k = insn.GetOpKind(i);
        return k switch
        {
            OpKind.Register => GetR8(c, insn.GetOpRegister(i)),
            OpKind.Immediate8 => insn.Immediate8,
            OpKind.Immediate8to16 => (byte)insn.Immediate8to16,
            OpKind.Memory => RdMem8(m, new Dst(MemSeg(c, in insn),
                Addr32(in insn) ? MemEa32(c, in insn) : MemEa(c, in insn))),   // ver Read32
            _ => throw new NotSupportedException($"interp: fuente no soportada {k} ({insn.Mnemonic})"),
        };
    }

    /// <summary>Segmento efectivo del operando de memoria: prefijo si lo hay; si no,
    /// SS para [BP]/[SP], DS para el resto — espejo exacto de Lifter.MemSegEa.
    /// EBP/ESP también van por SS (mismo criterio que Interp32.MemSeg): el kernel de DOS/16M
    /// direcciona su marco con `[ebp+...]` en código de 16 bits (prefijo 0x67) llevando
    /// DS=0020 y SS=00A8 distintos — con DS leía basura y el asignador de selectores pedía
    /// 0x092C selectores en vez de 1, que es el fallo que abortaba un juego DOS/16M con R6009.</summary>
    static ushort MemSeg(Cpu c, in Instruction insn)
    {
        if (insn.SegmentPrefix != Register.None) return GetR16(c, insn.SegmentPrefix);
        if (insn.MemoryBase is Register.BP or Register.SP or Register.EBP or Register.ESP) return c.SS;
        return c.DS;
    }

    /// <summary>Tamaño de DIRECCIÓN del operando de memoria: 32 bits si el modo r/m usa registros
    /// de 32 bits o un desplazamiento de 4 bytes (prefijo 0x67), 16 si no. En modo real el
    /// direccionamiento de 16 bits es lo normal y este predicado sale false, que es el camino de
    /// siempre.</summary>
    static bool Addr32(in Instruction insn)
    {
        if (insn.MemoryBase != Register.None) return RegSize(insn.MemoryBase) == 32;
        if (insn.MemoryIndex != Register.None) return RegSize(insn.MemoryIndex) == 32;
        return insn.MemoryDisplSize == 4;
    }

    /// <summary>Dirección efectiva SIN truncar (base + índice*escala + desplazamiento). Solo la
    /// necesita LEA con direccionamiento de 32 bits, que no toca memoria y por tanto entrega el
    /// valor entero.</summary>
    static uint MemEa32(Cpu c, in Instruction insn)
    {
        uint ea = 0;
        var b = insn.MemoryBase;
        if (b != Register.None) ea += RegSize(b) == 32 ? GetR32(c, b) : GetR16(c, b);
        var ix = insn.MemoryIndex;
        if (ix != Register.None)
            ea += (RegSize(ix) == 32 ? GetR32(c, ix) : GetR16(c, ix)) * (uint)insn.MemoryIndexScale;
        ea += (uint)insn.MemoryDisplacement64;
        return Addr32(in insn) ? ea : (ushort)ea;
    }

    /// <summary>Dirección efectiva como offset dentro del segmento. Se trunca a 16 bits porque en
    /// modo real el límite del segmento es 64 KB (un 386 en modo real que calcule un offset mayor
    /// lanza #GP; ningún juego lo hace). El mundo plano de 32 bits de la fase 1 usará MemEa32.
    /// La escala del índice se aplica siempre: con direccionamiento de 16 bits vale 1, así que el
    /// camino de los juegos probados no cambia.</summary>
    static ushort MemEa(Cpu c, in Instruction insn) => (ushort)MemEa32(c, in insn);

    /// <summary>Ancho en bits de un operando de memoria. Los valores de 32 bits (UInt32/Int32)
    /// caían antes en el `else` y se ejecutaban como si fueran de 8 — de ahí que una instrucción
    /// de 32 bits sobre memoria no fallase, sino que hiciera algo distinto en silencio.</summary>
    static int MemSizeBits(MemorySize ms) => ms switch
    {
        MemorySize.UInt16 or MemorySize.Int16 => 16,
        MemorySize.UInt32 or MemorySize.Int32 => 32,
        _ => 8,
    };

    static int OpSize(in Instruction insn, int i)
    {
        var k = insn.GetOpKind(i);
        if (k == OpKind.Register) return RegSize(insn.GetOpRegister(i));
        if (k == OpKind.Memory) return MemSizeBits(insn.MemorySize);
        int other = i == 0 ? 1 : 0;
        if (other < insn.OpCount)
        {
            var ko = insn.GetOpKind(other);
            if (ko == OpKind.Register) return RegSize(insn.GetOpRegister(other));
            if (ko == OpKind.Memory) return MemSizeBits(insn.MemorySize);
        }
        // Sin registro ni memoria de referencia (p.ej. `push imm32`): decidir por el inmediato.
        return k is OpKind.Immediate32 or OpKind.Immediate8to32 ? 32 : 16;
    }

    /// <summary>Ancho de la palabra que push/pop mueven en la pila. No vale <see cref="OpSize"/>:
    /// con un solo operando no hay "el otro" del que deducirlo, y un `push imm32` (66 68 …) hay
    /// que distinguirlo del `push imm16`. Un `push cs` sigue siendo de 16 bits, como hasta ahora.</summary>
    // ---- pila con el ancho del mundo ----
    //
    // En modo REAL el 8086 mueve sólo SP y el offset da la vuelta a los 64 KB: eso es lo que hace
    // todos los juegos probados y no cambia ni un byte (`Memory.SegmentosSonSelectores` está a false, y el
    // recorte a ushort conserva el wrap exacto de siempre).
    //
    // En modo PROTEGIDO el segmento de pila puede ser de 32 bits y el offset NO cabe en 16.
    // un extensor embebido entrega el control a su juego con ESP=0x61D96: truncado a 0x1D96, los dos `push`
    // del marco de salto caían DENTRO de la imagen del juego y le corrompían 8 bytes — que su
    // propia comprobación de integridad detectaba después como "Program File Corrupted", a miles
    // de instrucciones de la causa. Mismo interruptor que ya decide la resolución de direcciones
    // en `Dst.Ea`.
    // EL ANCHO DE LA PILA LO MANDA EL BIT B DEL DESCRIPTOR DE SS, no el modo. Con B=0 el
    // hardware empuja por SP y deja la mitad alta de ESP COMO ESTÉ — y los kernels cuentan con
    // ello. Medido en un juego DOS/4GW Pro: el despachador DPMI de DOS/4GW Pro hace `lss sp,[si+7Bh]` hacia
    // su pila de 16 bits (base 0x13FF80) con ESP arrastrando 0x100F#### del mundo plano del
    // juego; empujando por ESP el marco caía en la lineal 0x10231F7A — arena virtual sin mapear
    // — y el VMM se quedaba reintentando ese #PF imposible para siempre. El bit se cachea por
    // recarga de SS (Cpu.SsRecargas): leer la GDT en cada push costaría un ojo en el camino
    // caliente. En modo real no cambia nada: sigue mandando SP, el camino de todos los juegos probados.
    static bool _pilaB32;
    static uint _pilaB32Marca = uint.MaxValue;
    static bool PilaEs32(Cpu c)
    {
        if (!Memory.SegmentosSonSelectores) return false;
        if (c.SsRecargas != _pilaB32Marca)
        {
            var m = Memory.Last;
            if (m != null && DescriptorReal(c, m, c.SS, out _, out uint der))
                _pilaB32 = (der & 0x00400000) != 0;   // el bit B del descriptor real
            else if (c.GdtLimit != 0)
                // El invitado TIENE GDT y SS no está en ella: es un selector de modo real
                // arrastrado por una transición (el kernel de DOS/4GW conmuta con SS=0824 aún
                // cargado). Su caché oculta viene de modo real: B=0, se empuja por SP. Medido:
                // con ESP entero el push de 0018:2981 caía en 0x100FA426 (base 0x8240 + un ESP
                // con 0x100F#### arrastrado) en vez de en 0xA402.
                _pilaB32 = false;
            else
                // Mundos donde PakuPort es el host DPMI y no hay GDT en la memoria del
                // invitado: comportamiento de siempre (ESP entero), el que validan los juegos probados.
                _pilaB32 = true;
            _pilaB32Marca = c.SsRecargas;
        }
        return _pilaB32;
    }
    static uint SpDe(Cpu c) => PilaEs32(c) ? c.ESP : c.SP;
    static void SpA(Cpu c, uint v) { if (PilaEs32(c)) c.ESP = v; else c.SP = (ushort)v; }

    static ushort PilaRd16(Cpu c, Memory m, uint off) => Memory.SegmentosSonSelectores
        ? m.RdLin16(Memory.LinDeSegmento(c.SS, 0) + off) : m.RdU16(c.SS, (ushort)off);
    static uint PilaRd32(Cpu c, Memory m, uint off) => Memory.SegmentosSonSelectores
        ? m.RdLin32(Memory.LinDeSegmento(c.SS, 0) + off) : m.RdU32(c.SS, (ushort)off);
    static void PilaWr16(Cpu c, Memory m, uint off, ushort v)
    {
        if (Memory.SegmentosSonSelectores) m.WrLin16(Memory.LinDeSegmento(c.SS, 0) + off, v);
        else m.WrU16(c.SS, (ushort)off, v);
    }
    static void PilaWr32(Cpu c, Memory m, uint off, uint v)
    {
        if (Memory.SegmentosSonSelectores) m.WrLin32(Memory.LinDeSegmento(c.SS, 0) + off, v);
        else m.WrU32(c.SS, (ushort)off, v);
    }

    // The WRITE goes first and SP commits after: a push whose write page-faults must leave SP
    // intact so the instruction restarts cleanly (fault semantics of the 386). Measured in
    // Measured on a DOS/4GW Pro game: its VMM restores the interrupted frame with pushes to a stack
    // still swapped out, and with SP already decremented each retry leaked 4 bytes — the fault
    // address descended 4 by 4 forever (…A37A, A376, A372…) instead of repeating and finishing.
    static void Push16(Cpu c, Memory m, ushort v) { PilaWr16(c, m, SpDe(c) - 2, v); SpA(c, SpDe(c) - 2); }
    static void Push32(Cpu c, Memory m, uint v) { PilaWr32(c, m, SpDe(c) - 4, v); SpA(c, SpDe(c) - 4); }
    static ushort Pop16(Cpu c, Memory m) { ushort v = PilaRd16(c, m, SpDe(c)); SpA(c, SpDe(c) + 2); return v; }
    static uint Pop32(Cpu c, Memory m) { uint v = PilaRd32(c, m, SpDe(c)); SpA(c, SpDe(c) + 4); return v; }

    /// <summary>True para `push`/`pop` de un REGISTRO DE SEGMENTO con tamaño de operando de 32
    /// bits (prefijo 0x66 en código de 16). El 386 mueve CUATRO bytes de pila aunque el registro
    /// tenga dos: al apilar rellena la mitad alta con ceros y al desapilar lee sólo la palabra
    /// baja y suma 4 — dosbox-x <c>core_normal/prefix_66.h</c> (<c>Push_32(SegValue(cs))</c>) y
    /// <c>cpu.cpp CPU_PopSeg(seg,true)</c>.
    ///
    /// No basta con mirar el ancho del registro (que es 16 y por eso caía en el camino de 16
    /// bits): el ancho lo manda el prefijo, e Iced ya lo distingue en el <c>Code</c>. Tratarlo
    /// como el de 16 descuadra la pila en 2 bytes por instrucción, y el daño puede aparecer
    /// mucho después: si el retorno lo hace un `retfd` —que sí desapila 8—, la dirección de
    /// retorno del llamador se pierde y el `ret` de más arriba salta a lo que hubiera.</summary>
    static bool SegStack32(in Instruction insn) => insn.Code is
        Code.Pushd_ES or Code.Pushd_CS or Code.Pushd_SS or Code.Pushd_DS or
        Code.Pushd_FS or Code.Pushd_GS or
        Code.Popd_ES or Code.Popd_SS or Code.Popd_DS or Code.Popd_FS or Code.Popd_GS;

    static int StackOpSize(in Instruction insn) => insn.Op0Kind switch
    {
        OpKind.Register => RegSize(insn.Op0Register),
        OpKind.Memory => MemSizeBits(insn.MemorySize),
        OpKind.Immediate32 or OpKind.Immediate8to32 => 32,
        _ => 16,
    };

    static int RegSize(Register r) => r switch
    {
        Register.AL or Register.BL or Register.CL or Register.DL or
        Register.AH or Register.BH or Register.CH or Register.DH => 8,
        Register.EAX or Register.EBX or Register.ECX or Register.EDX or
        Register.ESP or Register.EBP or Register.ESI or Register.EDI => 32,
        _ => 16,
    };

    static uint GetR32(Cpu c, Register r) => r switch
    {
        Register.EAX => c.EAX, Register.EBX => c.EBX, Register.ECX => c.ECX, Register.EDX => c.EDX,
        Register.ESP => c.ESP, Register.EBP => c.EBP, Register.ESI => c.ESI, Register.EDI => c.EDI,
        // Un registro de 16 bits leído como fuente de 32 (formas mixtas raras): extensión con cero
        _ => GetR16(c, r),
    };

    static void SetR32(Cpu c, Register r, uint v)
    {
        switch (r)
        {
            case Register.EAX: c.EAX = v; break;
            case Register.EBX: c.EBX = v; break;
            case Register.ECX: c.ECX = v; break;
            case Register.EDX: c.EDX = v; break;
            case Register.ESP: c.ESP = v; break;
            case Register.EBP: c.EBP = v; break;
            case Register.ESI: c.ESI = v; break;
            case Register.EDI: c.EDI = v; break;
            default: throw new NotSupportedException($"interp: registro32 no mapeado {r}");
        }
    }

    static ushort GetR16(Cpu c, Register r) => r switch
    {
        Register.AX => c.AX, Register.BX => c.BX, Register.CX => c.CX, Register.DX => c.DX,
        Register.SP => c.SP, Register.BP => c.BP, Register.SI => c.SI, Register.DI => c.DI,
        Register.ES => c.ES, Register.CS => c.CS, Register.SS => c.SS, Register.DS => c.DS,
        Register.FS => c.FS, Register.GS => c.GS,
        _ => throw new NotSupportedException($"interp: registro no mapeado {r}"),
    };

    static void SetR16(Cpu c, Register r, ushort v)
    {
        switch (r)
        {
            case Register.AX: c.AX = v; break;
            case Register.BX: c.BX = v; break;
            case Register.CX: c.CX = v; break;
            case Register.DX: c.DX = v; break;
            case Register.SP: c.SP = v; break;
            case Register.BP: c.BP = v; break;
            case Register.SI: c.SI = v; break;
            case Register.DI: c.DI = v; break;
            case Register.ES: c.ES = v; break;
            case Register.CS: c.CS = v; break;
            case Register.SS: c.SS = v; break;
            case Register.DS: c.DS = v; break;
            case Register.FS: c.FS = v; break;
            case Register.GS: c.GS = v; break;
            default: throw new NotSupportedException($"interp: registro no mapeado {r}");
        }
    }

    static byte GetR8(Cpu c, Register r) => r switch
    {
        Register.AL => c.AL, Register.BL => c.BL, Register.CL => c.CL, Register.DL => c.DL,
        Register.AH => c.AH, Register.BH => c.BH, Register.CH => c.CH, Register.DH => c.DH,
        _ => throw new NotSupportedException($"interp: registro8 no mapeado {r}"),
    };

    static void SetR8(Cpu c, Register r, byte v)
    {
        switch (r)
        {
            case Register.AL: c.AL = v; break;
            case Register.BL: c.BL = v; break;
            case Register.CL: c.CL = v; break;
            case Register.DL: c.DL = v; break;
            case Register.AH: c.AH = v; break;
            case Register.BH: c.BH = v; break;
            case Register.CH: c.CH = v; break;
            case Register.DH: c.DH = v; break;
            default: throw new NotSupportedException($"interp: registro8 no mapeado {r}");
        }
    }

    // ================= familias (espejo de LiftBin/LiftUnary/LiftShift/...) =================

    static void Bin(Cpu c, Memory m, in Instruction insn,
                    Func<ushort, ushort, ushort> op16, Func<byte, byte, byte> op8,
                    Func<uint, uint, uint> op32)
    {
        int size = OpSize(in insn, 0);
        var d = DstOf(c, in insn, 0);
        if (size == 32) Write32(c, m, in d, op32(Read32D(c, m, in d), Read32(c, m, in insn, 1)));
        else if (size == 16) Write16(c, m, in d, op16(Read16D(c, m, in d), Read16(c, m, in insn, 1)));
        else Write8(c, m, in d, op8(Read8D(c, m, in d), Read8(c, m, in insn, 1)));
    }

    static void Unary(Cpu c, Memory m, in Instruction insn,
                      Func<ushort, ushort> op16, Func<byte, byte> op8, Func<uint, uint> op32)
    {
        int size = OpSize(in insn, 0);
        var d = DstOf(c, in insn, 0);
        if (size == 32) Write32(c, m, in d, op32(Read32D(c, m, in d)));
        else if (size == 16) Write16(c, m, in d, op16(Read16D(c, m, in d)));
        else Write8(c, m, in d, op8(Read8D(c, m, in d)));
    }

    static void Shift(Cpu c, Memory m, in Instruction insn,
                      Func<ushort, int, ushort> op16, Func<byte, int, byte> op8,
                      Func<uint, int, uint> op32)
    {
        int cnt = insn.GetOpKind(1) switch
        {
            OpKind.Immediate8 => insn.Immediate8,
            OpKind.Register when insn.Op1Register == Register.CL => c.CL,
            _ => throw new NotSupportedException($"interp: shift cuenta no imm8/CL ({insn.Mnemonic})"),
        };
        int size = OpSize(in insn, 0);
        var d = DstOf(c, in insn, 0);
        if (size == 32) Write32(c, m, in d, op32(Read32D(c, m, in d), cnt));
        else if (size == 16) Write16(c, m, in d, op16(Read16D(c, m, in d), cnt));
        else Write8(c, m, in d, op8(Read8D(c, m, in d), cnt));
    }

    static void DoubleShift(Cpu c, Memory m, in Instruction insn,
                            Func<ushort, ushort, int, ushort> op16, Func<byte, byte, int, byte> op8,
                            Func<uint, uint, int, uint> op32)
    {
        int cnt = insn.GetOpKind(2) switch
        {
            OpKind.Immediate8 => insn.Immediate8,
            OpKind.Register when insn.GetOpRegister(2) == Register.CL => c.CL,
            _ => throw new NotSupportedException($"interp: shld/shrd cuenta no imm8/CL"),
        };
        int size = OpSize(in insn, 0);
        var d = DstOf(c, in insn, 0);
        if (size == 32) Write32(c, m, in d, op32(Read32D(c, m, in d), Read32(c, m, in insn, 1), cnt));
        else if (size == 16) Write16(c, m, in d, op16(Read16D(c, m, in d), Read16(c, m, in insn, 1), cnt));
        else Write8(c, m, in d, op8(Read8D(c, m, in d), Read8(c, m, in insn, 1), cnt));
    }

    static void BitTest(Cpu c, Memory m, in Instruction insn)
    {
        int size = OpSize(in insn, 0);
        int bitMask = size switch { 32 => 0x1F, 16 => 0x0F, _ => 0x07 };
        int bit = (int)(size == 32 ? Read32(c, m, in insn, 1)
                        : size == 16 ? Read16(c, m, in insn, 1)
                        : Read8(c, m, in insn, 1)) & bitMask;
        var d = DstOf(c, in insn, 0);
        uint cur = size == 32 ? Read32D(c, m, in d)
                 : size == 16 ? Read16D(c, m, in d)
                 : Read8D(c, m, in d);
        uint mask = 1u << bit;
        c.CF = (cur & mask) != 0;
        uint nv = insn.Mnemonic switch
        {
            Mnemonic.Bts => cur | mask,
            Mnemonic.Btr => cur & ~mask,
            Mnemonic.Btc => cur ^ mask,
            _ => cur,   // Bt: solo CF
        };
        if (insn.Mnemonic != Mnemonic.Bt)
        {
            if (size == 32) Write32(c, m, in d, nv);
            else if (size == 16) Write16(c, m, in d, (ushort)nv);
            else Write8(c, m, in d, (byte)nv);
        }
    }

    /// <summary>lds/les/lfs/lgs/lss: offset (16 o 32 bits según el operando) + selector de 16.</summary>
    static void LoadFar(Cpu c, Memory m, in Instruction insn, Action<ushort> setSeg)
    {
        ushort seg = MemSeg(c, in insn);
        if (Addr32(in insn))
        {
            // Direccionamiento de 32 bits (prefijo 0x67): la EA NO cabe en 16 bits y truncarla
            // leía el puntero de OTRO sitio. Medido en un juego DOS/4GW Pro: el kernel de DOS/4GW hace
            // `lds ebx,ss:[ebx+0Ah]` con EBX=0x100F825E para leer el EIP interrumpido del marco,
            // y el truncado a 0x8268 le daba un dword de sus datos bajos (0x0AD68A64) — el VMM
            // se quedaba reintentando ese #PF imposible y acababa abortando el programa.
            uint lin = Memory.LinDeSegmento(seg, 0) + MemEa32(c, in insn);
            if (RegSize(insn.Op0Register) == 32)
            {
                SetR32(c, insn.Op0Register, m.RdLin32(lin));
                setSeg(m.RdLin16(lin + 4));
            }
            else
            {
                SetR16(c, insn.Op0Register, m.RdLin16(lin));
                setSeg(m.RdLin16(lin + 2));
            }
            return;
        }
        ushort ea = MemEa(c, in insn);
        if (RegSize(insn.Op0Register) == 32)
        {
            SetR32(c, insn.Op0Register, m.RdU32(seg, ea));
            setSeg(m.RdU16(seg, (ushort)(ea + 4)));
        }
        else
        {
            SetR16(c, insn.Op0Register, m.RdU16(seg, ea));
            setSeg(m.RdU16(seg, (ushort)(ea + 2)));
        }
    }

    // E/S por cadena (outs/ins): antes stub silencioso — la paleta de la intro de un juego llega
    // por rep outsb al 3C9 y se congelaba. Mismo contrato que StringOp: origen DS:SI con
    // override posible, destino fijo ES:DI, REP repite CX veces. (El Lifter conserva su stub:
    // el producto corre interp-only y el mundo lifteado valida por juez.)
    static void StringIo(Cpu c, Memory m, in Instruction insn, int size, bool isOut)
    {
        int n = size / 8;
        ushort ss = insn.SegmentPrefix != Register.None ? GetR16(c, insn.SegmentPrefix) : c.DS;
        bool rep = insn.HasRepPrefix || insn.HasRepePrefix || insn.HasRepnePrefix;
        void Body()
        {
            if (isOut)
            {
                if (c.Platform != null)
                {
                    if (size == 16) c.Platform.PortOut16(c.DX, m.RdU16(ss, c.SI));
                    else c.Platform.PortOut8(c.DX, m.RdU8(ss, c.SI));
                }
                c.SI = (ushort)(c.SI + (c.DF ? -n : n));
            }
            else
            {
                if (size == 16) m.WrU16(c.ES, c.DI, c.Platform != null ? c.Platform.PortIn16(c.DX) : (ushort)0xFFFF);
                else m.WrU8(c.ES, c.DI, c.Platform != null ? c.Platform.PortIn8(c.DX) : (byte)0xFF);
                c.DI = (ushort)(c.DI + (c.DF ? -n : n));
            }
        }
        if (!rep) { Body(); return; }
        // Un tick por elemento (ver Cpu.ChargeStringElement): el del último lo paga el Step de
        // la propia instrucción, así que se cobran CX-1.
        while (c.CX != 0) { c.CX--; Body(); if (c.CX != 0) c.ChargeStringElement(); }
    }

    // Instrucciones de cadena: espejo exacto de Lifter.LiftString (origen DS:SI con
    // override posible, destino ES:DI; REP repite CX veces; REPE/REPNE cortan por ZF).
    /// <summary>Operación de cadena con TAMAÑO DE DIRECCIÓN de 32 bits. Trabaja con direcciones
    /// lineales (base del segmento + índice de 32 bits) porque el offset ya no cabe en un ushort;
    /// en modo real la base es <c>seg×16</c> y sale exactamente lo mismo de siempre.</summary>
    /// <summary>Cede la ejecución al intérprete de 32 bits y no vuelve: el programa ya vive en su
    /// mundo plano. Se corre por trozos para que el temporizador y las IRQ del anfitrión sigan
    /// atendiéndose (el `Step` del final de cada trozo es el que mueve el reloj y la compuerta de
    /// frame del host).</summary>
    static bool CederA32(Cpu c, Memory m, Platform.DosPlatform dp, ushort ip)
    {
        var svc = dp.Dpmi!;
        Interp32.OnInt = svc.Int;
        Interp32.OnIrq = svc.ServicioIrq;
        Interp32.BaseDeSelector = svc.BaseDe16;
        Interp32.OnDescriptor = svc.Descriptores;
        Console.Error.WriteLine($"[DPMI] el mundo pasa a 32 BITS: CS={c.CS:X4} EIP=0x{ip:X4} " +
                                $"(base 0x{svc.BaseDe16(c.CS):X})");
        // El volcado tiene que salir TAMBIÉN cuando el intérprete lanza: el caso interesante es
        // justo ése —el flujo se ha ido a donde no debía— y sin el volcado no hay forma de ver
        // qué había (o qué faltaba) en la memoria a la que saltó.
        try { return CederA32Bucle(c, m, svc, ip); }
        catch { VolcarMundoPlano(c, m, svc); throw; }
    }

    static void VolcarMundoPlano(Cpu c, Memory m, Platform.FlatServices svc)
    {
        string? dump = Environment.GetEnvironmentVariable("PAKU_DUMP32");
        if (string.IsNullOrEmpty(dump)) return;
        // Desde la base del código hasta el tope de lo reservado: el juego no vive sólo en su
        // imagen, también en el montón que le dio el extensor — y ahí es donde un motor que
        // GENERA código deja lo que ejecuta.
        uint bas = svc.BaseDe16(c.CS);
        long fin = m.FlatLimit;
        var buf = new byte[Math.Min(fin - bas, 64L << 20)];
        for (int i = 0; i < buf.Length; i++) buf[i] = m.RdLin8((uint)(bas + i));
        System.IO.File.WriteAllBytes(dump, buf);
        Console.Error.WriteLine($"[DPMI] volcado del mundo plano: 0x{bas:X}+{buf.Length} -> {dump}");
    }

    static bool CederA32Bucle(Cpu c, Memory m, Platform.FlatServices svc, ushort ip)
    {
        // PAKU_SAMPLE_PC32=1: histograma del EIP del mundo plano. Es LA herramienta para un juego
        // que corre y no avanza: dice en qué bucle está metido, que es lo único que un log de
        // servicios no puede contar. El tamaño del trozo se hace IRREGULAR mientras muestrea para
        // que el corte no caiga siempre en el mismo punto de un bucle de periodo divisor.
        bool muestrear = Environment.GetEnvironmentVariable("PAKU_SAMPLE_PC32") == "1";
        var histograma = muestrear ? new Dictionary<uint, int>() : null;
        int muestras = 0, semilla = 12345;

        uint eip = ip;
        while (true)
        {
            // 16384 y no 65536: el trozo es lo que tarda el host en respirar (poll del reloj y
            // del audio, y la compuerta de frame). A las ~8 M instrucciones/s que da el
            // intérprete de 32 bits, 16384 son ~2 ms — bastante fino para que el anillo de audio
            // no se seque, y bastante grueso para que el coste de salir y entrar no se note.
            int trozo = 16384;
            if (muestrear)
            {
                semilla = semilla * 1103515245 + 12345;
                trozo = 40000 + ((semilla >> 8) & 0x7FFF);
                if (histograma!.Count > 0 && ++muestras % 2000 == 0)
                {
                    Console.Error.WriteLine($"[PC32] {muestras} muestras, los 8 sitios más vistos:");
                    foreach (var kv in histograma.OrderByDescending(k => k.Value).Take(8))
                        Console.Error.WriteLine($"[PC32]   {kv.Key:X8}  {kv.Value * 100.0 / muestras:F1}%  ({kv.Value})");
                }
            }
            var res = Interp32.Run(c, m, eip, trozo);
            // El POLL PERIÓDICO del host, una vez por trozo y a mano. Va aquí porque `Cpu.Step()`
            // sólo lo dispara cada 4096 ticks y en este mundo se llama UNA VEZ POR TROZO: el poll
            // acababa saliendo cada 4096 trozos, o sea cada ~33 s. Y de ese poll cuelga el
            // relleno del anillo de audio, así que la tarjeta sonaba a ráfagas y el anfitrión
            // entregaba bloques enteros de silencio — 33 microcortes de 85,8 ms EXACTOS en 90 s,
            // medidos: un valor constante y repetido nunca es del juego, es del sistema.
            c.OnPeriodicPoll?.Invoke(c);
            if (histograma != null)
                histograma[res.EipOffset] = histograma.TryGetValue(res.EipOffset, out var n) ? n + 1 : 1;
            if (res.Motivo != Interp32.Fin.Presupuesto)
            {
                Console.Error.WriteLine($"[DPMI] el mundo de 32 bits se detuvo: {res}");
                // Los registros y las últimas peticiones al DOS de 32 bits: sin esto, un
                // "INT 21h sin servicio" no dice QUÉ pidió el juego, que es lo único que
                // importa para saber qué falta.
                Console.Error.WriteLine(
                    $"[DPMI] EAX={c.EAX:X8} EBX={c.EBX:X8} ECX={c.ECX:X8} EDX={c.EDX:X8} " +
                    $"ESI={c.ESI:X8} EDI={c.EDI:X8} EBP={c.EBP:X8} ESP={c.ESP:X8} " +
                    $"DS={c.DS:X4} ES={c.ES:X4} SS={c.SS:X4}");
                int desde = Math.Max(0, svc.Traza.Count - 12);
                for (int i = desde; i < svc.Traza.Count; i++)
                    Console.Error.WriteLine($"[DPMI]   traza: {svc.Traza[i]}");
                foreach (var r in svc.Rechazados)
                    Console.Error.WriteLine($"[DPMI]   rechazado: {r}");
                // PAKU_DUMP32=<fichero>: vuelca la imagen del mundo plano tal como quedó. Es la
                // única forma de comparar byte a byte lo que cargamos con lo que hay en el disco:
                // un programa que comprueba su propia integridad dice que algo no cuadra, nunca
                // DÓNDE.
                VolcarMundoPlano(c, m, svc);
                return false;
            }
            eip = res.EipOffset;
            if (!c.Step()) return false;
        }
    }

    static void StringOp32(Cpu c, Memory m, in Instruction insn, int size, char kind,
                           int n, ushort segOrigen, bool rep, bool repne)
    {
        uint baseOrigen = Memory.LinDeSegmento(segOrigen, 0);
        uint baseDestino = Memory.LinDeSegmento(c.ES, 0);
        int paso = c.DF ? -n : n;

        void Uno()
        {
            uint o = baseOrigen + c.ESI, d = baseDestino + c.EDI;
            switch (kind)
            {
                case 'm':
                    if (size == 32) m.WrLin32(d, m.RdLin32(o));
                    else if (size == 16) m.WrLin16(d, m.RdLin16(o));
                    else m.WrLin8(d, m.RdLin8(o));
                    c.ESI = (uint)(c.ESI + paso); c.EDI = (uint)(c.EDI + paso);
                    break;
                case 's':
                    if (size == 32) m.WrLin32(d, c.EAX);
                    else if (size == 16) m.WrLin16(d, c.AX);
                    else m.WrLin8(d, c.AL);
                    c.EDI = (uint)(c.EDI + paso);
                    break;
                case 'l':
                    if (size == 32) c.EAX = m.RdLin32(o);
                    else if (size == 16) c.AX = m.RdLin16(o);
                    else c.AL = m.RdLin8(o);
                    c.ESI = (uint)(c.ESI + paso);
                    break;
                case 'z':
                    if (size == 32) c.Cmp32(c.EAX, m.RdLin32(d));
                    else if (size == 16) c.Cmp16(c.AX, m.RdLin16(d));
                    else c.Cmp8(c.AL, m.RdLin8(d));
                    c.EDI = (uint)(c.EDI + paso);
                    break;
                case 'c':
                    if (size == 32) c.Cmp32(m.RdLin32(o), m.RdLin32(d));
                    else if (size == 16) c.Cmp16(m.RdLin16(o), m.RdLin16(d));
                    else c.Cmp8(m.RdLin8(o), m.RdLin8(d));
                    c.ESI = (uint)(c.ESI + paso); c.EDI = (uint)(c.EDI + paso);
                    break;
            }
        }

        if (!rep && !repne) { Uno(); return; }
        if (kind is 'z' or 'c')
        {
            while (c.ECX != 0)
            {
                c.ECX--; Uno();
                if (repne ? c.ZF : !c.ZF) break;
                if (c.ECX != 0) c.ChargeStringElement();
            }
            return;
        }
        while (c.ECX != 0) { c.ECX--; Uno(); if (c.ECX != 0) c.ChargeStringElement(); }
    }

    static void StringOp(Cpu c, Memory m, in Instruction insn, int size, char kind)
    {
        int n = size / 8;
        ushort ss = insn.SegmentPrefix != Register.None ? GetR16(c, insn.SegmentPrefix) : c.DS;
        bool rep = insn.HasRepPrefix || insn.HasRepePrefix;
        bool repne = insn.HasRepnePrefix;
        ushort si0 = c.SI, di0 = c.DI, cx0 = c.CX;   // pre-op state for blit telemetry

        // TAMAÑO DE DIRECCIÓN de 32 bits (`rep movs [edi],[esi]`, prefijo 0x67): los índices son
        // ESI/EDI enteros y el contador ECX, no sus mitades bajas. Con los de 16 la copia da la
        // vuelta a los 64 KB y machaca memoria ajena — un extensor embebido mueve así su imagen
        // de 32 bits y acababa pisándose su propio código. Iced lo dice en el tipo de operando.
        bool addr32 = insn.Op0Kind is OpKind.MemoryESEDI or OpKind.MemorySegESI
                   || insn.Op1Kind is OpKind.MemoryESEDI or OpKind.MemorySegESI;
        if (addr32)
        {
            StringOp32(c, m, in insn, size, kind, n, ss, rep, repne);
            return;
        }

        void Body()
        {
            switch (kind)
            {
                case 'm':   // movs
                    if (size == 32) m.WrU32(c.ES, c.DI, m.RdU32(ss, c.SI));
                    else if (size == 16) m.WrU16(c.ES, c.DI, m.RdU16(ss, c.SI));
                    else m.WrU8(c.ES, c.DI, m.RdU8(ss, c.SI));
                    c.SI = (ushort)(c.SI + (c.DF ? -n : n));
                    c.DI = (ushort)(c.DI + (c.DF ? -n : n));
                    break;
                case 's':   // stos
                    if (size == 32) m.WrU32(c.ES, c.DI, c.EAX);
                    else if (size == 16) m.WrU16(c.ES, c.DI, c.AX);
                    else m.WrU8(c.ES, c.DI, c.AL);
                    c.DI = (ushort)(c.DI + (c.DF ? -n : n));
                    break;
                case 'l':   // lods
                    if (size == 32) c.EAX = m.RdU32(ss, c.SI);
                    else if (size == 16) c.AX = m.RdU16(ss, c.SI);
                    else c.AL = m.RdU8(ss, c.SI);
                    c.SI = (ushort)(c.SI + (c.DF ? -n : n));
                    break;
                case 'z':   // scas
                    if (size == 32) c.Cmp32(c.EAX, m.RdU32(c.ES, c.DI));
                    else if (size == 16) c.Cmp16(c.AX, m.RdU16(c.ES, c.DI));
                    else c.Cmp8(c.AL, m.RdU8(c.ES, c.DI));
                    c.DI = (ushort)(c.DI + (c.DF ? -n : n));
                    break;
                case 'c':   // cmps
                    if (size == 32) c.Cmp32(m.RdU32(ss, c.SI), m.RdU32(c.ES, c.DI));
                    else if (size == 16) c.Cmp16(m.RdU16(ss, c.SI), m.RdU16(c.ES, c.DI));
                    else c.Cmp8(m.RdU8(ss, c.SI), m.RdU8(c.ES, c.DI));
                    c.SI = (ushort)(c.SI + (c.DF ? -n : n));
                    c.DI = (ushort)(c.DI + (c.DF ? -n : n));
                    break;
            }
        }

        // Level 2: string-op writes are already covered by LogBlit as rectangles — mask them
        // from the raw-write hook in Memory.WrU8 while the op runs (try/finally: the flag must
        // never survive this call).
        bool suppressRaw = _blitLevel >= 2 && kind is 'm' or 's';
        if (suppressRaw) Memory.SuppressRawBlit = true;
        // Nivel 3: marca el principio de las escrituras de este blit, para que offline se sepa
        // cuáles son suyas. Se cierra en LogBlit, que es donde se calcula la identidad.
        if (BlitRawLog.Todo && kind is 'm' or 's') BlitRawLog.AbreGrupo();
        try
        {
            if (!rep && !repne)
            {
                Body();
                if ((_logBlit || RemakeMap.Active) && kind is 'm' or 's') LogBlit(c, m, in insn, kind, size, n, ss, si0, di0, 1);
                return;
            }
            if (kind is 'z' or 'c')
            {
                while (c.CX != 0)
                {
                    c.CX--;
                    Body();
                    if (repne ? c.ZF : !c.ZF) break;
                    if (c.CX != 0) c.ChargeStringElement();
                }
                return;
            }
            // Un tick por elemento (ver Cpu.ChargeStringElement): el del último lo paga el Step
            // de la propia instrucción, así que se cobran CX-1.
            while (c.CX != 0) { c.CX--; Body(); if (c.CX != 0) c.ChargeStringElement(); }
            if ((_logBlit || RemakeMap.Active) && kind is 'm' or 's') LogBlit(c, m, in insn, kind, size, n, ss, si0, di0, cx0);
        }
        finally
        {
            if (suppressRaw) Memory.SuppressRawBlit = false;
            // A string op that never reached LogBlit (destination in RAM, CX=0, a fault) must still
            // close its PBR1 group: left open, the loose writes that follow look like part of it
            // and build_texto.py, which counts exactly those, never sees them.
            if (BlitRawLog.Todo && BlitRawLog.GroupOpen)
                BlitRawLog.CierraGrupo(((uint)c.CS << 16) | insn.IP16, "", null);
        }
    }

    // Emits one line per movs/stos that landed in the active VRAM window. Source bytes are
    // hashed from Raw (never through ReadVram: that would load the EGA latches and perturb
    // the game); a VRAM->VRAM copy is logged without hash for the same reason.
    static void LogBlit(Cpu c, Memory m, in Instruction insn, char kind, int size, int n,
                        ushort ss, ushort si0, ushort di0, int elems)
    {
        var video = m.Video;
        if (video == null || elems == 0) return;
        int dstLin = Memory.Lin(c.ES, di0);
        int bytes = elems * n;
        bool dstVram = dstLin >= 0xA0000 && dstLin < 0xC0000 &&
                       (uint)(dstLin - video.MemoryMapBase) < (uint)video.MemoryMapSize;
        if (!dstVram)
        {
            // Fase 5 (doctrina del segundo porteo, "la procedencia debe ENCADENARSE"):
            // un movs RAM->RAM es un eslabón de composición (fichero -> buffer A ->
            // buffer B -> VRAM). Si el origen está etiquetado, el destino HEREDA la
            // etiqueta; sin esto la identidad se pierde en el salto intermedio y el
            // blit final llega anónimo. Con DF=1 el bloque tocado es [x-bytes+1, x].
            if (kind == 'm' && BlitProvenance.Enabled)
            {
                int srcLin0 = Memory.Lin(ss, si0);
                int srcStart = c.DF ? srcLin0 - bytes + 1 : srcLin0;
                if (BlitProvenance.TryFind(srcStart, out var chainFile, out var chainOff))
                    BlitProvenance.Record(c.DF ? dstLin - bytes + 1 : dstLin,
                                          bytes, chainFile, chainOff);
            }
            return;
        }

        string src;
        // La clave del blit: la MISMA cadena con la que tools/blit_catalog indexa este
        // rectángulo (fnv del origen, o el valor del relleno en un stos). Viaja al log crudo
        // en el nivel 3 para poder pegarle sus píxeles sin adivinar.
        string clave = "";
        string? provKit = null;
        if (kind == 's')
        {
            clave = $"{(size == 32 ? c.EAX : size == 16 ? c.AX : c.AL):X}";
            src = $"fill={clave}";
        }
        else
        {
            int srcLin = Memory.Lin(ss, si0);
            bool srcVram = srcLin >= 0xA0000 && srcLin < 0xC0000 &&
                           (uint)(srcLin - video.MemoryMapBase) < (uint)video.MemoryMapSize;
            if (srcVram)
                src = $"src={ss:X4}:{si0:X4} vram";
            else
            {
                uint h = 2166136261;
                for (int i = 0; i < bytes; i++)
                    h = (h ^ m.Raw[Memory.Lin(ss, (ushort)(si0 + (c.DF ? -i : i)))]) * 16777619;
                clave = $"{h:X8}";
                src = $"src={ss:X4}:{si0:X4} fnv={clave}";
                // Fase 2: tag the source with its file provenance when known. Se resuelve
                // ANTES del hook de reemplazo porque la procedencia es la identidad
                // preferente: sobrevive a que el juego componga el sprite con el fondo,
                // cosa que el hash de píxeles no hace.
                string? prov = null;
                if (BlitProvenance.TryFind(Memory.Lin(ss, si0), out var pf, out var poff))
                {
                    prov = provKit = $"{pf}@0x{poff:X}";
                    src += $" file={prov}";
                }
                // PASO 4: HD replacement instance for the host overlay (game pixel coords).
                if (RemakeMap.Active)
                {
                    var (gx, gy) = RemakeMap.GameXY(dstLin - video.MemoryMapBase, video.VideoMode);
                    // Anchura en píxeles y rutina que dibuja: con las dos, el motor puede
                    // reconocer que varios blits seguidos son UNA figura (el juego que compone
                    // fila a fila) y reemplazarla entera. Sin ellas sólo hay reemplazo por clave
                    // suelta, que en un juego así no llega al 1 % de lo que se pinta.
                    RemakeMap.Note(h, prov, gx, gy, bytes * RemakeMap.PixelsPerByte(video.VideoMode),
                                   ((uint)c.CS << 16) | insn.IP16, c.CyclesPerMs);
                }
            }
        }
        // Nivel 3: cierra el grupo abierto arriba. Un blit VRAM->VRAM no trae clave (no se hashea
        // el origen, que cargaría los latches EGA) y se cierra vacío: queda anónimo a propósito.
        if (BlitRawLog.Todo) BlitRawLog.CierraGrupo(((uint)c.CS << 16) | insn.IP16, clave, provKit);

        if (!_logBlit) return;
        _blitLog ??= OpenBlitLog();
        // `frame` y `orden`: EN QUÉ fotograma cae el blit y el CUÁNTO va dentro de él. Es lo que
        // convierte el orden de dibujo de una estimación en una medida — antes se infería por un
        // hueco de tiempo entre ráfagas, y el orden es justo lo que decide qué tapa a qué. Exige
        // que el contador de fotogramas avance, o sea PAKU_PRESENT_CAPTURE=1 (lo pone `capture`);
        // sin él sale frame=0 en todas las líneas, que se ve y no engaña.
        long frame = video.FrameGeneration;
        if (frame != _blitFrame) { _blitFrame = frame; _blitOrden = 0; }
        _blitLog.WriteLine($"t={Cpu.TickCount} at={c.CS:X4}:{insn.IP16:X4} op={kind}{size} " +
                           $"mode={video.VideoMode:X2} df={(c.DF ? 1 : 0)} dst={c.ES:X4}:{di0:X4} lin={dstLin:X5} n={elems} bytes={bytes} " +
                           $"frame={frame} orden={_blitOrden++} {src}");
        if (++_blitLines % 4096 == 0) _blitLog.Flush();
    }

    static StreamWriter OpenBlitLog()
    {
        Directory.CreateDirectory("logs");
        var w = new StreamWriter(Path.Combine("logs", "blit.log")) { AutoFlush = false };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { w.Flush(); } catch { } };
        return w;
    }

    // ================= x87 (espejo de Lifter.LiftFpu; misma Fpu software) =================

    static void ExecFpu(Cpu c, Memory m, in Instruction insn)
    {
        var f = c.Fpu;
        switch (insn.Mnemonic)
        {
            // control / estado → no-op (modo por defecto)
            case Mnemonic.Wait: case Mnemonic.Fninit: case Mnemonic.Fldcw: case Mnemonic.Fnclex:
            case Mnemonic.Fnop:
            case Mnemonic.Fnsave: case Mnemonic.Frstor: case Mnemonic.Fnstenv: case Mnemonic.Fldenv:
            // fnsetpm era del 287, donde ponía el coprocesador en modo protegido; del 387 en
            // adelante es un no-op reconocido. La usa el extensor DOS/16M (0A5C:0243 en un juego de prueba)
            // al cambiar de modo, y como esto emula un 386+, no-op es la respuesta correcta.
            case Mnemonic.Fnsetpm:
                return;
            case Mnemonic.Fnstcw:
                m.WrU16(MemSeg(c, in insn), MemEa(c, in insn), 0x037F);
                return;
            case Mnemonic.Fnstsw:
                if (insn.Op0Kind == OpKind.Register) c.AX = f.StatusWord();
                else m.WrU16(MemSeg(c, in insn), MemEa(c, in insn), f.StatusWord());
                return;

            // cargas
            case Mnemonic.Fld:
                if (insn.Op0Kind == OpKind.Register) f.Push(f.St(StIdx(insn.Op0Register)));
                else f.Push(FpuMemFloat(c, m, in insn));
                return;
            case Mnemonic.Fild: f.Push(FpuMemInt(c, m, in insn)); return;
            case Mnemonic.Fld1: f.Push(1.0); return;
            case Mnemonic.Fldz: f.Push(0.0); return;
            case Mnemonic.Fldpi: f.Push(Math.PI); return;
            case Mnemonic.Fldl2t: f.Push(Math.Log2(10.0)); return;
            case Mnemonic.Fldl2e: f.Push(Math.Log2(Math.E)); return;
            case Mnemonic.Fldln2: f.Push(Math.Log(2.0)); return;
            case Mnemonic.Fldlg2: f.Push(Math.Log10(2.0)); return;

            // BCD empaquetado (Turbo Basic los usa para formatear números en PRINT)
            case Mnemonic.Fbld:
            {
                // mirrors DOSBox-X FPU_FBLD (fpu_instructions.h:225), 18 packed digits + sign
                ushort seg = MemSeg(c, in insn), ea = MemEa(c, in insn);
                ulong val = 0, bcdBase = 1;
                for (int i = 0; i < 9; i++)
                {
                    byte b = m.RdU8(seg, (ushort)(ea + i));
                    val += (ulong)(b & 0xF) * bcdBase; bcdBase *= 10;
                    val += (ulong)((b >> 4) & 0xF) * bcdBase; bcdBase *= 10;
                }
                byte sb = m.RdU8(seg, (ushort)(ea + 9));
                double t = val + (double)(sb & 0xF) * bcdBase;
                if ((sb & 0x80) != 0) t = -t;
                f.Push(t);
                return;
            }
            case Mnemonic.Fbstp:
            {
                // mirrors DOSBox-X FPU_FBST (fpu_instructions.h:338) + pop
                ushort seg = MemSeg(c, in insn), ea = MemEa(c, in insn);
                double v = f.St(0);
                bool neg = v < 0;
                m.WrU8(seg, (ushort)(ea + 9), (byte)(neg ? 0x80 : 0));
                if (neg) v = -v;
                double r = Math.Round(v, MidpointRounding.ToEven);
                if (!(r <= 999999999999999999.0))   // overflow/NaN -> BCD integer indefinite
                {
                    m.WrU32(seg, ea, 0);
                    m.WrU32(seg, (ushort)(ea + 4), 0xC0000000);
                    m.WrU16(seg, (ushort)(ea + 8), 0xFFFF);
                    f.Pop();
                    return;
                }
                ulong rnd = (ulong)r;
                for (int i = 0; i < 9; i++)
                {
                    ulong tq = rnd / 10;
                    byte p = (byte)(rnd % 10);
                    rnd = tq / 10;
                    p |= (byte)((tq % 10) << 4);
                    m.WrU8(seg, (ushort)(ea + i), p);
                }
                f.Pop();
                return;
            }

            // almacenamientos
            case Mnemonic.Fst: FpuStore(c, m, in insn, pop: false); return;
            case Mnemonic.Fstp: FpuStore(c, m, in insn, pop: true); return;
            case Mnemonic.Fist: FpuStore(c, m, in insn, pop: false); return;
            case Mnemonic.Fistp: case Mnemonic.Fisttp: FpuStore(c, m, in insn, pop: true); return;

            // unarias sobre ST(0)
            case Mnemonic.Fchs: f.SetSt(0, -f.St(0)); return;
            case Mnemonic.Fabs: f.SetSt(0, Math.Abs(f.St(0))); return;
            case Mnemonic.Fsqrt: f.SetSt(0, Math.Sqrt(f.St(0))); return;
            case Mnemonic.Frndint: f.SetSt(0, Math.Round(f.St(0), MidpointRounding.ToEven)); return;
            case Mnemonic.Fxch:
            {
                int i = insn.OpCount > 0 && insn.Op0Kind == OpKind.Register ? StIdx(insn.Op0Register) : 1;
                double t = f.St(0); f.SetSt(0, f.St(i)); f.SetSt(i, t);
                return;
            }

            // transcendentales
            case Mnemonic.Fpatan: { double r = Math.Atan2(f.St(1), f.St(0)); f.Pop(); f.SetSt(0, r); return; }
            case Mnemonic.Fptan: f.SetSt(0, Math.Tan(f.St(0))); f.Push(1.0); return;
            case Mnemonic.Fyl2x: { double r = f.St(1) * Math.Log2(f.St(0)); f.Pop(); f.SetSt(0, r); return; }
            case Mnemonic.Fyl2xp1: { double r = f.St(1) * Math.Log2(f.St(0) + 1.0); f.Pop(); f.SetSt(0, r); return; }
            case Mnemonic.F2xm1: f.SetSt(0, Math.Pow(2.0, f.St(0)) - 1.0); return;
            case Mnemonic.Fscale: f.SetSt(0, f.St(0) * Math.Pow(2.0, Math.Truncate(f.St(1)))); return;
            case Mnemonic.Fprem: case Mnemonic.Fprem1: f.SetSt(0, Math.IEEERemainder(f.St(0), f.St(1))); return;
            case Mnemonic.Fxtract:
            {
                int exp = (int)Math.ILogB(f.St(0));
                double sig = f.St(0) * Math.Pow(2.0, -exp);
                f.SetSt(0, exp); f.Push(sig);
                return;
            }
            case Mnemonic.Fcos: f.SetSt(0, Math.Cos(f.St(0))); return;
            case Mnemonic.Fsin: f.SetSt(0, Math.Sin(f.St(0))); return;
            case Mnemonic.Fsincos: { double v = f.St(0); f.SetSt(0, Math.Sin(v)); f.Push(Math.Cos(v)); return; }

            // miscelánea
            case Mnemonic.Ftst: f.Compare(f.St(0), 0.0); return;
            case Mnemonic.Fxam: f.Examine(); return;
            case Mnemonic.Fdecstp: f.DecTop(); return;
            case Mnemonic.Fincstp: f.IncTop(); return;
            case Mnemonic.Ffree:
                f.Free(insn.OpCount > 0 && insn.Op0Kind == OpKind.Register ? StIdx(insn.Op0Register) : 0);
                return;

            // comparaciones (C0/C2/C3)
            case Mnemonic.Fcom: case Mnemonic.Fucom: FpuCompare(c, m, in insn, pops: 0); return;
            case Mnemonic.Fcomp: case Mnemonic.Fucomp: FpuCompare(c, m, in insn, pops: 1); return;
            case Mnemonic.Fcompp: case Mnemonic.Fucompp: FpuCompare(c, m, in insn, pops: 2); return;
            case Mnemonic.Ficom: f.Compare(f.St(0), FpuMemInt(c, m, in insn)); return;
            case Mnemonic.Ficomp: f.Compare(f.St(0), FpuMemInt(c, m, in insn)); f.Pop(); return;

            // compare + EFLAGS (P6+)
            case Mnemonic.Fcomi: case Mnemonic.Fucomi:
                f.CompareToFlags(c, f.St(0), f.St(FpuStOperand(in insn)));
                return;
            case Mnemonic.Fcomip: case Mnemonic.Fucomip:
                f.CompareToFlags(c, f.St(0), f.St(FpuStOperand(in insn)));
                f.Pop();
                return;

            // aritmética
            case Mnemonic.Fadd: FpuArith(c, m, in insn, '+', rev: false, pop: false); return;
            case Mnemonic.Faddp: FpuArith(c, m, in insn, '+', rev: false, pop: true); return;
            case Mnemonic.Fiadd: FpuIArith(c, m, in insn, '+', rev: false); return;
            case Mnemonic.Fsub: FpuArith(c, m, in insn, '-', rev: false, pop: false); return;
            case Mnemonic.Fsubp: FpuArith(c, m, in insn, '-', rev: false, pop: true); return;
            case Mnemonic.Fsubr: FpuArith(c, m, in insn, '-', rev: true, pop: false); return;
            case Mnemonic.Fsubrp: FpuArith(c, m, in insn, '-', rev: true, pop: true); return;
            case Mnemonic.Fisub: FpuIArith(c, m, in insn, '-', rev: false); return;
            case Mnemonic.Fisubr: FpuIArith(c, m, in insn, '-', rev: true); return;
            case Mnemonic.Fmul: FpuArith(c, m, in insn, '*', rev: false, pop: false); return;
            case Mnemonic.Fmulp: FpuArith(c, m, in insn, '*', rev: false, pop: true); return;
            case Mnemonic.Fimul: FpuIArith(c, m, in insn, '*', rev: false); return;
            case Mnemonic.Fdiv: FpuArith(c, m, in insn, '/', rev: false, pop: false); return;
            case Mnemonic.Fdivp: FpuArith(c, m, in insn, '/', rev: false, pop: true); return;
            case Mnemonic.Fdivr: FpuArith(c, m, in insn, '/', rev: true, pop: false); return;
            case Mnemonic.Fdivrp: FpuArith(c, m, in insn, '/', rev: true, pop: true); return;
            case Mnemonic.Fidiv: FpuIArith(c, m, in insn, '/', rev: false); return;
            case Mnemonic.Fidivr: FpuIArith(c, m, in insn, '/', rev: true); return;

            default:
                throw new NotSupportedException($"interp: x87 no soportado {insn.Mnemonic}");
        }
    }

    static int StIdx(Register r) => r >= Register.ST0 && r <= Register.ST7 ? r - Register.ST0 : 0;

    static int FpuStOperand(in Instruction insn) =>
        insn.OpCount > 0 && insn.Op0Kind == OpKind.Register ? StIdx(insn.Op0Register)
        : insn.OpCount > 1 && insn.Op1Kind == OpKind.Register ? StIdx(insn.Op1Register) : 1;

    static double FpuMemFloat(Cpu c, Memory m, in Instruction insn)
    {
        ushort seg = MemSeg(c, in insn), ea = MemEa(c, in insn);
        return insn.MemorySize switch
        {
            MemorySize.Float32 => m.RdF32(seg, ea),
            MemorySize.Float64 => m.RdF64(seg, ea),
            MemorySize.Float80 => Fpu.FromF80(
                m.RdU32(seg, ea) | ((ulong)m.RdU32(seg, (ushort)(ea + 4)) << 32),
                m.RdU16(seg, (ushort)(ea + 8))),
            MemorySize.Int16 => (short)m.RdU16(seg, ea),
            MemorySize.Int32 => (int)m.RdU32(seg, ea),
            _ => throw new NotSupportedException($"interp: x87 operando mem {insn.MemorySize} ({insn.Mnemonic})"),
        };
    }

    static double FpuMemInt(Cpu c, Memory m, in Instruction insn)
    {
        ushort seg = MemSeg(c, in insn), ea = MemEa(c, in insn);
        return insn.MemorySize switch
        {
            MemorySize.Int16 => (short)m.RdU16(seg, ea),
            MemorySize.Int32 => (int)m.RdU32(seg, ea),
            // fild qword: same 64-bit form Interp32 already handles (Interp32.FpuMemInt).
            MemorySize.Int64 => (long)(m.RdU32(seg, ea) | ((ulong)m.RdU32(seg, (ushort)(ea + 4)) << 32)),
            _ => throw new NotSupportedException($"interp: x87 int mem {insn.MemorySize} ({insn.Mnemonic})"),
        };
    }

    /// <summary>Entra o sale de modo protegido de 16 bits según el bit PE del MSW/CR0.
    ///
    /// El cambio consiste en decir de dónde sale la base de un segmento: en modo real de
    /// <c>seg&lt;&lt;4</c>, y en protegido del descriptor que el propio programa escribió en su GDT.
    /// Toda la maquinaria para eso ya existía por los extensores DPMI (Memory.BaseDelSelector);
    /// lo único nuevo aquí es que la tabla no la inventa el host, se lee de la memoria del
    /// invitado. Salir vuelve a dejar el mundo exactamente como estaba.</summary>
    static void ModoProtegido16(Cpu c, Memory m, uint msw)
    {
        bool antes = (c.Msw & 1) != 0, ahora = (msw & 1) != 0;
        c.Msw = msw;
        if (antes == ahora) return;

        // Cambiar PE no reinterpreta los registros de segmento en el acto, NI AL ENTRAR NI AL
        // SALIR: un 386 lleva una caché oculta de descriptor por registro, y la base cargada
        // sigue vigente hasta que ese registro se RECARGA. Por eso todo cambio de modo va
        // seguido de un salto largo. Medido en un juego DOS/16M, en las dos direcciones: al salir, el
        // `ljmp 01BD:28BB` que hay detrás del `mov cr0,eax` se leía con CS=0018 tomado como
        // 0x180; al entrar, el `jmp far 0018:0334` que hay detrás del `lmsw` se leía con
        // CS=0A5C tomado como selector. En ambos casos el intérprete acababa paseando por
        // memoria vacía, que parecía un opcode inválido y era un descarrilamiento.
        _basesCacheadas = new Dictionary<ushort, uint>();
        foreach (ushort s in new[] { c.CS, c.DS, c.ES, c.SS, c.FS, c.GS })
            _basesCacheadas[s] = antes ? BaseDeSelectorReal(c, m, s) : (uint)(s << 4);
        // A partir de aquí, ningún segmento se ha recargado: sus bases cacheadas valen todas.
        // Cada carga irá marcando su bit (Cpu.SegRecargados) y matando la suya.
        c.SegRecargados = 0;
        _csAlSalir = c.CS;
        _saliendoDePm = true;
        _destinoProtegido = ahora;
        // Durante la transición se pasa siempre por la tabla: lo cacheado conserva su base
        // vieja y lo demás ya se resuelve con el modo NUEVO, que es lo que hace el hardware
        // con un registro recargado después del cambio.
        Memory.BaseDelSelector = ResolutorConCache(c, m, ahora);
        Memory.SegmentosSonSelectores = true;
        Memory.GenDescriptores++;   // en modo real la GDT se escribe por caminos que no vigilamos

        if (ahora)
        {
            if (Trace)
            {
                Console.Error.WriteLine(
                    $"[PM16] modo protegido ON: GDT base=0x{c.GdtBase:X8} limite=0x{c.GdtLimit:X4}");
                for (uint i = 8; i <= c.GdtLimit && i < 8 * 24; i += 8)
                {
                    uint l = c.GdtBase + i;
                    uint bas = m.RdLin16(l + 2) | ((uint)m.RdLin8(l + 4) << 16)
                               | ((uint)m.RdLin8(l + 7) << 24);
                    Console.Error.WriteLine(
                        $"[PM16]   sel {i:X4}: base=0x{bas:X8} limite=0x{m.RdLin16(l):X4} " +
                        $"acceso=0x{m.RdLin8(l + 5):X2} gran=0x{m.RdLin8(l + 6):X2}");
                }
                Console.Error.WriteLine(
                    $"[PM16] IDT base=0x{c.IdtBase:X8} limite=0x{c.IdtLimit:X4}");
                foreach (byte v in new byte[] { 0x08, 0x09, 0x10, 0x16, 0x21, 0x31, 0x33 })
                {
                    if (c.IdtLimit < (uint)(v * 8 + 7)) continue;
                    uint g = c.IdtBase + (uint)(v * 8);
                    Console.Error.WriteLine(
                        $"[PM16]   int {v:X2}: sel={m.RdLin16(g + 2):X4} off={m.RdLin16(g):X4} " +
                        $"attr=0x{m.RdLin8(g + 5):X2}");
                }
            }
        }
        else if (Trace) Console.Error.WriteLine(
            "[PM16] modo protegido OFF (bases cacheadas hasta que se recargue CS)");
    }

    // Cambio de modo en curso: las bases viejas siguen valiendo hasta que CS se recarga, que es
    // cuando el hardware da el cambio por consumado. Ver ModoProtegido16.
    static bool _saliendoDePm, _destinoProtegido;
    static ushort _csAlSalir;
    static Dictionary<ushort, uint>? _basesCacheadas;

    /// <summary>Resuelve la base de un selector durante y después de un cambio de modo, con la
    /// CACHÉ OCULTA DE DESCRIPTORES del 386.
    ///
    /// La caché es POR REGISTRO, y ahí estaba el fallo: se tiraba entera en cuanto se recargaba
    /// CS, cuando lo que el hardware consume con esa recarga es SÓLO la de CS — DS, ES, SS, FS y
    /// GS siguen con su base vieja hasta que cada uno se recargue. Un juego DOS/4GW Pro depende de eso al
    /// pie de la letra: al volver a modo protegido lee su bandera "había paginación" con
    /// `ds:[0DFE]` llevando todavía el DS de modo real (0824), y sólo DESPUÉS recarga DS. Con la
    /// caché tirada, ese 0824 se resolvía como selector, la base salía 0, la bandera se leía de
    /// otra dirección y valía 0 — así que el kernel NO volvía a encender la paginación y su
    /// cargador moría con `can't initialize loader LINEXE_LOADER`.
    ///
    /// La caché es POR REGISTRO (`Cpu.SegRecargados`): la base vieja de un selector sólo vale
    /// mientras algún registro que NO se haya recargado siga llevándolo. Recargar CS consuma el
    /// cambio de modo, pero no la base de DS, ES, SS, FS ni GS — y de eso depende el kernel de
    /// DOS/16M al pie de la letra: al volver a modo protegido lee su bandera "había paginación"
    /// con `ds:[0DFE]` llevando todavía el DS de modo real, y sólo DESPUÉS recarga DS.
    ///
    /// Mirar sólo el VALOR no basta, y ahí se cayó el primer intento: un registro puede
    /// recargarse con el mismo selector que ya tenía, y eso también consume la caché. El kernel
    /// de un juego DOS/16M hace exactamente eso (`mov ds,dx` con el valor que DS ya llevaba) y luego lee su
    /// tabla de saltos por `ds:[9C2h]`; dando por buena la base vieja saltaba a `0018:0000`.</summary>
    static Func<ushort, uint> ResolutorConCache(Cpu c, Memory m, bool protegido) => sel =>
    {
        var cache = _basesCacheadas;
        if (cache != null && SelectorSinRecargar(c, sel) && cache.TryGetValue(sel, out uint b))
            return b;
        return protegido ? BaseDeSelectorReal(c, m, sel) : (uint)(sel << 4);
    };

    // La caché de descriptores del hardware, en el camino caliente (el porqué y lo que costaba
    // no tenerla, en Memory.GenDescriptores). Indexada por `sel >> 2`: el índice de 13 bits MÁS
    // el bit TI, que elige entre GDT y LDT; el RPL no entra en el descriptor.
    //
    // Guarda el descriptor ENTERO y no sólo la base porque el otro gran consumidor no es un
    // acceso a memoria: el discriminador del mundo mixto (EsCodigo32Pm16) mira el bit D del
    // descriptor de CS UNA VEZ POR INSTRUCCION, y eso son otras seis lecturas de la tabla.
    // Cachear solo la base dejaba fuera la mitad del gasto: medido en un juego DOS/4GW Pro, la velocidad
    // subia un 12 % en vez de lo que debia.
    static readonly uint[] _dscBase = new uint[16384];
    static readonly uint[] _dscLim = new uint[16384];
    static readonly uint[] _dscDer = new uint[16384];
    static readonly byte[] _dscFlags = new byte[16384];
    static readonly long[] _baseSelGen = new long[16384];
    const byte DscEnTabla = 1, DscPresente = 2;

    /// <summary>El descriptor de un selector, servido de la cache mientras las tablas no cambien.
    /// Las dos banderas van separadas a proposito: la BASE vale aunque el descriptor no este
    /// presente (es lo que hacia el lector de bases de siempre, y de eso dependen los juegos probados),
    /// mientras que lar/lsl y el discriminador del mundo mixto exigen el bit de presente.</summary>
    static byte DescriptorCacheado(Cpu c, Memory m, ushort sel,
                                   out uint bas, out uint lim, out uint der)
    {
        RevisaContextoDescriptores(c, m);
        int k = sel >> 2;
        if (_baseSelGen[k] == Memory.GenDescriptores)
        {
            bas = _dscBase[k]; lim = _dscLim[k]; der = _dscDer[k];
            return _dscFlags[k];
        }
        byte f = LeeDescriptorCrudo(c, m, sel, out bas, out lim, out der);
        _dscBase[k] = bas; _dscLim[k] = lim; _dscDer[k] = der;
        _dscFlags[k] = f; _baseSelGen[k] = Memory.GenDescriptores;
        return f;
    }
    static uint _ctxGdtBase, _ctxCr3;
    static ushort _ctxGdtLimit, _ctxLdtr;
    static bool _ctxPag;

    /// <summary>Invalida la caché entera si ha cambiado algo de lo que las bases dependen, y
    /// publica los rangos de la GDT/LDT para que las escrituras del invitado dentro de ellos la
    /// invaliden también. Se comprueba por COMPARACIÓN en vez de cazar cada `lgdt`/`lldt`/
    /// `mov cr3`: así un camino nuevo que toque las tablas no deja una base rancia en silencio,
    /// que es un fallo que aparecería lejísimos de su causa.</summary>
    static void RevisaContextoDescriptores(Cpu c, Memory m)
    {
        if (c.GdtBase == _ctxGdtBase && c.GdtLimit == _ctxGdtLimit && c.Ldtr == _ctxLdtr
            && c.Cr3 == _ctxCr3 && Memory.Paginacion == _ctxPag) return;
        _ctxGdtBase = c.GdtBase; _ctxGdtLimit = c.GdtLimit;
        _ctxLdtr = c.Ldtr; _ctxCr3 = c.Cr3; _ctxPag = Memory.Paginacion;
        Memory.GenDescriptores++;
        Memory.GdtIni = c.GdtBase; Memory.GdtFin = c.GdtBase + c.GdtLimit + 1u;
        Memory.LdtIni = Memory.LdtFin = 0;
        uint idxLdt = (uint)(c.Ldtr & 0xFFF8);
        if (idxLdt != 0 && idxLdt + 7 <= c.GdtLimit)
        {
            uint l = c.GdtBase + idxLdt;
            uint bas = m.RdLin16(l + 2) | ((uint)m.RdLin8(l + 4) << 16)
                       | ((uint)m.RdLin8(l + 7) << 24);
            uint lim = m.RdLin16(l);
            // Granularidad: el rango se toma por lo ALTO a propósito — pasarse invalida de más
            // (correcto y lento), quedarse corto deja pasar una escritura (incorrecto).
            if ((m.RdLin8(l + 6) & 0x80) != 0) lim = (lim << 12) | 0xFFF;
            Memory.LdtIni = bas; Memory.LdtFin = bas + lim + 1u;
        }
    }

    /// <summary>True si la base cacheada de ese selector sigue siendo la que usaría el hardware:
    /// algún registro lo lleva y NINGUNO de los que lo llevan se ha recargado.
    ///
    /// Lo segundo es lo que salva a un juego DOS/16M. Su kernel recarga SS, DS y ES con el selector 0020 y
    /// acto seguido lee su tabla de saltos por `ds:[9C2h]`, pero FS y GS **también** valen 0020 y
    /// todavía no se han recargado (los repone después). Con "basta uno sin recargar", la base
    /// vieja se colaba por la puerta de FS y el kernel saltaba a `0018:0000`.
    ///
    /// Es una aproximación —la caché de verdad va por registro, y aquí un selector compartido
    /// hace que un registro sin recargar pierda su base antes de tiempo— pero yerra siempre hacia
    /// la tabla, que es el comportamiento de siempre y el que validan todos los juegos probados.</summary>
    static bool SelectorSinRecargar(Cpu c, ushort sel)
    {
        byte r = c.SegRecargados;
        if (r == Cpu.TodosLosSegmentos) return false;   // camino normal: nada que consultar
        bool loLleva = false;
        if (c.CS == sel) { if ((r & Cpu.BitCS) != 0) return false; loLleva = true; }
        if (c.DS == sel) { if ((r & Cpu.BitDS) != 0) return false; loLleva = true; }
        if (c.ES == sel) { if ((r & Cpu.BitES) != 0) return false; loLleva = true; }
        if (c.SS == sel) { if ((r & Cpu.BitSS) != 0) return false; loLleva = true; }
        if (c.FS == sel) { if ((r & Cpu.BitFS) != 0) return false; loLleva = true; }
        if (c.GS == sel) { if ((r & Cpu.BitGS) != 0) return false; loLleva = true; }
        return loLleva;
    }

    static void CierraSalidaDePm(Cpu c, Memory m)
    {
        if (!_saliendoDePm || c.CS == _csAlSalir) return;
        _saliendoDePm = false;
        if (_destinoProtegido)
        {
            // La caché NO se tira aquí: recargar CS consuma el cambio de modo, no la base de los
            // registros de datos, que siguen con la suya hasta que cada uno se recargue (ver
            // ResolutorConCache). Se extingue sola conforme se van recargando.
            Memory.BaseDelSelector = ResolutorConCache(c, m, true);
            Memory.SegmentosSonSelectores = true;
        }
        else
        {
            // Hacia modo REAL se mantiene el comportamiento de siempre, que es el que valida la
            // todos los juegos probados: la base pasa a ser sel<<4 para todo. La caché por registro se
            // mantendría igual de bien, pero cambiar los dos lados a la vez dejaría sin red la
            // mitad que hoy funciona.
            _basesCacheadas = null;
            Memory.BaseDelSelector = null;
            Memory.SegmentosSonSelectores = false;
        }
        if (Trace) Console.Error.WriteLine(
            $"[PM16] modo {(_destinoProtegido ? "protegido" : "real")} efectivo (CS = {c.CS:X4})");
    }

    /// <summary>Base lineal de un selector, leída del descriptor real (GDT o LDT del programa).
    ///
    /// Formato de descriptor 286/386: base 0-15 en los bytes 2-3, base 16-23 en el 4, y base
    /// 24-31 en el 7 (en un 286 ese byte es reservado y vale 0, así que leerlo siempre es
    /// correcto para ambos). Un selector fuera de la tabla cae a <c>sel&lt;&lt;4</c>, que es lo mismo
    /// que ya hacía el mundo DPMI para un selector desconocido: mejor seguir con una dirección
    /// plausible que abortar en un sitio donde no se puede diagnosticar nada.</summary>
    /// <summary>Entrega la excepción 14 (fallo de página) por la IDT del programa, con CR2 puesta
    /// y el CÓDIGO DE ERROR encima del marco — que es lo que su manejador desapila antes del
    /// `iret`. El tamaño del marco lo manda el tipo de puerta, igual que en el resto del mundo
    /// protegido. `ip` sale apuntando al manejador y la instrucción que falló NO se ha
    /// consumido: al volver se repite, que es la semántica de un fallo de página del 386 y lo
    /// que espera un gestor de memoria virtual para poder traer la página y seguir.</summary>
    public static uint EntregaFalloDePagina(Cpu c, Memory m, FalloDePaginaException pf, uint ip)
        => EntregaFalloDePagina(c, m, pf, ip, out _);

    public static uint EntregaFalloDePagina(Cpu c, Memory m, FalloDePaginaException pf, uint ip,
                                            out bool porTarea)
    {
        porTarea = false;
        Memory.Cr2 = pf.Lineal;
        if ((c.Msw & 1) == 0 || c.IdtLimit < 14 * 8 + 7)
            throw new NotSupportedException(
                $"{pf.Message} y no hay IDT donde entregar el #PF (msw=0x{c.Msw:X8}, " +
                $"idt=0x{c.IdtBase:X8}+{c.IdtLimit:X4}) — CS:IP={c.CS:X4}:{ip:X4}");

        uint p = c.IdtBase + 14 * 8;
        byte attr = m.RdLin8(p + 5);
        ushort sel = m.RdLin16(p + 2);
        if ((attr & 0x80) == 0 || sel == 0)
            throw new NotSupportedException(
                $"{pf.Message} y la puerta 14 de la IDT no vale (attr=0x{attr:X2} sel={sel:X4})");

        if ((attr & 0x1F) == 0x05)
        {
            // TASK GATE: the VMM of DOS/4GW Professional handles #PF with a 386 task switch
            // (gate type 5, selector = TSS). Delivering it like an interrupt gate jumps to the
            // gate's OFFSET field, which a task gate ignores — measured on a DOS/4GW Pro game: 43BD landed
            // in zero-filled memory and the kernel derailed into its 0xCC fill at 0248:EAA0.
            porTarea = true;
            uint destinoT = ConmutaTarea(c, m, sel, ip, anidada: true);
            // The error code goes on the NEW task's stack (its handler pops it before iret),
            // and its SIZE follows the TSS type, not the stack's D bit: a 386 TSS gets a dword
            // (DOSBox-X cpu.cpp DESC_TASK_GATE: cpu_tss.is386 ? Push32 : Push16). Measured in
            // Measured on a DOS/4GW Pro game: with a word push the VMM read garbage above it
            // and reported COD=1 (protection violation), which is fatal for it, instead of the
            // real not-present read (COD=0) that its swap path handles.
            uint baseSsT = BaseDeSelectorReal(c, m, c.SS);
            c.ESP -= 4; m.WrLin32(baseSsT + c.ESP, pf.CodigoDeError);
            if (_avisosPf++ < 8 || Trace) Console.Error.WriteLine(
                $"[#PF] lineal=0x{pf.Lineal:X8} {(pf.Escritura ? "escritura" : "lectura")} " +
                $"por TAREA tss={sel:X4} -> {c.CS:X4}:{destinoT:X8}");
            return destinoT;
        }
        uint baseSs = BaseDeSelectorReal(c, m, c.SS);
        // Write-first, ESP commits last (restart rule; see Push16/32).
        if ((attr & 0x08) != 0)
        {
            uint e = c.ESP;
            m.WrLin32(baseSs + e - 4, c.EFlagsWord());
            m.WrLin32(baseSs + e - 8, c.CS);
            m.WrLin32(baseSs + e - 12, ip);
            m.WrLin32(baseSs + e - 16, pf.CodigoDeError);
            c.ESP = e - 16;
        }
        else
        {
            uint e = c.ESP;
            m.WrLin16(baseSs + e - 2, c.FlagsWord());
            m.WrLin16(baseSs + e - 4, c.CS);
            m.WrLin16(baseSs + e - 6, (ushort)ip);
            m.WrLin16(baseSs + e - 8, (ushort)pf.CodigoDeError);
            c.ESP = e - 8;
        }
        if ((attr & 1) == 0) c.IF = false;
        c.TF = false;
        c.NT = false;   // interrupt/trap gates clear NT (hardware), so a nested iret pops normally
        c.CS = sel;
        uint destino = m.RdLin16(p);
        // Sin depender de Trace: un #PF es un evento raro y decisivo, y si PakuPort inyecta uno
        // que el hardware no daría, el juego se descarrila lejos de aquí y sin rastro.
        if (_avisosPf++ < 8 || Trace) Console.Error.WriteLine(
            $"[#PF] lineal=0x{pf.Lineal:X8} {(pf.Escritura ? "escritura" : "lectura")} " +
            $"desde {c.CS:X4}:{ip:X4} -> {sel:X4}:{destino:X4} attr={attr:X2}");
        return destino;
    }

    /// <summary>Base lineal de un TSS: descriptor leído de la GDT (un TSS no puede vivir en
    /// una LDT). Exige tipo de 386 (9/B disponibles u ocupados): el de 286 tiene otro layout
    /// y aquí no hay ningún cliente medido que lo use — parar con mensaje antes que leer
    /// campos desplazados.</summary>
    static uint BaseDeTss386(Cpu c, Memory m, ushort sel)
    {
        uint d = c.GdtBase + (uint)(sel & 0xFFF8);
        byte acceso = m.RdLin8(d + 5);
        if ((acceso & 0x80) == 0 || (acceso & 0x0D) != 0x09)
            throw new NotSupportedException(
                $"task switch a TSS {sel:X4} con descriptor no-TSS-386 (acceso=0x{acceso:X2})");
        return (uint)(m.RdLin16(d + 2) | (m.RdLin8(d + 4) << 16)) | ((uint)m.RdLin8(d + 7) << 24);
    }

    /// <summary>Conmutación de tarea del 386 (task gate de la IDT, o el iret con NT=1 que
    /// deshace la anterior). Guarda el estado dinámico en el TSS de la tarea saliente (TR),
    /// carga el de la entrante, recarga CR3 (vacía el TLB: el VMM de la tarea toca tablas) y
    /// deja TODOS los registros de segmento como recargados — la caché de bases por registro
    /// no sobrevive a un cambio de tarea. `anidada`=true (entrega de una excepción por task
    /// gate) escribe el backlink en el TSS nuevo y enciende NT en la tarea entrante, que es lo
    /// que hace que su iret vuelva por aquí; `anidada`=false es esa vuelta, y limpia NT en la
    /// imagen guardada de la saliente (semántica exacta del iret del 386).</summary>
    static uint ConmutaTarea(Cpu c, Memory m, ushort nuevoTr, uint eipGuardar, bool anidada)
    {
        uint viejo = BaseDeTss386(c, m, c.TaskReg);
        uint nuevo = BaseDeTss386(c, m, nuevoTr);
        uint flagsGuardar = c.EFlagsWord();
        if (!anidada) flagsGuardar &= ~0x4000u;   // iret: NT queda apagado en la imagen saliente
        m.WrLin32(viejo + 0x20, eipGuardar); m.WrLin32(viejo + 0x24, flagsGuardar);
        m.WrLin32(viejo + 0x28, c.EAX); m.WrLin32(viejo + 0x2C, c.ECX);
        m.WrLin32(viejo + 0x30, c.EDX); m.WrLin32(viejo + 0x34, c.EBX);
        m.WrLin32(viejo + 0x38, c.ESP); m.WrLin32(viejo + 0x3C, c.EBP);
        m.WrLin32(viejo + 0x40, c.ESI); m.WrLin32(viejo + 0x44, c.EDI);
        m.WrLin32(viejo + 0x48, c.ES); m.WrLin32(viejo + 0x4C, c.CS);
        m.WrLin32(viejo + 0x50, c.SS); m.WrLin32(viejo + 0x54, c.DS);
        m.WrLin32(viejo + 0x58, c.FS); m.WrLin32(viejo + 0x5C, c.GS);
        // Bits de ocupado: la que se va deja de estarlo sólo en el iret; la que entra lo está
        // desde ya (el hardware lo exige para vetar reentradas; aquí se refleja por si el
        // kernel lee su propia GDT para saber qué tarea corre).
        if (!anidada) m.WrLin8(c.GdtBase + (uint)(c.TaskReg & 0xFFF8) + 5,
                               (byte)(m.RdLin8(c.GdtBase + (uint)(c.TaskReg & 0xFFF8) + 5) & ~0x02));
        m.WrLin8(c.GdtBase + (uint)(nuevoTr & 0xFFF8) + 5,
                 (byte)(m.RdLin8(c.GdtBase + (uint)(nuevoTr & 0xFFF8) + 5) | 0x02));
        if (anidada) m.WrLin32(nuevo, c.TaskReg);   // backlink de la tarea entrante
        uint cr3Nuevo = m.RdLin32(nuevo + 0x1C);
        uint eip = m.RdLin32(nuevo + 0x20);
        uint flags = m.RdLin32(nuevo + 0x24);
        if (anidada) flags |= 0x4000;   // la tarea entrante nace con NT=1: su iret vuelve
        c.EAX = m.RdLin32(nuevo + 0x28); c.ECX = m.RdLin32(nuevo + 0x2C);
        c.EDX = m.RdLin32(nuevo + 0x30); c.EBX = m.RdLin32(nuevo + 0x34);
        c.ESP = m.RdLin32(nuevo + 0x38); c.EBP = m.RdLin32(nuevo + 0x3C);
        c.ESI = m.RdLin32(nuevo + 0x40); c.EDI = m.RdLin32(nuevo + 0x44);
        c.ES = (ushort)m.RdLin32(nuevo + 0x48); c.CS = (ushort)m.RdLin32(nuevo + 0x4C);
        c.SS = (ushort)m.RdLin32(nuevo + 0x50); c.DS = (ushort)m.RdLin32(nuevo + 0x54);
        c.FS = (ushort)m.RdLin32(nuevo + 0x58); c.GS = (ushort)m.RdLin32(nuevo + 0x5C);
        c.Ldtr = (ushort)m.RdLin32(nuevo + 0x60);
        c.SetEFlagsWord(flags);   // NT (bit 14) viaja dentro: SetFlagsWord conserva IOPL+NT
        c.TaskReg = nuevoTr;
        c.SegRecargados = Cpu.TodosLosSegmentos;
        if (c.Cr3 != cr3Nuevo)
        {
            c.Cr3 = cr3Nuevo;
            Memory.ConfiguraPaginacion(Memory.Paginacion, cr3Nuevo);
        }
        if (Trace) Console.Error.WriteLine(
            $"[TAREA] {(anidada ? "entra" : "vuelve a")} tss={nuevoTr:X4} cs:eip={c.CS:X4}:{eip:X8} " +
            $"ss:esp={c.SS:X4}:{c.ESP:X8}");
        return eip;
    }

    /// <summary>El iret de una tarea anidada (NT=1): conmuta de vuelta a la tarea del backlink.
    /// `eipTrasIret` es la instrucción que sigue al iret, que es donde la tarea del manejador
    /// reanudará la próxima vez que su task gate la despierte.</summary>
    public static uint IretDeTarea(Cpu c, Memory m, uint eipTrasIret)
    {
        ushort backlink = m.RdLin16(BaseDeTss386(c, m, c.TaskReg));
        return ConmutaTarea(c, m, backlink, eipTrasIret, anidada: false);
    }

    /// <summary>True si el invitado está en modo protegido de 16 bits con una PILA GRANDE
    /// (descriptor de SS con B=1 o ESP por encima de 64K). En ese estado FireInterrupt NO
    /// puede entregar una IRQ: empuja el marco por el SP de 16 bits y lo escribe base+SP,
    /// hasta 1,75 MB por debajo de la pila real — medido en un juego DOS/16M: el marco caía sobre los
    /// datos bajos del kernel y el IRET de la ISR desapilaba banderas de basura (un CF=1
    /// heredado convertía en "fallo" un alloc que había ido bien). La entrega correcta la
    /// hace el bucle del mundo mixto (Pm16MundoMixto), con ESP entero.</summary>
    public static bool PilaGrandePm16(Cpu c, Memory m)
    {
        if ((c.Msw & 1) == 0 || !Memory.SegmentosSonSelectores) return false;
        // El ancho real lo dice el bit B del descriptor de SS (PilaEs32) — la heurística
        // "ESP > 0xFFFF" mentía en cuanto la pila pasó a empujarse por SP con la mitad alta
        // de ESP arrastrada del mundo plano: TODO parecía pila grande, FireInterrupt quedaba
        // vetado para siempre y las IRQ no entraban nunca en los bucles de 16 bits del kernel
        // (medido en un juego DOS/4GW Pro: 131 tics de reloj en 120 s con el PIT a 200 Hz, juego esperando
        // su tick eternamente en pantalla negra).
        return PilaEs32(c);
    }

    /// <summary>Límite y derechos de un selector leídos del descriptor REAL del programa
    /// (GDT o LDT), para `lar`/`lsl` en modo protegido de 16 bits. Devuelve false si el
    /// selector es nulo, se sale de la tabla o el descriptor no está presente — que es lo
    /// que en el hardware deja ZF=0. Los derechos van en el formato de LAR: byte de acceso
    /// en los bits 8-15 y el nibble alto del byte 6 (G/D/AVL) en los bits 20-23.</summary>
    static bool DescriptorReal(Cpu c, Memory m, ushort sel, out uint lim, out uint der)
        => (DescriptorCacheado(c, m, sel, out _, out lim, out der) & DscPresente) != 0;

    /// <summary>El lector crudo de la tabla, que es lo que la cache memoiza. Devuelve las dos
    /// banderas de DescriptorCacheado; bas/lim/der solo valen con EnTabla.</summary>
    static byte LeeDescriptorCrudo(Cpu c, Memory m, ushort sel,
                                   out uint bas, out uint lim, out uint der)
    {
        bas = 0; lim = 0; der = 0;
        uint tablaBase = c.GdtBase, tablaLimite = c.GdtLimit;
        if ((sel & 4) != 0)
        {
            // La LDT es a su vez un descriptor DENTRO de la GDT: su base sale de ahi.
            uint linLdt = c.GdtBase + (uint)(c.Ldtr & 0xFFF8);
            tablaBase = m.RdLin16(linLdt + 2)
                        | ((uint)m.RdLin8(linLdt + 4) << 16)
                        | ((uint)m.RdLin8(linLdt + 7) << 24);
            tablaLimite = m.RdLin16(linLdt);
        }
        uint idx = (uint)(sel & 0xFFF8);
        if (idx == 0 || idx + 7 > tablaLimite) return 0;
        uint lin = tablaBase + idx;
        bas = m.RdLin16(lin + 2) | ((uint)m.RdLin8(lin + 4) << 16)
              | ((uint)m.RdLin8(lin + 7) << 24);
        byte acceso = m.RdLin8(lin + 5);
        if ((acceso & 0x80) == 0) return DscEnTabla;   // sin bit de presente
        byte gran = m.RdLin8(lin + 6);
        lim = (uint)(m.RdLin16(lin) | ((gran & 0x0F) << 16));
        if ((gran & 0x80) != 0) lim = (lim << 12) | 0xFFF;   // granularidad de pagina
        der = ((uint)acceso << 8) | ((uint)(gran & 0xF0) << 16);
        return DscEnTabla | DscPresente;
    }

    /// <summary>EIP completo pendiente de consumir en la cesión al mundo de 32 bits: lo deja el
    /// `iretd` de 16 bits cuando su destino es un CS de código de 32 bits (el offset no cabe en
    /// `ip`). Ver la cesión en la cabecera del bucle de <see cref="Run"/>.</summary>
    static uint _eip32Pendiente;

    /// <summary>PAKU_SIN_ATAJO_DOS=1: devuelve el `int 21h AH=2Ch` del mundo mixto al kernel del
    /// extensor, que es como estaba antes. Es el CONTROL del A/B — ver el atajo en
    /// <see cref="Pm16MundoMixto"/>.</summary>
    public static readonly bool SinAtajoDos =
        Environment.GetEnvironmentVariable("PAKU_SIN_ATAJO_DOS") == "1";

    /// <summary>True si el selector es un segmento de CÓDIGO con el bit D puesto (opcodes de 32
    /// bits), leído del descriptor real del programa. Es el discriminador del mundo mixto de
    /// DOS/16M: decide qué intérprete decodifica cada tramo.</summary>
    static bool EsCodigo32Pm16(Cpu c, Memory m, ushort sel)
        => DescriptorReal(c, m, sel, out _, out uint der)
           && (der & 0x1800) == 0x1800     // S=1 y ejecutable
           && (der & 0x00400000) != 0;     // bit D del byte 6: código de 32 bits

    /// <summary>El bucle del MUNDO MIXTO de DOS/16M: corre el código de 32 bits del programa con
    /// Interp32 (bases y descriptores leídos de las tablas REALES del invitado) y devuelve el
    /// control a Interp cuando el flujo vuelve a un segmento de 16 bits — por transferencia far
    /// (Fin.CambioA16) o porque un INT vectoriza a una puerta del kernel de 16 bits, en cuyo caso
    /// el marco se empuja aquí con el tamaño que manda el TIPO de puerta (misma regla, medida en
    /// un juego DOS/16M, que el case Int de 16 bits y FireInterrupt). Las IRQ se sirven en el límite de cada
    /// trozo con la maquinaria de siempre (FireInterrupt vectoriza por la IDT y la ISR de 16 bits
    /// corre anidada).</summary>
    static (bool seguir, ushort ip) Pm16MundoMixto(Cpu c, Memory m, Platform.DosPlatform dp, uint eip)
    {
        Interp32.BaseDeSelector = sel => BaseDeSelectorReal(c, m, sel);
        Interp32.OnDescriptor = (ushort s, out uint lim, out uint der, out bool esc) =>
        {
            bool ok = DescriptorReal(c, m, s, out lim, out der);
            esc = ok && (der & 0x0800) == 0 && (der & 0x0200) != 0;   // dato escribible
            return ok;
        };
        Interp32.OnIrq = null;   // las IRQ se sirven en el límite del trozo, no en-interp32
        uint trasInt = 0; bool intVisto = false;
        Interp32.OnInt = (byte no, Cpu c2, Memory m2, ref uint tras) =>
        {
            // LA HORA SE SIRVE AQUI, SIN BAJAR AL KERNEL DEL EXTENSOR.
            //
            // Medido en un juego DOS/4GW Pro: hace un busy-wait de sincronismo que pregunta la
            // hora 20.000 VECES POR SEGUNDO (299.730 de cada 300.000 interrupciones que salen de
            // su codigo son `int 21h AH=2Ch`). Cada una la reflejaba el kernel de DOS/4GW Pro, y
            // eso son ~544 instrucciones nuestras CON IF=0, porque la puerta de la IDT es una
            // interrupt gate. 20.000 x 544 = 10,9 M instr/s, que era exactamente toda la maquina:
            // el 93 % de las instrucciones se iban al kernel y el 97 % del tiempo transcurria con
            // las interrupciones cerradas, asi que el tick del PIT que el juego estaba esperando
            // no podia entrar — y sin ese tick el bucle daba otra vuelta. Un circulo cerrado: de
            // los 200 Hz que pide su PIT llegaban 13, y acelerar la CPU x2,6 no movio la aguja.
            //
            // Por que es legitimo: AH=2Ch no toca memoria del invitado ni lleva punteros que
            // traducir — devuelve la hora en CH/CL/DH/DL y nada mas. Lo que el juego observa es
            // identico; lo unico que no ocurre es el viaje al kernel, que para esta llamada no
            // aporta nada. En el original ese viaje tambien existe, pero alli cuesta microsegundos
            // de un 486 y aqui se come la maquina entera.
            //
            // PAKU_SIN_ATAJO_DOS=1 lo apaga: es el control del A/B, y la unica forma de volver a
            // medir el comportamiento viejo en las mismas condiciones.
            if (no == 0x21 && c2.AH == 0x2C && !SinAtajoDos)
            { dp.Int(0x21, c2, m2); return true; }
            trasInt = tras; intVisto = true; return false;
        };
        Interp32.CsSigue32 = sel => EsCodigo32Pm16(c, m, sel);
        // La red "más pops que pushes" ancla el tope al PRIMER ESP visto — válido en el mundo
        // LE, donde la pila es una; aquí el programa cambia de pila con `lss` (medido en
        // en un juego DOS/16M: de 0x7102 del runtime a 0x1C3070 del juego) y el ancla vieja da falso rojo.
        Interp32.TopeDePila = uint.MaxValue;
        // Pila de 16 bits (B=0): el mundo de 16 sólo mueve SP y la mitad alta de ESP puede
        // traer basura de otra época; Interp32 empuja por ESP entero.
        if (DescriptorReal(c, m, c.SS, out _, out uint derSs) && (derSs & 0x00400000) == 0)
            c.ESP = c.SP;
        if (Trace) Console.Error.WriteLine(
            $"[PM16] mundo MIXTO -> 32 bits: CS={c.CS:X4} EIP=0x{eip:X8} SS={c.SS:X4} ESP=0x{c.ESP:X8}");
        while (true)
        {
            intVisto = false;
            var res = Interp32.Run(c, m, eip, 16384);
            c.OnPeriodicPoll?.Invoke(c);
            switch (res.Motivo)
            {
                case Interp32.Fin.Presupuesto:
                {
                    eip = res.EipOffset;
                    if (!c.Step()) return (false, 0);
                    // IRQs del mundo mixto: como el hardware — marco en SS:ESP (¡ESP entero!)
                    // por la puerta de la IDT; la ISR de 16 bits corre en Interp y su iretd
                    // vuelve aquí por la cesión de la cabecera del bucle. FireInterrupt NO vale
                    // en este mundo: empuja por el SP de 16 bits, y con la pila del juego en
                    // ESP=0x001C2Fxx escribía el marco 1,75 MB por debajo de la pila real,
                    // pisando los datos bajos del kernel — fallos intermitentes que se movían
                    // con el timing de las IRQ, medidos en un juego DOS/16M con y sin traza.
                    if (c.IF && dp.Pic.AckNext(out int irqMix))
                    {
                        byte vecIrq = dp.Pic.IrqToVector(irqMix);
                        if (irqMix == 0) Platform.DosPlatform.DiagIrq0++;
                        else if (irqMix == 1) Platform.DosPlatform.DiagIrq1++;
                        if (c.IdtLimit < (uint)(vecIrq * 8 + 7))
                            continue;   // sin puerta: se pierde, como una IRQ sin handler
                        uint pI = c.IdtBase + (uint)(vecIrq * 8);
                        byte atI = m.RdLin8(pI + 5);
                        ushort selI = m.RdLin16(pI + 2);
                        if ((atI & 0x80) == 0 || selI == 0) continue;
                        uint baseSsI = BaseDeSelectorReal(c, m, c.SS);
                        // Write-first, ESP commits last: a #PF halfway through the frame must
                        // not leave ESP moved (same restart rule as Push16/32).
                        if ((atI & 0x08) != 0)
                        {
                            uint e = c.ESP;
                            m.WrLin32(baseSsI + e - 4, c.EFlagsWord());
                            m.WrLin32(baseSsI + e - 8, c.CS);
                            m.WrLin32(baseSsI + e - 12, eip);
                            c.ESP = e - 12;
                        }
                        else
                        {
                            uint e = c.ESP;
                            m.WrLin16(baseSsI + e - 2, c.FlagsWord());
                            m.WrLin16(baseSsI + e - 4, (ushort)c.CS);
                            m.WrLin16(baseSsI + e - 6, (ushort)eip);
                            c.ESP = e - 6;
                        }
                        c.IF = false; c.TF = false; c.NT = false;
                        c.CS = selI;
                        ushort offI = m.RdLin16(pI);
                        if (Trace) Console.Error.WriteLine(
                            $"[PM16] irq{irqMix} (32b) via IDT -> {selI:X4}:{offI:X4}");
                        if (EsCodigo32Pm16(c, m, selI)) { eip = offI; continue; }
                        return (true, offI);
                    }
                    continue;
                }

                case Interp32.Fin.CambioA16:
                    if (Trace) Console.Error.WriteLine(
                        $"[PM16] mundo MIXTO -> 16 bits: CS={c.CS:X4} IP=0x{res.EipOffset:X4}");
                    return (true, (ushort)res.EipOffset);

                case Interp32.Fin.IntSinServicio:
                {
                    // IRQ pendiente ANTES del INT software: el bucle de espera de un juego DOS/4GW Pro
                    // hace `int 21h AH=2Ch` cada ~100 instrucciones y el presupuesto de 16384
                    // no vence NUNCA — las IRQ del PIT no entraban (4,7/s con el PIT a 200 Hz
                    // y el juego esperando su tick para siempre). Como el hardware: la IRQ se
                    // sirve en la frontera de instrucción, con la reanudación EN el propio
                    // `int`, que se re-ejecuta al volver la ISR.
                    if (c.IF && dp.Pic.AckNext(out int irqPre))
                    {
                        byte vecPre = dp.Pic.IrqToVector(irqPre);
                        if (irqPre == 0) Platform.DosPlatform.DiagIrq0++;
                        else if (irqPre == 1) Platform.DosPlatform.DiagIrq1++;
                        if (c.IdtLimit >= (uint)(vecPre * 8 + 7))
                        {
                            uint pPre = c.IdtBase + (uint)(vecPre * 8);
                            byte atPre = m.RdLin8(pPre + 5);
                            ushort selPre = m.RdLin16(pPre + 2);
                            if ((atPre & 0x80) != 0 && selPre != 0)
                            {
                                uint baseSsPre = BaseDeSelectorReal(c, m, c.SS);
                                uint reanuda = res.EipOffset;   // la instrucción `int`, sin consumir
                                if ((atPre & 0x08) != 0)
                                {
                                    uint e = c.ESP;
                                    m.WrLin32(baseSsPre + e - 4, c.EFlagsWord());
                                    m.WrLin32(baseSsPre + e - 8, c.CS);
                                    m.WrLin32(baseSsPre + e - 12, reanuda);
                                    c.ESP = e - 12;
                                }
                                else
                                {
                                    uint e = c.ESP;
                                    m.WrLin16(baseSsPre + e - 2, c.FlagsWord());
                                    m.WrLin16(baseSsPre + e - 4, (ushort)c.CS);
                                    m.WrLin16(baseSsPre + e - 6, (ushort)reanuda);
                                    c.ESP = e - 6;
                                }
                                c.IF = false; c.TF = false; c.NT = false;
                                c.CS = selPre;
                                ushort offPre = m.RdLin16(pPre);
                                if (Trace) Console.Error.WriteLine(
                                    $"[PM16] irq{irqPre} (pre-int) via IDT -> {selPre:X4}:{offPre:X4}");
                                if (EsCodigo32Pm16(c, m, selPre)) { eip = offPre; continue; }
                                return (true, offPre);
                            }
                        }
                    }
                    byte nInt = res.IntNo;
                    uint tras = intVisto ? trasInt : res.EipOffset;
                    if (c.IdtLimit < (uint)(nInt * 8 + 7))
                        throw new NotSupportedException(
                            $"pm16-mixto: INT {nInt:X2} fuera de la IDT (limite 0x{c.IdtLimit:X4})");
                    uint pG = c.IdtBase + (uint)(nInt * 8);
                    byte atG = m.RdLin8(pG + 5);
                    ushort selG = m.RdLin16(pG + 2);
                    if ((atG & 0x80) == 0 || selG == 0)
                        throw new NotSupportedException(
                            $"pm16-mixto: INT {nInt:X2} sin puerta valida (attr=0x{atG:X2} sel={selG:X4})");
                    uint baseSs = BaseDeSelectorReal(c, m, c.SS);
                    // Write-first, ESP commits last (restart rule; see the IRQ frame above).
                    if ((atG & 0x08) != 0)
                    {
                        uint e = c.ESP;
                        m.WrLin32(baseSs + e - 4, c.EFlagsWord());
                        m.WrLin32(baseSs + e - 8, c.CS);
                        m.WrLin32(baseSs + e - 12, tras);
                        c.ESP = e - 12;
                    }
                    else
                    {
                        uint e = c.ESP;
                        m.WrLin16(baseSs + e - 2, c.FlagsWord());
                        m.WrLin16(baseSs + e - 4, c.CS);
                        m.WrLin16(baseSs + e - 6, (ushort)tras);
                        c.ESP = e - 6;
                    }
                    bool ifAntesG = c.IF;
                    if ((atG & 1) == 0) c.IF = false;
                    c.TF = false; c.NT = false;
                    c.CS = selG;
                    ushort offG = m.RdLin16(pG);
                    if (Trace) Console.Error.WriteLine(
                        $"[PM16] int {nInt:X2} (32b) via IDT -> {selG:X4}:{offG:X4} attr={atG:X2} " +
                        $"AX={c.AX:X4} BX={c.BX:X4} CX={c.CX:X4} DX={c.DX:X4} EDX={c.EDX:X8} " +
                        $"SS:ESP={c.SS:X4}:{c.ESP:X8} tras=0x{tras:X8} IF={(ifAntesG ? 1 : 0)}");
                    if (EsCodigo32Pm16(c, m, selG)) { eip = offG; continue; }
                    return (true, offG);
                }

                default:
                    throw new NotSupportedException($"pm16-mixto: el mundo de 32 bits se detuvo: {res}");
            }
        }
    }

    /// <summary>Base lineal de un selector leida de la tabla REAL del programa (por la cache de
    /// descriptores). Un selector nulo o fuera de la tabla vale sel*16, o sea lo mismo que un
    /// segmento de modo real: los dos mundos comparten memoria y esa es la salida que esperan los
    /// juegos probados. La base vale aunque el descriptor no este presente: eso es cosa de quien pida
    /// permisos, no de quien direcciona.</summary>
    static uint BaseDeSelectorReal(Cpu c, Memory m, ushort sel)
        => (DescriptorCacheado(c, m, sel, out uint bas, out _, out _) & DscEnTabla) != 0
           ? bas : (uint)(sel << 4);

    static void FpuStore(Cpu c, Memory m, in Instruction insn, bool pop)
    {
        var f = c.Fpu;
        if (insn.Op0Kind == OpKind.Register)
        {
            f.SetSt(StIdx(insn.Op0Register), f.St(0));
        }
        else
        {
            ushort seg = MemSeg(c, in insn), ea = MemEa(c, in insn);
            double v = f.St(0);
            switch (insn.MemorySize)
            {
                case MemorySize.Float32: m.WrF32(seg, ea, (float)v); break;
                case MemorySize.Float64: m.WrF64(seg, ea, v); break;
                case MemorySize.Float80:
                {
                    var (mant, se) = Fpu.ToF80(v);
                    m.WrU32(seg, ea, (uint)mant);
                    m.WrU32(seg, (ushort)(ea + 4), (uint)(mant >> 32));
                    m.WrU16(seg, (ushort)(ea + 8), se);
                    break;
                }
                case MemorySize.Int16: m.WrU16(seg, ea, (ushort)Fpu.ToInt(v)); break;
                case MemorySize.Int32: m.WrU32(seg, ea, (uint)Fpu.ToInt(v)); break;
                // fistp qword (df /7): the 64-bit store Interp32 already had and this decoder
                // did not. A test game dies on it at 0824:14F7 (bytes DF7EF6) before drawing.
                case MemorySize.Int64:
                {
                    long i64 = Fpu.ToInt(v);
                    m.WrU32(seg, ea, (uint)i64);
                    m.WrU32(seg, (ushort)(ea + 4), (uint)((ulong)i64 >> 32));
                    break;
                }
                default: throw new NotSupportedException($"interp: x87 store {insn.MemorySize} ({insn.Mnemonic})");
            }
        }
        if (pop) f.Pop();
    }

    static void FpuCompare(Cpu c, Memory m, in Instruction insn, int pops)
    {
        var f = c.Fpu;
        double val = insn.OpCount == 0 ? f.St(1)
            : insn.Op0Kind == OpKind.Register ? f.St(StIdx(insn.Op0Register))
            : FpuMemFloat(c, m, in insn);
        f.Compare(f.St(0), val);
        for (int i = 0; i < pops; i++) f.Pop();
    }

    static double FpuApply(double a, char op, double b) => op switch
    {
        '+' => a + b, '-' => a - b, '*' => a * b, _ => a / b,
    };

    static void FpuArith(Cpu c, Memory m, in Instruction insn, char op, bool rev, bool pop)
    {
        var f = c.Fpu;
        // forma memoria: st(0) op= mem (sin pop). forma st: dst op= src.
        if (insn.OpCount <= 1 || insn.Op0Kind == OpKind.Memory)
        {
            double mem = FpuMemFloat(c, m, in insn);
            f.SetSt(0, rev ? FpuApply(mem, op, f.St(0)) : FpuApply(f.St(0), op, mem));
            return;
        }
        int d = StIdx(insn.Op0Register), s = StIdx(insn.Op1Register);
        double res = rev ? FpuApply(f.St(s), op, f.St(d)) : FpuApply(f.St(d), op, f.St(s));
        f.SetSt(d, res);
        if (pop) f.Pop();
    }

    static void FpuIArith(Cpu c, Memory m, in Instruction insn, char op, bool rev)
    {
        var f = c.Fpu;
        double mem = FpuMemInt(c, m, in insn);
        f.SetSt(0, rev ? FpuApply(mem, op, f.St(0)) : FpuApply(f.St(0), op, mem));
    }

    /// <summary>Un salto TOMADO hacia atras: la cabecera de un bucle. Se le pasa al grabador de
    /// corpus, que decide si merece casos. Apagado es la lectura de un bool.</summary>
    static void NotaAtras(Cpu c, in Instruction insn, ushort ip)
    {
        if (CorpusRecorder.Activo && insn.NearBranchTarget < ip)
            CorpusRecorder.NotaBucle(c, c.CS, (ushort)insn.NearBranchTarget, ip);
    }

    static bool TryCond(Cpu c, Mnemonic m, out bool taken)
    {
        switch (m)
        {
            case Mnemonic.Je: taken = c.ZF; return true;
            case Mnemonic.Jne: taken = !c.ZF; return true;
            case Mnemonic.Jb: taken = c.CF; return true;
            case Mnemonic.Jae: taken = !c.CF; return true;
            case Mnemonic.Jbe: taken = c.CF || c.ZF; return true;
            case Mnemonic.Ja: taken = !c.CF && !c.ZF; return true;
            case Mnemonic.Jl: taken = c.SF != c.OF; return true;
            case Mnemonic.Jge: taken = c.SF == c.OF; return true;
            case Mnemonic.Jle: taken = c.ZF || (c.SF != c.OF); return true;
            case Mnemonic.Jg: taken = !c.ZF && (c.SF == c.OF); return true;
            case Mnemonic.Js: taken = c.SF; return true;
            case Mnemonic.Jns: taken = !c.SF; return true;
            case Mnemonic.Jo: taken = c.OF; return true;
            case Mnemonic.Jno: taken = !c.OF; return true;
            case Mnemonic.Jp: taken = c.PF; return true;
            case Mnemonic.Jnp: taken = !c.PF; return true;
            default: taken = false; return false;
        }
    }

    static bool TrySetcc(Cpu c, Mnemonic m, out bool val)
    {
        switch (m)
        {
            case Mnemonic.Seta: val = !c.CF && !c.ZF; return true;
            case Mnemonic.Setae: val = !c.CF; return true;
            case Mnemonic.Setb: val = c.CF; return true;
            case Mnemonic.Setbe: val = c.CF || c.ZF; return true;
            case Mnemonic.Sete: val = c.ZF; return true;
            case Mnemonic.Setne: val = !c.ZF; return true;
            case Mnemonic.Setg: val = !c.ZF && (c.SF == c.OF); return true;
            case Mnemonic.Setge: val = c.SF == c.OF; return true;
            case Mnemonic.Setl: val = c.SF != c.OF; return true;
            case Mnemonic.Setle: val = c.ZF || (c.SF != c.OF); return true;
            case Mnemonic.Sets: val = c.SF; return true;
            case Mnemonic.Setns: val = !c.SF; return true;
            case Mnemonic.Seto: val = c.OF; return true;
            case Mnemonic.Setno: val = !c.OF; return true;
            case Mnemonic.Setp: val = c.PF; return true;
            case Mnemonic.Setnp: val = !c.PF; return true;
            default: val = false; return false;
        }
    }
}
