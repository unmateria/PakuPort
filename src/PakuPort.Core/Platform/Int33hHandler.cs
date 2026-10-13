namespace PakuPort.Core.Platform;

/// <summary>
/// INT 33h — driver de ratón DOS.
/// El host de render alimenta posición y botones; las funciones traducidas consultan por INT 33h.
///
/// Funciones soportadas:
///   AX=0000 Reset/check installed
///   AX=0001 Show cursor (no-op: dibuja el juego)
///   AX=0002 Hide cursor (no-op)
///   AX=0003 Get position and button status
///   AX=0004 Set position
///   AX=0005 Get button press information
///   AX=0006 Get button release information
///   AX=0007 Set horizontal range
///   AX=0008 Set vertical range
///   AX=0009 Set graphics cursor shape (stub)
///   AX=000A Set text cursor type (stub)
///   AX=000B Read motion counters (mickeys since last call)
///   AX=000C Set user-defined interrupt subroutine
///   AX=000F Set mickey/pixel ratio
///   AX=0010 Set mouse exclusion area (stub)
///   AX=0015 Get driver storage requirements
///   AX=001A Set mouse sensitivity
///   AX=001C Set interrupt rate (stub)
///
/// Adaptado de dosbox-x src/ints/mouse.cpp — Mouse_Handler.
/// </summary>
public sealed class Int33hHandler
{
    // ── Estado del ratón ──────────────────────────────────────────────────────────────────

    /// <summary>Posición X actual del cursor (unidades virtuales).</summary>
    /// <summary>Escala horizontal del espacio VIRTUAL del driver (contrato Microsoft: en modos
    /// de 320 de ancho el driver reporta X en 0..639, el doble del píxel; los juegos hacen
    /// <c>shr cx,1</c>). El host la fija a 2 cuando el framebuffer es &lt;640 de ancho.
    /// MouseX/MouseY se guardan SIEMPRE en píxeles de juego; la escala se aplica al exponer
    /// (callback, AH=03/05/06) y se deshace al recibir (AH=04).</summary>
    public ushort XScale = 1;

    /// <summary>Puerto de <c>Mouse_AfterNewVideoMode</c> (DOSBox-X mouse.cpp:1448-1555): el driver
    /// real reajusta su RANGO al cambiar de modo de vídeo. Nacía horneado a 639x199 y ahí se
    /// quedaba, así que un juego que salta a 640x480 y consulta el rango por AX=26/31 movía
    /// su cursor sólo por la franja superior de la pantalla — el mismo síntoma que DOSBox comenta
    /// en mouse.cpp:1461 ("the cursor is constrained to the upper left hand quadrant").
    /// NO se toca <see cref="XScale"/>: aquí la posición se guarda en píxeles del juego y el
    /// contrato virtual-640 lo resuelve el mundo del export (ver host), no el modo.</summary>
    public void OnVideoModeChanged(byte mode)
    {
        _videoMode = mode;
        _minX = 0; _minY = 0; _maxX = 639; _maxY = 479;
        _physMaxX = 639; _physMaxY = 479;
        _rangeSetX = false; _rangeSetY = false;
        _escalaVirtX = 1.0; _escalaVirtY = 1.0;   // el espacio se vuelve a declarar en el modo nuevo
        switch (mode)
        {
            case 0x00: case 0x01:      _maxX = 40 * 8 - 1; _maxY = 199; _physMaxX = 319; _physMaxY = 199; break;
            case 0x02: case 0x03:
            case 0x07:                 _maxY = 199; _physMaxX = 719; _physMaxY = 399; break;
            case 0x04: case 0x05:
            case 0x08: case 0x09:
            case 0x0D: case 0x13:      _maxY = 199; _physMaxX = 319; _physMaxY = 199; break;
            case 0x06: case 0x0A:
            case 0x0E:                 _maxY = 199; _physMaxY = 199; break;
            case 0x0F: case 0x10:      _maxY = 349; _physMaxY = 349; break;
            case 0x11: case 0x12:      _maxY = 479; break;
            default:                   break;   // SVGA/desconocido: 639x479
        }
    }

    private byte _videoMode = 0x03;   // último modo visto, para rehacer el rango en el reset

    /// <summary>Deja el cursor en el CENTRO, como hace el reset de un driver real
    /// (DOSBox-X mouse.cpp <c>Mouse_Reset</c>):
    /// <code>mouse.x = (mouse.max_x + 1) / 2;   mouse.y = (mouse.max_y + 1) / 2;</code>
    ///
    /// Por qué importa: hay juegos que AUTODETECTAN en qué unidades habla el driver leyendo la
    /// posición justo después del reset. Un juego de prueba (offset <c>0x2cb65</c> de su EXE, bytes verificados)
    /// compara con 160 —el centro de una pantalla de 320— y si no coincide concluye que el
    /// driver reporta en el espacio virtual-640; a partir de ahí divide entre dos todo lo que
    /// recibe. Con el cursor en 0,0 tras el reset la deducción sale al revés de lo que el
    /// driver hace de verdad y el cursor sólo alcanza media pantalla ("no llega al lado
    /// derecho", según una prueba manual).
    ///
    /// Aquí la posición se guarda SIEMPRE en píxeles de juego y el contrato virtual lo aplica
    /// <see cref="ExposeX"/> con <see cref="XScale"/>, así que el centro se calcula sobre la
    /// pantalla FÍSICA: en modo 13h son 160,100, que con XScale=2 se exponen como los 320,100
    /// que mide el oráculo (verdad terreno byte-exacta en <c>tools/mouse_center_test</c>).</summary>
    private void CenterCursor()
    {
        MouseX = (ushort)((_physMaxX + 1) / 2);
        MouseY = (ushort)((_physMaxY + 1) / 2);
    }

    public ushort MouseX;
    /// <summary>Posición Y actual del cursor (unidades virtuales).</summary>
    public ushort MouseY;
    /// <summary>Estado de botones: bit 0=izquierdo, bit 1=derecho, bit 2=central.</summary>
    public ushort Buttons;

    // Contadores de movimiento acumulado en mickeys (se resetean al leer con AX=000B)
    private short _mickeyX;
    private short _mickeyY;

    // Contadores de presión de botones (AX=0005) y liberación (AX=0006)
    // Índice: 0=izquierdo, 1=derecho, 2=central
    private readonly ushort[] _pressCount   = new ushort[3];
    private readonly ushort[] _releaseCount = new ushort[3];
    private readonly ushort[] _pressX       = new ushort[3];
    private readonly ushort[] _pressY       = new ushort[3];
    private readonly ushort[] _releaseX     = new ushort[3];
    private readonly ushort[] _releaseY     = new ushort[3];

    // ── Callback de usuario (AX=000C) ─────────────────────────────────────────────────────
    // Microsoft mouse driver: el juego instala una rutina far (ES:DX) con máscara de eventos
    // (CX): bit0=movimiento, bit1/2=izq press/release, bit3/4=dcha press/release,
    // bit5/6=central press/release. El driver la llama en contexto de interrupción con
    // AX=eventos, BX=botones, CX=x, DX=y, SI/DI=mickeys, salvando TODOS los registros.
    // Hay juegos que actualizan su posición SOLO por este callback (una única
    // llamada AH=03 en el arranque, cero polling en el bucle): sin despacharlo, ratón muerto.
    // El host produce eventos (SetPosition/SetButtons, hilo de render); el hilo del juego los
    // entrega en el límite de frame (DosPlatform.ServicePendingInterrupts), el mismo punto
    // seguro donde se sirven las IRQs. Adaptado de dosbox-x mouse.cpp (Mouse_AddEvent/EventHandler).
    private ushort _cbMask;
    private ushort _cbSeg;
    private ushort _cbOff;

    /// <summary>El MUNDO en el que se registró el callback (true = modo protegido de 16 bits).
    /// Un `seg:off` sólo significa lo mismo en el modo en que se dio: en modo real `0C5D` es un
    /// párrafo y en protegido es un SELECTOR, y la diferencia entre las dos lecturas son
    /// megabytes. Ver la comprobación en <see cref="ServiceCallback"/>.</summary>
    private bool _cbPm;
    private long _cbDiferidos;
    private ushort _cbSs, _cbSp;
    /// <summary>PAKU_CRUCE_CB=1: intenta CRUZAR a modo real para llamar al trampolin de un
    /// extensor (ver ServiceCallback). Apagado por defecto porque esta A MEDIO CAMINO: con el
    /// cruce el trampolin de un juego DOS/4GW Pro llega hasta su `ljmp 0070:4F11` y se descarrila dentro
    /// del kernel (3E58:0104), porque le estamos fabricando un contexto de modo real que no es
    /// el suyo. Se conserva encendible para seguir por ahi con la medida hecha.</summary>
    static readonly bool _cruceCb =
        Environment.GetEnvironmentVariable("PAKU_CRUCE_CB") == "1";

    /// <summary>PAKU_CB_IRET=1: llamar al trampolin del extensor con marco de INTERRUPCION (tres
    /// words) e IsrMode, en vez del far call de dos words de siempre. Es la pieza que falta para
    /// que el trampolin termine su trabajo —su codigo acaba en `iret`— pero TAL CUAL rompe: con
    /// ella el trampolin avanza de verdad (ya no se para a 34 instrucciones) y el juego muere en
    /// 0F34:0B78. Ojo al desensamblado: en 204B hay un `iret` y en 204C un `retf`, o sea que el
    /// trampolin tiene DOS salidas y probablemente dos contratos de entrada; falta ver contra el
    /// oraculo cual usa el driver de raton de DOSBox y con que pila.</summary>
    static readonly bool _cbComoIret =
        Environment.GetEnvironmentVariable("PAKU_CB_IRET") == "1";

    /// <summary>PAKU_CB_SALTO=1: entrega el evento SALTANDO al trampolin del extensor con el marco
    /// que su contrato exige, leido del original (ver <see cref="ServiceIrq12"/>). Con el, el
    /// trampolin llega de verdad al mundo protegido y ejecuta el codigo del callback — lo que
    /// nunca habia pasado — pero el juego acaba volviendo a DOS: al reentrar en modo protegido la
    /// PAGINACION no se restablece (`paginacion=no`) y las ocho primeras escrituras del kernel,
    /// a lineales 0x101E68xx, se descartan por caer fuera del espacio fisico. Ese es el frente
    /// abierto, y es un fallo DISTINTO del contrato del callback.
    ///
    /// Apagado por defecto a proposito: sin el, el raton no funciona pero el juego VIVE; con el,
    /// hoy, el juego se cierra en cuanto se mueve el raton.</summary>
    /// <summary>El `mouse.in_UIR` del original: mientras el callback del usuario esta EN CURSO no
    /// se le entrega otro evento. En DOSBox lo pone `INT74_Handler` justo antes de saltar y lo
    /// quita el stub `uir` al volver, y es lo que impide reentrar en el trampolin del extensor —
    /// que corre con las interrupciones ABIERTAS y cuya pila propia no es reentrante.</summary>
    static bool _cbEnCurso;

    /// <summary>Lo que <see cref="FinCallbackRaton"/> necesita para reenganchar la linea sin tener
    /// la instancia a mano (lo llama el interprete desde el stub de la cadena).</summary>
    static int _cbSinStub, _cbVueltas;
    static bool _cbEventosPendientes;
    static Pic? _picDelRaton;

    /// <summary>Lo llama el intérprete cuando el invitado alcanza el stub `uir` de la cadena de
    /// retorno (F000:CbRatonUirOff): el callback ha terminado. Equivale al `UIR_Handler` de
    /// DOSBox.</summary>
    public static void FinCallbackRaton(Cpu c, Memory m)
    {
        if (!_cbEnCurso) return;
        _cbEnCurso = false;
        // AQUI NO SE RELEVANTA LA LINEA, y esto costo caro: este gancho corre al LLEGAR al stub,
        // o sea ANTES de ejecutar su `cli`, y con las flags que el trampolin acaba de restaurar
        // (IF=1). Levantar la IRQ12 aqui la deja entregable en mitad del desmontaje —con el
        // pushad todavia sin desapilar y el guardia ya bajado—, y cada reentrada apila otros 50
        // bytes: medido, la pila del kernel se hundio de 0824:21B8 a 0824:0FDB y el flujo acabo
        // en 0000:0000. En el original el reenganche tampoco es inmediato: lo hace
        // INT74_Ret_Handler con PIC_AddEvent(MOUSE_Limit_Events, MOUSE_DELAY = 5 ms).
        // Aqui la linea la vuelve a levantar ServiceCallback en el siguiente limite de frame,
        // que ademas solo ocurre estando en modo protegido: el mismo efecto, sin inventar nada.
        if (_logInt33 && (_cbVueltas++ & 0x3F) == 1)
            LogInt33($"cb  IRQ12: el callback volvio por su cadena; SS:SP={c.SS:X4}:{c.SP:X4}");
    }

    /// <summary>PAKU_CB_ANIDADO=1 vuelve a la conducta VIEJA: llamar al trampolin del extensor de
    /// forma anidada, con un far-call y esperando un RETF. Esta medida y NO da raton —el trampolin
    /// se paraba a 34 instrucciones, o acababa ejecutando datos—, pero se conserva como escape.
    /// El camino normal es el SALTO con la cadena de retorno del original, que es el que entrega
    /// el raton de verdad (ver <see cref="ServiceIrq12"/>).</summary>
    static readonly bool _cbSalto =
        Environment.GetEnvironmentVariable("PAKU_CB_ANIDADO") != "1";

    // Cola de eventos del callback (modelo DOSBox mouse.cpp Mouse_AddEvent): el driver real
    // invoca la rutina de usuario UNA VEZ POR EVENTO, con el estado (posición/botones) DE ESE
    // evento. La versión anterior acumulaba una bitmask (_pendingEvents) y entregaba una sola
    // llamada con el estado ACTUAL: un press+release caídos en la misma ventana de servicio
    // llegaban coalescidos (ev=06) y el juego ignoraba el click (reproducido en un juego de prueba). Los move-only
    // contiguos sí se funden (solo cambia la posición). Productor y consumidor = hilo del juego.
    private readonly List<(ushort ev, ushort x, ushort y, ushort btn)> _cbEvents = new();
    private const int CbEventCap = 64;

    // ── Cola de entrada del host (productor = hilo de render, consumidor = hilo del juego) ───
    // El hilo de render SOLO ENCOLA la muestra cruda (SetPosition/SetButtons); el hilo del juego
    // la APLICA en DrainHostEvents (llamado desde MachineTimer.Poll antes de ticks/callbacks).
    // Único escritor de MouseX/MouseY/Buttons/_mickey*/_press*/_cbEvents = el hilo del juego
    // ⇒ los polls AH=03/05/06 del juego leen estado coherente sin lecturas rasgadas ni locks.
    // (Antes SetPosition/SetButtons mutaban ese estado desde el hilo de render → carrera con los
    // polls: X nueva + Y vieja = salto diagonal; botón nuevo + posición vieja = click rancio.)
    // LIST (no Queue): EnqueueRaw collapses consecutive position-only samples in place so the
    // queue is bounded by the number of BUTTON TRANSITIONS in flight — never by the render
    // frame rate. The render loop feeds a full-state sample per frame (up to ~1800/s); a slow
    // game frame used to let >HostEventCap move samples pile up and the FIFO cap DROPPED the
    // oldest — and if that oldest carried a press/release, the per-event callback lost or
    // fabricated a click (measured: ~27% of button edges dropped during play). The old
    // absolute-state callback hid this because it always re-sent the current button state.
    private readonly List<(ushort x, ushort y, ushort buttons)> _hostEvents = new();
    private const int HostEventCap = 256;                // safety net only: collapse makes overflow unreachable in practice
    private ushort _rawX, _rawY, _rawButtons;            // última muestra cruda del host (lado productor)
    private (ushort x, ushort y, ushort buttons) _lastEnq;
    private bool _haveLastEnq;
    private bool _tailIsMove;                            // newest queued entry is a pure position update (collapsible)
    private bool _rebaseline;                            // next applied sample re-seeds state without synthesizing edges (post-reset)

    // Determinismo/replay (spec §4): grabar/reproducir muestras con su Cpu.TickCount.
    private System.IO.StreamWriter? _inputLog;
    private List<(long tick, ushort x, ushort y, ushort buttons)>? _replay;
    private int _replayIdx;

    // Estado del driver para el resto de la tabla INT 33h (barrido de plataforma,
    // semántica de dosbox-x mouse.cpp)
    private ushort _language;                       // AX=22/23
    private ushort _dblSpeedThreshold = 64;         // AX=13
    private ushort _hotX, _hotY;                    // AX=09 los fija, AX=2A los lee
    private ushort _minX, _maxX = 639;              // AX=07, leídos por 26/31
    private ushort _minY, _maxY = 199;              // AX=08
    private ushort _physMaxX = 639, _physMaxY = 199; // pantalla física del modo actual (píxeles del host)
    private bool _rangeSetX, _rangeSetY;            // el juego llamó AH=07/08 explícitamente

    /// <summary>Unidades del driver por píxel físico, en cada eje. Las DECLARA el juego con un
    /// AH=07/08 de rango completo (mínimo 0), y NO cambian cuando después confina el cursor a una
    /// zona: un rango es un CORRAL, no un cambio de unidades.
    ///
    /// Esta distinción es justo la que faltaba, y se midió en un juego DOS/4GW Pro. El juego declara
    /// 0..1278 en X (el doble de sus 640 píxeles) y 0..479 en Y; al pulsar el MINIMAPA confina el
    /// cursor a X 48..302, Y 26..153 (los 128x128 de la esquina). La regla vieja decidía la escala
    /// comparando el ANCHO DEL RANGO VIGENTE con la pantalla, así que ese corral de 254 unidades
    /// —menor que los 639 píxeles— apagaba el x2 y el juego recibía la MITAD de la X: cualquier
    /// clic en el minimapa caía en su mitad izquierda. En Y no se notaba porque allí la escala ya
    /// era 1. Otro juego de prueba (0..2556 en 640x480) sale idéntico: 2556/639 = 4.</summary>
    private double _escalaVirtX = 1.0, _escalaVirtY = 1.0;

    /// <summary>Un rango que empieza en 0 declara el ESPACIO en el que el juego quiere hablar.
    /// Uno que empieza más allá es un corral dentro de ese mismo espacio y no toca la escala.</summary>
    private void ActualizaEscalaVirtual(bool ejeX)
    {
        if (ejeX) { if (_minX == 0 && _physMaxX > 0) _escalaVirtX = (double)_maxX / _physMaxX; }
        else      { if (_minY == 0 && _physMaxY > 0) _escalaVirtY = (double)_maxY / _physMaxY; }
    }

    // Mapeo al espacio virtual del juego (AH=07/08), como el seamless de DOSBox: la posición se
    // lleva a las unidades del juego y se RECORTA al rango vigente, que es lo que hace un driver
    // real (dosbox-x mouse.cpp: `if (mouse.x > mouse.max_x) mouse.x = mouse.max_x`).
    private ushort ExposeX(ushort px)
    {
        long v = (long)Math.Round(px * XScale * _escalaVirtX);
        if (_rangeSetX) v = Math.Clamp(v, _minX, _maxX);
        return (ushort)v;
    }
    private ushort ExposeY(ushort py)
    {
        long v = (long)Math.Round(py * _escalaVirtY);
        if (_rangeSetY) v = Math.Clamp(v, _minY, _maxY);
        return (ushort)v;
    }
    private ushort ReceiveX(ushort cx)
    {
        long v = cx;
        if (_rangeSetX) v = Math.Clamp(v, _minX, _maxX);
        double f = XScale * _escalaVirtX;
        return (ushort)(f > 0 ? Math.Round(v / f) : v);
    }
    private ushort ReceiveY(ushort dx)
    {
        long v = dx;
        if (_rangeSetY) v = Math.Clamp(v, _minY, _maxY);
        return (ushort)(_escalaVirtY > 0 ? Math.Round(v / _escalaVirtY) : v);
    }
    private int _hidden = 1;                        // contador show/hide (arranca oculto)
    private byte _cursorType;                       // AX=0A (0=sw,1=hw) — bit de AX=25
    private byte _page;                             // AX=1D/1E
    private bool _enabled = true;                   // AX=1F/20, leído por 26
    private const ushort TextAndMask = 0x77FF;      // defaults Microsoft (mouse.cpp)
    private const ushort TextXorMask = 0x7700;
    private const ushort DriverStateSize = 0x0228;  // sizeof(mouse) en dosbox-x (AX=15)
    private readonly object _evLock = new();
    private DosPlatform? _platform;

    // Diagnostics: enabled with PAKU_INT33_COUNT=1. Proves the game polls the
    // mouse at runtime (before the mouse snapshot fix it never called INT 33h).
    private long _int33Calls;
    private long _int33Ah03;
    private long _cbDispatched;

    // ── API del host ──────────────────────────────────────────────────────────────────────

    public void Register(DosPlatform p)
    {
        _platform = p;
        p.Int33 = this;
        p.RegisterInt(0x33, Handle);
        // LA IRQ DEL RATON (IRQ12 -> INT 74h). El driver es sintetico, pero el evento se entrega
        // como una interrupcion de verdad — que es lo que hace DOSBox (mouse.cpp:
        // Mouse_AddEvent -> PIC_ActivateIRQ(12), con su propio stub en la IVT del invitado). El
        // porque, en ServiceCallback: en un juego con extensor, el callback registrado desde modo
        // real SOLO se puede llamar en modo real, y con una IRQ es el EXTENSOR quien baja alli
        // con SU maquinaria — respetando IF, sus pilas y su contabilidad interna, que es
        // exactamente lo que no se puede falsificar desde fuera.
        p.RegisterInt(0x74, ServiceIrq12);
        p.UserCallbackService = ServiceCallback;
        // Poll (hilo del juego) drena la entrada del host encolada por el hilo de render, en el
        // mismo punto seguro donde se sirven IRQs y el callback de usuario.
        p.HostInputDrain = DrainHostEvents;
        InitInputLogReplay();
        if (Environment.GetEnvironmentVariable("PAKU_INT33_COUNT") == "1")
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                Console.Error.WriteLine($"int33_calls={_int33Calls} ah03={_int33Ah03} cb_dispatched={_cbDispatched}");
    }

    // PAKU_INPUT_LOG=<fichero>: apunta cada muestra APLICADA con su Cpu.TickCount.
    // PAKU_INPUT_REPLAY=<fichero>: en Poll se aplican las muestras del log cuando TickCount
    // alcanza el registrado (el host deja de encolar de Raylib de facto: _hostEvents se ignora)
    // ⇒ corridas de ratón reproducibles (red del ScriptDriver headless y para bisecar crashes).
    private void InitInputLogReplay()
    {
        var replayPath = Environment.GetEnvironmentVariable("PAKU_INPUT_REPLAY");
        if (!string.IsNullOrEmpty(replayPath) && File.Exists(replayPath))
        {
            _replay = new List<(long, ushort, ushort, ushort)>();
            foreach (var line in File.ReadAllLines(replayPath))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 4 && long.TryParse(f[0], out var tk) &&
                    ushort.TryParse(f[1], out var x) && ushort.TryParse(f[2], out var y) &&
                    ushort.TryParse(f[3], out var b))
                    _replay.Add((tk, x, y, b));
            }
            _replay.Sort((a, b) => a.tick.CompareTo(b.tick));
            Console.Error.WriteLine($"[INPUT] replay: {_replay.Count} muestras de {replayPath}");
        }
        // De serie las entregas apuntan el input a logs/input.log (la rotación por partida del
        // host lo archiva): el siguiente reporte de ratón de una prueba manual trae los datos consigo.
        // PAKU_INPUT_LOG=<fichero> lo redirige; PAKU_INPUT_LOG=0 lo apaga.
        var logPath = Environment.GetEnvironmentVariable("PAKU_INPUT_LOG")
                      ?? Path.Combine("logs", "input.log");
        if (logPath == "0") logPath = null;
        if (!string.IsNullOrEmpty(logPath))
        {
            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _inputLog = new System.IO.StreamWriter(logPath) { AutoFlush = true };
                Console.Error.WriteLine($"[INPUT] log -> {logPath}");
            }
            catch (Exception ex) { Console.Error.WriteLine("[INPUT] no pude abrir el log: " + ex.Message); }
        }
    }

    /// <summary>
    /// Re-siembra el callback de usuario congelado en un snapshot (gameplay_regs.json
    /// "int33Callback"): el juego lo instaló (AX=000C) ANTES del instante del snapshot,
    /// así que el port nunca ve esa llamada y debe restaurarlo, igual que los vectores
    /// de ISR del juego (DosPlatform.RestoreGameVectors).
    /// </summary>
    public void RestoreCallback(ushort seg, ushort off, ushort mask)
    {
        _cbSeg = seg; _cbOff = off; _cbMask = mask;
    }

    /// <summary>Los mickeys los alimenta el host con el movimiento CRUDO del ratón, no el delta
    /// de la posición del cursor. Lo activa el host mientras tiene el ratón capturado.
    ///
    /// Por qué: un driver real acumula mickeys del hardware y clampea el cursor APARTE
    /// (DOSBox-X mouse.cpp Mouse_CursorMoved: `mickey_accum_x += dx * mickeysPerPixel_x` dentro
    /// de `if (user_cursor_locked)`, y el `if (mouse.x > mouse.max_x) mouse.x = mouse.max_x`
    /// viene DESPUÉS y solo toca la posición). Derivando los mickeys de la posición ya
    /// recortada, todo movimiento contra el borde de la imagen se pierde — y un juego que
    /// integra AH=0B en su propio espacio se desincroniza y ya no alcanza los extremos: una
    /// prueba manual informó de que el ratón no se movía por toda la pantalla.</summary>
    public bool RawMotionMode;

    private int _pendingRawDx, _pendingRawDy;

    /// <summary>Movimiento crudo del ratón del host desde el frame anterior, en píxeles de juego
    /// y SIN recortar por los bordes. Lo llama el hilo de render; se convierte a mickeys en el
    /// hilo del juego (DrainHostEvents), como todo lo demás.</summary>
    public void AddRawMotion(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        lock (_evLock) { _pendingRawDx += dx; _pendingRawDy += dy; }
    }

    // Contrato de mickeys del driver real (DOSBox-X mouse.cpp, ratio por defecto 8,16):
    // X se acumula en unidades del espacio virtual-640 (= 2 por pixel en modos de 320 de
    // ancho) y Y a mickeysPerPixel_y = 16/8 = 2 por pixel. Los juegos que integran AH=0B
    // (alguno divide entre 2) esperan ese 2x; a 1x el cursor queda a mitad de alcance.
    // AH=0F sigue siendo no-op: si algun juego fija ratio, estos factores pasan a ratio/8.
    // PAKU_MICKEY_1X=1 restaura la acumulacion 1x previa (solo para A/B).
    private void AccumulateMickeys(int dx, int dy)
    {
        int mx = (!_mickey1x && _physMaxX == 319) ? 2 : 1;
        int my = _mickey1x ? 1 : 2;
        _mickeyX += (short)(dx * mx);
        _mickeyY += (short)(dy * my);
    }

    /// <summary>El host (hilo de render) llama esto al mover el ratón. SOLO ENCOLA la muestra
    /// cruda; la aplica el hilo del juego en DrainHostEvents. Firma pública intacta.</summary>
    public void SetPosition(ushort x, ushort y)
    {
        InputTape.RecMouse(x, y);
        lock (_evLock) { _rawX = x; _rawY = y; EnqueueRaw(); }
    }

    /// <summary>El host (hilo de render) llama esto al cambiar los botones. SOLO ENCOLA.</summary>
    public void SetButtons(ushort buttons)
    {
        InputTape.RecButtons(buttons);
        lock (_evLock) { _rawButtons = buttons; EnqueueRaw(); }
    }

    // Encola la muestra cruda actual (bajo _evLock). Deduplica repeticiones EXACTAS y COLAPSA
    // muestras solo-movimiento consecutivas (mismos botones) en una única entrada que rastrea la
    // posición MÁS RECIENTE: el juego relee la posición absoluta, así que solo la última importa,
    // y DOSBox también descarta eventos MOUSE_HAS_MOVED duplicados (mouse.cpp Mouse_AddEvent). El
    // colimado deja la cola acotada por el nº de TRANSICIONES DE BOTÓN, no por los FPS de render,
    // de modo que un frame lento del juego jamás desborda la cola y descarta un press/release
    // (que el callback por-evento convertiría en un click fantasma o perdido). Cada entrada de
    // FLANCO conserva su posición exacta (el click cae donde se pulsó), solo se funden los moves.
    private void EnqueueRaw()
    {
        var s = (_rawX, _rawY, _rawButtons);
        if (_haveLastEnq && s == _lastEnq) return;                 // exact duplicate
        bool buttonsChanged = _haveLastEnq && s.Item3 != _lastEnq.Item3;
        _haveLastEnq = true; _lastEnq = s;
        if (!buttonsChanged && _tailIsMove && _hostEvents.Count > 0)
        {
            _hostEvents[^1] = s;                                  // collapse move-only update into the newest move entry
        }
        else
        {
            // Safety net (unreachable in practice: needs >HostEventCap real button transitions
            // between drains). If ever hit, drop the OLDEST entry — after collapse the front
            // is a move/edge boundary; keeping recent edges matters more than ancient ones.
            if (_hostEvents.Count >= HostEventCap) _hostEvents.RemoveAt(0);
            _hostEvents.Add(s);
            _tailIsMove = !buttonsChanged;                        // edge sample is NOT collapsible; move sample is
        }
    }

    /// <summary>Aplica en el HILO DEL JUEGO las muestras crudas encoladas por el host (o, en modo
    /// replay, las del log cuando Cpu.TickCount las alcanza). Único escritor del estado del ratón.
    /// Lo invoca MachineTimer.Poll antes de entregar ticks/callbacks (via DosPlatform.HostInputDrain).</summary>
    public void DrainHostEvents()
    {
        if (_replay != null)
        {
            long now = Cpu.TickCount;
            while (_replayIdx < _replay.Count && _replay[_replayIdx].tick <= now)
            {
                var s = _replay[_replayIdx++];
                ApplySample(s.x, s.y, s.buttons);
            }
            return;
        }
        // Movimiento crudo acumulado por el host (ratón capturado): a mickeys ANTES de aplicar
        // las posiciones, en este mismo hilo. Sin recorte por bordes — ver RawMotionMode.
        int rawDx, rawDy;
        lock (_evLock) { rawDx = _pendingRawDx; rawDy = _pendingRawDy; _pendingRawDx = _pendingRawDy = 0; }
        if (rawDx != 0 || rawDy != 0) AccumulateMickeys(rawDx, rawDy);

        // Snapshot + clear the whole batch under the lock, apply outside it (the applied state is
        // consumer-only). Draining the entire queue each Poll keeps the collapse window tight.
        (ushort x, ushort y, ushort buttons)[] batch;
        lock (_evLock)
        {
            if (_hostEvents.Count == 0) return;
            batch = _hostEvents.ToArray();
            _hostEvents.Clear();
            _tailIsMove = false;
        }
        foreach (var s in batch) ApplySample(s.x, s.y, s.buttons);
    }

    // Aplica UNA muestra cruda: la misma lógica de mickeys/press/release que antes
    // vivía en SetPosition/SetButtons, ahora SIEMPRE en el hilo del juego (byte a byte idéntica).
    private void ApplySample(ushort x, ushort y, ushort buttons)
    {
        if (_rebaseline)
        {
            // First sample after a driver reset (AX=00/21) re-establishes the baseline WITHOUT
            // synthesizing motion or button edges: the host feeds full state every frame, so a
            // reset that zeroed the internal state must not turn a still-held button into a
            // phantom press, nor the jump from the zeroed origin into a giant mickey delta.
            // (A real driver is edge-driven from hardware and never diffs a full state.)
            _rebaseline = false;
            MouseX = x; MouseY = y; Buttons = buttons;
            return;
        }
        ushort ev = 0;
        // posición (antiguo cuerpo de SetPosition)
        if (x != MouseX || y != MouseY)
        {
            // Con el ratón capturado los mickeys NO salen de aquí: salen del movimiento crudo
            // (AddRawMotion), porque esta posición ya viene recortada al borde de la imagen y
            // el recorte se comería el movimiento. Ver RawMotionMode.
            if (!RawMotionMode)
                AccumulateMickeys((short)(x - MouseX), (short)(y - MouseY));
            MouseX = x;
            MouseY = y;
            ev |= 0x01;   // bit 0: cursor moved
        }
        // botones (antiguo cuerpo de SetButtons)
        ushort changed = (ushort)(Buttons ^ buttons);
        if (changed != 0)
        {
            for (int i = 0; i < 3; i++)
            {
                if ((changed & (1 << i)) != 0)
                {
                    if ((buttons & (1 << i)) != 0)
                    {
                        _pressCount[i]++;
                        _pressX[i] = MouseX;
                        _pressY[i] = MouseY;
                        ev |= (ushort)(0x02 << (i * 2));   // bits 1/3/5: press izq/dcha/central
                    }
                    else
                    {
                        _releaseCount[i]++;
                        _releaseX[i] = MouseX;
                        _releaseY[i] = MouseY;
                        ev |= (ushort)(0x04 << (i * 2));   // bits 2/4/6: release izq/dcha/central
                    }
                }
            }
            Buttons = buttons;
        }
        if (ev != 0)
        {
            bool moveOnly = (ev & ~(ushort)0x01) == 0;
            if (moveOnly && _cbEvents.Count > 0 && (_cbEvents[^1].ev & ~(ushort)0x01) == 0)
                _cbEvents[^1] = (0x01, MouseX, MouseY, Buttons);   // fundir moves contiguos
            else
            {
                if (_cbEvents.Count >= CbEventCap) _cbEvents.RemoveAt(0);
                _cbEvents.Add((ev, MouseX, MouseY, Buttons));
            }
        }
        _inputLog?.WriteLine($"{Cpu.TickCount} {x} {y} {buttons}");
    }

    /// <summary>
    /// Entrega los eventos pendientes al callback del juego (AX=000C). Corre en el HILO DEL
    /// JUEGO en el límite de frame (DosPlatform.ServicePendingInterrupts), como las IRQs.
    /// Semántica del driver real: far call con AX=eventos, BX=botones, CX=x, DX=y,
    /// SI/DI=mickeys; el driver salva y restaura todos los registros del interrumpido.
    /// </summary>
    private void ServiceCallback(Cpu c, Memory m)
    {
        if (_cbMask == 0 || (_cbSeg == 0 && _cbOff == 0) || _platform?.IsrDispatch == null)
        {
            // Sin callback instalado los eventos no se acumulan (si el juego lo instala
            // después, no debe recibir historia pre-instalación).
            if (_cbEvents.Count > 0) _cbEvents.Clear();
            return;
        }
        if (_cbEvents.Count == 0) return;

        // EL CALLBACK SOLO SE PUEDE LLAMAR EN EL MUNDO EN EL QUE SE REGISTRO.
        //
        // Medido en un juego DOS/4GW Pro, y es lo que tumbaba el juego en cuanto se movia el raton: el
        // extensor de DOS/4GW registra su trampolin `0C5D:1F60` DESDE MODO REAL (`pm=0`), pero
        // este despacho ocurre en el limite de frame, que en un juego con extensor cae casi
        // siempre con el invitado en MODO PROTEGIDO (`pm=1 msw=80000001`, dentro del kernel).
        // Alli `0C5D` ya no es un parrafo sino un SELECTOR, asi que la llamada se iba a donde
        // dijera la GDT — el juego acababa ejecutando la IVT como codigo y moria con un
        // "opcode invalido @ 0000:0083".
        //
        // El evento NO se consume: se queda en la cola y se entrega en el primer limite de
        // frame que caiga en el mundo correcto. La cola ya tiene tope (CbEventCap) y funde los
        // movimientos contiguos, asi que esperar no la desborda ni encola historia rancia.
        // EL CALLBACK SOLO SIGNIFICA ALGO EN EL MUNDO EN EL QUE SE REGISTRO, asi que si el
        // invitado esta en otro hay que CRUZARLO — igual que cruza un extensor de verdad.
        //
        // Medido en un juego DOS/4GW Pro, y es lo que tumbaba el juego en cuanto se movia el raton: el
        // extensor de DOS/4GW registra su trampolin `0C5D:1F60` DESDE MODO REAL (`pm=0`), pero
        // este despacho ocurre en el limite de frame, que en un juego con extensor cae casi
        // siempre con el invitado en MODO PROTEGIDO (`pm=1 msw=80000001`, dentro del kernel).
        // Alli `0C5D` ya no es un parrafo sino un SELECTOR, la llamada se iba a donde dijera la
        // GDT, y el juego acababa ejecutando la IVT como codigo ("opcode invalido @ 0000:0083").
        //
        // Esperar a que el invitado pase por modo real NO vale, y esta medido: de 80.000 limites
        // de frame en 70 s solo 188 caen en modo real y solo 32 son utiles (con IF y pila
        // pequena) — y esas 32 son todas del arranque. Una vez el juego arranca, no vuelve.
        //
        // Lo que hace el hardware es lo que hacemos aqui: cuando llega la IRQ del raton y el
        // sistema esta en modo protegido, el extensor REFLEJA la interrupcion — guarda el estado
        // protegido, baja a modo real, llama al manejador real y restaura. La pila de modo real
        // que usamos es la que el propio extensor tenia puesta cuando REGISTRO el callback, que
        // es por definicion una pila valida de ese mundo.
        bool cruzar = Memory.SegmentosSonSelectores != _cbPm && _cruceCb;
        var mundo = default(MundoGuardado);
        // CON EL CAMINO BUENO, AQUI NO SE DESPACHA NUNCA. Este limite de frame puede pillar al
        // invitado en modo real y COINCIDIENDO con el mundo del registro —cualquier ventana de
        // reflexion del extensor, incluido el tramo real de NUESTRO PROPIO trampolin en vuelo, que
        // corre con IF=1—, y entonces caia al despacho anidado con el contrato viejo: un far-call
        // que pisa cs:[1FA0..1FA5] y cambia SS a 0824 a media excursion. Firma de esa corrupcion:
        // `opcode invalido @ 0000:0083` con CS=0000, o sea ejecutando la IVT como codigo.
        // La entrega va toda por la IRQ12, que es como la hace el hardware.
        if (Memory.SegmentosSonSelectores != _cbPm && !cruzar)
        {
            // Sin cruce (por defecto) el evento NO se consume: se queda en la cola, que ya tiene
            // tope y funde los movimientos contiguos. El juego se queda sin raton, pero VIVO —
            // que es exactamente lo que separa "no responde" de "se ha muerto".
            if (_logInt33 && (++_cbDiferidos & 0x3FF) == 1)
            {
                LogInt33($"cb  por IRQ12: {_cbEvents.Count} evento(s), el mundo del registro es " +
                         $"pm={(_cbPm ? 1 : 0)} y ahora estamos en " +
                         $"pm={(Memory.SegmentosSonSelectores ? 1 : 0)}; {_cbDiferidos} veces");
                // Las puertas de la IDT del invitado, que son las que dicen si el extensor va a
                // reflejar la IRQ12. Medido en un juego DOS/4GW Pro: IDT[74] sel=0080 off=01D0 attr=8E e
                // IDT[75] sel=0080 off=01D4 — mismo selector y paso constante de 4 bytes, o sea
                // una TABLA DE REFLECTORES; y IVT[74]=0000:0000, el hueco donde va nuestro stub.
                if (c.IdtLimit >= 0x74 * 8 + 7)
                    foreach (byte v in new byte[] { 0x08, 0x74, 0x75 })
                    {
                        uint g = c.IdtBase + (uint)(v * 8);
                        LogInt33($"cb  IDT[{v:X2}] sel={m.RdLin16(g + 2):X4} off={m.RdLin16(g):X4} " +
                                 $"attr={m.RdLin8(g + 5):X2}   IVT[{v:X2}]=" +
                                 $"{m.RdLin16((uint)(v * 4 + 2)):X4}:{m.RdLin16((uint)(v * 4)):X4}");
                    }
            }
            // LA LINEA DEL RATON, en vez de la llamada directa: que el extensor baje a modo real
            // con SU maquinaria. El evento no se consume aqui — lo consume el manejador de la
            // INT 74h cuando la reflexion llegue.
            _platform!.Pic.Desenmascara(12);   // como el driver PS/2 al instalarse
            _platform.Pic.RaiseIrq(12);
            return;
        }
        // AQUI EL INVITADO ESTA EN MODO REAL, y con el camino bueno eso significa esperar, no
        // entregar. Dos razones, las dos medidas:
        //   (a) Despachar anidado usaria el contrato viejo a media excursion — ver el comentario
        //       de arriba y la firma `opcode invalido @ 0000:0083` con CS=0000.
        //   (b) Y levantar la linea aqui es PEOR todavia: en modo real el kernel del extensor
        //       corre con IF=0 en todas partes MENOS en el `pushf` de su reflector (0C5D:0F34),
        //       asi que la IRQ12 se entrega SIEMPRE ahi — medido: las nueve entregas, sin una
        //       excepcion, interrumpen en 0C5D:0F34. Eso es secuestrar al reflector justo cuando
        //       iba a reflejar OTRA interrupcion, y con su misma pila (ss:[0996]). En el original
        //       la IRQ12 llega estando el juego en MODO PROTEGIDO y es el extensor quien la baja
        //       con su maquinaria — ahi el codigo interrumpido es del juego (medido: 047A:100F),
        //       no el reflector.
        // ...PERO SOLO SI HAY EXTENSOR. Todo el razonamiento de arriba habla del kernel de un
        // extensor: "estamos en modo real" significa alli "estamos dentro de su reflector", y por
        // eso hay que esperar a la IRQ12. En un juego de MODO REAL PURO —la mayoria de los juegos
        // probados— no hay tal kernel: modo real es el mundo del juego, este limite
        // de frame es el sitio bueno, y la IRQ12 no se levanta NUNCA (solo se levanta al detectar
        // discrepancia de mundos, y ahi los dos son real). Cortar aqui a secas los dejaba sin
        // raton: medido con el mismo arnes y su control, un juego A pasaba de 3.498 callbacks en 60 s a
        // CERO, y un juego B de 2.687 a CERO. El discriminante es PEGAJOSO (Memory.HuboModoProtegido) y
        // no el modo actual, justo porque el extensor pasa por modo real todo el rato.
        if (_cbSalto && Memory.HuboModoProtegido) return;
        if (cruzar)
        {
            if (_cbPm) return;          // registrado en protegido y estamos en real: no se cruza
            mundo = BajaAModoReal(c);
            if (_logInt33 && (++_cbDiferidos & 0x3FF) == 1)
                LogInt33($"cb  CRUCE a modo real para llamar a {_cbSeg:X4}:{_cbOff:X4} " +
                         $"(pila {_cbSs:X4}:{_cbSp:X4}), {_cbDiferidos} veces");
        }

        DespachaEventos(c, m, cruzar, mundo, int.MaxValue, comoInterrupcion: cruzar);
    }

    /// <summary>Despacha hasta `maxEventos` de la cola llamando al callback registrado, salvando
    /// y restaurando el contexto del interrumpido. Lo comparten los dos caminos: el limite de
    /// frame (juego de modo real, todos los eventos pendientes) y el manejador de la IRQ del
    /// raton (juego con extensor, UN evento por interrupcion, como el hardware).</summary>
    private void DespachaEventos(Cpu c, Memory m, bool cruzar, in MundoGuardado mundo,
                                 int maxEventos, bool comoInterrupcion = false)
    {
        // Save the interrupted context: the real driver preserves every register
        // around the user routine (the game's callback clobbers DS/DI freely).
        ushort ax = c.AX, bx = c.BX, cx = c.CX, dx = c.DX;
        ushort si = c.SI, di = c.DI, bp = c.BP, ds = c.DS, es = c.ES;
        ushort cs = c.CS;   // FarDispatch mutates c.CS; the interrupted Interp fetches through it
        // FULL flags word, not just IF: the real callback runs inside the driver's hardware
        // ISR, whose IRET restores the interrupted code's FLAGS verbatim. This dispatch fires
        // at an arbitrary instruction boundary — restoring only IF let the game's callback
        // leak CF/ZF/... into a cmp/jcc straddle (one game's INT 91h dispatcher interrupted between
        // cmp and jae → bogus "invalid function 4" exit). PAKU_CB_FLAGS=0 reverts (A/B).
        ushort flags = c.FlagsWord();
        c.IF = false;

        // UNA llamada far POR EVENTO, con la posición/botones de ESE evento (contrato del
        // driver real; una ráfaga pendiente = ráfaga de IRQs consecutivas del hardware).
        try
        {
        int servidos = 0;
        while (_cbEvents.Count > 0 && servidos < maxEventos)
        {
            var e = _cbEvents[0];
            _cbEvents.RemoveAt(0);
            ushort ev = (ushort)(e.ev & _cbMask);
            if (ev == 0) continue;   // evento fuera de la máscara: se consume sin invocar
            _cbDispatched++; servidos++;

            c.AX = ev;
            c.BX = e.btn;
            c.CX = ExposeX(e.x);
            c.DX = ExposeY(e.y);
            if (_logInt33 && (_cbDispatched & 0x3F) == 1)
                LogInt33($"cb  ev={ev:X2} raw={e.x},{e.y} -> CX={c.CX} DX={c.DX} rango=({_minX},{_minY})-({_maxX},{_maxY})" +
                         $" | DESPACHO a {_cbSeg:X4}:{_cbOff:X4} con el invitado en" +
                         $" pm={(Memory.SegmentosSonSelectores ? 1 : 0)} msw={c.Msw:X8}" +
                         $" CS={c.CS:X4} SS:ESP={c.SS:X4}:{c.ESP:X8}");
            c.SI = (ushort)_mickeyX;
            c.DI = (ushort)_mickeyY;

            // EL TRAMPOLIN DE UN EXTENSOR NO ES UNA RUTINA NORMAL: acaba en IRET, no en RETF,
            // y por el camino se va al mundo protegido. Desensamblado el de un juego DOS/4GW Pro
            // (0C5D:1F60, una tabla de 16 entradas que llaman todas al mismo sitio):
            //
            //     1FE8: pushf / cli / guarda SS:SP en cs:[1FA0]
            //     1FF9: cambia a la pila del kernel (segmento 0824)
            //     2006: pushal, guarda segmentos
            //     201E: call 0x290
            //     2021: ljmp 0070:4F11          <- se va al mundo protegido
            //     ...
            //     204B: iret                     <- vuelve, ya con SS:SP restaurados
            //
            // Eso obliga a dos cosas que el despacho de siempre no hacia, y sin las cuales el
            // trampolin se quedaba a 34 instrucciones de su trabajo (medido): un marco de
            // INTERRUPCION de tres words, que es lo que su IRET va a desapilar, y IsrMode, que
            // hace que los ret/retf internos CONTINUEN y solo el IRET termine el despacho (sin
            // el, el primer retf de la rutina interna daba el callback por acabado antes del
            // ljmp). El camino de siempre —far call de dos words y fin en el primer retf— se
            // conserva intacto para los callbacks que SON del juego (los de modo real puro).
            ushort spBefore = c.SP;
            if (comoInterrupcion)
            {
                // LA DIRECCION DE RETORNO TIENE QUE SER REAL, no un IP=0 de adorno: el lado
                // PROTEGIDO del trampolin LEE este marco (el que el prologo captura en
                // cs:[1FA0]) para calcular donde reanudar cuando vuelve del mundo de 32 bits.
                // Con un retorno inventado reanudaba en cualquier parte — medido: moria en
                // 0F34:0B78 ejecutando codigo real con un contexto que no era el suyo. Se le da
                // el IRET del stub de la INT 74h, que es una instruccion de verdad en la memoria
                // del invitado; es la misma idea que la cadena de stubs de DOSBox (CB_IRQ12_RET).
                ushort retSeg = Cpu.IvtStubSeg, retOff = (ushort)(Cpu.IvtStubOff(0x74) + 1);
                c.SP -= 2; m.WrU16(c.SS, c.SP, flags);
                c.SP -= 2; m.WrU16(c.SS, c.SP, retSeg);
                c.SP -= 2; m.WrU16(c.SS, c.SP, retOff);
            }
            else
            {
                // Far-call frame (return CS:IP). The lifted routine ends in RETF and pops it;
                // rebalance defensively if the dispatch missed (same pattern as FireInterrupt).
                c.SP -= 2; m.WrU16(c.SS, c.SP, c.CS);
                c.SP -= 2; m.WrU16(c.SS, c.SP, 0);
            }
            // Same WholeProgram toggle as FireInterrupt and the software-INT dispatch: under
            // Interp.WholeProgram the callback's terminal retf would CONTINUE at the fake
            // IP=0 of this frame (thunk dispatcher at seg:0000) instead of returning —
            // cascades into the menu teardown and consumes stale stack slots (measured on a test game).
            bool savedWp = Interp.WholeProgram, savedIsr = Interp.IsrMode;
            Interp.WholeProgram = comoInterrupcion;
            Interp.IsrMode = comoInterrupcion;
            long insnAntes = Interp.InsnCount;
            _platform.IsrDispatch(c, m, _cbSeg, _cbOff);
            if (_logInt33 && (_cbDispatched & 0x3F) == 1)
                LogInt33($"cb  el trampolin ejecuto {Interp.InsnCount - insnAntes:N0} instrucciones; " +
                         $"al volver CS:SP={c.CS:X4}:{c.SP:X4} pm={(Memory.SegmentosSonSelectores ? 1 : 0)}");
            Interp.WholeProgram = savedWp;
            Interp.IsrMode = savedIsr;
            c.FarRet = false;   // callback boundary: its terminating RETF must not unwind the game loop
            if (c.SP != spBefore) c.SP = spBefore;
        }

        }
        finally { if (cruzar) VuelveDelModoReal(c, mundo); }

        c.AX = ax; c.BX = bx; c.CX = cx; c.DX = dx;
        c.SI = si; c.DI = di; c.BP = bp; c.DS = ds; c.ES = es;
        c.CS = cs;
        if (_preserveCbFlags) c.SetFlagsWord(flags);
        else c.IF = (flags & 0x0200) != 0;
    }

    /// <summary>Manejador de INT 74h: la IRQ del raton, ya reflejada a modo real por el extensor
    /// (o entregada directamente en un juego de modo real). Aqui el invitado esta EN EL MUNDO DEL
    /// CALLBACK y con la contabilidad del kernel coherente, que es todo lo que le faltaba al
    /// despacho desde el limite de frame. Un evento por IRQ, como el hardware: si quedan mas, se
    /// vuelve a levantar la linea.</summary>
    private void ServiceIrq12(Cpu c, Memory m)
    {
        _platform!.Pic.Eoi(12);
        if (_cbMask == 0 || (_cbSeg == 0 && _cbOff == 0)) return;
        if (Memory.SegmentosSonSelectores != _cbPm) return;   // aun no estamos donde toca
        if (_cbEvents.Count == 0) return;
        if (_cbEnCurso) return;   // el callback anterior no ha vuelto: como el in_UIR del original
        if (!_cbSalto)
        {
            // Camino por defecto: llamada anidada. No da raton (el trampolin se para a 34
            // instrucciones) pero NO rompe nada, que es lo unico que se puede garantizar mientras
            // el camino bueno —el salto de aqui debajo— siga topando con la paginacion.
            DespachaEventos(c, m, false, default, 1, comoInterrupcion: _cbComoIret);
            if (_cbEvents.Count > 0) _platform.Pic.RaiseIrq(12);
            return;
        }

        // NO SE LLAMA AL TRAMPOLIN: SE SALTA A EL, y se le deja debajo un FAR-RETURN DE DOS WORDS.
        // El contrato no esta adivinado: se leyo del original con el juego vivo y se comprobo
        // contra la fuente del emulador que hace de driver — los dos coinciden. Detalle completo,
        // con el desensamblado y los volcados del juego vivo.
        //
        // El trampolin del extensor (0C5D:1F60 en el juego medido) es una tabla de 16 entradas de la
        // forma `call 1FE8` + un byte de indice, y el punto comun 1FE8 hace esto:
        //
        //     1FE8  pushf / cli / guarda FLAGS, SS y SP del llamante en cs:[1FA0..1FA5]
        //     1FFD  se cambia a su propia pila (0824) ... pushad, segmentos
        //     2018  mov bx,es:[bx]      ; BX = el word de la CIMA de la pila del llamante
        //     201B  push word cs:[bx]   ; el INDICE del callback: lo lee del near-return del `call`
        //     2021  ljmp 0070:4F11      ; -> mundo protegido
        //     2035  pop [si] / pop [si+2] / pop [si+4]   ; TRES words en la pila del llamante
        //     204B  iret                ; y termina consumiendolos
        //
        // O sea: consume SIEMPRE TRES words desde el SP con el que entra en 1FE8. Entrando por la
        // tabla, el `call` de la entrada aporta el PRIMERO (y de ahi saca el indice), asi que al
        // llamante le tocan exactamente DOS: un far-return. Eso descarta las dos formas que se
        // habian probado a ciegas — un marco de interrupcion de tres words son CUATRO con el
        // near-return, y el `iret` deja SP dos bytes corrido; y un salto pelado no deja retorno
        // ninguno (el juego salia limpio a DOS por INT 21h/4Ch).
        //
        // El far-return apunta al IRET del stub de la INT 74h, que es una instruccion de verdad en
        // la memoria del invitado: cuando el trampolin acaba, ese IRET consume el marco de la IRQ
        // y devuelve el control al codigo interrumpido. Es la cadena de DOSBox (callback -> uir ->
        // int74_ret) con un eslabon menos, porque el EOI ya lo hemos hecho en C# y la bandera
        // in_UIR es estado del emulador, no del invitado.
        //
        // Y llamarlo anidado desde dentro del manejador no vale, aunque el marco sea correcto: lo
        // mete a ejecutar A MEDIA REFLEXION de la IRQ. Aqui el IRET del propio stub SALTA alli y
        // el trampolin corre en el contexto de la interrupcion, como el hardware manda.
        // SOLO SI VENIMOS DE EJECUTAR EL STUB. `FireInterrupt` tiene dos caminos, y para un vector
        // que apunta a un stub nuestro (IVT[74]=F000:12E8) toma el que LLAMA AL MANEJADOR C# SIN
        // EJECUTAR EL STUB — correcto para una INT software emulada, pero aqui todo el marco que
        // se monta abajo cuenta con que el `iret` del stub lo consuma. Sin stub, esos ~50 bytes se
        // quedan HUERFANOS sobre la pila del kernel, con SP corrido y el guardia alto para
        // siempre. Medido: nueve entregas buenas y a la decima la pila del kernel se hundio de
        // 0824:21B8 a 0824:0FDB y el flujo acabo paseando por la IVT (0000:0083, byte F0).
        // Si no estamos ejecutando el stub, el evento se queda en la cola y ya lo levantara
        // ServiceCallback desde donde debe.
        if (c.CS != Cpu.IvtStubSeg)
        {
            if (_logInt33 && (++_cbSinStub & 0x3F) == 1)
                LogInt33($"cb  IRQ12 SIN STUB (entrada directa al manejador) desde " +
                         $"{c.CS:X4} SS:SP={c.SS:X4}:{c.SP:X4}; el evento se queda en la cola. {_cbSinStub} veces");
            return;
        }

        var e = _cbEvents[0];
        _cbEvents.RemoveAt(0);
        ushort ev = (ushort)(e.ev & _cbMask);
        if (ev == 0) { if (_cbEvents.Count > 0) _platform.Pic.RaiseIrq(12); return; }

        _cbDispatched++;
        // El marco de la INT 74h tal y como llega: dice EN QUE PUNTO estamos interrumpiendo al
        // invitado, que es lo unico que distingue una entrega buena de una que lo parte.
        ushort qIp = m.RdU16(c.SS, c.SP), qCs = m.RdU16(c.SS, (ushort)(c.SP + 2)),
               qFl = m.RdU16(c.SS, (ushort)(c.SP + 4));

        // 1) LO QUE HACE EL ISR DE LA IRQ12 ANTES DE NADA: salvar los registros. `push ds`,
        //    `push es`, `pushad` — 36 bytes. El callback los va a pisar (nosotros mismos le
        //    ponemos AX/BX/CX/DX/SI/DI justo debajo), y el codigo interrumpido espera
        //    encontrarlos intactos al volver. Sin esto el kernel del extensor se descarrila
        //    lejisimos de la causa, que es exactamente lo que pasaba: la pila del trampolin
        //    quedaba 40 bytes por encima de la del original (36 de aqui + 4 del segundo
        //    far-return). El `sti` del original va en las flags del marco, mas abajo.
        c.SP -= 2; m.WrU16(c.SS, c.SP, c.DS);
        c.SP -= 2; m.WrU16(c.SS, c.SP, c.ES);
        uint espPrevio = c.ESP;   // pushad guarda el ESP de ANTES de empezar a empujar
        foreach (uint r in new[] { c.EAX, c.ECX, c.EDX, c.EBX, espPrevio, c.EBP, c.ESI, c.EDI })
        { c.SP -= 4; m.WrU32(c.SS, c.SP, r); }

        // 2) La cadena de retorno, de abajo arriba: primero el epilogo (que deshace el guardado
        //    de arriba y hace `iret`), y encima el stub que solo cede el control. Son DOS
        //    far-returns porque el `iret` del trampolin consume TRES words —el near-return de su
        //    tabla y los dos del PRIMER far-return—, asi que el segundo tiene que estar debajo,
        //    esperando al `retf` del primero. Es la cadena del original, eslabon por eslabon.
        ushort finSeg = Cpu.IvtStubSeg, finOff = Cpu.CbRatonFinOff;
        ushort uirSeg = Cpu.IvtStubSeg, uirOff = Cpu.CbRatonUirOff;
        c.SP -= 2; m.WrU16(c.SS, c.SP, finSeg);
        c.SP -= 2; m.WrU16(c.SS, c.SP, finOff);
        c.SP -= 2; m.WrU16(c.SS, c.SP, uirSeg);
        c.SP -= 2; m.WrU16(c.SS, c.SP, uirOff);

        // 3) Los registros del evento (INT74_Handler): AX = tipo, BL = botones, BH = rueda,
        //    CX = X, DX = Y, SI/DI = mickeys con signo.
        c.AX = ev; c.BX = e.btn; c.CX = ExposeX(e.x); c.DX = ExposeY(e.y);
        c.SI = (ushort)_mickeyX; c.DI = (ushort)_mickeyY;

        // 4) Y encima del todo, el marco con el que el `iret` de ESTE stub salta al trampolin.
        //    Con IF=1: el ISR del original arranca con `sti`, o sea el callback corre con las
        //    interrupciones ABIERTAS, y el kernel del extensor cuenta con ello.
        ushort fl = (ushort)(c.FlagsWord() | 0x0200);
        _cbEnCurso = true;
        _cbEventosPendientes = _cbEvents.Count > 0;
        _picDelRaton = _platform.Pic;
        c.SP -= 2; m.WrU16(c.SS, c.SP, fl);
        c.SP -= 2; m.WrU16(c.SS, c.SP, _cbSeg);
        c.SP -= 2; m.WrU16(c.SS, c.SP, _cbOff);
        if (_logInt33 && (_cbDispatched & 0x3F) == 1)
            LogInt33($"cb  IRQ12 #{_cbDispatched}: salto a {_cbSeg:X4}:{_cbOff:X4} con ev={ev:X2} " +
                     $"CX={c.CX} DX={c.DX}; el extensor vuelve a {qCs:X4}:{qIp:X4} fl={qFl:X4}; " +
                     $"marco sobre SS:SP={c.SS:X4}:{c.SP:X4}, " +
                     $"cadena {uirSeg:X4}:{uirOff:X4} -> {finSeg:X4}:{finOff:X4}");
        // Nada de relevantar la linea aqui: lo hace FinCallbackRaton cuando la cadena vuelva.
    }

    /// <summary>El estado que hay que devolver intacto tras cruzar a modo real. Van los SEIS
    /// selectores y los registros ENTEROS: el mundo del que venimos es de 32 bits y un despacho
    /// que sólo salvara las mitades bajas le dejaría la mitad alta de cada registro pisada.</summary>
    private struct MundoGuardado
    {
        public uint Msw, Cr3, Eax, Ebx, Ecx, Edx, Esi, Edi, Ebp, Esp;
        public ushort Cs, Ds, Es, Ss, Fs, Gs;
        public byte SegRecargados;
        public bool Paginacion;
        public Func<ushort, uint>? Resolutor;
    }

    private MundoGuardado BajaAModoReal(Cpu c)
    {
        var g = new MundoGuardado
        {
            Msw = c.Msw, Cr3 = c.Cr3, Paginacion = Memory.Paginacion,
            Resolutor = Memory.BaseDelSelector, SegRecargados = c.SegRecargados,
            Eax = c.EAX, Ebx = c.EBX, Ecx = c.ECX, Edx = c.EDX,
            Esi = c.ESI, Edi = c.EDI, Ebp = c.EBP, Esp = c.ESP,
            Cs = c.CS, Ds = c.DS, Es = c.ES, Ss = c.SS, Fs = c.FS, Gs = c.GS,
        };
        // PG no puede quedarse encendida sin PE (el 386 no lo permite), y además la traducción
        // dejaría de valer: el trampolín y su pila viven en memoria baja del DOS.
        Memory.ConfiguraPaginacion(false, c.Cr3);
        c.Msw &= ~0x80000001u;
        Memory.SegmentosSonSelectores = false;
        Memory.BaseDelSelector = null;
        c.SS = _cbSs; c.ESP = _cbSp;
        return g;
    }

    private void VuelveDelModoReal(Cpu c, in MundoGuardado g)
    {
        c.Msw = g.Msw; c.Cr3 = g.Cr3;
        Memory.BaseDelSelector = g.Resolutor;
        Memory.SegmentosSonSelectores = g.Resolutor != null && (g.Msw & 1) != 0;
        Memory.ConfiguraPaginacion(g.Paginacion, g.Cr3);
        c.EAX = g.Eax; c.EBX = g.Ebx; c.ECX = g.Ecx; c.EDX = g.Edx;
        c.ESI = g.Esi; c.EDI = g.Edi; c.EBP = g.Ebp;
        c.CS = g.Cs; c.DS = g.Ds; c.ES = g.Es; c.SS = g.Ss; c.FS = g.Fs; c.GS = g.Gs;
        c.ESP = g.Esp;
        // Las cargas de arriba han marcado los seis bits de "recargado"; la caché oculta de
        // descriptores tiene que volver a decir lo que decía, o el intérprete creerá que el
        // invitado recargó unos segmentos que no ha tocado.
        c.SegRecargados = g.SegRecargados;
    }

    private static readonly bool _preserveCbFlags =
        Environment.GetEnvironmentVariable("PAKU_CB_FLAGS") != "0";

    // Called from the driver reset paths (AX=0000/0021). Drops queued pre-reset samples and
    // arms _rebaseline so the next host sample re-seeds MouseX/Y/Buttons WITHOUT synthesizing
    // edges: without this a button held across the reset (Buttons zeroed → next full-state
    // sample has it set) fabricated a phantom press, and the dedup on a same-position sample
    // could leave the cursor stuck at the zeroed origin until it moved to a new pixel.
    private void ResetHostInputState()
    {
        // El guardia de reentrada tambien se levanta aqui: si una excursion no llega a volver por
        // su cadena, sin esto el raton se quedaria mudo PARA SIEMPRE — incluso si el juego
        // reinstala el callback, que es justo lo que hace un reset.
        _cbEnCurso = false;
        lock (_evLock)
        {
            _hostEvents.Clear();
            _tailIsMove = false;
            _haveLastEnq = false;
        }
        _rebaseline = true;
    }

    // ── Handler ───────────────────────────────────────────────────────────────────────────

    // PAKU_LOG_INT33=1: traza de llamadas al driver a stderr (AH=03 muestreada 1/256 para no inundar)
    private static readonly bool _logInt33 = Environment.GetEnvironmentVariable("PAKU_LOG_INT33") == "1";
    private static readonly bool _mickey1x = Environment.GetEnvironmentVariable("PAKU_MICKEY_1X") == "1";
    private static void LogInt33(string msg) => Console.Error.WriteLine($"[INT33] {msg}");

    private void Handle(Cpu cpu, Memory mem)
    {
        _int33Calls++;
        if (cpu.AX == 0x0003) _int33Ah03++;
        if (_logInt33 && (cpu.AX != 0x0003 || (_int33Ah03 & 0xFF) == 1))
            LogInt33($"in  AX={cpu.AX:X4} BX={cpu.BX:X4} CX={cpu.CX:X4} DX={cpu.DX:X4} pos={MouseX},{MouseY} rango=({_minX},{_minY})-({_maxX},{_maxY}) xscale={XScale}");
        switch (cpu.AX)
        {
            case 0x0000: // Reset / check installed
                // dosbox-x mouse.cpp: Mouse_Reset — AX=FFFFh instalado, BX=nº botones.
                // El reset también desinstala el callback de usuario (spec Microsoft).
                cpu.AX = 0xFFFF;
                cpu.BX = 3;
                Buttons = 0;
                _mickeyX = 0; _mickeyY = 0;
                for (int i = 0; i < 3; i++)
                    _pressCount[i] = _releaseCount[i] = 0;
                _cbMask = 0; _cbSeg = 0; _cbOff = 0;
                _cbEvents.Clear();
                // Mouse_Reset llama a Mouse_AfterNewVideoMode(false): el reset rehace el rango
                // del modo ACTUAL y deja el cursor centrado en él.
                OnVideoModeChanged(_videoMode);
                CenterCursor();
                ResetHostInputState();
                break;

            case 0x0001: // Show cursor
                // El render lo hace el juego; solo llevamos el contador (AX=2A lo expone)
                if (_hidden > 0) _hidden--;
                break;

            case 0x0002: // Hide cursor
                _hidden++;
                break;

            case 0x0003: // Get position and button status
                // dosbox-x mouse.cpp: Mouse_GetStatus. Do NOT capture the presented frame here.
                // Measured on a test game in the oracle (DOSBox): the game's software-cursor SHOW
                // routine polls AH=03 for the position and only AFTER that draws the sprite, and it
                // runs right after a HIDE (erase). So at this poll the cursor is ERASED — capturing
                // here yields the one guaranteed-cursorless frame per redraw = the move-flicker.
                // Presentation is clock-driven in MachineTimer (async VGA scan-out model), like the
                // oracle. See VideoState.CaptureFrontBuffer.
                cpu.BX = Buttons;
                cpu.CX = ExposeX(MouseX);
                cpu.DX = ExposeY(MouseY);
                break;

            case 0x0004: // Set cursor position
                MouseX = ReceiveX(cpu.CX);
                MouseY = ReceiveY(cpu.DX);
                break;

            case 0x0005: // Get button press information
                // dosbox-x mouse.cpp: Mouse_GetPressInformation
                // BX = índice de botón (0=left, 1=right, 2=middle)
                {
                    int btn = cpu.BX & 3;
                    if (btn < 3)
                    {
                        cpu.BX = _pressCount[btn];
                        cpu.CX = ExposeX(_pressX[btn]);
                        cpu.DX = ExposeY(_pressY[btn]);
                        _pressCount[btn] = 0; // resetear contador tras leer
                    }
                    else
                    {
                        cpu.BX = 0; cpu.CX = 0; cpu.DX = 0;
                    }
                    cpu.AX = Buttons; // estado actual de botones en AX
                }
                break;

            case 0x0006: // Get button release information
                // dosbox-x mouse.cpp: Mouse_GetReleaseInformation
                {
                    int btn = cpu.BX & 3;
                    if (btn < 3)
                    {
                        cpu.BX = _releaseCount[btn];
                        cpu.CX = ExposeX(_releaseX[btn]);
                        cpu.DX = ExposeY(_releaseY[btn]);
                        _releaseCount[btn] = 0;
                    }
                    else
                    {
                        cpu.BX = 0; cpu.CX = 0; cpu.DX = 0;
                    }
                    cpu.AX = Buttons;
                }
                break;

            case 0x0007: // Set horizontal range (min en CX, max en DX)
                // dosbox-x mouse.cpp Mouse_SetXRange (swap si vienen invertidos). El host
                // controla el clamp real; el driver guarda el rango (AX=26/31 lo leen).
                if ((short)cpu.CX <= (short)cpu.DX) { _minX = cpu.CX; _maxX = cpu.DX; }
                else { _minX = cpu.DX; _maxX = cpu.CX; }
                _rangeSetX = true;
                ActualizaEscalaVirtual(ejeX: true);
                break;

            case 0x0008: // Set vertical range
                if ((short)cpu.CX <= (short)cpu.DX) { _minY = cpu.CX; _maxY = cpu.DX; }
                else { _minY = cpu.DX; _maxY = cpu.CX; }
                _rangeSetY = true;
                ActualizaEscalaVirtual(ejeX: false);
                break;

            case 0x0009: // Set graphics cursor shape
                // dosbox-x mouse.cpp: Mouse_SetGraphicsCursor — BX/CX = hot spot,
                // ES:DX → bitmap 16×16 (el render lo hace el juego; guardamos el hot spot).
                _hotX = cpu.BX; _hotY = cpu.CX;
                break;

            case 0x000A: // Set text cursor type
                // dosbox-x mouse.cpp: Mouse_SetTextCursor — BX=0 sw, BX=1 hw (AX=25 lo expone)
                _cursorType = (byte)(cpu.BX & 1);
                break;

            case 0x000D: // Light pen emulation ON  (no implementado también en dosbox-x)
            case 0x000E: // Light pen emulation OFF
                break;

            case 0x000B: // Read motion counters (mickeys acumulados desde la última llamada)
                // dosbox-x mouse.cpp: Mouse_GetMotion
                // Devuelve CX = delta X mickeys, DX = delta Y mickeys. Reset tras leer.
                cpu.CX = (ushort)_mickeyX;
                cpu.DX = (ushort)_mickeyY;
                _mickeyX = 0;
                _mickeyY = 0;
                break;

            case 0x000C: // Set user-defined interrupt subroutine
                // dosbox-x mouse.cpp: Mouse_SetEventHandler
                // CX = máscara de eventos, ES:DX → callback. Se despacha en ServiceCallback.
                _cbMask = cpu.CX;
                _cbSeg = cpu.ES;
                _cbOff = cpu.DX;
                _cbPm = Memory.SegmentosSonSelectores;
                _cbSs = cpu.SS; _cbSp = cpu.SP;   // la pila del mundo donde vive el callback
                if (_logInt33)
                    LogInt33($"cb  REGISTRO ES:DX={cpu.ES:X4}:{cpu.DX:X4} EDX={cpu.EDX:X8} " +
                             $"mask={cpu.CX:X4} desde CS:IP={cpu.CS:X4}:{Interp.LastPc & 0xFFFF:X4} " +
                             $"pm={(Memory.SegmentosSonSelectores ? 1 : 0)} msw={cpu.Msw:X8}");
                break;

            case 0x000F: // Set mickey/pixel ratio (CX = horizontal, DX = vertical)
                // dosbox-x mouse.cpp: no-op en modo emulado básico.
                break;

            case 0x0010: // Set mouse exclusion area
                // dosbox-x mouse.cpp: Mouse_SetExclusionArea
                // El cursor no se muestra en el rectángulo CX/DX–SI/DI. STUB.
                break;

            case 0x0011: // CuteMouse wheel API check (dosbox-x mouse.cpp case 0x11)
                cpu.AX = 0x574D;   // 'WM' — firma de detección
                cpu.BX = 0;        // capabilities reservadas
                cpu.CX = 1;        // rueda soportada
                break;

            case 0x0012: // Set large graphics cursor block — no implementado (igual dosbox-x)
                break;

            case 0x0013: // Define double-speed threshold (DX=0 → 64, dosbox-x)
                _dblSpeedThreshold = cpu.DX != 0 ? cpu.DX : (ushort)64;
                break;

            case 0x0015: // Get driver storage requirements
                // dosbox-x mouse.cpp: BX = sizeof(mouse) del driver
                cpu.BX = DriverStateSize;
                break;

            case 0x0018: // Set alternate mouse user handler — no implementado (igual dosbox-x)
            case 0x0019: // Return user alternate interrupt vector — ídem
                break;

            case 0x001A: // Set mouse sensitivity (BX = horizontal, CX = vertical, DX = doublespeed threshold)
                // dosbox-x mouse.cpp: Mouse_SetSensitivity — no-op en modo básico.
                break;

            case 0x001C: // Set interrupt rate
                break;

            case 0x0014: // Swap user-defined event handler (devuelve el anterior)
                (cpu.CX, _cbMask) = (_cbMask, cpu.CX);
                (cpu.ES, _cbSeg) = (_cbSeg, cpu.ES);
                (cpu.DX, _cbOff) = (_cbOff, cpu.DX);
                _cbPm = Memory.SegmentosSonSelectores;   // el nuevo se registra en ESTE mundo
                break;

            case 0x0016: // Save driver state
            case 0x0017: // Restore driver state
                break;

            case 0x001B: // Get mouse sensitivity
                cpu.BX = 50; cpu.CX = 50; cpu.DX = 50;
                break;

            case 0x001D: // Set display page
                _page = (byte)cpu.BX;
                break;

            case 0x001E: // Get display page
                cpu.BX = _page;
                break;

            case 0x001F: // Disable mouse driver (dosbox-x: ES:BX=0, sin tocar AX)
                cpu.ES = 0; cpu.BX = 0;
                _enabled = false;
                _hidden = 1;
                break;

            case 0x0020: // Enable mouse driver
                _enabled = true;
                break;

            case 0x0021: // Software reset (también desinstala el callback)
                // dosbox-x mouse.cpp case 0x21 llama al MISMO Mouse_Reset que AX=0000.
                cpu.AX = 0xFFFF;
                cpu.BX = 3;
                _cbMask = 0; _cbSeg = 0; _cbOff = 0;
                _cbEvents.Clear();
                OnVideoModeChanged(_videoMode);
                CenterCursor();
                ResetHostInputState();
                break;

            case 0x0022: // Set language for messages
                _language = cpu.BX;
                break;

            case 0x0023: // Get language for messages
                cpu.BX = _language;
                break;

            case 0x0024: // Get mouse info
                cpu.BH = 8; cpu.BL = 5; // version 8.05
                cpu.CH = 0x04; // IRQ type (PS/2)
                cpu.CL = 0;
                break;

            case 0x0025: // Get general driver information (dosbox-x mouse.cpp case 0x25)
                cpu.AL = 1;                                            // nº de MDDs (guess dosbox)
                cpu.AH = (byte)(0x40 | (_cursorType << 4) | 0x01);     // integrado | cursor | 30 rep/s
                cpu.BX = 0; cpu.CX = 0; cpu.DX = 0;
                break;

            case 0x0026: // Get maximum virtual coordinates
                cpu.BX = (ushort)(_enabled ? 0x0000 : 0xFFFF);
                cpu.CX = _maxX;
                cpu.DX = _maxY;
                break;

            case 0x0027: // Get screen/cursor masks and mickey counts
                cpu.AX = TextAndMask;
                cpu.BX = TextXorMask;
                cpu.CX = (ushort)_mickeyX;
                cpu.DX = (ushort)_mickeyY;
                _mickeyX = 0; _mickeyY = 0;
                break;

            case 0x0028: // Set video mode — TODO también en dosbox-x (no-op)
            case 0x0029: // Enumerate video modes — ídem
                break;

            case 0x002A: // Get cursor hot spot
                cpu.AL = (byte)(-_hidden);   // contador negativo de visibilidad (Microsoft)
                cpu.BX = _hotX;
                cpu.CX = _hotY;
                break;

            case 0x002B: // Load acceleration profiles — no-op (igual dosbox-x)
            case 0x002C: // Get acceleration profiles
            case 0x002D: // Select acceleration profile
            case 0x002E: // Set acceleration profile names
            case 0x002F: // Mouse hardware reset
            case 0x0030: // Get/set BallPoint information
                break;

            case 0x0031: // Get current minimum/maximum virtual coordinates
                cpu.AX = _minX;
                cpu.BX = _minY;
                cpu.CX = _maxX;
                cpu.DX = _maxY;
                break;

            case 0x0032: // Get active advanced functions — no-op (igual dosbox-x)
            case 0x0033: // Get switch settings and acceleration profile data
            case 0x0034: // Get initialization file
            case 0x0035: // LCD screen large pointer support
            case 0x004D: // Return pointer to copyright string — no-op (igual dosbox-x)
            case 0x006D: // Get version string — ídem
            case 0x53C1: // Logitech CyberMan — ídem
                break;
        }
    }
}
