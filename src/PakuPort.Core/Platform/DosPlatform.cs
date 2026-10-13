namespace PakuPort.Core.Platform;

/// <summary>
/// Plataforma DOS generica: despacha INTs a sub-handlers y puertos de E/S al modelo de
/// hardware. Los sub-handlers se registran por numero de INT; si no hay handler registrado,
/// la interrupcion es un no-op (equivalente al stub actual del juez byte-exacto).
/// </summary>
public class DosPlatform : IPlatform
{
    private readonly Dictionary<byte, Action<Cpu, Memory>> _intHandlers = new();
    private readonly Dictionary<ushort, Func<ushort, byte>> _portInHandlers = new();
    private readonly Dictionary<ushort, Action<ushort, byte>> _portOutHandlers = new();

    public VideoState Video { get; } = new();

    // ---- infraestructura de maquina ----

    /// <summary>Tabla de vectores de interrupcion (IVT). Vive en Memory.Raw[0..0x400].</summary>
    public Ivt Ivt { get; } = new();

    /// <summary>BIOS Data Area. Se inicializa con AttachMemory o en la primera llamada con memoria.</summary>
    public BiosDataArea Bda { get; private set; } = null!;

    /// <summary>8259 PIC (maestro + esclavo).</summary>
    public Pic Pic { get; } = new();

    /// <summary>8253/8254 PIT (3 canales, canal 0 = timer del sistema).</summary>
    public Pit Pit { get; } = new();

    /// <summary>CMOS/RTC (puertos 0x70/0x71).</summary>
    public Cmos Cmos { get; } = new();

    /// <summary>Controlador de teclado 8042 (puertos 0x60/0x64).</summary>
    public KeyboardController Keyboard { get; } = new();

    /// <summary>PC Speaker / gate canal 2 PIT (puerto 0x61).</summary>
    public Speaker Speaker { get; } = new();

    /// <summary>Adlib/OPL2 (puertos 0x388/0x389).</summary>
    public Opl Opl { get; } = new();

    /// <summary>El MPU-401 (música MIDI). Existe siempre, como el OPL: es hardware de la placa,
    /// no algo que dependa del `game.json`. Un juego que no lo use no paga nada por él.</summary>
    public Mpu401 Mpu { get; } = new();

    /// <summary>Game port analógico (puerto 0x201). El host lo alimenta con teclado/gamepad.</summary>
    public GamePort GamePort { get; } = new();

    /// <summary>Temporizador de maquina. El host llama Advance() una vez por frame.</summary>
    public MachineTimer MachineTimer { get; } = new();

    /// <summary>8237 DMA (controlador 0, canales 0-3). Solo se instancia si el juego declara
    /// SoundBlaster (game.json bloque `sb`); null = puertos DMA en el stub de reposo.</summary>
    public Dma8237? Dma { get; private set; }

    /// <summary>SoundBlaster (DSP + DMA single-cycle 8-bit). Solo se instancia si el juego
    /// declara `sb` en game.json; null = puertos SB en el stub de reposo (null-op total).</summary>
    public SoundBlaster? Sb { get; private set; }

    /// <summary>Instala el SoundBlaster + 8237: sustituye los stubs de reposo de los puertos
    /// SB (0x220-0x22F) y DMA (0x00-0x0F, páginas 0x81-0x83/0x87) por el modelo real. El
    /// backend de audio (<paramref name="audio"/>) puede ser null (detección sin sonido, arnés).
    /// Requiere AttachMemory previo.</summary>
    public void InstallSoundBlaster(ushort basePort, int irq, int dmaChannel, HostAudio? audio)
    {
        Dma = new Dma8237();
        Sb = new SoundBlaster(basePort, irq, dmaChannel, Dma, audio);
        if (_mem != null) Sb.AttachMemory(_mem);
    }

    /// <summary>Handler de teclado BIOS (INT 16h). Lo asigna Int16hHandler.Register.
    /// La consola DOS (INT 21h AH=01/07/08/0B) lee de aqui para ver las MISMAS teclas
    /// que la ventana del host inyecta, en vez de stdin.</summary>
    public Int16hHandler? Int16 { get; set; }
    /// <summary>El driver de raton, para quien tenga que meterle entrada desde dentro del
    /// nucleo (la reproduccion de una cinta: <see cref="InputTape.Reproduce"/>).</summary>
    public Int33hHandler? Int33 { get; set; }

    /// <summary>Handler DOS (INT 21h). Lo asigna Int21hHandler.Register. El host lo usa
    /// para restaurar la tabla de handles del kernel al reanudar un snapshot (OpenAtHandle).</summary>
    public Int21hHandler? Int21 { get; set; }

    /// <summary>
    /// El host lo asigna para llamar a Gen.FarDispatch(c, m, seg, off).
    /// Cuando es null, FireInterrupt cae al handler C# registrado.
    /// Necesario para evitar dependencia circular entre PakuPort.Core y PakuPort.Generated.
    /// </summary>
    public Action<Cpu, Memory, ushort, ushort>? IsrDispatch;

    /// <summary>
    /// Servicio de callbacks de usuario instalados por el juego vía un driver (hoy: el
    /// callback de eventos del ratón, INT 33h AX=000C — lo asigna Int33hHandler.Register).
    /// Se invoca en ServicePendingInterrupts, en el hilo del juego, tras servir las IRQs:
    /// el mismo punto seguro (límite de frame) para reentrar a FarDispatch.
    /// </summary>
    public Action<Cpu, Memory>? UserCallbackService;

    /// <summary>
    /// Drena la entrada del host (ratón) encolada por el hilo de render y la aplica en el HILO
    /// DEL JUEGO. Lo asigna Int33hHandler.Register (= DrainHostEvents). Lo invoca MachineTimer.Poll
    /// ANTES de entregar ticks/callbacks: mismo punto seguro (flujo de instrucciones) que las IRQs
    /// y el callback de usuario, de modo que los polls AH=03/05/06 del juego leen estado coherente.
    /// </summary>
    public Action? HostInputDrain;

    // ---- inicializacion ----

    /// <summary>
    /// Asocia la memoria a la BDA y otros componentes que necesitan acceso directo a Raw.
    /// Llamar UNA vez antes de usar FireInterrupt o MachineTimer.
    /// </summary>
    private Memory? _mem;

    /// <summary>Host DPMI del mundo de 16 bits, para los juegos con EXTENSOR EMBEBIDO. Es la MISMA
    /// clase que sirve al mundo plano de 32 bits: un cliente DPMI pide lo mismo venga de donde
    /// venga, y tener dos implementaciones de la misma especificación sería pedir que divergieran.
    /// Nace sólo si `game.json` declara `dpmiHost`.</summary>
    public FlatServices? Dpmi;

    public void AttachMemory(Memory m)
    {
        _mem = m;
        if (Bda == null!)
        {
            Bda = new BiosDataArea(m);
            Bda.Init();
            InitIvtStubs(m);
        }
        Sb?.AttachMemory(m);
        if (BiosInterrupts.DpmiHostDisponible && Dpmi == null)
        {
            // Tope de imagen = 1 MiB: en este mundo el programa vive en memoria convencional, así
            // que todo lo que reparta DPMI va por encima (BaseDelMontonDpmi).
            Dpmi = new FlatServices(m, 0x100000) { Plataforma = this };
            RegisterInt(0x31, (c, mm) => Dpmi!.Int31Publico(c, mm));
            InitIvtStubs(m);   // el vector 31h tiene que existir ANTES de que el cliente lo llame
        }
    }

    private void InitIvtStubs(Memory m)
    {
        // Each registered INT gets an IVT vector at F000:intNo.
        // Cpu.Iret detects CS==F000 and dispatches via Platform.Int(ip, ...).
        foreach (byte intNo in _intHandlers.Keys)
        {
            // Stub de 2 bytes: NOP + IRET. El NOP existe para que la comprobación canónica de
            // "¿hay driver?" (vector no nulo Y primer byte != 0CFh) encuentre uno — ver
            // Cpu.IvtStubBase. Ejecutarlo hace el trabajo del handler C# (Interp/Cpu.Iret
            // despachan al entrar) y vuelve por el IRET de siempre.
            int stub = Memory.Lin(Cpu.IvtStubSeg, Cpu.IvtStubOff(intNo));
            if (stub + 1 < Memory.Size)
            {
                m.Raw[stub]     = 0x90; // NOP
                m.Raw[stub + 1] = 0xCF; // IRET
            }
            // Área histórica de 1 byte: ya no la apunta ningún vector, pero se mantiene porque
            // hay ISR de juego que saltan a F000:00nn con la dirección escrita a mano.
            int viejo = Memory.Lin(Cpu.IvtStubSeg, intNo);
            if (viejo < Memory.Size)
                m.Raw[viejo] = 0xCF;
            Ivt.SetVector(m, intNo, Cpu.IvtStubSeg, Cpu.IvtStubOff(intNo));
        }

        // Punto de entrada del cambio a modo protegido que publica el host DPMI (INT 2Fh
        // AX=1687h → ES:DI). No cuelga de ningún vector: el cliente lo llama con un `call far`
        // usando la dirección que le dimos. `NOP; RETF` — el NOP es donde el intérprete engancha
        // el traspaso al mundo de 32 bits, y el RETF es la salida limpia si nadie lo atiende
        // (que es lo que pasa mientras `dpmiHost` esté apagado, o sea en todos los juegos probados).
        // Cuatro stubs de 2 bytes seguidos, todos `NOP; RETF`:
        //   1400 cambio a modo protegido   1402 guardar/restaurar estado (modo real)
        //   1404 guardar/restaurar (protegido)  1406/1408 cambio de modo DIRECTO (raw)
        // Los de estado y los raw son no-ops de verdad y no una simplificación: en PakuPort los
        // dos "modos" comparten el MISMO estado de CPU —no hay dos juegos de registros que salvar
        // ni una MMU que reprogramar—, así que guardar y restaurar es literalmente no hacer nada.
        for (ushort off = Cpu.DpmiSwitchOff; off <= Cpu.DpmiRawSwitchPmOff; off += 2)
        {
            int sw = Memory.Lin(Cpu.IvtStubSeg, off);
            if (sw + 1 >= Memory.Size) break;
            m.Raw[sw]     = 0x90; // NOP
            m.Raw[sw + 1] = 0xCB; // RETF
        }

        // La cadena de retorno del callback del ratón por IRQ12 (ver Cpu.CbRatonUirOff): el
        // trampolín de un extensor vuelve por su PROPIO `iret`, así que el sitio al que vuelve
        // tiene que ser código de verdad en la memoria del invitado, no un gancho en C#.
        int uir = Memory.Lin(Cpu.IvtStubSeg, Cpu.CbRatonUirOff);
        if (uir + 6 < Memory.Size)
        {
            // El `cli` no es adorno: en el original el stub uir es RETF_CLI y el epílogo empieza
            // por `cli`, o sea que TODO el desmontaje (uir -> popad -> iret) corre con IF=0. Sin
            // él queda una ventana con las interrupciones abiertas, el guardia de reentrada ya
            // bajado y el marco del extensor todavía sin consumir en la pila: reentrada legal.
            m.Raw[uir]     = 0xFA;               // CLI
            m.Raw[uir + 1] = 0xCB;               // RETF          — cede el control al epílogo
            m.Raw[uir + 2] = 0x66; m.Raw[uir + 3] = 0x61;   // POPAD
            m.Raw[uir + 4] = 0x07;               // POP ES
            m.Raw[uir + 5] = 0x1F;               // POP DS
            m.Raw[uir + 6] = 0xCF;               // IRET          — y vuelve al código interrumpido
        }
    }

    // ---- registro de handlers ----

    public void RegisterInt(byte intNo, Action<Cpu, Memory> handler) => _intHandlers[intNo] = handler;

    /// <summary>True si hay handler C# integrado para intNo (INT 10/16/21/33...).</summary>
    public bool HasIntHandler(byte intNo) => _intHandlers.ContainsKey(intNo);

    /// <inheritdoc/>
    public bool AtiendeInt(byte intNo) => HasIntHandler(intNo);

    /// <summary>Vector de la IVT SI apunta a código instalado por el juego (no al stub F000
    /// ni al trampolín 9F00 del explorador); (0,0) en caso contrario. Para que el interp
    /// vectorice fielmente los INT software a handlers del juego (VMs de código enhebrado).</summary>
    public (ushort seg, ushort off) GetGameVector(Memory m, byte intNo)
    {
        if (Ivt == null) return (0, 0);
        var (seg, off) = Ivt.GetVector(m, intNo);
        bool gameIsr = (seg != 0 || off != 0) && seg != Cpu.IvtStubSeg && seg != 0x9F00;
        return gameIsr ? (seg, off) : ((ushort)0, (ushort)0);
    }

    /// <summary>Destino de una interrupción, honrando el modo. En modo real sale de la IVT de
    /// siempre; en modo protegido de 16 bits, de la IDT que el programa cargó con <c>lidt</c>,
    /// que es OTRA tabla y en OTRO formato (puertas de 8 bytes: offset, selector, tipo).
    ///
    /// Sin esto, un programa que ya corre en modo protegido recibe sus IRQs con un CS de modo
    /// real y se va a ejecutar donde no hay nada. Medido en un juego DOS/16M: corriendo en selectores
    /// (CS=0080, DS=SS=00A8), el timer lo mandaba a 332D:0101 — el vector de INT 8 leído de la
    /// IVT — donde sólo hay ceros.</summary>
    public (ushort seg, ushort off) VectorDeInterrupcion(Cpu c, Memory m, byte intNo)
        => VectorDeInterrupcion(c, m, intNo, out _);

    public (ushort seg, ushort off) VectorDeInterrupcion(Cpu c, Memory m, byte intNo,
                                                         out bool puerta386)
    {
        puerta386 = false;
        if ((c.Msw & 1) == 0 || c.IdtLimit < (uint)(intNo * 8 + 7))
            return Ivt != null ? Ivt.GetVector(m, intNo) : ((ushort)0, (ushort)0);
        uint p = c.IdtBase + (uint)(intNo * 8);
        // Puerta de 286 (tipo 0x06/0x07): offset de 16 bits en los dos primeros bytes. Una de
        // 386 (0x0E/0x0F) lleva los 16 bits altos en +6, que aquí no se usan porque el código
        // del extensor es de 16 bits. El byte 5 son los atributos: sin el bit de PRESENTE la
        // puerta no vale, y un selector nulo tampoco — con la entrada a medias se saltaba al
        // segmento 0 a ejecutar la tabla de vectores.
        // El TIPO también manda el MARCO: una puerta de 386 empuja EFLAGS/CS/EIP como DWORDS
        // y su handler termina en `iretd` — con un marco de words desapila 12 bytes de un
        // marco de 6 y la pila del interrumpido queda corrida (medido en un juego DOS/16M: el timer por
        // la puerta 0070:4315 dejaba un `pop ds` leyendo basura y el kernel terminaba el
        // programa sin querer).
        byte attr = m.RdLin8(p + 5);
        ushort sel = m.RdLin16(p + 2);
        if ((attr & 0x80) == 0 || sel == 0) return ((ushort)0, (ushort)0);
        puerta386 = (attr & 0x08) != 0;
        return (sel, m.RdLin16(p));
    }

    /// <summary>True when a generic software INT n should be vectored through the game's
    /// own installed IVT handler (no built-in C# handler AND the vector points at game
    /// code). Combines HasIntHandler + GetGameVector; the interp's `int n` case and the
    /// lifted `int n` share this guard for the "threaded VM by software INT" class
    /// (one game's INT 91h, a Turbo-Basic game's INT ECh) where the handler reads its return IP as an
    /// inline-operand pointer, so a synthetic nested-dispatch frame corrupts it.</summary>
    public bool HasGameSoftIntVector(Memory m, byte intNo)
    {
        if (HasIntHandler(intNo)) return false;
        var (seg, off) = GetGameVector(m, intNo);
        return seg != 0 || off != 0;
    }
    public void RegisterPortIn(ushort port, Func<ushort, byte> handler) => _portInHandlers[port] = handler;
    public void RegisterPortOut(ushort port, Action<ushort, byte> handler) => _portOutHandlers[port] = handler;

    // ---- IPlatform ----

    // Trazado de diagnóstico, off por defecto (PAKU_TRACE=1). Sin coste en runs normales.
    private static readonly bool _trace =
        Environment.GetEnvironmentVariable("PAKU_TRACE") == "1";
    private static readonly bool _logKbd =
        Environment.GetEnvironmentVariable("PAKU_LOG_KBD") == "1";
    private int _traceCount;

    public void Int(byte intNo, Cpu cpu, Memory mem)
    {
        if (_trace && _traceCount < 2000)
        {
            _traceCount++;
            bool known = _intHandlers.ContainsKey(intNo);
            Console.Error.WriteLine($"INT {intNo:X2} AH={cpu.AH:X2} AL={cpu.AL:X2} BX={cpu.BX:X4} CX={cpu.CX:X4} DX={cpu.DX:X4} DS={cpu.DS:X4} ES={cpu.ES:X4} SP={cpu.SP:X4}{(known ? "" : " [SIN HANDLER]")}");
        }
        if (_intHandlers.TryGetValue(intNo, out var h))
        {
            h(cpu, mem);
            return;
        }
        // No built-in C# handler: a software INT to a game-installed vector must be
        // dispatched via the IVT (faithful 8086 behaviour), else the ISR never runs.
        // Games install custom vectors (e.g. one test game's INT 91h/92h graphics
        // driver) with DOS AH=25; the interp/lifted `int n` reaches here unhandled and
        // would silently no-op, leaving return registers stale (AH=0 => the game's
        // "graphic mode not supported" path). Mirrors FireInterrupt's game-ISR branch.
        var (seg, off) = Ivt != null ? Ivt.GetVector(mem, intNo) : ((ushort)0, (ushort)0);
        bool gameIsr = (seg != 0 || off != 0) && seg != Cpu.IvtStubSeg && seg != 0x9F00;
        if (gameIsr && IsrDispatch != null)
        {
            ushort flags = cpu.FlagsWord();
            ushort spBefore = cpu.SP;
            ushort csBefore = cpu.CS;
            cpu.IF = false;
            cpu.SP -= 2; mem.WrU16(cpu.SS, cpu.SP, flags);
            cpu.SP -= 2; mem.WrU16(cpu.SS, cpu.SP, cpu.CS);
            cpu.SP -= 2; mem.WrU16(cpu.SS, cpu.SP, 0);
            // The caller may be running under Interp.WholeProgram (child EXEC'd programs),
            // where iret continues in-loop instead of returning. Dispatching the ISR as a
            // nested Run needs its terminating iret to RETURN from that nested Run so control
            // comes back here; toggle WholeProgram off for the duration of the nested ISR.
            bool savedWp = Interp.WholeProgram;
            Interp.WholeProgram = false;
            // See FireInterrupt: PSTK class at the ISR boundary — run to the terminal IRET,
            // tolerating a private-stack switch (SP > sp0). Only in WholeProgram context.
            bool savedIsr = Interp.IsrMode;
            Interp.IsrMode = savedWp;
            IsrDispatch(cpu, mem, seg, off);
            Interp.IsrMode = savedIsr;
            Interp.WholeProgram = savedWp;
            cpu.FarRet = false;
            cpu.CS = csBefore;
            // If the ISR ended in RETF/miss (4 bytes) instead of IRET (6), our FLAGS word
            // is still on the stack: pop it so SP balances and IF is re-enabled.
            if (cpu.SP == (ushort)(spBefore - 2))
            {
                cpu.SetFlagsWord(mem.RdU16(cpu.SS, cpu.SP));
                cpu.SP = spBefore;
            }
        }
    }

    private readonly HashSet<ushort> _seenPorts = new();

    public byte PortIn8(ushort port)
    {
        if (_trace && _seenPorts.Add(port)) // log la PRIMERA lectura de cada puerto distinto
            Console.Error.WriteLine($"PORT IN  {port:X4}");
        // SoundBlaster + 8237 DMA: solo si el juego los declara (game.json `sb`). Sin SB
        // instanciado, los puertos caen a los stubs de reposo de abajo (null-op para todo
        // juego no-SB → gates intactos por construcción).
        if (Sb != null)
        {
            // base+8/base+9 mirror the AdLib OPL2 chip on a real SB
            // (DOSBox-X adlib.cpp:1678-1679: OPL handlers installed at base+8).
            if (port == Sb.BasePort + 8) return Opl.In(0x388);
            if (port == Sb.BasePort + 9) return Opl.In(0x389);
            if (port is >= 0x220 and <= 0x22F) return Sb.PortIn(port);
            if (port <= 0x0F || port is >= 0x80 and <= 0x8F
                             || port is >= 0xC0 and <= 0xDF) return Dma!.PortIn(port);
        }
        // Hardware integrado: PIT, PIC, teclado, speaker, CMOS, OPL
        // (VGA lo maneja VgaPortHandler registrado via RegisterPortIn; no interferimos)
        switch (port)
        {
            case 0x40: case 0x41: case 0x42: return Pit.In(port);
            case 0x20: case 0x21: case 0xA0: case 0xA1: return Pic.In(port);
            case 0x60: case 0x64: return Keyboard.In(port);
            case 0x61: return Speaker.In(port);
            case 0x70: case 0x71: return Cmos.In(port);
            case 0x388: case 0x389: return Opl.In(port);
            case 0x201: return GamePort.Read();
            // MPU-401 en modo UART (0x330 datos, 0x331 estado). Antes eran dos constantes de
            // reposo y ningún juego podía usar música MIDI: ver Mpu401.cs.
            case 0x330: case 0x331: return Mpu.PortIn(port);
            // SoundBlaster DSP — valores de reposo verificados contra DOSBox-X (arnés io_sb_*):
            case 0x22A: return 0xAA; // DSP read data (tras reset)
            case 0x22C: return 0x7F; // DSP write-buffer status (bit7=0 = listo para escribir)
            case 0x22E: return 0x7F; // DSP read-buffer status (bit7=0, sin dato)
            // DMA1 8237 y LPT1 en reposo — valores verificados contra DOSBox-X (arnés io_*).
            // Estado de reposo (sin transferencias/impresión); el modelado completo del chip
            // con su estado es trabajo futuro.
            case 0x08: return 0x00; // DMA1 status (sin TC/request pendiente)
            case 0x0F: return 0x0F; // DMA1 mask-all (todos los canales enmascarados)
            case 0x379: return 0xDF; // LPT1 status (impresora presente, no lista)
        }
        if (port is >= 0x00 and <= 0x0F or >= 0x80 and <= 0x8F or >= 0xC0 and <= 0xDF)
            return 0xFF; // DMA controllers — no-op
        if (port is >= 0x220 and <= 0x22F)
            return 0xFF; // SB misc ports — no-op
        // Fallback: handlers registrados dinamicamente (VGA, CGA, custom)
        if (_portInHandlers.TryGetValue(port, out var h))
            return h(port);
        return 0xFF;
    }

    // Mirror of PortOut16: a 16-bit IN reads (port, low) + (port+1, high) on the ISA bus.
    public ushort PortIn16(ushort port) => (ushort)(PortIn8(port) | (PortIn8((ushort)(port + 1)) << 8));

    public void PortOut8(ushort port, byte val)
    {
        // SoundBlaster + 8237 DMA (ver PortIn8): enrutado solo si el juego declara `sb`.
        if (Sb != null)
        {
            // base+8/base+9 mirror the AdLib OPL2 chip on a real SB
            // (DOSBox-X adlib.cpp:1678-1679: OPL handlers installed at base+8).
            if (port == Sb.BasePort + 8) { Opl.Out(0x388, val); return; }
            if (port == Sb.BasePort + 9) { Opl.Out(0x389, val); return; }
            if (port is >= 0x220 and <= 0x22F) { Sb.PortOut(port, val); return; }
            if (port <= 0x0F || port is >= 0x80 and <= 0x8F
                             || port is >= 0xC0 and <= 0xDF) { Dma!.PortOut(port, val); return; }
        }
        switch (port)
        {
            case 0x330: case 0x331: Mpu.PortOut(port, val); return;
            case 0x40: case 0x41: case 0x42: case 0x43: Pit.Out(port, val); return;
            case 0x20: case 0x21: case 0xA0: case 0xA1: Pic.Out(port, val); return;
            case 0x60: case 0x64: Keyboard.Out(port, val); return;
            case 0x61: Speaker.Out(port, val); return;
            case 0x70: case 0x71: Cmos.Out(port, val); return;
            case 0x388: case 0x389: Opl.Out(port, val); return;
            case 0x201: GamePort.Trigger(); return; // one-shot de los ejes
        }
        if (port is >= 0x00 and <= 0x0F or >= 0x80 and <= 0x8F or >= 0xC0 and <= 0xDF)
            return; // DMA controllers — no-op
        if (port is >= 0x220 and <= 0x22F)
            return; // SoundBlaster — no-op
        // Fallback: handlers registrados
        if (_portOutHandlers.TryGetValue(port, out var h))
            h(port, val);
    }

    // ISA 16-bit I/O reaches 8-bit devices as two byte accesses: (port, low) + (port+1, high)
    // (DOSBox-X iohandler.cpp:93-94, the default word handler). The universal VGA idiom
    // `mov ax,0F02h; out dx,ax` (DX=3C4: index in AL, data in AH) depends on it — dropping
    // the high byte left map-mask/bitmask stale and striped every per-plane color blit.
    public void PortOut16(ushort port, ushort val)
    {
        PortOut8(port, (byte)val);
        PortOut8((ushort)(port + 1), (byte)(val >> 8));
    }

    // ---- disparo de interrupciones hardware ----

    /// <summary>
    /// Simula CPU_HW_Interrupt de DOSBox-X: empuja FLAGS/CS/IP en la pila, limpia IF y TF,
    /// lee el vector n de la IVT y despacha al handler instalado.
    /// Si el vector apunta a codigo del juego (seg != 0 || off != 0) y IsrDispatch != null,
    /// llama IsrDispatch(c, m, seg, off) para ejecutar la ISR transpilada.
    /// Si no, cae al handler C# registrado via RegisterInt.
    /// NOTA: requiere que Bda este inicializada (llama AttachMemory primero).
    /// </summary>
    public void FireInterrupt(byte n, Cpu c, Memory m)
    {
        ushort flags = c.FlagsWord();   // capture with IF still set, like real INT
        c.IF = false;

        // En modo protegido de 16 bits el destino sale de la IDT del programa, no de la IVT.
        var (seg, off) = VectorDeInterrupcion(c, m, n, out bool puerta386);

        bool gameIsr = (seg != 0 || off != 0) && seg != Cpu.IvtStubSeg && seg != 0x9F00;
        if (_logKbd && n == 9)
            Console.Error.WriteLine($"[KBD] INT9 -> {seg:X4}:{off:X4} ({(gameIsr ? "ISR del juego" : "stub BIOS")})");
        if (gameIsr && IsrDispatch != null)
        {
            // Real hardware interrupt frame: FLAGS, CS, IP (6 bytes). A game ISR
            // ends in IRET -> Cpu.Iret pops all three and restores FLAGS (IF back
            // on). If instead the ISR ends in RETF or the dispatch MISSes (both
            // consume only CS:IP, 4 bytes), the FLAGS word is left on the stack:
            // pop it ourselves so SP balances and IF is re-enabled. Without this
            // rebalance either path drifts SP by 2 bytes per tick and IRET reads
            // garbage FLAGS (can latch IF=0 and kill all further IRQ dispatch).
            ushort spBefore = c.SP;
            ushort csBefore = c.CS;
            if (puerta386)
            {
                // Puerta de 386: el marco son TRES DWORDS (EFLAGS/CS/EIP) y el handler
                // termina en `iretd` (12 bytes). Con el marco de words, su iretd desapilaba
                // 12 bytes de un marco de 6 y la pila del interrumpido quedaba corrida —
                // medido en un juego DOS/16M: el `pop ds` de detras cargaba 14E6 y el kernel convertia
                // ese selector basura a segmento 0 (el DS=0000 del "fatal error 1005").
                c.SP -= 4; m.WrU32(c.SS, c.SP, flags);
                c.SP -= 4; m.WrU32(c.SS, c.SP, c.CS);
                c.SP -= 4; m.WrU32(c.SS, c.SP, 0);
            }
            else
            {
                c.SP -= 2; m.WrU16(c.SS, c.SP, flags);
                c.SP -= 2; m.WrU16(c.SS, c.SP, c.CS);
                c.SP -= 2; m.WrU16(c.SS, c.SP, 0);
            }
            // Same WholeProgram toggle as the software-INT dispatch above: under
            // Interp.WholeProgram (EXEC'd children) a nested ISR's terminal iret would
            // CONTINUE inside the nested Run instead of returning — double execution of
            // the interrupted flow (one test game's nondeterministic mid-instruction crash at
            // 479A:B522 via its hooked INT 8).
            bool savedWp = Interp.WholeProgram;
            Interp.WholeProgram = false;
            // PSTK class at the ISR boundary (an AdLib-driver hang): a timer ISR that switches to a
            // private stack (SP > entry sp0) must run to its terminal IRET, not bail on the
            // first ret over the switched stack. IsrMode makes ret/retf continue in the nested
            // Run; only enable it where the ISR ends in IRET (WholeProgram context: interpOnly /
            // EXEC'd children). In the lifted world (savedWp false) it stays off so a
            // RETF/miss-terminated handler still ends the nested Run.
            bool savedIsr = Interp.IsrMode;
            Interp.IsrMode = savedWp;
            IsrDispatch(c, m, seg, off);
            Interp.IsrMode = savedIsr;
            Interp.WholeProgram = savedWp;
            c.FarRet = false;   // ISR boundary: a RETF-terminated handler must not unwind the game
            // ISR boundary also preserves CS: FarDispatch mutates c.CS to route the
            // dispatch and a RETF-terminated or missed handler leaves it stale; the
            // interrupted Interp fetches through c.CS, so restore it unconditionally.
            c.CS = csBefore;
            if (c.SP == (ushort)(spBefore - 2))
            {
                c.SetFlagsWord(m.RdU16(c.SS, c.SP));
                c.SP = spBefore;
            }
        }
        else
        {
            Int(n, c, m);
            // The C# handler stands in for the whole ISR *including its IRET*: restore
            // the FLAGS captured before dispatch (IF back on). Without this, every IRQ
            // that falls to a C# handler (e.g. INT 9 when the game never hooks it)
            // latches IF=0 forever and kills all further IRQ/mouse-callback dispatch.
            c.SetFlagsWord(flags);
        }
    }

    private bool _servicing;

    /// <summary>
    /// Sirve las IRQs hardware pendientes (timer, teclado) que el PIC tenga listas y no
    /// enmascaradas, mientras IF esté activo. Se llama desde el HILO DEL JUEGO en un punto
    /// seguro (límite de frame de Cpu.Step), de modo que disparar la ISR (que reentra a
    /// FarDispatch sobre el mismo cpu/mem) no compita con el hilo del host. Guarda de
    /// reentrada por si una ISR vuelve a cruzar un límite de frame.
    /// </summary>
    // DIAG (heartbeat [HB] del host): contadores de despacho de IRQs, permanentes.
    public static long DiagBoundaryCalls, DiagBoundaryIfOff, DiagIrq0, DiagIrq1;
    static readonly bool _diagCbOportunidades =
        Environment.GetEnvironmentVariable("PAKU_DIAG_CB") == "1";
    static long _diagPollTotal, _diagPollReal, _diagPollBueno;

    long _vetosPilaGrande;
    bool _avisadoStubPisado;

    public void ServicePendingInterrupts(Cpu c, Memory m)
    {
        if (_servicing || Bda == null!) return;
        if (_diagCbOportunidades)
        {
            _diagPollTotal++;
            if (!Memory.SegmentosSonSelectores) _diagPollReal++;
            if (!Memory.SegmentosSonSelectores && c.IF && !Interp.PilaGrandePm16(c, m)) _diagPollBueno++;
            if ((_diagPollTotal % 20000) == 0)
                Console.Error.WriteLine($"[DIAGCB] polls={_diagPollTotal:N0} enModoReal={_diagPollReal:N0} " +
                                        $"utiles(real+IF+pila)={_diagPollBueno:N0}");
        }
        // Mundo mixto DOS/16M con pila grande: aquí una IRQ no se puede entregar bien
        // (el marco iría por SP de 16 bits a base+SP, lejos de la pila real). Se deja
        // PENDIENTE en el PIC y la entrega el bucle del mundo mixto con ESP entero.
        // Vigila que nadie haya pisado la cadena de retorno del callback del raton: es ROM del
        // BIOS, o sea que en un PC real esto no podria pasar. Una comparacion por limite de frame.
        if (!_avisadoStubPisado
            && m.Raw[Memory.Lin(Cpu.IvtStubSeg, Cpu.CbRatonUirOff)] != 0xFA)
        {
            _avisadoStubPisado = true;
            Console.Error.WriteLine(
                $"[STUB PISADO] la cadena del raton en F000:{Cpu.CbRatonUirOff:X4} ya no es CLI: " +
                $"vale {m.Raw[Memory.Lin(Cpu.IvtStubSeg, Cpu.CbRatonUirOff)]:X2}. " +
                $"Invitado en {c.CS:X4} SS:SP={c.SS:X4}:{c.SP:X4} msw={c.Msw:X8} " +
                $"hist={Interp.HistDumpPublico()}");
        }
        if (Interp.PilaGrandePm16(c, m))
        {
            // Un veto que no se levanta NUNCA es un cuelgue: ninguna IRQ entra, ni la del reloj,
            // y el invitado se queda esperando un tick que no llega. Cuesta un contador y avisa
            // UNA vez con lo que hace falta para saber por qué: el SS que lo está vetando.
            if (++_vetosPilaGrande == 2_000_000)
                Console.Error.WriteLine(
                    $"[VETO] la entrega de IRQ lleva 2M vetos seguidos por pila grande: " +
                    $"SS={c.SS:X4} ESP={c.ESP:X8} CS={c.SS:X4}/{c.CS:X4} msw={c.Msw:X8}");
            return;
        }
        _vetosPilaGrande = 0;
        _servicing = true;
        DiagBoundaryCalls++;
        if (!c.IF) DiagBoundaryIfOff++;
        try
        {
            int guard = 0;
            while (c.IF && guard++ < 8 && Pic.AckNext(out int irq))
            {
                if (irq == 0) DiagIrq0++; else if (irq == 1) DiagIrq1++;
                // Banco de pruebas por funcion: una IRQ se graba por su EFECTO NETO, igual que
                // una INT. Es el unico punto donde el hardware entra en el invitado en modo
                // real, asi que es el unico gancho que hace falta (ver CorpusRecorder.MarcaIrq).
                byte vec = IrqToVectorPublic(irq);
                bool grabando = CorpusRecorder.Activo;
                if (grabando) CorpusRecorder.MarcaIrq(vec, c, m);
                try { FireInterrupt(vec, c, m); }
                finally { if (grabando) CorpusRecorder.ConfirmaIrq(c, m); }
            }
            if (c.IF) UserCallbackService?.Invoke(c, m);
        }
        finally { _servicing = false; }
    }

    private byte IrqToVectorPublic(int irq) => Pic.IrqToVector(irq);

    /// <summary>Tras AttachMemory (que stubbea los vectores de los INT registrados),
    /// restaura los vectores que el JUEGO tenía instalados en un snapshot (p.ej. el
    /// timer del driver de sonido). Los trampolines del explorador (9F00) y los
    /// stubs propios (F000) no se restauran.</summary>
    public static void RestoreGameVectors(Memory m, byte[] snap)
    {
        for (int v = 0; v < 256; v++)
        {
            ushort off = (ushort)(snap[v * 4] | (snap[v * 4 + 1] << 8));
            ushort seg = (ushort)(snap[v * 4 + 2] | (snap[v * 4 + 3] << 8));
            if (seg == 0 && off == 0) continue;
            if (seg == 0x9F00 || seg == Cpu.IvtStubSeg) continue;
            if (seg * 16 + off >= 0xA0000) continue;
            Array.Copy(snap, v * 4, m.Raw, v * 4, 4);
        }
    }
}
