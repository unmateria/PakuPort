using PakuPort.Judge.Corpus;
using PakuPort.Judge.Diff;

namespace PakuPort.Core.Verify;

public enum SurveyStatus { Green, Red, Unsupported, DecodeError, NoCorpus }

public sealed class SurveyEntry
{
    public required string FuncName { get; init; }
    public required int Offset { get; init; }
    public SurveyStatus Status { get; set; }
    public List<string> OpcodesUsed { get; set; } = new();
    public List<string> NotSupported { get; set; } = new();
    public List<string> Stubbed { get; set; } = new();
    public int Passed { get; set; }
    public int Total { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Barrido determinista del corpus (la "lista de trabajo" de Fase 1, plan §5). Para CADA función
/// con casos .pfc: decodifica + emite (barato) para conocer su set de opcodes; si está totalmente
/// soportada, compila y la pasa por el juez. Clasifica en verde / rojo / opcodes-no-soportados /
/// error-de-decode. NO inventa nada: solo reporta el estado real, que dicta qué opcode implementar
/// a continuación (por impacto). Es el motor de la autonomía: el programa dice qué falta.
/// </summary>
public static class CorpusSurvey
{
    public static List<SurveyEntry> Run(string exePath, string corpusDir, OverlayDirectory dir, HashSet<int>? codeMap = null, int loadBase = 0, ushort loadSegment = 0)
    {
        var exe = ExeImage.Load(exePath, loadSegment);
        // Fase 4: relocs INTERNAS de EXEPACK del residente, con guard w==0 (salta los
        // falsos positivos de DecompToPacked = far-ptrs sin rellenar, que rompían verdes). Arregla
        // 04de: `mov es,164B` (inmediato-segmento no-cero) -> 264B, los MISMOS bytes que corrió el
        // oráculo. Gateado a EXEPACK+loadSeg -> juegos sin EXEPACK intactos.
        if (loadSegment != 0) ExePackDecompressor.ApplyExepackRelocations(exe.Bytes, loadSegment);
        exe.CodeMap = codeMap;
        var entries = new List<SurveyEntry>();

        if (!Directory.Exists(corpusDir)) return entries;

        // Eval de 2 pasadas (stub / follow), extraído para reusar con FUN_1000_* (imagen base)
        // y DRV_* (imagen compuesta con los drivers lifteados). Idéntico al original.
        void EvalTwoPass(ExeImage fnExe, int rootKey, List<CorpusCase> cases, SurveyEntry e, IReadOnlyList<int>? extraSeeds = null)
        {
            // Descubre desde rootKey + TODAS las semillas extra (entradas del driver observadas), con
            // known compartido para no re-explorar subarboles solapados. Asi todos los entry points
            // del driver quedan emitidos como Fn_, y con la resolucion de far-calls del composite el
            // BFS sigue ademas las internas -> el despacho gana el lifteado, no el nativo.
            List<DecodedFunction> DiscoverSeeded(bool followFar)
            {
                if (extraSeeds == null || extraSeeds.Count == 0)
                    return Emitter.DiscoverCallTree(fnExe, rootKey, dir, followFar);
                var known = new HashSet<int>();
                var all = new List<DecodedFunction>();
                foreach (int seed in Enumerable.Repeat(rootKey, 1).Concat(extraSeeds))
                {
                    var tree = Emitter.DiscoverCallTree(fnExe, seed, dir, followFar, known: known);
                    foreach (var fn in tree) if (known.Add(fn.Key)) all.Add(fn);
                }
                return all;
            }

            void Eval(bool followFar)
            {
                EmitResult em;
                try { em = Emitter.EmitProgram(DiscoverSeeded(followFar), rootKey); }
                catch (Exception ex) { e.Status = SurveyStatus.DecodeError; e.Note = ex.Message; return; }

                e.OpcodesUsed = em.OpcodesUsed.ToList();
                e.Stubbed = em.Stubbed.ToList();
                if (!em.FullySupported)
                { e.Status = SurveyStatus.Unsupported; e.NotSupported = em.NotSupported.ToList(); return; }

                try
                {
                    var del = FunctionVerifier.Compile(em.Source, em.Namespace, em.ClassName, em.MethodName);
                    var (allPassed, passed, total, failures) = FunctionVerifier.ReplayAllFx(cases, del, loadBase);
                    if (!allPassed && failures.Count > 0)
                        (allPassed, passed, total) = FunctionVerifier.TryMaskNoise(del, loadBase, cases, passed, failures);
                    e.Passed = passed; e.Total = total;
                    e.Status = allPassed ? SurveyStatus.Green : SurveyStatus.Red;
                }
                catch (Exception ex) { e.Status = SurveyStatus.DecodeError; e.Note = "compile/run: " + ex.Message; }
            }

            Eval(false);   // modo stub
            if (e.Status != SurveyStatus.Green)
            {
                var s = (e.Status, e.Passed, e.Total, e.OpcodesUsed, e.NotSupported, e.Stubbed, e.Note);
                Eval(true);   // reintento siguiendo el C-runtime (limpio gracias al CodeMap por emulación)
                if (e.Status != SurveyStatus.Green && e.Passed <= s.Passed)
                {
                    e.Status = s.Status; e.Passed = s.Passed; e.Total = s.Total;
                    e.OpcodesUsed = s.OpcodesUsed; e.NotSupported = s.NotSupported;
                    e.Stubbed = s.Stubbed; e.Note = s.Note;
                }
            }
        }

        foreach (var funcDir in Directory.GetDirectories(corpusDir, "FUN_1000_*").OrderBy(x => x, StringComparer.Ordinal))
        {
            string funcName = Path.GetFileName(funcDir);
            int offset;
            try { offset = FunctionVerifier.ParseOffset(funcName); }
            catch { continue; }

            var e = new SurveyEntry { FuncName = funcName, Offset = offset };
            entries.Add(e);

            var cases = FunctionVerifier.LoadCorpus(corpusDir, funcName);
            if (cases.Count == 0) { e.Status = SurveyStatus.NoCorpus; continue; }
            e.Total = cases.Count;

            EvalTwoPass(exe, OverlayDirectory.ResidentBase + offset, cases, e);
        }

        // Fase 3: corpus del DRIVER. Los dirs DRV_<key> se decodifican de la IMAGEN
        // COMPUESTA (base + drivers de gráficos/sonido lifteados en 0x40000+, mismo layout que el export).
        // rootKey = la key hex del nombre (DRV_403A8 -> 0x403A8). Gateado a DRV_* -> el path de las
        // FUN_1000_* es byte-idéntico (usa `exe`, no la compuesta) => cero riesgo a los 132.
        var drvDirs = Directory.GetDirectories(corpusDir, "DRV_*").OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (drvDirs.Count > 0)
        {
            var driverRegions = new List<(ushort Seg, string Name, int KeyBase, int Len)>();
            var driverSeeds = new List<int>();
            var driverExe = BuildDriverComposite(exe.Bytes, exePath, corpusDir, dir, driverRegions, driverSeeds);
            if (driverExe != null)
            {
                driverExe.CodeMap = codeMap;
                // Mapa de segmentos de driver para la resolucion ESTATICA de far-calls internas
                // (Decoder.TryResolveDriverFar): el BFS sigue las 9A seg:off del driver y el lifter
                // las emite como llamada directa Fn_ -> nunca tocan el fallback nativo.
                driverExe.DriverRegions = driverRegions.Select(r => (r.Seg, r.KeyBase, r.Len)).ToList();
                // Registro RUNTIME de segmentos: cada seg del rango de cada region -> keyBase
                // ajustado. TryGetDriverBase casa exacto por seg, asi que registramos tambien el 2o
                // segmento del driver (0x4A7A = 0x4956+0x124) para que la far-call en runtime se
                // enrute al lifteado (fk = _dkb + off).
                foreach (var r in driverRegions)
                {
                    int span = (r.Len + 15) / 16;
                    for (int s = r.Seg; s < r.Seg + span; s++)
                        Platform.DriverRegistry.RegisterDriverSegment((ushort)s, r.Name, r.KeyBase + (s - r.Seg) * 16);
                }
                foreach (var drvDir in drvDirs)
                {
                    string name = Path.GetFileName(drvDir);
                    int rootKey;
                    try { rootKey = Convert.ToInt32(name.Substring(4), 16); } catch { continue; }
                    var e = new SurveyEntry { FuncName = name, Offset = rootKey };
                    entries.Add(e);
                    var cases = FunctionVerifier.LoadCorpus(corpusDir, name);
                    if (cases.Count == 0) { e.Status = SurveyStatus.NoCorpus; continue; }
                    e.Total = cases.Count;
                    EvalTwoPass(driverExe, rootKey, cases, e, driverSeeds);
                }
            }
        }
        return entries;
    }

    /// <summary>Construye la imagen COMPUESTA (base + drivers lifteados en 0x40000+) para verificar
    /// funciones DRV_*. Replica BuildDriverCompositeFromList (Program.cs): lee cada driver EXE del
    /// dir del juego, lo reloca por +reloc (execOvls de gameplay_regs.json) y lo pega en su keyBase
    /// (nextBase=0x40000, += (body+0xFF)&amp;~0xFF). La región base [0,0x40000) queda intacta.</summary>
    static ExeImage? BuildDriverComposite(byte[] baseImg, string exePath, string corpusDir,
        OverlayDirectory dir, List<(ushort Seg, string Name, int KeyBase, int Len)> driverRegions, List<int> driverSeeds)
    {
        string regsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(corpusDir))!, "explorer", "gameplay_regs.json");
        if (!File.Exists(regsPath)) return null;
        string gameDir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(regsPath));
        if (!doc.RootElement.TryGetProperty("execOvls", out var ovls)) return null;
        int nextBase = 0x40000;
        var grown = new List<(int At, byte[] Body)>();
        foreach (var ov in ovls.EnumerateArray())
        {
            string oname = ov.GetProperty("name").GetString()!;
            int seg = ov.GetProperty("seg").GetInt32();
            int reloc = ov.GetProperty("reloc").GetInt32();
            string? dpath = Directory.EnumerateFiles(gameDir)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(oname, StringComparison.OrdinalIgnoreCase));
            if (dpath == null) continue;
            var dd = File.ReadAllBytes(dpath);
            if (dd.Length < 28 || dd[0] != 'M' || dd[1] != 'Z') continue;
            int dHdr = (dd[8] | (dd[9] << 8)) * 16;
            int dNrel = dd[6] | (dd[7] << 8);
            int dRt = dd[24] | (dd[25] << 8);
            var bodyArr = new byte[dd.Length - dHdr];
            Array.Copy(dd, dHdr, bodyArr, 0, bodyArr.Length);
            for (int ri = 0; ri < dNrel; ri++)
            {
                int ro = dRt + ri * 4;
                if (ro + 3 >= dd.Length) break;
                int fo = dd[ro] | (dd[ro + 1] << 8);
                int fs = dd[ro + 2] | (dd[ro + 3] << 8);
                int fa = fs * 16 + fo;
                if (fa + 1 < bodyArr.Length)
                {
                    int w = (bodyArr[fa] | (bodyArr[fa + 1] << 8)) + reloc;
                    bodyArr[fa] = (byte)w; bodyArr[fa + 1] = (byte)(w >> 8);
                }
            }
            grown.Add((nextBase, bodyArr));
            // Registrar la region como overlay -> BaseOf/CodeSizeOf correctos para el discover del
            // driver (near-branches resueltas dentro de su segmento, no contra un overlay VROOMM).
            dir.Add(0x1000 + driverRegions.Count, nextBase, bodyArr.Length);
            driverRegions.Add(((ushort)seg, oname.ToLowerInvariant(), nextBase, bodyArr.Length));
            nextBase += (bodyArr.Length + 0xFF) & ~0xFF;
        }
        if (grown.Count == 0) return null;
        var composite = new byte[Math.Max(baseImg.Length, nextBase)];
        Array.Copy(baseImg, composite, baseImg.Length);
        foreach (var (at, b) in grown) Array.Copy(b, 0, composite, at, b.Length);
        // Semillas por driverCall observado ("NOMBRE+0xOFF"): entrada del driver que el juego usa
        // (puerto de Program.cs). Se descubren como raices para que queden emitidas como Fn_.
        if (doc.RootElement.TryGetProperty("driverCalls", out var dcs))
            foreach (var pcall in dcs.EnumerateObject())
            {
                int plus = pcall.Name.LastIndexOf("+0x", StringComparison.OrdinalIgnoreCase);
                if (plus < 0) continue;
                string cn = pcall.Name[..plus].ToLowerInvariant();
                int coff = Convert.ToInt32(pcall.Name[(plus + 3)..], 16);
                var reg = driverRegions.FirstOrDefault(dr => dr.Name == cn);
                if (reg.Name != null && coff < reg.Len) driverSeeds.Add(reg.KeyBase + coff);
            }
        return new ExeImage(composite);
    }
}
