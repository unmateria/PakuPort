namespace PakuPort.Core;

/// <summary>
/// LA FRONTERA DE LO DESCONOCIDO — qué bytes de código se ejecutaron de verdad, y cuáles no.
///
/// <para>Es el antídoto contra la alucinación, y por eso el plan del expediente lo pide con esas
/// palabras (día 4 del plan): <b>la IA necesita saber dónde NO hay datos</b>,
/// porque ahí es exactamente donde inventa con aplomo. Un comentario que dice «esta rama comprueba
/// si el jugador tiene combustible» es una conjetura si esa rama no se tomó nunca; y hasta hoy nada
/// distinguía las dos cosas.</para>
///
/// <para><b>Qué se mide, exactamente:</b> el rango completo de cada instrucción ejecutada
/// (<c>[lineal, lineal+longitud)</c>), no sólo su primer byte. Marcar sólo el primero dejaría los
/// bytes de en medio como «no ejecutados» y la frontera saldría llena de agujeros que no existen.
/// De regalo, sale la métrica de cobertura del porteo.</para>
///
/// <para><b>Límite declarado, y es el mismo que el del corpus:</b> esto engancha en
/// <see cref="Interp"/>, el mundo de 16 bits. El mundo plano de 32 bits (los juegos DOS/4GW y
/// los de extensor embebido) no pasa por aquí y su cobertura NO se mide — lo que cae fuera del megabyte se
/// cuenta aparte y se dice en la cabecera del volcado, en vez de contarse como si no existiera.</para>
///
/// <para>Palanca: <c>PAKU_COBERTURA=&lt;fichero&gt;</c>. Apagada cuesta una lectura de estático y
/// un salto por instrucción, igual que <see cref="PerfilCapas"/>.</para>
/// </summary>
public static class Cobertura
{
    /// <summary>Leído una vez por instrucción; el bool va delante para que apagado no cueste más.</summary>
    public static bool Activo;

    static byte[]? _mapa;          // un byte por dirección lineal del espacio de 1 MiB
    static string _ruta = "";
    static long _instrucciones, _fueraDelMega;
    static bool _enganchado;
    static uint _prevLineal, _prevFin, _prevDestino = SinSalto;
    static long _prevInsn;
    /// <summary>Lados de salto condicional ya vistos: (lineal del salto &lt;&lt; 1) | tomado.</summary>
    static readonly HashSet<ulong> _lados = [];

    /// <summary>PAKU_COBERTURA=&lt;fichero&gt;: marca el rango de cada instruccion que el
    /// interprete ejecuta y al terminar vuelca los TRAMOS de codigo pisados. Es la frontera de lo
    /// desconocido del expediente: lo que no aparece aqui no se ejecuto nunca, y sobre eso la IA
    /// no tiene datos. Solo mide el mundo de 16 bits; lo de fuera se cuenta y se declara.</summary>
    public static string? ArrancaDesdeEntorno()
    {
        string? ruta = Environment.GetEnvironmentVariable("PAKU_COBERTURA");
        if (string.IsNullOrWhiteSpace(ruta)) return null;
        _ruta = ruta;
        _mapa = new byte[1 << 20];
        _instrucciones = _fueraDelMega = 0;
        _prevDestino = SinSalto;
        _lados.Clear();
        Activo = true;
        if (!_enganchado)
        {
            _enganchado = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Vuelca();
        }
        return $"[COBERTURA] marcando el codigo ejecutado (mundo de 16 bits) -> {_ruta}";
    }

    /// <summary>Valor de <c>destinoCond</c> para una instrucción que no es un salto condicional.</summary>
    public const uint SinSalto = uint.MaxValue;

    /// <summary>Marca los <paramref name="largo"/> bytes que ocupa la instrucción.
    /// <paramref name="destinoCond"/> es la lineal a la que salta si es un salto condicional
    /// (<c>jcc</c>, <c>loop</c>, <c>jcxz</c>), o <see cref="SinSalto"/>.</summary>
    public static void Marca(uint lineal, int largo, uint destinoCond)
    {
        _instrucciones++;
        var mapa = _mapa;
        if (mapa == null) return;
        // NOVEDAD POR SALTO. Una direccion nueva no basta: la SALIDA de un bucle casi nunca
        // ejecuta nada que no se hubiera ejecutado antes (cae en codigo ya visto), asi que con la
        // novedad por direccion las salidas se quedaban sin casos — medido en un juego de prueba,
        // cero en tres bucles. Lo nuevo ahi es el LADO del salto: la primera vez que un salto
        // condicional cae por cada uno de sus dos lados. Se apunta con el instante DEL SALTO y no
        // del destino: la vuelta que sale se cierra al llegar al destino, antes de que el contador
        // avance, y un instante posterior al cierre no lo cubre ningun caso.
        // Solo cuenta si se llego al destino por el salto (cayo detras o fue a su objetivo): una
        // IRQ que entra justo despues no es un lado del salto.
        if (_prevDestino != SinSalto && (lineal == _prevFin || lineal == _prevDestino)
            && _lados.Add(((ulong)_prevLineal << 1) | (lineal == _prevFin ? 0UL : 1UL)))
            _novedades.Add((_prevInsn, lineal));
        _prevLineal = lineal; _prevFin = lineal + (uint)largo; _prevDestino = destinoCond;
        _prevInsn = Interp.InsnCount;
        if (lineal >= (uint)mapa.Length) { _fueraDelMega++; return; }
        // LA PRIMERA VEZ que se ejecuta una instruccion se apunta con su instante (el contador de
        // instrucciones del interprete). Es la primera pasada del corpus guiado por cobertura: la
        // segunda, con la misma cinta, graba las invocaciones que pasan por esos instantes (ver
        // CorpusRecorder, PAKU_CORPUS_NOVEDADES), y asi cada camino que la partida recorrio tiene
        // al menos un caso — no solo los de las ocho primeras llamadas.
        if (mapa[lineal] == 0) _novedades.Add((Interp.InsnCount, lineal));
        uint fin = Math.Min(lineal + (uint)largo, (uint)mapa.Length);
        for (uint a = lineal; a < fin; a++) mapa[a] = 1;
    }

    /// <summary>Vuelca los tramos contiguos de código ejecutado. Texto, una línea por tramo, para
    /// que se pueda leer, diffear y meter en un `git diff` sin herramientas.</summary>
    static readonly List<(long insn, uint lineal)> _novedades = [];

    public static void Vuelca()
    {
        var mapa = _mapa;
        if (mapa == null) return;
        _mapa = null;
        Activo = false;
        try
        {
            using var wn = new StreamWriter(_ruta + ".novedades");
            wn.WriteLine("# instruccion en que se ejecuto POR PRIMERA VEZ cada direccion, o en que un salto");
            wn.WriteLine("# condicional cayo POR PRIMERA VEZ por uno de sus dos lados (insn lineal-del-destino)");
            foreach (var (insn, lin) in _novedades) wn.WriteLine($"{insn} {lin:X5}");
        }
        catch (IOException) { }
        long bytes = 0;
        var tramos = new List<(int ini, int fin)>();
        for (int a = 0; a < mapa.Length; a++)
        {
            if (mapa[a] == 0) continue;
            int ini = a;
            while (a < mapa.Length && mapa[a] != 0) a++;
            tramos.Add((ini, a));
            bytes += a - ini;
        }
        try
        {
            using var w = new StreamWriter(_ruta);
            w.WriteLine($"# cobertura de codigo ejecutado (mundo de 16 bits)");
            w.WriteLine($"# instrucciones={_instrucciones} bytes={bytes} tramos={tramos.Count} " +
                        $"fuera_del_megabyte={_fueraDelMega}");
            w.WriteLine("inicio,fin,bytes");
            foreach (var (ini, fin) in tramos) w.WriteLine($"{ini:X5},{fin:X5},{fin - ini}");
        }
        catch (IOException) { return; }
        Console.Error.WriteLine($"[COBERTURA] {bytes:N0} bytes de codigo en {tramos.Count} tramos " +
                                $"({_instrucciones:N0} instrucciones) -> {_ruta}");
    }
}
