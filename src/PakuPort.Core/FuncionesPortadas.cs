namespace PakuPort.Core;

/// <summary>
/// LAS FUNCIONES QUE YA ESTAN TRADUCIDAS — el gancho que convierte el tablero del remake
/// (<c>pakuport expediente tablero</c>) en un juego que se puede jugar A MEDIO PORTAR.
///
/// <para><b>El hueco que tapa.</b> El juez ya media una traduccion contra las invocaciones reales
/// de su funcion, pero el port en marcha era todo o nada: o el interprete entero
/// (<c>interpOnly</c>), o el C# generado desde <c>Fn_00300</c>. No habia forma de decir "esta
/// funcion la hace mi C# y el resto lo sigue haciendo el original". Con esto, una funcion
/// traducida se ejecuta DE VERDAD dentro del juego y todo lo demas sigue siendo el binario,
/// interpretado — que es la doctrina normal de un porteo incremental y no cuesta fidelidad.</para>
///
/// <para><b>Por que el gancho va en la direccion de ENTRADA y no en el <c>call</c>.</b> Da igual
/// como se llegue: <c>call near</c>, <c>call far</c>, un salto de cola o un puntero indirecto.
/// Es la misma razon por la que el grabador del corpus abre el caso aqui y no en el
/// <c>case Call</c>.</para>
///
/// <para><b>Coste apagado: una comparacion contra null por instruccion</b>, igual que
/// <c>CorpusRecorder.Activo</c> o <c>ReplayIrq</c>. Encendido, una lectura de un bitmap (un bit
/// por direccion lineal): el bucle de 16 bits es el camino mas caliente que hay y un
/// <c>Dictionary</c> por instruccion no cabe ahi.</para>
///
/// <para><b>El contrato es el del banco</b>, sin cambiarlo: la traduccion entra con la direccion
/// de retorno ya en la pila y al salir deja <c>SP</c> como lo dejaria el <c>ret</c> original. Aqui
/// se lee ese retorno ANTES de llamar y se continua en el. Una traduccion que no ajuste la pila
/// descarrila el juego — y es justo lo que el juez caza antes de que llegue hasta aqui.</para>
/// </summary>
public static class FuncionesPortadas
{
    /// <summary>Lo que hay que saber de una traduccion para poder volver de ella.</summary>
    public sealed class Traducida
    {
        /// <summary>Una FUNCION: sale con su `ret`, y se continua en la direccion de retorno.</summary>
        public Action<Cpu, Memory>? Fn;
        /// <summary>Una VUELTA de bucle: no tiene direccion de retorno, asi que devuelve ELLA por
        /// donde sigue el programa — la cabecera si toca otra vuelta, o el sitio por el que se sale
        /// del bucle. Ver <see cref="RegistraVuelta"/>.</summary>
        public Func<Cpu, Memory, ushort>? Vuelta;
        public required string Nombre;
        /// <summary>La funcion original sale con <c>retf</c>: el marco lleva tambien CS.</summary>
        public bool Lejana;
        /// <summary>Bytes de argumentos que saca su `ret n` (0 para un `ret` normal).</summary>
        public int RetN;
        public long Veces;
        /// <summary>El reloj (<c>Cpu.TickCount</c>) de la primera y la más reciente vez que se uso. Es
        /// el MISMO reloj que sella cada linea de <c>blit.log</c>, y hace falta para saber si un
        /// juicio A/B llega siquiera a cubrir esta funcion: si la primera vez cae despues del
        /// tramo comparado, un verde ahi no dice nada de esta traduccion.</summary>
        public long TPrimera = -1, TReciente = -1;
        /// <summary>Veces que se rechazo ejecutarla por anidamiento (corte de recursion). Va al
        /// log porque es un fallo de la traduccion, no del juego, y hay que verlo.</summary>
        public long Recursivas;
        /// <summary>Vueltas que volvieron sin cobrar ni una instruccion (ver el gancho).</summary>
        public long SinRitmo;
        public bool EsVuelta => Vuelta != null;
    }

    static Dictionary<int, Traducida>? _porDireccion;
    static byte[]? _mapa;                       // un bit por direccion lineal: el descarte rapido
    const int TamMapa = 0x110000;               // 1 MB + 64 KB: todo lo direccionable en modo real

    /// <summary>La direccion lineal de un <c>seg:off</c> de modo real. NO lleva mascara, y esa es
    /// toda la historia: <c>&amp; (TamMapa - 1)</c> parecia un modulo y no lo es, porque 0x110000
    /// no es potencia de dos: el AND se limita a BORRAR el bit 0x10000 y hace que dos direcciones
    /// distintas caigan en la misma entrada. Medido en un juego de prueba: la traduccion registrada en
    /// <c>0814:03D3</c> (lineal 0x08513) se disparaba tambien en <c>134C:5053</c> (lineal 0x18513),
    /// que es codigo de OTRO overlay del juego; la traduccion leia de la pila un retorno que no era
    /// el suyo, el programa se iba a ejecutar los ceros del principio del segmento y acababa
    /// llamando al driver de sonido con basura (<i>Invalid function number 1816 to graphics
    /// driver</i>) y el juego a DOS. No hace falta mascara ninguna: el maximo posible es
    /// <c>0xFFFF0 + 0xFFFF = 0x10FFEF</c>, que ya cabe en el mapa.</summary>
    static int Lineal(int seg, int off) => (seg << 4) + off;

    /// <summary>Hay al menos una funcion traducida. Se mira una vez por instruccion.</summary>
    public static bool Activo => _mapa != null;

    /// <summary>`PAKU_SIN_PORTADAS=1` apaga TODAS las traducciones sin recompilar. Es lo que
    /// permite el control A/B honesto: la misma cinta, el MISMO ejecutable, y como unica
    /// diferencia si el gancho esta o no. Comparar dos binarios distintos no probaria nada.</summary>
    public static bool Apagadas => Environment.GetEnvironmentVariable("PAKU_SIN_PORTADAS") == "1";

    public static void Registra(ushort cs, ushort ip, Action<Cpu, Memory> fn, string nombre, bool lejana = false,
                                int retN = 0)
        => Anota(cs, ip, new Traducida { Fn = fn, Nombre = nombre, Lejana = lejana, RetN = retN });

    /// <summary>Registra la traduccion de UNA VUELTA de un bucle, con cabecera en
    /// <paramref name="cs"/>:<paramref name="ip"/>.
    ///
    /// <para><b>El hueco que tapa.</b> En un juego escrito a mano en ensamblador casi todo el
    /// trabajo vive en bucles que no son funciones: el bucle principal se llama una vez y no
    /// vuelve, y la pantalla de atraccion corre desde el punto de entrada sin que nadie la llame.
    /// El corpus ya graba esas vueltas y el juez ya las juzga; lo que no se podia era CORRERLAS en
    /// el juego, porque el gancho continuaba en la direccion de retorno de la pila y una vuelta no
    /// tiene ninguna. Aqui la traduccion devuelve por donde sigue, y el interprete continua ahi:
    /// si es la cabecera, el gancho la vuelve a coger y la vuelta siguiente tambien es C#.</para></summary>
    public static void RegistraVuelta(ushort cs, ushort ip, Func<Cpu, Memory, ushort> vuelta, string nombre)
        => Anota(cs, ip, new Traducida { Vuelta = vuelta, Nombre = nombre });

    /// <summary>Registra la traduccion del PROGRAMA ENTERO: la que empieza en la primera
    /// instruccion del juego y no vuelve nunca (la carpeta PROGRAMA del tablero).
    ///
    /// <para>Con ella el juego entero es C# del kit, y eso decide el motor: el gancho vive en el
    /// interprete, asi que el juego tiene que arrancar por el interprete aunque su export tenga
    /// por defecto el codigo lifteado de siempre (algunos juegos lo tienen). Lo mira la plantilla del
    /// host (<see cref="HayPrograma"/>). Con <c>PAKU_SIN_PORTADAS=1</c> no se registra nada y el
    /// juego arranca como siempre.</para></summary>
    public static void RegistraPrograma(ushort cs, ushort ip, Func<Cpu, Memory, ushort> programa)
    {
        if (Apagadas) return;
        HayPrograma = true;
        RegistraVuelta(cs, ip, programa, "PROGRAMA");
    }

    /// <summary>El remake trae el programa entero (ver <see cref="RegistraPrograma"/>).</summary>
    public static bool HayPrograma { get; private set; }

    static void Anota(ushort cs, ushort ip, Traducida t)
    {
        if (Apagadas) return;
        _porDireccion ??= new Dictionary<int, Traducida>();
        _mapa ??= new byte[TamMapa >> 3];
        int lineal = Lineal(cs, ip);
        _porDireccion[lineal] = t;
        _mapa[lineal >> 3] |= (byte)(1 << (lineal & 7));
    }

    /// <summary>La traduccion ejecutada mas reciente salia con `retf`. Lo lee el interprete cuando esa
    /// salida termina su Run (ver el gancho en <c>Interp.Run</c>).</summary>
    public static bool RecienteFueLejana;

    public static void Limpia()
    {
        _porDireccion = null;
        _mapa = null;
    }

    /// <summary>El descarte rapido del bucle: false para casi todas las instrucciones.</summary>
    public static bool Hay(ushort cs, ushort ip)
    {
        var mapa = _mapa;
        if (mapa == null) return false;
        int lineal = Lineal(cs, ip);
        return (mapa[lineal >> 3] & (1 << (lineal & 7))) != 0;
    }

    /// <summary>Ejecuta la traduccion y devuelve el <c>ip</c> en el que el interprete continua
    /// (el CS lo ajusta aqui si la funcion era lejana). El bitmap es solo el descarte rapido;
    /// la comprobacion de verdad es la del diccionario. Ojo: eso NO salvo del fallo de la
    /// mascara, porque el diccionario se indexaba con la misma cuenta (ver <see cref="Lineal"/>)
    /// y colisionaba igual.</summary>
    public static bool Ejecuta(Cpu c, Memory m, ref ushort ip)
    {
        if (_porDireccion == null) return false;
        int lineal = Lineal(c.CS, ip);
        if (!_porDireccion.TryGetValue(lineal, out var t)) return false;

        // CORTE DE RECURSION. Una traduccion llama a lo no portado con `o.Llama`, y eso vuelve a
        // entrar en el interprete: si por cualquier camino se llega otra vez a la direccion de
        // ENTRADA de una traduccion, esto se anida en la pila de C#. Y un StackOverflowException
        // de .NET NO SE PUEDE CAPTURAR: mata el proceso entero, sin log y sin volcado (el mismo
        // muro que ya obligo a poner Cpu.GuardStack para el codigo generado).
        //
        // MEDIDO el 2026-09-06 en un juego de prueba: la traduccion de `FUN_1000_375c` sale VERDE 8/8 en
        // el banco y a los ~7 M de instrucciones de partida se llama a si misma y MATA el juego.
        // El juez no puede cazar esto —mide la funcion contra invocaciones grabadas, no dentro de
        // la partida—, asi que la red tiene que estar aqui.
        //
        // CORRECCION del 2026-09-23: aquella "recursion" no era de la traduccion. Era `o.Llama`,
        // que dentro de la partida no volvia nunca (ver Original.HastaQueVuelva): el juego seguia
        // corriendo dentro de la llamada y volvia a pasar por la misma traduccion un nivel mas
        // abajo. El corte se queda como red, que para eso sigue valiendo.
        //
        // Pasado el tope se devuelve false: el interprete ejecuta el codigo ORIGINAL de esa
        // direccion, que es lo seguro. El juego sigue, degradado y DICIENDOLO, en vez de morir.
        // El corte fino primero: la que se repite EN LA PILA es la culpable, y cortarla a ella sola
        // deja trabajando a las demas. Con el tope global a secas, una traduccion recursiva
        // arrastraba a las cinco inocentes que tenia debajo — medido en un juego de prueba, `3335`,
        // `3efe`, `4012`, `4041` y `3b6a` salian rechazadas cientos de veces por culpa de `375c`.
        int mismas = 0;
        for (int i = 0; i < _profundidad; i++) if (ReferenceEquals(_pila[i], t.Nombre)) mismas++;
        if (mismas >= MaxMismaFuncion || _profundidad >= MaxProfundidad)
        {
            if (!_avisadoHondo)
            {
                _avisadoHondo = true;
                // Se dice QUIEN se repite en la pila, no solo quien toco fondo: la de arriba suele
                // ser una victima. La culpable es la que aparece muchas veces.
                var cuenta = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var n2 in _pila) if (n2 != null) cuenta[n2] = cuenta.GetValueOrDefault(n2) + 1;
                string culpables = string.Join(", ", cuenta.Where(kv => kv.Value > 1)
                    .OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"));
                Console.Error.WriteLine(
                    $"[PORTADAS] {t.Nombre} ya esta {mismas} vez(es) en la pila de traducciones " +
                    $"({c.CS:X4}:{ip:X4}). A partir de aqui la ejecuta el binario ORIGINAL." +
                    (culpables.Length > 0 ? $" Se repiten en la pila: {culpables}." : "") +
                    " Una traduccion que acaba llamandose a si misma es un fallo suyo, y el juez del" +
                    " banco NO lo puede ver: mide la funcion contra invocaciones grabadas, no dentro" +
                    " de la partida. Sin este corte el proceso moriria de desbordamiento de pila.");
            }
            t.Recursivas++;
            return false;
        }

        t.Veces++;
        if (t.TPrimera < 0) t.TPrimera = Cpu.TickCount;
        t.TReciente = Cpu.TickCount;
        RecienteFueLejana = t.Lejana;

        if (t.Vuelta != null)
        {
            long reloj0 = Cpu.TickCount;
            ushort sigue;
            _pila[_profundidad++] = t.Nombre;
            try { sigue = t.Vuelta(c, m); }
            finally { _pila[--_profundidad] = null!; }
            // UNA VUELTA QUE NO COBRA NADA CUELGA EL JUEGO. Si vuelve a su propia cabecera, el
            // gancho la vuelve a coger sin que el interprete llegue a dar un solo paso: el reloj
            // no avanza, no entra ninguna IRQ, y un bucle que espera un tic espera para siempre.
            // Una traduccion bien hecha cobra su ritmo (`o.Instrucciones`, y el juez lo mide);
            // esto es solo la red para la que no lo hace, y se dice en el log.
            if (Cpu.TickCount == reloj0)
            {
                t.SinRitmo++;
                if (!c.Step()) return false;
                Interp.InsnCount++;
            }
            ip = sigue;
            return true;
        }

        ushort spEntrada = c.SP;
        ushort ipRetorno = m.RdU16(c.SS, c.SP);
        ushort csRetorno = t.Lejana ? m.RdU16(c.SS, (ushort)(c.SP + 2)) : c.CS;

        _pila[_profundidad++] = t.Nombre;
        try { t.Fn!(c, m); }
        finally { _pila[--_profundidad] = null!; }

        // SALIDA POR UN ATAJO. Lo normal es que la funcion deje la pila justo encima de su
        // direccion de retorno —2 bytes mas arriba, 4 si es lejana, mas los n de un `ret n`— y se
        // sigue en la direccion que habia al entrar. Pero un juego en ensamblador tambien sale de
        // otra forma: un juego de prueba, al abandonar la partida, hace `pop ax` (tira su propia
        // direccion de retorno) y salta a un `ret` que vuelve directamente al MENU, dos niveles
        // mas arriba. Seguir en la direccion de entrada seria volver a un bucle del que el
        // original ya se fue. Si la pila no subio lo que sube un `ret`, se sigue por la palabra
        // que ese ultimo `ret` consumio de verdad, que sigue en memoria justo debajo del SP.
        int normal = (t.Lejana ? 4 : 2) + t.RetN;
        if ((ushort)(c.SP - spEntrada) != normal)
        {
            ipRetorno = m.RdU16(c.SS, (ushort)(c.SP - 2));
            csRetorno = c.CS;
        }

        if (t.Lejana) c.CS = csRetorno;
        ip = ipRetorno;
        return true;
    }

    /// <summary>Traducciones anidadas ahora mismo. Ver el corte de recursion en
    /// <see cref="Ejecuta"/>.</summary>
    static int _profundidad;
    static bool _avisadoHondo;
    /// <summary>Los nombres de las traducciones anidadas ahora mismo, para poder decir CUAL se
    /// repite cuando se toca el tope.</summary>
    static readonly string[] _pila = new string[MaxProfundidad];
    /// <summary>Tope de anidamiento. Holgado a proposito: una traduccion puede llamar a otra que
    /// llame a otra, y eso es legitimo. Lo que no lo es son cientos.</summary>
    const int MaxProfundidad = 64;
    /// <summary>Veces que una MISMA traduccion puede estar en la pila. Una traduccion recursiva de
    /// verdad existe (una funcion recursiva del juego), por eso no es 1; lo que no existe es que
    /// se anide sin fondo.</summary>
    const int MaxMismaFuncion = 8;

    /// <summary>El uso se vuelca al salir, a `logs/portadas.log`. Verde en el juez y CERO veces
    /// aqui son dos cosas distintas: la primera dice que la traduccion es correcta, la segunda que
    /// el juego ni siquiera pasa por ella en esa partida — y sin medirlo, un remake puede parecer
    /// que avanza sin que se ejecute una sola linea nueva.</summary>
    public static void Vuelca()
    {
        if (_porDireccion == null) return;
        try
        {
            Directory.CreateDirectory("logs");
            using var w = new StreamWriter(Path.Combine("logs", "portadas.log"));
            foreach (var t in _porDireccion.Values.OrderByDescending(x => x.Veces))
                w.WriteLine($"{t.Nombre} {(t.EsVuelta ? "vuelta" : "funcion")} veces={t.Veces} t_primera={t.TPrimera} t_reciente={t.TReciente}" +
                            (t.Recursivas > 0 ? $" RECHAZADAS_POR_ANIDAMIENTO={t.Recursivas} (esas veces la ejecuto el binario ORIGINAL)" : "") +
                            (t.SinRitmo > 0 ? $" SIN_RITMO={t.SinRitmo} (vueltas que no cobraron instrucciones: el reloj del juego no avanza con ellas)" : ""));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Lo que se imprime al arrancar el juego, o null si no hay ninguna traduccion.</summary>
    public static string? Aviso() =>
        _porDireccion == null
            ? (Apagadas ? "[PORTADAS] apagadas por PAKU_SIN_PORTADAS" : null)
            : $"[PORTADAS] {_porDireccion.Count} funciones traducidas a C#; el resto lo sigue " +
              "haciendo el binario original";

    /// <summary>Cuantas veces se ha usado cada traduccion durante la partida. Es la unica forma
    /// de saber si una funcion traducida se esta ejecutando de verdad o si el juego no pasa por
    /// ella: verde en el juez y cero veces aqui son dos cosas distintas.</summary>
    public static IEnumerable<(string nombre, long veces)> Uso() =>
        _porDireccion == null
            ? []
            : _porDireccion.Values.OrderByDescending(t => t.Veces).Select(t => (t.Nombre, t.Veces));
}
