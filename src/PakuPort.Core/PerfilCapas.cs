namespace PakuPort.Core;

/// <summary>
/// PERFIL DE CAPAS — quién hace qué, medido, no supuesto.
///
/// <para>Un port de PakuPort son miles de funciones planas. Para la IA que viene detrás, la
/// pregunta que ordena todo lo demás es cuál de ellas es <b>la lógica del juego</b>, que es lo
/// único que un remake necesita de verdad: lo que pinta se va a redibujar, lo que suena se va a
/// resintetizar y lo que lee ficheros se va a reescribir contra el sistema de hoy.</para>
///
/// <para><b>El criterio es el de la procedencia, y es una medida:</b> una función es de la capa
/// de render porque <b>escribió en la VRAM</b>, no porque se llame «draw»; es de música porque
/// <b>tocó el puerto 0x388</b>; es de E/S porque <b>pasó por INT 21h</b>. Lo que no hace ninguna
/// de esas cosas es lógica. Un nombre inventado por parecido lo puede desmentir el binario; esto
/// no, porque es lo que el binario hizo.</para>
///
/// <para><b>Cómo se atribuye.</b> Cada evento se apunta con el <c>CS:IP</c> del que lo ejecutó
/// (<see cref="Interp.LastPc"/>, el mismo anillo que la caja negra), y quien agrega convierte esa
/// dirección en función. Se registra la dirección LINEAL y no la clave de función: la fórmula que
/// convierte una en otra necesita <c>LoadBase</c> e <c>ImageBias</c>, que el corpus ya guarda, y
/// resolverlo aquí ataría el fichero al juego que lo grabó.</para>
///
/// <para><b>Límite declarado:</b> esto atribuye el evento a quien lo EJECUTA, no a quien lo pide.
/// Si una función de lógica llama a la rutina de dibujo, la de render es la segunda — que es lo
/// correcto para separar capas, pero significa que un envoltorio de una línea sale como lógica.
/// El expediente lo cruza con «a qué llama» para eso.</para>
///
/// <para>Palanca: <c>PAKU_CAPAS=&lt;fichero.csv&gt;</c>. Apagado cuesta una lectura de estático.</para>
/// </summary>
public static class PerfilCapas
{
    /// <summary>Leído en cada evento externo (no en cada instrucción): un estático y un salto.</summary>
    public static bool Activo;

    public const byte TipoVram = 0;
    public const byte TipoOut = 1;
    public const byte TipoIn = 2;
    public const byte TipoInt = 3;

    static string _ruta = "";
    /// <summary>clave = tipo(8) | detalle(16) | lineal(20) → veces. El detalle es el puerto o el
    /// número de INT; para la VRAM es 0.</summary>
    static readonly Dictionary<long, long> _cuenta = [];
    static bool _enganchado;

    /// <summary>PAKU_CAPAS=&lt;fichero.csv&gt;: perfila QUIEN hace cada cosa durante una partida
    /// real — quien escribe en la VRAM, quien toca cada puerto y quien llama a cada INT—, apuntado
    /// al CS:IP que lo ejecuta. Es lo que permite decir, con una medida y no con un parecido de
    /// nombre, que funcion es render, cual es audio, cual es E/S y cual es LOGICA, que es lo unico
    /// que un remake necesita reescribir. Lo agrega `pakuport expediente capas`.</summary>
    public static string? ArrancaDesdeEntorno()
    {
        string? ruta = Environment.GetEnvironmentVariable("PAKU_CAPAS");
        if (string.IsNullOrWhiteSpace(ruta)) return null;
        _ruta = ruta;
        _cuenta.Clear();
        Activo = true;
        if (!_enganchado)
        {
            _enganchado = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Vuelca();
        }
        return $"[CAPAS] perfilando quien escribe VRAM, quien toca puertos y quien llama a INT -> {_ruta}";
    }

    /// <summary>Apunta un evento externo a nombre de la instrucción que lo está ejecutando.</summary>
    public static void Nota(byte tipo, ushort detalle)
    {
        uint pc = Interp.LastPc;
        int lineal = (int)(((pc >> 16) << 4) + (pc & 0xFFFF)) & 0xFFFFF;
        long clave = ((long)tipo << 36) | ((long)detalle << 20) | (uint)lineal;
        _cuenta.TryGetValue(clave, out long n);
        _cuenta[clave] = n + 1;
    }

    /// <summary>Vuelca el perfil como CSV: <c>tipo,detalle,cs,ip,lineal,veces</c>. El CS:IP se
    /// reconstruye como <c>lineal&gt;&gt;4 : lineal&amp;15</c> porque lo que importa es la
    /// dirección, no el par concreto con el que se alcanzó.</summary>
    public static void Vuelca()
    {
        if (!Activo || _cuenta.Count == 0) return;
        Activo = false;
        string[] nombres = ["vram", "out", "in", "int"];
        using var w = new StreamWriter(_ruta);
        w.WriteLine("tipo,detalle,lineal,veces");
        foreach (var kv in _cuenta.OrderByDescending(k => k.Value))
        {
            byte tipo = (byte)(kv.Key >> 36);
            int detalle = (int)((kv.Key >> 20) & 0xFFFF);
            int lineal = (int)(kv.Key & 0xFFFFF);
            w.WriteLine($"{(tipo < nombres.Length ? nombres[tipo] : tipo.ToString())},{detalle:X},{lineal:X5},{kv.Value}");
        }
        Console.Error.WriteLine($"[CAPAS] {_cuenta.Count} pares (evento, direccion) -> {_ruta}");
    }
}
