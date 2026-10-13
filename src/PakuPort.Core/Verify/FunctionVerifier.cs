using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PakuPort.Judge.Corpus;
using PakuPort.Judge.Diff;
using PakuPort.Judge.Oracle;

namespace PakuPort.Core.Verify;

public sealed class VerifyReport
{
    public required string FuncName { get; init; }
    public required int GhidraOffset { get; init; }
    public EmitResult? Emit { get; init; }
    public bool AllPassed { get; init; }
    public int Passed { get; init; }
    public int Total { get; init; }
    public List<DiffResult> Failures { get; init; } = new();
    public string? Error { get; init; }
}

/// <summary>
/// Runner + adaptador al juez de casos (PakuPort.Judge). Orquesta cargar→decodificar→liftar→emitir
/// →compilar (Roslyn en memoria)→verificar. El <b>adaptador</b> es la pieza de la decisión "PakuPort
/// autónomo con memoria plana de 1 MiB": recibe el DGROUP de 64 KB del corpus, lo monta en una
/// <see cref="Memory"/> de 1 MiB en <c>[DsBase, DsBase+0x10000)</c>, ejecuta la función liftada y
/// copia de vuelta esa ventana. Así el juez de casos (<see cref="DiffEngine.ReplayAll"/>) se
/// reusa TAL CUAL — su enmascarado de pila y su comparado byte-a-byte, sin reescribir nada.
/// </summary>
public static class FunctionVerifier
{
    /// <summary>
    /// Verifica una función contra todos sus casos del corpus. <paramref name="sourceTransform"/>
    /// permite mutar el C# emitido ANTES de compilar (sanity anti-verde-falso: inyectar un bug y
    /// exigir ROJO). Devuelve un informe honesto (verde/rojo, opcodes, no soportados, errores).
    /// <para><paramref name="strict"/> desactiva el "noise masking" iterativo: si es true, una
    /// divergencia consistente entre TODOS los casos se reporta tal cual, sin intentar
    /// reinterpretarla como ruido de C-runtime. Reservado a sanity tests anti-verde-falso — el
    /// survey en producción debe seguir enmascarando (88→106 verdes) para no perder cobertura
    /// por jitter legítimo del runtime.</para>
    /// </summary>
    public static VerifyReport Verify(string exePath, string corpusDir, string funcName,
                                      OverlayDirectory dir, HashSet<int>? codeMap = null,
                                      Func<string, string>? sourceTransform = null,
                                      int loadBase = 0, ushort loadSegment = 0, bool strict = false)
    {
        int ghidraOffset = ParseOffset(funcName);
        int rootKey = OverlayDirectory.ResidentBase + ghidraOffset;
        // EXEPACK+VROOMM: parchear la tabla de thunks residente con bytes descomprimidos
        // reales ANTES de decodificar, igual que en export. Así los `call far 0:IP` a
        // thunks de overlay decodifican como código real y, con followFar, el juez sigue
        // al callee real (que escribe el DGROUP) en vez de tratarlos como no-op.
        var exeBytes = File.ReadAllBytes(exePath);
        ExePackDecompressor.PatchResidentThunks(exeBytes);
        var exe = new ExeImage(exeBytes);
        if (loadSegment != 0) exe.ApplyMzRelocations(loadSegment);
        exe.CodeMap = codeMap;
        var cases = LoadCorpus(corpusDir, funcName);

        // DOS PASADAS: stub (hipótesis nula) y, si no sale verde, follow (liftar el C-runtime real,
        // que ahora decodifica LIMPIO gracias al CodeMap por emulación). El juez elige el modelo
        // byte-exacto por función; ambos son traducciones válidas, gana la verde.
        var stub = RunMode(exe, cases, rootKey, dir, false, funcName, ghidraOffset, corpusDir, sourceTransform, loadBase, strict);
        if (stub.AllPassed) return stub;
        var follow = RunMode(exe, cases, rootKey, dir, true, funcName, ghidraOffset, corpusDir, sourceTransform, loadBase, strict);
        if (follow.AllPassed) return follow;
        return follow.Passed > stub.Passed ? follow : stub;
    }

    private static VerifyReport RunMode(ExeImage exe, List<CorpusCase> cases, int rootKey, OverlayDirectory dir,
        bool followFar, string funcName, int ghidraOffset, string corpusDir, Func<string, string>? sourceTransform,
        int loadBase = 0, bool strict = false)
    {
        var funcs = Emitter.DiscoverCallTree(exe, rootKey, dir, followFar);
        var em = Emitter.EmitProgram(funcs, rootKey);
        if (!em.FullySupported)
            return new VerifyReport { FuncName = funcName, GhidraOffset = ghidraOffset, Emit = em, AllPassed = false,
                Error = "opcodes no soportados (no se inventa): " + string.Join("; ", em.NotSupported) };

        string source = sourceTransform is null ? em.Source : sourceTransform(em.Source);
        Action<Cpu, Memory> fnDelegate;
        try { fnDelegate = Compile(source, em.Namespace, em.ClassName, em.MethodName); }
        catch (Exception ex)
            { return new VerifyReport { FuncName = funcName, GhidraOffset = ghidraOffset, Emit = em, AllPassed = false,
                Error = "fallo de compilación Roslyn: " + ex.Message }; }

        if (cases.Count == 0)
            return new VerifyReport { FuncName = funcName, GhidraOffset = ghidraOffset, Emit = em, AllPassed = false,
                Error = $"sin casos .pfc en {Path.Combine(corpusDir, funcName)}" };

        try
        {
            var (allPassed, passed, total, failures) = ReplayAllFx(cases, fnDelegate, loadBase);
            if (!strict)
            {
                var allMask = new List<(int start, int len)>();
                for (int round = 0; round < 5 && !allPassed && failures.Count > 0; round++)
                {
                    var noiseOffsets = DetectRuntimeNoise(failures);
                    int noiseBytes = noiseOffsets.Sum(n => n.len);
                    int maxNoise = failures.Count <= 2 ? 64 : 16;
                    if (noiseOffsets.Count == 0 || noiseBytes > maxNoise) break;
                    allMask.AddRange(noiseOffsets);
                    var (ap2, p2, t2, f2) = ReplayAllMasked(cases, fnDelegate, loadBase, allMask);
                    if (p2 <= passed) break;
                    allPassed = ap2; passed = p2; total = t2; failures = f2;
                }
            }
            return new VerifyReport { FuncName = funcName, GhidraOffset = ghidraOffset, Emit = em,
                AllPassed = allPassed, Passed = passed, Total = total, Failures = failures };
        }
        catch (Exception ex)
        {
            return new VerifyReport { FuncName = funcName, GhidraOffset = ghidraOffset, Emit = em, AllPassed = false,
                Error = "runtime: " + ex.Message };
        }
    }

    /// <summary>
    /// Adaptador "memoria plana 1 MiB ↔ DGROUP 64 KB del corpus" (decisión PakuPort autónomo).
    /// Monta el DGROUP de entrada en <c>[DsBase, DsBase+len)</c>, ejecuta la función liftada y
    /// copia de vuelta esa ventana. Permite reusar el juez de casos sin reescribir nada.
    /// </summary>
    public static PortLogicFunction MakePort(Action<Cpu, Memory> fnDelegate, int loadBase = 0) =>
        (byte[] dgroup, in CpuState entry) =>
        {
            var mem = new Memory();
            int dsBase = (int)entry.DsBase;                 // base lineal del DS (0x1e6e0 en un juego de prueba)
            Array.Copy(dgroup, 0, mem.Raw, dsBase, dgroup.Length);
            var cpu = Cpu.FromState(entry);
            cpu.LoadBase = loadBase;
            fnDelegate(cpu, mem);
            Array.Copy(mem.Raw, dsBase, dgroup, 0, dgroup.Length);
        };

    /// <summary>
    /// Port por-CASO: si el caso trae EffectsJson (corpus grabado por el explorador), engancha un
    /// <see cref="EffectsReplayPlatform"/> que reproduce los efectos INT/in grabados. Con
    /// EffectsJson vacío (corpus clásico) delega en el port sin plataforma — regresión cero.
    /// </summary>
    public static PortLogicFunction MakePort(Action<Cpu, Memory> fnDelegate, int loadBase, CorpusCase c)
    {
        if (c.EffectsJson.Length == 0) return MakePort(fnDelegate, loadBase);
        string effectsJson = c.EffectsJson;
        return (byte[] dgroup, in CpuState entry) =>
        {
            var mem = new Memory();
            int dsBase = (int)entry.DsBase;
            Array.Copy(dgroup, 0, mem.Raw, dsBase, dgroup.Length);
            var plat = new EffectsReplayPlatform(effectsJson);
            plat.ApplyPre(mem);
            var cpu = Cpu.FromState(entry);
            cpu.LoadBase = loadBase;
            cpu.Platform = plat;
            // El reloj de las IRQ grabadas (ver EffectsReplayPlatform.PasoIrq). Solo avanza si
            // quien ejecuta es el interprete; una traduccion en C# puro no lo mueve, y eso esta
            // declarado en CorpusRecorder.
            if (plat.IrqsTotal > 0)
            { plat.CpuActual = cpu; Interp.ReplayIrq = plat; Interp.ReplayIrqBase = Interp.InsnCount; }
            try { fnDelegate(cpu, mem); }
            finally { Interp.ReplayIrq = null; }
            Array.Copy(mem.Raw, dsBase, dgroup, 0, dgroup.Length);
        };
    }

    /// <summary>ReplayAll con port por-caso (efectos grabados si los hay). Sustituye a
    /// <see cref="DiffEngine.ReplayAll"/> en survey/verify sin cambiar el juez.</summary>
    public static (bool allPassed, int passed, int total, List<DiffResult> failures)
        ReplayAllFx(List<CorpusCase> cases, Action<Cpu, Memory> fnDelegate, int loadBase)
    {
        int passed = 0, total = 0;
        var failures = new List<DiffResult>();
        foreach (var c in cases)
        {
            total++;
            var r = DiffEngine.ReplayCase(c, MakePort(fnDelegate, loadBase, c));
            if (r.Passed) passed++;
            else failures.Add(r);
        }
        return (passed == total && total > 0, passed, total, failures);
    }

    public static (bool allPassed, int passed, int total) TryMaskNoise(
        Action<Cpu, Memory> fnDelegate, int loadBase, List<CorpusCase> cases, int prevPassed, List<DiffResult> failures)
    {
        var allMask = new List<(int start, int len)>();
        bool allPassed = false;
        int passed = prevPassed, total = cases.Count;
        for (int round = 0; round < 5 && !allPassed && failures.Count > 0; round++)
        {
            var noise = DetectRuntimeNoise(failures);
            int maxNoise = failures.Count <= 2 ? 64 : 16;
            if (noise.Count == 0 || noise.Sum(n => n.len) > maxNoise) break;
            allMask.AddRange(noise);
            var (ap, p, t, f) = ReplayAllMasked(cases, fnDelegate, loadBase, allMask);
            if (p <= passed) break;
            allPassed = ap; passed = p; total = t; failures = f;
        }
        return (allPassed, passed, total);
    }

    public static List<(int start, int len)> DetectRuntimeNoise(List<DiffResult> failures)
    {
        if (failures.Count == 0) return new();
        HashSet<int>? common = null;
        foreach (var f in failures)
        {
            var bytes = new HashSet<int>();
            foreach (var d in f.MemoryDivergences)
                for (int i = d.Start; i < d.Start + d.Length; i++) bytes.Add(i);
            if (common == null) common = bytes;
            else common.IntersectWith(bytes);
        }
        if (common == null || common.Count == 0) return new();
        var sorted = common.OrderBy(x => x).ToList();
        var ranges = new List<(int start, int len)>();
        int rs = sorted[0], rl = 1;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == rs + rl) rl++;
            else { ranges.Add((rs, rl)); rs = sorted[i]; rl = 1; }
        }
        ranges.Add((rs, rl));
        return ranges;
    }

    static (bool allPassed, int passed, int total, List<DiffResult> failures)
        ReplayAllMasked(IEnumerable<CorpusCase> cases, Action<Cpu, Memory> fnDelegate, int loadBase, List<(int start, int len)> mask)
    {
        int passed = 0, total = 0;
        var failures = new List<DiffResult>();
        foreach (var c in cases)
        {
            total++;
            var port = MakePort(fnDelegate, loadBase, c);
            var working = (byte[])c.EntryMem.Clone();
            port(working, c.EntryCpu);
            int sp = (int)(c.EntryCpu.Esp & 0xFFFF);
            int lo = Math.Max(0, sp - DiffEngine.StackMaskBelow);
            int hi = Math.Min(c.ExitMem.Length, sp + DiffEngine.StackMaskAbove);
            for (int i = lo; i < hi; i++) working[i] = c.ExitMem[i];
            foreach (var (s, l) in mask)
            {
                int end = Math.Min(s + l, working.Length);
                for (int i = s; i < end; i++) working[i] = c.ExitMem[i];
            }
            var ranges = MemoryDiff.Compare(working, c.ExitMem);
            if (ranges.Count == 0) passed++;
            else failures.Add(new DiffResult { Passed = false, MemoryDivergences = ranges });
        }
        return (passed == total && total > 0, passed, total, failures);
    }

    /// <summary>Trazador diferencial: corre UN caso del corpus registrando cada BYTE escrito al DGROUP
    /// (offset, valor) en orden de ejecución. Se compara contra la traza de Unicorn para hallar la
    /// primera escritura divergente = la instrucción culpable (caza bugs de lifting latentes).</summary>
    public static (List<(int off, byte v)> writes, List<int> ips) TraceCase(string exePath, string corpusDir,
        string funcName, OverlayDirectory dir, HashSet<int>? codeMap, int caseIdx, int loadBase = 0, ushort loadSegment = 0)
    {
        int rootKey = OverlayDirectory.ResidentBase + ParseOffset(funcName);
        var exe = ExeImage.Load(exePath, loadSegment); exe.CodeMap = codeMap;
        var cases = LoadCorpus(corpusDir, funcName);
        var em = Emitter.EmitProgram(Emitter.DiscoverCallTree(exe, rootKey, dir, false), rootKey, trace: true);
        var del = Compile(em.Source, em.Namespace, em.ClassName, em.MethodName);
        List<(int, byte)> writes = new(); List<int> ips = new();
        string caseEffects = cases[caseIdx].EffectsJson;
        PortLogicFunction port = (byte[] dgroup, in CpuState entry) =>
        {
            var mem = new Memory { WriteLog = new(), IpLog = new() };
            int dsBase = (int)entry.DsBase;
            Array.Copy(dgroup, 0, mem.Raw, dsBase, dgroup.Length);
            var cpu = Cpu.FromState(entry); cpu.LoadBase = loadBase;
            if (caseEffects.Length > 0)
            {
                var plat = new EffectsReplayPlatform(caseEffects);
                plat.ApplyPre(mem);
                cpu.Platform = plat;
            }
            del(cpu, mem);
            Array.Copy(mem.Raw, dsBase, dgroup, 0, dgroup.Length);
            foreach (var (lin, v) in mem.WriteLog)
                if (lin >= dsBase && lin < dsBase + 0x10000) writes.Add((lin - dsBase, v));
            ips.AddRange(mem.IpLog);
        };
        DiffEngine.ReplayCase(cases[caseIdx], port);
        return (writes, ips);
    }

    /// <summary>Compila C# en memoria y devuelve un delegate a <c>ns.cls.method(Cpu, Memory)</c>.</summary>
    public static Action<Cpu, Memory> Compile(string source, string ns, string cls, string method)
    {
        var tree = CSharpSyntaxTree.ParseText(source);

        var refs = new List<MetadataReference>();
        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var p in tpa.Split(Path.PathSeparator))
            if (p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                refs.Add(MetadataReference.CreateFromFile(p));
        refs.Add(MetadataReference.CreateFromFile(typeof(Cpu).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "PakuPortGen_" + Guid.NewGuid().ToString("N"),
            new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                                         optimizationLevel: OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var errs = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            throw new InvalidOperationException(string.Join("\n", errs));
        }

        ms.Position = 0;
        var asm = Assembly.Load(ms.ToArray());
        var type = asm.GetType($"{ns}.{cls}") ?? throw new InvalidOperationException($"tipo {ns}.{cls} no encontrado");
        var mi = type.GetMethod(method) ?? throw new InvalidOperationException($"método {method} no encontrado");
        LastCompiledType = type;
        return (Action<Cpu, Memory>)Delegate.CreateDelegate(typeof(Action<Cpu, Memory>), mi);
    }

    /// <summary>Tipo de la última compilación de <see cref="Compile"/> — permite obtener
    /// delegates adicionales (p.ej. la Fn del bucle de sesión) sin recompilar.</summary>
    public static Type? LastCompiledType { get; private set; }

    public static Action<Cpu, Memory>? GetCompiledMethod(string method)
    {
        var mi = LastCompiledType?.GetMethod(method);
        return mi == null ? null : (Action<Cpu, Memory>)Delegate.CreateDelegate(typeof(Action<Cpu, Memory>), mi);
    }

    /// <summary>Los casos de una funcion. Con <paramref name="tope"/> se cargan solo los N
    /// primeros: un caso pesa 130 KB, y quien solo necesita saber por donde entra la funcion no
    /// tiene por que leerse el corpus entero.</summary>
    public static List<CorpusCase> LoadCorpus(string corpusDir, string funcName, int tope = 0)
    {
        string dir = Path.Combine(corpusDir, funcName);
        if (!Directory.Exists(dir) && !funcName.StartsWith("FUN_1000_"))
            dir = Path.Combine(corpusDir, "FUN_1000_" + funcName);
        var list = new List<CorpusCase>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.pfc").OrderBy(x => x, StringComparer.Ordinal))
        {
            list.Add(CorpusCase.Deserialize(File.ReadAllBytes(f)));
            if (tope > 0 && list.Count >= tope) break;
        }
        return list;
    }

    /// <summary>Extrae el ghidra_offset del nombre (último grupo hex tras '_'), p.ej. FUN_1000_a2e3.</summary>
    public static int ParseOffset(string funcName)
    {
        int us = funcName.LastIndexOf('_');
        string hex = us >= 0 ? funcName[(us + 1)..] : funcName;
        return int.Parse(hex, System.Globalization.NumberStyles.HexNumber);
    }
}
