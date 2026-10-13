using PakuPort.Core.Platform;

namespace PakuPort.Core;

/// <summary>
/// Memoria plana real-mode de 1 MiB. Cuando Video != null, el rango A0000-BFFFF se desvía a VRAM
/// unificada (4 planos EGA/VGA) filtrada por GC reg 6 memory map. Modelo Spice86: un solo
/// intercept para 128 KB, VideoState decide si la dirección cae en el rango mapeado.
/// </summary>
public sealed class Memory
{
    public const int Size = 0x100000; // 1 MiB

    public readonly byte[] Raw = new byte[Size];

    /// <summary>Última memoria construida. Solo para diagnóstico: un cuelgue no lanza excepción,
    /// así que sin esto no hay forma de volcar la imagen del proceso colgado (ver PAKU_SAMPLE_PC
    /// + PAKU_FATAL_MEMDUMP, que vuelcan en el ProcessExit).</summary>
    public static Memory? Last;

    public Memory() { Last = this; GenDescriptores++; }

    /// <summary>Modelo de vídeo unificado EGA/VGA. A0000-BFFFF → VideoState.WriteVram/ReadVram.</summary>
    public VideoState? Video;

    public static int Lin(ushort seg, ushort off) => ((seg << 4) + off) & 0xFFFFF;

    /// <summary>MODO PROTEGIDO DE 16 BITS: los registros de segmento dejan de ser párrafos y pasan
    /// a ser SELECTORES, cuya base sale de una tabla de descriptores. Apagado por defecto — con él
    /// apagado no cambia absolutamente nada, que es como corren todos los juegos probados.
    ///
    /// Existe por los juegos con EXTENSOR EMBEBIDO: tras el cambio de modo por DPMI, el
    /// extensor sigue ejecutando código de 16 BITS —guarda sus selectores, pide descriptores con
    /// `INT 31h`— antes de saltar a su mundo de 32. Ese tramo no lo puede correr `Interp32`, que
    /// decodifica a 32, ni el `Interp` de siempre, que direcciona con `seg &lt;&lt; 4`. Con esto lo
    /// corre el de siempre y sólo cambia de dónde sale la base.
    ///
    /// Y las direcciones dejan de recortarse a 20 bits: en modo protegido un selector puede
    /// apuntar por encima del megabyte, que es justo para lo que el programa entró en modo
    /// protegido. Por eso se va por el camino LINEAL, el mismo del mundo plano.</summary>
    public static bool SegmentosSonSelectores
    {
        get => _segmentosSonSelectores;
        set
        {
            _segmentosSonSelectores = value;
            if (value) HuboModoProtegido = true;
        }
    }
    private static bool _segmentosSonSelectores;

    /// <summary>PEGAJOSO: el invitado ha estado en modo protegido alguna vez, o sea que corre un
    /// EXTENSOR. No es lo mismo que <see cref="SegmentosSonSelectores"/>, que dice dónde está
    /// AHORA: un extensor baja a modo real constantemente para reflejar interrupciones, y en ese
    /// tramo los dos valores discrepan. Quien necesita distinguir "juego de modo real puro" de
    /// "juego con extensor que ahora mismo está abajo" tiene que mirar ÉSTE.
    ///
    /// Existe por una regresión medida: la entrega del ratón se reescribió para ir por IRQ12 (que
    /// es lo correcto con extensor, porque sólo él sabe cruzar de mundo) y el despacho directo del
    /// límite de frame se cortó con un `return` incondicional. En un juego de modo real puro ese
    /// despacho es el ÚNICO camino que hay —la IRQ12 sólo se levanta al detectar discrepancia de
    /// mundos, que ahí no ocurre nunca—, así que dos juegos de prueba se quedaron sin ratón:
    /// medido con el mismo arnés, 3.498 y 2.687 callbacks en 60 s pasaron a CERO.</summary>
    public static bool HuboModoProtegido;

    /// <summary>Base lineal de un selector. La pone el host de servicios (FlatServices) al hacer
    /// el cambio de modo; sin ella, comportamiento de modo real.</summary>
    public static Func<ushort, uint>? BaseDelSelector;

    // ---- LA CACHÉ DE DESCRIPTORES DEL 386, en el camino caliente ----
    //
    // Un 386 lee el descriptor UNA vez, al cargar el registro de segmento, y guarda base/límite
    // en una caché invisible; los accesos posteriores NO vuelven a la tabla. PakuPort volvía a
    // ella en CADA acceso a memoria, y el precio se midió en un juego DOS/4GW Pro: 260 M resoluciones de
    // base en 110 M de instrucciones de 16 bits (2,4 por instrucción) de las que 259,99 M leían
    // la GDT del invitado byte a byte, con su traducción de página cada una — 22 traducciones
    // por instrucción y el mundo de 16 bits a 3,4 M instr/s frente a los 19,6 M del de 32.
    //
    // Esto NO es una heurística: mientras la tabla no cambie, memoizar da exactamente lo mismo
    // que releerla. Lo único que hay que hacer bien es la INVALIDACIÓN, y va por dos caminos:
    //   1. El CONTEXTO (lgdt/lldt/cr3/paginación/modo) se compara en el resolutor — así no
    //      depende de haber cazado los seis sitios que lo tocan, presentes y futuros.
    //   2. Las ESCRITURAS dentro de la GDT o la LDT levantan la generación desde aquí: el
    //      kernel de un extensor escribe descriptores en caliente (DOS/4GW reparte selectores
    //      con INT 31h) y la base cacheada dejaría de valer.
    // La generación arranca en 1 para que un array de generaciones recién creado (todo ceros)
    // sea inválido por construcción.
    public static long GenDescriptores = 1;

    /// <summary>Rangos LINEALES de la GDT y de la LDT vigentes; los publica quien resuelve los
    /// descriptores. Una escritura dentro de ellos invalida la caché entera.</summary>
    public static uint GdtIni, GdtFin, LdtIni, LdtFin;

    static void VigilaEscrituraEnTablas(uint a)
    {
        if ((a >= GdtIni && a < GdtFin) || (a >= LdtIni && a < LdtFin)) GenDescriptores++;
    }

    static uint BaseDeSegmento(ushort seg) =>
        BaseDelSelector != null ? BaseDelSelector(seg) : (uint)(seg << 4);

    /// <summary>Dirección lineal de un `seg:off`, honrando el modo. En modo real es
    /// <see cref="Lin"/> de siempre (con su wrap a 20 bits, que es fiel); en modo protegido de 16
    /// bits sale de la base del selector y NO se recorta.</summary>
    public static uint LinDeSegmento(ushort seg, ushort off) =>
        SegmentosSonSelectores ? BaseDeSegmento(seg) + off : (uint)Lin(seg, off);

    public List<(int lin, byte v)>? WriteLog;

    public List<int>? IpLog;
    public void LogIP(int ip) => IpLog?.Add(ip);

    public byte RdU8(ushort seg, ushort off)
    {
        // Modo protegido de 16 bits: el segmento es un selector y la dirección puede pasar del
        // megabyte, así que se va por el camino lineal (que abajo vuelve aquí con el paso a
        // FÍSICO, sin recursión: RdLin8 no reconstruye un seg:off).
        if (SegmentosSonSelectores) return RdLin8(BaseDeSegmento(seg) + off);
        return RdFisico8(Lin(seg, off));
    }

    /// <summary>Lectura por dirección FÍSICA ya resuelta (con el desvío a VRAM). Es el fondo
    /// común de los dos mundos: sin él, el camino lineal y el de seg:off se llamarían en
    /// círculo en cuanto el modo protegido está activo.</summary>
    public byte RdFisico8(int a)
    {
        if (Video != null && a >= 0xA0000 && a < 0xC0000)
        {
            int vramOff = a - Video.MemoryMapBase;
            if ((uint)vramOff < (uint)Video.MemoryMapSize)
                return Video.ReadVram(vramOff);
        }
        byte v = Raw[a];
        // Banco de pruebas (PAKU_CORPUS): lo que una función lee FUERA de su DGROUP es su
        // contexto, y sin él el caso sólo se reproduce con una foto del mundo del mismo instante.
        // El fetch de instrucciones no pasa por aquí (va directo al array), así que esto no se
        // llena con el código. Apagado: un estático y un salto.
        if (CorpusRecorder.Activo) CorpusRecorder.NotaLectura(a, v);
        return v;
    }

    public ushort RdU16(ushort seg, ushort off)
    {
        return (ushort)(RdU8(seg, off) | (RdU8(seg, (ushort)(off + 1)) << 8));
    }

    // PAKU_MEMWATCH=<hex lineal>: depuración — imprime el stack trace C# (cadena de
    // Fn lifteadas) la primera vez que se escribe esa dirección con OTRO valor.
    // Robusto ante var vacía/basura ('' con Convert.ToInt32 tiraba TypeInitializationException
    // y mataba la clase Memory entera ).
    static readonly int _watchLin =
        int.TryParse(Environment.GetEnvironmentVariable("PAKU_MEMWATCH"),
                     System.Globalization.NumberStyles.HexNumber, null, out var _w) ? _w : -1;
    static int _watchHits;
    static readonly bool _watchTodo = Environment.GetEnvironmentVariable("PAKU_MEMWATCH_ALL") == "1";

    /// <summary>Set by Interp.StringOp while a movs/stos runs (PAKU_LOG_BLIT=2): those writes
    /// are already logged as rectangles in blit.log, so the raw-write hook skips them.</summary>
    public static bool SuppressRawBlit;

    public void WrU8(ushort seg, ushort off, byte v)
    {
        if (SegmentosSonSelectores) { WrLin8(BaseDeSegmento(seg) + off, v); return; }
        WrFisico8(Lin(seg, off), seg, off, v);
    }

    /// <summary>Escritura por dirección FÍSICA ya resuelta. `seg`/`off` sólo viajan para los
    /// mensajes de los vigilantes de memoria.</summary>
    public void WrFisico8(int a, ushort seg, ushort off, byte v)
    {
        // Contrapartida de la nota de lectura: una dirección que la función escribe ANTES de
        // leerla no es contexto de entrada — el valor lo produce ella misma.
        if (CorpusRecorder.Activo) CorpusRecorder.NotaEscritura(a, v);
        if (Video != null && a >= 0xA0000 && a < 0xC0000)
        {
            int vramOff = a - Video.MemoryMapBase;
            if ((uint)vramOff < (uint)Video.MemoryMapSize)
            {
                Video.WriteVram(vramOff, v);
                // Erase detection for HD replacement instances (PASO 4 frame semantics);
                // must see EVERY write, including string-op bytes SuppressRawBlit masks.
                if (RemakeMap.Active) RemakeMap.TouchWrite(vramOff, Video.VideoMode);
                if (BlitRawLog.Enabled)
                    BlitRawLog.Log(a, vramOff, v, Video, SuppressRawBlit);
                return;
            }
        }
        // Sin la condicion "y el valor cambia": para saber si a una direccion NO la escribe NADIE
        // hace falta ver tambien las escrituras del mismo valor. PAKU_MEMWATCH_ALL=1.
        if (_watchLin >= 0 && (a == _watchLin || a == _watchLin + 1)
            && (_watchTodo || Raw[a] != v) && _watchHits++ < 8)
        {
            var st = Environment.StackTrace;
            int p = st.IndexOf("Gen.Fn", StringComparison.Ordinal);
            uint ipc = Interp.LastPc; // writer's CS:IP when the write comes from interpreted code
            Console.Error.WriteLine($"[MEMWATCH] lin={a:X5} val={v:X2} (antes {Raw[a]:X2}) seg={seg:X4} off={off:X4} interpPC={ipc >> 16:X4}:{ipc & 0xFFFF:X4} " +
                (p >= 0 ? st.Substring(p, Math.Min(360, st.Length - p)).Replace(Environment.NewLine, " | ") : st));
        }
        Raw[a] = v;
        WriteLog?.Add((a, v));
    }

    public void WrU16(ushort seg, ushort off, ushort v)
    {
        WrU8(seg, off, (byte)v);
        WrU8(seg, (ushort)(off + 1), (byte)(v >> 8));
    }

    // ---- accesos de 32 bits y coma flotante (para el x87). Little-endian con wrap a 1 MiB. ----
    public uint RdU32(ushort seg, ushort off)
        => (uint)(RdU16(seg, off) | (RdU16(seg, (ushort)(off + 2)) << 16));
    public void WrU32(ushort seg, ushort off, uint v)
    { WrU16(seg, off, (ushort)v); WrU16(seg, (ushort)(off + 2), (ushort)(v >> 16)); }

    public float RdF32(ushort seg, ushort off) => BitConverter.UInt32BitsToSingle(RdU32(seg, off));
    public void WrF32(ushort seg, ushort off, float v) => WrU32(seg, off, BitConverter.SingleToUInt32Bits(v));

    public double RdF64(ushort seg, ushort off)
    {
        ulong lo = RdU32(seg, off);
        ulong hi = RdU32(seg, (ushort)(off + 4));
        return BitConverter.UInt64BitsToDouble(lo | (hi << 32));
    }
    public void WrF64(ushort seg, ushort off, double v)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(v);
        WrU32(seg, off, (uint)bits);
        WrU32(seg, (ushort)(off + 4), (uint)(bits >> 32));
    }

    // ================= espacio PLANO de 32 bits =================
    //
    // El mundo de modo protegido direcciona linealmente, sin segmentos, y puede pasar de 1 MiB.
    // En vez de ensanchar `Raw` y tocar `Lin()` —que enmascara a 20 bits porque el wrap del modo
    // real ES ASI y sobre eso están validados los 21 juegos probados—, el megabyte bajo se COMPARTE
    // y sólo lo de arriba vive en un array aparte.
    //
    // Compartirlo no es un ahorro: es lo que hace que una escritura del juego de 32 bits a
    // 0xA0000 acabe en `VideoState` (medido: un juego DOS/4GW escribe 67 veces directo
    // a la VRAM) y que la BDA y la IVT sean las mismas para los dos mundos.
    //
    // Los accesos lineales bajos delegan en RdU8/WrU8 descomponiendo la dirección en (seg, off):
    // `Lin(a >> 4, a & 0xF)` devuelve exactamente `a`, así que pasan por el MISMO camino de
    // siempre (intercepción de VRAM, watch de memoria, telemetría de blits) sin duplicar nada
    // ni tocar una línea de la ruta caliente del mundo de 16 bits.

    byte[]? _high;

    /// <summary>Bytes direccionables por encima de 1 MiB. 0 = no hay mundo plano (por defecto).</summary>
    public int HighSize => _high?.Length ?? 0;

    /// <summary>La memoria plana, para volcarla (PAKU_MEMDUMP_ALTO). En un juego de 32 bits todo
    /// lo interesante vive AQUÍ y no en el MiB de abajo: los datos que el juego descomprime al
    /// cargar —un mapa de terreno, por ejemplo— no aparecen en PAKU_MEMDUMP, que sólo ve la
    /// memoria convencional, y eso se lee como "no está" cuando lo que pasa es que no se mira.</summary>
    public byte[]? High => _high;

    /// <summary>Reserva el espacio plano por encima de 1 MiB. Idempotente al alza: pedir menos de
    /// lo ya reservado no encoge nada (un juego puede pedir memoria por DPMI varias veces).</summary>
    public void EnableFlat(int bytesAbove1M)
    {
        if (bytesAbove1M <= 0 || bytesAbove1M <= HighSize) return;
        var nuevo = new byte[bytesAbove1M];
        _high?.CopyTo(nuevo, 0);
        _high = nuevo;
    }

    /// <summary>Tope direccionable del espacio plano.</summary>
    public long FlatLimit => (long)Size + HighSize;

    // ---------------------------------------------------------------------------------------
    // PAGINACIÓN DEL 386 (CR0.PG). Hasta aquí PakuPort trataba lineal = física, que es exacto
    // mientras el programa mapee identidad — y por eso el hueco no se notó en ningún juego probado. Lo
    // rompe DOS/4GW Professional: su cargador de LE mapea el ejecutable en la lineal
    // 0xFF000000, que sólo existe a través de las tablas, y sin traducir, las escrituras del
    // juego se iban al vacío (`can't initialize loader LINEXE_LOADER`).
    //
    // Se activa SÓLO con PG encendido: apagado cuesta la lectura de un bool que el predictor
    // acierta siempre, y ningún otro juego probado lo enciende.
    //
    // Con PG activo, los vigilantes y la traza de esta clase (PAKU_WATCH_LIN, PAKU_TRAZA_VAR)
    // miran direcciones FÍSICAS: la traducción es lo PRIMERO que pasa, para que no haya dos
    // caminos que traduzcan distinto.

    /// <summary>El programa encendió CR0.PG. Lo pone el intérprete al escribir CR0.</summary>
    public static bool Paginacion;

    /// <summary>Base física del directorio de páginas (CR3 sin los bits de control).</summary>
    public static uint DirectorioPaginas;

    /// <summary>Dirección lineal del último fallo de página, o sea CR2.</summary>
    public static uint Cr2;

    // TLB de mapeo directo: 4096 entradas cubren 16 MiB de trabajo sin colisionar apenas, y una
    // entrada vale `páginaLineal + 1` para que el 0 signifique "vacía" sin tener que limpiarla.
    const int TlbBits = 12, TlbTam = 1 << TlbBits, TlbMask = TlbTam - 1;
    static readonly uint[] _tlbLin = new uint[TlbTam];
    static readonly uint[] _tlbFis = new uint[TlbTam];

    /// <summary>Vacía el TLB. Lo llama el intérprete al recargar CR3 — que es justo lo que hace
    /// un extensor DESPUÉS de tocar sus tablas, y es su única forma de decir "ya no valen".</summary>
    public static void VaciaTlb() => Array.Clear(_tlbLin);

    /// <summary>Enciende o apaga la paginación con la CR3 vigente. Vaciar el TLB al cambiar
    /// cualquiera de las dos es obligatorio: las traducciones cacheadas son de otro mundo.</summary>
    /// <summary>PAKU_SIN_PAGINACION=1: no traducir aunque el programa encienda PG (lineal =
    /// física, como antes de que esto existiera). Es la escotilla del A/B — la que separa "el
    /// juego falla por la traducción" de "falla por otra cosa".</summary>
    static readonly bool _sinPaginacion =
        Environment.GetEnvironmentVariable("PAKU_SIN_PAGINACION") == "1";

    /// <summary>PAKU_LOG_PAGINAS=1: las primeras traducciones, diciendo cuáles son identidad y
    /// cuáles mueven la página de sitio.</summary>
    public static readonly bool LogPaginas = Environment.GetEnvironmentVariable("PAKU_LOG_PAGINAS") == "1";
    static int _avisosPag;

    /// <summary>Cuántas traducciones NO fueron identidad. Si sale 0, la paginación del
    /// programa es un mapeo plano y no puede explicar ninguna divergencia.</summary>
    public static long PaginasMovidas;

    public static void ConfiguraPaginacion(bool activa, uint cr3)
    {
        Paginacion = activa && !_sinPaginacion;
        DirectorioPaginas = cr3 & 0xFFFFF000;
        GenDescriptores++;   // la misma lineal de la tabla puede caer en otra física
        VaciaTlb();
    }

    /// <summary>Un byte por dirección FÍSICA, sin traducir, sin vigilantes y sin la intercepción
    /// de VRAM: para leer el directorio y las tablas de páginas, que la MMU lee del bus.</summary>
    byte LeeFisicoCrudo(uint a)
    {
        if (a < Size) return Raw[a];
        int i = (int)(a - Size);
        var h = _high;
        return h != null && (uint)i < (uint)h.Length ? h[i] : (byte)0xFF;
    }

    public uint LeeFisicoCrudo32(uint a) => (uint)(LeeFisicoCrudo(a)
                                            | (LeeFisicoCrudo(a + 1) << 8)
                                            | (LeeFisicoCrudo(a + 2) << 16)
                                            | (LeeFisicoCrudo(a + 3) << 24));

    /// <summary>Lineal → física por el directorio y las tablas del propio programa. Una página
    /// ausente lanza <see cref="FalloDePaginaException"/>, que el intérprete convierte en la
    /// excepción 14 del invitado: es lo que espera un extensor con memoria virtual, y lanzarla
    /// (en vez de devolver una dirección cualquiera) evita el peor final posible, que es seguir
    /// leyendo memoria de otro sitio como si tal cosa.</summary>
    public uint TraduceLineal(uint a, bool escritura)
    {
        uint pl = a >> 12;
        int k = (int)(pl & TlbMask);
        if (_tlbLin[k] == pl + 1) return _tlbFis[k] | (a & 0xFFF);

        uint entradaDir = LeeFisicoCrudo32(DirectorioPaginas + ((a >> 22) << 2));
        if ((entradaDir & 1) == 0) throw new FalloDePaginaException(a, escritura, false);
        uint entradaTabla = LeeFisicoCrudo32((entradaDir & 0xFFFFF000) + (((a >> 12) & 0x3FF) << 2));
        if ((entradaTabla & 1) == 0) throw new FalloDePaginaException(a, escritura, true);

        uint fis = entradaTabla & 0xFFFFF000;
        if (fis != (a & 0xFFFFF000))
        {
            PaginasMovidas++;
            if (LogPaginas && _avisosPag++ < 40)
                Console.Error.WriteLine(
                    $"[PAG] lineal 0x{a & 0xFFFFF000:X8} -> fisica 0x{fis:X8} *** MOVIDA ***");
        }
        _tlbLin[k] = pl + 1;
        _tlbFis[k] = fis;
        return fis | (a & 0xFFF);
    }

    public byte RdLin8(uint a)
    {
        if (Paginacion) a = TraduceLineal(a, false);
        if (_trazaIni <= a && a < _trazaFin) ApuntaLectura(a);
        // FÍSICO directo, no RdU8: con el modo protegido activo aquél vuelve aquí y se llaman
        // en círculo. La dirección ya está resuelta, que es justo lo que RdFisico8 espera.
        if (a < Size) return RdFisico8((int)a);
        int i = (int)(a - Size);
        var h = _high;
        return h != null && (uint)i < (uint)h.Length ? h[i] : (byte)0xFF;
    }

    // PAKU_TRAZA_LECTURA=<hexIni>-<hexFin>: QUÉ direcciones de esa región lee el juego, en un
    // buffer circular con las últimas. El vigilante de al lado mira ESCRITURAS, y hay preguntas
    // que sólo contestan las lecturas: qué parte de un mapa está mirando el jugador ahora mismo.
    // En un motor 3D que dibuja desde un mapa, las direcciones que su renderizador lee del mapa
    // dibujan el abanico de visión, y el vértice de ese abanico es la cámara. Preguntárselo al
    // juego en vez de buscar sus variables a ciegas.
    // Apagada (sin la variable) no cuesta nada: una comparación contra un rango vacío.
    static readonly uint _trazaIni, _trazaFin;
    static uint[]? _traza;
    static int _trazaPos;
    const int TrazaMax = 1 << 21;          // 2 M de direcciones = varios fotogramas

    // Los helpers DEVUELVEN lo que hay que asignar en vez de asignarlo: `_trazaIni`/`_trazaFin`
    // y `_vars` son readonly estáticos —se leen en la ruta más caliente de la memoria y el JIT
    // los pliega— y eso sólo se puede asignar aquí dentro.
    static Memory()
    {
        (_trazaIni, _trazaFin) = LeeRangoTraza();
        if (_trazaFin > _trazaIni)
        {
            _traza = new uint[TrazaMax];
            AppDomain.CurrentDomain.ProcessExit += (_, _) => VuelcaTraza();
        }

        _vars = LeeVars();
        if (_vars.Length > 0)
        {
            _varDisparo = _vars[0] + 3;
            _varMuestras = new uint[VarMax * (_vars.Length + 1)];
            AppDomain.CurrentDomain.ProcessExit += (_, _) => VuelcaVars();
        }
    }

    static (uint, uint) LeeRangoTraza()
    {
        var v = Environment.GetEnvironmentVariable("PAKU_TRAZA_LECTURA");
        if (string.IsNullOrEmpty(v)) return (0, 0);
        var partes = v.Split('-');
        if (partes.Length != 2) return (0, 0);
        return uint.TryParse(partes[0], System.Globalization.NumberStyles.HexNumber, null, out var i0)
            && uint.TryParse(partes[1], System.Globalization.NumberStyles.HexNumber, null, out var i1)
            && i1 > i0 ? (i0, i1) : (0, 0);
    }

    static void ApuntaLectura(uint a)
    {
        var t = _traza;
        if (t == null) return;
        // Se apunta la direccion Y QUIEN la lee. Sin el EIP no se pueden separar dos rutinas que
        // leen el MISMO mapa —por ejemplo el renderizador y un mapa en miniatura del HUD— y
        // entonces sus dos patrones de lectura se mezclan en uno que no significa nada.
        int i = (_trazaPos & (TrazaMax / 2 - 1)) * 2;
        t[i] = a;
        t[i + 1] = UltimoEip32;
        _trazaPos++;
    }

    static void VuelcaTraza()
    {
        var t = _traza;
        if (t == null || _trazaPos == 0) return;
        try
        {
            Directory.CreateDirectory("logs");
            int cap = TrazaMax / 2;
            int n = Math.Min(_trazaPos, cap);
            var bytes = new byte[n * 8];
            // En orden cronológico: si el circular dio la vuelta, la más vieja es la siguiente.
            int inicio = _trazaPos > cap ? _trazaPos & (cap - 1) : 0;
            for (int k = 0; k < n; k++)
            {
                int i = ((inicio + k) & (cap - 1)) * 2;
                BitConverter.TryWriteBytes(bytes.AsSpan(k * 8), t[i]);
                BitConverter.TryWriteBytes(bytes.AsSpan(k * 8 + 4), t[i + 1]);
            }
            File.WriteAllBytes(Path.Combine("logs", "lecturas.bin"), bytes);
            Console.Error.WriteLine($"[TRAZA] {n} lecturas (direccion+EIP) de {_trazaIni:X}-{_trazaFin:X} -> logs/lecturas.bin");
        }
        catch { }
    }

    // PAKU_TRAZA_VAR=<hex>[,<hex>...]: la TRAYECTORIA de unas cuantas variables del mundo plano,
    // no sólo quién las toca. El vigilante de abajo identifica al culpable y se para a los 12
    // avisos, y además avisa por BYTE, así que de un dword sólo enseña una esquina: sirve para
    // saber quién escribe, no para ver cómo evoluciona un valor.
    //
    // Se apunta una muestra con el valor de TODAS las variables cada vez que se completa una
    // escritura sobre la PRIMERA de la lista (la disparadora). El disparo va en su último byte y
    // no en el primero: `WrLin32` escribe de byte en byte, así que mirando el primero se leería
    // el dword a medias — mitad valor nuevo, mitad viejo.
    //
    // Para qué: grabando la posición y la orientación de una cámara se reconstruye su
    // trayectoria fotograma a fotograma, que es lo que hace falta para redibujar una escena
    // desde donde el juego la está mirando.
    static readonly uint[] _vars;
    static readonly uint _varDisparo = uint.MaxValue;
    static uint[]? _varMuestras;
    static int _varPos;
    const int VarMax = 1 << 18;            // muestras; a ~3 por fotograma son minutos de vuelo

    static uint[] LeeVars()
    {
        var v = Environment.GetEnvironmentVariable("PAKU_TRAZA_VAR");
        if (string.IsNullOrEmpty(v)) return Array.Empty<uint>();
        var lista = new List<uint>();
        foreach (var p in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (uint.TryParse(p.Trim(), System.Globalization.NumberStyles.HexNumber, null,
                              out var d))
                lista.Add(d);
        return lista.ToArray();
    }

    void ApuntaVars()
    {
        var m = _varMuestras;
        if (m == null) return;
        int ancho = _vars.Length + 1;
        int i = (_varPos & (VarMax - 1)) * ancho;
        m[i] = UltimoEip32;
        for (int k = 0; k < _vars.Length; k++) m[i + 1 + k] = RdLin32(_vars[k]);
        _varPos++;
    }

    static void VuelcaVars()
    {
        var m = _varMuestras;
        if (m == null || _varPos == 0) return;
        try
        {
            Directory.CreateDirectory("logs");
            int ancho = _vars.Length + 1;
            int n = Math.Min(_varPos, VarMax);
            int inicio = _varPos > VarMax ? _varPos & (VarMax - 1) : 0;
            var sb = new System.Text.StringBuilder();
            sb.Append("muestra,eip");
            foreach (var d in _vars) sb.Append(",v").Append(d.ToString("X"));
            sb.AppendLine();
            for (int k = 0; k < n; k++)
            {
                int i = ((inicio + k) & (VarMax - 1)) * ancho;
                sb.Append(k).Append(',').Append(m[i].ToString("X8"));
                for (int j = 1; j < ancho; j++) sb.Append(',').Append((int)m[i + j]);
                sb.AppendLine();
            }
            File.WriteAllText(Path.Combine("logs", "variables.csv"), sb.ToString());
            Console.Error.WriteLine($"[TRAZAVAR] {n} muestras de {_vars.Length} variables " +
                                    "-> logs/variables.csv");
        }
        catch { }
    }

    // PAKU_WATCH_LIN=<hex lineal>: quién escribe en esa dirección del espacio PLANO, con el EIP
    // del intérprete de 32 bits. El PAKU_MEMWATCH de arriba no sirve aquí: aquel vigila el mundo
    // de 16 bits por seg:off y no ve nada de lo que pasa por encima del megabyte.
    static readonly long _watchLin32 =
        long.TryParse(Environment.GetEnvironmentVariable("PAKU_WATCH_LIN"),
                      System.Globalization.NumberStyles.HexNumber, null, out var _wl) ? _wl : -1;
    static int _watchLin32Hits;

    /// <summary>EIP lineal de la instrucción que el intérprete de 32 bits está ejecutando. Lo pone
    /// <c>Interp32</c> para que el vigilante pueda decir QUIÉN escribió, no sólo que se escribió.</summary>
    public static uint UltimoEip32;

    /// <summary>Generación por página de 4K del espacio ALTO, para el cache de decodificación de
    /// <c>Interp32</c>: toda escritura al espacio alto incrementa la de su página, y una
    /// instrucción cacheada sólo vale si la generación de sus páginas no cambió — así la
    /// AUTOMODIFICACIÓN (un juego de prueba parchea los inmediatos de su motor en runtime) invalida
    /// exactamente lo que toca. El índice va con máscara: el aliasing con espacios de más de
    /// 64 MiB invalida DE MÁS, nunca de menos.</summary>
    public static readonly long[] GenPagina = new long[1 << 14];
    public const uint GenPaginaMask = (1u << 14) - 1;

    /// <summary>Escrituras que cayeron fuera del espacio reservado y se perdieron. Si sale
    /// distinto de cero, el juego está trabajando contra un agujero negro.</summary>
    public static long EscriturasPerdidas;
    static int _escriturasPerdidasAvisadas;

    public void WrLin8(uint a, byte v)
    {
        if (GdtFin != 0) VigilaEscrituraEnTablas(a);
        if (Paginacion)
        {
            uint fis = TraduceLineal(a, true);
            // La generacion de pagina la marca WrLin8Directo por la direccion FISICA, pero los
            // caches de decodificacion indexan por la LINEAL (que es la que identifica al codigo:
            // el VMM puede traerse la misma pagina a otra fisica sin que su contenido cambie).
            // Con paginacion las dos difieren, asi que un programa que se automodifica invalidaba
            // la entrada equivocada y el cache habria servido la instruccion vieja.
            if (fis != a && a >= Size) GenPagina[(a >> 12) & GenPaginaMask]++;
            a = fis;
        }
        // Después de escribir, no antes: la muestra debe llevar el valor nuevo (ver PAKU_TRAZA_VAR).
        if (a == _varDisparo) { WrLin8Directo(a, v); ApuntaVars(); return; }
        if (_watchLin32 >= 0 && a == (uint)_watchLin32 && _watchLin32Hits++ < 12)
        {
            // Los DOS mundos pueden escribir aquí: en modo protegido de 16 bits `WrU8` acaba
            // en esta misma función, así que el PC del intérprete de 16 va también — sin él,
            // una escritura del extensor parece venir de un EIP de 32 bits que ni se ha movido.
            uint pc16 = Interp.LastPc;
            Console.Error.WriteLine(
                $"[WATCHLIN] 0x{a:X8} <- {v:X2} (antes {RdLin8(a):X2}) EIP32=0x{UltimoEip32:X8} " +
                $"interpPC={pc16 >> 16:X4}:{pc16 & 0xFFFF:X4}");
        }
        WrLin8Directo(a, v);
    }

    // --- Procedencia de RAM (PAKU_PROC_PIXEL=1): quién escribió cada byte del espacio ALTO ---
    //
    // Es la mitad que le faltaba a la procedencia de píxel: un juego que compone en un buffer de
    // RAM y lo presenta con un blitter deja TODA la VRAM atribuida al blitter. Etiquetando la RAM
    // alta —donde vive todo lo de un juego de 32 bits— el movs del blitter puede HEREDAR la
    // etiqueta del byte fuente (ver VideoState.IdHeredado). La RAM baja no se etiqueta: los
    // juegos de 16 bits componen por seg:off y su procedencia ya la resuelve el kit por blits.
    static readonly bool _procRam = Platform.VideoState.ProcedenciaPixel;
    ushort[]? _procHigh;
    uint _procRamBloque = uint.MaxValue;
    ushort _procRamId;

    /// <summary>El id de procedencia del byte de RAM alta `a` (lineal), o 0 si nadie lo ha
    /// escrito, no hay palanca, o la dirección no es del espacio alto.</summary>
    public ushort ProcDeLin(uint a)
    {
        var p = _procHigh;
        if (p == null || a < Size) return 0;
        int i = (int)(a - Size);
        return (uint)i < (uint)p.Length ? p[i] : (ushort)0;
    }

    void MarcaProcRam(int i, byte v)
    {
        var h = _high!;
        // ESCRIBIR LO MISMO NO ES PINTAR: el dibujador de sprites de un juego de prueba reescribe también
        // el fondo de su rectángulo con el valor que ya había, y si eso robara la identidad, el
        // fondo alrededor de cada sprite pasaría a «vivo» y el host lo devolvería encima de la
        // capa HD — una prueba manual lo veía como rachas del fondo original pegadas al sprite.
        if (h[i] == v) return;
        var p = _procHigh;
        if (p == null || p.Length < h.Length)
        {
            var nuevo = new ushort[h.Length];
            p?.CopyTo(nuevo, 0);
            _procHigh = p = nuevo;
        }
        uint bloque = UltimoEip32 & Platform.VideoState.BloqueMask;
        if (bloque != _procRamBloque)
        {
            _procRamBloque = bloque;
            _procRamId = Platform.VideoState.IdRegistra(bloque);
        }
        p[i] = _procRamId;
    }

    void WrLin8Directo(uint a, byte v)
    {
        // FÍSICO directo, no WrU8: ver la nota de RdLin8 (se llamarían en círculo).
        if (a < Size) { WrFisico8((int)a, (ushort)(a >> 4), (ushort)(a & 0xF), v); return; }
        int i = (int)(a - Size);
        var h = _high;
        if (h != null && (uint)i < (uint)h.Length)
        {
            if (_procRam) MarcaProcRam(i, v);
            h[i] = v;
            GenPagina[(a >> 12) & GenPaginaMask]++;
            return;
        }
        // Fuera del espacio reservado se descarta a propósito: en modo protegido eso sería un
        // fallo de página, y quien decide qué hacer con él es el host DPMI, no la memoria.
        // Lanzar aquí mataría el hilo sin dejar que el juego lo maneje.
        //
        // Pero NO en silencio: un juego que escribe donde no hay nada se ve exactamente igual que
        // uno que no escribe, y eso es indepurable. Se cuentan todas y se avisa de las primeras.
        EscriturasPerdidas++;
        if (_escriturasPerdidasAvisadas++ < 8)
            Console.Error.WriteLine($"[FUERA] escritura descartada en 0x{a:X8} <- {v:X2} " +
                                    $"(el espacio llega a 0x{Size + HighSize:X8}) EIP32=0x{UltimoEip32:X8} " +
                                    $"interpPC={Interp.LastPc >> 16:X4}:{Interp.LastPc & 0xFFFF:X4} " +
                                    $"paginacion={(Paginacion ? $"SI cr3=0x{DirectorioPaginas:X8}" : "no")}");
    }

    /// <summary>Copia un bloque a la memoria del programa DESDE UN SERVICIO de la plataforma:
    /// el buffer de una lectura de fichero, la imagen que carga un EXEC, el PSP que DOS rellena.
    ///
    /// <para>Existe por el BANCO DE PRUEBAS. Un <c>Array.Copy</c> a <see cref="Raw"/> no pasa por
    /// <see cref="WrFisico8"/>, así que <c>CorpusRecorder</c> no ve esos bytes y el WRITE-SET de
    /// la INT sale sin ellos. Dentro del DGROUP no se notaba —el efecto se calcula ADEMÁS por
    /// foto y diff de esos 64 KB—, pero un buffer FUERA del DGROUP se perdía entero. Medido en
    /// un juego de prueba (2026-08-27): la función <c>d57e</c> lee 23.413 bytes de disco a
    /// <c>615E:9EA5</c>, que es un bloque del far heap; al reproducir el caso el buffer llegaba
    /// vacío, el descompresor procesaba basura, se salía del segmento, daba la vuelta por el
    /// offset 0 y machacaba la cabecera del heap. El juego moría en su ruta de error fatal con
    /// <c>invalid far heap in gmalloc: bad node</c>, y el síntoma que se veía tres capas más
    /// abajo era un <c>INT 10h</c> que el caso no tenía grabado.</para>
    ///
    /// <para>Con el grabador apagado cuesta UNA comparación de un bool por bloque, no por byte.
    /// </para></summary>
    public void CopiaDeServicio(int dst, byte[] src, int srcOff, int len)
    {
        Array.Copy(src, srcOff, Raw, dst, len);
        if (CorpusRecorder.Activo)
            for (int i = 0; i < len; i++) CorpusRecorder.NotaEscritura(dst + i, src[srcOff + i]);
    }

    /// <summary>Igual, para una copia DENTRO de la memoria del programa que hace un servicio (el
    /// PSP y el entorno que DOS duplica al cargar un hijo). El intermedio es lo que hace que un
    /// solape se comporte como el <c>memmove</c> que <c>Array.Copy</c> ya garantiza.</summary>
    public void CopiaDeServicio(int dst, int src, int len)
    {
        var tmp = new byte[len];
        Array.Copy(Raw, src, tmp, 0, len);
        CopiaDeServicio(dst, tmp, 0, len);
    }

    public ushort RdLin16(uint a) => (ushort)(RdLin8(a) | (RdLin8(a + 1) << 8));
    public void WrLin16(uint a, ushort v) { WrLin8(a, (byte)v); WrLin8(a + 1, (byte)(v >> 8)); }
    public uint RdLin32(uint a) => (uint)(RdLin16(a) | (RdLin16(a + 2) << 16));
    public void WrLin32(uint a, uint v) { WrLin16(a, (ushort)v); WrLin16(a + 2, (ushort)(v >> 16)); }

    /// <summary>Copia una imagen (la que produce <see cref="LeLoader"/>) al espacio plano,
    /// reservando lo que haga falta por encima de 1 MiB.
    ///
    /// La reserva se REDONDEA A PÁGINA, y no es cosmético. La pila de un LE arranca en el tope
    /// exacto del último objeto, así que si la memoria termina en ese mismo byte, un `dword` leído
    /// a caballo del borde devuelve 3 bytes buenos y un 0xFF (lo que da <see cref="RdLin8"/> fuera
    /// de lo reservado). Se vio como una dirección de retorno con el byte alto contaminado —
    /// `0x00153BCA` leída como `0xFF153BCA`— y parecía corrupción de pila. Un extensor de verdad
    /// mapea páginas enteras y nunca deja el borde a mitad de una palabra.</summary>
    public void LoadFlatImage(byte[] image, uint at = 0)
    {
        long fin = (long)at + image.Length;
        long finPagina = (fin + 0xFFF) & ~0xFFFL;
        if (finPagina > Size) EnableFlat((int)(finPagina - Size));
        for (int i = 0; i < image.Length; i++) WrLin8((uint)(at + i), image[i]);
    }
}

/// <summary>Una página ausente con la paginación encendida. La lanza <see cref="Memory"/> desde
/// el acceso y la recoge el intérprete, que entrega la excepción 14 del invitado por su IDT con
/// CR2 puesta y SIN avanzar el puntero de instrucción: al volver del manejador la instrucción se
/// repite, que es lo que hace el hardware y lo que el gestor de memoria virtual de un extensor
/// (DOS/4GW Professional y su fichero .SWP) da por hecho.</summary>
public sealed class FalloDePaginaException : Exception
{
    public readonly uint Lineal;
    public readonly bool Escritura;
    /// <summary>La tabla existía y la página no (true), o faltaba ya la entrada del directorio
    /// (false). Va en el bit 0 del código de error, que es el que distingue "no presente" de
    /// "violación de protección".</summary>
    public readonly bool TablaPresente;

    public FalloDePaginaException(uint lineal, bool escritura, bool tablaPresente)
        : base($"fallo de página en la lineal 0x{lineal:X8} ({(escritura ? "escritura" : "lectura")})")
    {
        Lineal = lineal; Escritura = escritura; TablaPresente = tablaPresente;
    }

    /// <summary>El código de error que el 386 empuja en la pila del manejador: bit 0 presente,
    /// bit 1 escritura, bit 2 usuario. Aquí siempre supervisor (el extensor corre en anillo 0).</summary>
    public uint CodigoDeError => (Escritura ? 2u : 0u);
}
