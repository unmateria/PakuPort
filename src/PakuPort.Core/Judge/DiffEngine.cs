using PakuPort.Judge.Corpus;
using PakuPort.Judge.Oracle;

namespace PakuPort.Judge.Diff;

/// <summary>
/// Función del port bajo prueba para funciones de LÓGICA.
/// Recibe el DGROUP de entrada (mutable, ya cargado) y el estado de CPU de entrada
/// (params en registros). Debe mutar el buffer igual que lo haría el original.
/// </summary>
public delegate void PortLogicFunction(byte[] dgroup, in CpuState entry);

public sealed class DiffResult
{
    public bool Passed { get; init; }
    public List<DiffRange> MemoryDivergences { get; init; } = new();
    public string? Note { get; init; }

    public override string ToString()
    {
        if (Passed) return "PASA (idéntico al original)";
        var head = $"ROJO: {MemoryDivergences.Count} divergencia(s) de memoria";
        if (Note != null) head += $" — {Note}";
        var lines = MemoryDivergences.Take(8).Select(d => "    " + d);
        return head + "\n" + string.Join("\n", lines);
    }
}

/// <summary>
/// El verificador (Componente 6, modo offline). Re-pasa un caso del corpus por la función C#
/// del port y exige que el DGROUP resultante sea idéntico al exitMem grabado del original.
/// El binario decide; no hay opinión humana.
/// </summary>
public static class DiffEngine
{
    /// <summary>
    /// Banda de pila ALREDEDOR del SP de entrada que se IGNORA al comparar. En este juego SS==DS,
    /// así que el snapshot de 64KB incluye la pila. Entre la entrada y la salida de la función
    /// pueden saltar interrupciones (IRQ0 del timer) cuyo handler empuja/saca de la pila: deja
    /// scratch NO DETERMINISTA (depende de CUÁNDO salta el IRQ) por debajo Y por encima del SP
    /// (el SP varía entre entrada=sp y salida=sp+2). Eso NO es estado persistente del juego y debe
    /// excluirse de toda verificación determinista. El port reproduce los WRITES de DATOS (offsets
    /// bajos, lejos de la pila), no este scratch.  Por debajo: marco propio + frames de IRQ
    /// anidados; por encima: dir. de retorno + parámetros + scratch a la salida.
    /// </summary>
    public const int StackMaskBelow = 0x100;
    public const int StackMaskAbove = 0x80;

    /// <summary>Verifica UN caso. Carga entryMem, ejecuta el port, diffea contra exitMem.</summary>
    public static DiffResult ReplayCase(CorpusCase c, PortLogicFunction port)
    {
        // copia de trabajo del estado de entrada (no mutar el caso)
        var working = (byte[])c.EntryMem.Clone();
        port(working, c.EntryCpu);

        // Neutralizar la banda de pila transitoria alrededor del SP de entrada (scratch de
        // compilador + interrupciones): forzar igualdad en [sp - below, sp + above).
        int sp = (int)(c.EntryCpu.Esp & 0xFFFF);
        int lo = Math.Max(0, sp - StackMaskBelow);
        int hi = Math.Min(c.ExitMem.Length, sp + StackMaskAbove);
        for (int i = lo; i < hi; i++) working[i] = c.ExitMem[i];

        // Neutralizar registros no deterministas: cursor de ratón (IRQ ratón) + tick counter de
        // timer (IRQ0 / 0x9576-0x96DC). Ninguna función de lógica los escribe intencionadamente.
        working[0x5912] = c.ExitMem[0x5912];
        working[0x5913] = c.ExitMem[0x5913];
        working[0x5914] = c.ExitMem[0x5914];
        working[0x5915] = c.ExitMem[0x5915];
        working[0x9576] = c.ExitMem[0x9576];
        working[0x9577] = c.ExitMem[0x9577];
        working[0x96DC] = c.ExitMem[0x96DC];
        working[0x96DD] = c.ExitMem[0x96DD];

        var ranges = MemoryDiff.Compare(working, c.ExitMem);
        return new DiffResult
        {
            Passed = ranges.Count == 0,
            MemoryDivergences = ranges,
            Note = ranges.Count == 0 ? null : $"depth={c.CallDepth}",
        };
    }

    /// <summary>Verifica TODOS los casos. Verde solo si pasan todos (regla del plan).</summary>
    public static (bool allPassed, int passed, int total, List<DiffResult> failures)
        ReplayAll(IEnumerable<CorpusCase> cases, PortLogicFunction port)
    {
        int passed = 0, total = 0;
        var failures = new List<DiffResult>();
        foreach (var c in cases)
        {
            total++;
            var r = ReplayCase(c, port);
            if (r.Passed) passed++;
            else failures.Add(r);
        }
        return (passed == total && total > 0, passed, total, failures);
    }
}
