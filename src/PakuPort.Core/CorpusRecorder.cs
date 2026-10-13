using System.Buffers.Binary;

namespace PakuPort.Core;

/// <summary>
/// GRABADOR DE CORPUS POR FUNCIÓN — el banco de pruebas que acompaña a cada función del port.
///
/// <para>Tesis (día 1 del plan del expediente): una función que llega acompañada de
/// N invocaciones reales grabadas se puede reescribir a ciegas y saber en un segundo si está
/// bien. Es lo que convierte "la IA reescribe y reza" en un bucle cerrado con test.</para>
///
/// <para><b>Enganche por SP, en la cabecera del bucle del intérprete.</b> No toca ninguna rama de
/// <see cref="Interp"/>: en cada instrucción ya están CS, IP y SP, y eso basta.
/// <list type="bullet">
/// <item>si <c>(cs,ip)</c> es el arranque de una función de interés → se abre un caso con los
///   registros de entrada, el DGROUP y el SP de ese instante;</item>
/// <item>si un caso abierto ve que SP ha subido por encima del suyo → la función ha retornado, y
///   se cierra con el estado de ese momento.</item>
/// </list>
/// Así vale igual para <c>call</c> near, far e indirecto, y hasta para una función a la que se
/// llega por un <c>jmp</c> de cola. Lo único que se marca en las ramas de <c>call</c> es el
/// DESTINO, y sólo para el modo auto (ver <see cref="NotaFuncion"/>).</para>
///
/// <para><b>Honestidad del cierre.</b> Al abrir se anota la dirección de retorno que hay en la
/// pila; al cerrar se exige que el IP coincida con ella. Si no coincide, el caso <b>se descarta</b>
/// y se cuenta como cierre sucio: es una función que no retorna por donde se la llamó (el
/// FarRet-unwind de un juego con co-rutinas, un longjmp, una co-rutina). Grabar esos casos sería sembrar el corpus de
/// pares entrada/salida que no se corresponden — un verde falso esperando a pasar.</para>
///
/// <para><b>Alcance de la memoria grabada: el DGROUP de 64 KB</b> desde el DS de entrada, que es
/// exactamente lo que el juez de casos (PakuPort.Judge, <c>DiffEngine</c>) sabe comparar. Límite conocido, y
/// escrito en el propio índice: lo que la función toque fuera de esa ventana (VRAM, un buffer en
/// otro segmento) no entra en el caso.</para>
///
/// <para><b>Por qué el formato se escribe aquí a mano y no con <c>CorpusCase</c>:</b> el
/// contenedor PFC1 vive en el juez de casos (PakuPort.Judge), que
/// NO viaja a los exports. Un export es un producto self-contained. Así que aquí se escribe el
/// contenedor documentado, byte a byte, y el juez lo lee con su propio <c>CorpusCase</c>: si los
/// dos lados no coinciden, la verificación sale roja — que es justo la prueba que se quiere.</para>
///
/// <para>Palancas: <c>PAKU_CORPUS=&lt;dir&gt;</c> lo enciende; <c>PAKU_CORPUS_FUNCS=&lt;fichero&gt;</c>
/// da la lista de funciones (el <c>functions.json</c> de <c>pakuport discover</c>, o un .txt de
/// offsets hex) y sin ella va en modo auto; <c>PAKU_CORPUS_MAX</c> los casos por función (8) y
/// <c>PAKU_CORPUS_MAXFUN</c> el tope de funciones en auto (400).</para>
/// </summary>
public static class CorpusRecorder
{
    /// <summary>Leído una vez por instrucción en el bucle del intérprete: un estático y un salto
    /// cuando está apagado, el mismo coste que el aviso de descarrilamiento a VRAM.</summary>
    public static bool Activo;

    const int DgroupLen = 0x10000;
    const int EstadoLen = 76;          // layout verbatim del CpuState del juez (PakuPort.Judge)

    sealed class Frame
    {
        /// <summary>Se abrio con la cuota ya llena porque un instante de NOVEDAD cae dentro de su
        /// ventana (ver <see cref="_novedades"/>). Solo se escribe si de verdad lo contiene.</summary>
        public long PorNovedad = -1;
        public int Key;                 // clave canónica de función (ResidentBase + offset de fichero)
        public ushort SpEntrada;        // SP justo en la primera instrucción de la función
        public ushort RetIp;            // dirección de retorno que había en la pila al entrar
        public uint DsBase;
        public byte[] Entrada = [];
        public byte[] Mem = [];
        /// <summary>Efectos externos observados MIENTRAS este frame estaba abierto, ya en JSON.
        /// Un frame padre incluye los de sus hijos: el replay ejecuta la función entera.</summary>
        public List<Efecto> Fx = [];
        /// <summary>Lo que la función leyó FUERA de su DGROUP y nadie había escrito antes dentro
        /// de la llamada: su contexto. Es el campo `pre` del esquema.</summary>
        public Dictionary<int, byte> Pre = [];
        /// <summary>Direcciones fuera del DGROUP ya resueltas dentro de esta llamada. Si el primer
        /// acceso fue una ESCRITURA, el valor lo produce la propia función y no es contexto.</summary>
        public HashSet<int> Tocadas = [];
        public bool PreTruncado;
        /// <summary>Bytes de write-set de INT ya grabados en este caso. Es lo que más puede
        /// crecer: una lectura de fichero deja el buffer entero.</summary>
        public int BytesW;
        /// <summary>El SP MÁS BAJO al que llegó la llamada. Todo lo que quede entre él y el SP de
        /// entrada lo pisó el marco de alguien y es scratch por definición: nadie puede leerlo
        /// después sin haberlo escrito antes. El juez lo necesita MEDIDO y no como una constante,
        /// porque las interrupciones de hardware que ocurrieron durante la grabación bajan más
        /// que la función — medido en un juego de prueba: `c39e` usa 0x104 bytes de marco propio y
        /// la IRQ del timer dejó su dirección de retorno 60 bytes por debajo de la banda fija de
        /// 256 que el juez enmascaraba. Sólo vale si la pila vive en el DGROUP (SS = el segmento
        /// del DGROUP); si no, esos offsets son datos como los demás y no se tocan.</summary>
        public ushort SpMin;
        public bool PilaEnDgroup;
        /// <summary>Huella del CÓDIGO que había bajo CS cuando arrancó la llamada, y el CS. El
        /// `mundo.bin` es UNA foto —la del final de la partida— y en un juego con overlays el
        /// mismo segmento aloja código distinto según el momento: al reproducir un caso viejo se
        /// ejecutan los bytes del overlay equivocado. Medido en un juego de prueba: la función
        /// `b40a` arrancaba en `cbw` / `and al,2Bh` / `fucomi` y se metía en un mar de ceros, y
        /// así 48 de 231 funciones. La ventana del código viaja con el caso, deduplicada por esta
        /// huella: son 64 KB por overlay DISTINTO, no por caso.</summary>
        public ulong CodigoHuella;
        public ushort CodigoSeg;
        /// <summary>La ventana de código COPIADA al abrir la llamada. Se guarda aquí y no se lee
        /// de la memoria al cerrar porque en esos 64 KB caben datos que cambian durante la
        /// llamada: la huella salía distinta, el caso se declaraba «el código cambió» y se
        /// quedaba SIN ventana. Medido en un juego de prueba: 40 casos de 875, y entre ellos las
        /// siete funciones (`da72`, `daee`, `db7d`, `dbce`, `dc32`, `df32`, `df66`) que sin
        /// ventana pierden también el mundo garantizado y saltan sus `call far`.</summary>
        public byte[]? CodigoBytes;
        /// <summary>Longitud de esa ventana. NO son siempre 64 KB: se corta donde empieza el
        /// DGROUP, porque los datos cambian a cada instante y meterlos en la huella hacía que
        /// TODOS los casos salieran «el código cambió durante la llamada» — medido: 113 ventanas
        /// distintas de 64 KB y ni una sola utilizable.</summary>
        public int CodigoLargo;
        public int NumFx;                   // efectos observados (con sus repeticiones)
        public long InsnAlAbrir;
        /// <summary>Instrucciones gastadas dentro de ISR de HARDWARE en el momento de abrir. El
        /// reloj de las IRQ del caso (el campo `d` del efecto "q") las descuenta: en el replay la
        /// ISR no se ejecuta, así que un contador que las incluyera mediría dos relojes
        /// distintos y la IRQ caería en otra instrucción del bucle de espera.</summary>
        public long InsnIsrAlAbrir;
        public bool Desbordado;             // paso del presupuesto: el caso NO se graba
        /// <summary>Este frame es una VUELTA de bucle, no una llamada: no se cierra con un `ret`
        /// sino al volver a la cabecera con la misma pila. Ver <see cref="NotaBucle"/>.</summary>
        public bool EsVuelta;
        /// <summary>Cabecera y ultimo byte del CUERPO del bucle (el `ip` del salto que vuelve
        /// atras). Hace falta para saber cuando el programa SE SALE del bucle: si no, el frame de
        /// la vuelta final —la que no se completa— sigue vivo hasta que retorne quien lo contiene
        /// y ensucia todas las vueltas de los bucles de fuera, que lo ven vivo por debajo. Medido
        /// en un juego de prueba: 451 vueltas descartadas por eso.</summary>
        public ushort CuerpoIni, CuerpoFin;
        /// <summary>La vuelta termino saliendo del cuerpo, no volviendo a la cabecera. Se graba
        /// igual: es el caso que cubre la condicion de SALIDA del bucle.</summary>
        public bool SalioDelBucle;
    }

    /// <summary>Un efecto y cuántas veces seguidas se repitió. Sin el contador, un bucle que
    /// espera el retrace leyendo el puerto 0x3DA treinta mil veces deja treinta mil entradas
    /// iguales: medido en un juego de prueba, 1,15 MB de efectos en UN caso.</summary>
    sealed class Efecto
    {
        public required string J;
        public int C = 1;
    }

    sealed class Ficha
    {
        /// <summary>Casos escritos POR NOVEDAD, ademas de la cuota normal.</summary>
        public int PorNovedad;
        /// <summary>El ultimo instante de novedad que ya cubre un caso escrito de esta unidad.</summary>
        public long NovedadCubierta = -1;
        /// <summary>Lo que dura (en instrucciones) el caso mas largo escrito de esta unidad. Acota
        /// la ventana de novedad: sin ella, un bucle de 300 vueltas por fotograma abria —y copiaba
        /// 128 KB por cada una— cientos de casos por instante que luego se tiraban.</summary>
        public long DuracionMax;
        public int Llamadas;            // veces que se vio arrancar la función (o la vuelta)
        public int Grabados;            // casos escritos a disco
        public int CierresSucios;       // retornos que no volvieron por donde se llamó
        public int Desbordados;         // llamadas que pasaron del presupuesto de un caso
        public int Cuota;
        /// <summary>La unidad grabada es una VUELTA de bucle, no una llamada. Se descubrió por un
        /// salto hacia atrás y no por un `call`, así que no tiene dirección de retorno: entra por
        /// la cabecera y sale al volver a ella con la misma pila.</summary>
        public bool EsVuelta;
        /// <summary>Bytes que salta hacia atrás el back-edge que la descubrió: el tamaño del
        /// cuerpo del bucle. Va al índice porque es el criterio con el que se aceptó.</summary>
        public int Alcance;
        /// <summary>Vueltas que llegaron a la cabecera con alguien vivo todavía por debajo en la
        /// pila. No se graban: la pila no se ha desmontado y el par entrada/salida no se
        /// corresponde.</summary>
        public int VueltasSucias;
        /// <summary>Vueltas que no llegaron a completarse porque el programa se salió del bucle, o
        /// porque quien lo contiene retornó a media vuelta. NO es un fallo: todo bucle se sale
        /// exactamente una vez por cada vez que se entra en él.</summary>
        public int VueltasAMedias;
        /// <summary>De los casos grabados, cuantos terminan SALIENDO del bucle en vez de volver a
        /// la cabecera. Va al indice: son los que cubren la condicion de salida, y si son cero la
        /// traduccion no tiene con que comprobar por donde se sale.</summary>
        public int Salidas;
    }

    static readonly List<Frame> _abiertos = [];
    static readonly Dictionary<int, Ficha> _fichas = [];
    static string _dir = "";
    static int _cuota = 8;

    /// <summary>EL CORPUS GUIADO POR COBERTURA (`PAKU_CORPUS_NOVEDADES=&lt;fichero&gt;`).
    ///
    /// <para><b>El hueco que tapa.</b> La cuota guarda las OCHO PRIMERAS invocaciones de cada
    /// unidad, y un juego hace casi siempre lo mismo: en un juego de prueba los ocho primeros turnos del
    /// jugador son el arranque, y el disparo que acierta, el fin de la partida o el record no
    /// entran nunca. Traducir esos caminos sin caso es traducir sin red.</para>
    ///
    /// <para><b>Como.</b> Dos pasadas con la MISMA cinta, que el arnes ya es determinista. La
    /// primera (<c>PAKU_COBERTURA</c>) apunta en <c>&lt;csv&gt;.novedades</c> el instante en que
    /// cada direccion de codigo se ejecuto por primera vez. La segunda lee esos instantes y, con la
    /// cuota ya llena, abre tambien las invocaciones que empiezan poco antes de uno
    /// (<c>PAKU_CORPUS_VENTANA_NOV</c> instrucciones, 400.000 por defecto) y solo escribe las que de
    /// verdad lo contienen. Cada instante acaba cubierto por un caso de CADA unidad que lo
    /// envuelve, de la mas interior a la de fuera. Tope por unidad: <c>PAKU_CORPUS_MAX_NOV</c>
    /// (64).</para></summary>
    static long[] _novedades = [];
    static int _novIdx;
    static long _ventanaNov = 400_000;
    static int _maxNov = 64;
    static int _sinCuota;               // funciones que ya llenaron su cuota
    static bool _auto;                  // sin lista: las funciones se aprenden de los `call` vistos
    static int _maxFunciones = 400;
    /// <summary>Tope de bytes de contexto por caso. Una función que recorre medio megabyte fuera
    /// de su DGROUP existe; grabarlo entero por caso, no.</summary>
    static int _maxPre = 256 * 1024;
    /// <summary>Presupuesto de un caso. Una función que dura toda la partida no es un caso de
    /// prueba: es el programa. Medido en un juego de prueba, el bucle principal dejó UN caso de
    /// 307 MB con 19,2 millones de efectos (lecturas del retrace, que alternan 9/0 y por eso ni
    /// siquiera colapsan). Pasado el tope el caso se descarta y se dice en el índice.</summary>
    static int _maxFx = 20_000;
    /// <summary>Tope de write-set de INT por caso. Una INT que escribe en la memoria del programa
    /// —`AH=3Fh` leyendo un fichero a un buffer, `AH=48h` partiendo la cadena de MCB— tiene que
    /// dejar esos bytes grabados o el caso NO SE REPRODUCE: medido el 2026-08-25 en un juego
    /// de prueba, era la causa de 26 de las 70 funciones que el propio original no reproducía. Pasado el
    /// tope el caso se DESCARTA entero, no se graba a medias: un caso con la mitad del write-set
    /// sale rojo sin decir por qué, que es peor que no tenerlo.
    ///
    /// <para>4 MB y no 512 KB desde el 2026-08-28, y es una medida, no una holgura: al empezar a
    /// grabar el write-set de los servicios que copian EN BLOQUE (ver
    /// <see cref="Memory.CopiaDeServicio"/>) los write-sets legítimos crecieron de 827 bytes a
    /// 411 KB en el corpus entero de un juego de prueba, y con el tope viejo `21a5` se quedaba SIN
    /// casos (2 -> 0) y el corpus perdía una función. Con 4 MB vuelve, y el corpus ocupa lo
    /// mismo: 122 MB con los dos topes.</para></summary>
    static int _maxW = 4 * 1024 * 1024;
    static long _maxInsn = 5_000_000;

    /// <summary>Alcance mínimo, en bytes, de un salto hacia atrás para que su destino se acepte
    /// como cabecera de bucle. Es el único filtro contra la avalancha: en un juego de prueba hay 46
    /// back-edges distintos en el código que se ejecuta y la mayoría son `loop` de tres
    /// instrucciones, cuyo trabajo YA viaja dentro de la vuelta del bucle grande que los contiene.
    /// Con 0 se apaga la grabación de bucles y el corpus vuelve a ser sólo de llamadas, que es el
    /// control negativo de esta palanca.</summary>
    static int _bucleMin = 32;

    /// <summary>Enciende el grabador si <c>PAKU_CORPUS</c> está puesto. Devuelve el aviso a
    /// imprimir, o null si no se pidió nada. Se llama una vez al arrancar el hilo del juego.</summary>
    public static string? ArrancaDesdeEntorno()
    {
        string? dir = Environment.GetEnvironmentVariable("PAKU_CORPUS");
        if (string.IsNullOrWhiteSpace(dir)) return null;

        if (int.TryParse(Environment.GetEnvironmentVariable("PAKU_CORPUS_MAX"), out int max) && max > 0) _cuota = max;
        if (int.TryParse(Environment.GetEnvironmentVariable("PAKU_CORPUS_MAXFUN"), out int maxF) && maxF > 0) _maxFunciones = maxF;
        if (int.TryParse(Environment.GetEnvironmentVariable("PAKU_CORPUS_MAXPRE"), out int maxP) && maxP > 0) _maxPre = maxP;
        if (int.TryParse(Environment.GetEnvironmentVariable("PAKU_CORPUS_MAXFX"), out int maxX) && maxX > 0) _maxFx = maxX;
        if (int.TryParse(Environment.GetEnvironmentVariable("PAKU_CORPUS_MAXW"), out int maxW) && maxW > 0) _maxW = maxW;
        if (long.TryParse(Environment.GetEnvironmentVariable("PAKU_CORPUS_MAXINSN"), out long maxI) && maxI > 0) _maxInsn = maxI;
        if (int.TryParse(Environment.GetEnvironmentVariable("PAKU_CORPUS_BUCLE_MIN"), out int bmin) && bmin >= 0) _bucleMin = bmin;
        if (long.TryParse(Environment.GetEnvironmentVariable("PAKU_CORPUS_VENTANA_NOV"), out long vn) && vn > 0) _ventanaNov = vn;
        if (int.TryParse(Environment.GetEnvironmentVariable("PAKU_CORPUS_MAX_NOV"), out int mn) && mn >= 0) _maxNov = mn;
        _novedades = [];
        _novIdx = 0;
        string? nov = Environment.GetEnvironmentVariable("PAKU_CORPUS_NOVEDADES");
        if (!string.IsNullOrWhiteSpace(nov) && File.Exists(nov))
            _novedades = File.ReadLines(nov).Where(l => l.Length > 0 && l[0] != '#')
                .Select(l => long.Parse(l.Split(' ')[0])).Distinct().Order().ToArray();

        _dir = dir;
        Directory.CreateDirectory(_dir);
        _fichas.Clear();
        _abiertos.Clear();
        _sinCuota = 0;
        _profIrq = 0; _abiertosAlEntrarIrq = 0; _insnIsrTotal = 0; _wsIrq = null; _fotosIrq.Clear();

        // Con lista se graban esas y sólo esas. Sin lista, modo AUTO: las funciones se aprenden de
        // los `call` que la propia ejecución hace, que es lo que permite grabar juegos donde
        // `discover` no llega — un COM, por ejemplo, que no es un MZ y ni siquiera carga.
        string? lista = Environment.GetEnvironmentVariable("PAKU_CORPUS_FUNCS");
        _auto = string.IsNullOrWhiteSpace(lista);
        if (!_auto)
        {
            if (!File.Exists(lista)) return $"[CORPUS] la lista de funciones no existe: {lista}";
            var offsets = LeeLista(lista!);
            if (offsets.Count == 0) return $"[CORPUS] la lista {lista} no tiene ni un offset: no se graba nada";
            foreach (int off in offsets)
                _fichas[OverlayDirectory.ResidentBase + off] = new Ficha { Cuota = _cuota };
        }

        Activo = true;
        return _auto
            ? $"[CORPUS] grabando -> {_dir} (modo auto: funciones aprendidas de los `call`, hasta {_maxFunciones} funciones x {_cuota} casos)"
            : $"[CORPUS] grabando -> {_dir} ({_fichas.Count} funciones de la lista, hasta {_cuota} casos cada una)";
    }

    /// <summary>Acepta el <c>functions.json</c> de <c>pakuport discover</c> (objeto con la lista
    /// "functions") o un fichero de texto con un offset hex por línea.</summary>
    static List<int> LeeLista(string path)
    {
        var res = new List<int>();
        string texto = File.ReadAllText(path);
        if (texto.TrimStart().StartsWith('{'))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(texto);
            if (doc.RootElement.TryGetProperty("functions", out var arr))
                foreach (var e in arr.EnumerateArray())
                    if (TryHex(e.GetString(), out int v)) res.Add(v);
        }
        else
        {
            foreach (string ln in texto.Split('\n'))
                if (TryHex(ln.Trim(), out int v)) res.Add(v);
        }
        return res;
    }

    static bool TryHex(string? s, out int v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
    }

    /// <summary>El intérprete acaba de ver un <c>call</c> a <paramref name="seg"/>:<paramref
    /// name="off"/>. En modo auto eso es la definición operativa de "aquí empieza una función":
    /// no se infiere del binario, se observa. Con lista explícita no hace nada.</summary>
    public static void NotaFuncion(Cpu c, ushort seg, ushort off)
    {
        if (!_auto) return;
        int key = Interp.KeyOf(c, seg, off);
        if (key < OverlayDirectory.ResidentBase) return;   // driver o BIOS: no es código del juego
        if (_fichas.TryGetValue(key, out var ya))
        {
            // La misma dirección puede ser cabecera de un bucle Y destino de un `call`. Manda el
            // `call`: una llamada tiene dirección de retorno y por tanto cierre honesto, y una
            // vuelta no. Sólo se promueve mientras no haya grabado nada, para no mezclar dos
            // clases de caso en la misma carpeta.
            if (ya.EsVuelta && ya.Grabados == 0) { ya.EsVuelta = false; ya.Llamadas = 0; }
            return;
        }
        if (_fichas.Count >= _maxFunciones) return;
        _fichas[key] = new Ficha { Cuota = _cuota };
    }

    /// <summary>El intérprete acaba de TOMAR un salto hacia atrás, de <paramref name="src"/> a
    /// <paramref name="dst"/>. Eso es la definición operativa de una cabecera de bucle: se observa,
    /// no se infiere del binario.
    ///
    /// <para><b>Por qué hace falta, y está medido</b> (un juego de prueba, 2026-09-06): grabando sólo
    /// llamadas, el corpus cubre el <b>3,6 %</b> de los eventos del juego. El otro 96,4 % no está
    /// en ninguna función que se le escapara al grabador —los 12 destinos de `call` que la partida
    /// ejecuta están los 12 aprendidos—: está en código que <b>no vive dentro de ninguna llamada
    /// que retorne</b>. La pantalla de atracción (48,9 % de los eventos) corre desde el punto de
    /// entrada sin que nadie la llame, y el bucle principal del juego (47,4 %) es una llamada que
    /// se hace UNA vez y no retorna jamás. Ni uno ni otro pueden tener un par entrada/salida.
    /// La unidad que sí tienen es <b>la vuelta</b>.</para>
    ///
    /// <para>El filtro es el alcance del salto (<see cref="_bucleMin"/>): los bucles diminutos
    /// —un `loop` de tres instrucciones que copia una fila— no aportan un caso propio, porque su
    /// trabajo ya viaja dentro de la vuelta del bucle grande que los contiene.</para></summary>
    public static void NotaBucle(Cpu c, ushort seg, ushort dst, ushort src)
    {
        if (!_auto || _bucleMin <= 0 || _fichas.Count >= _maxFunciones) return;
        if (src - dst < _bucleMin) return;
        int key = Interp.KeyOf(c, seg, dst);
        if (key < OverlayDirectory.ResidentBase) return;   // driver o BIOS: no es código del juego
        if (_fichas.TryGetValue(key, out var ya))
        {
            // Un bucle con varias salidas hacia atras (un `continue`) llega a la misma cabecera
            // desde sitios distintos. El cuerpo es el que abarca el salto MAS LARGO: quedarse con
            // el primero que se vio dejaria el rango corto y el frame se cerraria a media vuelta.
            if (ya.EsVuelta && src - dst > ya.Alcance) ya.Alcance = src - dst;
            return;
        }
        _fichas[key] = new Ficha { Cuota = _cuota, EsVuelta = true, Alcance = src - dst };
    }

    // --- CONTEXTO DEL CASO (lo que se lee fuera del DGROUP) ----------------------------------
    // MEDIDO el 2026-08-24 y es la causa DOMINANTE de las rojas: sobre las MISMAS 100 funciones de
    // un juego de prueba, el corpus sale 75 % verde cuando la foto del mundo es de hace 8 s y 47 %
    // cuando es de hace 25 s. Un caso no puede depender de una foto global tomada en otro
    // instante: tiene que llevar su propio contexto. Es el campo `pre` que el esquema ya preveía.
    //
    // Se registra la PRIMERA vez que se toca cada dirección de fuera del DGROUP, y sólo si ese
    // primer acceso es una LECTURA — si la función la escribió antes, el valor lo produce ella y
    // no es contexto de entrada.

    /// <summary>El intérprete acaba de leer <paramref name="v"/> de la dirección física
    /// <paramref name="a"/>. El fetch de instrucciones NO pasa por aquí (va directo al array), así
    /// que el contexto no se llena con el código.</summary>
    public static void NotaLectura(int a, byte v)
    {
        for (int i = 0; i < _abiertos.Count; i++)
        {
            var f = _abiertos[i];
            if (f.Desbordado) continue;
            if (a >= f.DsBase && a < f.DsBase + DgroupLen) continue;   // dentro de su ventana
            if (!f.Tocadas.Add(a)) continue;
            if (f.Pre.Count >= _maxPre) { f.PreTruncado = true; continue; }
            f.Pre[a] = v;
        }
    }

    /// <summary>Escritura fuera del DGROUP: marca la dirección como resuelta por la propia función
    /// para que una lectura posterior no la confunda con contexto de entrada.
    ///
    /// <para>Y si hay una INT despachándose, esta escritura es SUYA: se apunta con su valor para
    /// que el replay la reproduzca (campo `w` del esquema). El DOS escribe en la memoria del
    /// programa más de lo que parece — el buffer de una lectura de fichero, y la cadena de MCB
    /// cuando reparte memoria.</para></summary>
    public static void NotaEscritura(int a, byte v)
    {
        if (_wsInt != null) _wsInt[a] = v;
        // Y lo mismo para una IRQ en curso. Esto es lo que le da a la ISR memoria FUERA del
        // DGROUP: el diff de la foto solo ve esos 64 KB, y un driver escribe sus banderas en su
        // propio segmento. Medido en un juego de prueba: `b5c6` hace `call far 1615:0` en bucle
        // hasta que `[cs:00B2h]` del driver (lineal 0xABA2, muy por debajo del DGROUP) pasa de 1
        // a 0, y quien lo pone a 0 es su ISR.
        if (_wsIrq != null) _wsIrq[a] = v;
        for (int i = 0; i < _abiertos.Count; i++)
        {
            var f = _abiertos[i];
            if (a < f.DsBase || a >= f.DsBase + DgroupLen) f.Tocadas.Add(a);
        }
    }

    /// <summary>El contexto como el `pre` del esquema: tramos contiguos `[["46C","64000000"],…]`
    /// (lineal en hex → bytes en hex). Se agrupa porque un buffer leído seguido son miles de
    /// direcciones consecutivas y una entrada por byte multiplicaría el fichero por diez.</summary>
    static string SerializaPre(Dictionary<int, byte> pre)
    {
        if (pre.Count == 0) return "";
        var dirs = pre.Keys.ToArray();
        Array.Sort(dirs);
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < dirs.Length)
        {
            int ini = dirs[i], j = i;
            while (j + 1 < dirs.Length && dirs[j + 1] == dirs[j] + 1) j++;
            var bytes = new byte[j - i + 1];
            for (int k = 0; k < bytes.Length; k++) bytes[k] = pre[ini + k];
            if (sb.Length > 0) sb.Append(',');
            sb.Append($"[\"{ini:X}\",\"{Convert.ToHexString(bytes)}\"]");
            i = j + 1;
        }
        return sb.ToString();
    }

    // --- EFECTOS EXTERNOS (INT y `in`) -------------------------------------------------------
    // Sin esto, toda función que hable con la máquina es roja al reproducirla: `375c` de
    // un juego de prueba hace `mov ah,0; int 1Ah; cmp dx,bx; je` esperando a que cambie el tick del
    // reloj, y sin plataforma DX no cambia NUNCA. Lo consume `EffectsReplayPlatform`, que ya
    // existía y esperaba justo este esquema.
    static int _intPendiente = -1;
    static ushort _intCsTras, _intIpTras;
    /// <summary>Escrituras hechas MIENTRAS se despacha la INT pendiente. Sólo existe entre
    /// <see cref="MarcaInt"/> y el paso siguiente: fuera de una INT es null y no cuesta nada.</summary>
    static SortedDictionary<int, byte>? _wsInt;

    /// <summary>El intérprete va a despachar `int n` y, si la atiende la plataforma, volverá a
    /// <paramref name="csTras"/>:<paramref name="ipTras"/>. El efecto no se puede grabar aquí
    /// —los registros de salida aún no existen—, así que se deja pendiente y lo confirma el
    /// siguiente paso del bucle.</summary>
    public static void MarcaInt(int n, ushort csTras, ushort ipTras, Cpu c, Memory m)
    {
        _intPendiente = n; _intCsTras = csTras; _intIpTras = ipTras;
        _wsInt = null;
        _fotos.Clear();
        if (_abiertos.Count <= PrimerFrameVivo) return;
        _wsInt = new SortedDictionary<int, byte>();
        // FOTO DEL DGROUP. Los manejadores de DOS escriben en la memoria del programa por el
        // array crudo (208 sitios en Platform/), no por `Memory.WrU8`, asi que la nota de
        // escritura NO los ve. Y esos bytes son exactamente los que el juez compara: sin ellos,
        // `AH=3Fh` no deja el fichero leido y `AH=48h` no parte la cadena de MCB. Se fotografia
        // por DGROUP distinto (normalmente uno solo, aunque haya varios frames abiertos).
        for (int i = PrimerFrameVivo; i < _abiertos.Count; i++)
        {
            var f = _abiertos[i];
            if (f.Desbordado) continue;
            if (_fotos.ContainsKey(f.DsBase)) continue;
            if (!_libres.TryPop(out var buf)) buf = new byte[DgroupLen];
            Array.Copy(m.Raw, f.DsBase, buf, 0, DgroupLen);
            _fotos[f.DsBase] = buf;
        }
    }

    /// <summary>Fotos del DGROUP tomadas justo antes de la INT en curso, por dirección base.</summary>
    static readonly Dictionary<uint, byte[]> _fotos = [];
    /// <summary>Buffers de foto reutilizados: una INT por instrucción no puede pedir 64 KB al
    /// recolector cada vez.</summary>
    static readonly Stack<byte[]> _libres = new();

    /// <summary>Un `in` acaba de leer <paramref name="valor"/> del puerto. Aquí sí se sabe el
    /// resultado, así que se graba en el acto.</summary>
    public static void NotaIn(ushort valor)
    {
        if (_abiertos.Count == 0) return;
        Anota($"{{\"t\":\"p\",\"v\":{valor}}}");
    }

    /// <summary>Añade el efecto a todos los frames abiertos, colapsando la repetición inmediata.
    ///
    /// <para>Mientras corre una ISR de HARDWARE, los frames que ya estaban abiertos NO se enteran:
    /// para ellos esa ISR no se ejecuta en el replay —se reproduce por su efecto NETO, el "q"— así
    /// que sus INT y sus `in` quedarían en la cola sin que nadie los pida, y el primer efecto de
    /// verdad de la función saldría `DesyncEfectos`. Los frames abiertos DENTRO de la ISR sí los
    /// anotan: esos casos se reproducen ejecutando su propio código, y sin sus efectos se quedan
    /// sin nada que consumir — medido, era la regresión de `2ccd` y `2e3c`, dos funciones que la
    /// ISR del timer llama y que leen el puerto 0x21.</para></summary>
    static void Anota(string j)
    {
        for (int i = PrimerFrameVivo; i < _abiertos.Count; i++)
        {
            var f = _abiertos[i];
            if (f.Desbordado) continue;
            f.NumFx++;
            if (f.Fx.Count >= _maxFx) { f.Desbordado = true; f.Fx.Clear(); f.Pre.Clear(); continue; }
            if (f.Fx.Count > 0 && f.Fx[^1].J == j) f.Fx[^1].C++;
            else f.Fx.Add(new Efecto { J = j });
        }
    }

    /// <summary>Confirma el INT pendiente si el flujo volvió justo detrás de él. Si fue a parar a
    /// otro sitio, la INT vectorizó a código del juego: ese código se interpreta y sus efectos ya
    /// están en el flujo, así que grabarla sería contar dos veces. Se descarta.</summary>
    static void ConfirmaInt(Cpu c, Memory m, ushort ip)
    {
        int n = _intPendiente;
        _intPendiente = -1;
        var ws = _wsInt; _wsInt = null;
        bool valida = n >= 0 && _abiertos.Count > PrimerFrameVivo && c.CS == _intCsTras && ip == _intIpTras;
        // El diff de la foto: lo que el manejador cambió dentro del DGROUP. Se hace antes de
        // devolver los buffers, y sólo si la INT cuenta.
        foreach (var (dsBase, foto) in _fotos)
        {
            if (valida && ws != null)
                for (int k = 0; k < DgroupLen; k++)
                    if (foto[k] != m.Raw[dsBase + k]) ws[(int)dsBase + k] = m.Raw[dsBase + k];
            _libres.Push(foto);
        }
        _fotos.Clear();
        if (!valida) return;
        // El WRITE-SET de la INT: lo que el manejador escribió en la memoria del programa. Sin
        // esto, `AH=3Fh` no deja el fichero leído y `AH=48h` no parte la cadena de MCB, y el caso
        // no se reproduce ni ejecutando el binario original.
        string w = "";
        if (ws != null && ws.Count > 0)
        {
            w = ",\"w\":[" + Tramos(ws) + "]";
            for (int i = PrimerFrameVivo; i < _abiertos.Count; i++)
            {
                var f = _abiertos[i];
                if (f.Desbordado) continue;
                f.BytesW += ws.Count;
                if (f.BytesW > _maxW) { f.Desbordado = true; f.Fx.Clear(); f.Pre.Clear(); }
            }
        }
        Anota($"{{\"t\":\"i\",\"n\":{n},\"r\":[{c.AX},{c.BX},{c.CX},{c.DX},{c.SI},{c.DI},{c.ES},{c.DS}]," +
              $"\"cf\":{(c.CF ? 1 : 0)},\"zf\":{(c.ZF ? 1 : 0)}{w}}}");
    }

    // --- INTERRUPCIONES DE HARDWARE (IRQ) ----------------------------------------------------
    // Una funcion que espera una IRQ no se puede reproducir con una cola de INT y `in`: se queda
    // dando vueltas en su bucle para siempre. Medido en un juego de prueba (2026-08-25), 14 de las
    // 19 funciones que el propio binario original no reproducia son exactamente eso — `475a` lee
    // el vector 8, enmascara todo menos IRQ0, programa el PIT, instala su ISR y se queda en
    // `0AAF:1DD7 <-> 1DD9`.
    //
    // Se graba igual que una INT: por su EFECTO NETO. La ISR no se ejecuta en el replay; lo que
    // viaja con el caso es lo que la ISR dejo escrito en el DGROUP, MAS el instante en que
    // ocurrio. Ese instante es la clave: un bucle de espera necesita que la memoria cambie EN el
    // punto correcto, no al principio.
    //
    // El reloj es el CONTADOR DE INSTRUCCIONES DEL INTERPRETE descontando las gastadas dentro de
    // ISR, que es lo unico que mide igual en la grabacion y en el replay.
    //
    // LIMITE DECLARADO: ese reloj solo avanza cuando el codigo lo ejecuta el interprete. Una
    // traduccion a C# puro no lo mueve, asi que no consume estas IRQ; para esas funciones quien
    // traduce tiene que delegar el bucle de espera al original (`Original.Llama`). Se dice aqui
    // para que no sea una sorpresa medida por nadie mas.
    static int _profIrq;
    /// <summary>Frames que ya estaban abiertos cuando entró la IRQ. Los de índice menor viven
    /// FUERA de la ISR y para ellos la interrupción es un efecto neto; los de índice mayor o
    /// igual se abrieron DENTRO y son casos normales, con sus efectos propios.</summary>
    static int _abiertosAlEntrarIrq;
    /// <summary>El primer frame al que hay que anotarle efectos ahora mismo.</summary>
    static int PrimerFrameVivo => _profIrq > 0 ? _abiertosAlEntrarIrq : 0;
    static long _insnIsrTotal;          // instrucciones gastadas dentro de ISR de hardware, en total
    static long _insnAlMarcarIrq, _insnIsrAlMarcarIrq;
    static int _vectorIrq;
    static SortedDictionary<int, byte>? _wsIrq;
    static ushort _spAlMarcarIrq;
    static bool _pilaEnDgroupIrq;
    /// <summary>Los registros del interrumpido justo antes de entrar en la ISR. Una ISR también
    /// devuelve REGISTROS cambiados y no sólo memoria: la rutina de calibración de un juego
    /// de prueba (`475a`) hace `sub ax,ax; sti; or ax,ax; je $-2` y su ISR es literalmente
    /// `not ax / push ax / mov al,20h / out 20h,al / pop ax / iret` — el bucle sale porque el
    /// manejador le cambió AX por debajo. Sin esto no retorna jamás.</summary>
    static readonly ushort[] _regsAlMarcarIrq = new ushort[9];
    /// <summary>Fotos del DGROUP tomadas justo antes de la IRQ en curso. Separadas de las de la
    /// INT a proposito: la ISR puede hacer un `int` y aquellas se reciclan en cada uno.</summary>
    static readonly Dictionary<uint, byte[]> _fotosIrq = [];

    /// <summary>El PIC va a entregar una IRQ al invitado. Toma la foto del DGROUP y para el
    /// reloj; el efecto lo cierra <see cref="ConfirmaIrq"/> cuando la ISR ha vuelto.</summary>
    public static void MarcaIrq(int vector, Cpu c, Memory m)
    {
        if (_profIrq++ > 0) return;                 // IRQ anidada: cuenta dentro de la de fuera
        _abiertosAlEntrarIrq = _abiertos.Count;
        _insnAlMarcarIrq = Interp.InsnCount;
        _insnIsrAlMarcarIrq = _insnIsrTotal;
        _vectorIrq = vector;
        _wsIrq = null;
        _spAlMarcarIrq = c.SP;
        _pilaEnDgroupIrq = false;
        LeeRegs(c, _regsAlMarcarIrq);
        if (_abiertos.Count == 0) return;
        _wsIrq = new SortedDictionary<int, byte>();
        for (int i = 0; i < _abiertos.Count; i++)
        {
            var f = _abiertos[i];
            if (f.Desbordado) continue;
            _pilaEnDgroupIrq |= (uint)(c.SS << 4) == f.DsBase;
            if (_fotosIrq.ContainsKey(f.DsBase)) continue;
            if (!_libres.TryPop(out var buf)) buf = new byte[DgroupLen];
            Array.Copy(m.Raw, f.DsBase, buf, 0, DgroupLen);
            _fotosIrq[f.DsBase] = buf;
        }
    }

    /// <summary>La ISR ha vuelto: se anota el efecto "q" con su write-set neto y su instante.</summary>
    public static void ConfirmaIrq(Cpu c, Memory m)
    {
        if (--_profIrq > 0) return;
        _profIrq = 0;
        long gastadas = Interp.InsnCount - _insnAlMarcarIrq;
        _insnIsrTotal += gastadas;
        var ws = _wsIrq; _wsIrq = null;
        foreach (var (dsBase, foto) in _fotosIrq)
        {
            if (ws != null)
                for (int k = 0; k < DgroupLen; k++)
                    if (foto[k] != m.Raw[dsBase + k]) ws[(int)dsBase + k] = m.Raw[dsBase + k];
            _libres.Push(foto);
        }
        _fotosIrq.Clear();
        if (ws == null || _abiertos.Count == 0) return;
        // El MARCO de la ISR no es efecto: son los bytes por debajo del SP que habia al entrar,
        // y esos los pisa cualquiera. Aplicarlos en el replay seria escribir basura en la pila
        // del caso, que ademas esta en otro sitio si el SP no coincide.
        if (_pilaEnDgroupIrq)
        {
            foreach (var f in _abiertos)
            {
                if (f.Desbordado || (uint)(c.SS << 4) != f.DsBase) continue;
                foreach (var k in ws.Keys.ToList())
                {
                    // SOLO dentro del DGROUP: una direccion por debajo de la base da un offset
                    // NEGATIVO, que compararlo con el SP borraria del write-set todo lo que la
                    // ISR escribio en segmentos bajos — justo lo que hace falta conservar.
                    int off = k - (int)f.DsBase;
                    if (off >= 0 && off < _spAlMarcarIrq) ws.Remove(k);
                }
                break;
            }
        }
        // El write-set de REGISTROS: sólo los que la ISR dejó distintos. Grabarlos todos
        // aplastaría los que el propio bucle de espera lleva (el contador de vueltas), y grabar
        // ninguno deja al bucle sin salida.
        var rr = new ushort[9];
        LeeRegs(c, rr);
        var cambios = new List<string>();
        for (int k = 0; k < 9; k++)
            if (rr[k] != _regsAlMarcarIrq[k]) cambios.Add($"[{k},{rr[k]}]");
        if (ws.Count == 0 && cambios.Count == 0) return;
        string w = (ws.Count > 0 ? ",\"w\":[" + Tramos(ws) + "]" : "")
                 + (cambios.Count > 0 ? ",\"rw\":[" + string.Join(",", cambios) + "]" : "");
        // El instante, POR FRAME: cada caso empezo en un punto distinto del programa.
        long insnAhora = _insnAlMarcarIrq, isrAhora = _insnIsrAlMarcarIrq;
        for (int i = 0; i < Math.Min(_abiertos.Count, _abiertosAlEntrarIrq); i++)
        {
            var f = _abiertos[i];
            if (f.Desbordado) continue;
            f.NumFx++;
            f.BytesW += ws.Count;
            if (f.BytesW > _maxW || f.Fx.Count >= _maxFx)
            { f.Desbordado = true; f.Fx.Clear(); f.Pre.Clear(); continue; }
            long d = (insnAhora - f.InsnAlAbrir) - (isrAhora - f.InsnIsrAlAbrir);
            f.Fx.Add(new Efecto { J = $"{{\"t\":\"q\",\"n\":{_vectorIrq},\"d\":{d}{w}}}" });
        }
    }

    /// <summary>Los nueve registros que una ISR puede devolver cambiados, en el orden del campo
    /// `rw`: AX BX CX DX SI DI BP DS ES. SP, SS, CS e IP no entran: los restaura el `iret` y el
    /// marco de quien llamó.</summary>
    static void LeeRegs(Cpu c, ushort[] d)
    {
        d[0] = c.AX; d[1] = c.BX; d[2] = c.CX; d[3] = c.DX;
        d[4] = c.SI; d[5] = c.DI; d[6] = c.BP; d[7] = c.DS; d[8] = c.ES;
    }

    /// <summary>Direcciones sueltas → tramos contiguos `["1E6F0","AABB"],…`, el mismo formato que
    /// el `pre`. Sin agrupar, un buffer de 20 KB serían 20.000 entradas de JSON.</summary>
    static string Tramos(SortedDictionary<int, byte> bytes)
    {
        var sb = new System.Text.StringBuilder();
        int ini = -1, ult = -2;
        var acc = new List<byte>();
        void Cierra()
        {
            if (ini < 0) return;
            if (sb.Length > 0) sb.Append(',');
            sb.Append($"[\"{ini:X}\",\"{Convert.ToHexString(acc.ToArray())}\"]");
            acc.Clear();
        }
        foreach (var kv in bytes)
        {
            if (kv.Key != ult + 1) { Cierra(); ini = kv.Key; }
            acc.Add(kv.Value);
            ult = kv.Key;
        }
        Cierra();
        return sb.ToString();
    }

    /// <summary>Tira los casos abiertos sin escribirlos: alguien de FUERA (un evento `P` de la
    /// cinta) acaba de cambiar la memoria, y su salida llevaria un cambio que ninguna instruccion
    /// del caso explica — un caso irreproducible que el juez contaria contra quien traduce.</summary>
    public static void DescartaAbiertos()
    {
        foreach (var f in _abiertos) DevuelveCodigo(f);
        _abiertos.Clear();
    }

    /// <summary>El primer instante de novedad que cae dentro de la ventana de una invocacion que
    /// empieza ahora y que esta unidad todavia no cubre, o -1.</summary>
    static long NovedadCercana(Ficha ficha)
    {
        if (_novedades.Length == 0 || ficha.PorNovedad >= _maxNov) return -1;
        long ahora = Interp.InsnCount;
        while (_novIdx < _novedades.Length && _novedades[_novIdx] <= ahora) _novIdx++;
        // La ventana: el doble de lo que ha durado el caso mas largo de esta unidad (y un minimo
        // para las que aun no tienen ninguno), sin pasar del tope general.
        long ventana = Math.Min(_ventanaNov, ficha.DuracionMax > 0 ? ficha.DuracionMax * 2 + 64 : _ventanaNov);
        for (int i = _novIdx; i < _novedades.Length && _novedades[i] <= ahora + ventana; i++)
            if (_novedades[i] > ficha.NovedadCubierta) return _novedades[i];
        return -1;
    }

    /// <summary>Un paso del bucle del intérprete: cierra lo que haya retornado y abre lo que
    /// empiece. Sólo se llama con <see cref="Activo"/> en true.</summary>
    public static void Paso(Cpu c, Memory m, ushort ip)
    {
        // 0. El INT de la instrucción anterior, que sólo ahora tiene resultado.
        if (_intPendiente >= 0) ConfirmaInt(c, m, ip);

        // 1. CERRAR. Los frames van ordenados por profundidad (SP decreciente), así que se
        //    examinan desde el final. Un frame cuyo SP ya fue superado ha terminado — bien por su
        //    ret, bien porque alguien de más arriba se llevó la pila por delante (y entonces el
        //    filtro de la dirección de retorno lo descarta).
        while (_abiertos.Count > 0)
        {
            var f = _abiertos[^1];
            if ((short)(c.SP - f.SpEntrada) <= 0) break;
            _abiertos.RemoveAt(_abiertos.Count - 1);
            // Una VUELTA que muere porque su contenedor retornó se quedó a medias: no hay estado
            // de salida que comparar con el de entrada. Se cuenta y se tira.
            // Una vuelta que muere porque su contenedor retorno se quedo de verdad a medias: la
            // pila ya no es la suya y no hay estado de salida comparable. Esa si se tira.
            if (f.EsVuelta) { _fichas[f.Key].VueltasAMedias++; DevuelveCodigo(f); continue; }
            Cierra(f, c, m, ip);
        }

        // 1b. SALIR DEL BUCLE. Un frame de VUELTA cuyo `ip` ya no está en el cuerpo del bucle, y
        //     con la pila a su nivel (no estamos dentro de una llamada suya), es una vuelta que no
        //     se va a completar: el programa salió por la condición de salida. Se descarta y se
        //     libera, o seguiría vivo hasta que retornase quien lo contiene y haría que toda vuelta
        //     de los bucles de fuera se viera "con alguien vivo por debajo".
        while (_abiertos.Count > 0)
        {
            var f = _abiertos[^1];
            if (!f.EsVuelta || c.SP < f.SpEntrada) break;
            if (c.CS == f.CodigoSeg && ip >= f.CuerpoIni && ip <= f.CuerpoFin) break;
            _abiertos.RemoveAt(_abiertos.Count - 1);
            // ESTA VUELTA SE SALIO DEL BUCLE, Y ESO TAMBIEN ES UN CASO. El par entrada/salida es
            // igual de honesto que el de una vuelta completa —misma pila, y el `ip` de salida
            // viaja en el estado— y es el UNICO que cubre la condicion de salida del bucle, que
            // es justo el camino que una traduccion tiene que acertar para que el juego siga.
            // Medido en un juego de prueba: tirarlos dejaba SEIS unidades con cero casos.
            f.SalioDelBucle = true;
            Cierra(f, c, m, ip);
        }

        // El SP más bajo de cada llamada viva: la cota MEDIDA del scratch de pila (ver Frame.SpMin).
        for (int i = 0; i < _abiertos.Count; i++)
        {
            var f = _abiertos[i];
            if (f.PilaEnDgroup && (uint)(c.SS << 4) == f.DsBase && c.SP < f.SpMin) f.SpMin = c.SP;
        }

        // 2. ABRIR. Con lista, cuando todas llenaron su cuota no queda nada que hacer y el
        //    grabador se apaga solo (coste cero el resto de la partida). En auto no se apaga:
        //    puede aparecer una función nueva en cualquier momento.
        if (!_auto && _novedades.Length == 0 && _fichas.Count > 0 && _fichas.Count == _sinCuota) { Activo = false; return; }
        int key = Interp.KeyOf(c, c.CS, ip);
        if (!_fichas.TryGetValue(key, out var ficha)) return;

        // Ya hay un frame de esta misma clave con esta misma pila. Para una LLAMADA es un bucle
        // interno que salta a su primera instrucción: es el mismo caso y no se abre otro. Para una
        // VUELTA es justo lo contrario — el programa ha vuelto a la cabecera, así que esa vuelta
        // ACABA aquí y la siguiente empieza aquí mismo.
        for (int i = _abiertos.Count - 1; i >= 0; i--)
        {
            if (_abiertos[i].Key != key || _abiertos[i].SpEntrada != c.SP) continue;
            if (!_abiertos[i].EsVuelta) return;
            // Sólo se cierra si es el frame más profundo. Si alguien abrió por debajo y sigue
            // vivo, la pila no se ha desmontado y esta vuelta no ha terminado de verdad: cerrarla
            // dejaría un par entrada/salida que no se corresponde, que es la clase de verde falso
            // que este grabador existe para no producir.
            if (i != _abiertos.Count - 1) { ficha.VueltasSucias++; return; }
            var vuelta = _abiertos[i];
            _abiertos.RemoveAt(i);
            Cierra(vuelta, c, m, ip);
            break;
        }

        // Con la cuota llena solo se abre si hay un instante de novedad cerca. Y esto va DESPUES de
        // cerrar la vuelta anterior, no antes: medido el 2026-09-23, con la comprobacion delante
        // una vuelta abierta por novedad no se cerraba al volver a la cabecera (la funcion salia
        // antes de mirar) y se grababa como un caso de muchas vueltas seguidas —de DX=38h a 0 en
        // FUN_1000_3186 de un juego de prueba—, mientras el juez paraba en la primera: 60 de los casos
        // de novedad de vueltas eran irreproducibles por el propio binario original.
        long porNovedad = -1;
        if (ficha.Grabados >= ficha.Cuota)
        {
            porNovedad = NovedadCercana(ficha);
            if (porNovedad < 0) return;
        }

        ficha.Llamadas++;
        uint dsBase = (uint)(c.DS << 4);
        if (dsBase + DgroupLen > m.Raw.Length) return;   // DS pegado al techo: sin ventana que grabar

        var mem = new byte[DgroupLen];
        Array.Copy(m.Raw, dsBase, mem, 0, DgroupLen);
        _abiertos.Add(new Frame
        {
            PorNovedad = porNovedad,
            Key = key,
            SpEntrada = c.SP,
            RetIp = m.RdU16(c.SS, c.SP),
            DsBase = dsBase,
            Entrada = Estado(c, ip),
            Mem = mem,
            InsnAlAbrir = Interp.InsnCount,
            InsnIsrAlAbrir = _insnIsrTotal,
            SpMin = c.SP,
            EsVuelta = ficha.EsVuelta,
            CuerpoIni = ip,
            // Si el cuerpo se sale del segmento, el fin se queda en el tope: con el wrap de 16
            // bits el rango daria la vuelta y toda instruccion pareceria estar fuera del bucle.
            CuerpoFin = ip + ficha.Alcance > 0xFFFF ? (ushort)0xFFFF : (ushort)(ip + ficha.Alcance),
            PilaEnDgroup = (uint)(c.SS << 4) == dsBase,
            CodigoSeg = c.CS,
            CodigoLargo = LargoCodigo(m, c.CS, dsBase),
            CodigoHuella = Huella(m, (uint)(c.CS << 4), LargoCodigo(m, c.CS, dsBase)),
            CodigoBytes = CopiaCodigo(m, c.CS, LargoCodigo(m, c.CS, dsBase)),
        });
    }

    /// <summary>Ventana de código que viaja con un caso: los 64 KB que el CS de la llamada puede
    /// alcanzar sin cambiar de segmento, que es donde vive su cuerpo y todo lo que llame `near`.
    /// Lo que quede fuera sigue saliendo del `mundo.bin`.</summary>
    const int CodigoLen = 0x10000;

    /// <summary>FNV-1a de 64 bits sobre la ventana de código. No hace falta criptografía: sólo
    /// distinguir un overlay de otro, y que dos ventanas iguales compartan fichero.</summary>
    static ulong Huella(Memory m, uint baseLin, int largo)
    {
        ulong h = 14695981039346656037UL;
        int fin = (int)Math.Min(baseLin + (uint)largo, (uint)m.Raw.Length);
        for (int i = (int)baseLin; i < fin; i++) { h ^= m.Raw[i]; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>La ventana de código que hay que llevarse con el caso: desde el CS de la llamada
    /// hasta donde empieza el DGROUP (o 64 KB, lo que llegue antes).
    ///
    /// <para>Si el DGROUP empieza en el CS o por debajo, la ventana es CERO y no se graba nada:
    /// esos 64 KB ya viajan en el caso como DGROUP —código y datos comparten segmento, que es lo
    /// normal en un .COM o en el modelo small— y grabarlos otra vez sería duplicar el caso entero
    /// y, peor, declararlo «volátil» en cuanto un dato cambiara.</para></summary>
    static int LargoCodigo(Memory m, ushort cs, uint dsBase)
    {
        uint ini = (uint)(cs << 4);
        if (dsBase <= ini) return 0;
        uint fin = Math.Min(ini + CodigoLen, dsBase);
        if (fin > m.Raw.Length) fin = (uint)m.Raw.Length;
        return fin > ini ? (int)(fin - ini) : 0;
    }

    /// <summary>Ventanas de código ya volcadas a disco, para no escribir la misma dos veces.</summary>
    static readonly HashSet<ulong> _codigosVolcados = [];

    /// <summary>La foto de la ventana de código al abrir la llamada, del pool de buffers.</summary>
    static byte[]? CopiaCodigo(Memory m, ushort cs, int largo)
    {
        if (largo <= 0) return null;
        if (!_libres.TryPop(out var buf)) buf = new byte[DgroupLen];
        Array.Copy(m.Raw, (uint)(cs << 4), buf, 0, largo);
        return buf;
    }

    /// <summary>Devuelve al pool el buffer de la ventana de código de un frame que se tira sin
    /// pasar por <see cref="CierraInterno"/>.</summary>
    static void DevuelveCodigo(Frame f)
    {
        if (f.CodigoBytes != null) { _libres.Push(f.CodigoBytes); f.CodigoBytes = null; }
    }

    static void Cierra(Frame f, Cpu c, Memory m, ushort ip)
    {
        try { CierraInterno(f, c, m, ip); }
        finally
        {
            // El buffer de la ventana de codigo vuelve al pool pase lo que pase: un cierre sucio
            // o un caso desbordado tambien lo pidieron.
            if (f.CodigoBytes != null) { _libres.Push(f.CodigoBytes); f.CodigoBytes = null; }
        }
    }

    static void CierraInterno(Frame f, Cpu c, Memory m, ushort ip)
    {
        var ficha = _fichas[f.Key];

        // El filtro de honestidad: se vuelve por donde se llamó, o no es un caso de esta función.
        // Una VUELTA no tiene dirección de retorno — su honestidad es otra y ya está comprobada
        // por quien la cierra: se ha vuelto a la MISMA cabecera con la MISMA pila y sin nadie vivo
        // por debajo.
        if (!f.EsVuelta && ip != f.RetIp) { ficha.CierresSucios++; return; }
        if (f.PorNovedad >= 0)
        {
            // Solo vale si CONTIENE el instante nuevo: se abrio a ciegas, por cercania.
            if (Interp.InsnCount < f.PorNovedad || f.PorNovedad <= ficha.NovedadCubierta) return;
        }
        else if (ficha.Grabados >= ficha.Cuota) return;
        // El presupuesto: una función que abarca la partida entera no es un caso de prueba.
        if (f.Desbordado || Interp.InsnCount - f.InsnAlAbrir > _maxInsn) { ficha.Desbordados++; return; }

        var salida = new byte[DgroupLen];
        Array.Copy(m.Raw, f.DsBase, salida, 0, DgroupLen);

        int ghidraOffset = f.Key - OverlayDirectory.ResidentBase;
        string dir = Path.Combine(_dir, $"FUN_1000_{ghidraOffset:x}");
        Directory.CreateDirectory(dir);
        string pre = SerializaPre(f.Pre);
        // El contador de repeticiones va como campo "c" del propio efecto; sin "c" vale 1.
        string fxs = string.Join(",", f.Fx.Select(e =>
            e.C == 1 ? e.J : e.J[..^1] + $",\"c\":{e.C}}}"));
        // `spmin` va SIEMPRE que la pila viva en el DGROUP, aunque no haya ni efectos ni
        // contexto: es lo que el juez necesita para no exigir igualdad en tierra de nadie.
        string spmin = f.PilaEnDgroup ? $",\"spmin\":{f.SpMin}" : "";
        // La ventana de código de ESTE caso, deduplicada por su huella. Se vuelca al cerrar y no
        // al abrir: un caso que se descarta (cierre sucio, desbordado) no deja fichero.
        string codigo = VuelcaCodigo(f, m);
        // `bucle` va SIEMPRE que el caso sea una vuelta, aunque no haya efectos ni contexto: es lo
        // que le dice al juez que este caso no termina en un `ret` sino al volver a la cabecera, y
        // sin eso el replay daría vueltas hasta agotar el presupuesto.
        // El VALOR de `bucle` es el tamano del cuerpo. El replay lo necesita: una vuelta termina
        // al volver a la cabecera O al salir del cuerpo, y sin el cuerpo no sabria lo segundo.
        string bucle = f.EsVuelta ? $",\"bucle\":{f.CuerpoFin - f.CuerpoIni}" : "";
        string efectos = (f.Fx.Count == 0 && pre.Length == 0 && spmin.Length == 0
                          && codigo.Length == 0 && bucle.Length == 0) ? ""
            : "{\"v\":1,\"pre\":[" + pre + "],\"fx\":[" + fxs + "]" + spmin + codigo + bucle + "}";
        File.WriteAllBytes(Path.Combine(dir, $"{ficha.Grabados:D4}.pfc"),
            SerializaPfc1(ghidraOffset, _abiertos.Count, f.DsBase, f.Entrada, Estado(c, ip), f.Mem, salida, efectos));
        ficha.Grabados++;
        ficha.DuracionMax = Math.Max(ficha.DuracionMax, Interp.InsnCount - f.InsnAlAbrir);
        if (f.SalioDelBucle) ficha.Salidas++;
        if (f.PorNovedad >= 0)
        {
            ficha.PorNovedad++;
            // Todos los instantes nuevos que caen dentro de este caso quedan cubiertos por el.
            ficha.NovedadCubierta = Interp.InsnCount;
        }
        else if (ficha.Grabados >= ficha.Cuota) _sinCuota++;
    }

    /// <summary>Escribe la ventana de código del caso si no estaba ya, y devuelve el trozo de
    /// JSON que la referencia. La huella se recalcula sobre la memoria de AHORA sólo para
    /// comprobar que el código no cambió durante la llamada: si cambió (un overlay se recargó a
    /// media función), el caso no se puede acompañar de UNA ventana y se dice con `codvolatil`.</summary>
    static string VuelcaCodigo(Frame f, Memory m)
    {
        uint baseLin = (uint)(f.CodigoSeg << 4);
        if (f.CodigoLargo <= 0 || f.CodigoBytes == null) return "";
        // Lo que viaja es la foto de ENTRADA, no la memoria de ahora. Si algo de esos 64 KB
        // cambió durante la llamada se dice con `codvolatil` —es informacion util para quien
        // diagnostique— pero la ventana se lleva igual: sin ella el caso pierde tambien su mundo
        // garantizado y salta todos sus `call far`, que es mucho peor que un dato desfasado en
        // una zona que la funcion a lo mejor ni mira.
        bool volatil = Huella(m, baseLin, f.CodigoLargo) != f.CodigoHuella;
        string nombre = $"{f.CodigoSeg:X4}-{f.CodigoHuella:X16}.bin";
        if (_codigosVolcados.Add(f.CodigoHuella))
        {
            string dir = Path.Combine(_dir, "codigo");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, nombre), f.CodigoBytes.AsSpan(0, f.CodigoLargo).ToArray());
        }
        return $",\"cod\":\"{nombre}\",\"codseg\":{f.CodigoSeg}" + (volatil ? ",\"codvolatil\":1" : "");
    }

    /// <summary>El contenedor PFC1, tal como lo documenta <c>PakuPort.Judge.Corpus.CorpusCase</c>:
    /// <c>"PFC1" | u32 ghidraOffset | u32 callDepth | u32 dsBase | entryCpu[76] | exitCpu[76] |
    /// u32 memLen | entryMem | exitMem | u32 effLen | effectsJson</c>.</summary>
    static byte[] SerializaPfc1(int ghidraOffset, int callDepth, uint dsBase,
                                byte[] entradaCpu, byte[] salidaCpu, byte[] entradaMem, byte[] salidaMem,
                                string efectosJson)
    {
        int memLen = entradaMem.Length;
        var eff = System.Text.Encoding.UTF8.GetBytes(efectosJson);
        var d = new byte[4 + 4 + 4 + 4 + EstadoLen * 2 + 4 + memLen * 2 + 4 + eff.Length];
        int p = 0;
        void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(p), v); p += 4; }
        void Bytes(byte[] b) { Array.Copy(b, 0, d, p, b.Length); p += b.Length; }
        U32(0x31434650);            // "PFC1"
        U32((uint)ghidraOffset);
        U32((uint)callDepth);
        U32(dsBase);
        Bytes(entradaCpu);
        Bytes(salidaCpu);
        U32((uint)memLen);
        Bytes(entradaMem);
        Bytes(salidaMem);
        U32((uint)eff.Length);
        Bytes(eff);
        return d;
    }

    /// <summary>El estado de la CPU en el layout de <c>CpuState</c> (76 B): 10×u32
    /// <c>eax ebx ecx edx esi edi ebp esp eip eflags</c>, 6×u16 <c>cs ds es fs gs ss</c>,
    /// 6×u32 con las bases de segmento (en modo real, segmento×16).</summary>
    static byte[] Estado(Cpu c, ushort ip)
    {
        var d = new byte[EstadoLen];
        void U(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        void H(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), v);
        U(0, c.AX); U(4, c.BX); U(8, c.CX); U(12, c.DX);
        U(16, c.SI); U(20, c.DI); U(24, c.BP); U(28, c.SP);
        U(32, ip); U(36, c.FlagsWord());
        H(40, c.CS); H(42, c.DS); H(44, c.ES); H(46, 0); H(48, 0); H(50, c.SS);
        U(52, (uint)(c.CS << 4)); U(56, (uint)(c.DS << 4)); U(60, (uint)(c.ES << 4));
        U(64, 0); U(68, 0); U(72, (uint)(c.SS << 4));
        return d;
    }

    /// <summary>Cierra la grabación y deja el índice en <c>indice.json</c>: por función, cuántas
    /// veces arrancó, cuántos casos se grabaron y cuántos cierres salieron sucios. El índice es
    /// parte del expediente, no un log: dice de qué funciones hay banco de pruebas y de cuáles no,
    /// que es justo lo que la IA necesita saber antes de fiarse.</summary>
    public static void Termina()
    {
        if (_dir.Length == 0 || !Activo) return;
        Activo = false;

        // EL MUNDO DE CÓDIGO, una vez. Un caso no se puede repetir sólo con su DGROUP: la función
        // llama a otras, y ese código no está en el EXE tal como se carga — hay overlays, hay
        // segmentos que el juego mueve, y en un juego de prueba la imagen canónica son 1.964 bytes
        // contra un juego entero. Sin esto el replay salta a memoria vacía y "ejecuta datos": 141
        // de 237 funciones morían con un opcode inventado que en realidad era basura. Es UN fichero
        // por captura, no por caso.
        // Límite declarado: es una foto del final. Un overlay que ya se descargó no está en ella.
        if (Memory.Last != null)
            File.WriteAllBytes(Path.Combine(_dir, "mundo.bin"), Memory.Last.Raw);

        var filas = _fichas
            .Where(kv => kv.Value.Llamadas > 0 || kv.Value.Grabados > 0)
            .OrderByDescending(kv => kv.Value.Grabados)
            .Select(kv => new
            {
                func = $"FUN_1000_{kv.Key - OverlayDirectory.ResidentBase:x}",
                offset = $"0x{kv.Key - OverlayDirectory.ResidentBase:X}",
                // Qué es cada carpeta: una LLAMADA (entra por su primera instrucción y sale por su
                // `ret`) o una VUELTA de bucle (entra por la cabecera y sale al volver a ella con
                // la misma pila). El nombre de la carpeta es la clave canónica de la dirección, no
                // una afirmación de que ahí empiece una función: esto es lo que lo dice.
                unidad = kv.Value.EsVuelta ? "vuelta" : "llamada",
                alcance_del_bucle = kv.Value.EsVuelta ? kv.Value.Alcance : 0,
                llamadas = kv.Value.Llamadas,
                casos = kv.Value.Grabados,
                casos_por_novedad = kv.Value.PorNovedad,
                // Por que una fila puede tener 0 casos. Sin esto, la IA que lee el indice ve una
                // entrada vacia y no sabe si es un hueco del grabador o un hecho del programa.
                sin_casos_porque = kv.Value.Grabados > 0 ? null
                    : kv.Value.Desbordados > 0
                        ? "cada invocacion pasa del presupuesto de un caso: dura demasiado"
                        : kv.Value.EsVuelta && kv.Value.VueltasAMedias > 0
                            ? "toda vuelta murio porque quien contiene el bucle retorno a media " +
                              "vuelta: la pila ya no era la suya y no hay estado de salida que " +
                              "comparar"
                            : kv.Value.CierresSucios > 0
                                ? "no retorna por donde se la llamo"
                                : "se vio arrancar pero no se cerro ni una vez",
                cierres_sucios = kv.Value.CierresSucios,
                vueltas_sucias = kv.Value.VueltasSucias,
                vueltas_a_medias = kv.Value.VueltasAMedias,
                casos_que_salen_del_bucle = kv.Value.Salidas,
                desbordados = kv.Value.Desbordados,
            }).ToList();

        var resumen = new
        {
            funciones_candidatas = _fichas.Count,
            funciones_vistas = filas.Count,
            funciones_con_casos = filas.Count(f => f.casos > 0),
            llamadas_con_casos = filas.Count(f => f.casos > 0 && f.unidad == "llamada"),
            bucles_con_casos = filas.Count(f => f.casos > 0 && f.unidad == "vuelta"),
            casos_totales = filas.Sum(f => f.casos),
            casos_por_novedad = filas.Sum(f => f.casos_por_novedad),
            instantes_de_novedad = _novedades.Length,
            cierres_sucios = filas.Sum(f => f.cierres_sucios),
            vueltas_sucias = filas.Sum(f => f.vueltas_sucias),
            vueltas_a_medias = filas.Sum(f => f.vueltas_a_medias),
            casos_que_salen_del_bucle = filas.Sum(f => f.casos_que_salen_del_bucle),
            desbordados = filas.Sum(f => f.desbordados),
            alcance_minimo_de_bucle = _bucleMin,
            presupuesto_por_caso = $"{_maxFx} efectos o {_maxInsn:N0} instrucciones",
            frames_sin_cerrar = _abiertos.Count,
            modo = _auto
                ? (_bucleMin > 0
                    ? $"auto (llamadas aprendidas de los `call`, vueltas de los saltos hacia atras de >= {_bucleMin} B)"
                    : "auto (solo llamadas, aprendidas de los `call`)")
                : "lista explícita",
            cuota_por_funcion = _cuota,
            ventana_memoria = $"DGROUP {DgroupLen} B desde el DS de entrada",
            funciones = filas,
        };
        File.WriteAllText(Path.Combine(_dir, "indice.json"),
            System.Text.Json.JsonSerializer.Serialize(resumen, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"[CORPUS] {resumen.casos_totales} casos de {resumen.funciones_con_casos} funciones " +
                                $"({resumen.funciones_vistas} vistas de {resumen.funciones_candidatas} candidatas, " +
                                $"{resumen.cierres_sucios} cierres sucios) -> {_dir}");
    }
}
