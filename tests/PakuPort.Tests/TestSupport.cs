using System.Linq;
using PakuPort.Core;
using PakuPort.Core.Verify;

namespace PakuPort.Tests;

/// <summary>Utilidades compartidas por los tests: localizar la config y ejecutar snippets liftados.</summary>
public static class TestSupport
{
    /// <summary>Ruta del fichero de vectores (copiado a la salida del test).</summary>
    public static string VectorsPath() => Path.Combine(AppContext.BaseDirectory, "vectors", "opcodes.json");

    /// <summary>
    /// Pasa un snippet de bytes por TODO el pipeline de PakuPort (decode→lift→emit→compile→run):
    /// le añade un <c>ret</c>, lo ejecuta sobre un Cpu con AX/BX/CX dados y devuelve el Cpu final.
    /// Así los tests de opcodes ejercitan el lifter REAL, no solo los helpers de flags.
    /// </summary>
    public static Cpu RunSnippet(byte[] code, ushort ax, ushort bx, ushort cx, bool cfIn = false, ushort dx = 0, bool afIn = false)
    {
        var full = code.Concat(new byte[] { 0xC3 }).ToArray(); // + ret
        var fn = Decoder16.DecodeFunction(full, 0, 0, new OverlayDirectory());
        var em = Emitter.Emit(fn);
        if (!em.FullySupported)
            throw new InvalidOperationException("snippet con opcodes no soportados: " + string.Join("; ", em.NotSupported));
        var del = FunctionVerifier.Compile(em.Source, em.Namespace, em.ClassName, em.MethodName);
        var cpu = new Cpu { AX = ax, BX = bx, CX = cx, DX = dx, CF = cfIn, AF = afIn };
        del(cpu, new Memory());
        return cpu;
    }
}
