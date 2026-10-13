using System.Linq;
using System.Text;
using Iced.Intel;

namespace PakuPort.Core;

public sealed class EmitResult
{
    public required string Source { get; init; }
    public required string Namespace { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public required IReadOnlyList<string> OpcodesUsed { get; init; }
    public required IReadOnlyList<string> NotSupported { get; init; }
    public required IReadOnlyList<string> Stubbed { get; init; }
    public required IReadOnlyList<int> Functions { get; init; }   // claves emitidas (árbol de llamadas)
    public bool FullySupported => NotSupported.Count == 0;
}

/// <summary>
/// Emitter: ensambla en UNA clase C# todos los métodos <c>Fn_&lt;clave&gt;(Cpu, Memory)</c> del árbol
/// de llamadas (residente + overlays). <c>call near</c> → método del callee; <c>INT 3F</c> → método
/// de la función de overlay (resuelto estáticamente). El emitido declara opcodes usados, no
/// soportados y stubs.
/// </summary>
public static class Emitter
{
    public const string Ns = "PakuPort.Generated";
    public const string Cls = "Gen";

    /// <summary>Emite una sola función (sin árbol). Para snippets de test (residente, base 0x200).</summary>
    public static EmitResult Emit(DecodedFunction fn) => EmitProgram(new[] { fn }, fn.Key);

    public static EmitResult EmitProgram(IReadOnlyList<DecodedFunction> funcs, int rootKey, bool trace = false,
        (ushort Seg, ushort Off)? vroommStub = null, bool softStubs = false,
        OverlayDirectory? overlayDir = null, Dictionary<int, int>? vroommStubTable = null,
        ushort overlayLoadBase = 0, ExeImage? exe = null,
        Dictionary<int, int>? thunkRedirects = null, int imageBias = 0)
    {
        var lifter = new Lifter
        {
            VroommStub = vroommStub, VroommStubTable = vroommStubTable,
            OverlayLoadBase = overlayLoadBase,
            InlineParamFunctions = DetectInlineParamFunctions(funcs),
        };
        // Ventana de overlays VROOMM: rango de keys que ALIAS al contenido dinámico de
        // la ventana (CS residente, IP >= overlayLoadBase). Ninguna función de ahí se
        // liftea (la imagen prístina tiene ceros/basura ahí): se despachan en runtime
        // según qué overlay ocupe la ventana (firma de sus primeros 16 bytes en Memory).
        int winKeyLo = -1, winKeyHi = -1, winLinOff = 0;
        if (overlayLoadBase > 0 && vroommStub is var (wvs, _) && imageBias > 0)
        {
            winLinOff = wvs * 16 + overlayLoadBase;
            winKeyLo = winLinOff - imageBias + ExeImage.ResidentBase;
            winKeyHi = winKeyLo + (0x10000 - overlayLoadBase);
        }
        bool InWindow(int k) => winKeyLo >= 0 && k >= winKeyLo && k < winKeyHi;

        var src = new StringBuilder();
        src.Append("// generado por PakuPort — NO editar a mano (recompilación 1:1 byte-exacta)\n");
        src.Append("using PakuPort.Core;\n\n");
        src.Append($"namespace {Ns}\n{{\n");
        // partial: permite trocear GameCode.cs en varios ficheros en el export
        // (un juego grande son miles de funciones = decenas de MB en un solo .cs
        // que ahoga al compilador). En el compile en memoria (survey/run) sigue
        // siendo una sola clase — `partial` es inocuo ahí.
        src.Append($"    public static partial class {Cls}\n    {{\n");
        var emittedKeys = new HashSet<int>();
        var allKnownKeys = new HashSet<int>(funcs.Select(f => f.Key));
        foreach (var fn in funcs)
        {
            if (InWindow(fn.Key))
            {
                emittedKeys.Add(fn.Key);
                src.Append($"        public static void Fn_{fn.Key:X5}(Cpu c, Memory m) => WindowDispatch(c, m, 0x{fn.Key - winKeyLo:X4}); // alias de ventana\n");
                continue;
            }
            lifter.CurrentBase = fn.Base;
            lifter.CurrentCodeSize = overlayDir?.CodeSizeOf(fn.Base) ?? 0;
            lifter.CurrentFnLabels = fn.JumpTargets;
            lifter.ManualRetReg = Lifter.DetectManualRetReg(fn);
            emittedKeys.Add(fn.Key);
            EmitMethod(fn, lifter, src, trace, softStubs, allKnownKeys);
        }

        // Generar stubs para funciones referenciadas pero no descubiertas
        var body = src.ToString();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(body, @"Fn_([0-9A-Fa-f]{5})\("))
        {
            int key = Convert.ToInt32(m.Groups[1].Value, 16);
            if (emittedKeys.Add(key))
                src.Append(InWindow(key)
                    ? $"        public static void Fn_{key:X5}(Cpu c, Memory m) => WindowDispatch(c, m, 0x{key - winKeyLo:X4}); // alias de ventana\n"
                    : $"        public static void Fn_{key:X5}(Cpu c, Memory m) {{ }} // stub: callee no descubierto\n");
        }

        // WindowDispatch: resuelve el overlay que OCUPA la ventana ahora mismo (los
        // primeros 32 bytes en Memory.Raw contra el directorio, con tolerancia a las
        // palabras relocadas por VROOMM) y llama a su función estática.
        src.Append("\n        static int _winOvKey = -1; static readonly byte[] _wCache = new byte[32];\n");
        src.Append("        public static void WindowDispatch(Cpu c, Memory m, int delta)\n");
        src.Append("        {\n");
        if (winKeyLo >= 0 && exe != null && overlayDir != null)
        {
            src.Append($"            int wl = c.LoadBase + 0x{winLinOff:X5};\n");
            src.Append("            bool same = _winOvKey >= 0;\n");
            src.Append("            for (int i = 0; i < 32 && same; i++) if (m.Raw[wl + i] != _wCache[i]) same = false;\n");
            src.Append("            if (!same)\n");
            src.Append("            {\n");
            src.Append("                System.Array.Copy(m.Raw, wl, _wCache, 0, 32);\n");
            src.Append("                _winOvKey = -1; int best = 27; // >=28/32 bytes: tolera relocs\n");
            src.Append("                foreach (var t in _ovSigs)\n");
            src.Append("                {\n");
            src.Append("                    int sc = 0;\n");
            src.Append("                    for (int i = 0; i < 32; i++) if (t.Sig[i] == _wCache[i]) sc++;\n");
            src.Append("                    if (sc > best) { best = sc; _winOvKey = t.Key; }\n");
            src.Append("                }\n");
            src.Append("            }\n");
            src.Append("            if (_winOvKey >= 0 && CallKey(c, m, _winOvKey + delta)) return;\n");
            src.Append($"            c.FarDispatchMisses?.Add(_winOvKey >= 0 ? _winOvKey + delta : -(0x{winKeyLo:X5} + delta));\n");
            src.Append("            // Interp: miss de ventana -> ejecutar los bytes reales de la ventana\n");
            src.Append($"            if (Interp.Enabled) {{ if (Interp.Run(c, m, (ushort)((c.LoadBase >> 4) + 0x{(vroommStub?.Seg ?? 0):X4}), (ushort)(0x{overlayLoadBase:X4} + delta), CallKey, FarDispatch)) c.FarRet = true; return; }}\n");
            src.Append("            c.SP += 4;\n");
            src.Append("        }\n");
            src.Append("        static readonly (int Key, byte[] Sig)[] _ovSigs = {\n");
            foreach (var (ovno, start, size) in overlayDir.Entries)
            {
                if (start + 32 > exe.Length || size <= 0) continue;
                string sig = string.Join(", ", exe.Bytes.Skip(start).Take(32).Select(b => $"0x{b:X2}"));
                src.Append($"            (0x{start:X5}, new byte[] {{ {sig} }}), // ov {ovno:X2}\n");
            }
            src.Append("        };\n");
        }
        else
        {
            src.Append("            c.SP += 4; // sin ventana configurada\n");
            src.Append("        }\n");
            src.Append("        static readonly (int Key, byte[] Sig)[] _ovSigs = System.Array.Empty<(int, byte[])>();\n");
        }

        // CallKey: despacho por file key → Fn_XXXXX (compartido por FarDispatch y WindowDispatch)
        // ThunkRedirects vienen en coordenadas del mundo canónico (fk = IP + base); en el
        // mundo memimage/snapshot FarDispatch resta ImageBias, así que las claves de los
        // cases se desplazan -imageBias para que los far-calls a thunks CD 3F del residente
        // aterricen en la función real del overlay. Un thunk con redirect GANA sobre una
        // función lifteada en la misma key (liftear el placeholder del thunk ejecuta basura).
        var redirectAdj = new Dictionary<int, int>();
        if (thunkRedirects != null)
            foreach (var (thunkFk, targetKey) in thunkRedirects)
                if (emittedKeys.Contains(targetKey))
                    redirectAdj[thunkFk - imageBias] = targetKey;
        src.Append("\n        [System.ThreadStatic] static int _farDepth;\n");
        src.Append("        public static bool CallKey(Cpu c, Memory m, int fk)\n");
        src.Append("        {\n");
        src.Append("            switch (fk)\n");
        src.Append("            {\n");
        // Una clave NEGATIVA se imprime como 8 dígitos (0xFFFFAC97) y ese literal es uint en
        // C#: no cabe en el `switch (int fk)` y el export no compila — que es la peor clase de
        // fallo, porque ningún gate de runtime lo caza (pasó con `ret imm16` y aquí
        // con un simulador de vuelo). Se emite con unchecked, que conserva los bits y casa con el
        // fk que calcula el runtime. Que HAYA claves negativas es otro asunto, anotado aparte.
        static string CaseLabel(int k) => k < 0 ? $"unchecked((int)0x{k:X8})" : $"0x{k:X5}";
        foreach (int key in emittedKeys.OrderBy(k => k))
            if (!redirectAdj.ContainsKey(key))
                src.Append($"                case {CaseLabel(key)}: Fn_{key:X5}(c, m); return true;\n");
        foreach (var (thunkFk, targetKey) in redirectAdj.OrderBy(kv => kv.Key))
            src.Append($"                case {CaseLabel(thunkFk)}: Fn_{targetKey:X5}(c, m); return true;\n");
        src.Append("                default: return false;\n");
        src.Append("            }\n");
        src.Append("        }\n");

        // FarDispatch: convierte (seg, off) runtime → file key → Fn_XXXXX. Devuelve true
        // siempre (lo maneja o lo contabiliza) — el bool es para servir de Interp.FarDispatchFn.
        src.Append("        public static bool FarDispatch(Cpu c, Memory m, ushort seg, ushort off)\n");
        src.Append("        {\n");
        src.Append("            if (++_farDepth > 256) { _farDepth--; c.SP += 4; return true; }\n");
        src.Append("            // Efecto \"f\": en replay (juez), una call far a driver se consume como efecto grabado.\n");
        src.Append("            if (c.Platform is PakuPort.Core.Platform.IFarCallSink _fcs && _fcs.TryFarCall(seg, off, c, m)) { _farDepth--; return true; }\n");
        src.Append("            if (seg == 0 && c.LoadBase > 0) seg = (ushort)(c.LoadBase >> 4);\n");
        src.Append("            c.CS = seg;\n");
        src.Append("            // zona boot del mundo snapshot (bootstrap VROOMM antes de la imagen):\n");
        src.Append("            // seg==loadSeg && off<ImageBias -> pseudo-overlay en BootKeyBase\n");
        src.Append($"            if (c.ImageBias > 0 && off < c.ImageBias && seg == (ushort)(c.LoadBase >> 4) && CallKey(c, m, 0x{PakuPort.Core.OverlayDirectory.BootKeyBase:X5} + off)) {{ _farDepth--; return true; }}\n");
        src.Append("            // drivers EXEC lifteados (imagen compuesta): fk = keyBase + off\n");
        src.Append("            int fk = PakuPort.Core.Platform.DriverRegistry.TryGetDriverBase(seg, out var _dkb)\n");
        src.Append("                ? _dkb + off\n");
        src.Append("                : seg * 16 + off - c.LoadBase - c.ImageBias + 0x200;\n");
        src.Append("            // Selector PRE-reloc: la zona descomprimida por EXEPACK en runtime no esta\n");
        src.Append("            // cubierta por la tabla de relocs del MZ, asi que el lifteado lleva el seg\n");
        src.Append("            // sin relocar (p.ej. ljmp 011A:D73A en vez de 111A:D73A). Si la key sale\n");
        src.Append("            // invalida (< base residente), reinterpretar sumando loadSeg.\n");
        src.Append("            if (fk < 0x200 && seg != 0 && c.LoadBase > 0)\n");
        src.Append("            {\n");
        src.Append("                // Zona de la tabla de thunks VROOMM (seg == loadSeg, off < ImageBias): la MEMORIA\n");
        src.Append("                // es la verdad — si el thunk esta parcheado en runtime (jmp far), seguirlo AQUI.\n");
        src.Append("                // Sin esto, la reinterpretacion pre-reloc de abajo fabrica una key que puede caer\n");
        src.Append("                // de carambola en el rango de la ventana de overlays (delta basura -> #DE).\n");
        src.Append("                int _tl = ((seg << 4) + off) & 0xFFFFF;\n");
        src.Append("                if (seg == (ushort)(c.LoadBase >> 4) && m.Raw[_tl] == 0xEA && _farDepth < 250)\n");
        src.Append("                {\n");
        src.Append("                    ushort _tto = (ushort)(m.Raw[_tl + 1] | (m.Raw[_tl + 2] << 8));\n");
        src.Append("                    ushort _tts = (ushort)(m.Raw[_tl + 3] | (m.Raw[_tl + 4] << 8));\n");
        src.Append("                    if ((_tto | _tts) != 0) { FarDispatch(c, m, _tts, _tto); _farDepth--; return true; }\n");
        src.Append("                }\n");
        src.Append("                int fkRel = (seg + (c.LoadBase >> 4)) * 16 + off - c.LoadBase - c.ImageBias + 0x200;\n");
        src.Append("                if (fkRel >= 0x200) { c.CS = (ushort)(seg + (c.LoadBase >> 4)); fk = fkRel; }\n");
        src.Append("            }\n");
        if (winKeyLo >= 0)
        {
            src.Append($"            if (fk >= 0x{winKeyLo:X5} && fk < 0x{winKeyHi:X5}) {{ WindowDispatch(c, m, fk - 0x{winKeyLo:X5}); _farDepth--; return true; }}\n");
        }
        src.Append("            if (CallKey(c, m, fk)) { _farDepth--; return true; }\n");
        src.Append("            // thunk VROOMM parcheado en runtime: la memoria contiene jmp far -> seguirlo\n");
        src.Append("            int _lin = ((seg << 4) + off) & 0xFFFFF;\n");
        src.Append("            if (m.Raw[_lin] == 0xEA && _farDepth < 250)\n");
        src.Append("            { ushort _to = (ushort)(m.Raw[_lin + 1] | (m.Raw[_lin + 2] << 8)); ushort _ts = (ushort)(m.Raw[_lin + 3] | (m.Raw[_lin + 4] << 8)); FarDispatch(c, m, _ts, _to); _farDepth--; return true; }\n");
        src.Append("            // thunk VROOMM SIN parchear: `call <loader>; <far-ptr>`. El loader del overlay\n");
        src.Append("            // manager salta al far-ptr descriptor que sigue al call (offset+3). Seguirlo al\n");
        src.Append("            // destino real (driver/overlay) cuando su segmento es un driver EXEC registrado.\n");
        src.Append("            if (m.Raw[_lin] == 0xE8 && _farDepth < 250)\n");
        src.Append("            { ushort _doff = (ushort)(m.Raw[_lin + 3] | (m.Raw[_lin + 4] << 8)); ushort _dseg = (ushort)(m.Raw[_lin + 5] | (m.Raw[_lin + 6] << 8));\n");
        src.Append("              if (_dseg != 0 && PakuPort.Core.Platform.DriverRegistry.TryGetDriverBase(_dseg, out var _)) { FarDispatch(c, m, _dseg, _doff); _farDepth--; return true; } }\n");
        src.Append("            // trampolin de IVT (snapshot del explorador: `int nn; iret`): emular el INT\n");
        src.Append("            // por la plataforma y el IRET final (el llamante llego estilo ISR: flags+cs+ip)\n");
        src.Append("            if (m.Raw[_lin] == 0xCD)\n");
        src.Append("            { c.Platform?.Int(m.Raw[_lin + 1], c, m); c.SP += 4; c.SetFlagsWord(m.RdU16(c.SS, c.SP)); c.SP += 2; _farDepth--; return true; }\n");
        src.Append("            // Miss hacia memoria dinamica (arena MCB): el destino es contenido cargado\n");
        src.Append("            // en runtime (driver/overlay) — su clave lineal NO es una clave de imagen;\n");
        src.Append("            // sembrarla lifteria bytes desalineados que secuestran el dispatch. No loguear.\n");
        src.Append("            if (!(c.Platform is PakuPort.Core.Platform.DosPlatform _dpm && _dpm.Int21 != null && _dpm.Int21.IsArenaSegment(seg)))\n");
        src.Append("                c.FarDispatchMisses?.Add(fk);\n");
        src.Append("            // Interp: miss -> ejecutar los bytes ORIGINALES en seg:off\n");
        src.Append("            if (Interp.Enabled) { if (Interp.Run(c, m, c.CS, off, CallKey, FarDispatch)) c.FarRet = true; _farDepth--; return true; }\n");
        src.Append("            c.SP += 4; _farDepth--; return true;\n");
        src.Append("        }\n");

        src.Append("    }\n}\n");

        return new EmitResult
        {
            Source = src.ToString(),
            Namespace = Ns,
            ClassName = Cls,
            MethodName = $"Fn_{rootKey:X5}",
            OpcodesUsed = lifter.OpcodesUsed.ToList(),
            NotSupported = lifter.NotSupported,
            Stubbed = lifter.Stubbed.Distinct().ToList(),
            Functions = funcs.Select(f => f.Key).ToList(),
        };
    }

    private static void EmitMethod(DecodedFunction fn, Lifter lifter, StringBuilder src, bool trace = false, bool softStubs = false, HashSet<int>? allKnownKeys = null)
    {
        var labels = fn.JumpTargets;
        var methodBody = new StringBuilder();
        var decoded = new HashSet<ushort>();
        foreach (var d in fn.Instructions) decoded.Add(d.Offset);

        // Pre-pasada: labels extra para inline param skip targets (goto L_XXXX tras call a función
        // con inline params — el target es la primera instrucción real después de los datos inline).
        var extraLabels = new HashSet<ushort>();
        var skipTargets = new Dictionary<ushort, ushort>();
        var tailCallTargets = new Dictionary<ushort, int>();
        if (lifter.InlineParamFunctions is { Count: > 0 })
        {
            foreach (var d in fn.Instructions)
            {
                if (d.Iced.Mnemonic != Mnemonic.Call) continue;
                int targetKey = -1;
                if (d.FarCallTargetKey >= 0)
                    targetKey = d.FarCallTargetKey;
                else if (d.Iced.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                    targetKey = fn.Base + (ushort)d.Iced.NearBranchTarget;
                if (targetKey >= 0 && lifter.InlineParamFunctions.TryGetValue(targetKey, out int skip))
                {
                    ushort ret = (ushort)(d.Iced.IP + (ulong)d.Iced.Length);
                    ushort ideal = (ushort)(ret + skip);
                    // Snap: buscar la instrucción decodificada más cercana >= ideal
                    ushort snapped = ideal;
                    if (!decoded.Contains(ideal))
                    {
                        snapped = 0xFFFF;
                        foreach (ushort off in decoded)
                            if (off >= ideal && off < snapped) snapped = off;
                    }
                    if (snapped < 0xFFFF)
                    {
                        extraLabels.Add(snapped);
                        skipTargets[d.Offset] = snapped;
                    }
                    else
                    {
                        // Skip target fuera de esta función — tail-call a la función que empieza allí
                        int tcKey = fn.Base + ideal;
                        if (allKnownKeys != null && allKnownKeys.Contains(tcKey))
                            tailCallTargets[d.Offset] = tcKey;
                    }
                }
            }
        }
        lifter.InlineSkipTargets = skipTargets.Count > 0 ? skipTargets : null;
        lifter.InlineTailCallTargets = tailCallTargets.Count > 0 ? tailCallTargets : null;

        bool needEntryLabel = fn.Instructions.Count > 0 && fn.Instructions[0].Offset < fn.SegEntry;
        for (int _ii = 0; _ii < fn.Instructions.Count; _ii++)
        {
            var d = fn.Instructions[_ii];
            if (labels.Contains(d.Offset) || extraLabels.Contains(d.Offset) || (needEntryLabel && d.Offset == fn.SegEntry))
                methodBody.Append($"        L_{d.Offset:X4}: if (!c.Step()) return;\n");
            if (trace) methodBody.Append($"            m.LogIP(0x{d.Offset:X4} | (c.SP << 16));\n");
            bool lifted = lifter.TryLift(d, out string code);
            // Interp: un opcode no representable (datos decodificados como
            // código, x87 raro...) deja de ser un throw ciego — con el intérprete activo se
            // ejecutan los bytes ORIGINALES desde esa dirección. El throw queda como fallback.
            if (!lifted && softStubs)
            {
                int badKey = fn.Base + d.Offset;
                code = $"{{ c.GapHits?.Add(0x{badKey:X5}); if (Cpu.GapTrace) System.Console.Error.WriteLine(\"[GAP] opcode no representable @0x{badKey:X5}\"); " +
                       $"if (Interp.Enabled) {{ if (Interp.RunGap(c, m, 0x{fn.Base:X5}, 0x{d.Offset:X4}, CallKey, FarDispatch)) c.FarRet = true; return; }} {code} }}";
            }
            // VM-INT class (session 17): a generic software `int n` (n != 0x3F) whose vector the
            // game installed reads its RETURN IP off the stack as an inline-operand pointer
            // (threaded-code runtimes: a game's INT 91h graphics VM, Turbo Basic's INT ECh p-code). The bytes past
            // the int are operands, not code, so the lifted continuation is garbage by construction
            // and must NOT run. When the game-vector guard holds at runtime, hand the whole
            // continuation to the interp FROM THE int's own address (same RunGap contract as the
            // gaps): it re-executes the `int n`, vectors faithfully through the IVT, and consumes
            // the inline operands. WholeProgram is on for the duration so each ISR's iret continues
            // in-interp (mirrors DosPlatform.Int and the EXEC-child run) until the entry frame
            // retf-unwinds back here or the program exits. If no game vector is installed (or a
            // built-in C# handler exists, e.g. INT 21h), fall back to the built-in dispatch (`code`).
            // The handoff runs PURE interp (callKey/farDispatch = null, the interp-verify mode):
            // a threaded program fires INTs forever, and every interp->lifted->int bounce would
            // nest one more C# frame that never unwinds (a Turbo Basic game: stack overflow 0xC00000FD). Pure
            // mode emulates far calls like hardware, so control never re-enters lifted code and
            // the C# stack depth stays flat for the whole life of the VM.
            else if (softStubs && d.Iced.Mnemonic == Mnemonic.Int && d.Iced.Immediate8 != 0x3F)
            {
                byte intn = d.Iced.Immediate8;
                // The lifted int `code` ends in a `// int NN` line comment, so the closing brace
                // must go on its OWN line or the comment would swallow it.
                code = $"{{ if (Interp.Enabled && c.Platform is PakuPort.Core.Platform.DosPlatform __dp && __dp.HasGameSoftIntVector(m, 0x{intn:X2})) " +
                       $"{{ bool __wp = Interp.WholeProgram; Interp.WholeProgram = true; bool __r; " +
                       $"try {{ __r = Interp.RunGap(c, m, 0x{fn.Base:X5}, 0x{d.Offset:X4}, null, null); }} finally {{ Interp.WholeProgram = __wp; }} " +
                       $"if (__r) c.FarRet = true; return; }} {code}\n            }}";
            }
            methodBody.Append($"            // {d.Offset:X4}: {d.Text}\n");
            methodBody.Append($"            {code}\n");

            // hueco de fallthrough. El sucesor lineal de esta instruccion
            // (offset+longitud) puede no estar decodificado (rama fusionada por offset desde otro
            // punto de entrada). Si el Lifter no corto el flujo arriba (no emitio return/goto),
            // caer aqui ejecutaria bytes originales nunca decodificados: cortar y registrar.
            bool endsFlow = d.Iced.Mnemonic is Mnemonic.Jmp or Mnemonic.Ret or Mnemonic.Retf or Mnemonic.Iret or Mnemonic.Hlt;
            // d.Length, NO d.Iced.Length: el Decoder ajusta la longitud de los thunks int 3F
            // (CD 3F + 3 bytes de operando inline = 5). Con la longitud cruda de iced (2) el
            // sucesor caia en MITAD del operando -> gap espurio -> RunGap sobre la
            // OverlayWindow vacia -> el interp ejecutaba ceros (techo de la generación del mundo de un juego de prueba).
            ushort succ = (ushort)(d.Offset + d.Length);
            if (!endsFlow && !decoded.Contains(succ))
            {
                int gapKey = fn.Base + succ;
                methodBody.Append($"            {{ c.GapHits?.Add(0x{gapKey:X5}); if (Cpu.GapTrace) System.Console.Error.WriteLine(\"[GAP] fallthrough no decodificado @0x{gapKey:X5}\"); " +
                    $"if (Interp.Enabled && Interp.RunGap(c, m, 0x{fn.Base:X5}, 0x{succ:X4}, CallKey, FarDispatch)) c.FarRet = true; return; }}\n");
            }
        }

        // Encontrar TODOS los "goto L_XXXX" en el cuerpo y generar stubs para labels faltantes
        var body = methodBody.ToString();
        var emittedLabels = new HashSet<string>();
        foreach (var t in labels) emittedLabels.Add($"L_{t:X4}");
        foreach (var t in extraLabels) emittedLabels.Add($"L_{t:X4}");
        var missingLabels = new HashSet<string>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(body, @"goto (L_[0-9A-Fa-f]{4})"))
        {
            string lbl = m.Groups[1].Value;
            if (!emittedLabels.Contains(lbl) && !decoded.Contains(Convert.ToUInt16(lbl[2..], 16)))
                missingLabels.Add(lbl);
        }

        src.Append($"        // fn @file 0x{fn.Key:X5} (base 0x{fn.Base:X5}, seg 0x{fn.SegEntry:X4})\n");
        src.Append($"        public static void Fn_{fn.Key:X5}(Cpu c, Memory m)\n        {{\n");
        // Stack level at entry: the post-call retf guard (Lifter.CallGuard) compares against it
        // to detect a callee that unwound THIS function's frame via a shared-epilogue retf.
        src.Append("            ushort _spEntry = c.SP; _ = _spEntry;\n");
        // Guest calls become nested C# calls, so a guest chain that never returns would overflow
        // the host stack and kill the process uncatchably. See Cpu.GuardStack.
        src.Append("            Cpu.GuardStack(c);\n");
        if (fn.Instructions.Count > 0 && fn.Instructions[0].Offset < fn.SegEntry)
            src.Append($"            goto L_{fn.SegEntry:X4};\n");
        src.Append(body);

        var allUndecoded = labels.Where(t => !decoded.Contains(t)).ToList();
        if (allUndecoded.Count > 0 || missingLabels.Count > 0)
        {
            src.Append("            return;\n");
            foreach (var t in allUndecoded)
            {
                int gapKey = fn.Base + t;
                src.Append(softStubs
                    ? $"        L_{t:X4}: {{ c.GapHits?.Add(0x{gapKey:X5}); if (Cpu.GapTrace) System.Console.Error.WriteLine(\"[GAP] camino no ejercitado @0x{gapKey:X5}\"); " +
                      $"if (Interp.Enabled && Interp.RunGap(c, m, 0x{fn.Base:X5}, 0x{t:X4}, CallKey, FarDispatch)) c.FarRet = true; return; }} // camino no ejercitado @0x{t:X4}\n"
                    : $"        L_{t:X4}: throw new System.NotSupportedException(\"camino no ejercitado @0x{t:X4}\");\n");
            }
            foreach (var lbl in missingLabels)
            {
                // Salto de cola fuera de la función (epílogo compartido, optimización de
                // tamaño clásica): un `return` a secas DEJA EL FRAME DOS SIN DESPILAR y el
                // SP se hunde llamada a llamada (R6000 del runtime C del juego). Despachar
                // al código real del target: su epílogo (mov sp,bp; pop bp; ret) equilibra.
                int tailKey = fn.Base + Convert.ToInt32(lbl[2..], 16);
                src.Append(softStubs
                    ? $"        {lbl}: if (!CallKey(c, m, 0x{tailKey:X5})) {{ c.FarDispatchMisses?.Add(0x{tailKey:X5}); " +
                      $"if (Interp.Enabled && Interp.RunGap(c, m, 0x{fn.Base:X5}, 0x{tailKey - fn.Base:X4}, CallKey, FarDispatch)) c.FarRet = true; }} return; // salto de cola @0x{tailKey:X5}\n"
                    : $"        {lbl}: throw new System.NotSupportedException(\"salto fuera de ventana: {lbl}\");\n");
            }
        }
        src.Append("        }\n");
    }

    /// <summary>
    /// Descubre TODAS las funciones del EXE: combina semillas explícitas (corpus/entry) con
    /// barrido del CodeMap. Para cada offset del CodeMap que no pertenezca a una función ya
    /// descubierta, intenta decodificar como función nueva. Esto captura funciones que solo
    /// se llaman indirectamente (function pointers, dispatch tables).
    /// </summary>
    public static Dictionary<int, DecodedFunction> DiscoverAll(ExeImage exe, OverlayDirectory dir,
        IEnumerable<int> seeds, bool followFar = true, bool comMode = false, Dictionary<int, int>? vroommStubTable = null,
        HashSet<int>? known = null)
    {
        var all = new Dictionary<int, DecodedFunction>();
        // Set compartido de claves ya decodificadas. Se pasa a cada DiscoverCallTree para que sus
        // BFS se saltén subárboles ya explorados: evita el O(seeds × tree) cuando las semillas
        // solapan (caso normal: 1364 seeds Spice86 que apuntan a sitios ya alcanzados por el
        // árbol del entry point). Si el llamador lo pasa, se inicializa con sus claves — el
        // probe loop del export reusa el mismo set entre iteraciones para no re-descubrir.
        var knownLocal = known != null ? new HashSet<int>(known) : new HashSet<int>();

        // Fase 1: desde semillas explícitas (corpus + entry)
        foreach (int seed in seeds)
        {
            if (knownLocal.Contains(seed)) continue;
            try
            {
                var tree = DiscoverCallTree(exe, seed, dir, followFar, maxFuncs: 512, comMode: comMode, vroommStubTable: vroommStubTable, known: knownLocal);
                foreach (var fn in tree)
                {
                    if (all.TryAdd(fn.Key, fn)) knownLocal.Add(fn.Key);
                }
                if (!knownLocal.Contains(seed))
                    Console.Error.WriteLine($"  [discover] seed 0x{seed:X5}: el arbol no incluyo la propia seed (funcs={tree.Count})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [discover] seed 0x{seed:X5} descartada: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Fase 2: barrido del CodeMap — buscar inicios de función no alcanzados por call graph
        if (exe.CodeMap != null)
        {
            // Construir intervalos cubiertos [start, end) para filtrar rápido
            var intervals = new List<(int start, int end)>();
            foreach (var fn in all.Values)
            {
                if (fn.Instructions.Count == 0) continue;
                int start = fn.Key;
                var last = fn.Instructions[^1];
                int end = fn.Base + last.Offset + last.Length;
                intervals.Add((start, end));
            }
            intervals.Sort((a, b) => a.start.CompareTo(b.start));

            // Recoger call targets del EXE (E8 rel16) como indicadores de inicio de función
            var callTargets = new HashSet<int>();
            int imageEnd = Math.Min(exe.Length, ExeImage.ResidentBase + 0x30000);
            for (int off = ExeImage.ResidentBase; off < imageEnd - 3; off++)
            {
                byte b = exe.ByteAt(off);
                if (b == 0xE8)
                {
                    short rel = (short)(exe.ByteAt(off + 1) | (exe.ByteAt(off + 2) << 8));
                    int target = off + 3 + rel;
                    if (target >= ExeImage.ResidentBase && target < imageEnd)
                        callTargets.Add(target);
                }
            }

            // Candidatos: call targets que apuntan a código verificado (CodeMap) o tienen prólogo
            var codeSet = exe.CodeMap != null ? new HashSet<int>(exe.CodeMap) : new HashSet<int>();
            var candidateSet = new HashSet<int>();
            foreach (int t in callTargets)
            {
                if (t < ExeImage.ResidentBase || t >= imageEnd) continue;
                // Confiar si el target está en el CodeMap (código verificado por emulación)
                if (codeSet.Contains(t)) { candidateSet.Add(t); continue; }
                // Si no está en CodeMap, solo aceptar si tiene prólogo push bp; mov bp,sp
                if (exe.ByteAt(t) == 0x55 && exe.ByteAt(t + 1) == 0x8B && exe.ByteAt(t + 2) == 0xEC)
                    candidateSet.Add(t);
            }
            // Añadir offsets del CodeMap con prólogo push bp (funciones llamadas indirectamente)
            foreach (int off in codeSet)
                if (off >= ExeImage.ResidentBase &&
                    exe.ByteAt(off) == 0x55 && exe.ByteAt(off + 1) == 0x8B && exe.ByteAt(off + 2) == 0xEC)
                    candidateSet.Add(off);

            // Barrido de prólogos push bp; mov bp,sp en TODO el EXE (captura C-runtime)
            for (int off = ExeImage.ResidentBase; off < imageEnd - 3; off++)
                if (exe.ByteAt(off) == 0x55 && exe.ByteAt(off + 1) == 0x8B && exe.ByteAt(off + 2) == 0xEC)
                    candidateSet.Add(off);

            // Call far 9A scan: disabled — causes false positives that break game state
            // when code at the target is actually data. The iterative discovery via
            // FarDispatch MISSes is the correct way to find far-called functions.
            // int loadSeg = exe.LoadSegment;
            // if (loadSeg > 0) { ... }

            // Far pointers in data: disabled for now — the reference game has 0 relocs, and false positives
            // from other games can cause incorrect function discovery
            // foreach (int target in exe.GetFarPointerTargets())
            //     if (target < imageEnd)
            //         candidateSet.Add(target);

            var candidates = candidateSet.OrderBy(x => x).ToList();

            foreach (int off in candidates)
            {
                if (knownLocal.Contains(off)) continue;
                // Verificar que no cae dentro de una función ya descubierta
                int idx = intervals.BinarySearch((off, off), Comparer<(int, int)>.Create((a, b) => a.Item1.CompareTo(b.Item1)));
                if (idx < 0) idx = ~idx - 1;
                if (idx >= 0 && idx < intervals.Count && off >= intervals[idx].start && off < intervals[idx].end)
                    continue;

                try
                {
                    var tree = DiscoverCallTree(exe, off, dir, followFar, maxFuncs: 512, comMode: comMode, vroommStubTable: vroommStubTable, known: knownLocal);
                    foreach (var fn in tree)
                    {
                        if (all.TryAdd(fn.Key, fn))
                        {
                            knownLocal.Add(fn.Key);
                            var last2 = fn.Instructions.Count > 0 ? fn.Instructions[^1] : default;
                            int end2 = fn.Base + last2.Offset + last2.Length;
                            intervals.Add((fn.Key, end2));
                        }
                    }
                }
                catch { }
            }
            intervals.Sort((a, b) => a.start.CompareTo(b.start));
        }

        // Fase 3: inline param recovery (iterativa) — funciones con add [bp+2],N causan que el
        // decoder no alcance las instrucciones tras los datos inline. Re-decodificar desde los skip
        // targets, iterando hasta que no haya semillas nuevas (las funciones recuperadas pueden a su
        // vez contener calls a funciones inline param).
        var inlineParams = DetectInlineParamFunctions(all.Values.ToList());
        if (inlineParams.Count > 0)
        {
            for (int round = 0; round < 8; round++)
            {
                var skipSeeds = new HashSet<int>();
                foreach (var fn in all.Values)
                {
                    foreach (var d in fn.Instructions)
                    {
                        if (d.Iced.Mnemonic != Mnemonic.Call) continue;
                        int targetKey = -1;
                        if (d.FarCallTargetKey >= 0) targetKey = d.FarCallTargetKey;
                        else if (d.Iced.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                            targetKey = fn.Base + (ushort)d.Iced.NearBranchTarget;
                        if (targetKey < 0 || !inlineParams.TryGetValue(targetKey, out int skip)) continue;
                        ushort ret = (ushort)(d.Iced.IP + (ulong)d.Iced.Length);
                        int skipKey = fn.Base + (ushort)(ret + skip);
                        if (!all.ContainsKey(skipKey)) skipSeeds.Add(skipKey);
                    }
                }
                if (skipSeeds.Count == 0) break;
                foreach (int seed in skipSeeds)
                {
                    if (knownLocal.Contains(seed)) continue;
                    try
                    {
                        var tree = DiscoverCallTree(exe, seed, dir, followFar, maxFuncs: 64, comMode: comMode, vroommStubTable: vroommStubTable, known: knownLocal);
                        foreach (var fn in tree)
                        {
                            if (all.TryAdd(fn.Key, fn)) knownLocal.Add(fn.Key);
                        }
                    }
                    catch { }
                }
            }
        }

        return all;
    }

    /// <summary>
    /// Descubre el árbol de llamadas: `call near` (mismo módulo) e `INT 3F` (a un overlay). Devuelve
    /// las funciones (raíz primero), dedup por clave. Un destino con error de decode se omite.
    /// <para><paramref name="known"/> permite pasar un set de claves YA decodificadas para saltarse
    /// sus subárboles enteros sin re-trabajo: clave para que <see cref="DiscoverAll"/> sea O(seeds+tree)
    /// en lugar de O(seeds × tree) cuando las semillas solapan. El llamador es responsable de
    /// añadir las claves devueltas en <c>order</c> a <paramref name="known"/> tras la llamada.</para>
    /// </summary>
    public static List<DecodedFunction> DiscoverCallTree(ExeImage exe, int rootKey, OverlayDirectory dir, bool followFar = false, int maxFuncs = 1024, bool comMode = false, Dictionary<int, int>? vroommStubTable = null, HashSet<int>? known = null)
    {
        var visited = known != null ? new HashSet<int>(known) : new HashSet<int>();
        var order = new List<DecodedFunction>();
        var queue = new Queue<int>();
        queue.Enqueue(rootKey);
        while (queue.Count > 0 && order.Count < maxFuncs)
        {
            int key = queue.Dequeue();
            if (!visited.Add(key)) continue;
            int baseFile = dir.BaseOf(key);
            DecodedFunction fn;
            try
            {
                // Para funciones en el segmento residente, pasar bytes desde la base del segmento
                // (no desde el key) para que saltos hacia atrás se resuelvan dentro de la ventana.
                byte[] code;
                int codeBaseOff = -1;
                if (baseFile == ExeImage.ResidentBase)
                {
                    code = exe.GetBytesForKey(baseFile, 0xFFFF);
                    codeBaseOff = 0;
                }
                else
                    code = exe.GetBytesForKey(key);
                fn = Decoder16.DecodeFunction(code, key, baseFile, dir, exe, followFar,
                    intFallsThrough: true, codeBaseOffset: codeBaseOff);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [discover] key 0x{key:X5} no decodifica: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            order.Add(fn);
            var bodyOffs = new HashSet<int>(fn.Instructions.Count);
            foreach (var d in fn.Instructions) bodyOffs.Add(d.Offset);
            foreach (var d in fn.Instructions)
            {
                if (d.OverlayTargetKey >= 0) { queue.Enqueue(d.OverlayTargetKey); continue; }  // INT 3F
                if (d.FarCallTargetKey >= 0) { queue.Enqueue(d.FarCallTargetKey); continue; } // call far -> residente real
                if (d.JumpTable != null)                                        // dispatcher: handlers
                { foreach (var (_, tk) in d.JumpTable) queue.Enqueue(tk); continue; }
                if (d.Iced.FlowControl == FlowControl.Call &&
                    d.Iced.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                {
                    ushort tgt = (ushort)d.Iced.NearBranchTarget;
                    int codeSz = dir.CodeSizeOf(baseFile);
                    if (codeSz > 0 && tgt >= codeSz)
                    {
                        if (vroommStubTable != null && vroommStubTable.TryGetValue(tgt, out int resolved))
                            queue.Enqueue(resolved);
                        continue;
                    }
                    queue.Enqueue(baseFile + tgt);
                }
                // Salto de cola fuera del cuerpo decodificado (epílogo compartido): el emitter lo
                // stubbea como despacho CallKey en runtime ("salto de cola"), así que el target
                // DEBE estar emitido — un MISS silencioso deja la pila sin desapilar (pop es/ds
                // posteriores leen el frame far como datos → DS basura aguas abajo).
                if (d.Iced.FlowControl is FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch &&
                    d.Iced.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                {
                    ushort tgt = (ushort)d.Iced.NearBranchTarget;
                    if (!bodyOffs.Contains(tgt))
                    {
                        int codeSz = dir.CodeSizeOf(baseFile);
                        if (codeSz <= 0 || tgt < codeSz) queue.Enqueue(baseFile + tgt);
                    }
                }
            }
        }
        return order;
    }

    /// <summary>
    /// Detecta funciones con patrón "inline params" (compiladores Borland/Turbo):
    /// <c>push bp; mov bp,sp; les bx,[bp+2]; add [bp+2],N; pop bp</c>.
    /// Estas funciones leen N bytes de datos inline tras el CALL y ajustan la dirección de retorno.
    /// </summary>
    public static Dictionary<int, int> DetectInlineParamFunctions(IReadOnlyList<DecodedFunction> funcs)
    {
        var result = new Dictionary<int, int>();
        foreach (var fn in funcs)
        {
            var insns = fn.Instructions;
            int limit = Math.Min(insns.Count, 8);
            for (int i = 0; i < limit; i++)
            {
                var iced = insns[i].Iced;
                if (iced.Mnemonic != Mnemonic.Add) continue;
                if (iced.Op0Kind != OpKind.Memory) continue;
                if (iced.MemoryBase != Register.BP) continue;
                if (iced.MemoryIndex != Register.None) continue;
                if (iced.MemoryDisplacement32 != 2) continue;
                if (iced.MemorySize != MemorySize.UInt16) continue;
                if (iced.Op1Kind is not (OpKind.Immediate8to16 or OpKind.Immediate16 or OpKind.Immediate8)) continue;
                int skip = (ushort)iced.GetImmediate(1);
                if (skip > 0 && skip <= 64)
                {
                    result[fn.Key] = skip;
                    break;
                }
            }
        }
        return result;
    }
}
