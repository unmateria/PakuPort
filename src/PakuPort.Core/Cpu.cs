using PakuPort.Core.Platform;
using PakuPort.Judge.Oracle;

namespace PakuPort.Core;

/// <summary>
/// Estado de CPU 8086 real-mode + aritmética con flags <b>eager</b>. La semántica de
/// cada familia replica el Intel SDM y se PRUEBA contra una referencia independiente (Unicorn,
/// tools/gen_opcode_vectors.py). La aritmética vive aquí, centralizada y testeada, en métodos que
/// fijan los flags y DEVUELVEN el resultado; el lifter solo los invoca. Flags como riesgo nº1: por
/// eso AF/OF/CF se calculan explícitamente por nibble/signo, no "a ojo".
/// </summary>
public sealed class Cpu
{
    public ushort AX, BX, CX, DX, SP, BP, SI, DI;

    // LA CACHÉ OCULTA DE DESCRIPTORES ES POR REGISTRO, y por eso los seis segmentos son
    // propiedades: cada carga marca SU bit en `SegRecargados`. Cambiar de modo no reinterpreta
    // los segmentos —la base vieja sigue vigente hasta que ESE registro se recarga—, y sin saber
    // cuál se ha recargado no se puede distinguir "DS conserva su base de modo real" de "DS ya
    // es un selector". Marcar por VALOR de selector no sirve: un registro puede recargarse con
    // el mismo valor que ya tenía, y eso también consume la caché (medido en un juego DOS/16M, cuyo kernel
    // hace `mov ds,dx` con el selector que DS ya llevaba). Coste: una OR por carga de segmento.
    public const byte BitCS = 1, BitDS = 2, BitES = 4, BitSS = 8, BitFS = 16, BitGS = 32;
    public const byte TodosLosSegmentos = 0x3F;

    /// <summary>Qué registros de segmento se han recargado desde el último cambio de modo.
    /// <see cref="Interp"/> lo pone a cero al cambiar de modo y lo consulta para saber a qué
    /// selectores les sigue valiendo la base cacheada.</summary>
    public byte SegRecargados = TodosLosSegmentos;

    private ushort _cs, _es, _ss, _fs, _gs;
    public ushort CS { get => _cs; set { _cs = value; SegRecargados |= BitCS; } }
    public ushort ES { get => _es; set { _es = value; SegRecargados |= BitES; } }
    public ushort SS { get => _ss; set { _ss = value; SegRecargados |= BitSS; SsRecargas++; } }

    /// <summary>Cuenta de recargas de SS. La usa el intérprete para cachear el bit B del
    /// descriptor de la pila (el ancho SP/ESP) sin releer la GDT en cada push.</summary>
    public uint SsRecargas;
    public ushort FS { get => _fs; set { _fs = value; SegRecargados |= BitFS; } }
    public ushort GS { get => _gs; set { _gs = value; SegRecargados |= BitGS; } }

    private ushort _ds;
    public ushort DS
    {
        get => _ds;
        set
        {
            if (_dsWatch && value == 0x00BD && _ds != 0x00BD)
                Console.Error.WriteLine($"  [DS->00BD] from {_ds:X4} at\n{new System.Diagnostics.StackTrace().ToString().Split('\n').Where(l => l.Contains("Fn_")).Take(6).Aggregate("", (a, b) => a + "    " + b.Trim() + "\n")}");
            _ds = value;
            SegRecargados |= BitDS;
        }
    }
    /// <summary>PAKU_DS_WATCH=1: avisa de cada cambio de DS. Para cazar el juego que pierde su
    /// segmento de datos, que se ve como lecturas de basura muy lejos de la causa.</summary>
    private static readonly bool _dsWatch = Environment.GetEnvironmentVariable("PAKU_DS_WATCH") == "1";
    public int LoadBase;

    /// <summary>Ciclos (Steps) por ms de tiempo real. 0 = sin governor (velocidad libre).
    /// Modelo DOSBox cycles: los juegos CPU-paced de la época necesitan una máquina de época.</summary>
    public int CyclesPerMs;

    // 32-bit register access (386 real mode — upper 16 bits preserved across 16-bit ops)
    private ushort _eaxHi, _ebxHi, _ecxHi, _edxHi, _espHi, _ebpHi, _esiHi, _ediHi;
    public uint EAX { get => (uint)(_eaxHi << 16) | AX; set { AX = (ushort)value; _eaxHi = (ushort)(value >> 16); } }
    public uint EBX { get => (uint)(_ebxHi << 16) | BX; set { BX = (ushort)value; _ebxHi = (ushort)(value >> 16); } }
    public uint ECX { get => (uint)(_ecxHi << 16) | CX; set { CX = (ushort)value; _ecxHi = (ushort)(value >> 16); } }
    public uint EDX { get => (uint)(_edxHi << 16) | DX; set { DX = (ushort)value; _edxHi = (ushort)(value >> 16); } }
    public uint ESP { get => (uint)(_espHi << 16) | SP; set { SP = (ushort)value; _espHi = (ushort)(value >> 16); } }
    public uint EBP { get => (uint)(_ebpHi << 16) | BP; set { BP = (ushort)value; _ebpHi = (ushort)(value >> 16); } }
    public uint ESI { get => (uint)(_esiHi << 16) | SI; set { SI = (ushort)value; _esiHi = (ushort)(value >> 16); } }
    public uint EDI { get => (uint)(_ediHi << 16) | DI; set { DI = (ushort)value; _ediHi = (ushort)(value >> 16); } }

    public byte AL { get => (byte)AX; set => AX = (ushort)((AX & 0xFF00) | value); }
    public byte AH { get => (byte)(AX >> 8); set => AX = (ushort)((AX & 0x00FF) | (value << 8)); }
    public byte BL { get => (byte)BX; set => BX = (ushort)((BX & 0xFF00) | value); }
    public byte BH { get => (byte)(BX >> 8); set => BX = (ushort)((BX & 0x00FF) | (value << 8)); }
    public byte CL { get => (byte)CX; set => CX = (ushort)((CX & 0xFF00) | value); }
    public byte CH { get => (byte)(CX >> 8); set => CX = (ushort)((CX & 0x00FF) | (value << 8)); }
    public byte DL { get => (byte)DX; set => DX = (ushort)((DX & 0xFF00) | value); }
    public byte DH { get => (byte)(DX >> 8); set => DX = (ushort)((DX & 0x00FF) | (value << 8)); }

    public bool CF, PF, AF, ZF, SF, OF, DF, IF;

    /// <summary>TF (bit 8): paso a paso. Tras cada instrucción con TF puesto la CPU dispara un
    /// INT 1. No es cosa de depuradores: los programas PROTEGIDOS lo usan al revés — activan TF
    /// y descifran desde su propio manejador de INT 1, contando con que un depurador de verdad
    /// se lo comería. Ver el case Int3/Int1 de Interp (medido en tres juegos protegidos).</summary>
    public bool TF;

    /// <summary>Un <c>retf</c> lifteado acaba de ejecutarse. La rutina que sale por retf pudo
    /// desapilar el marco far de un llamador EXTERIOR (idiom "escape": pop de la dirección de
    /// retorno near + jmp a un epílogo compartido que termina en retf). Como las llamadas se
    /// modelan con llamadas C# anidadas, cada call-site comprueba este flag al volver: si SP
    /// quedó POR ENCIMA del nivel de entrada de la función (su propio marco ya no existe),
    /// propaga el return; si no (p.ej. idiom <c>push cs; call near</c>), lo apaga y sigue.
    /// Las fronteras de despacho del host (ISR/callbacks) lo limpian tras servir.
    /// Setter: consumir el flag (=false) cierra el episodio de unwind y limpia el latch
    /// FarRetBottom* (ver NoteFarRetUnwind).</summary>
    public bool FarRet
    {
        get => _farRet;
        set { _farRet = value; if (!value) FarRetBottomValid = false; }
    }
    private bool _farRet;

    /// <summary>PRIMER destino descartado del episodio de unwind en curso (gate DEEP,
    /// clase H): cuando el unwind FarRet llega al fondo del hilo, el destino del
    /// PRIMER retf del episodio es lo que el hardware ejecutaría a continuación — los
    /// FarRetCs/Ip posteriores son artefactos de los resume inline del propio unwind.
    /// El host reanuda aquí en vez de dejar morir el hilo.</summary>
    public ushort FarRetBottomCs, FarRetBottomIp;
    public bool FarRetBottomValid;

    /// <summary>Coprocesador x87 (pila de coma flotante). Ver <see cref="Fpu"/>.</summary>
    public readonly Fpu Fpu = new();

    /// <summary>Plataforma DOS (INT handlers, puertos de E/S). Cuando es null, INTs y puertos
    /// son no-op (modo verificación: equivalente a los stubs del juez byte-exacto).</summary>
    public IPlatform? Platform;

    /// <summary>Presupuesto de pasos (visitas a etiquetas de salto). Acota la ejecución para que un
    /// bucle desbocado (p.ej. una espera de hardware con INT stubbeado) se reporte como excepción en
    /// vez de COLGAR el runner. Generoso para bucles legítimos (&lt; 0x10000 iteraciones).</summary>
    public long Budget = 20_000_000;

    /// <summary>Budget bounds the number of steps, but NOT the depth of the C# call stack: a
    /// guest whose main loop is wired with far calls that never return turns into nested C#
    /// calls that grow without bound, and .NET's StackOverflowException is NOT catchable — it
    /// takes the whole process down, so `pakuport export` dies without exporting anything.
    /// Measured victims: two test games (one with the cycle Fn_0A8B6 -> Fn_3141F -> Fn_28CAD ->
    /// Fn_33489 -> Fn_0A8B6, the other with Fn_10664 recursing into itself). A bigger thread stack does
    /// not help — 256 MB was tried and the recursion is unbounded, not merely deep.
    /// Every generated function calls this on entry (Emitter), which converts the process kill
    /// into an ordinary catchable exception carrying the guest address that was running.</summary>
    public static void GuardStack(Cpu c)
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new GuestStackDepthException(c.CS, c.SP);
    }

    // ---- Modo protegido de 16 bits (extensores estilo DOS/16M de Rational) ----
    // Estos extensores NO usan DPMI: cargan su propia GDT/IDT con lgdt/lidt y encienden PE con
    // lmsw, y siguen ejecutando código de 16 bits ya en modo protegido. El mundo de selectores
    // que PakuPort tenía era el de DPMI (FlatServices), donde los descriptores los pide el
    // cliente por INT 31h y los inventa el host; aquí las tablas son del programa y están en su
    // memoria, así que la base de un selector se LEE de la GDT que él mismo escribió.
    public uint GdtBase, IdtBase;
    public ushort GdtLimit, IdtLimit, Ldtr, TaskReg;
    /// <summary>MSW / CR0 bajo: bit 0 = PE (modo protegido), 1 = MP, 2 = EM, 3 = TS.</summary>
    public uint Msw;
    /// <summary>CR3: base física del directorio de páginas. PakuPort NO traduce (lineal =
    /// física), pero lo guarda porque el programa lo LEE para encontrar sus propias tablas —
    /// devolverle 0 le decía que su directorio está encima de la IVT.</summary>
    public uint Cr3;
    /// <summary>Ya se avisó de que el programa encendió la paginación (aviso único).</summary>
    public bool PagingAvisado;

    public System.Threading.ManualResetEventSlim? FrameGate;
    /// <summary>El host lo asigna para servir interrupciones hardware pendientes (timer/teclado)
    /// en el límite de frame, sobre el hilo del juego (sin carrera). Típicamente
    /// <c>c =&gt; platform.ServicePendingInterrupts(c, mem)</c>.</summary>
    public Action<Cpu>? OnFrameBoundary;
    /// <summary>Poll periódico barato (cada 4096 Steps) sobre el hilo del juego. El host lo
    /// engancha a <c>MachineTimer.Poll</c> (modelo DOSBox: el PIT vive en el hilo del juego,
    /// gobernado por tiempo real con granularidad de instrucciones). null (defecto) = coste cero
    /// para juez/interp-verify.</summary>
    public Action<Cpu>? OnPeriodicPoll;

    /// <summary>Máscara de Steps del poll periódico (por defecto 4096). DOSBox entrega los
    /// eventos del PIC en el ciclo EXACTO (PIC_AddEvent), así que un bucle del invitado que
    /// espera unos miles de instrucciones por una IRQ siempre la ve; aquí el poll es periódico,
    /// y una ventana de 4096 Steps se traga deadlines más cortos que la propia espera del
    /// invitado. Un dispositivo con un evento de plazo corto en vuelo la baja con
    /// <see cref="RequestFinePoll"/> mientras dure.</summary>
    public static long PollMask = DefaultPollMask;
    public const long DefaultPollMask = 0xFFF;
    private const long FinePollMask = 0x3F;
    private static int _finePollRequests;

    /// <summary>Pide (on=true) o suelta (on=false) el poll fino de eventos hardware. Balanceado
    /// por contador: varios dispositivos pueden tener plazos cortos a la vez.</summary>
    public static void RequestFinePoll(bool on)
    {
        int n = on ? System.Threading.Interlocked.Increment(ref _finePollRequests)
                   : System.Threading.Interlocked.Decrement(ref _finePollRequests);
        if (n < 0) { System.Threading.Interlocked.Exchange(ref _finePollRequests, 0); n = 0; }
        PollMask = n > 0 ? FinePollMask : DefaultPollMask;
    }

    public HashSet<int>? FarDispatchMisses;
    /// <summary>Huecos tocados en runtime: stubs "camino no ejercitado" y fallthroughs a bytes
    /// nunca decodificados. Claves en file-key space (fn.Base + offset).</summary>
    public HashSet<int>? GapHits;

    /// <summary>Destino que el último retf desapiló (lo escribe LiftRet/Interp ANTES de ajustar
    /// SP). Telemetría de la clase co-rutina: cuando un guard de unwind
    /// FarRet PROPAGA (el marco actual fue desapilado por el callee), este destino se descarta
    /// hoy en silencio — NoteFarRetUnwind lo registra para gaps.log. Solo lectura/registro,
    /// cero cambio de comportamiento; el fix real (redirigir aquí) queda para el Frente 1.</summary>
    public ushort FarRetCs, FarRetIp;
    /// <summary>WHERE the last retf popped its destination (SS:SP before the pop). The near-guard
    /// redirect (gate NCORA) requires the pop-site to be IMMEDIATELY ABOVE the call-site's return
    /// cell (_spc+2, same SS) — the definition of a hand-pushed continuation AT that site. The
    /// same destination can arrive via a driver-init episode popped on a PRIVATE stack (a test game's
    /// boot: 111A:1092 travels both ways) where redirecting is lethal; the pop-site separates them.</summary>
    public ushort FarRetSs, FarRetSp;
    /// <summary>Unwinds FarRet observados: (CS del marco desenrollado &lt;&lt; 32) | (FarRetCs &lt;&lt; 16)
    /// | FarRetIp, deduplicados. null (defecto) = sin coste; el host exportado lo activa.</summary>
    public HashSet<long>? FarRetUnwinds;
    public void NoteFarRetUnwind()
    {
        // Latch del episodio: solo el PRIMER descarte con el unwind vivo (_farRet). Los
        // Note posteriores del mismo episodio (marcos superiores propagando) no lo pisan;
        // un Note con FarRet ya consumido (resume inline del guard STOP) no latchea.
        if (_farRet && !FarRetBottomValid)
        {
            FarRetBottomCs = FarRetCs; FarRetBottomIp = FarRetIp; FarRetBottomValid = true;
        }
        FarRetUnwinds?.Add(((long)CS << 32) | ((long)FarRetCs << 16) | FarRetIp);
    }

    /// <summary>FarRet co-routine redirection (Frente 1) master switch. OFF by default so the JUDGE and,
    /// crucially, the EXPORT-time discovery probe run the proven guard (resume inline): a redirect during
    /// the probe would run <see cref="Interp"/>, perturbing which functions get discovered (non-deterministic
    /// budget, spurious seeds). The generated host turns it ON — that is where the co-routine is executed
    /// (and where CORO, the gate, exercises it via a real export).</summary>
    public static bool FarRetRedirectEnabled;

    /// <summary>Telemetry: FarRet unwinds redirected to their real destination (co-routine) and those
    /// skipped by the intra-module fail-safe. Redirects ~0 outside co-routine blitters in a healthy game.
    /// Logged individually under PAKU_GAP_TRACE.</summary>
    public static long FarRetRedirects, FarRetSkips;

    /// <summary>Decide whether a stopped FarRet unwind (the caller has already checked FarRetIp differs
    /// from this call-site's inline continuation) may be redirected to its real destination. Fail-safe:
    /// the co-routine class is INTRA-module (§1.1) — the retf returns within the same segment it was
    /// called in — so require <see cref="FarRetCs"/> == <see cref="CS"/>. A garbage CS from the near/far
    /// ±2 stack imbalance (§1.3: 0000, or a stable selector into virgin memory like 1F22 that defeated a
    /// CS-range check) is popped from a mis-aligned stack and practically never equals the running CS →
    /// rejected, the old guard runs (resume inline, no behaviour change).</summary>
    public bool ShouldFarRetRedirect()
    {
        if (!FarRetRedirectEnabled) return false;
        if (FarRetCs == CS)
        {
            FarRetRedirects++;
            if (GapTrace) Console.Error.WriteLine($"[FREDIR] cs={CS:X4} -> {FarRetCs:X4}:{FarRetIp:X4}");
            return true;
        }
        FarRetSkips++;
        if (GapTrace)
            Console.Error.WriteLine($"[REDIR-SKIP] CS={CS:X4} -> {FarRetCs:X4}:{FarRetIp:X4} (cross-segment, not a co-routine)");
        return false;
    }

    /// <summary>Near-guard co-routine redirect master switch (5º intento). OFF by default after a
    /// manual-playtest regression: in a real game the redirect recursed Interp.Run per lifted↔interp
    /// bounce (BUILD-click killed the host with no exit.log = stack overflow) and ran continuations
    /// hardware wouldn't at that point (wagon lists vanished). PAKU_NREDIR=1 re-enables it for
    /// experiments; the NCORA gate runs with it ON to keep the mechanism proven while the shipped
    /// default stays safe. The proper fix needs the oracle side-by-side (menus front).</summary>
    public static readonly bool NearRedirectEnabled =
        Environment.GetEnvironmentVariable("PAKU_NREDIR") == "1";

    /// <summary>Near-site variant of <see cref="ShouldFarRetRedirect"/> (5º intento, gate NCORA):
    /// compares against the CALL-SITE's CS captured before the call (_ncs) instead of the running
    /// c.CS — c.CS arrives STALE at a near guard mid-unwind (the propagate branches skip the
    /// restores), so comparing against it vetoes genuine same-segment continuations (measured).</summary>
    public bool ShouldFarRetRedirectTo(ushort siteCs)
    {
        if (!NearRedirectEnabled) return false;
        if (!FarRetRedirectEnabled) return false;
        if (FarRetCs == siteCs) { FarRetRedirects++; return true; }
        // (near-guard fires are traced by the [NREDIR] line at the call-site, which has the site)
        FarRetSkips++;
        if (GapTrace)
            Console.Error.WriteLine($"[REDIR-SKIP] siteCS={siteCs:X4} -> {FarRetCs:X4}:{FarRetIp:X4} (cross-segment, not a co-routine)");
        return false;
    }
    public static readonly bool GapTrace = Environment.GetEnvironmentVariable("PAKU_GAP_TRACE") == "1";
    /// <summary>Sesgo de coordenadas del mundo de memoria vs el key-space del código lifteado.
    /// El key-space es fileOffset = ghidra + 0x200 (imagen normalizada en loadSeg: linear =
    /// loadSeg*16 + ghidra). Con boot-real/snapshot la memoria es memimage (X-space):
    /// linear = loadSeg*16 + xOfGhidra0 + ghidra. FarDispatch debe restar este sesgo
    /// (xOfGhidra0, p.ej. 0x129 en un juego de prueba) o TODOS los far dispatch por puntero caen
    /// 0x129 arriba (corrupción silenciosa). 0 por defecto (juez/run normal intactos).</summary>
    public int ImageBias;
    public const ushort IvtStubSeg = 0xF000;

    /// <summary>Área de stubs de la IVT: DOS BYTES por vector (NOP; IRET) en F000:1200+n*2.
    ///
    /// El stub no puede empezar por 0xCF. La comprobación canónica de "¿hay driver instalado?"
    /// (Ralf Brown, INT 33h) es: vector no nulo Y primer byte distinto de CF, porque un vector
    /// que apunta a un IRET pelado es un vector sin nada detrás. Con un stub de UN byte 0xCF,
    /// todo juego que se detecte así concluye que la máquina no tiene ese dispositivo — un juego de
    /// prueba lo hace literal (`mov bl,es:[bx]` / `cmp bl,0CFh`) y por eso no llamaba al ratón ni una vez.
    /// El NOP delante deja pasar la comprobación y el IRET sigue devolviendo el control como
    /// siempre (modelo DOSBox: callback en F000:xxxx seguido de IRET).
    ///
    /// El área histórica de un byte (F000:00nn) se sigue escribiendo y se sigue despachando:
    /// hay código que salta ahí con la dirección puesta a mano en vez de leer el vector.</summary>
    public const ushort IvtStubBase = 0x1200;

    public static ushort IvtStubOff(byte intNo) => (ushort)(IvtStubBase + intNo * 2);

    /// <summary>Punto de entrada del CAMBIO A MODO PROTEGIDO que el host DPMI publica en
    /// <c>INT 2Fh AX=1687h</c> (ES:DI). El cliente lo llama con un `call far` y, si todo va bien,
    /// vuelve ya en modo protegido con sus selectores puestos.
    ///
    /// Va justo detrás del área de stubs de la IVT (0x1200 + 256×2 = 0x1400), así que no pisa
    /// ninguno. En memoria hay <c>NOP; RETF</c>: el NOP es donde el intérprete engancha el
    /// traspaso, y el RETF deja una salida limpia si nadie lo atiende.</summary>
    public const ushort DpmiSwitchOff = 0x1400;

    /// <summary>Puntos de entrada que el host DPMI publica en <c>INT 31h AX=0305</c> (guardar y
    /// restaurar estado) y <c>AX=0306</c> (cambio de modo DIRECTO). Van pegados al de arriba.</summary>
    public const ushort DpmiSaveRealOff = 0x1402, DpmiSavePmOff = 0x1404,
                        DpmiRawSwitchRealOff = 0x1406, DpmiRawSwitchPmOff = 0x1408;

    /// <summary>La CADENA DE RETORNO del callback del ratón por IRQ12, calcada del original
    /// (DOSBox `CB_IRQ12` / `CB_IRQ12_RET`).
    ///
    /// El ISR de la IRQ12 de un PC salva los registros ANTES de llamar al callback del usuario y
    /// los restaura después: `sti; push ds; push es; pushad` a la ida, `popad; pop es; pop ds;
    /// iret` a la vuelta. Sin eso el código interrumpido continúa con los registros que el
    /// callback haya pisado — y ahí es donde el kernel de un extensor se descarrila, lejísimos
    /// de la causa. Son 36 bytes de pila, y la falta se midió como tal: la pila del trampolín
    /// bajo PakuPort quedaba 40 bytes por encima de la del original (36 del guardado + 4 del
    /// far-return que también faltaba).
    ///
    /// Dos stubs seguidos, detrás del stub VESA de 0x140A:
    /// Van en memoria del invitado, y no en C#, porque el trampolín del extensor llega a ellos
    /// por su propio `iret`: tienen que ser instrucciones de verdad.</summary>
    ///   1410  <c>CLI; RETF</c>                        (el `uir` del original, que es RETF_CLI)
    ///   1412  <c>POPAD; POP ES; POP DS; IRET</c>       (el epílogo: deshace el guardado y vuelve)
    /// Van DELANTE del área de stubs de la IVT (0x1200) y no detrás de los puntos DPMI, porque
    /// detrás NO es seguro: medido en un juego DOS/4GW Pro, el kernel del extensor escribe en F000:1410
    /// desde su selector 0070 con la paginación puesta, y al pisar el `cli` la cadena de retorno
    /// se convertía en ceros — el `iret` del trampolín caía en ellos, recorría el segmento entero
    /// y acababa ejecutando la IVT. La zona 0x1200+ lleva todos los juegos probados sin que nadie la toque.
    public const ushort CbRatonUirOff = 0x11F0, CbRatonFinOff = 0x11F2;

    /// <summary>Nº de INT si <paramref name="off"/> es el ARRANQUE de un stub del área nueva;
    /// -1 si no lo es (incluido el segundo byte, el IRET: ahí no se despacha nada).</summary>
    public static int IvtStubInt(ushort off)
    {
        int rel = off - IvtStubBase;
        return (rel >= 0 && rel < 0x200 && (rel & 1) == 0) ? rel >> 1 : -1;
    }

    public void Iret(Memory m)
    {
        ushort ip  = m.RdU16(SS, SP); SP += 2;
        ushort cs  = m.RdU16(SS, SP); SP += 2;
        SetFlagsWord(m.RdU16(SS, SP)); SP += 2;
        // Hardware-faithful: IRET restores CS. Without this, an ISR chain that goes
        // through FarDispatch (which sets c.CS to the dispatch target, e.g. the F000
        // stub) returns to the interrupted Interp with a stale CS, and the next fetch
        // reads from the wrong segment (zeros walk -> garbage far calls -> unwind).
        CS = cs;
        if (cs == IvtStubSeg && Platform != null)
        {
            int stubInt = IvtStubInt(ip);              // área de 2 bytes (la que apuntan los vectores)
            if (stubInt < 0 && ip <= 0xFF) stubInt = ip; // área histórica de 1 byte
            if (stubInt >= 0) Platform.Int((byte)stubInt, this, m);
        }
    }

    /// <summary>IRETD: igual que <see cref="Iret"/> pero desapilando palabras de 4 bytes
    /// (EIP, CS con la mitad alta a descartar, EFLAGS). NO lo cubre la red de la fase 0 — no
    /// hay forma de montarle un marco de interrupción en un vector de opcode —, así que aquí
    /// está escrito como espejo literal de Iret y su verificación llega con el host DPMI.</summary>
    public void Iretd(Memory m)
    {
        ushort ip = (ushort)m.RdU32(SS, SP); SP += 4;
        ushort cs = (ushort)m.RdU32(SS, SP); SP += 4;
        SetEFlagsWord(m.RdU32(SS, SP)); SP += 4;
        CS = cs;
        if (cs == IvtStubSeg && Platform != null)
        {
            int stubInt = IvtStubInt(ip);
            if (stubInt < 0 && ip <= 0xFF) stubInt = ip;
            if (stubInt >= 0) Platform.Int((byte)stubInt, this, m);
        }
    }

    /// <summary>Reloj monótono de la máquina: pasos ejecutados en total (etiquetas del código
    /// lifteado + instrucciones interpretadas). Es la base de tiempo fina del PIT (los juegos
    /// calibran la velocidad de CPU midiendo el contador del 8253 alrededor de un delay loop:
    /// si el contador no avanza entre dos lecturas, la resta da 0 y el div revienta).</summary>
    public static long TickCount;

    /// <summary>Cobra UN elemento de un bucle REP (movs/stos/lods/scas/cmps/outs/ins).
    ///
    /// DOSBox-X con core=normal descuenta un ciclo por CADA elemento dentro del bucle REP
    /// (`if ((--CPU_Cycles) &lt;= 0) break;` en src/cpu/core_normal/string.h), no uno por la
    /// instrucción entera. Sin esto un `rep movsw` de 1000 palabras nos costaba 1 tick y a
    /// DOSBox 1000, así que `cyclesPerMs=N` dejaba de equivaler a `cycles=N` en cuanto un
    /// juego blitea con REP — que es lo que hacen todos. Medido en un juego de
    /// prueba: 110x más rápido que el oráculo en la sonda `tools/speed_probe/REPTEST.COM`, y
    /// en juego 159 redibujados/s del mapa contra los ~45 del original. Como el juego no
    /// tiene reloj propio, esa divergencia NO la puede compensar ningún `cyclesPerMs`: el
    /// blit y el cómputo puro se escalan por factores distintos.
    ///
    /// Sólo cuenta el tick y atiende el poll periódico; NO toca Budget ni FrameGate, así que
    /// un REP largo no se puede quedar a medias en una frontera de frame. El TickCount avanza
    /// de uno en uno, luego la prueba de bits del poll sigue siendo exacta.</summary>
    public void ChargeStringElement()
    {
        TickCount++;
        if ((TickCount & PollMask) == 0)
        {
            OnPeriodicPoll?.Invoke(this);
            if (Interp.SamplePcEnabled) Interp.SamplePc(this);
            // PAKU_AUTOEXIT_INSN: cortar la partida en la misma instruccion en cualquier
            // corrida, que es lo que hace comparable un A/B. Va aqui por lo mismo que la
            // linea de arriba: esta rama ya se tomaba, asi que apagado no cuesta nada.
            if (CorteDeterminista.Insn > 0) CorteDeterminista.Comprueba(this);
            // PAKU_RASTRO: donde estaba el programa cada 4096 instrucciones. Misma rama, mismo
            // motivo: sirve para saber DONDE dejan de coincidir dos corridas que deberian ser una.
            if (RastroDeterminismo.Activo) RastroDeterminismo.Anota(this);
        }
    }

    public bool Step()
    {
        TickCount++;
        // Poll de temporizador por tiempo real en el hilo del juego (modelo DOSBox). Un test de
        // bits por Step; 4096 Steps ≈ 0,27 ms a 15 M insns/s. El PIT entrega IRQ0 a la frecuencia
        // programada tick a tick (sin colapso en el IRR) en vez de ~30/s en el límite de frame.
        if ((TickCount & PollMask) == 0)
        {
            OnPeriodicPoll?.Invoke(this);
            // PAKU_SAMPLE_PC_MS: periodic PC sampling for the offline scene segmenter. It rides
            // the poll branch on purpose — that check already exists, so when the flag is off the
            // instruction stream pays nothing (one static bool read every 4096 steps, inside a
            // branch that was taken anyway). See Interp.SamplePc.
            if (Interp.SamplePcEnabled) Interp.SamplePc(this);
            // PAKU_AUTOEXIT_INSN: cortar la partida en la misma instruccion en cualquier
            // corrida, que es lo que hace comparable un A/B. Va aqui por lo mismo que la
            // linea de arriba: esta rama ya se tomaba, asi que apagado no cuesta nada.
            if (CorteDeterminista.Insn > 0) CorteDeterminista.Comprueba(this);
            // PAKU_RASTRO: donde estaba el programa cada 4096 instrucciones. Misma rama, mismo
            // motivo: sirve para saber DONDE dejan de coincidir dos corridas que deberian ser una.
            if (RastroDeterminismo.Activo) RastroDeterminismo.Anota(this);
        }
        if (--Budget < 0)
        {
            if (FrameGate != null)
            {
                // SIN FRENO (PAKU_SIN_FRENO con cinta): no se espera a la ventana. La frontera
                // cae igual cada PresupuestoPorFrame instrucciones — y ademas en el MISMO sitio
                // en cualquier corrida, que con la espera dependia de cuando pintaba el anfitrion.
                if (PresupuestoPorFrame > 0 && PakuPort.Core.Platform.MachineTimer.SinFrenoAhora)
                    Budget = PresupuestoPorFrame;
                else
                {
                    FrameGate.Wait();
                    FrameGate.Reset();
                }
                OnFrameBoundary?.Invoke(this);
                return true;
            }
            return false;
        }
        return true;
    }

    /// <summary>Instrucciones por frontera de frame, tal como las fija el anfitrion. Solo se usa
    /// sin freno (ver <see cref="Step"/>); 0 = no se sabe.</summary>
    public long PresupuestoPorFrame;

    public static Cpu FromState(in CpuState s)
    {
        var c = new Cpu
        {
            AX = (ushort)s.Eax, BX = (ushort)s.Ebx, CX = (ushort)s.Ecx, DX = (ushort)s.Edx,
            SI = (ushort)s.Esi, DI = (ushort)s.Edi, BP = (ushort)s.Ebp, SP = (ushort)s.Esp,
            CS = s.Cs, DS = s.Ds, ES = s.Es, SS = s.Ss,
        };
        uint f = s.Eflags;
        c.CF = (f & (1u << 0)) != 0;
        c.PF = (f & (1u << 2)) != 0;
        c.AF = (f & (1u << 4)) != 0;
        c.ZF = (f & (1u << 6)) != 0;
        c.SF = (f & (1u << 7)) != 0;
        c.IF = (f & (1u << 9)) != 0;
        c.DF = (f & (1u << 10)) != 0;
        c.OF = (f & (1u << 11)) != 0;
        return c;
    }

    // ---------------- helpers de flags ----------------

    public static bool ParityEven(int v)
    {
        int b = v & 0xFF;
        b ^= b >> 4; b ^= b >> 2; b ^= b >> 1;
        return (b & 1) == 0;
    }

    /// <summary>Ensambla EFLAGS (32 bits, para PUSHFD).</summary>
    public uint EFlagsWord() => FlagsWord();
    /// <summary>Restaura EFLAGS (32 bits, para POPFD).</summary>
    public void SetEFlagsWord(uint f) => SetFlagsWord((ushort)f);

    // 386 real mode: IOPL (bits 12-13) and NT (bit 14) are writable via POPF and read back
    // via PUSHF; bit 15 reads 0. Dropping them is the 80286 signature that CPU-detection
    // stubs test for (e.g. "80386 processor is required").
    ushort _ioplNt;

    /// <summary>Nested Task flag (EFLAGS bit 14). Set by a task switch through a task gate
    /// (INT/exception), checked by IRET: with NT=1 the IRET returns via the TSS backlink
    /// instead of popping a stack frame. DOS/4GW Professional handles #PF with a task gate.</summary>
    public bool NT
    {
        get => (_ioplNt & 0x4000) != 0;
        set { if (value) _ioplNt |= 0x4000; else _ioplNt &= 0xBFFF; }
    }

    /// <summary>Ensambla el registro FLAGS (para PUSHF). Bit 1 reservado siempre a 1.</summary>
    public ushort FlagsWord()
    {
        ushort f = (ushort)(0x0002 | _ioplNt);
        if (CF) f |= 0x0001; if (PF) f |= 0x0004; if (AF) f |= 0x0010; if (ZF) f |= 0x0040;
        if (SF) f |= 0x0080; if (TF) f |= 0x0100; if (IF) f |= 0x0200; if (DF) f |= 0x0400;
        if (OF) f |= 0x0800;
        return f;
    }

    /// <summary>Restaura los flags desde una palabra (para POPF).</summary>
    public void SetFlagsWord(ushort f)
    {
        _ioplNt = (ushort)(f & 0x7000);
        CF = (f & 0x0001) != 0; PF = (f & 0x0004) != 0; AF = (f & 0x0010) != 0; ZF = (f & 0x0040) != 0;
        SF = (f & 0x0080) != 0; TF = (f & 0x0100) != 0; IF = (f & 0x0200) != 0; DF = (f & 0x0400) != 0;
        OF = (f & 0x0800) != 0;
    }

    private void SetSZP16(int res) { ZF = (res & 0xFFFF) == 0; SF = (res & 0x8000) != 0; PF = ParityEven(res); }
    private void SetSZP8(int res)  { ZF = (res & 0xFF) == 0;   SF = (res & 0x80) != 0;   PF = ParityEven(res); }

    // ---- ADD / ADC ----
    public ushort Add16(ushort a, ushort b, int carryIn = 0)
    {
        int r = a + b + carryIn; int res = r & 0xFFFF;
        CF = (uint)r > 0xFFFF;
        AF = ((a & 0xF) + (b & 0xF) + carryIn) > 0xF;
        OF = (((a ^ res) & (b ^ res)) & 0x8000) != 0;
        SetSZP16(res); return (ushort)res;
    }
    public byte Add8(byte a, byte b, int carryIn = 0)
    {
        int r = a + b + carryIn; int res = r & 0xFF;
        CF = (uint)r > 0xFF;
        AF = ((a & 0xF) + (b & 0xF) + carryIn) > 0xF;
        OF = (((a ^ res) & (b ^ res)) & 0x80) != 0;
        SetSZP8(res); return (byte)res;
    }
    public ushort Adc16(ushort a, ushort b) => Add16(a, b, CF ? 1 : 0);
    public byte Adc8(byte a, byte b) => Add8(a, b, CF ? 1 : 0);

    // ---- SUB / SBB / CMP ----
    public ushort Sub16(ushort a, ushort b, int borrowIn = 0)
    {
        int r = a - b - borrowIn; int res = r & 0xFFFF;
        CF = r < 0;
        AF = ((a & 0xF) - (b & 0xF) - borrowIn) < 0;
        OF = (((a ^ b) & (a ^ res)) & 0x8000) != 0;
        SetSZP16(res); return (ushort)res;
    }
    public byte Sub8(byte a, byte b, int borrowIn = 0)
    {
        int r = a - b - borrowIn; int res = r & 0xFF;
        CF = r < 0;
        AF = ((a & 0xF) - (b & 0xF) - borrowIn) < 0;
        OF = (((a ^ b) & (a ^ res)) & 0x80) != 0;
        SetSZP8(res); return (byte)res;
    }
    public ushort Sbb16(ushort a, ushort b) => Sub16(a, b, CF ? 1 : 0);
    public byte Sbb8(byte a, byte b) => Sub8(a, b, CF ? 1 : 0);
    public void Cmp16(ushort a, ushort b) => Sub16(a, b);   // flags only
    public void Cmp8(byte a, byte b) => Sub8(a, b);

    // ---- INC / DEC (no tocan CF) ----
    public ushort Inc16(ushort a)
    {
        bool cf = CF; ushort res = Add16(a, 1); CF = cf; OF = a == 0x7FFF; return res;
    }
    public byte Inc8(byte a)
    {
        bool cf = CF; byte res = Add8(a, 1); CF = cf; OF = a == 0x7F; return res;
    }
    public ushort Dec16(ushort a)
    {
        bool cf = CF; ushort res = Sub16(a, 1); CF = cf; OF = a == 0x8000; return res;
    }
    public byte Dec8(byte a)
    {
        bool cf = CF; byte res = Sub8(a, 1); CF = cf; OF = a == 0x80; return res;
    }

    // ---- NEG ----
    public ushort Neg16(ushort a) { ushort res = Sub16(0, a); return res; }
    public byte Neg8(byte a) { byte res = Sub8(0, a); return res; }

    // ---- lógicas: AND/OR/XOR/TEST (CF=OF=0, AF indefinido→0) ----
    public ushort And16(ushort a, ushort b) { int res = a & b; CF = OF = false; AF = false; SetSZP16(res); return (ushort)res; }
    public byte And8(byte a, byte b) { int res = a & b; CF = OF = false; AF = false; SetSZP8(res); return (byte)res; }
    public ushort Or16(ushort a, ushort b) { int res = a | b; CF = OF = false; AF = false; SetSZP16(res); return (ushort)res; }
    public byte Or8(byte a, byte b) { int res = a | b; CF = OF = false; AF = false; SetSZP8(res); return (byte)res; }
    public ushort Xor16(ushort a, ushort b) { int res = a ^ b; CF = OF = false; AF = false; SetSZP16(res); return (ushort)res; }
    public byte Xor8(byte a, byte b) { int res = a ^ b; CF = OF = false; AF = false; SetSZP8(res); return (byte)res; }
    public void Test16(ushort a, ushort b) => And16(a, b);
    public void Test8(byte a, byte b) => And8(a, b);

    // NOT no toca flags
    public static ushort Not16(ushort a) => (ushort)~a;
    public static byte Not8(byte a) => (byte)~a;

    // ---- desplazamientos (cuenta enmascarada a 0x1F como 286+/QEMU; counts reales son pequeños) ----
    public ushort Shl16(ushort v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int res = (v << cnt) & 0xFFFF;
        CF = cnt <= 16 && ((v >> (16 - cnt)) & 1) != 0;
        if (cnt == 1) OF = ((res & 0x8000) != 0) ^ CF;
        SetSZP16(res); return (ushort)res;
    }
    public byte Shl8(byte v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int res = (v << cnt) & 0xFF;
        CF = cnt <= 8 && ((v >> (8 - cnt)) & 1) != 0;
        if (cnt == 1) OF = ((res & 0x80) != 0) ^ CF;
        SetSZP8(res); return (byte)res;
    }
    public ushort Shr16(ushort v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int res = v >> cnt;
        CF = cnt <= 16 && ((v >> (cnt - 1)) & 1) != 0;
        if (cnt == 1) OF = (v & 0x8000) != 0;
        SetSZP16(res); return (ushort)res;
    }
    public byte Shr8(byte v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int res = v >> cnt;
        CF = cnt <= 8 && ((v >> (cnt - 1)) & 1) != 0;
        if (cnt == 1) OF = (v & 0x80) != 0;
        SetSZP8(res); return (byte)res;
    }
    public ushort Sar16(ushort v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int s = (short)v;
        int res = s >> Math.Min(cnt, 15);
        CF = ((s >> Math.Min(cnt - 1, 15)) & 1) != 0;
        if (cnt == 1) OF = false;
        SetSZP16(res); return (ushort)(res & 0xFFFF);
    }
    public byte Sar8(byte v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int s = (sbyte)v;
        int res = s >> Math.Min(cnt, 7);
        CF = ((s >> Math.Min(cnt - 1, 7)) & 1) != 0;
        if (cnt == 1) OF = false;
        SetSZP8(res); return (byte)(res & 0xFF);
    }

    // ---- LAHF/SAHF y aritmética BCD/ASCII (semántica SDM; probadas vs Unicorn) ----
    public void Sahf()
    {
        byte ah = AH;
        CF = (ah & 0x01) != 0; PF = (ah & 0x04) != 0; AF = (ah & 0x10) != 0;
        ZF = (ah & 0x40) != 0; SF = (ah & 0x80) != 0;
    }

    public void Daa()
    {
        byte oldAL = AL; bool oldCF = CF; CF = false;
        if ((AL & 0x0F) > 9 || AF) { int r = AL + 6; CF = oldCF || r > 0xFF; AL = (byte)r; AF = true; }
        else AF = false;
        if (oldAL > 0x99 || oldCF) { AL = (byte)(AL + 0x60); CF = true; }
        SetSZP8(AL);
    }
    public void Das()
    {
        byte oldAL = AL; bool oldCF = CF; CF = false;
        if ((AL & 0x0F) > 9 || AF) { int r = AL - 6; CF = oldCF || r < 0; AL = (byte)r; AF = true; }
        else AF = false;
        if (oldAL > 0x99 || oldCF) { AL = (byte)(AL - 0x60); CF = true; }
        SetSZP8(AL);
    }
    public void Aaa()
    {
        if ((AL & 0x0F) > 9 || AF) { AX = (ushort)(AX + 0x106); AF = true; CF = true; }
        else { AF = false; CF = false; }
        AL = (byte)(AL & 0x0F);
    }
    public void Aas()
    {
        if ((AL & 0x0F) > 9 || AF) { AX = (ushort)(AX - 6); AH = (byte)(AH - 1); AF = true; CF = true; }
        else { AF = false; CF = false; }
        AL = (byte)(AL & 0x0F);
    }
    public void Aam(byte imm)
    {
        if (imm == 0) throw new DivideByZeroException("aam por cero (#DE)");
        AH = (byte)(AL / imm); AL = (byte)(AL % imm); SetSZP8(AL);
    }
    public void Aad(byte imm)
    {
        AL = (byte)(AL + AH * imm); AH = 0; SetSZP8(AL);
    }

    // ---- rotaciones (solo CF/OF; SF/ZF/PF/AF sin cambio). OF solo definido si cuenta==1. ----
    public ushort Rol16(ushort v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int x = v;
        for (int i = 0; i < cnt; i++) { int msb = (x >> 15) & 1; x = ((x << 1) | msb) & 0xFFFF; }
        CF = (x & 1) != 0;
        if (cnt == 1) OF = (((x >> 15) & 1) != 0) ^ CF;
        return (ushort)x;
    }
    public byte Rol8(byte v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int x = v;
        for (int i = 0; i < cnt; i++) { int msb = (x >> 7) & 1; x = ((x << 1) | msb) & 0xFF; }
        CF = (x & 1) != 0;
        if (cnt == 1) OF = (((x >> 7) & 1) != 0) ^ CF;
        return (byte)x;
    }
    public ushort Ror16(ushort v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int x = v;
        for (int i = 0; i < cnt; i++) { int lsb = x & 1; x = ((x >> 1) | (lsb << 15)) & 0xFFFF; }
        CF = ((x >> 15) & 1) != 0;
        if (cnt == 1) OF = (((x >> 15) & 1) ^ ((x >> 14) & 1)) != 0;
        return (ushort)x;
    }
    public byte Ror8(byte v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int x = v;
        for (int i = 0; i < cnt; i++) { int lsb = x & 1; x = ((x >> 1) | (lsb << 7)) & 0xFF; }
        CF = ((x >> 7) & 1) != 0;
        if (cnt == 1) OF = (((x >> 7) & 1) ^ ((x >> 6) & 1)) != 0;
        return (byte)x;
    }
    public ushort Rcl16(ushort v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int x = v, cf = CF ? 1 : 0;
        for (int i = 0; i < cnt; i++) { int n = (x >> 15) & 1; x = ((x << 1) | cf) & 0xFFFF; cf = n; }
        CF = cf != 0;
        if (cnt == 1) OF = (((x >> 15) & 1) != 0) ^ CF;
        return (ushort)x;
    }
    public byte Rcl8(byte v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int x = v, cf = CF ? 1 : 0;
        for (int i = 0; i < cnt; i++) { int n = (x >> 7) & 1; x = ((x << 1) | cf) & 0xFF; cf = n; }
        CF = cf != 0;
        if (cnt == 1) OF = (((x >> 7) & 1) != 0) ^ CF;
        return (byte)x;
    }
    public ushort Rcr16(ushort v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int cf = CF ? 1 : 0;
        if (cnt == 1) OF = (((v >> 15) & 1) != 0) ^ (cf != 0);   // OF antes de rotar
        int x = v;
        for (int i = 0; i < cnt; i++) { int n = x & 1; x = ((x >> 1) | (cf << 15)) & 0xFFFF; cf = n; }
        CF = cf != 0;
        return (ushort)x;
    }
    public byte Rcr8(byte v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int cf = CF ? 1 : 0;
        if (cnt == 1) OF = (((v >> 7) & 1) != 0) ^ (cf != 0);
        int x = v;
        for (int i = 0; i < cnt; i++) { int n = x & 1; x = ((x >> 1) | (cf << 7)) & 0xFF; cf = n; }
        CF = cf != 0;
        return (byte)x;
    }

    // ---- MUL / IMUL (CF=OF definidos; SF/ZF/PF/AF indefinidos) ----
    public void Mul8(byte src) { int p = AL * src; AX = (ushort)p; CF = OF = AH != 0; }
    public void Mul16(ushort src) { uint p = (uint)AX * src; AX = (ushort)p; DX = (ushort)(p >> 16); CF = OF = DX != 0; }
    public void Imul8(byte src) { int p = (sbyte)AL * (sbyte)src; AX = (ushort)p; CF = OF = (short)AX != (sbyte)AL; }
    public void Imul16(ushort src)
    {
        int p = (short)AX * (short)src;
        AX = (ushort)p; DX = (ushort)(p >> 16);
        CF = OF = p != (short)AX;   // resultado no cabe en 16 bits con signo
    }
    /// <summary>IMUL de 2/3 operandos (186+): destino = a*b con signo, truncado a 16 bits.</summary>
    public ushort Imul16Two(ushort a, ushort b)
    {
        int p = (short)a * (short)b;
        ushort r = (ushort)p;
        CF = OF = p != (short)r;
        return r;
    }

    // ---- DIV / IDIV (flags indefinidos; #DE modelado como excepción, igual que el trap INT 0) ----
    public void Div8(byte src)
    {
        if (src == 0) throw new DivideByZeroException("div8 por cero (#DE)");
        uint d = AX; uint q = d / src;
        if (q > 0xFF) throw new OverflowException("div8 overflow (#DE)");
        AL = (byte)q; AH = (byte)(d % src);
    }
    public void Div16(ushort src)
    {
        if (src == 0) throw new DivideByZeroException("div16 por cero (#DE)");
        uint d = ((uint)DX << 16) | AX; uint q = d / src;
        if (q > 0xFFFF) throw new OverflowException("div16 overflow (#DE)");
        AX = (ushort)q; DX = (ushort)(d % src);
    }
    public void Idiv8(byte src)
    {
        if (src == 0) throw new DivideByZeroException("idiv8 por cero (#DE)");
        short d = (short)AX; sbyte v = (sbyte)src;
        int q = d / v;
        if (q > 127 || q < -128) throw new OverflowException("idiv8 overflow (#DE)");
        AL = (byte)q; AH = (byte)(d % v);
    }
    public void Idiv16(ushort src)
    {
        if (src == 0)
        {
            if (Environment.GetEnvironmentVariable("PAKU_EXIT_STACK") == "1")
                Console.Error.WriteLine($"  [idiv16=0] DS={DS:X4} DX:AX={DX:X4}:{AX:X4} BP={BP:X4} SP={SP:X4}");
            throw new DivideByZeroException("idiv16 por cero (#DE)");
        }
        int d = (int)(((uint)DX << 16) | AX); short v = (short)src;
        int q = d / v;
        if (q > 32767 || q < -32768) throw new OverflowException("idiv16 overflow (#DE)");
        AX = (ushort)q; DX = (ushort)(d % v);
    }

    // ================= ALU de 32 bits =================
    //
    // Espejo EXACTO de las familias de 8/16 bits de arriba, con el ancho cambiado. No es un
    // camino nuevo: es el mismo modelo de flags (AF por nibble, OF por signo) que el corpus de
    // Unicorn ya valida en 16 bits, ahora en 32. La verdad terreno de esta tanda la dicta
    // tools/gen_opcode_vectors32.py (Unicorn) y la mide tests/Interp32Tests.cs.
    //
    // Cuidado de C#: `v << n` con v de 32 bits enmascara n a 5 bits SOLO, así que un
    // desplazamiento de 32 equivale a 0. Por eso todos los helpers salen antes con cnt==0 y
    // nunca calculan `32 - cnt` con cnt cero.

    private void SetSZP32(uint res) { ZF = res == 0; SF = (res & 0x80000000u) != 0; PF = ParityEven((int)res); }

    // ---- ADD / ADC ----
    public uint Add32(uint a, uint b, int carryIn = 0)
    {
        ulong r = (ulong)a + b + (uint)carryIn; uint res = (uint)r;
        CF = r > 0xFFFFFFFFul;
        AF = ((a & 0xF) + (b & 0xF) + (uint)carryIn) > 0xF;
        OF = (((a ^ res) & (b ^ res)) & 0x80000000u) != 0;
        SetSZP32(res); return res;
    }
    public uint Adc32(uint a, uint b) => Add32(a, b, CF ? 1 : 0);

    // ---- SUB / SBB / CMP ----
    public uint Sub32(uint a, uint b, int borrowIn = 0)
    {
        long r = (long)a - b - borrowIn; uint res = (uint)r;
        CF = r < 0;
        AF = ((int)(a & 0xF) - (int)(b & 0xF) - borrowIn) < 0;
        OF = (((a ^ b) & (a ^ res)) & 0x80000000u) != 0;
        SetSZP32(res); return res;
    }
    public uint Sbb32(uint a, uint b) => Sub32(a, b, CF ? 1 : 0);
    /// <summary>CMP de 32 bits (también lo usan CMPSD/SCASD).</summary>
    public void Cmp32(uint a, uint b) => Sub32(a, b);   // flags only

    // ---- INC / DEC (no tocan CF) ----
    public uint Inc32(uint a)
    {
        bool cf = CF; uint res = Add32(a, 1); CF = cf; OF = a == 0x7FFFFFFFu; return res;
    }
    public uint Dec32(uint a)
    {
        bool cf = CF; uint res = Sub32(a, 1); CF = cf; OF = a == 0x80000000u; return res;
    }

    public uint Neg32(uint a) => Sub32(0, a);

    // ---- lógicas (CF=OF=0, AF indefinido→0) ----
    public uint And32(uint a, uint b) { uint res = a & b; CF = OF = false; AF = false; SetSZP32(res); return res; }
    public uint Or32(uint a, uint b) { uint res = a | b; CF = OF = false; AF = false; SetSZP32(res); return res; }
    public uint Xor32(uint a, uint b) { uint res = a ^ b; CF = OF = false; AF = false; SetSZP32(res); return res; }
    public void Test32(uint a, uint b) => And32(a, b);
    public static uint Not32(uint a) => ~a;

    // ---- desplazamientos ----
    public uint Shl32(uint v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        uint res = v << cnt;
        CF = ((v >> (32 - cnt)) & 1) != 0;
        if (cnt == 1) OF = ((res & 0x80000000u) != 0) ^ CF;
        SetSZP32(res); return res;
    }
    public uint Shr32(uint v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        uint res = v >> cnt;
        CF = ((v >> (cnt - 1)) & 1) != 0;
        if (cnt == 1) OF = (v & 0x80000000u) != 0;
        SetSZP32(res); return res;
    }
    public uint Sar32(uint v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        int s = (int)v;
        int res = s >> cnt;
        CF = ((s >> (cnt - 1)) & 1) != 0;
        if (cnt == 1) OF = false;
        SetSZP32((uint)res); return (uint)res;
    }

    // ---- rotaciones (solo CF/OF; SF/ZF/PF/AF sin cambio) ----
    public uint Rol32(uint v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        uint x = (v << cnt) | (v >> (32 - cnt));
        CF = (x & 1) != 0;
        if (cnt == 1) OF = (((x >> 31) & 1) != 0) ^ CF;
        return x;
    }
    public uint Ror32(uint v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        uint x = (v >> cnt) | (v << (32 - cnt));
        CF = (x & 0x80000000u) != 0;
        if (cnt == 1) OF = (((x >> 31) & 1) ^ ((x >> 30) & 1)) != 0;
        return x;
    }
    /// <summary>RCL de 32 bits: rotación de 33 bits (el operando más CF), modelo RCLD de
    /// dosbox-x instructions.h. El bucle es el mismo idioma que Rcl16.</summary>
    public uint Rcl32(uint v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        uint x = v; uint cf = CF ? 1u : 0u;
        for (int i = 0; i < cnt; i++) { uint n = (x >> 31) & 1; x = (x << 1) | cf; cf = n; }
        CF = cf != 0;
        if (cnt == 1) OF = (((x >> 31) & 1) != 0) ^ CF;
        return x;
    }
    public uint Rcr32(uint v, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return v;
        uint cf = CF ? 1u : 0u;
        if (cnt == 1) OF = (((v >> 31) & 1) != 0) ^ (cf != 0);   // OF antes de rotar
        uint x = v;
        for (int i = 0; i < cnt; i++) { uint n = x & 1; x = (x >> 1) | (cf << 31); cf = n; }
        CF = cf != 0;
        return x;
    }

    // ---- SHLD / SHRD de 32 bits ----
    // OF sigue la regla de dosbox-x (t_DSHLd/t_DSHRd en flags.cpp): "cambió el bit de signo".
    public uint Shld32(uint dst, uint src, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return dst;
        uint res = (dst << cnt) | (src >> (32 - cnt));
        CF = ((dst >> (32 - cnt)) & 1) != 0;
        if (cnt == 1) OF = ((res ^ dst) & 0x80000000u) != 0;
        SetSZP32(res); return res;
    }
    public uint Shrd32(uint dst, uint src, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return dst;
        uint res = (dst >> cnt) | (src << (32 - cnt));
        CF = ((dst >> (cnt - 1)) & 1) != 0;
        if (cnt == 1) OF = ((res ^ dst) & 0x80000000u) != 0;
        SetSZP32(res); return res;
    }

    // ---- MUL / IMUL (CF=OF definidos; SF/ZF/PF/AF indefinidos) ----
    public void Mul32(uint src) { ulong p = (ulong)EAX * src; EAX = (uint)p; EDX = (uint)(p >> 32); CF = OF = EDX != 0; }
    public void Imul32(uint src)
    {
        long p = (long)(int)EAX * (int)src;
        EAX = (uint)p; EDX = (uint)(p >> 32);
        CF = OF = p != (int)EAX;   // no cabe en 32 bits con signo
    }
    /// <summary>IMUL de 2/3 operandos: destino = a*b con signo, truncado a 32 bits.</summary>
    public uint Imul32Two(uint a, uint b)
    {
        long p = (long)(int)a * (int)b;
        uint r = (uint)p;
        CF = OF = p != (int)r;
        return r;
    }

    // ---- DIV / IDIV (flags indefinidos; #DE como excepción, igual que en 8/16) ----
    public void Div32(uint src)
    {
        if (src == 0) throw new DivideByZeroException("div32 por cero (#DE)");
        ulong d = ((ulong)EDX << 32) | EAX; ulong q = d / src;
        if (q > 0xFFFFFFFFul) throw new OverflowException("div32 overflow (#DE)");
        EAX = (uint)q; EDX = (uint)(d % src);
    }
    public void Idiv32(uint src)
    {
        if (src == 0) throw new DivideByZeroException("idiv32 por cero (#DE)");
        long d = (long)(((ulong)EDX << 32) | EAX); int v = (int)src;
        // long.MinValue / -1 desborda el propio long en .NET; es también el #DE del hardware.
        if (d == long.MinValue && v == -1) throw new OverflowException("idiv32 overflow (#DE)");
        long q = d / v;
        if (q > int.MaxValue || q < int.MinValue) throw new OverflowException("idiv32 overflow (#DE)");
        EAX = (uint)q; EDX = (uint)(d % v);
    }

    // ---- BSF / BSR de 32 bits ----
    public uint Bsf32(uint v) { for (int i = 0; i < 32; i++) if (((v >> i) & 1) != 0) return (uint)i; return 0; }
    public uint Bsr32(uint v) { for (int i = 31; i >= 0; i--) if (((v >> i) & 1) != 0) return (uint)i; return 0; }

    // ---- SHLD / SHRD (386 double-precision shifts) ----
    public ushort Shld16(ushort dst, ushort src, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return dst;
        uint combined = ((uint)dst << 16) | src;
        combined <<= cnt;
        ushort res = (ushort)(combined >> 16);
        CF = cnt <= 16 && ((dst >> (16 - cnt)) & 1) != 0;
        if (cnt == 1) OF = ((res & 0x8000) != 0) ^ CF;
        SetSZP16(res); return res;
    }
    public byte Shld8(byte dst, byte src, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return dst;
        uint combined = ((uint)dst << 8) | src;
        combined <<= cnt;
        byte res = (byte)(combined >> 8);
        CF = cnt <= 8 && ((dst >> (8 - cnt)) & 1) != 0;
        SetSZP8(res); return res;
    }
    public ushort Shrd16(ushort dst, ushort src, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return dst;
        uint combined = ((uint)src << 16) | dst;
        combined >>= cnt;
        ushort res = (ushort)combined;
        CF = ((dst >> (cnt - 1)) & 1) != 0;
        if (cnt == 1) OF = ((dst & 0x8000) != 0);
        SetSZP16(res); return res;
    }
    public byte Shrd8(byte dst, byte src, int count)
    {
        int cnt = count & 0x1F; if (cnt == 0) return dst;
        uint combined = ((uint)src << 8) | dst;
        combined >>= cnt;
        byte res = (byte)combined;
        CF = ((dst >> (cnt - 1)) & 1) != 0;
        SetSZP8(res); return res;
    }

    // ---- BSF / BSR (386 bit scan) ----
    public ushort Bsf16(ushort v) { for (int i = 0; i < 16; i++) if (((v >> i) & 1) != 0) return (ushort)i; return 0; }
    public byte Bsf8(byte v) { for (int i = 0; i < 8; i++) if (((v >> i) & 1) != 0) return (byte)i; return 0; }
    public ushort Bsr16(ushort v) { for (int i = 15; i >= 0; i--) if (((v >> i) & 1) != 0) return (ushort)i; return 0; }
    public byte Bsr8(byte v) { for (int i = 7; i >= 0; i--) if (((v >> i) & 1) != 0) return (byte)i; return 0; }
}

/// <summary>The guest ran out of host stack: a chain of guest calls that never return. Raised by
/// <see cref="Cpu.GuardStack"/> so the condition arrives as a normal exception instead of killing
/// the process with an uncatchable StackOverflowException.</summary>
public sealed class GuestStackDepthException(ushort cs, ushort sp)
    : System.Exception($"pila del host agotada en CS={cs:X4} SP={sp:X4}: cadena de llamadas del " +
                       "invitado que no retorna (call/far call sin retf). No es un opcode que falte.")
{
    public ushort Cs { get; } = cs;
    public ushort Sp { get; } = sp;
}
