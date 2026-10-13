using Iced.Intel;

namespace PakuPort.Core;

/// <summary>
/// Intérprete del mundo PLANO de 32 bits (fase 1): ejecuta la imagen que produce
/// <see cref="LeLoader"/> sin segmentos, direccionando linealmente sobre <see cref="Memory"/>.
///
/// <b>Por qué es una clase aparte y no un modo de <see cref="Interp"/>.</b> Aquél lleva el
/// desenrollado FarRet, los overlays VROOMM, el despacho far, los modos de ISR y el key-space del
/// código lifteado — toda la maquinaria sobre la que están validados los 21 juegos y los 4 gates.
/// Ese mundo es segmentado por definición; meterle un mundo sin segmentos es pedir una regresión
/// a cambio de nada, porque aquí no hace falta ni una de esas piezas. Lo que SÍ se comparte es lo
/// que importa: la ALU de <see cref="Cpu"/>, validada byte a byte contra Unicorn (fase 0), y la
/// memoria, cuyo primer megabyte es el mismo para los dos mundos.
///
/// <b>Segmentos.</b> Bajo un extensor DOS el programa corre en modelo plano: CS/DS/ES/SS tienen
/// base 0 y límite 4 GB, así que un prefijo de segmento no cambia la dirección. Aquí se ignoran a
/// propósito. FS/GS con base propia son cosa del host DPMI (fase 2) y hoy no se modelan.
///
/// <b>Lo que no está, lanza con la dirección.</b> Nunca calla: la clase de fallo que este proyecto
/// persigue es la divergencia silenciosa.
/// </summary>
public static class Interp32
{
    /// <summary>Servicio de interrupción del mundo plano: el `INT n` del juego. En la fase 1 no
    /// hay ninguno y el intérprete se para; en la fase 2 aquí entra el host DPMI/DOS.</summary>
    public delegate bool ServiceFn(byte intNo, Cpu c, Memory m, ref uint eip);
    public static ServiceFn? OnInt;

    /// <summary>Consulta de descriptor para `lar`/`lsl`/`verr`/`verw`. Lo instala el host de
    /// servicios, que es quien tiene la tabla de selectores: sin él estas instrucciones no
    /// tendrían contra qué contestar y lo honrado es decir que el selector no vale.</summary>
    public delegate bool DescriptorFn(ushort sel, out uint limite, out uint derechos, out bool escribible);
    public static DescriptorFn? OnDescriptor;

    /// <summary>Interrupciones HARDWARE del mundo plano. Se consulta cada
    /// <see cref="PasoDePoll"/> instrucciones; si devuelve true, el interprete empuja el marco
    /// (EFLAGS, CS, EIP), baja IF y salta al manejador — igual que el hardware.
    ///
    /// Sin esto un juego que se conduce desde sus IRQ no avanza NUNCA: un juego DOS/4GW engancha el
    /// timer y su bucle principal espera el tick, asi que ni siquiera llega a mirar el teclado.
    /// Se midio descartando una por una todas las vias de entrada (INT 16h: cero llamadas;
    /// puerto 0x60: cero accesos; buffer de la BDA: teclas metidas y sin leer).</summary>
    public delegate bool IrqFn(Cpu c, Memory m, out ushort selector, out uint offset);
    public static IrqFn? OnIrq;

    /// <summary>Cada cuantas instrucciones se mira si hay IRQ pendiente. Mismo orden de magnitud
    /// que el poll del mundo de 16 bits (Cpu.PollMask = 4096).</summary>
    public static long PasoDePoll = 4096;

    /// <summary>Instrucciones ejecutadas EN TOTAL, acumuladas entre llamadas a <see cref="Run"/>.
    /// Es el reloj del invitado: la unica magnitud de tiempo que no depende del reloj de pared y
    /// por tanto la unica con la que dos ejecuciones del mismo binario son comparables.
    ///
    /// Se actualiza en la frontera de poll, no por instruccion: la granularidad es
    /// <see cref="PasoDePoll"/> y eso basta para un tick del PIT, que son ordenes de magnitud
    /// mas instrucciones.</summary>
    public static long Ejecutadas;

    /// <summary>Base lineal de un selector. En el modelo plano casi todos valen 0 —los punteros
    /// del programa YA son direcciones lineales, cosa medida al comparar la imagen con la de
    /// DOS/4GW—, pero el juego crea selectores con base propia por DPMI y una llamada far a uno
    /// de ésos tiene que ir a base+offset. Sin host instalado, base 0.</summary>
    public static Func<ushort, uint>? BaseDeSelector;
    static int _avisoSelectorCadena;
    // Cacheada: se consulta por cada elemento de movs y la propiedad vive en otra clase.
    static readonly bool _procPixel = Platform.VideoState.ProcedenciaPixel;
    static uint BaseSel(ushort s) => BaseDeSelector?.Invoke(s) ?? 0u;

    // ── CACHE DE DECODIFICACIÓN ────────────────────────────────────────────────────────────────
    //
    // Sin él, cada instrucción EJECUTADA se decodifica con Iced otra vez — y decodificar cuesta
    // más que ejecutar una instrucción simple. El techo medido era ~11,5M instr/s, que en
    // un juego 3D de 32 bits son ~7 fps de motor a CUALQUIER velocidad de governor (y
    // sin cadencia no hay paralaje). Es el "acelerador" que el pivote a intérprete-puro
    // dejó pendiente.
    //
    // La INVALIDACIÓN es lo delicado: un juego de prueba se AUTOMODIFICA (parchea inmediatos del motor
    // 3D en runtime). Se resuelve con una GENERACIÓN POR PÁGINA de 4K: toda escritura al
    // espacio alto incrementa la generación de su página (Memory.WrLin8Directo), y una entrada
    // del cache sólo vale si la generación combinada de sus páginas (la de la instrucción y la
    // siguiente, por si cruza) no ha cambiado. El índice de página va con máscara: el aliasing
    // sólo puede invalidar DE MÁS, nunca de menos.
    //
    // El cache sólo sirve código del espacio ALTO (>1 MiB): el bajo lo escriben mil caminos del
    // mundo de 16 bits que no pasan por WrLin8, y vigilarlos todos costaría más que el cache.
    // La clave es (lineal, eip): la MISMA dirección lineal decodificada con otro EIP (un
    // selector con base) daría destinos de salto distintos.
    //
    // PAKU_INTERP32_CACHE=0 lo apaga (el A/B de la escotilla).
    public static readonly bool CacheDecode =
        Environment.GetEnvironmentVariable("PAKU_INTERP32_CACHE") != "0";
    const int CacheBits = 18;                      // 262144 entradas (~12 MB con la Instruction)
    const uint CacheMask = (1u << CacheBits) - 1;
    static readonly Instruction[] _cInsn = new Instruction[1 << CacheBits];
    static readonly uint[] _cLin = new uint[1 << CacheBits];    // 0 = entrada vacía
    static readonly uint[] _cEip = new uint[1 << CacheBits];
    static readonly long[] _cGen = new long[1 << CacheBits];

    static long GenCombinada(uint lin) =>
        Memory.GenPagina[(lin >> 12) & Memory.GenPaginaMask]
        + Memory.GenPagina[((lin + 14) >> 12) & Memory.GenPaginaMask];

    /// <summary>PAKU_LOG_FAR32=1: traza cada transferencia far con su selector, su offset y la
    /// dirección lineal resultante. Una llamada far que acaba en memoria vacía es indepurable sin
    /// esto: el destino sale de un puntero en memoria y no se ve en el desensamblado.</summary>
    public static readonly bool LogFar = Environment.GetEnvironmentVariable("PAKU_LOG_FAR32") == "1";

    /// <summary>PAKU_LOG_PORTS32=1: cada in/out del mundo plano. Un driver de sonido que "no
    /// encuentra hardware" sondea puertos, y sin ver esa conversación no hay forma de saber si el
    /// que no contesta somos nosotros o es que sondea otra dirección.</summary>
    public static readonly bool LogPuertos = Environment.GetEnvironmentVariable("PAKU_LOG_PORTS32") == "1";

    /// <summary>PAKU_WATCH_FS=1: avisa de cada cambio de FS con la instruccion que lo hizo. El
    /// driver de sonido de HMI toma su segmento de datos DE FS (`push fs` / `pop ds`), asi que
    /// quien lo ponga a cero le deja los punteros de funcion apuntando a la IVT.</summary>
    public static readonly bool VigilarFs = Environment.GetEnvironmentVariable("PAKU_WATCH_FS") == "1";

    /// <summary>PAKU_LOG_FUERA32=1: cada escritura que se sale del espacio, con la instrucción que
    /// la hizo y los selectores con SUS BASES. Memory sólo sabe la dirección final, y con eso no
    /// se distingue una base mal leída de un descriptor de un puntero que el juego corrompió.</summary>
    public static readonly bool LogFuera = Environment.GetEnvironmentVariable("PAKU_LOG_FUERA32") == "1";

    static long _perdidasPrevias;
    static int _avisosFuera;

    /// <summary>Por qué terminó <see cref="Run"/>. <see cref="Fin.CambioA16"/>: una
    /// transferencia far cargó un CS que <see cref="CsSigue32"/> declara de 16 bits — el mundo
    /// mixto de DOS/16M (kernel de 16 bits + juego de 32) devuelve ahí el control al intérprete
    /// de 16 bits, que es el único que decodifica bien ese código.</summary>
    public enum Fin { Presupuesto, Hlt, IntSinServicio, FueraDeLaImagen, CambioA16 }

    /// <summary>Mundo mixto 16/32 (DOS/16M): dice si un CS recién cargado sigue siendo de 32
    /// bits. Si devuelve false, <see cref="Run"/> termina con <see cref="Fin.CambioA16"/> en vez
    /// de decodificar código de 16 bits como si fuera de 32 (que era una divergencia SILENCIOSA).
    /// null (mundo LE/DPMI de siempre) = no se comprueba nada.</summary>
    public static Func<ushort, bool>? CsSigue32;

    public sealed class Resultado
    {
        public Fin Motivo;
        public uint Eip;
        /// <summary>EIP como OFFSET de CS. Hace falta para reanudar la ejecucion por trozos (que
        /// es como se inyecta teclado): `Eip` es la lineal y no vale para volver a entrar.</summary>
        public uint EipOffset;
        public ushort Cs;
        public long Instrucciones;
        public byte IntNo;
        /// <summary>Últimos EIP ejecutados, del más reciente al más antiguo (caja negra).</summary>
        public uint[] Historial = System.Array.Empty<uint>();
        public override string ToString() =>
            $"{Motivo} tras {Instrucciones} instrucciones en EIP=0x{Eip:X8}" +
            (Motivo == Fin.IntSinServicio ? $" (INT {IntNo:X2}h)" : "");
    }

    sealed class LinearCodeReader : CodeReader
    {
        public Memory M = null!;
        public uint Position;
        public override int ReadByte() => M.RdLin8(Position++);
    }

    // 512 y no 64: cuando un salto perdido cae en memoria a ceros, el intérprete recorre cientos
    // de `add [eax],al` de dos bytes antes de morir, y con un historial corto el punto de partida
    // —lo único que importa— se ha ido de la ventana.
    const uint HistLen = 512;
    static readonly uint[] _hist = new uint[HistLen];
    static int _histIdx;

    /// <summary>Anillo de TRANSFERENCIAS de control (de dónde a dónde saltó). Cuando el flujo se
    /// va a memoria vacía, el historial de EIP se llena entero con el paseo por los ceros y no
    /// dice nada; lo que hace falta es el salto que lo originó. Se anota sólo en los saltos, así
    /// que cabe muchísimo más recorrido en las mismas entradas.</summary>
    const int SaltosLen = 64;
    static readonly (uint desde, uint hasta, string que)[] _saltos = new (uint, uint, string)[SaltosLen];
    static int _saltosIdx;

    static int _saltosAlVacio;
    static int _avisosLinBajo;

    /// <summary>Cpu en curso, sólo para los diagnósticos de Salto (volcar la pila al [VACIO]).</summary>
    static Cpu? CpuActual;

    static void Salto(uint desde, uint hasta, string que, uint baseSeg = 0)
    {
        _saltos[_saltosIdx++ & (SaltosLen - 1)] = (desde, hasta, que);
        // Un salto a memoria EN BLANCO se caza AQUÍ o no se caza: a partir de ahí el intérprete
        // se pasea por los ceros ejecutando `add [eax],al` durante millones de instrucciones, y
        // para cuando algo falla el origen se ha ido de cualquier ventana de historial.
        var m = Memory.Last;
        uint destinoLineal = baseSeg + hasta;
        if (m != null && _saltosAlVacio < 4 && m.RdLin32(destinoLineal) == 0 && m.RdLin32(destinoLineal + 4) == 0)
        {
            _saltosAlVacio++;
            Console.Error.WriteLine($"[VACIO] {que} 0x{desde:X8} -> 0x{destinoLineal:X8}: el destino está a CERO");
            Console.Error.WriteLine($"[VACIO]   camino: {Hist()}");
            if (CpuActual != null)
            {
                var cD = CpuActual;
                var sb = new System.Text.StringBuilder($"[VACIO]   SS:ESP={cD.SS:X4}:{cD.ESP:X8} pila:");
                for (int i = -4; i <= 8; i++)
                    sb.Append($" [{i * 4:+0;-0}]={m.RdLin32((uint)(BaseSel(cD.SS) + cD.ESP + i * 4)):X8}");
                Console.Error.WriteLine(sb.ToString());
            }
        }
    }

    static string Saltos()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= SaltosLen; i++)
        {
            var s = _saltos[(_saltosIdx - i) & (SaltosLen - 1)];
            if (s.que == null) break;
            sb.Append($"\n    {s.que,-9} 0x{s.desde:X8} -> 0x{s.hasta:X8}");
        }
        return sb.ToString();
    }

    /// <summary>Ejecuta desde <paramref name="eip"/> hasta agotar el presupuesto, encontrar un
    /// `hlt`, o toparse con un `INT` que nadie sirve.</summary>
    /// <summary>Tope inicial de la pila. Si ESP lo supera es que hay mas pops que pushes, y eso
    /// se caza AQUI y no 60 instrucciones despues, cuando el `ret` ya ha saltado a la nada.</summary>
    public static uint TopeDePila;

    public static Resultado Run(Cpu c, Memory m, uint eip, long maxInsns)
    {
        if (TopeDePila == 0) TopeDePila = c.ESP;
        CpuActual = c;
        var reader = new LinearCodeReader { M = m };
        var decoder = Decoder.Create(32, reader, DecoderOptions.None);
        var res = new Resultado();
        long n = 0;
        ushort fsPrevio = c.FS;
        ushort gsPrevio = c.GS;

        while (n < maxInsns)
        {
            // EIP es un OFFSET DENTRO DE CS, no una dirección lineal. Para el programa principal
            // la base de CS es 0 y da igual, pero un driver cargado aparte corre con su propio
            // selector de código: sus llamadas por tabla (`call ebx` con EBX=0x324) son offsets
            // de SU segmento, y sin sumar la base acaban en la dirección 0x324, que es memoria
            // vacía. Se midió con el driver de sonido de HMI.
            uint lin = BaseSel(c.CS) + eip;
            if (lin < 0x400 && _avisosLinBajo++ < 2)
                Console.Error.WriteLine(
                    $"[LINBAJO] fetch en 0x{lin:X8} (CS={c.CS:X4} EIP=0x{eip:X8}) saltos:{Saltos()}");
            _hist[_histIdx++ & (int)(HistLen - 1)] = lin;
            Memory.UltimoEip32 = lin;       // para el vigilante de escrituras (PAKU_WATCH_LIN)
            Instruction insn;
            try
            {
            if (CacheDecode && lin >= Memory.Size)
            {
                int k = (int)(lin & CacheMask);
                long gen = GenCombinada(lin);
                if (_cLin[k] == lin && _cEip[k] == eip && _cGen[k] == gen)
                    insn = _cInsn[k];
                else
                {
                    reader.Position = lin;
                    decoder.IP = eip;
                    decoder.Decode(out insn);
                    if (insn.Code == Code.INVALID)
                    {
                        var ctx = new byte[40];
                        for (int i = 0; i < 40; i++) ctx[i] = m.RdLin8((uint)(lin - 16 + i));
                        throw new NotSupportedException(
                            $"interp32: opcode inválido @ 0x{lin:X8} (byte {m.RdLin8(lin):X2}) " +
                            $"bytes[-16..+24]={Convert.ToHexString(ctx)} hist={Hist()}{Saltos()}");
                    }
                    _cInsn[k] = insn; _cLin[k] = lin; _cEip[k] = eip; _cGen[k] = gen;
                }
            }
            else
            {
                reader.Position = lin;
                decoder.IP = eip;
                decoder.Decode(out insn);
                if (insn.Code == Code.INVALID)
                {
                    var ctx = new byte[40];
                    for (int i = 0; i < 40; i++) ctx[i] = m.RdLin8((uint)(lin - 16 + i));
                    throw new NotSupportedException(
                        $"interp32: opcode inválido @ 0x{lin:X8} (byte {m.RdLin8(lin):X2}) " +
                        $"bytes[-16..+24]={Convert.ToHexString(ctx)} hist={Hist()}{Saltos()}");
                }
            }
            }
            catch (FalloDePaginaException pf)
            {
                // #PF de FETCH: el decode lee memoria y la página puede no estar (demand paging
                // del VMM de DOS/4GW Professional). Se entrega AQUÍ, con el EIP verdadero: si se
                // escapa de Run, lo caza el catch del intérprete de 16 bits y entrega con su `ip`
                // rancio y truncado — medido en un juego DOS/4GW Pro: guardaba F457 en vez de 0x100C0D50 y el
                // juego "reanudaba" en memoria baja rellena de CC.
                if (Interp.Trace) Console.Error.WriteLine(
                    $"[PF32] fetch: {pf.Message} @ 0x{lin:X8} hist={Hist()}");
                eip = Interp.EntregaFalloDePagina(c, m, pf, eip, out bool porTareaF);
                if (porTareaF && CsSigue32 != null && !CsSigue32(c.CS))
                { res.Motivo = Fin.CambioA16; res.Cs = c.CS; res.EipOffset = eip; res.Eip = lin; goto fin; }
                continue;
            }
            uint next = (uint)(eip + (uint)insn.Length);
            n++;

            // Frontera de interrupcion: ANTES de ejecutar, como el hardware, y solo con IF puesto.
            // El reloj del invitado avanza AUNQUE IF este bajo: si no, un juego que enmascara las
            // interrupciones un rato pararia el tiempo, y las IRQ le llegarian todas de golpe al
            // volver a permitirlas.
            if ((n & (PasoDePoll - 1)) == 0)
            {
                Ejecutadas += PasoDePoll;
                // `Cpu.TickCount` es el reloj del invitado del mundo de 16 bits, en instrucciones,
                // y de el cuelgan los plazos de la Sound Blaster. En el mundo plano no lo movia
                // nadie, asi que un bloque de DMA con plazo "en N instrucciones" no vencia NUNCA.
                Cpu.TickCount += PasoDePoll;
                if (OnIrq != null && c.IF && OnIrq(c, m, out ushort selIrq, out uint offIrq))
                {
                    if (Platform.FlatServices.LogIrq)
                        System.Console.Error.WriteLine(
                            $"[IRQ32]   interrumpido en CS={c.CS:X4} EIP=0x{eip:X8} (lineal 0x{lin:X8}) " +
                            $"insn={insn}");
                    Push(c, m, c.EFlagsWord(), 32);
                    Push(c, m, c.CS, 32);
                    Push(c, m, eip, 32);      // se reanuda en ESTA instruccion, no en la siguiente
                    c.IF = false;
                    Salto(lin, BaseSel(selIrq) + offIrq, "irq");
                    c.CS = selIrq; eip = offIrq;
                    continue;
                }
            }
            if (c.ESP > TopeDePila)
                throw new InvalidOperationException(
                    $"interp32: la PILA se ha salido por arriba (ESP=0x{c.ESP:X8} > tope 0x{TopeDePila:X8}) " +
                    $"antes de 0x{lin:X8} ({insn}) — hay mas pops que pushes{Saltos()}");

            // Para el catch de #PF: un fallo restaura la CPU al INICIO de la instrucción
            // (386: las instrucciones que fallan son reiniciables). Sin esto, un pushad cuyo
            // 4º push falla deja ESP 12 bytes abajo y el reintento vuelve a empujarlo TODO —
            // medido en un juego DOS/4GW Pro: su función de carga salía con la pila corrida y el `ret`
            // saltaba a una dirección DE la propia pila.
            uint espAntesPf = c.ESP;
            try
            {
            switch (insn.Mnemonic)
            {
                // ---------- transferencia ----------
                case Mnemonic.Mov:
                {
                    // Registros de control (espejo del case Mov de Interp): UVBELib, embebida en
                    // un juego DOS/4GW Pro, lee CR0 para sondear la CPU desde el mundo plano de 32 bits.
                    if (insn.Op1Register is >= Register.CR0 and <= Register.CR15)
                    {
                        SetR(c, insn.Op0Register, insn.Op1Register switch
                        {
                            Register.CR0 => c.Msw,
                            Register.CR2 => Memory.Cr2,
                            Register.CR3 => c.Cr3,
                            _ => 0u,
                        }, 32);
                        break;
                    }
                    if (insn.Op0Register is >= Register.CR0 and <= Register.CR15)
                    {
                        uint vCr = Rd(c, m, in insn, 1, 32);
                        if (insn.Op0Register == Register.CR0)
                        {
                            Memory.ConfiguraPaginacion((vCr & 0x80000000u) != 0, c.Cr3);
                            c.Msw = vCr;
                        }
                        else if (insn.Op0Register == Register.CR2) Memory.Cr2 = vCr;
                        else if (insn.Op0Register == Register.CR3)
                        { c.Cr3 = vCr; Memory.ConfiguraPaginacion(Memory.Paginacion, vCr); }
                        break;
                    }
                    var d = Dst(c, m, in insn, 0); Wr(c, m, in d, Rd(c, m, in insn, 1, Size(in insn, 0)), Size(in insn, 0)); break;
                }
                case Mnemonic.Movzx:
                {
                    int ss = SrcSize(in insn);
                    uint v = Rd(c, m, in insn, 1, ss);
                    Wr(c, m, Dst(c, m, in insn, 0), ss == 8 ? (byte)v : (ushort)v, Size(in insn, 0));
                    break;
                }
                case Mnemonic.Movsx:
                {
                    int ss = SrcSize(in insn);
                    uint v = Rd(c, m, in insn, 1, ss);
                    int sv = ss == 8 ? (sbyte)v : (short)v;
                    Wr(c, m, Dst(c, m, in insn, 0), (uint)sv, Size(in insn, 0));
                    break;
                }
                case Mnemonic.Xchg:
                {
                    int sz = Size(in insn, 0);
                    var a = Dst(c, m, in insn, 0); var b = Dst(c, m, in insn, 1);
                    uint x = RdD(c, m, in a, sz), y = RdD(c, m, in b, sz);
                    Wr(c, m, in a, y, sz); Wr(c, m, in b, x, sz);
                    break;
                }
                // LEA da el OFFSET, no la dirección lineal: no toca memoria, así que la base del
                // selector NO entra. Con `Ea()` entraba, y en un mundo cuyo segmento tiene base 0
                // —la de un juego DOS/4GW— eso no se nota; con la de un juego con extensor embebido (0x800000) TODO puntero
                // calculado salía 8 MiB por encima. Su motor 3D elige la entrada a su código
                // generado con `lea ebx,[ebx*4+0x4530]` y `jmp ebx`, así que saltaba a memoria en
                // blanco y se paseaba por los ceros durante millones de instrucciones.
                case Mnemonic.Lea: Wr(c, m, Dst(c, m, in insn, 0), EaSinBase(c, in insn), Size(in insn, 0)); break;

                // Punteros far 16:32 (`les edi,[ebp+0Ch]`): 4 bytes de offset + 2 de selector.
                // El selector se guarda porque el host DPMI lo necesita; el direccionamiento no
                // lo usa, que el modelo es plano.
                case Mnemonic.Lds: case Mnemonic.Les: case Mnemonic.Lfs:
                case Mnemonic.Lgs: case Mnemonic.Lss:
                {
                    uint a = Ea(c, in insn);
                    int sz = Size(in insn, 0);
                    ushort sel;
                    if (sz == 32) { Wr(c, m, Dst(c, m, in insn, 0), m.RdLin32(a), 32); sel = m.RdLin16(a + 4); }
                    else { Wr(c, m, Dst(c, m, in insn, 0), m.RdLin16(a), 16); sel = m.RdLin16(a + 2); }
                    switch (insn.Mnemonic)
                    {
                        case Mnemonic.Lds: c.DS = sel; break;
                        case Mnemonic.Les: c.ES = sel; break;
                        case Mnemonic.Lfs: c.FS = sel; break;
                        case Mnemonic.Lgs: c.GS = sel; break;
                        default: c.SS = sel; break;
                    }
                    break;
                }
                case Mnemonic.Xlatb: c.AL = m.RdLin8((uint)(c.EBX + c.AL)); break;
                case Mnemonic.Cbw: c.AX = (ushort)(short)(sbyte)c.AL; break;
                case Mnemonic.Cwde: c.EAX = (uint)(int)(short)c.AX; break;
                case Mnemonic.Cwd: c.DX = (ushort)(((c.AX & 0x8000) != 0) ? 0xFFFF : 0); break;
                case Mnemonic.Cdq: c.EDX = (c.EAX & 0x80000000u) != 0 ? 0xFFFFFFFFu : 0u; break;
                case Mnemonic.Lahf: c.AH = (byte)c.FlagsWord(); break;
                case Mnemonic.Sahf: c.Sahf(); break;

                // ---------- pila ----------
                case Mnemonic.Push:
                {
                    int sz = TamPila(c, m, in insn, lin);
                    uint v = Rd(c, m, in insn, 0, sz == 32 && EsSegmento(insn.Op0Kind == OpKind.Register ? insn.Op0Register : Register.None) ? 16 : sz);
                    Push(c, m, v, sz);
                    break;
                }
                case Mnemonic.Pop:
                {
                    int sz = TamPila(c, m, in insn, lin);
                    uint v = Pop(c, m, sz);
                    bool seg = insn.Op0Kind == OpKind.Register && EsSegmento(insn.Op0Register);
                    Wr(c, m, Dst(c, m, in insn, 0), v, seg ? 16 : sz);
                    break;
                }
                case Mnemonic.Pushfd: Push(c, m, c.EFlagsWord(), 32); break;
                case Mnemonic.Popfd: c.SetEFlagsWord(Pop(c, m, 32)); break;
                case Mnemonic.Pushf: Push(c, m, c.FlagsWord(), 16); break;
                case Mnemonic.Popf: c.SetFlagsWord((ushort)Pop(c, m, 16)); break;
                // PUSHA/POPA de 16 bits. El driver de sonido los usa (`pushaw`), y faltaban: una
                // `popaw` ejecutada como `popad` desapilaria 32 bytes en vez de 16 y descuadraria
                // la pila del llamador -- que es exactamente el sintoma que se vio (un `pop fs`
                // sacando el valor de la ranura de al lado).
                case Mnemonic.Pusha:
                {
                    ushort t = (ushort)c.ESP;
                    Push(c, m, c.AX, 16); Push(c, m, c.CX, 16); Push(c, m, c.DX, 16); Push(c, m, c.BX, 16);
                    Push(c, m, t, 16); Push(c, m, (ushort)c.EBP, 16);
                    Push(c, m, (ushort)c.ESI, 16); Push(c, m, (ushort)c.EDI, 16);
                    break;
                }
                case Mnemonic.Popa:
                    c.DI = (ushort)Pop(c, m, 16); c.SI = (ushort)Pop(c, m, 16);
                    c.BP = (ushort)Pop(c, m, 16);
                    c.ESP += 2;                                   // el SP apilado se descarta
                    c.BX = (ushort)Pop(c, m, 16); c.DX = (ushort)Pop(c, m, 16);
                    c.CX = (ushort)Pop(c, m, 16); c.AX = (ushort)Pop(c, m, 16);
                    break;

                case Mnemonic.Pushad:
                {
                    uint t = c.ESP;
                    Push(c, m, c.EAX, 32); Push(c, m, c.ECX, 32); Push(c, m, c.EDX, 32); Push(c, m, c.EBX, 32);
                    Push(c, m, t, 32); Push(c, m, c.EBP, 32); Push(c, m, c.ESI, 32); Push(c, m, c.EDI, 32);
                    break;
                }
                case Mnemonic.Popad:
                    c.EDI = Pop(c, m, 32); c.ESI = Pop(c, m, 32); c.EBP = Pop(c, m, 32);
                    c.ESP += 4;                                   // el ESP apilado se descarta
                    c.EBX = Pop(c, m, 32); c.EDX = Pop(c, m, 32);
                    c.ECX = Pop(c, m, 32); c.EAX = Pop(c, m, 32);
                    break;
                case Mnemonic.Enter:
                {
                    int frame = insn.Immediate16, nivel = insn.Immediate8_2nd & 0x1F;
                    Push(c, m, c.EBP, 32);
                    uint fp = c.ESP;
                    for (int i = 1; i < nivel; i++) { c.EBP -= 4; Push(c, m, m.RdLin32(c.EBP), 32); }
                    if (nivel > 0) Push(c, m, fp, 32);
                    c.EBP = fp; c.ESP -= (uint)frame;
                    break;
                }
                case Mnemonic.Leave: c.ESP = c.EBP; c.EBP = Pop(c, m, 32); break;

                // ---------- aritmética ----------
                case Mnemonic.Add: Bin(c, m, in insn, (x, y, s) => Alu(c, s, x, y, 'a')); break;
                case Mnemonic.Adc: Bin(c, m, in insn, (x, y, s) => Alu(c, s, x, y, 'A')); break;
                case Mnemonic.Sub: Bin(c, m, in insn, (x, y, s) => Alu(c, s, x, y, 's')); break;
                case Mnemonic.Sbb: Bin(c, m, in insn, (x, y, s) => Alu(c, s, x, y, 'S')); break;
                case Mnemonic.And: Bin(c, m, in insn, (x, y, s) => Alu(c, s, x, y, '&')); break;
                case Mnemonic.Or: Bin(c, m, in insn, (x, y, s) => Alu(c, s, x, y, '|')); break;
                case Mnemonic.Xor: Bin(c, m, in insn, (x, y, s) => Alu(c, s, x, y, '^')); break;
                case Mnemonic.Cmp:
                {
                    int sz = Size(in insn, 0);
                    Alu(c, sz, Rd(c, m, in insn, 0, sz), Rd(c, m, in insn, 1, sz), 's');
                    break;
                }
                case Mnemonic.Test:
                {
                    int sz = Size(in insn, 0);
                    Alu(c, sz, Rd(c, m, in insn, 0, sz), Rd(c, m, in insn, 1, sz), '&');
                    break;
                }
                case Mnemonic.Inc: Un(c, m, in insn, (x, s) => s == 32 ? c.Inc32(x) : s == 16 ? c.Inc16((ushort)x) : c.Inc8((byte)x)); break;
                case Mnemonic.Dec: Un(c, m, in insn, (x, s) => s == 32 ? c.Dec32(x) : s == 16 ? c.Dec16((ushort)x) : c.Dec8((byte)x)); break;
                case Mnemonic.Neg: Un(c, m, in insn, (x, s) => s == 32 ? c.Neg32(x) : s == 16 ? c.Neg16((ushort)x) : c.Neg8((byte)x)); break;
                case Mnemonic.Not: Un(c, m, in insn, (x, s) => s == 32 ? ~x : s == 16 ? (uint)(ushort)~x : (byte)~x); break;

                case Mnemonic.Mul:
                {
                    int sz = Size(in insn, 0); uint v = Rd(c, m, in insn, 0, sz);
                    if (sz == 32) c.Mul32(v); else if (sz == 16) c.Mul16((ushort)v); else c.Mul8((byte)v);
                    break;
                }
                case Mnemonic.Imul:
                    switch (insn.OpCount)
                    {
                        case 1:
                        {
                            int sz = Size(in insn, 0); uint v = Rd(c, m, in insn, 0, sz);
                            if (sz == 32) c.Imul32(v); else if (sz == 16) c.Imul16((ushort)v); else c.Imul8((byte)v);
                            break;
                        }
                        case 2:
                        {
                            int sz = Size(in insn, 0);
                            uint a = Rd(c, m, in insn, 0, sz), b = Rd(c, m, in insn, 1, sz);
                            Wr(c, m, Dst(c, m, in insn, 0), sz == 32 ? c.Imul32Two(a, b) : c.Imul16Two((ushort)a, (ushort)b), sz);
                            break;
                        }
                        default:
                        {
                            int sz = Size(in insn, 0);
                            uint a = Rd(c, m, in insn, 1, sz), b = Rd(c, m, in insn, 2, sz);
                            Wr(c, m, Dst(c, m, in insn, 0), sz == 32 ? c.Imul32Two(a, b) : c.Imul16Two((ushort)a, (ushort)b), sz);
                            break;
                        }
                    }
                    break;
                case Mnemonic.Div:
                case Mnemonic.Idiv:
                {
                    int sz = Size(in insn, 0); uint v = Rd(c, m, in insn, 0, sz);
                    bool sgn = insn.Mnemonic == Mnemonic.Idiv;
                    try
                    {
                        if (sz == 32) { if (sgn) c.Idiv32(v); else c.Div32(v); }
                        else if (sz == 16) { if (sgn) c.Idiv16((ushort)v); else c.Div16((ushort)v); }
                        else { if (sgn) c.Idiv8((byte)v); else c.Div8((byte)v); }
                    }
                    catch (ArithmeticException)
                    {
                        // #DE. En modo protegido lo vectoriza el manejador del extensor; sin host
                        // DPMI (fase 2) no hay a dónde ir, así que se para con la dirección.
                        throw new NotSupportedException($"interp32: #DE @ 0x{lin:X8} ({insn}) — falta el manejador de excepciones del host (fase 2)");
                    }
                    break;
                }

                // ---------- desplazamientos ----------
                case Mnemonic.Shl: case Mnemonic.Sal: Sh(c, m, in insn, (v, k, s) => s == 32 ? c.Shl32(v, k) : s == 16 ? c.Shl16((ushort)v, k) : c.Shl8((byte)v, k)); break;
                case Mnemonic.Shr: Sh(c, m, in insn, (v, k, s) => s == 32 ? c.Shr32(v, k) : s == 16 ? c.Shr16((ushort)v, k) : c.Shr8((byte)v, k)); break;
                case Mnemonic.Sar: Sh(c, m, in insn, (v, k, s) => s == 32 ? c.Sar32(v, k) : s == 16 ? c.Sar16((ushort)v, k) : c.Sar8((byte)v, k)); break;
                case Mnemonic.Rol: Sh(c, m, in insn, (v, k, s) => s == 32 ? c.Rol32(v, k) : s == 16 ? c.Rol16((ushort)v, k) : c.Rol8((byte)v, k)); break;
                case Mnemonic.Ror: Sh(c, m, in insn, (v, k, s) => s == 32 ? c.Ror32(v, k) : s == 16 ? c.Ror16((ushort)v, k) : c.Ror8((byte)v, k)); break;
                case Mnemonic.Rcl: Sh(c, m, in insn, (v, k, s) => s == 32 ? c.Rcl32(v, k) : s == 16 ? c.Rcl16((ushort)v, k) : c.Rcl8((byte)v, k)); break;
                case Mnemonic.Rcr: Sh(c, m, in insn, (v, k, s) => s == 32 ? c.Rcr32(v, k) : s == 16 ? c.Rcr16((ushort)v, k) : c.Rcr8((byte)v, k)); break;
                case Mnemonic.Shld: case Mnemonic.Shrd:
                {
                    int sz = Size(in insn, 0);
                    int k = insn.GetOpKind(2) == OpKind.Immediate8 ? insn.Immediate8 : c.CL;
                    var d = Dst(c, m, in insn, 0);
                    uint dv = RdD(c, m, in d, sz), sv = Rd(c, m, in insn, 1, sz);
                    uint r = insn.Mnemonic == Mnemonic.Shld
                        ? (sz == 32 ? c.Shld32(dv, sv, k) : c.Shld16((ushort)dv, (ushort)sv, k))
                        : (sz == 32 ? c.Shrd32(dv, sv, k) : c.Shrd16((ushort)dv, (ushort)sv, k));
                    Wr(c, m, in d, r, sz);
                    break;
                }

                // ---------- bits ----------
                case Mnemonic.Bt: case Mnemonic.Bts: case Mnemonic.Btr: case Mnemonic.Btc:
                {
                    int sz = Size(in insn, 0);
                    int bit = (int)Rd(c, m, in insn, 1, sz) & (sz == 32 ? 31 : sz == 16 ? 15 : 7);
                    var d = Dst(c, m, in insn, 0);
                    uint cur = RdD(c, m, in d, sz);
                    uint mask = 1u << bit;
                    c.CF = (cur & mask) != 0;
                    if (insn.Mnemonic != Mnemonic.Bt)
                        Wr(c, m, in d, insn.Mnemonic switch
                        {
                            Mnemonic.Bts => cur | mask,
                            Mnemonic.Btr => cur & ~mask,
                            _ => cur ^ mask,
                        }, sz);
                    break;
                }
                case Mnemonic.Bsf: case Mnemonic.Bsr:
                {
                    int sz = Size(in insn, 1);
                    uint v = Rd(c, m, in insn, 1, sz);
                    c.ZF = v == 0;
                    if (!c.ZF)
                    {
                        uint r = insn.Mnemonic == Mnemonic.Bsf
                            ? (sz == 32 ? c.Bsf32(v) : c.Bsf16((ushort)v))
                            : (sz == 32 ? c.Bsr32(v) : c.Bsr16((ushort)v));
                        Wr(c, m, Dst(c, m, in insn, 0), r, sz);
                    }
                    break;
                }

                // ---------- cadenas ----------
                case Mnemonic.Movsb: case Mnemonic.Movsw: case Mnemonic.Movsd:
                case Mnemonic.Stosb: case Mnemonic.Stosw: case Mnemonic.Stosd:
                case Mnemonic.Lodsb: case Mnemonic.Lodsw: case Mnemonic.Lodsd:
                case Mnemonic.Scasb: case Mnemonic.Scasw: case Mnemonic.Scasd:
                case Mnemonic.Cmpsb: case Mnemonic.Cmpsw: case Mnemonic.Cmpsd:
                    Cadena(c, m, in insn);
                    break;

                // ---------- control de flujo ----------
                case Mnemonic.Jmp:
                    if (insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32)
                    { eip = (uint)insn.NearBranchTarget; continue; }
                    if (insn.Op0Kind == OpKind.Register)
                    { uint t = GetR(c, insn.Op0Register, 32); Salto(lin, t, "jmp reg", lin - eip); eip = t; continue; }
                    if (insn.Op0Kind == OpKind.Memory && insn.MemorySize is MemorySize.UInt32 or MemorySize.DwordOffset)
                    { uint t = m.RdLin32(Ea(c, in insn)); Salto(lin, t, "jmp [m]", lin - eip); eip = t; continue; }
                    if (insn.Op0Kind == OpKind.Memory)
                    {
                        // salto far por puntero 16:32
                        uint a = Ea(c, in insn);
                        uint off = m.RdLin32(a); ushort sel = m.RdLin16(a + 4);
                        Salto(lin, BaseSel(sel) + off, "jmp far");
                        c.CS = sel; eip = off;
                        if (CsSigue32 != null && !CsSigue32(sel))
                        { res.Motivo = Fin.CambioA16; res.Cs = sel; res.EipOffset = eip; res.Eip = lin; goto fin; }
                        continue;
                    }
                    if (insn.Op0Kind is OpKind.FarBranch32 or OpKind.FarBranch16)
                    {
                        c.CS = insn.FarBranchSelector; eip = insn.FarBranch32;
                        if (CsSigue32 != null && !CsSigue32(c.CS))
                        { res.Motivo = Fin.CambioA16; res.Cs = c.CS; res.EipOffset = eip; res.Eip = lin; goto fin; }
                        continue;
                    }
                    throw new NotSupportedException($"interp32: forma de jmp no soportada @ 0x{lin:X8} ({insn})");

                case Mnemonic.Call:
                    if (insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32)
                    { Push(c, m, next, 32); eip = (uint)insn.NearBranchTarget; continue; }
                    if (insn.Op0Kind == OpKind.Register)
                    { uint t = GetR(c, insn.Op0Register, 32); Push(c, m, next, 32); Salto(lin, t, "call reg", lin - eip); eip = t; continue; }
                    if (insn.Op0Kind == OpKind.Memory && insn.MemorySize is MemorySize.UInt32 or MemorySize.DwordOffset)
                    {
                        uint ea = Ea(c, in insn);
                        uint t = m.RdLin32(ea);
                        if (t == 0 && _avisosLinBajo < 4) Console.Error.WriteLine(
                            $"[CALL0] call [0x{ea:X8}]=0 desde 0x{lin:X8} ({insn})");
                        Push(c, m, next, 32); Salto(lin, t, "call [m]", lin - eip); eip = t; continue;
                    }
                    if (insn.Op0Kind == OpKind.Memory)
                    {
                        // llamada far por puntero 16:32. Se apila CS y el retorno como hace el
                        // hardware, para que el `retf` del destino vuelva por su propio pie.
                        uint a = Ea(c, in insn);
                        uint off = m.RdLin32(a); ushort sel = m.RdLin16(a + 4);
                        uint destino = BaseSel(sel) + off;
                        Salto(lin, destino, "call far");
                        if (LogFar)
                            Console.Error.WriteLine($"[FAR32] call desde 0x{lin:X8} por [0x{a:X8}] " +
                                                    $"-> {sel:X4}:{off:X8} = 0x{destino:X8}  " +
                                                    // Los argumentos: un driver con un solo punto de
                                                    // entrada despacha por registro, y sin verlos no
                                                    // se sabe QUE se le esta pidiendo.
                                                    $"EAX={c.EAX:X8} EBX={c.EBX:X8} ECX={c.ECX:X8} EDX={c.EDX:X8} " +
                                                    $"DS={c.DS:X4}(0x{BaseSel(c.DS):X}) ES={c.ES:X4}(0x{BaseSel(c.ES):X}) " +
                                                    $"FS={c.FS:X4}(0x{BaseSel(c.FS):X}) GS={c.GS:X4}(0x{BaseSel(c.GS):X})");
                        Push(c, m, c.CS, 32); Push(c, m, next, 32);
                        c.CS = sel; eip = off;
                        if (CsSigue32 != null && !CsSigue32(sel))
                        { res.Motivo = Fin.CambioA16; res.Cs = sel; res.EipOffset = eip; res.Eip = lin; goto fin; }
                        continue;
                    }
                    if (insn.Op0Kind is OpKind.FarBranch32 or OpKind.FarBranch16)
                    {
                        // Con prefijo 0x66 en un segmento de 32 bits el marco far es de WORDS,
                        // como en el hardware; el retf 16-bit del destino desapila 2+2.
                        int szFar = insn.Op0Kind == OpKind.FarBranch16 ? 16 : 32;
                        Push(c, m, c.CS, szFar); Push(c, m, next, szFar);
                        c.CS = insn.FarBranchSelector; eip = insn.FarBranch32;
                        if (CsSigue32 != null && !CsSigue32(c.CS))
                        { res.Motivo = Fin.CambioA16; res.Cs = c.CS; res.EipOffset = eip; res.Eip = lin; goto fin; }
                        continue;
                    }
                    throw new NotSupportedException($"interp32: forma de call no soportada @ 0x{lin:X8} ({insn})");

                case Mnemonic.Ret:
                {
                    uint r = Pop(c, m, 32);
                    if (insn.OpCount >= 1) c.ESP += insn.Immediate16;
                    Salto(lin, r, "ret", lin - eip); eip = r; continue;
                }
                case Mnemonic.Retf:
                {
                    // `retf` con prefijo 0x66 (Retfw): marco far de WORDS — lo empuja un
                    // `call far` 16:16 (mundo mixto DOS/16M). Desapilar dwords ahí corría la
                    // pila 4 bytes y el control volvía a un CS de basura.
                    int szR = insn.Code is Code.Retfw or Code.Retfw_imm16 ? 16 : 32;
                    uint r = Pop(c, m, szR);
                    ushort sel = (ushort)Pop(c, m, szR);
                    if (insn.OpCount >= 1) c.ESP += insn.Immediate16;
                    Salto(lin, BaseSel(sel) + r, "retf");
                    c.CS = sel; eip = r;
                    if (CsSigue32 != null && !CsSigue32(sel))
                    { res.Motivo = Fin.CambioA16; res.Cs = sel; res.EipOffset = eip; res.Eip = lin; goto fin; }
                    continue;
                }
                case Mnemonic.Iretd:
                {
                    // IRET con NT=1: retorno de una tarea anidada (task gate) — no hay marco,
                    // se conmuta por el backlink del TSS. Misma rama que en Interp (16 bits).
                    if ((c.Msw & 1) != 0 && c.NT)
                    {
                        eip = Interp.IretDeTarea(c, m, next);
                        if (CsSigue32 != null && !CsSigue32(c.CS))
                        { res.Motivo = Fin.CambioA16; res.Cs = c.CS; res.EipOffset = eip; res.Eip = lin; goto fin; }
                        continue;
                    }
                    uint r = Pop(c, m, 32);
                    ushort sel = (ushort)Pop(c, m, 32);
                    c.SetEFlagsWord(Pop(c, m, 32));
                    c.CS = sel; eip = r;
                    if (CsSigue32 != null && !CsSigue32(sel))
                    { res.Motivo = Fin.CambioA16; res.Cs = sel; res.EipOffset = eip; res.Eip = lin; goto fin; }
                    continue;
                }
                case Mnemonic.Loop: if (--c.ECX != 0) { eip = (uint)insn.NearBranchTarget; continue; } break;
                case Mnemonic.Loope: if (--c.ECX != 0 && c.ZF) { eip = (uint)insn.NearBranchTarget; continue; } break;
                case Mnemonic.Loopne: if (--c.ECX != 0 && !c.ZF) { eip = (uint)insn.NearBranchTarget; continue; } break;
                case Mnemonic.Jecxz: if (c.ECX == 0) { eip = (uint)insn.NearBranchTarget; continue; } break;
                case Mnemonic.Jcxz: if (c.CX == 0) { eip = (uint)insn.NearBranchTarget; continue; } break;

                case Mnemonic.Int3:   // 0xCC vectors through the IDT like any int (a DOS/4GW Pro game
                case Mnemonic.Int1:   // ships an `int 3` in its own code; the kernel serves it)
                case Mnemonic.Int:
                {
                    byte no = insn.Mnemonic == Mnemonic.Int3 ? (byte)3
                            : insn.Mnemonic == Mnemonic.Int1 ? (byte)1
                            : (byte)insn.Immediate8;
                    uint tras = next;
                    if (OnInt != null && OnInt(no, c, m, ref tras)) { eip = tras; continue; }
                    res.Motivo = Fin.IntSinServicio; res.IntNo = no; res.Eip = lin; res.EipOffset = eip; res.Cs = c.CS;
                    goto fin;
                }
                case Mnemonic.Hlt: res.Motivo = Fin.Hlt; res.Eip = lin; res.EipOffset = eip; res.Cs = c.CS; goto fin;

                // ---------- banderas y varios ----------
                case Mnemonic.Clc: c.CF = false; break;
                case Mnemonic.Stc: c.CF = true; break;
                case Mnemonic.Cmc: c.CF = !c.CF; break;
                case Mnemonic.Cld: c.DF = false; break;
                case Mnemonic.Std: c.DF = true; break;
                case Mnemonic.Cli: c.IF = false; break;
                case Mnemonic.Sti: c.IF = true; break;
                case Mnemonic.Nop: break;

                // ---------- modo protegido de verdad ----------
                // Estas son las únicas del plan (§2.3) que no son "la variante de 32 bits de algo
                // que ya existe". Aquí no hay GDT ni LDT reales: se contestan contra la tabla de
                // selectores del host DPMI, que es donde vive esa verdad en esta arquitectura.
                case Mnemonic.Lar: case Mnemonic.Lsl:
                {
                    ushort sel = (ushort)Rd(c, m, in insn, 1, 16);
                    if (OnDescriptor != null && OnDescriptor(sel, out uint lim, out uint der, out _))
                    {
                        Wr(c, m, Dst(c, m, in insn, 0), insn.Mnemonic == Mnemonic.Lar ? der : lim, Size(in insn, 0));
                        c.ZF = true;
                    }
                    else c.ZF = false;
                    break;
                }
                case Mnemonic.Verr: case Mnemonic.Verw:
                {
                    ushort sel = (ushort)Rd(c, m, in insn, 0, 16);
                    bool ok = OnDescriptor != null && OnDescriptor(sel, out _, out _, out bool esc)
                              && (insn.Mnemonic == Mnemonic.Verr || esc);
                    c.ZF = ok;
                    break;
                }
                case Mnemonic.Smsw:
                    // Machine Status Word: PE=1 (estamos en modo protegido) + ET (coprocesador).
                    Wr(c, m, Dst(c, m, in insn, 0), 0x0011, Size(in insn, 0));
                    break;
                case Mnemonic.Sldt:
                    Wr(c, m, Dst(c, m, in insn, 0), 0, Size(in insn, 0));   // no modelamos LDT
                    break;
                case Mnemonic.Sgdt: case Mnemonic.Sidt:
                {
                    // Pseudo-descriptor: límite (2) + base (4). No hay tablas de verdad, así que
                    // se entrega una vacía en vez de basura de la pila.
                    uint a = Ea(c, in insn);
                    m.WrLin16(a, 0xFFFF); m.WrLin32(a + 2, 0);
                    break;
                }
                case Mnemonic.Arpl:
                {
                    ushort d = (ushort)Rd(c, m, in insn, 0, 16), s = (ushort)Rd(c, m, in insn, 1, 16);
                    if ((d & 3) < (s & 3)) { Wr(c, m, Dst(c, m, in insn, 0), (uint)((d & ~3) | (s & 3)), 16); c.ZF = true; }
                    else c.ZF = false;
                    break;
                }

                // ---------- puertos ----------
                // Van a la MISMA plataforma que el mundo de 16 bits (VGA, PIT, teclado…): el
                // hardware es uno solo, y un juego de 32 bits que programa el Sequencer a pelo
                // —que es lo que hace éste, medido— tiene que ver los mismos
                // registros que ve un juego de 16 bits.
                case Mnemonic.In:
                {
                    ushort port = insn.Op1Kind == OpKind.Immediate8 ? insn.Immediate8 : c.DX;
                    if (c.Platform != null)
                    {
                        if (insn.Op0Register == Register.EAX)
                            c.EAX = c.Platform.PortIn16(port) | ((uint)c.Platform.PortIn16((ushort)(port + 2)) << 16);
                        else if (insn.Op0Register == Register.AX) c.AX = c.Platform.PortIn16(port);
                        else c.AL = c.Platform.PortIn8(port);
                        if (LogPuertos) Console.Error.WriteLine($"[IO32] in  0x{port:X3} -> {c.AL:X2}   @0x{lin:X8}");
                    }
                    break;
                }
                case Mnemonic.Out:
                {
                    ushort port = insn.Op0Kind == OpKind.Immediate8 ? insn.Immediate8 : c.DX;
                    if (c.Platform != null)
                    {
                        if (LogPuertos) Console.Error.WriteLine($"[IO32] out 0x{port:X3} <- {c.AL:X2}   @0x{lin:X8}");
                        if (insn.Op1Register == Register.EAX)
                        { c.Platform.PortOut16(port, c.AX); c.Platform.PortOut16((ushort)(port + 2), (ushort)(c.EAX >> 16)); }
                        else if (insn.Op1Register == Register.AX) c.Platform.PortOut16(port, c.AX);
                        else c.Platform.PortOut8(port, c.AL);
                    }
                    break;
                }
                case Mnemonic.Outsb: case Mnemonic.Outsw: case Mnemonic.Outsd:
                case Mnemonic.Insb: case Mnemonic.Insw: case Mnemonic.Insd:
                    CadenaIo(c, in insn);
                    break;

                default:
                {
                    // x87: espejo de Interp.ExecFpu sobre la MISMA Fpu, con direccionamiento
                    // lineal. El runtime de Watcom hace `fninit` en su arranque, así que sin esto
                    // no se pasa de la inicialización de la biblioteca de C.
                    string mn0 = insn.Mnemonic.ToString();
                    if (insn.Mnemonic == Mnemonic.Wait || (mn0.Length > 1 && mn0[0] == 'F' && char.IsLower(mn0[1])))
                    { Fpu32(c, m, in insn); break; }
                    if (Cond(c, insn.Mnemonic, out bool tomado))
                    {
                        if (tomado) { eip = (uint)insn.NearBranchTarget; continue; }
                        break;
                    }
                    if (Setcc(c, insn.Mnemonic, out bool sv2))
                    { Wr(c, m, Dst(c, m, in insn, 0), (uint)(sv2 ? 1 : 0), 8); break; }
                    throw new NotSupportedException(
                        $"interp32: opcode no soportado {insn.Mnemonic} @ 0x{lin:X8} ({insn}) hist={Hist()}{Saltos()}");
                }
            }
            }
            catch (NotSupportedException e) when (!e.Message.Contains(" @ 0x"))
            {
                // Los helpers (GetR/SetR/Dst/Rd) lanzan sin coordenadas. Sin la instrucción y el
                // EIP un fallo aquí es inlocalizable, que es justo lo que este proyecto no admite.
                throw new NotSupportedException(
                    $"{e.Message} @ 0x{lin:X8} ({insn}) hist={Hist()}{Saltos()}", e);
            }
            catch (FalloDePaginaException pf)
            {
                // Igual que en el mundo de 16 bits: se entrega la excepción 14 por la IDT del
                // programa y NO se avanza EIP, así que al volver del manejador la instrucción se
                // repite con la página ya traída.
                c.ESP = espAntesPf;   // fault semantics: the instruction restarts from scratch
                if (Interp.Trace) Console.Error.WriteLine(
                    $"[PF32] {pf.Message} @ 0x{lin:X8} ({insn}) hist={Hist()}");
                eip = Interp.EntregaFalloDePagina(c, m, pf, eip, out bool porTarea);
                // Por TAREA el manejador puede ser de 16 bits: cederle el control como en
                // cualquier transferencia far del mundo mixto.
                if (porTarea && CsSigue32 != null && !CsSigue32(c.CS))
                { res.Motivo = Fin.CambioA16; res.Cs = c.CS; res.EipOffset = eip; res.Eip = lin; goto fin; }
                continue;
            }
            if (VigilarFs && c.FS != fsPrevio)
            {
                Console.Error.WriteLine($"[FS] {fsPrevio:X4} -> {c.FS:X4} (base 0x{BaseSel(c.FS):X8}) " +
                                        $"en 0x{lin:X8} ({insn})");
                fsPrevio = c.FS;
            }
            // GS va con la MISMA palanca, y por el mismo motivo que FS: es un segmento que el
            // programa usa para una ventana de datos propia y que nadie mira. Un juego de prueba
            // lee una tabla entera por GS — si GS no lleva su base, lee ceros y dibuja mal sin
            // que falle nada más. La base va en el aviso: el selector solo no dice si apunta bien.
            if (VigilarFs && c.GS != gsPrevio)
            {
                Console.Error.WriteLine($"[GS] {gsPrevio:X4} -> {c.GS:X4} (base 0x{BaseSel(c.GS):X8}) " +
                                        $"en 0x{lin:X8} ({insn})");
                gsPrevio = c.GS;
            }
            if (LogFuera && Memory.EscriturasPerdidas != _perdidasPrevias)
            {
                // Una escritura fuera del espacio se descarta (Memory.WrLin8) y sólo se sabe la
                // dirección: para arreglarla hace falta QUIÉN y CON QUÉ SELECTOR, porque una base
                // mal leída de un descriptor se ve exactamente igual que un puntero corrupto.
                _perdidasPrevias = Memory.EscriturasPerdidas;
                if (_avisosFuera++ < 12)
                    Console.Error.WriteLine(
                        $"[FUERA32] 0x{lin:X8} ({insn}) CS={c.CS:X4}(0x{BaseSel(c.CS):X8}) " +
                        $"DS={c.DS:X4}(0x{BaseSel(c.DS):X8}) ES={c.ES:X4}(0x{BaseSel(c.ES):X8}) " +
                        $"SS={c.SS:X4}(0x{BaseSel(c.SS):X8}) EAX={c.EAX:X8} EBX={c.EBX:X8} " +
                        $"ECX={c.ECX:X8} EDX={c.EDX:X8} ESI={c.ESI:X8} EDI={c.EDI:X8} EBP={c.EBP:X8}");
            }
            eip = next;
        }
        res.Motivo = Fin.Presupuesto; res.Eip = BaseSel(c.CS) + eip; res.EipOffset = eip; res.Cs = c.CS;
    fin:
        res.Instrucciones = n;
        res.Historial = Historial();
        return res;
    }

    static uint[] Historial()
    {
        var h = new uint[HistLen];
        for (int i = 0; i < HistLen; i++) h[i] = _hist[(_histIdx - 1 - i) & (int)(HistLen - 1)];
        return h;
    }

    static string Hist()
    {
        var sb = new System.Text.StringBuilder();
        // El anillo entero, no 16: cuando el flujo se va a memoria vacia hay que ver DONDE
        // empezo el paseo, y con 16 entradas solo se ve el paseo.
        // Los tramos con paso CONSTANTE se colapsan a `inicio..fin (xN paso)`: un paseo por
        // memoria a ceros son cientos de `add [eax],al` de dos bytes, y listarlos uno a uno
        // llenaba la ventana entera sin dejar ver de DÓNDE venía el salto, que es el único dato
        // que sirve.
        var h = Historial();
        for (int i = 0; i < h.Length; )
        {
            int j = i + 1;
            if (j < h.Length)
            {
                long paso = (long)h[i] - h[j];
                while (j + 1 < h.Length && (long)h[j] - h[j + 1] == paso && paso != 0) j++;
            }
            if (sb.Length > 0) sb.Append('<');
            if (j - i >= 3) sb.Append($"{h[i]:X8}..{h[j]:X8}(x{j - i + 1})");
            else { sb.Append($"{h[i]:X8}"); j = i; }
            i = j + 1;
        }
        return sb.ToString();
    }

    // ================= operandos =================

    readonly struct Op
    {
        public readonly bool IsMem;
        public readonly Register Reg;
        public readonly uint Addr;
        public Op(Register r) { IsMem = false; Reg = r; Addr = 0; }
        public Op(uint a) { IsMem = true; Reg = Register.None; Addr = a; }
    }

    static Op Dst(Cpu c, Memory m, in Instruction insn, int i) => insn.GetOpKind(i) switch
    {
        OpKind.Register => new Op(insn.GetOpRegister(i)),
        OpKind.Memory => new Op(Ea(c, in insn)),
        var k => throw new NotSupportedException($"interp32: destino {k} no soportado ({insn.Mnemonic})"),
    };

    static uint RdD(Cpu c, Memory m, in Op o, int size) => o.IsMem
        ? (size == 32 ? m.RdLin32(o.Addr) : size == 16 ? m.RdLin16(o.Addr) : m.RdLin8(o.Addr))
        : GetR(c, o.Reg, size);

    static void Wr(Cpu c, Memory m, in Op o, uint v, int size)
    {
        if (o.IsMem)
        {
            if (size == 32) m.WrLin32(o.Addr, v);
            else if (size == 16) m.WrLin16(o.Addr, (ushort)v);
            else m.WrLin8(o.Addr, (byte)v);
        }
        else SetR(c, o.Reg, v, size);
    }

    static uint Rd(Cpu c, Memory m, in Instruction insn, int i, int size) => insn.GetOpKind(i) switch
    {
        OpKind.Register => GetR(c, insn.GetOpRegister(i), size),
        OpKind.Immediate8 => insn.Immediate8,
        OpKind.Immediate16 => insn.Immediate16,
        OpKind.Immediate32 => insn.Immediate32,
        OpKind.Immediate8to16 => (uint)(short)insn.Immediate8to16,
        OpKind.Immediate8to32 => (uint)insn.Immediate8to32,
        OpKind.Memory => size == 32 ? m.RdLin32(Ea(c, in insn))
                       : size == 16 ? m.RdLin16(Ea(c, in insn))
                       : m.RdLin8(Ea(c, in insn)),
        var k => throw new NotSupportedException($"interp32: fuente {k} no soportada ({insn.Mnemonic})"),
    };

    /// <summary>Dirección efectiva LINEAL = base del segmento efectivo + offset.
    ///
    /// La base NO es siempre cero, aunque el modelo sea plano. El juego se fabrica selectores con
    /// base propia por DPMI (`INT 31h AX=0007`) y luego direcciona con offsets pequeños contra
    /// ellos: se midió cargando el driver de sonido, donde puso `DS` a un selector con base
    /// `0x90000` y leyó con `EDX=0`. Ignorar la base mandaba esa lectura a la dirección 0 y la
    /// llamada al driver acababa ejecutando memoria vacía. Con todas las bases a cero —el caso
    /// normal— esto suma 0 y no cambia nada.</summary>
    static uint Ea(Cpu c, in Instruction insn)
        => EaSinBase(c, in insn) + BaseSel(SegmentoEfectivo(c, in insn));

    /// <summary>La dirección efectiva SIN la base del selector: base + índice×escala +
    /// desplazamiento, que es lo que vale un puntero dentro del segmento. Es lo que devuelve
    /// <c>LEA</c>; para acceder a memoria hace falta además la base (ver <see cref="Ea"/>).</summary>
    static uint EaSinBase(Cpu c, in Instruction insn)
    {
        uint ea = 0;
        if (insn.MemoryBase != Register.None) ea += GetR(c, insn.MemoryBase, 32);
        if (insn.MemoryIndex != Register.None) ea += GetR(c, insn.MemoryIndex, 32) * (uint)insn.MemoryIndexScale;
        return ea + (uint)insn.MemoryDisplacement64;
    }

    /// <summary>Selector con el que se direcciona: el prefijo si lo hay; si no, SS para lo que se
    /// basa en EBP/ESP y DS para el resto — la misma regla que el mundo de 16 bits.</summary>
    static ushort SegmentoEfectivo(Cpu c, in Instruction insn)
    {
        if (insn.SegmentPrefix != Register.None)
            return insn.SegmentPrefix switch
            {
                Register.ES => c.ES, Register.CS => c.CS, Register.SS => c.SS,
                Register.FS => c.FS, Register.GS => c.GS, _ => c.DS,
            };
        return insn.MemoryBase is Register.EBP or Register.ESP or Register.BP or Register.SP ? c.SS : c.DS;
    }

    static int Size(in Instruction insn, int i)
    {
        var k = insn.GetOpKind(i);
        if (k == OpKind.Register) return RegSize(insn.GetOpRegister(i));
        if (k == OpKind.Memory) return MemBits(insn.MemorySize);
        int otro = i == 0 ? 1 : 0;
        if (otro < insn.OpCount)
        {
            var ko = insn.GetOpKind(otro);
            if (ko == OpKind.Register) return RegSize(insn.GetOpRegister(otro));
            if (ko == OpKind.Memory) return MemBits(insn.MemorySize);
        }
        return 32;
    }

    static int SrcSize(in Instruction insn) => insn.GetOpKind(1) == OpKind.Register
        ? RegSize(insn.Op1Register)
        : (insn.MemorySize is MemorySize.UInt8 or MemorySize.Int8 ? 8 : 16);

    static bool EsSegmento(Register r) =>
        r is Register.ES or Register.CS or Register.SS or Register.DS or Register.FS or Register.GS;

    /// <summary>Ancho de la palabra que push/pop mueven en la PILA. En el mundo plano el tamaño
    /// por defecto es 32 (el descriptor de pila tiene el bit B puesto).
    ///
    /// El caso que muerde: <c>push ds</c>. El registro es de 16 bits, pero el 386 en modo de 32
    /// mueve <b>4 bytes de pila</b> igual (los dos altos quedan indefinidos). Tratarlo como 2
    /// desalinea todo lo que el llamador tenga por encima — y el arranque de Watcom lee sus
    /// argumentos con <c>[esp+N]</c> justo después de un <c>push ds</c>, así que la función de DOS
    /// que pedía salía leída de dos mitades de cosas distintas. El ancho lo decide el prefijo
    /// 0x66, que se mira en el propio byte de la instrucción en vez de deducirlo.</summary>
    /// <param name="lin">Direccion LINEAL de la instruccion (base de CS + EIP). Tiene que ser la
    /// lineal, no el EIP: el prefijo se lee de la memoria, y con codigo cargado en un selector con
    /// base propia el offset apunta a otro sitio. Leerlo con el EIP hacia que `pop es` se tomara
    /// por uno de 16 bits, desapilara 2 bytes en vez de 4 y descuadrara la pila del llamador.</param>
    static int TamPila(Cpu c, Memory m, in Instruction insn, uint lin)
    {
        if (insn.Op0Kind == OpKind.Register && EsSegmento(insn.Op0Register))
            return m.RdLin8(lin) == 0x66 ? 16 : 32;
        return insn.Op0Kind switch
        {
            OpKind.Register => RegSize(insn.Op0Register),
            OpKind.Memory => MemBits(insn.MemorySize),
            OpKind.Immediate16 or OpKind.Immediate8to16 => 16,
            _ => 32,
        };
    }

    static int MemBits(MemorySize ms) => ms switch
    {
        MemorySize.UInt8 or MemorySize.Int8 => 8,
        MemorySize.UInt16 or MemorySize.Int16 or MemorySize.WordOffset => 16,
        _ => 32,
    };

    static int RegSize(Register r) => r switch
    {
        Register.AL or Register.BL or Register.CL or Register.DL or
        Register.AH or Register.BH or Register.CH or Register.DH => 8,
        Register.AX or Register.BX or Register.CX or Register.DX or
        Register.SP or Register.BP or Register.SI or Register.DI or
        Register.ES or Register.CS or Register.SS or Register.DS or Register.FS or Register.GS => 16,
        _ => 32,
    };

    static uint GetR(Cpu c, Register r, int size) => size switch
    {
        8 => r switch
        {
            Register.AL => c.AL, Register.BL => c.BL, Register.CL => c.CL, Register.DL => c.DL,
            Register.AH => c.AH, Register.BH => c.BH, Register.CH => c.CH, Register.DH => c.DH,
            _ => throw new NotSupportedException($"interp32: registro8 {r}"),
        },
        16 => r switch
        {
            Register.AX => c.AX, Register.BX => c.BX, Register.CX => c.CX, Register.DX => c.DX,
            Register.SP => (ushort)c.ESP, Register.BP => (ushort)c.EBP,
            Register.SI => (ushort)c.ESI, Register.DI => (ushort)c.EDI,
            Register.ES => c.ES, Register.CS => c.CS, Register.SS => c.SS,
            Register.DS => c.DS, Register.FS => c.FS, Register.GS => c.GS,
            // Formas mixtas legítimas del x86: `mov es, eax` codifica un origen de 32 bits para
            // un destino de 16 y usa la MITAD BAJA. Truncar aquí es la semántica, no un parche.
            Register.EAX => c.AX, Register.EBX => c.BX, Register.ECX => c.CX, Register.EDX => c.DX,
            Register.ESP => (ushort)c.ESP, Register.EBP => (ushort)c.EBP,
            Register.ESI => (ushort)c.ESI, Register.EDI => (ushort)c.EDI,
            _ => throw new NotSupportedException($"interp32: registro16 {r}"),
        },
        _ => r switch
        {
            Register.EAX => c.EAX, Register.EBX => c.EBX, Register.ECX => c.ECX, Register.EDX => c.EDX,
            Register.ESP => c.ESP, Register.EBP => c.EBP, Register.ESI => c.ESI, Register.EDI => c.EDI,
            // Formas mixtas: extensión con cero. SOLO si el tamaño natural es otro — con el
            // mismo tamaño esto recursaba sin fondo (un registro de sistema no contemplado
            // mataba el proceso entero con StackOverflow en vez de decir CUÁL era).
            _ => RegSize(r) != 32 ? GetR(c, r, RegSize(r))
                : throw new NotSupportedException($"interp32: registro32 {r}"),
        },
    };

    static void SetR(Cpu c, Register r, uint v, int size)
    {
        if (size == 8)
        {
            switch (r)
            {
                case Register.AL: c.AL = (byte)v; return; case Register.BL: c.BL = (byte)v; return;
                case Register.CL: c.CL = (byte)v; return; case Register.DL: c.DL = (byte)v; return;
                case Register.AH: c.AH = (byte)v; return; case Register.BH: c.BH = (byte)v; return;
                case Register.CH: c.CH = (byte)v; return; case Register.DH: c.DH = (byte)v; return;
            }
            throw new NotSupportedException($"interp32: registro8 {r}");
        }
        if (size == 16)
        {
            switch (r)
            {
                case Register.AX: c.AX = (ushort)v; return; case Register.BX: c.BX = (ushort)v; return;
                case Register.CX: c.CX = (ushort)v; return; case Register.DX: c.DX = (ushort)v; return;
                case Register.SP: c.SP = (ushort)v; return; case Register.BP: c.BP = (ushort)v; return;
                case Register.SI: c.SI = (ushort)v; return; case Register.DI: c.DI = (ushort)v; return;
                case Register.ES: c.ES = (ushort)v; return; case Register.SS: c.SS = (ushort)v; return;
                case Register.DS: c.DS = (ushort)v; return; case Register.FS: c.FS = (ushort)v; return;
                case Register.GS: c.GS = (ushort)v; return; case Register.CS: c.CS = (ushort)v; return;
            }
            throw new NotSupportedException($"interp32: registro16 {r}");
        }
        switch (r)
        {
            case Register.EAX: c.EAX = v; return; case Register.EBX: c.EBX = v; return;
            case Register.ECX: c.ECX = v; return; case Register.EDX: c.EDX = v; return;
            case Register.ESP: c.ESP = v; return; case Register.EBP: c.EBP = v; return;
            case Register.ESI: c.ESI = v; return; case Register.EDI: c.EDI = v; return;
        }
        SetR(c, r, v, RegSize(r));
    }

    // ================= familias =================

    static uint Alu(Cpu c, int size, uint a, uint b, char op) => size switch
    {
        32 => op switch
        {
            'a' => c.Add32(a, b), 'A' => c.Adc32(a, b), 's' => c.Sub32(a, b), 'S' => c.Sbb32(a, b),
            '&' => c.And32(a, b), '|' => c.Or32(a, b), _ => c.Xor32(a, b),
        },
        16 => op switch
        {
            'a' => c.Add16((ushort)a, (ushort)b), 'A' => c.Adc16((ushort)a, (ushort)b),
            's' => c.Sub16((ushort)a, (ushort)b), 'S' => c.Sbb16((ushort)a, (ushort)b),
            '&' => c.And16((ushort)a, (ushort)b), '|' => c.Or16((ushort)a, (ushort)b),
            _ => c.Xor16((ushort)a, (ushort)b),
        },
        _ => op switch
        {
            'a' => c.Add8((byte)a, (byte)b), 'A' => c.Adc8((byte)a, (byte)b),
            's' => c.Sub8((byte)a, (byte)b), 'S' => c.Sbb8((byte)a, (byte)b),
            '&' => c.And8((byte)a, (byte)b), '|' => c.Or8((byte)a, (byte)b),
            _ => c.Xor8((byte)a, (byte)b),
        },
    };

    static void Bin(Cpu c, Memory m, in Instruction insn, Func<uint, uint, int, uint> f)
    {
        int sz = Size(in insn, 0);
        var d = Dst(c, m, in insn, 0);
        Wr(c, m, in d, f(RdD(c, m, in d, sz), Rd(c, m, in insn, 1, sz), sz), sz);
    }

    static void Un(Cpu c, Memory m, in Instruction insn, Func<uint, int, uint> f)
    {
        int sz = Size(in insn, 0);
        var d = Dst(c, m, in insn, 0);
        Wr(c, m, in d, f(RdD(c, m, in d, sz), sz), sz);
    }

    static void Sh(Cpu c, Memory m, in Instruction insn, Func<uint, int, int, uint> f)
    {
        int k = insn.GetOpKind(1) switch
        {
            OpKind.Immediate8 => insn.Immediate8,
            OpKind.Register when insn.Op1Register == Register.CL => c.CL,
            _ => throw new NotSupportedException($"interp32: cuenta de shift no imm8/CL ({insn.Mnemonic})"),
        };
        int sz = Size(in insn, 0);
        var d = Dst(c, m, in insn, 0);
        Wr(c, m, in d, f(RdD(c, m, in d, sz), k, sz), sz);
    }

    // La pila también va por su selector (SS). Hoy su base es 0 y esto suma cero, pero dejarlo
    // implícito sería una bomba de relojería el día que un juego mueva SS a un selector con base.
    static void Push(Cpu c, Memory m, uint v, int size)
    {
        // Write first, commit ESP after: a push whose write page-faults must leave ESP intact
        // so the instruction restarts cleanly (see the same rule in Interp.Push16/32).
        if (size == 32) { m.WrLin32(BaseSel(c.SS) + c.ESP - 4, v); c.ESP -= 4; }
        else { m.WrLin16(BaseSel(c.SS) + c.ESP - 2, (ushort)v); c.ESP -= 2; }
    }

    static uint Pop(Cpu c, Memory m, int size)
    {
        if (size == 32) { uint v = m.RdLin32(BaseSel(c.SS) + c.ESP); c.ESP += 4; return v; }
        ushort w = m.RdLin16(BaseSel(c.SS) + c.ESP); c.ESP += 2; return w;
    }

    /// <summary>movs/stos/lods/scas/cmps. Direccionamiento de 32 bits (ESI/EDI/ECX), que es el
    /// del mundo plano; con prefijo 0x67 el 386 usaría SI/DI/CX, pero eso no aparece aquí.</summary>
    static void Cadena(Cpu c, Memory m, in Instruction insn)
    {
        string mn = insn.Mnemonic.ToString();
        int size = mn.EndsWith("b") ? 8 : mn.EndsWith("w") ? 16 : 32;
        char kind = char.ToLowerInvariant(mn[0]) == 'm' ? 'm'
                  : mn.StartsWith("Sto") ? 's' : mn.StartsWith("Lod") ? 'l'
                  : mn.StartsWith("Sca") ? 'z' : 'c';
        int n = size / 8;
        int paso = c.DF ? -n : n;
        bool rep = insn.HasRepPrefix || insn.HasRepePrefix;
        bool repne = insn.HasRepnePrefix;
        // Origen por DS (o el prefijo que haya) y destino SIEMPRE por ES — igual que el hardware.
        uint bo = BaseSel(insn.SegmentPrefix != Register.None ? SegmentoEfectivo(c, in insn) : c.DS);
        uint bd = BaseSel(c.ES);
        // Si una cadena ya ha perdido escrituras, decir CON QUÉ SELECTOR: una base equivocada
        // manda el bloque entero a un sitio que no existe y el juego se ve como si no dibujara.
        if (Memory.EscriturasPerdidas > 0 && _avisoSelectorCadena++ < 4)
            Console.Error.WriteLine($"[FUERA] cadena '{kind}' ES={c.ES:X4}(base 0x{bd:X}) EDI=0x{c.EDI:X8}" +
                                    $" DS={c.DS:X4}(base 0x{bo:X}) ESI=0x{c.ESI:X8}");

        void Body()
        {
            uint s = bo + c.ESI, d = bd + c.EDI;
            switch (kind)
            {
                case 'm':
                    // Procedencia encadenada: si el byte fuente está etiquetado (lo escribió
                    // alguien en la RAM alta), el destino hereda ESA identidad — el caso es el
                    // blitter que sube un buffer de composición a la VRAM. Un id por elemento
                    // (el del primer byte): un movsd que cruce dos rutinas es un caso de un
                    // píxel que no justifica cuadruplicar las consultas.
                    if (_procPixel) Platform.VideoState.IdHeredado = m.ProcDeLin(s);
                    if (size == 32) m.WrLin32(d, m.RdLin32(s));
                    else if (size == 16) m.WrLin16(d, m.RdLin16(s));
                    else m.WrLin8(d, m.RdLin8(s));
                    c.ESI = (uint)(c.ESI + paso); c.EDI = (uint)(c.EDI + paso);
                    break;
                case 's':
                    if (size == 32) m.WrLin32(d, c.EAX);
                    else if (size == 16) m.WrLin16(d, c.AX);
                    else m.WrLin8(d, c.AL);
                    c.EDI = (uint)(c.EDI + paso);
                    break;
                case 'l':
                    if (size == 32) c.EAX = m.RdLin32(s);
                    else if (size == 16) c.AX = m.RdLin16(s);
                    else c.AL = m.RdLin8(s);
                    c.ESI = (uint)(c.ESI + paso);
                    break;
                case 'z':
                    if (size == 32) c.Cmp32(c.EAX, m.RdLin32(d));
                    else if (size == 16) c.Cmp16(c.AX, m.RdLin16(d));
                    else c.Cmp8(c.AL, m.RdLin8(d));
                    c.EDI = (uint)(c.EDI + paso);
                    break;
                default:
                    if (size == 32) c.Cmp32(m.RdLin32(s), m.RdLin32(d));
                    else if (size == 16) c.Cmp16(m.RdLin16(s), m.RdLin16(d));
                    else c.Cmp8(m.RdLin8(s), m.RdLin8(d));
                    c.ESI = (uint)(c.ESI + paso); c.EDI = (uint)(c.EDI + paso);
                    break;
            }
        }

        if (!rep && !repne) { Body(); Platform.VideoState.IdHeredado = 0; return; }
        if (kind is 'z' or 'c')
        {
            while (c.ECX != 0) { c.ECX--; Body(); if (repne ? c.ZF : !c.ZF) break; }
            return;
        }
        while (c.ECX != 0) { c.ECX--; Body(); }
        // La herencia muere con la instrucción: un mov suelto posterior no debe heredar nada.
        Platform.VideoState.IdHeredado = 0;
    }

    // ================= x87 =================
    // Espejo de Interp.ExecFpu sobre la MISMA <see cref="Fpu"/> (la de software, ya validada por
    // los corpus del mundo de 16 bits). Lo único que cambia es que los operandos de memoria son
    // direcciones lineales en vez de seg:off.

    static void Fpu32(Cpu c, Memory m, in Instruction insn)
    {
        var f = c.Fpu;
        switch (insn.Mnemonic)
        {
            case Mnemonic.Wait: case Mnemonic.Fninit: case Mnemonic.Fldcw: case Mnemonic.Fnclex:
            case Mnemonic.Fnop: case Mnemonic.Fnsave: case Mnemonic.Frstor:
            case Mnemonic.Fnstenv: case Mnemonic.Fldenv:
                return;
            case Mnemonic.Fnstcw: m.WrLin16(Ea(c, in insn), 0x037F); return;
            case Mnemonic.Fnstsw:
                if (insn.Op0Kind == OpKind.Register) c.AX = f.StatusWord();
                else m.WrLin16(Ea(c, in insn), f.StatusWord());
                return;

            case Mnemonic.Fld:
                if (insn.Op0Kind == OpKind.Register) f.Push(f.St(St(insn.Op0Register)));
                else f.Push(FpuMem(c, m, in insn));
                return;
            case Mnemonic.Fild: f.Push(FpuMemInt(c, m, in insn)); return;
            case Mnemonic.Fld1: f.Push(1.0); return;
            case Mnemonic.Fldz: f.Push(0.0); return;
            case Mnemonic.Fldpi: f.Push(Math.PI); return;
            case Mnemonic.Fldl2t: f.Push(Math.Log2(10.0)); return;
            case Mnemonic.Fldl2e: f.Push(Math.Log2(Math.E)); return;
            case Mnemonic.Fldln2: f.Push(Math.Log(2.0)); return;
            case Mnemonic.Fldlg2: f.Push(Math.Log10(2.0)); return;

            case Mnemonic.Fst: FpuStore(c, m, in insn, false); return;
            case Mnemonic.Fstp: FpuStore(c, m, in insn, true); return;
            case Mnemonic.Fist: FpuStore(c, m, in insn, false); return;
            case Mnemonic.Fistp: case Mnemonic.Fisttp: FpuStore(c, m, in insn, true); return;

            case Mnemonic.Fchs: f.SetSt(0, -f.St(0)); return;
            case Mnemonic.Fabs: f.SetSt(0, Math.Abs(f.St(0))); return;
            case Mnemonic.Fsqrt: f.SetSt(0, Math.Sqrt(f.St(0))); return;
            case Mnemonic.Frndint: f.SetSt(0, Math.Round(f.St(0), MidpointRounding.ToEven)); return;
            case Mnemonic.Fxch:
            {
                int i = insn.OpCount > 0 && insn.Op0Kind == OpKind.Register ? St(insn.Op0Register) : 1;
                double t = f.St(0); f.SetSt(0, f.St(i)); f.SetSt(i, t);
                return;
            }
            case Mnemonic.Fpatan: { double r = Math.Atan2(f.St(1), f.St(0)); f.Pop(); f.SetSt(0, r); return; }
            case Mnemonic.Fptan: f.SetSt(0, Math.Tan(f.St(0))); f.Push(1.0); return;
            case Mnemonic.Fyl2x: { double r = f.St(1) * Math.Log2(f.St(0)); f.Pop(); f.SetSt(0, r); return; }
            case Mnemonic.Fyl2xp1: { double r = f.St(1) * Math.Log2(f.St(0) + 1.0); f.Pop(); f.SetSt(0, r); return; }
            case Mnemonic.F2xm1: f.SetSt(0, Math.Pow(2.0, f.St(0)) - 1.0); return;
            case Mnemonic.Fscale: f.SetSt(0, f.St(0) * Math.Pow(2.0, Math.Truncate(f.St(1)))); return;
            case Mnemonic.Fprem: case Mnemonic.Fprem1: f.SetSt(0, Math.IEEERemainder(f.St(0), f.St(1))); return;
            case Mnemonic.Fcos: f.SetSt(0, Math.Cos(f.St(0))); return;
            case Mnemonic.Fsin: f.SetSt(0, Math.Sin(f.St(0))); return;
            case Mnemonic.Fsincos: { double v = f.St(0); f.SetSt(0, Math.Sin(v)); f.Push(Math.Cos(v)); return; }
            case Mnemonic.Ftst: f.Compare(f.St(0), 0.0); return;
            case Mnemonic.Fxam: f.Examine(); return;
            case Mnemonic.Fdecstp: f.DecTop(); return;
            case Mnemonic.Fincstp: f.IncTop(); return;
            case Mnemonic.Ffree:
                f.Free(insn.OpCount > 0 && insn.Op0Kind == OpKind.Register ? St(insn.Op0Register) : 0);
                return;

            case Mnemonic.Fcom: case Mnemonic.Fucom: FpuCmp(c, m, in insn, 0); return;
            case Mnemonic.Fcomp: case Mnemonic.Fucomp: FpuCmp(c, m, in insn, 1); return;
            case Mnemonic.Fcompp: case Mnemonic.Fucompp: FpuCmp(c, m, in insn, 2); return;
            case Mnemonic.Ficom: f.Compare(f.St(0), FpuMemInt(c, m, in insn)); return;
            case Mnemonic.Ficomp: f.Compare(f.St(0), FpuMemInt(c, m, in insn)); f.Pop(); return;

            case Mnemonic.Fadd: FpuAr(c, m, in insn, '+', false, false); return;
            case Mnemonic.Faddp: FpuAr(c, m, in insn, '+', false, true); return;
            case Mnemonic.Fiadd: FpuIAr(c, m, in insn, '+', false); return;
            case Mnemonic.Fsub: FpuAr(c, m, in insn, '-', false, false); return;
            case Mnemonic.Fsubp: FpuAr(c, m, in insn, '-', false, true); return;
            case Mnemonic.Fsubr: FpuAr(c, m, in insn, '-', true, false); return;
            case Mnemonic.Fsubrp: FpuAr(c, m, in insn, '-', true, true); return;
            case Mnemonic.Fisub: FpuIAr(c, m, in insn, '-', false); return;
            case Mnemonic.Fisubr: FpuIAr(c, m, in insn, '-', true); return;
            case Mnemonic.Fmul: FpuAr(c, m, in insn, '*', false, false); return;
            case Mnemonic.Fmulp: FpuAr(c, m, in insn, '*', false, true); return;
            case Mnemonic.Fimul: FpuIAr(c, m, in insn, '*', false); return;
            case Mnemonic.Fdiv: FpuAr(c, m, in insn, '/', false, false); return;
            case Mnemonic.Fdivp: FpuAr(c, m, in insn, '/', false, true); return;
            case Mnemonic.Fdivr: FpuAr(c, m, in insn, '/', true, false); return;
            case Mnemonic.Fdivrp: FpuAr(c, m, in insn, '/', true, true); return;
            case Mnemonic.Fidiv: FpuIAr(c, m, in insn, '/', false); return;
            case Mnemonic.Fidivr: FpuIAr(c, m, in insn, '/', true); return;

            default: throw new NotSupportedException($"interp32: x87 no soportado {insn.Mnemonic}");
        }
    }

    static int St(Register r) => r >= Register.ST0 && r <= Register.ST7 ? r - Register.ST0 : 0;

    static double FpuMem(Cpu c, Memory m, in Instruction insn)
    {
        uint a = Ea(c, in insn);
        return insn.MemorySize switch
        {
            MemorySize.Float32 => BitConverter.UInt32BitsToSingle(m.RdLin32(a)),
            MemorySize.Float64 => BitConverter.UInt64BitsToDouble(m.RdLin32(a) | ((ulong)m.RdLin32(a + 4) << 32)),
            MemorySize.Float80 => Fpu.FromF80(m.RdLin32(a) | ((ulong)m.RdLin32(a + 4) << 32), m.RdLin16(a + 8)),
            MemorySize.Int16 => (short)m.RdLin16(a),
            MemorySize.Int32 => (int)m.RdLin32(a),
            _ => throw new NotSupportedException($"interp32: x87 operando mem {insn.MemorySize} ({insn.Mnemonic})"),
        };
    }

    static double FpuMemInt(Cpu c, Memory m, in Instruction insn)
    {
        uint a = Ea(c, in insn);
        return insn.MemorySize switch
        {
            MemorySize.Int16 => (short)m.RdLin16(a),
            MemorySize.Int32 => (int)m.RdLin32(a),
            MemorySize.Int64 => (long)(m.RdLin32(a) | ((ulong)m.RdLin32(a + 4) << 32)),
            _ => throw new NotSupportedException($"interp32: x87 int mem {insn.MemorySize} ({insn.Mnemonic})"),
        };
    }

    static void FpuStore(Cpu c, Memory m, in Instruction insn, bool pop)
    {
        var f = c.Fpu;
        if (insn.Op0Kind == OpKind.Register) f.SetSt(St(insn.Op0Register), f.St(0));
        else
        {
            uint a = Ea(c, in insn);
            double v = f.St(0);
            switch (insn.MemorySize)
            {
                case MemorySize.Float32: m.WrLin32(a, BitConverter.SingleToUInt32Bits((float)v)); break;
                case MemorySize.Float64:
                {
                    ulong b = BitConverter.DoubleToUInt64Bits(v);
                    m.WrLin32(a, (uint)b); m.WrLin32(a + 4, (uint)(b >> 32));
                    break;
                }
                case MemorySize.Float80:
                {
                    var (mant, se) = Fpu.ToF80(v);
                    m.WrLin32(a, (uint)mant); m.WrLin32(a + 4, (uint)(mant >> 32)); m.WrLin16(a + 8, se);
                    break;
                }
                case MemorySize.Int16: m.WrLin16(a, (ushort)Fpu.ToInt(v)); break;
                case MemorySize.Int32: m.WrLin32(a, (uint)Fpu.ToInt(v)); break;
                case MemorySize.Int64:
                {
                    long i64 = (long)Math.Round(v, MidpointRounding.ToEven);
                    m.WrLin32(a, (uint)i64); m.WrLin32(a + 4, (uint)((ulong)i64 >> 32));
                    break;
                }
                default: throw new NotSupportedException($"interp32: x87 store {insn.MemorySize} ({insn.Mnemonic})");
            }
        }
        if (pop) f.Pop();
    }

    static void FpuCmp(Cpu c, Memory m, in Instruction insn, int pops)
    {
        var f = c.Fpu;
        double v = insn.OpCount == 0 ? f.St(1)
            : insn.Op0Kind == OpKind.Register ? f.St(St(insn.Op0Register))
            : FpuMem(c, m, in insn);
        f.Compare(f.St(0), v);
        for (int i = 0; i < pops; i++) f.Pop();
    }

    static double Ap(double a, char op, double b) => op switch { '+' => a + b, '-' => a - b, '*' => a * b, _ => a / b };

    static void FpuAr(Cpu c, Memory m, in Instruction insn, char op, bool rev, bool pop)
    {
        var f = c.Fpu;
        if (insn.OpCount <= 1 || insn.Op0Kind == OpKind.Memory)
        {
            double mem = FpuMem(c, m, in insn);
            f.SetSt(0, rev ? Ap(mem, op, f.St(0)) : Ap(f.St(0), op, mem));
            return;
        }
        int d = St(insn.Op0Register), s = St(insn.Op1Register);
        f.SetSt(d, rev ? Ap(f.St(s), op, f.St(d)) : Ap(f.St(d), op, f.St(s)));
        if (pop) f.Pop();
    }

    static void FpuIAr(Cpu c, Memory m, in Instruction insn, char op, bool rev)
    {
        var f = c.Fpu;
        double mem = FpuMemInt(c, m, in insn);
        f.SetSt(0, rev ? Ap(mem, op, f.St(0)) : Ap(f.St(0), op, mem));
    }

    /// <summary>outs/ins con direccionamiento de 32 bits. La paleta se carga así en muchos juegos
    /// (`rep outsb` al 3C9), y tragárselo deja los colores congelados — pasó en un juego de prueba.</summary>
    static void CadenaIo(Cpu c, in Instruction insn)
    {
        string mn = insn.Mnemonic.ToString();
        int size = mn.EndsWith("b") ? 8 : mn.EndsWith("w") ? 16 : 32;
        bool salida = mn.StartsWith("Out");
        int n = size / 8, paso = c.DF ? -n : n;
        var m = Memory.Last!;
        bool rep = insn.HasRepPrefix || insn.HasRepePrefix || insn.HasRepnePrefix;
        uint bo = BaseSel(insn.SegmentPrefix != Register.None ? SegmentoEfectivo(c, in insn) : c.DS);
        uint bd = BaseSel(c.ES);

        void Body()
        {
            if (salida)
            {
                // Origen por DS (o el prefijo): sin sumar la base del selector, un `rep outsb` que
                // carga la paleta lee de memoria no mapeada, recibe 0xFF y deja el DAC ENTERO en
                // (63,63,63) -- pantalla blanca. Se midió exactamente eso.
                uint s = bo + c.ESI;
                if (c.Platform != null)
                {
                    if (size == 8) c.Platform.PortOut8(c.DX, m.RdLin8(s));
                    else c.Platform.PortOut16(c.DX, m.RdLin16(s));
                }
                c.ESI = (uint)(c.ESI + paso);
            }
            else
            {
                uint d = bd + c.EDI;
                if (size == 8) m.WrLin8(d, c.Platform?.PortIn8(c.DX) ?? 0xFF);
                else m.WrLin16(d, c.Platform?.PortIn16(c.DX) ?? 0xFFFF);
                c.EDI = (uint)(c.EDI + paso);
            }
        }
        if (!rep) { Body(); return; }
        while (c.ECX != 0) { c.ECX--; Body(); }
    }

    static bool Cond(Cpu c, Mnemonic m, out bool t)
    {
        switch (m)
        {
            case Mnemonic.Je: t = c.ZF; return true;
            case Mnemonic.Jne: t = !c.ZF; return true;
            case Mnemonic.Jb: t = c.CF; return true;
            case Mnemonic.Jae: t = !c.CF; return true;
            case Mnemonic.Jbe: t = c.CF || c.ZF; return true;
            case Mnemonic.Ja: t = !c.CF && !c.ZF; return true;
            case Mnemonic.Jl: t = c.SF != c.OF; return true;
            case Mnemonic.Jge: t = c.SF == c.OF; return true;
            case Mnemonic.Jle: t = c.ZF || (c.SF != c.OF); return true;
            case Mnemonic.Jg: t = !c.ZF && (c.SF == c.OF); return true;
            case Mnemonic.Js: t = c.SF; return true;
            case Mnemonic.Jns: t = !c.SF; return true;
            case Mnemonic.Jo: t = c.OF; return true;
            case Mnemonic.Jno: t = !c.OF; return true;
            case Mnemonic.Jp: t = c.PF; return true;
            case Mnemonic.Jnp: t = !c.PF; return true;
            default: t = false; return false;
        }
    }

    static bool Setcc(Cpu c, Mnemonic m, out bool v)
    {
        switch (m)
        {
            case Mnemonic.Seta: v = !c.CF && !c.ZF; return true;
            case Mnemonic.Setae: v = !c.CF; return true;
            case Mnemonic.Setb: v = c.CF; return true;
            case Mnemonic.Setbe: v = c.CF || c.ZF; return true;
            case Mnemonic.Sete: v = c.ZF; return true;
            case Mnemonic.Setne: v = !c.ZF; return true;
            case Mnemonic.Setg: v = !c.ZF && (c.SF == c.OF); return true;
            case Mnemonic.Setge: v = c.SF == c.OF; return true;
            case Mnemonic.Setl: v = c.SF != c.OF; return true;
            case Mnemonic.Setle: v = c.ZF || (c.SF != c.OF); return true;
            case Mnemonic.Sets: v = c.SF; return true;
            case Mnemonic.Setns: v = !c.SF; return true;
            case Mnemonic.Seto: v = c.OF; return true;
            case Mnemonic.Setno: v = !c.OF; return true;
            case Mnemonic.Setp: v = c.PF; return true;
            case Mnemonic.Setnp: v = !c.PF; return true;
            default: v = false; return false;
        }
    }
}
