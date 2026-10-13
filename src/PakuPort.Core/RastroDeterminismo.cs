namespace PakuPort.Core;

/// <summary>
/// `PAKU_RASTRO=&lt;fichero&gt;` — EL RASTRO DE UNA CORRIDA, para saber DONDE empiezan a
/// diferenciarse dos que deberian ser la misma.
///
/// <para><b>Para que existe.</b> Comparar el estado FINAL de dos corridas dice que difieren, no
/// por que: cuando la memoria del invitado sale con unos bytes distintos, el sintoma esta a
/// millones de instrucciones de la causa. Este rastro anota, cada 4096 instrucciones, donde
/// estaba el programa y con que registros. Un `diff` de los dos ficheros da la PRIMERA
/// instruccion en que las dos corridas dejan de ser la misma, y ahi si se puede mirar que leyo
/// el juego.</para>
///
/// <para>El eje es <see cref="Cpu.TickCount"/>, que avanza igual en las dos corridas, asi que las
/// lineas se corresponden una a una mientras no haya divergencia — no hace falta alinear nada.</para>
///
/// <para><b>Coste apagado: cero en el camino caliente.</b> Viaja en el poll periodico de
/// <see cref="Cpu.Step"/>, que ya se tomaba; apagado es una lectura de un campo estatico.</para>
/// </summary>
public static class RastroDeterminismo
{
    static readonly string? Ruta = Environment.GetEnvironmentVariable("PAKU_RASTRO");
    public static readonly bool Activo = !string.IsNullOrEmpty(Ruta);

    static StreamWriter? _w;
    // Un evento del hardware puede venir del hilo del host (el PIC lo levanta desde alli tambien),
    // asi que la escritura va con candado: esto es diagnostico, no puede tumbar la corrida.
    static readonly object _cerrojo = new();

    /// <summary>Un evento del hardware con el instante en que ocurrio (la IRQ que se levanta, por
    /// ejemplo). Es lo que separa "el juego hace otra cosa" de "le entra otra cosa": la causa de
    /// una divergencia esta casi siempre en una entrada, no en el codigo.</summary>
    public static void AnotaEvento(string que)
    {
        lock (_cerrojo) { Abre(); _w!.WriteLine($"{Cpu.TickCount} {que}"); }
    }

    static void Abre()
    {
        if (_w != null) return;
        _w = new StreamWriter(Ruta!) { AutoFlush = false };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _w.Flush(); } catch { } };
    }

    public static void Anota(Cpu c)
    {
        lock (_cerrojo) { Abre(); AnotaLinea(c); }
    }

    static void AnotaLinea(Cpu c)
    {
        _w!.WriteLine($"{Cpu.TickCount} {Interp.CurrentPc >> 16:X4}:{Interp.CurrentPc & 0xFFFF:X4} ax={c.AX:X4} bx={c.BX:X4} cx={c.CX:X4} " +
                     $"dx={c.DX:X4} si={c.SI:X4} di={c.DI:X4} bp={c.BP:X4} sp={c.SP:X4} ds={c.DS:X4} es={c.ES:X4}" +
                     Memoria());
    }

    /// <summary>`PAKU_RASTRO_MEM=&lt;lineal hex&gt;:&lt;bytes&gt;[,...]`: unos bytes del invitado
    /// en cada linea del rastro. Los registros dicen DONDE esta el programa; esto dice en que
    /// ESTADO esta la partida (la fase, el modo de control, un contador), que es lo que hace falta
    /// para saber si una cinta llega a donde se queria.</summary>
    static readonly (int lin, int n)[] Trozos =
        (Environment.GetEnvironmentVariable("PAKU_RASTRO_MEM") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.Split(':'))
        .Select(p => (Convert.ToInt32(p[0], 16), p.Length > 1 ? int.Parse(p[1]) : 1)).ToArray();

    static string Memoria()
    {
        var m = Memory.Last;
        if (Trozos.Length == 0 || m == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var (lin, n) in Trozos)
        {
            sb.Append($" m{lin:X5}=");
            for (int i = 0; i < n; i++) sb.Append(m.Raw[lin + i].ToString("X2"));
        }
        return sb.ToString();
    }
}
