namespace PakuPort.Core.Platform;

/// <summary>
/// Host de servicios del mundo plano de 32 bits — plan.md fase 2. Es lo que el juego creía que
/// era su extensor DOS: DPMI (<c>INT 31h</c>) y el DOS que se llama desde 32 bits (<c>INT 21h</c>).
///
/// <b>La regla, que sale de lo medido y no de una preferencia.</b> PakuPort no imita a DOS/4GW:
/// lo SUSTITUYE. Por eso:
/// <list type="bullet">
/// <item>Lo <b>estándar</b> (DOS y DPMI) se implementa de verdad.</item>
/// <item>Lo <b>propietario</b> de DOS/4G (<c>INT 21h AX=FF00</c>, <c>AH=EDh</c>…) se rechaza. No
/// es dejadez: el arranque de Watcom TIENE camino alternativo para cada una —lo tiene que tener,
/// porque el mismo binario corre bajo hosts DPMI que no son DOS/4GW— y se comprobó leyendo esas
/// ramas en el propio binario antes de rechazar nada.</item>
/// </list>
///
/// <b>Selectores.</b> En un espacio plano no hay descriptores de verdad: un selector es un
/// <i>handle</i> con base y límite en una tabla nuestra. Los que el modelo plano usa (código,
/// datos, pila) tienen base 0, que es lo que hace que los punteros del programa sean direcciones
/// lineales directas — cosa que se MIDIÓ al comparar la imagen con la de DOS/4GW (los fixups son
/// absolutos, ver tools/le_loader/README.md §4).
/// </summary>
public sealed class FlatServices
{
    public sealed class Descriptor
    {
        public uint Base;
        public uint Limit = 0xFFFFFFFF;
        public bool Presente = true;
        public bool Codigo;
        /// <summary>Bit D/B del descriptor: el segmento es de 32 bits. Es lo que distingue el
        /// código de 16 del de 32 dentro del mismo mundo protegido, y por tanto QUÉ intérprete
        /// tiene que ejecutarlo. Lo declara el cliente en `INT 31h AX=0009` (bit 6 del byte alto
        /// de los derechos): un extensor embebido marca así el selector al que salta su juego.</summary>
        public bool Ancho32;
    }

    /// <summary>Selector plano de datos/pila y de código. Valores del convenio que el propio
    /// arranque de Watcom da por hecho cuando no hay extensor conocido (se ven literales en su
    /// rama por defecto: <c>mov cx,0x24</c> / <c>0x2c</c> / <c>mov bx,0x17</c>).</summary>
    public const ushort SelDatos = 0x0014, SelCodigo = 0x000C, SelPsp = 0x0024;

    /// <summary>Direccion del `iretd` que sirve de manejador por defecto (zona libre de la
    /// memoria baja, entre la BDA y el area de arranque del DOS).</summary>
    const uint StubIretd = 0x0600;

    readonly Dictionary<ushort, Descriptor> _desc = new();
    ushort _siguienteSel = 0x0040;

    /// <summary>Asignador del espacio plano: bloques por encima de la imagen del juego.</summary>
    uint _heap;

    /// <summary>Estado de las interrupciones virtuales del cliente (DPMI 0900/0901/0902). Arranca
    /// habilitado, como en un host de verdad. Antes 0900 y 0901 devolvían 1 a secas: un cliente
    /// que las deshabilita, hace su sección crítica y RESTAURA lo que le contestamos se quedaba
    /// con ellas habilitadas para siempre.</summary>
    bool _interrupcionesVirtuales = true;

    /// <summary>Dirección lineal de cada bloque de DPMI 0501, para poder redimensionarlo (0503):
    /// el handle por sí solo no dice dónde está.</summary>
    readonly Dictionary<uint, uint> _dirDeBloque = new();

    /// <summary>Dónde empieza el montón de DPMI. La resta contra <c>_heap</c> es lo repartido, y
    /// de ahí sale lo que queda libre en el informe del 0500.</summary>
    uint _baseDelMonton;
    /// <summary>Monton de memoria BAJA (DPMI 0100). Vive entre la imagen del juego y la VRAM
    /// porque el DMA de la ISA solo direcciona el primer megabyte.</summary>
    uint _dosHeap;
    readonly Dictionary<uint, uint> _bloques = new();   // handle -> tamaño
    uint _siguienteHandle = 1;

    public readonly Memory Mem;
    /// <summary>Todo lo que el juego ha pedido, en orden. Telemetría: la lista MEDIDA de
    /// servicios vale más que cualquier análisis estático.</summary>
    public readonly List<string> Traza = new();
    /// <summary>Servicios propietarios rechazados (con su rama alternativa ya comprobada).</summary>
    public readonly List<string> Rechazados = new();

    /// <summary>Ficheros que el juego pidió abrir y no están. No es un error nuestro por sí solo
    /// —un juego sondea ficheros opcionales a propósito— pero es LA pista cuando falta contenido:
    /// así se supo que a una copia de un juego de prueba le faltan cuatro efectos de sonido del rip
    /// original. Va al informe de la partida para que el jugador no tenga que saber nada de esto.</summary>
    public readonly List<string> NoEncontrados = new();

    /// <summary>PAKU_TRAZA_FICHEROS=1: cada seek y cada lectura con su posición real. Sin esto,
    /// entender por qué un juego elige mal un offset dentro de un catálogo es imposible.</summary>
    public static readonly bool TrazaFicheros =
        Environment.GetEnvironmentVariable("PAKU_TRAZA_FICHEROS") == "1";

    /// <summary>Con true, ninguna llamada de DOS puede crear, truncar ni escribir un fichero del
    /// juego. La sonda `le-run` lo enciende: mientras se itera sobre el arranque de un juego un
    /// `AH=3C` (crear) truncaria el fichero real, y perder datos del usuario explorando no es una
    /// posibilidad aceptable. El host de produccion lo dejara en false.</summary>
    public bool SoloLectura;

    /// <summary>Vectores de interrupcion de MODO PROTEGIDO (selector:offset de 32 bits).
    ///
    /// Bajo DPMI, un `INT 21h AH=25` hecho DESDE modo protegido no toca la IVT del primer
    /// megabyte: fija el vector protegido, y el offset son 32 bits enteros. Guardarlo en la IVT
    /// truncaba el offset a 16 y dejaba el manejador del juego en el limbo.</summary>
    readonly (ushort sel, uint off)[] _vecPm = new (ushort, uint)[256];

    /// <summary>La plataforma (PIC, temporizador, VGA...). Es la MISMA que la del mundo de 16
    /// bits: el hardware es uno solo.</summary>
    public DosPlatform? Plataforma;

    /// <summary>Tecla que se entrega cuando el juego espera una pulsacion. 0x20 = espacio,
    /// que es lo que pasa casi cualquier pantalla de "pulsa una tecla".</summary>
    public byte TeclaSintetica = 0x20;

    readonly string _dirJuego;
    readonly Dictionary<int, FileStream> _ficheros = new();

    /// <summary>Handle de fichero como lo reparte DOS: el MENOR libre de la tabla del proceso
    /// (0-4 son los estándar: stdin, stdout, stderr, aux, prn). Es el mismo
    /// <c>AllocHandle</c> del DOS de 16 bits (<see cref="Int21hHandler"/>), y no es un detalle.
    ///
    /// Antes se repartía un contador que sólo subía y NUNCA reutilizaba. Un programa que abre y
    /// cierra ficheros en serie —un juego de prueba carga sus 40 y pico efectos `.voc` uno detrás de
    /// otro— trepaba hasta el handle 128, un número que en DOS real no ve jamás porque allí el
    /// cerrado devuelve su hueco. Y ahí se plantaba: **ese era el "muro del audio"** que parecía
    /// un fallo del driver de sonido. Se veía sólo con la Sound Blaster instalada porque sin ella
    /// el juego no carga ningún efecto y sus handles no pasan de los primeros.</summary>
    int AsignarHandleFichero()
    {
        int h = 5;
        while (_ficheros.ContainsKey(h)) h++;
        return h;
    }

    /// <summary>Donde empieza el montón de DPMI. Todo lo que hay por debajo es del programa, que
    /// lo reclama con `AH=4A` segun le hace falta; por encima repartimos nosotros. 8 MiB de sitio
    /// contiguo para crecer es mas de lo que un juego de la época pide a un DOS/4GW —el probado llega
    /// a ~3,6 MiB— y deja otros 8 para los bloques DPMI dentro del espacio plano de 16.</summary>
    public const uint BaseDelMontonDpmi = 8 * 1024 * 1024;

    /// <summary>Tope que el programa ha reclamado con SETBLOCK. Es la frontera: por debajo no se
    /// entrega nada por DPMI.</summary>
    public uint TopeDelPrograma;

    public FlatServices(Memory mem, uint topeDeLaImagen, string dirJuego = ".")
    {
        Mem = mem;
        _dirJuego = dirJuego;
        // El montón de DPMI va MUY por encima de la imagen, no pegado detrás de ella. El espacio
        // contiguo que sigue al programa ES DEL PROGRAMA: el runtime de C de Watcom hace crecer
        // ahi su monton a pasos con `INT 21h AH=4A` (medido en un juego DOS/4GW: tope 0x18E1A8 ->
        // 0x1961A8 -> ... -> 0x3791A8 y subiendo). Pegado a la imagen —como estaba, a 12 KB del
        // tope— el juego SE COMIA los bloques que le habiamos entregado: cargaba su driver de
        // sonido en 0x193000 y unos millones de instrucciones despues lo machacaba entero con su
        // propia rutina de pintar pixeles. De ahi la "corrupcion aleatoria" que parecia una
        // carrera del emulador. Un extensor de verdad nunca pone un bloque DPMI ahi.
        _heap = Math.Max((topeDeLaImagen + 0xFFFFu) & ~0xFFFFu, BaseDelMontonDpmi);
        _baseDelMonton = _heap;
        // Memoria CONVENCIONAL (la de DPMI 0100, que el DMA de la ISA necesita). Si la imagen
        // del juego esta por encima de 1 MiB —que es donde la pone un extensor de verdad,
        // medido: DOS/4GW la dejo en 0x1E6000— el primer megabyte queda ENTERO para esto.
        // Cargarla abajo se come la RAM baja y el juego se planta con "Insufficient base RAM".
        _dosHeap = topeDeLaImagen >= 0x100000u ? 0x10000u : ((topeDeLaImagen + 0xFFFu) & ~0xFFFu);
        _desc[SelDatos] = new Descriptor();
        _desc[SelCodigo] = new Descriptor { Codigo = true };
        _desc[SelPsp] = new Descriptor();
        Interp32.OnDescriptor = Descriptores;
        Interp32.BaseDeSelector = BaseDe;

        // Manejador POR DEFECTO de todos los vectores de modo protegido: un `iretd` pelado.
        //
        // No es adorno. Un juego que engancha una interrupcion pide ANTES el manejador anterior
        // (`AH=35`) para encadenarlo al final del suyo. Devolverle 0:0 —que es lo que se hacia—
        // hace que encadene a la direccion CERO: un juego DOS/4GW enganchaba el timer y su ISR acababa
        // saltando a la nada en la primera IRQ. Un host DPMI de verdad siempre tiene algo ahi.
        mem.WrLin8(StubIretd, 0xCF);        // en 32 bits, 0xCF ES `iretd`
        for (int i = 0; i < _vecPm.Length; i++) _vecPm[i] = (SelCodigo, StubIretd);
    }

    /// <summary>EL CAMBIO A MODO PROTEGIDO que pide un cliente DPMI (el `call far ES:DI` del
    /// punto de entrada que publicamos en <c>INT 2Fh AX=1687h</c>).
    ///
    /// <b>El truco, que es lo que lo hace barato y fiable:</b> los selectores que se le devuelven
    /// al cliente son NUMÉRICAMENTE los mismos que sus segmentos de modo real, y su descriptor
    /// lleva base <c>seg×16</c>. Así, justo después del cambio, direccionar por ellos da
    /// EXACTAMENTE las mismas direcciones que antes: el mundo del cliente no se mueve ni un byte,
    /// que es precisamente lo que la especificación promete ("los selectores dan acceso a la misma
    /// memoria que tenían los segmentos"). Lo que cambia es que a partir de aquí puede pedir
    /// descriptores nuevos por <c>INT 31h</c> y ponerles la base que quiera, incluso por encima
    /// del megabyte — y eso ya sale de esta tabla.
    ///
    /// Es la mitad de 16 bits del mundo protegido. El salto al de 32 lo hace el propio cliente
    /// cuando se fabrica un selector de código de 32 bits y salta a él.</summary>
    public void EntrarEnModoProtegido(Cpu c, Memory m)
    {
        // No se registra NADA: `BaseDe16` trata cualquier selector que no esté en la tabla como un
        // segmento de modo real (base = valor×16), así que el mundo entero del cliente —su código,
        // su pila, sus datos y hasta los que aún no ha tocado— sigue direccionando EXACTAMENTE
        // igual que un instante antes. Registrar sólo los seis registros de segmento era peor que
        // no registrar ninguno: en cuanto el `retf` de salida cargaba en CS un selector que no
        // estaba en la lista, su base salía 0 y el flujo se iba a la tabla de interrupciones.
        Memory.BaseDelSelector = BaseDe16;
        Memory.SegmentosSonSelectores = true;
        Traza.Add($"DPMI: cambio a MODO PROTEGIDO — CS={c.CS:X4} DS={c.DS:X4} SS={c.SS:X4} ES={c.ES:X4}");
        Console.Error.WriteLine($"[DPMI] modo protegido: CS={c.CS:X4} DS={c.DS:X4} SS={c.SS:X4} ES={c.ES:X4}");
    }

    /// <summary>Lo que ven `lar`/`lsl`/`verr`/`verw`. Los derechos son los de un segmento de 4 GB,
    /// granularidad de página, 32 bits y presente — que es lo que ES un selector del modelo plano;
    /// `lar` los devuelve ya en la posición del dword alto del descriptor.</summary>
    public bool Descriptores(ushort sel, out uint limite, out uint derechos, out bool escribible)
    {
        limite = 0; derechos = 0; escribible = false;
        var d = Sel((ushort)(sel & ~7));
        d ??= Sel(sel);
        if (d == null || !d.Presente) return false;
        limite = d.Limit;
        escribible = !d.Codigo;
        derechos = d.Codigo ? 0x00CFFA00u : 0x00CFF200u;
        return true;
    }

    /// <summary>Puntero de un servicio DOS llamado desde 32 bits: base del selector + offset.
    ///
    /// NO es `EDX` a secas. El juego pone DS a un selector con base propia (el del driver de
    /// sonido, base 0x90000) y pasa `EDX=0`; tratar EDX como dirección lineal cargaba el fichero
    /// en la dirección 0 y la llamada al driver acababa ejecutando memoria vacía. Se midió.</summary>
    uint Ptr(Cpu c, uint off) => BaseDe(c.DS) + off;

    /// <summary>Lee una cadena ASCIIZ del espacio plano.</summary>
    string LeerCadena(Memory m, uint at)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 260; i++)
        {
            byte b = m.RdLin8((uint)(at + i));
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>Resuelve una ruta DOS igual que <see cref="Int21hHandler"/>: quita la unidad,
    /// cuelga del directorio del juego y busca sin distinguir mayúsculas. Los ficheros de un
    /// juego de 1994 están en mayúsculas y el sistema de ficheros de DOS no distingue; usar otro
    /// criterio aquí haría que el mundo plano "no encontrara" lo que el de 16 bits sí encuentra.</summary>
    /// <summary>Quita el relleno de espacios de un nombre 8.3, en el nombre y en la extensión.
    ///
    /// No es cosmético: DOS lleva los nombres internamente en formato FCB, ocho caracteres para
    /// el nombre y tres para la extensión RELLENOS CON ESPACIOS, y esos espacios no forman parte
    /// del nombre. Un programa que construya el suyo así y lo pase como ASCIIZ —un juego pide
    /// literalmente <c>"name.ext    "</c>— lo encuentra en DOS y no lo encontraba aquí, porque
    /// Windows sí distingue. Se veía como "el fichero no existe" teniéndolo al lado, y el juego
    /// abortaba con <c>GAME ABORTED - The file "NAME.EXT    " has an error</c>.</summary>
    static string NombreDos(string s)
    {
        int corte = s.LastIndexOfAny(new[] { '\\', '/' });
        string dir = corte >= 0 ? s[..(corte + 1)] : "";
        string nom = corte >= 0 ? s[(corte + 1)..] : s;
        int punto = nom.IndexOf('.');
        if (punto < 0) return dir + nom.TrimEnd(' ');
        return dir + nom[..punto].TrimEnd(' ') + "." + nom[(punto + 1)..].TrimEnd(' ');
    }

    string ResolverRuta(Memory m, uint at)
    {
        string dos = NombreDos(LeerCadena(m, at)).Replace('\\', Path.DirectorySeparatorChar);
        if (dos.Length > 2 && dos[1] == ':') dos = dos[2..];
        string full = Path.Combine(_dirJuego, dos.TrimStart(Path.DirectorySeparatorChar));
        if (File.Exists(full) || Directory.Exists(full)) return full;
        string dir = Path.GetDirectoryName(full) ?? _dirJuego;
        if (!Directory.Exists(dir)) return full;
        string nombre = Path.GetFileName(full);
        foreach (string f in Directory.GetFileSystemEntries(dir))
            if (string.Equals(Path.GetFileName(f), nombre, StringComparison.OrdinalIgnoreCase))
                return f;
        return full;
    }

    // ---- búsqueda de ficheros (INT 21h AH=4E/4F) ----
    //
    // La DTA es una dirección LINEAL del espacio plano, no un seg:off: la fija AH=1A y ahí
    // escriben AH=4E/4F los 43 bytes que DOS deja por cada fichero encontrado.
    uint _dta;
    string[]? _hallazgos;
    int _hallazgo;

    /// <summary>Ficheros que casan con un patrón de DOS, ordenados. Se resuelve sobre el
    /// directorio del juego y sin distinguir mayúsculas, igual que <see cref="ResolverRuta"/>:
    /// en un disco DOS los nombres son de una sola caja y aquí llegan como los escribió el juego.</summary>
    string[] BuscarFicheros(string patron)
    {
        string dos = NombreDos(patron).Replace('\\', Path.DirectorySeparatorChar);
        if (dos.Length > 2 && dos[1] == ':') dos = dos[2..];
        dos = dos.TrimStart(Path.DirectorySeparatorChar);
        string dir = Path.Combine(_dirJuego, Path.GetDirectoryName(dos) ?? "");
        string mascara = Path.GetFileName(dos);
        if (mascara.Length == 0 || !Directory.Exists(dir)) return Array.Empty<string>();
        try
        {
            var v = Directory.GetFiles(dir, mascara);
            Array.Sort(v, StringComparer.OrdinalIgnoreCase);
            return v;
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Deja en la DTA el registro de 43 bytes de DOS: 21 reservados para la búsqueda,
    /// atributo, hora, fecha, tamaño y el nombre 8.3 terminado en cero.</summary>
    void EscribirDta(Memory m, string ruta)
    {
        if (_dta == 0) return;
        var fi = new FileInfo(ruta);
        for (uint i = 0; i < 0x15; i++) m.WrLin8(_dta + i, 0);        // reservado para el sistema
        m.WrLin8(_dta + 0x15, 0x20);                                  // atributo: archivo normal
        var t = fi.LastWriteTime;
        m.WrLin16(_dta + 0x16, (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)));
        m.WrLin16(_dta + 0x18, (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
        m.WrLin32(_dta + 0x1A, (uint)Math.Min(fi.Length, uint.MaxValue));
        string nom = fi.Name.ToUpperInvariant();
        for (int i = 0; i < 13; i++)
            m.WrLin8((uint)(_dta + 0x1E + i), i < nom.Length ? (byte)nom[i] : (byte)0);
    }

    public Descriptor? Sel(ushort s) => _desc.TryGetValue(s, out var d) ? d : null;

    /// <summary>Base de un selector. Los desconocidos valen 0 (modelo plano) en vez de fallar:
    /// un selector que el juego se inventa apuntando al espacio plano es lo normal aquí.</summary>
    public uint BaseDe(ushort s) => _desc.TryGetValue(s, out var d) ? d.Base
                                   : (_segmentosDeModoReal ? (uint)(s << 4) : 0u);

    /// <summary>Mientras se simula una interrupción de modo real (DPMI 0300h) los segmentos que
    /// trae la estructura del cliente son de MODO REAL. Ver el case 0x0300.</summary>
    bool _segmentosDeModoReal;

    /// <summary>Base de un selector en el mundo de 16 BITS (juegos con extensor embebido). A
    /// diferencia del mundo plano, aquí un selector desconocido NO vale 0: vale lo que valdría en
    /// modo real, <c>valor×16</c>. Es lo que hace que el cambio de modo no mueva nada del mundo
    /// del cliente — sólo los descriptores que él se fabrique por <c>INT 31h</c> tienen base
    /// propia.</summary>
    public uint BaseDe16(ushort s) => _desc.TryGetValue(s, out var d) ? d.Base : (uint)(s << 4);

    /// <summary>¿Es <paramref name="s"/> un selector de CÓDIGO DE 32 BITS? Un selector que no está
    /// en la tabla no lo es (es un segmento de modo real). Con esto el intérprete de 16 sabe
    /// cuándo el mundo ha pasado a 32 y tiene que ceder el control a <see cref="Interp32"/>.</summary>
    public bool EsCodigo32(ushort s) => _desc.TryGetValue(s, out var d) && d.Ancho32 && d.Codigo;

    /// <summary>Servicio de interrupciones hardware para <see cref="Interp32"/>: adelanta el
    /// temporizador y, si el PIC tiene una IRQ pendiente Y el juego tiene manejador para su
    /// vector, la entrega. Espejo de DosPlatform.ServicePendingInterrupts, que es lo que hace el
    /// mundo de 16 bits — mismo PIC y mismo temporizador, que el hardware es uno solo.
    ///
    /// Si el juego NO tiene manejador para ese vector se descarta la IRQ (se le hace el ACK igual):
    /// saltar a un vector vacio seria peor que no entregarla.</summary>
    readonly System.Diagnostics.Stopwatch _reloj = new();
    double _msDelUltimoTick;
    long _instrDelUltimoTick;

    /// <summary>PAKU_IRQ_DET=N: el temporizador deja de ir por el reloj de PARED y pasa a ir por
    /// el contador de instrucciones del invitado — un tick de IRQ0 cada N instrucciones.
    ///
    /// No es una opcion de rendimiento, es una herramienta de diagnostico, y la unica que hace
    /// bisecable un fallo intermitente: con el reloj de pared la IRQ cae en una instruccion
    /// distinta en cada ejecucion, asi que dos trazas del MISMO binario no se pueden comparar y el
    /// reventon aparece en un sitio distinto cada vez (medido en un juego DOS/4GW: 19/40/20 IRQ y tres
    /// direcciones sin relacion). Con este reloj, dos ejecuciones son la misma ejecucion.
    ///
    /// N son las instrucciones por MILISEGUNDO virtual, no por tick: el periodo del tick sale de
    /// la frecuencia que el juego le haya programado al PIT. Tiene que ser asi para que el reloj
    /// del temporizador y el del audio —que cuenta en milisegundos, `Cpu.CyclesPerMs`— vayan
    /// acompasados; con un numero fijo de instrucciones por tick, un juego que baja su PIT a
    /// 18,2 Hz veia pasar el tiempo 3 veces mas despacio de lo que le sonaba la tarjeta.
    /// Valor razonable: el `cyclesPerMs` habitual de los juegos probados, 3000.</summary>
    public static readonly long IrqDeterminista =
        long.TryParse(Environment.GetEnvironmentVariable("PAKU_IRQ_DET"), out var _det) && _det > 0 ? _det : 0;

    /// <summary>PAKU_IRQ_MAX=K: entrega sólo las K primeras IRQ y a partir de ahí ninguna (se les
    /// hace el ACK igual). Es la bisección: con el reloj determinista, si con K no revienta y con
    /// K+1 sí, la IRQ culpable es la K+1 y se sabe la instrucción exacta en la que se entregó.</summary>
    public static readonly long IrqMax =
        long.TryParse(Environment.GetEnvironmentVariable("PAKU_IRQ_MAX"), out var _max) && _max >= 0 ? _max : -1;

    /// <summary>PAKU_LOG_IRQ32=1: cada IRQ entregada con su vector, dónde se interrumpió y con qué
    /// pila. Sin esto, "revienta en la IRQ 17" no dice en qué estaba el juego cuando le entró.</summary>
    public static readonly bool LogIrq = Environment.GetEnvironmentVariable("PAKU_LOG_IRQ32") == "1";

    /// <summary>La hora que ve el juego. Con el reloj determinista está CONGELADA: un juego de prueba pide
    /// `AH=2C` al arrancar y siembra con ella su generador aleatorio, así que la hora de pared
    /// hacía que dos ejecuciones del mismo binario tomaran caminos distintos — medido, era la
    /// segunda fuente de no determinismo después del temporizador.</summary>
    /// <summary>PAKU_HORA_FIJA=1: congela la hora SIN tocar la fuente del temporizador. Hace falta
    /// para medir con reloj de pared un juego cuya pantalla de protección se siembra con la hora
    /// (medido en un juego de prueba): sin esto, la única forma de precalcular la respuesta y automatizar el paso era
    /// PAKU_IRQ_DET, que además cambia el reloj y por tanto lo que se quería medir.</summary>
    static readonly bool HoraFija = Environment.GetEnvironmentVariable("PAKU_HORA_FIJA") == "1";

    /// <summary>PAKU_SB_RELOJ_INVITADO=1: devuelve el DMA de la tarjeta al reloj del invitado, que
    /// es como estaba antes. Es el CONTROL del A/B de los microcortes: sin poder medir el
    /// comportamiento viejo en las mismas condiciones, "ahora suena bien" es una impresión.</summary>
    static readonly bool RelojInvitadoSb =
        Environment.GetEnvironmentVariable("PAKU_SB_RELOJ_INVITADO") == "1";

    static DateTime Ahora(Cpu c) =>
        IrqDeterminista > 0 || HoraFija ? GuestClock.Epoch : GuestClock.Now(c.CyclesPerMs);

    public bool ServicioIrq(Cpu c, Memory m, out ushort selector, out uint offset)
    {
        selector = 0; offset = 0;
        var p = Plataforma;
        if (p == null) return false;
        p.Cmos.CyclesPerMs = c.CyclesPerMs;   // the flat world has its own clock, not MachineTimer.Poll

        // Reloj PROPIO, no `MachineTimer.Poll`: aquel DISPARA las ISR el mismo por el camino de
        // 16 bits (FireInterrupt), asi que se comia la IRQ antes de que llegara aqui y el mundo
        // plano no veia ninguna. Aqui solo se LEVANTAN en el PIC y el interprete las entrega.
        double hz = p.Pit.Channel0FrequencyHz;
        if (IrqDeterminista > 0)
        {
            // El PIT tiene que estar programado igual: si el juego no lo ha tocado todavia, no hay
            // temporizador que valga. Lo que cambia es la FUENTE DE TIEMPO, no el modelo.
            double porTick = IrqDeterminista * 1000.0 / hz;
            if (hz > 0 && hz < 20000
                && Interp32.Ejecutadas - _instrDelUltimoTick >= porTick)
            {
                _instrDelUltimoTick = Interp32.Ejecutadas;
                p.Pic.RaiseIrq(0);
            }
        }
        else
        {
            if (!_reloj.IsRunning) { _reloj.Start(); _msDelUltimoTick = 0; }
            double ahora = _reloj.Elapsed.TotalMilliseconds;
            if (hz > 0 && hz < 20000)
            {
                double periodo = 1000.0 / hz;
                int vencidos = (int)((ahora - _msDelUltimoTick) / periodo);
                if (vencidos > 0)
                {
                    if (vencidos > 8) vencidos = 8;      // no acumular una avalancha
                    _msDelUltimoTick += vencidos * periodo;
                    p.Pic.RaiseIrq(0);
                }
            }
        }

        // Sonido: la Sound Blaster levanta su IRQ al terminar cada bloque de DMA, y el driver de
        // HMI la espera para encadenar el siguiente. Sin esto el juego se queda plantado en su
        // logo con el sonido a medias — medido: 16.907 IRQ de temporizador entregadas y CERO del
        // vector 0Fh, que es el que engancha. Solo se LEVANTA; la entrega es del interprete.
        // La tarjeta consume su bufer con el reloj de PARED en este mundo: aqui no hay governor
        // que mantenga el tiempo virtual pegado al real, asi que contarlo en instrucciones dejaba
        // el audio seco en cuanto se pedia mas velocidad de la que alcanza el interprete. Con el
        // reloj determinista se vuelve al del invitado, que es lo que lo hace reproducible.
        if (p.Sb != null)
        {
            p.Sb.DmaEnTiempoReal = IrqDeterminista == 0 && !RelojInvitadoSb;
            p.Sb.LevantarIrqSiVence(p, c);
        }

        // Teclado: los scancodes que el host encolo pasan al 8042, que levanta IRQ1 por cada
        // byte ENTREGADO (su propio hook), igual que en el mundo de 16 bits.
        p.Keyboard.PicRaiseIrq1 ??= () => p.Pic.RaiseIrq(1);
        while (p.Keyboard.TryDequeueHostScancode(out byte sc)) p.Keyboard.PushScancode(sc);

        while (p.Pic.AckNext(out int irq))
        {
            byte vec = p.Pic.IrqToVector(irq);
            var h = _vecPm[vec];
            if (h.off == StubIretd) continue;   // solo el `iretd` por defecto: se descarta
            if (IrqMax >= 0 && IrqEntregadas >= IrqMax) continue;   // biseccion: ACK sin entregar
            selector = h.sel; offset = h.off;
            IrqEntregadas++;
            // El punto exacto en el que se interrumpio lo imprime el interprete, que es quien
            // tiene el EIP vivo (aqui `c.EIP` va por detras: el interprete lleva el suyo local).
            if (LogIrq)
                Console.Error.WriteLine(
                    $"[IRQ32] #{IrqEntregadas} irq{irq} vec={vec:X2}h -> {h.sel:X4}:{h.off:X8}  " +
                    $"ESP=0x{c.ESP:X8} instr={Interp32.Ejecutadas}");
            return true;
        }
        return false;
    }

    /// <summary>Telemetria: IRQ entregadas al juego. Si sale cero cuando el juego engancha el
    /// timer, es que el temporizador no esta avanzando.</summary>
    public long IrqEntregadas;

    /// <summary>Cadenas ya sacadas por stderr con INT 21h AH=09. Con tope, para que un programa
    /// que escriba en bucle no ahogue el log.</summary>
    int _impresas;

    // ================================================================
    public bool Int(byte no, Cpu c, Memory m, ref uint eip)
    {
        switch (no)
        {
            case 0x21: return Int21(c, m);
            case 0x31: return Int31(c, m);
        }
        // El resto del BIOS (vídeo, teclado, ratón, reloj) YA está implementado y validado en el
        // mundo de 16 bits: se delega en la misma plataforma en vez de escribir un segundo vídeo.
        // El hardware es uno solo — lo mismo que ya se hace con los puertos en Interp32.
        //
        // CAVEAT dicho claro: los servicios que reciben PUNTEROS los esperan como seg:off, y aquí
        // llegan planos. Los que no llevan puntero (fijar modo, leer tecla, mover el ratón) son
        // exactos; los que sí (cargar una fuente, escribir una cadena) harían falta con buffer de
        // transferencia y hoy NO están puenteados — se verán como divergencia, no como error.
        if (c.Platform == null)
            return Rechazar($"INT {no:X2}h AX={c.AX:X4}", "no hay plataforma conectada");
        Traza.Add($"INT {no:X2}h AX={c.AX:X4} BX={c.BX:X4} CX={c.CX:X4} DX={c.DX:X4} -> plataforma");
        c.Platform.Int(no, c, m);
        return true;
    }

    bool Rechazar(string que, string porque)
    {
        Rechazados.Add($"{que} — {porque}");
        return false;
    }

    /// <summary>OPT-IN por juego (`game.json "apiExtensor": true`). Un extensor EMBEBIDO no habla
    /// DPMI con su cliente: le ofrece su propia API por <c>INT 21h</c>, y aunque los números
    /// parezcan los de DOS no lo son. Va con interruptor porque COLISIONAN: un juego DOS/4GW corre
    /// sobre DOS/4GW y usa el <c>AH=25</c> de toda la vida por este mismo camino.</summary>
    public bool ApiExtensor
    {
        get => _apiExtensor && Environment.GetEnvironmentVariable("PAKU_API_EXTENSOR") != "0";
        set => _apiExtensor = value;
    }
    bool _apiExtensor;

    /// <summary>La API propietaria de un extensor DOS embebido en un juego de prueba
    /// (su propio banner de copyright). Nada de esto se dedujo: está leído en el binario
    /// del juego ya descifrado.
    ///
    /// Lo decisivo, y lo que se estaba haciendo mal: **el número de interrupción va en CL**, no
    /// en AL — AL distingue la operación. Servido como el <c>AH=25</c> de DOS, las ISR del juego
    /// se registraban en los vectores 03h/05h/06h en vez de en el 23h/24h/**08h (timer)**/
    /// **09h (teclado)**, así que ni el reloj ni las teclas llegaban a su código.
    ///
    /// Devuelve false para lo que no reconoce, y entonces cae al camino de DOS de siempre.</summary>
    bool Int21Extensor(Cpu c, Memory m)
    {
        switch (c.AX)
        {
            case 0x2503:   // leer el manejador de CL
                // El juego lo guarda tal cual para restaurarlo luego con AX=2505, así que el
                // formato sólo tiene que ser el MISMO en las dos: seg:off empaquetado en EBX,
                // que es como el propio juego construye los suyos
                // (`mov ebx,[0x533] / shl ebx,16 / mov bx,6`).
                c.EBX = ((uint)_vecReal[c.CL].seg << 16) | _vecReal[c.CL].off;
                Traza.Add($"  [ext] leer vector {c.CL:X2}h -> {c.EBX:X8}");
                c.CF = false; return true;

            case 0x2505:   // instalar el manejador de MODO REAL de CL (EBX = seg:off)
                _vecReal[c.CL] = ((ushort)(c.EBX >> 16), c.EBX & 0xFFFF);
                Traza.Add($"  [ext] instalar vector REAL {c.CL:X2}h = {c.EBX >> 16:X4}:{c.EBX & 0xFFFF:X4}");
                c.CF = false; return true;

            case 0x2506:   // instalar el manejador de MODO PROTEGIDO de CL (DS:EDX)
                // Éste es el que importa: por aquí engancha el juego su timer y su teclado, y es
                // el que consulta la entrega de IRQ (`_vecPm`).
                _vecPm[c.CL] = (c.DS, c.EDX);
                Traza.Add($"  [ext] instalar vector PROTEGIDO {c.CL:X2}h -> {c.DS:X4}:{c.EDX:X8}");
                c.CF = false; return true;

            case 0x350B:   // memoria libre, en BYTES
                // El juego la redondea a 8 KB (`and eax,0xffffe000`), le quita 1/512 y luego pide
                // ESO ENTERO con 350C. Antes esto caía en el "get vector" de DOS, que no toca
                // EAX: se quedaba con 0x350B y el juego se creía con OCHO KILOBYTES.
                c.EAX = MemoriaDelExtensor;
                Traza.Add($"  [ext] memoria libre -> {c.EAX:N0} bytes");
                c.CF = false; return true;

            case 0x350C:   // reservar ECX bytes -> EAX = base lineal
            {
                // Del MISMO montón que DPMI 0501, y respaldándolo con EnableFlat: sin eso las
                // escrituras del juego por encima del megabyte se descartan EN SILENCIO
                // (ver Memory.WrLin8) y el juego trabaja sobre un agujero negro.
                uint tam = c.ECX;
                uint dir = (_heap + 0xFFFu) & ~0xFFFu;
                long fin = (long)dir + tam;
                if (tam == 0 || fin > int.MaxValue)
                { c.CF = true; Rechazar($"INT 21h AX=350C ({tam:N0} bytes)", "no cabe"); return true; }
                Mem.EnableFlat((int)Math.Max(0, fin - Memory.Size));
                _heap = (uint)fin;
                // EAX va como OFFSET dentro del segmento de datos del cliente, no como dirección
                // lineal. Lo dice su propio código: guarda esto en [0x50F] y para convertirlo a
                // lineal hace `sub ecx,[0x407]`, donde [0x407] es MENOS la base de su segmento.
                // Devolviéndolo lineal, el juego le sumaba la base OTRA VEZ y acababa llamando a
                // 0x800000 por encima de donde debía — ejecutando ceros en el montón.
                c.EAX = dir - BaseDe(c.DS);
                Traza.Add($"  [ext] reservar {tam:N0} bytes -> offset 0x{c.EAX:X} (lineal 0x{dir:X})");
                c.CF = false; return true;
            }

            // ── Descriptores del extensor: 3501 crea, 3503 fija BASE, 3505 fija LÍMITE ──────
            //
            // Estaban en el saco de "aceptada sin modelar", y eso dejó a un juego 3D entero
            // dibujando con datos a cero. La secuencia, tal como la usa el programa:
            //
            //     mov ax,0x3501 / int 21h        -> BX = selector nuevo
            //     jb  error
            //     mov [0x2778],bx                 (lo guarda)
            //     mov ecx,[0x50f] / sub ecx,[0x407]   -> ECX = base LINEAL que quiere
            //     mov ax,0x3503 / int 21h        -> fija la base del selector de BX
            //     jb  error
            //     mov bx,[0x2778] / mov ecx,0x70000000
            //     mov ax,0x3505 / int 21h        -> fija el límite
            //
            // El selector va en BX (por eso lo recarga antes de 3505) y el valor en ECX entero,
            // no en CX:DX como DPMI. Aceptarlas sin tocar registros dejaba BX con basura: el
            // juego metía ESO en GS, `BaseDe16` no lo conocía y lo trataba como segmento de modo
            // real (base en el primer megabyte). El juego leía una de sus tablas por GS, así que
            // leía CEROS y dibujaba sin fallar nada más — con el color bien, porque ése va por
            // DS, que sí tiene base.
            case 0x3501:   // reservar un descriptor -> BX
            {
                ushort s = _siguienteSel; _siguienteSel += 8;
                _desc[s] = new Descriptor();
                c.BX = s;
                Traza.Add($"  [ext] reservar descriptor -> {s:X4}");
                c.CF = false; return true;
            }

            case 0x3503:   // fijar la BASE del descriptor de BX a ECX (lineal)
                (_desc.TryGetValue(c.BX, out var dB) ? dB : _desc[c.BX] = new Descriptor())
                    .Base = c.ECX;
                Traza.Add($"  [ext] base de {c.BX:X4} = 0x{c.ECX:X8}");
                c.CF = false; return true;

            case 0x3505:   // fijar el LÍMITE del descriptor de BX a ECX
                (_desc.TryGetValue(c.BX, out var dL) ? dL : _desc[c.BX] = new Descriptor())
                    .Limit = c.ECX;
                Traza.Add($"  [ext] limite de {c.BX:X4} = 0x{c.ECX:X8}");
                c.CF = false; return true;
        }
        // TODO lo demás de AH=25/35 se atiende AQUÍ, sin caer al camino de DOS. No es pereza:
        // el `AH=35` de DOS hace `ES = <selector del manejador>`, y eso MACHACA el ES del juego
        // con un selector nuestro de base 0. Un juego de prueba llama a `AX=3501/3503/3505` en su arranque
        // y luego escribe la pantalla con `stosw es:[edi]` usando un puntero que sólo vale con la
        // base de su segmento de datos: con ES pisado, el blit entero se iba a una dirección que
        // no existe y se perdía en silencio — el juego corría sin dibujar un solo píxel.
        //
        // Se acepta (CF=0) y no se toca ningún registro: lo que estas funciones hacen de verdad
        // (gestión de memoria y de estado del extensor) no está medido, y fingir un fallo manda
        // al juego a su rama de error (`jb` justo detrás de cada una).
        Traza.Add($"  [ext] AX={c.AX:X4} aceptada sin modelar (no toca registros)");
        c.CF = false;
        return true;
    }

    /// <summary>Vectores de MODO REAL vistos por la API del extensor. Aparte de los de modo
    /// protegido: el cliente guarda unos y otros y los restaura al salir.</summary>
    readonly (ushort seg, uint off)[] _vecReal = new (ushort, uint)[256];

    /// <summary>Lo que la API del extensor dice que hay libre. No es un número redondo por gusto:
    /// el cliente pide EXACTAMENTE esto (menos 1/512) de una tacada, así que es también lo que se
    /// va a reservar de verdad.</summary>
    const uint MemoriaDelExtensor = 16u << 20;

    // ================================================================
    bool Int21(Cpu c, Memory m)
    {
        Traza.Add($"INT 21h AX={c.AX:X4} EBX={c.EBX:X8} ECX={c.ECX:X8} EDX={c.EDX:X8}");
        if (ApiExtensor && (c.AH == 0x25 || c.AH == 0x35) && Int21Extensor(c, m)) return true;
        switch (c.AH)
        {
            case 0x30:
                // Versión de DOS. La mitad ALTA de EAX es la firma del extensor y va a CERO: eso
                // es lo que hace DOS/4GW, medido en el volcado del oráculo por las huellas que
                // deja cada rama (tools/le_loader/README.md §8). Con firma el juego se iría por
                // un camino de Phar Lap del que no tenemos verdad terreno ninguna.
                c.EAX = 0x00000005;      // DOS 5.0, como reportó el oráculo
                c.EBX = 0; c.ECX = 0;
                return true;

            case 0x4A:
            {
                // SETBLOCK. En el mundo plano no hay MCB que redimensionar, pero esto NO es un
                // "que si" a ciegas: es como el runtime de C de Watcom hace crecer su monton, y
                // BX (parrafos) es el TOPE que reclama. Concederlo sin mirar es lo que dejaba al
                // programa expandirse por encima de bloques DPMI ya entregados y machacarlos.
                uint topePedido = (uint)c.EBX * 16;
                if (topePedido >= BaseDelMontonDpmi)
                {
                    // Sin sitio. Se contesta como DOS —CF, error 8 y el mayor tamaño posible en
                    // BX— en vez de conceder y corromper: el runtime sabe encajar un "no".
                    c.CF = true; c.AX = 0x0008;
                    c.EBX = BaseDelMontonDpmi / 16;
                    return true;
                }
                if (topePedido > TopeDelPrograma)
                {
                    TopeDelPrograma = topePedido;
                    // Que la memoria EXISTA: si no, sus escrituras se pierden y sus lecturas dan
                    // 0xFF, que es como la paleta acabo entera en (63,63,63) en un juego de prueba.
                    Mem.EnableFlat((int)Math.Max(0, (long)topePedido - Memory.Size));
                }
                c.CF = false;
                return true;
            }

            // Fijar/leer vector de interrupcion. ANTES SE TIRABAN A LA BASURA: `AH=25` decia que
            // si y no guardaba nada, y `AH=35` devolvia siempre cero. El juego engancha asi el
            // timer (se ve `AX=3508`/`AX=2508` en la traza), de modo que su manejador quedaba en
            // el limbo. Van a la IVT de verdad, la del primer megabyte, que es la MISMA que ve el
            // mundo de 16 bits.
            case 0x25:
                _vecPm[c.AL] = (c.DS, c.EDX);
                Traza.Add($"  engancha el vector {c.AL:X2}h -> {c.DS:X4}:{c.EDX:X8}");
                c.CF = false; return true;
            case 0x35:
                c.ES = _vecPm[c.AL].sel; c.EBX = _vecPm[c.AL].off;
                c.CF = false; return true;

            // ---- fecha y hora: el arranque de la biblioteca de C las pide siempre ----
            case 0x2A:
            {
                var t = Ahora(c);
                c.CX = (ushort)t.Year; c.DH = (byte)t.Month; c.DL = (byte)t.Day;
                c.AL = (byte)t.DayOfWeek;
                c.CF = false; return true;
            }
            case 0x2C:
            {
                var t = Ahora(c);
                c.CH = (byte)t.Hour; c.CL = (byte)t.Minute;
                c.DH = (byte)t.Second; c.DL = (byte)(t.Millisecond / 10);
                c.CF = false; return true;
            }
            case 0x2B: case 0x2D: c.AL = 0; c.CF = false; return true;   // fijar fecha/hora: se acepta

            // ---- entrada por consola ----
            // La sonda no tiene teclado. Se SINTETIZA la tecla y se deja dicho en la traza: sin
            // esto el juego se para en su "pulsa una tecla" y no se puede ver qué hace después.
            case 0x08: case 0x07:
                c.AL = TeclaSintetica;
                Traza.Add($"  el juego espera una tecla -> se le da 0x{TeclaSintetica:X2} (SINTETICA)");
                c.CF = false; return true;
            case 0x0B: c.AL = 0xFF; c.CF = false; return true;   // hay carácter disponible
            case 0x01: case 0x06:
                c.AL = TeclaSintetica; c.CF = false; return true;
            case 0x0C: c.AL = 0; c.CF = false; return true;      // vaciar buffer y leer

            // ---- entorno del proceso ----
            case 0x19: c.AL = 2; c.CF = false; return true;               // unidad actual = C:
            case 0x0E: c.AL = 26; c.CF = false; return true;              // elegir unidad
            case 0x36:   // espacio libre en disco (dos.cpp DOS_GetFreeDiskSpace)
                // Los MISMOS números que contesta el mundo de 16 bits (Int21hHandler AH=36):
                // 64 sectores por cluster, 512 bytes por sector, 0x3000 clusters libres de
                // 0x4000 = ~384 MiB libres. Faltaba aquí, y un juego de prueba lo pide nada más arrancar
                // para avisar de si vas a poder guardar la partida ("Drive %c has only %lu bytes
                // available" / "Error Checking Drive %c! Run SCANDISK."); sin respuesta se
                // quedaba clavado en el INT 21h.
                c.AX = 0x0040; c.BX = 0x3000; c.CX = 0x0200; c.DX = 0x4000;
                c.CF = false; return true;
            case 0x47: c.CF = false; c.AL = 0; return true;               // directorio actual (raíz)
            case 0x1A:                                                    // fijar DTA
                // Hay que GUARDARLA: es donde AH=4E/4F dejan lo que encuentran. Aceptar la
                // llamada sin apuntarla dejaba la búsqueda de ficheros sin sitio donde escribir.
                _dta = Ptr(c, c.EDX);
                c.CF = false; return true;

            case 0x4E:   // buscar el primero
            {
                string patron = LeerCadena(m, Ptr(c, c.EDX));
                _hallazgos = BuscarFicheros(patron);
                _hallazgo = 0;
                Traza.Add($"  buscar \"{patron}\" -> {_hallazgos.Length} coincidencia(s)");
                if (_hallazgos.Length == 0) { c.EAX = 2; c.CF = true; return true; }   // no encontrado
                EscribirDta(m, _hallazgos[_hallazgo++]);
                c.EAX = 0; c.CF = false; return true;
            }
            case 0x4F:   // buscar el siguiente
            {
                if (_hallazgos == null || _hallazgo >= _hallazgos.Length)
                { c.EAX = 18; c.CF = true; return true; }                 // no hay más ficheros
                EscribirDta(m, _hallazgos[_hallazgo++]);
                c.EAX = 0; c.CF = false; return true;
            }
            case 0x33: c.DL = 0; c.CF = false; return true;               // estado de BREAK
            case 0x62: c.EBX = 0; c.CF = false; return true;              // segmento del PSP

            // ---- ficheros. El puntero al nombre viene en EDX y es una dirección LINEAL ----
            case 0x3D:   // abrir
            {
                string p = ResolverRuta(m, Ptr(c, c.EDX));
                try
                {
                    int modo = SoloLectura ? 0 : (c.AL & 3);
                    var fs = new FileStream(p,
                        FileMode.Open,
                        modo == 0 ? FileAccess.Read : modo == 1 ? FileAccess.Write : FileAccess.ReadWrite,
                        FileShare.ReadWrite);
                    int h = AsignarHandleFichero();
                    _ficheros[h] = fs;
                    c.EAX = (uint)h; c.CF = false;
                    Traza.Add($"  abrir \"{Path.GetFileName(p)}\" -> handle {h}");
                }
                catch
                {
                    c.EAX = 2; c.CF = true;          // fichero no encontrado
                    Traza.Add($"  abrir \"{p}\" -> NO EXISTE");
                    NoEncontrados.Add(Path.GetFileName(p));
                }
                return true;
            }
            case 0x3C:   // crear
            {
                string p = ResolverRuta(m, Ptr(c, c.EDX));
                if (SoloLectura)
                {
                    Traza.Add($"  CREAR \"{Path.GetFileName(p)}\" DENEGADO (sonda en solo lectura)");
                    c.EAX = 5; c.CF = true;   // acceso denegado
                    return true;
                }
                try
                {
                    var fs = new FileStream(p, FileMode.Create, FileAccess.ReadWrite);
                    int h = AsignarHandleFichero();
                    _ficheros[h] = fs;
                    c.EAX = (uint)h; c.CF = false;
                }
                catch { c.EAX = 3; c.CF = true; }
                return true;
            }
            case 0x3E:   // cerrar
                if (_ficheros.Remove((int)c.BX, out var cerrar)) { cerrar.Dispose(); c.CF = false; }
                else c.CF = true;
                return true;
            case 0x3F:   // leer
            {
                if (!_ficheros.TryGetValue(c.BX, out var fr)) { c.EAX = 6; c.CF = true; return true; }
                var buf = new byte[c.CX];
                long posAntes = fr.Position;
                int leidos = fr.Read(buf, 0, buf.Length);
                if (TrazaFicheros)
                    Traza.Add($"  h{c.BX} leer {c.CX} desde 0x{posAntes:X} -> {leidos} bytes");
                uint dst = Ptr(c, c.EDX);
                for (int i = 0; i < leidos; i++) m.WrLin8((uint)(dst + i), buf[i]);
                if (leidos > 0x40)
                    Traza.Add($"  leer {leidos} bytes de h{c.BX} -> DS={c.DS:X4}(base 0x{BaseDe(c.DS):X}) + 0x{c.EDX:X} = 0x{dst:X}");
                c.EAX = (uint)leidos; c.CF = false;
                return true;
            }
            case 0x40:   // escribir
            {
                if (!_ficheros.TryGetValue(c.BX, out var fw))
                {
                    // handles 1 y 2 = salida estándar y de error: al log, no a un fichero
                    if (c.BX is 1 or 2)
                    {
                        var sb = new System.Text.StringBuilder();
                        var hex = new System.Text.StringBuilder();
                        uint src0 = Ptr(c, c.EDX);
                        for (int i = 0; i < c.CX; i++)
                        {
                            byte b = m.RdLin8((uint)(src0 + i));
                            sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                            hex.Append($"{b:X2} ");
                        }
                        Traza.Add($"  el juego escribe en 0x{src0:X}: \"{sb}\"");
                        Traza.Add($"     hex: {hex}");
                        c.EAX = c.CX; c.CF = false;
                        return true;
                    }
                    c.EAX = 6; c.CF = true; return true;
                }
                if (SoloLectura)
                {
                    Traza.Add($"  ESCRIBIR {c.CX} bytes en h{c.BX} DENEGADO (sonda en solo lectura)");
                    c.EAX = c.CX; c.CF = false;   // se le dice que fue bien, pero el disco no se toca
                    return true;
                }
                var buf = new byte[c.CX];
                uint src = Ptr(c, c.EDX);
                for (int i = 0; i < buf.Length; i++) buf[i] = m.RdLin8((uint)(src + i));
                fw.Write(buf, 0, buf.Length);
                c.EAX = c.CX; c.CF = false;
                return true;
            }
            case 0x42:   // mover el puntero
            {
                if (!_ficheros.TryGetValue(c.BX, out var fq)) { c.EAX = 6; c.CF = true; return true; }
                long off = (int)((c.CX << 16) | c.DX);
                long antes = fq.Position;
                long pos = fq.Seek(off, c.AL switch { 1 => SeekOrigin.Current, 2 => SeekOrigin.End, _ => SeekOrigin.Begin });
                if (TrazaFicheros)
                    Traza.Add($"  h{c.BX} seek AL={c.AL} off={off} : 0x{antes:X} -> 0x{pos:X}");
                c.EAX = (uint)pos; c.EDX = (uint)(pos >> 16);
                c.CF = false;
                return true;
            }
            case 0x09:
            {
                // Escribir una cadena terminada en '$'. Es por donde un programa de 32 bits dice
                // lo que le pasa: sin esto, un juego que aborta con un mensaje se ve como un
                // "INT 21h sin servicio" mudo. La cadena va a la traza Y a stderr, igual que ya
                // hace AH=40 con los handles 1 y 2.
                uint p = Ptr(c, c.EDX);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 1024; i++)
                {
                    byte b = m.RdLin8((uint)(p + i));
                    if (b == (byte)'$') break;
                    sb.Append(b == 13 ? ' ' : b == 10 ? ' ' : b >= 32 && b < 127 ? (char)b : '.');
                }
                Traza.Add($"  el juego imprime: \"{sb}\"");
                if (_impresas++ < 64) Console.Error.WriteLine($"[DOS32] {sb}");
                c.EAX = (c.EAX & 0xFFFFFF00u) | 0x24;   // AL='$', lo que devuelve DOS
                c.CF = false;
                return true;
            }
            case 0x41: c.CF = false; return true;    // borrar: se acepta sin borrar nada
            case 0x43: c.CX = 0x20; c.CF = false; return true;   // atributos: archivo normal
            case 0x44:
                // IOCTL "leer información del dispositivo". El resultado va en DX, NO en AX, y
                // para un fichero de disco el bit 7 va a CERO: ponerlo lo declara dispositivo de
                // caracteres (una consola) y el runtime deja de tratarlo como fichero.
                c.DX = 0x0002;          // fichero en la unidad C
                c.EAX = 0; c.CF = false;
                return true;

            case 0x4C:
                Traza.Add($"  -> el juego pide SALIR con código {c.AL}");
                return false;                        // que el llamante decida

            case 0xFF:   // AX=FF00: API propietaria de DOS/4G
            case 0xED:   // AH=EDh: idem
                // Rechazo DELIBERADO, con la rama alternativa leída en el binario antes de
                // decidirlo. Devolver AL=0 es lo que ve el runtime cuando no hay DOS/4G debajo.
                c.AL = 0;
                Rechazar($"INT 21h AX={c.AX:X4}", "propietaria de DOS/4G; el runtime tiene rama alternativa");
                return true;
        }
        return Rechazar($"INT 21h AH={c.AH:X2}", "servicio DOS no implementado todavía en el mundo plano");
    }

    /// <summary>El `INT 31h` visto desde el mundo de 16 bits (lo registra
    /// <see cref="DosPlatform"/>). Mismo servicio, misma tabla de descriptores: un cliente DPMI
    /// pide lo mismo venga del mundo que venga.</summary>
    public void Int31Publico(Cpu c, Memory m)
    {
        ushort ax = c.AX, bx = c.BX, cx = c.CX, dx = c.DX;
        Int31(c, m);
        if (LogDpmi)
            Console.Error.WriteLine($"[DPMI31] AX={ax:X4} BX={bx:X4} CX={cx:X4} DX={dx:X4}" +
                $"  ->  AX={c.AX:X4} BX={c.BX:X4} CX={c.CX:X4} DX={c.DX:X4} CF={(c.CF ? 1 : 0)}");
    }

    /// <summary>PAKU_LOG_DPMI=1: cada servicio DPMI con lo que entra y lo que sale. Un cliente que
    /// se rinde tras una llamada fallida no dice cuál era: sin esto no hay forma de saberlo.</summary>
    public static readonly bool LogDpmi = Environment.GetEnvironmentVariable("PAKU_LOG_DPMI") == "1";

    // ================================================================
    bool Int31(Cpu c, Memory m)
    {
        Traza.Add($"INT 31h AX={c.AX:X4} EBX={c.EBX:X8} ECX={c.ECX:X8} EDX={c.EDX:X8} ESI={c.ESI:X8} EDI={c.EDI:X8}");
        switch (c.AX)
        {
            case 0x0000:   // reservar descriptores
            {
                int n = c.CX == 0 ? 1 : c.CX;
                ushort primero = _siguienteSel;
                for (int i = 0; i < n; i++) { _desc[_siguienteSel] = new Descriptor(); _siguienteSel += 8; }
                c.AX = primero; c.CF = false;
                return true;
            }
            case 0x0001:   // liberar descriptor
                _desc.Remove(c.BX); c.CF = false; return true;

            case 0x0400:   // versión del host DPMI
                // Los MISMOS valores que la comprobación de instalación de INT 2Fh AX=1687h
                // (BiosInterrupts): un cliente que pregunte por las dos vías tiene que ver lo
                // mismo, y hay runtimes que comparan. AH=mayor, AL=menor.
                c.AX = 0x005A;   // 0.90
                c.BX = 0x0001;   // bit0: 386+ (clientes de 32 bits). bit1=0 → las interrupciones
                                 // se reflejan en modo virtual, coherente con decir "Windows 3.x"
                                 // en INT 2Fh AX=1600. bit2=0: sin memoria virtual.
                c.CL = 0x03;     // tipo de CPU: 80386
                c.DH = 0x08; c.DL = 0x70;   // bases de los dos PIC, las de un PC/AT
                c.CF = false;
                return true;

            case 0x0306:   // direcciones del cambio de modo DIRECTO (raw mode switch)
                // SE OFRECE. Antes no, con el argumento de que el contrato de registros de esas
                // rutinas no se podia inventar — cierto, pero ya no hay que inventarlo: esta
                // MEDIDO en el propio cliente (un juego de prueba, 08A2:14D7 y 08A2:48A3) y es el camino
                // por el que el extensor entrega el control a su juego. Por el otro, el de
                // 0300, se rinde y sale con AH=4C. La escotilla queda al reves para el A/B:
                // PAKU_DPMI_RAW=0 lo retira. Solo lo ven los juegos con `dpmiHost`.
                if (Environment.GetEnvironmentVariable("PAKU_DPMI_RAW") == "0")
                { c.CF = true; return true; }
                goto case 0x0305;

            case 0x0305:   // direcciones de GUARDAR/RESTAURAR estado de la tarea
                // Un extensor pide estas cuatro direcciones para poder cruzar entre modo real y
                // protegido sin pasar por el host en cada salto. Un extensor probado se RINDE si 0306 falla
                // (medido: pide 0306, le decimos que no, y su siguiente servicio es un INT 21h
                // AH=4C de salida). Aqui los dos modos comparten el mismo estado de CPU, asi que
                // los cuatro puntos de entrada son `RETF` y eso es EXACTO, no un apaño.
            {
                bool raw = c.AX == 0x0306;
                c.AX = 0;                       // bytes de buffer que hacen falta: ninguno
                // BX:CX = procedimiento del lado REAL; SI:DI = el del lado PROTEGIDO.
                c.BX = Cpu.IvtStubSeg;
                c.CX = raw ? Cpu.DpmiRawSwitchRealOff : Cpu.DpmiSaveRealOff;
                c.SI = Cpu.IvtStubSeg;
                c.DI = raw ? Cpu.DpmiRawSwitchPmOff : Cpu.DpmiSavePmOff;
                c.CF = false;
                return true;
            }

            case 0x0003:   // incremento entre selectores consecutivos
                // NO es cosmético. Un cliente pide N descriptores con AX=0000, recibe el PRIMERO y
                // deduce los otros N-1 sumando ESTE número. Devolver AX sin tocar (0x0003) hacía
                // que un juego de prueba creyera que sus cuatro descriptores eran 48,4B,4E,51 cuando los
                // nuestros son 48,50,58,60: a partir de ahí ponía bases y derechos en selectores
                // que no existen, y todo lo que venía detrás operaba sobre humo. Un descriptor
                // ocupa 8 bytes en la LDT y por eso el incremento es 8 en cualquier host real.
                c.AX = 8; c.CF = false; return true;

            case 0x0006:   // leer base de un selector -> CX:DX
            {
                uint b = BaseDe(c.BX);
                c.CX = (ushort)(b >> 16); c.DX = (ushort)b; c.CF = false;
                return true;
            }
            case 0x0007:   // fijar base
                (_desc.TryGetValue(c.BX, out var d7) ? d7 : _desc[c.BX] = new Descriptor())
                    .Base = (uint)((c.CX << 16) | c.DX);
                c.CF = false; return true;

            case 0x0008:   // fijar límite
                (_desc.TryGetValue(c.BX, out var d8) ? d8 : _desc[c.BX] = new Descriptor())
                    .Limit = (uint)((c.CX << 16) | c.DX);
                c.CF = false; return true;

            case 0x0002:   // segmento de modo real -> selector (base = seg*16, límite 64 KB)
            {
                ushort s = _siguienteSel; _siguienteSel += 8;
                _desc[s] = new Descriptor { Base = (uint)(c.BX * 16), Limit = 0xFFFF };
                c.AX = s; c.CF = false; return true;
            }
            case 0x0009:   // fijar derechos de acceso de un descriptor
            {
                var d9 = Sel(c.BX) ?? (_desc[c.BX] = new Descriptor());
                d9.Presente = (c.CX & 0x80) != 0;
                d9.Codigo = (c.CX & 0x08) != 0;
                d9.Ancho32 = (c.CX & 0x4000) != 0;   // CH bit 6 = D/B

                c.CF = false; return true;
            }
            case 0x000B:   // leer descriptor -> 8 bytes en ES:EDI
            {
                var db = Sel(c.BX) ?? new Descriptor { Presente = false };
                uint at = BaseDe(c.ES) + c.EDI;
                uint lim = db.Limit >= 0xFFFFF ? db.Limit >> 12 : db.Limit;
                bool granPagina = db.Limit >= 0xFFFFF;
                m.WrLin16(at, (ushort)lim);
                m.WrLin16(at + 2, (ushort)db.Base);
                m.WrLin8(at + 4, (byte)(db.Base >> 16));
                m.WrLin8(at + 5, (byte)((db.Presente ? 0x80 : 0) | 0x70 | (db.Codigo ? 0x18 : 0x10) | 0x02));
                m.WrLin8(at + 6, (byte)(((lim >> 16) & 0x0F) | (granPagina ? 0x80 : 0) | 0x40));
                m.WrLin8(at + 7, (byte)(db.Base >> 24));
                c.CF = false; return true;
            }
            case 0x000C:   // fijar descriptor desde los 8 bytes de ES:EDI
            {
                uint at = BaseDe(c.ES) + c.EDI;
                uint lim = m.RdLin16(at) | ((uint)(m.RdLin8(at + 6) & 0x0F) << 16);
                uint bas = m.RdLin16(at + 2) | ((uint)m.RdLin8(at + 4) << 16) | ((uint)m.RdLin8(at + 7) << 24);
                byte acceso = m.RdLin8(at + 5);
                byte flags = m.RdLin8(at + 6);
                if ((flags & 0x80) != 0) lim = (lim << 12) | 0xFFF;   // granularidad de página
                var dc = Sel(c.BX) ?? (_desc[c.BX] = new Descriptor());
                dc.Base = bas; dc.Limit = lim;
                dc.Presente = (acceso & 0x80) != 0;
                dc.Codigo = (acceso & 0x08) != 0;
                c.CF = false; return true;
            }

            case 0x000A:   // crear alias de datos de un selector de código
            {
                // La base del original NO puede salir de `new Descriptor()` cuando el selector no
                // está en la tabla: en el mundo de 16 bits un selector desconocido es un segmento
                // de modo real, y su base es `valor×16`. Con base 0, el alias que un juego de prueba crea
                // sobre su propio segmento de código apuntaba a la MEMORIA BAJA, y todo lo que
                // escribía por él —sus tablas de despacho— acababa machacando la IVT en vez de su
                // código. El síntoma era un salto a `0000:0054` a ejecutar basura, a un montón de
                // instrucciones de distancia de la causa.
                var orig = Sel(c.BX);
                uint baseOrig = orig?.Base ?? BaseDe16(c.BX);
                uint limOrig = orig?.Limit ?? 0xFFFF;
                ushort s = _siguienteSel; _siguienteSel += 8;
                _desc[s] = new Descriptor { Base = baseOrig, Limit = limOrig };
                c.AX = s; c.CF = false; return true;
            }

            case 0x0500:   // información de memoria libre: 0x30 bytes en ES:EDI
            {
                // Los NUEVE campos de la estructura del DPMI 0.9, TODOS con un número real. El
                // spec deja poner 0xFFFFFFFF en los que el host no sabe, y eso es una trampa: un
                // cliente que multiplica el campo por el tamaño de página se lleva el
                // desbordamiento sin enterarse. Un juego de prueba lee el 0x18 (páginas físicas totales),
                // hace 0xFFFFFFFF*4096, le sale -4096, y aborta con "Not enough Extended Memory
                // available to run <juego> / Amount Needed: 6000000, Amount found: -4096" — sin
                // abrir un solo fichero y saliendo por su propia puerta, que desde fuera parecía
                // un juego que "vuelve a DOS limpiamente". Medido campo a campo: rellenando cada
                // dword con su índice, el número que el juego imprimió fue 28672 = 7*4096.
                // El total es el MISMO que declara la API del extensor en 350B (16 MiB): dos
                // caminos distintos no pueden contestar cosas distintas sobre la misma memoria.
                uint repartido = _heap - _baseDelMonton;
                uint libre = repartido < MemoriaDelExtensor ? MemoriaDelExtensor - repartido : 0;
                uint pagsLibres = libre >> 12;
                uint pagsTotales = MemoriaDelExtensor >> 12;
                uint pagsEspacio = (uint)((_baseDelMonton + MemoriaDelExtensor) >> 12);
                uint at = BaseDe(c.ES) + c.EDI;
                m.WrLin32(at + 0x00, libre);          // mayor bloque libre, EN BYTES
                m.WrLin32(at + 0x04, pagsLibres);     // máxima reserva de páginas sin bloquear
                m.WrLin32(at + 0x08, pagsLibres);     // máxima reserva de páginas bloqueadas
                m.WrLin32(at + 0x0C, pagsEspacio);    // tamaño del espacio lineal, en páginas
                m.WrLin32(at + 0x10, pagsLibres);     // páginas sin bloquear en total
                m.WrLin32(at + 0x14, pagsLibres);     // páginas libres en total
                m.WrLin32(at + 0x18, pagsTotales);    // páginas físicas en total
                m.WrLin32(at + 0x1C, pagsLibres);     // espacio lineal libre, en páginas
                m.WrLin32(at + 0x20, 0);              // fichero de intercambio: no hay
                for (uint i = 0x24; i < 0x30; i += 4) m.WrLin32(at + i, 0xFFFFFFFF);   // reservados
                c.CF = false;
                return true;
            }
            case 0x0501:   // reservar memoria: tamaño BX:CX -> dirección BX:CX, handle SI:DI
            {
                uint tam = (uint)((c.BX << 16) | c.CX);
                uint dir = (_heap + 0xFFFu) & ~0xFFFu;
                long fin = (long)dir + tam;
                if (tam == 0 || fin > int.MaxValue) { c.CF = true; c.AX = 0x8012; return true; }
                Mem.EnableFlat((int)Math.Max(0, fin - Memory.Size));
                _heap = (uint)fin;
                uint h = _siguienteHandle++;
                _bloques[h] = tam;
                _dirDeBloque[h] = dir;
                c.BX = (ushort)(dir >> 16); c.CX = (ushort)dir;
                c.SI = (ushort)(h >> 16); c.DI = (ushort)h;
                c.CF = false;
                return true;
            }
            case 0x0502:   // liberar memoria
                _bloques.Remove((uint)((c.SI << 16) | c.DI));
                _dirDeBloque.Remove((uint)((c.SI << 16) | c.DI));
                c.CF = false; return true;

            case 0x0300:   // SIMULAR UNA INTERRUPCIÓN DE MODO REAL
            {
                // La vía canónica por la que un programa de modo protegido habla con el BIOS y
                // con DOS: BL = número de interrupción, CX = words a copiar de la pila, y
                // ES:EDI apuntando a la estructura de 50 bytes con los registros de entrada, que
                // a la vuelta trae los de salida. Sin esto, un juego que quiera preguntarle algo
                // al BIOS —el modo de vídeo, el ratón, la hora— se queda clavado en el INT 31h.
                // Un juego de prueba la usa nada más cargar sus fuentes, con un buffer que acaba de pedir
                // por DPMI 0100 en memoria convencional (seg 0x1000), que es justo para lo que
                // sirve el par 0100+0300.
                uint at = BaseDe(c.ES) + c.EDI;
                byte vec = c.BL;
                if (c.CX != 0)
                    return Rechazar($"INT 31h AX=0300 int {vec:X2}h con CX={c.CX} words de pila",
                                    "copiar la pila de modo protegido a la real no está modelado");

                // Los registros del invitado se SALVAN enteros: la simulación los machaca y el
                // programa espera encontrar los suyos al volver (el DPMI sólo devuelve resultados
                // por la estructura, nunca por los registros de modo protegido).
                var salvo = (c.EAX, c.EBX, c.ECX, c.EDX, c.ESI, c.EDI, c.EBP,
                             c.DS, c.ES, c.FS, c.GS, c.CF);

                c.EDI = m.RdLin32(at + 0x00); c.ESI = m.RdLin32(at + 0x04);
                c.EBP = m.RdLin32(at + 0x08);
                c.EBX = m.RdLin32(at + 0x10); c.EDX = m.RdLin32(at + 0x14);
                c.ECX = m.RdLin32(at + 0x18); c.EAX = m.RdLin32(at + 0x1C);
                ushort flags = m.RdLin16(at + 0x20);
                c.CF = (flags & 1) != 0;
                c.ES = m.RdLin16(at + 0x22); c.DS = m.RdLin16(at + 0x24);
                c.FS = m.RdLin16(at + 0x26); c.GS = m.RdLin16(at + 0x28);

                // Los segmentos de la estructura son de MODO REAL, no selectores: mientras dura
                // la llamada, uno que no esté en la tabla vale valor×16 igual que en el mundo de
                // 16 bits. Sin esto un puntero DS:DX de la estructura se resolvería a la base 0
                // y el servicio leería o escribiría en la IVT.
                _segmentosDeModoReal = true;
                try
                {
                    uint eipIgnorado = 0;
                    if (!Int(vec, c, m, ref eipIgnorado))
                    {
                        _segmentosDeModoReal = false;
                        (c.EAX, c.EBX, c.ECX, c.EDX, c.ESI, c.EDI, c.EBP,
                         c.DS, c.ES, c.FS, c.GS, c.CF) = salvo;
                        return false;   // ya queda apuntado como rechazado por quien lo sirva
                    }
                }
                finally { _segmentosDeModoReal = false; }

                m.WrLin32(at + 0x00, c.EDI); m.WrLin32(at + 0x04, c.ESI);
                m.WrLin32(at + 0x08, c.EBP);
                m.WrLin32(at + 0x10, c.EBX); m.WrLin32(at + 0x14, c.EDX);
                m.WrLin32(at + 0x18, c.ECX); m.WrLin32(at + 0x1C, c.EAX);
                m.WrLin16(at + 0x20, (ushort)((flags & ~1) | (c.CF ? 1 : 0)));
                m.WrLin16(at + 0x22, c.ES); m.WrLin16(at + 0x24, c.DS);
                m.WrLin16(at + 0x26, c.FS); m.WrLin16(at + 0x28, c.GS);

                (c.EAX, c.EBX, c.ECX, c.EDX, c.ESI, c.EDI, c.EBP,
                 c.DS, c.ES, c.FS, c.GS, c.CF) = salvo;
                Traza.Add($"  [dpmi] simular INT {vec:X2}h de modo real -> estructura en 0x{at:X}");
                c.CF = false;
                return true;
            }

            case 0x0100:   // reservar memoria DOS (por debajo de 1 MiB)
            {
                // La pide el driver de sonido para su buffer de DMA: el DMA de la ISA solo
                // direcciona el primer megabyte, asi que esto NO puede salir del monton de arriba.
                // Se sirve del hueco que queda entre la imagen del juego y la VRAM.
                uint tam = (uint)(c.BX * 16);
                uint dir = (_dosHeap + 0xFu) & ~0xFu;
                if (tam == 0 || dir + tam > 0xA0000u)
                {
                    // Sin sitio: se contesta como DOS, con el mayor bloque disponible en BX.
                    c.CF = true; c.AX = 0x0008;
                    c.BX = (ushort)((0xA0000u - dir) / 16);
                    return true;
                }
                _dosHeap = dir + tam;
                ushort sel = _siguienteSel; _siguienteSel += 8;
                _desc[sel] = new Descriptor { Base = dir, Limit = tam - 1 };
                c.AX = (ushort)(dir >> 4);   // segmento de modo real
                c.DX = sel;                  // selector para tocarlo desde 32 bits
                c.CF = false;
                Traza.Add($"  memoria DOS: {tam} bytes en 0x{dir:X} (seg {dir >> 4:X4}, sel {sel:X4})");
                return true;
            }
            case 0x0101:   // liberar memoria DOS
                _desc.Remove(c.DX); c.CF = false; return true;
            case 0x0102:   // redimensionar memoria DOS: se concede sin mover nada
                c.CF = false; return true;

            case 0x0600: case 0x0601:   // bloquear / desbloquear region
                c.CF = false; return true;   // no hay paginación: no hay nada que bloquear

            case 0x0800:   // mapear dirección física -> IDENTIDAD
            {
                // `Memory` ya intercepta A0000-BFFFF y lo desvía a VideoState, así que devolver
                // la misma dirección hace que una escritura del juego a la VRAM acabe donde debe.
                // Es exactamente lo que anticipaba el plan (§4, fase 2).
                c.CF = false;
                return true;   // BX:CX ya trae la física; se devuelve tal cual
            }
            case 0x0801:   // desmapear
                c.CF = false; return true;

            case 0x0200:   // leer vector de interrupción de MODO REAL
            {
                // La IVT de verdad, la del primer megabyte, que es la MISMA que ve el mundo de 16
                // bits: el driver de sonido engancha su IRQ ahi y el temporizador del host la usa.
                uint v = m.RdLin32((uint)(c.BL * 4));
                c.CX = (ushort)(v >> 16); c.DX = (ushort)v;
                c.CF = false; return true;
            }
            case 0x0201:   // fijar vector de interrupción de modo real
                m.WrLin32((uint)(c.BL * 4), (uint)((c.CX << 16) | c.DX));
                c.CF = false; return true;

            case 0x0202:   // leer manejador de excepción de modo protegido
                c.CX = SelCodigo; c.EDX = 0; c.CF = false; return true;
            case 0x0203:   // fijar manejador de excepción de modo protegido
                c.CF = false; return true;

            case 0x0204:   // leer vector de interrupcion de modo protegido
                c.CX = _vecPm[c.BL].sel; c.EDX = _vecPm[c.BL].off;
                c.CF = false; return true;
            case 0x0205:   // fijar vector de interrupcion de modo protegido
                _vecPm[c.BL] = (c.CX, c.EDX);
                Traza.Add($"  engancha el vector {c.BL:X2}h (DPMI) -> {c.CX:X4}:{c.EDX:X8}");
                c.CF = false; return true;
            case 0x0900:   // deshabilitar interrupciones virtuales, devolviendo el estado ANTERIOR
                c.AL = (byte)(_interrupcionesVirtuales ? 1 : 0);
                _interrupcionesVirtuales = false; c.CF = false; return true;
            case 0x0901:   // habilitarlas, devolviendo el estado anterior
                c.AL = (byte)(_interrupcionesVirtuales ? 1 : 0);
                _interrupcionesVirtuales = true; c.CF = false; return true;
            case 0x0902:   // leer el estado sin cambiarlo
                c.AL = (byte)(_interrupcionesVirtuales ? 1 : 0);
                c.CF = false; return true;

            case 0x000D:   // reservar UN descriptor concreto (BX), no el siguiente libre
            {
                // Lo usa el código que quiere un selector fijo pactado de antemano. Si ya está
                // repartido se contesta que no (8011h = descriptor no disponible), que es lo que
                // separa "es mío" de "se lo estoy pisando a otro".
                if (_desc.ContainsKey(c.BX)) { c.CF = true; c.AX = 0x8011; return true; }
                _desc[c.BX] = new Descriptor();
                if (c.BX >= _siguienteSel) _siguienteSel = (ushort)(c.BX + 8);
                c.CF = false; return true;
            }

            case 0x0503:   // redimensionar un bloque de memoria: BX:CX tamaño, SI:DI handle
            {
                uint h = (uint)((c.SI << 16) | c.DI);
                if (!_bloques.TryGetValue(h, out uint tamViejo)) { c.CF = true; c.AX = 0x8023; return true; }
                uint tamNuevo = (uint)((c.BX << 16) | c.CX);
                if (tamNuevo == 0) { c.CF = true; c.AX = 0x8021; return true; }
                // El montón es un bump allocator y no compacta: crecer es reservar detrás y COPIAR.
                // Encoger se concede en el sitio. La dirección y el handle que se devuelven pueden
                // ser otros, y el contrato de DPMI lo contempla justo por esto.
                if (tamNuevo <= tamViejo)
                {
                    _bloques[h] = tamNuevo;
                    c.BX = (ushort)(_dirDeBloque[h] >> 16); c.CX = (ushort)_dirDeBloque[h];
                    c.CF = false; return true;
                }
                uint dirVieja = _dirDeBloque[h];
                uint dirNueva = (_heap + 0xFFFu) & ~0xFFFu;
                long fin = (long)dirNueva + tamNuevo;
                if (fin > int.MaxValue) { c.CF = true; c.AX = 0x8012; return true; }
                Mem.EnableFlat((int)Math.Max(0, fin - Memory.Size));
                _heap = (uint)fin;
                for (uint i = 0; i < tamViejo; i++) m.WrLin8(dirNueva + i, m.RdLin8(dirVieja + i));
                uint hNuevo = _siguienteHandle++;
                _bloques.Remove(h); _dirDeBloque.Remove(h);
                _bloques[hNuevo] = tamNuevo; _dirDeBloque[hNuevo] = dirNueva;
                c.BX = (ushort)(dirNueva >> 16); c.CX = (ushort)dirNueva;
                c.SI = (ushort)(hNuevo >> 16); c.DI = (ushort)hNuevo;
                c.CF = false; return true;
            }

            case 0x0604:   // tamaño de página, en BX:CX
                c.BX = 0; c.CX = 0x1000; c.CF = false; return true;

            // Sugerencias de paginación de un cliente que se cree en una máquina con memoria
            // virtual. PakuPort no pagina hacia disco: nada que marcar, nada que descartar, y
            // aceptarlas es la respuesta CORRECTA, no un apaño — un host DPMI sin fichero de
            // intercambio contesta exactamente esto.
            case 0x0602:   // marcar región de modo real como paginable
            case 0x0603:   // volver a fijarla
            case 0x0702:   // candidata a descarte
            case 0x0703:   // descartar el contenido de las páginas
                c.CF = false; return true;

            // ── Lo que NO está, y por qué se dice en vez de fingirlo ────────────────────────
            //
            // 0301/0302 (llamar a un procedimiento de MODO REAL) y 0303/0304 (publicar una
            // dirección de modo real que rebote a una rutina de 32 bits del cliente) necesitan
            // las dos algo que hoy no existe: EJECUTAR código de modo real desde el mundo plano
            // y volver. El 0300 de al lado no lo necesita porque el destino de una interrupción
            // lo sirve el propio host, no el invitado.
            //
            // Fingirlas sería peor que no tenerlas: un juego que reserve un callback creería que
            // tiene ratón por callback y no lo tendría, y el síntoma —un cursor que no se mueve—
            // no se parece en nada a la causa. Ningún juego probado las ha pedido todavía;
            // el día que una salga en `gaps.log`, aquí está lo que hace falta.
            case 0x0301:
                return Rechazar("INT 31h AX=0301 (call real mode procedure, far return)",
                                "hace falta ejecutar código de modo real desde el mundo plano");
            case 0x0302:
                return Rechazar("INT 31h AX=0302 (call real mode procedure, iret)",
                                "hace falta ejecutar código de modo real desde el mundo plano");
            case 0x0303:
                return Rechazar("INT 31h AX=0303 (allocate real mode callback)",
                                "hace falta un puente de modo real a modo protegido, ver el comentario");
            case 0x0304:
                return Rechazar("INT 31h AX=0304 (free real mode callback)",
                                "no se reparten callbacks, así que no hay ninguno que liberar");
        }
        return Rechazar($"INT 31h AX={c.AX:X4}", "función DPMI no implementada todavía");
    }
}
