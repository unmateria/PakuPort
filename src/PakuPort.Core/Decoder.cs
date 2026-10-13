using Iced.Intel;

namespace PakuPort.Core;

/// <summary>Una instrucción decodificada (offsets relativos al segmento de 16 bits del módulo).</summary>
public readonly struct DecodedInsn
{
    public readonly ushort Offset;        // offset relativo al segmento (16-bit)
    public readonly int Length;           // bytes (INT 3F = 5: int + descriptor)
    public readonly string Mnemonic;
    public readonly string Text;
    public readonly byte[] Bytes;
    public readonly Instruction Iced;
    public readonly int OverlayTargetKey; // INT 3F: clave (offset de fichero) del overlay destino; -1 si no
    /// <summary>jmp indirecto por tabla (CS-relativa) resuelto: (valor del reg índice, clave destino).</summary>
    public readonly (ushort idx, int targetKey)[]? JumpTable;
    public readonly string? JumpIndexReg;   // expresión C# del registro índice (p.ej. "c.BX")
    /// <summary>call far selector-0 a una FUNCIÓN residente real (no thunk): clave a seguir; -1 = stub.</summary>
    public readonly int FarCallTargetKey;

    public DecodedInsn(ushort offset, int length, string mnemonic, string text, byte[] bytes,
                       Instruction iced, int overlayTargetKey = -1,
                       (ushort idx, int targetKey)[]? jumpTable = null, string? jumpIndexReg = null,
                       int farCallTargetKey = -1)
    {
        Offset = offset; Length = length; Mnemonic = mnemonic; Text = text; Bytes = bytes;
        Iced = iced; OverlayTargetKey = overlayTargetKey; JumpTable = jumpTable; JumpIndexReg = jumpIndexReg;
        FarCallTargetKey = farCallTargetKey;
    }

    public override string ToString() => $"{Offset:X4}: {Convert.ToHexString(Bytes).ToLowerInvariant(),-16} {Text}";
}

public sealed class DecodedFunction
{
    /// <summary>Clave única de la función = offset de FICHERO de su entrada.</summary>
    public required int Key { get; init; }
    /// <summary>Offset de fichero donde empieza el código del MÓDULO (0x200 residente; overlayCodeStart[n]).</summary>
    public required int Base { get; init; }
    public required IReadOnlyList<DecodedInsn> Instructions { get; init; }
    public required IReadOnlySet<ushort> JumpTargets { get; init; }

    /// <summary>Offset relativo al segmento de la entrada (16-bit) = Key - Base.</summary>
    public ushort SegEntry => (ushort)(Key - Base);
}

/// <summary>
/// Decoder de PakuPort: iced-x86 16-bit, descenso recursivo intra-función, trabajando en el espacio
/// de offsets RELATIVOS AL SEGMENTO (16-bit) del módulo. <b>INT 3F</b> (VROOMM) es un thunk de 5 bytes
/// <c>CD 3F &lt;ovno&gt; &lt;offset&gt;</c>: se trata como una unidad de 5 bytes, se resuelve su destino
/// (otro módulo) vía <see cref="OverlayDirectory"/>, y la ejecución CAE a la siguiente instrucción.
/// Los demás INT también caen (retornan). Estático, sin emulador.
/// </summary>
public static class Decoder16
{
    public static DecodedFunction DecodeFunction(byte[] code, int key, int baseFile, OverlayDirectory dir,
                                                 ExeImage? exe = null, bool followFar = false, int maxInsns = 8192,
                                                 bool intFallsThrough = false, int codeBaseOffset = -1)
    {
        ushort segEntry = (ushort)(key - baseFile);
        ushort codeStart = codeBaseOffset >= 0 ? (ushort)codeBaseOffset : segEntry;
        var reader = new ByteArrayCodeReader(code);
        var decoder = Iced.Intel.Decoder.Create(16, reader, segEntry, DecoderOptions.None);
        var formatter = new MasmFormatter();
        var output = new StringOutput();

        var byOffset = new SortedDictionary<ushort, DecodedInsn>();
        var jumpTargets = new HashSet<ushort>();
        var worklist = new Stack<ushort>();
        worklist.Push(segEntry);

        // CodeMap de radare2 (whitelist código/datos). Solo se aplica si radare2 analizó ESTA función
        // (su entrada está en el mapa) — así no se trunca una función que radare2 no cubrió.
        var codeMap = exe?.CodeMap;
        bool useCodeMap = codeMap != null && codeMap.Contains(key);

        // Targets de saltos condicionales desde instrucciones ya en el CodeMap: son código
        // seguro incluso si el CodeMap no los cubre (hueco de cobertura del emulador).
        var trustedTargets = new HashSet<ushort>();

        int budget = maxInsns;
        while (worklist.Count > 0 && budget > 0)
        {
            ushort addr = worklist.Pop();
            while (budget-- > 0)
            {
                if (byOffset.ContainsKey(addr)) break;
                // Skip if addr falls inside an already-decoded instruction
                if (FallsInsideExisting(byOffset, addr)) break;
                int idx = addr - codeStart;
                if (idx < 0 || idx >= code.Length) break;
                if (useCodeMap && !codeMap!.Contains(baseFile + addr) && !trustedTargets.Contains(addr)) break;

                reader.Position = idx;
                decoder.IP = addr;
                decoder.Decode(out Instruction insn);
                if (insn.Code == Code.INVALID) break;
                if (IsImplausibleRealMode(in insn)) break;

                int len = insn.Length;
                string mnem = insn.Mnemonic.ToString().ToLowerInvariant();
                int overlayKey = -1;
                bool isOverlay = false;

                if (insn.Mnemonic == Mnemonic.Int && insn.GetOpKind(0) == OpKind.Immediate8
                    && insn.Immediate8 == 0x3F && idx + 5 <= code.Length)
                {
                    int ovno = code[idx + 2];
                    int off = code[idx + 3] | (code[idx + 4] << 8);
                    overlayKey = dir.Resolve(ovno, off);
                    isOverlay = true;
                    len = 5;
                }

                var bytes = code[idx..Math.Min(idx + len, code.Length)];

                // jmp por tabla CS-relativa (dispatcher): resolver estáticamente leyendo la tabla del EXE.
                // SOLO en mundo canónico o fuera del residente: en el residente híbrido (memimage)
                // el disp y las entradas de la tabla son RealCS-relativas (medido en un juego de prueba: disp
                // 0xBFA9 con tabla RealCS-rel) — la resolución estática leería basura. El camino
                // runtime (LiftNearIndirectJmp, bias-consciente) las resuelve correctamente.
                (ushort idx, int targetKey)[]? jumpTable = null;
                string? jumpIdxReg = null;
                if (exe != null && (exe.ImageBias == 0 || baseFile != OverlayDirectory.ResidentBase)
                    && insn.Mnemonic == Mnemonic.Jmp && insn.Op0Kind == OpKind.Memory
                    && insn.MemorySegment == Register.CS && insn.MemoryBase != Register.None
                    && insn.MemoryIndex == Register.None)
                {
                    (jumpTable, jumpIdxReg) = ResolveJumpTable(insn, addr, baseFile, exe, byOffset);
                }

                // call far seg:offset → registrar target para FarDispatch si apunta a código residente.
                // Seg puede ser 0 (pre-reloc, VROOMM: necesita followFar) o un segmento absoluto
                // (MZ relocado: siempre seguir).
                int farCallKey = -1;
                if (exe != null && insn.Mnemonic == Mnemonic.Call
                    && insn.Op0Kind == OpKind.FarBranch16)
                {
                    int t = -1;
                    ushort sel = insn.FarBranchSelector;
                    // Imagen COMPUESTA (drivers en 0x40000+): una far-call interna del driver lleva
                    // el segmento ya relocado (9A off seg, seg=0x4956...). Resolverla a su key en el
                    // composite para que el discover siga las internas del driver y las emita como Fn_.
                    if (sel != 0 && exe.TryResolveDriverFar(sel, insn.FarBranch16, out int dvk))
                        t = dvk;
                    else if (sel == 0 && followFar)
                        // ImageBias: en el mundo memimage el offset x86 lleva el sesgo de la
                        // descompresión EXEPACK (linear = load + bias + ghidra). Sin restarlo,
                        // `call far 0:0390` resolvía a la key 0x129 bytes ARRIBA (p.ej. el
                        // read-key del menú de un juego caía en la rutina error-exit del residente).
                        t = OverlayDirectory.ResidentBase + insn.FarBranch16 - (exe?.ImageBias ?? 0);
                    else if (sel != 0 && exe.LoadSegment != 0 && sel >= exe.LoadSegment)
                        // ImageBias: en la imagen híbrida (mundo memimage) linear = loadSeg*16
                        // + bias + ghidra; sin restarlo la key cae bias bytes ARRIBA y se
                        // liftea código corrido (cazado: lcall 1000:09BF → Fn de bytes basura
                        // que dejaba BP=0x0068 y `mov sp,bp` hundía la pila).
                        t = (sel - exe.LoadSegment) * 16 + insn.FarBranch16 - exe.ImageBias + OverlayDirectory.ResidentBase;
                    if (t >= OverlayDirectory.ResidentBase && t < exe.Length)
                    {
                        byte b0 = exe.ByteAt(t);
                        // Excluir stubs/thunks VROOMM del despacho estático: un far-call cuyo
                        // destino empieza por EA (jmp far ya parcheado) o E8 (call al loader del
                        // overlay manager, seguido de un far-ptr descriptor) NO es una entrada de
                        // función — se resuelve en runtime por FarDispatch, que sigue el thunk al
                        // driver/overlay real. Liftarlo como Fn_ estática ejecuta el stub como
                        // código basura (p.ej. un `pop ds` espurio corrompía DS -> #DE aguas abajo).
                        if (b0 != 0xEA && b0 != 0xE8 && b0 != 0xC3 && b0 != 0xCB && b0 != 0xCC && b0 != 0x00)
                            farCallKey = t;
                    }
                }

                string text;
                if (isOverlay)
                    text = overlayKey < 0 ? "int 0x3F -> ov?? (no en directorio)" : $"int 0x3F -> Fn_{overlayKey:X5}";
                else if (jumpTable != null) text = $"jmp tabla[{jumpTable.Length}] cs:[{insn.MemoryBase}]";
                else { output.Reset(); formatter.Format(in insn, output); text = output.ToStringAndReset(); }
                byOffset[addr] = new DecodedInsn(addr, len, mnem, text, bytes, insn, overlayKey, jumpTable, jumpIdxReg, farCallKey);

                ushort next = (ushort)(addr + len);

                bool hasNearTarget = insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;
                if (hasNearTarget)
                {
                    ushort tgt = (ushort)insn.NearBranchTarget;
                    bool inWindow = (tgt - codeStart) >= 0 && (tgt - codeStart) < code.Length;
                    if (inWindow && insn.FlowControl is not FlowControl.Call)
                    {
                        jumpTargets.Add(tgt);
                        worklist.Push(tgt);
                        trustedTargets.Add(tgt);
                    }
                }

                // Si la instrucción actual cae al next (no es terminador), el next es código confiable.
                // Esto propaga la confianza linealmente: branch condicional → su fallthrough,
                // call → su return address, instrucciones normales → su siguiente. NO se propaga
                // tras terminadores (ret, jmp incondicional, jmp indirecto) porque el next podría
                // ser datos intercalados.
                if (insn.FlowControl is not (FlowControl.Return or FlowControl.UnconditionalBranch
                    or FlowControl.IndirectBranch))
                    trustedTargets.Add(next);

                // INT 3F (overlay) CAE a la siguiente instrucción (el overlay retorna y continúa).
                // Un INT de DOS/BIOS (stub) CORTA el decode aquí: si el post-INT escribiera DGROUP el
                // juez lo cazaría (rojo), no hay falso verde; y evita decodificar datos tras el INT.
                bool fallsThrough = isOverlay || insn.FlowControl switch
                {
                    FlowControl.Return => false,
                    FlowControl.UnconditionalBranch => false,
                    FlowControl.IndirectBranch => false,
                    FlowControl.Interrupt => intFallsThrough,
                    _ => true,
                };
                if (!fallsThrough) break;
                addr = next;
            }
        }

        // CodeMap gap recovery: tras agotar el worklist, buscar direcciones CodeMap-verificadas
        // que quedaron sin decodificar tras terminadores (jmp indirect, ret). Captura código
        // solo accesible por jump tables (targets de jmp word ptr [reg+disp]).
        if (useCodeMap && budget > 0)
        {
            bool foundNew = true;
            while (foundNew && budget > 0)
            {
                foundNew = false;
                // Buscar el primer hueco: dirección post-terminador en CodeMap no decodificada
                foreach (var kv in byOffset)
                {
                    var di = kv.Value;
                    bool isTerminator = di.Iced.FlowControl is FlowControl.Return
                        or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch;
                    if (!isTerminator) continue;
                    ushort after = (ushort)(kv.Key + di.Length);
                    if (byOffset.ContainsKey(after)) continue;
                    // Scan forward for nearest CodeMap address (INT may skip a few bytes)
                    ushort found = 0; bool ok = false;
                    for (int scan = 0; scan < 16; scan++)
                    {
                        ushort candidate = (ushort)(after + scan);
                        int ci = candidate - codeStart;
                        if (ci < 0 || ci >= code.Length) break;
                        if (codeMap!.Contains(baseFile + candidate))
                        { found = candidate; ok = true; break; }
                    }
                    if (!ok) continue;
                    jumpTargets.Add(found);
                    worklist.Push(found);
                    foundNew = true;
                    break;
                }
                // Decodificar desde las nuevas semillas
                while (worklist.Count > 0 && budget > 0)
                {
                    ushort addr2 = worklist.Pop();
                    while (budget-- > 0)
                    {
                        if (byOffset.ContainsKey(addr2)) break;
                        if (FallsInsideExisting(byOffset, addr2)) break;
                        int idx2 = addr2 - codeStart;
                        if (idx2 < 0 || idx2 >= code.Length) break;
                        if (!codeMap!.Contains(baseFile + addr2)) break;

                        reader.Position = idx2;
                        decoder.IP = addr2;
                        decoder.Decode(out Instruction insn2);
                        if (insn2.Code == Code.INVALID || IsImplausibleRealMode(in insn2)) break;

                        int len2 = insn2.Length;
                        string mnem2 = insn2.Mnemonic.ToString().ToLowerInvariant();
                        output.Reset(); formatter.Format(in insn2, output); string text2 = output.ToStringAndReset();
                        byOffset[addr2] = new DecodedInsn(addr2, len2, mnem2, text2, code[idx2..Math.Min(idx2 + len2, code.Length)], insn2);

                        bool hasNear2 = insn2.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;
                        if (hasNear2 && insn2.FlowControl is not FlowControl.Call)
                        {
                            ushort tgt2 = (ushort)insn2.NearBranchTarget;
                            if ((tgt2 - codeStart) >= 0 && (tgt2 - codeStart) < code.Length)
                            {
                                jumpTargets.Add(tgt2);
                                worklist.Push(tgt2);
                            }
                        }

                        bool falls2 = insn2.FlowControl switch
                        {
                            FlowControl.Return => false,
                            FlowControl.UnconditionalBranch => false,
                            FlowControl.IndirectBranch => false,
                            FlowControl.Interrupt => intFallsThrough,
                            _ => true,
                        };
                        if (!falls2) break;
                        addr2 = (ushort)(addr2 + len2);
                    }
                }
            }
        }

        return new DecodedFunction
        {
            Key = key,
            Base = baseFile,
            Instructions = byOffset.Values.ToList(),
            JumpTargets = jumpTargets,
        };
    }

    // Resuelve `jmp word ptr cs:[reg ± disp]` (dispatcher): lee la tabla del EXE (en el segmento de
    // código), acota con el `cmp reg,N` previo, y devuelve (valor índice → clave función destino).
    // Los destinos son funciones handler (el dispatcher tail-salta a ellas). Estático, sin emulador.
    private static ((ushort idx, int targetKey)[]? table, string? idxReg) ResolveJumpTable(
        in Instruction insn, ushort addr, int baseFile, ExeImage exe,
        SortedDictionary<ushort, DecodedInsn> decoded)
    {
        ushort tableBase = (ushort)insn.MemoryDisplacement64;

        // bound N = el `cmp <reg>, imm` más reciente antes del jmp (acota el nº de entradas).
        int n = -1;
        foreach (var kv in decoded)
        {
            if (kv.Key >= addr) break;
            var di = kv.Value.Iced;
            if (di.Mnemonic != Mnemonic.Cmp) continue;
            int imm = di.Op1Kind switch
            {
                OpKind.Immediate8 => di.Immediate8,
                OpKind.Immediate16 => di.Immediate16,
                OpKind.Immediate8to16 => (ushort)di.Immediate8to16,
                _ => -1
            };
            if (imm >= 0) n = imm;
        }
        if (n < 0 || n > 512) return (null, null);   // sin bound fiable

        var list = new List<(ushort, int)>();
        for (int k = 0; k <= n; k++)
        {
            int entryFile = baseFile + ((tableBase + 2 * k) & 0xFFFF);
            if (entryFile < 0 || entryFile + 1 >= exe.Length) break;
            ushort target = exe.RdU16File(entryFile);
            int targetKey = baseFile + target;
            if (targetKey < baseFile || targetKey >= exe.Length) break;   // destino implausible
            list.Add(((ushort)(2 * k), targetKey));
        }
        if (list.Count == 0) return (null, null);
        return (list.ToArray(), IndexRegName(insn.MemoryBase));
    }

    // ¿la instrucción decodificada es imposible en real-mode 8086/186/286? (= datos como código)
    private static bool FallsInsideExisting(SortedDictionary<ushort, DecodedInsn> byOffset, ushort addr)
    {
        // Find the largest key <= addr using the sorted dictionary
        ushort? prev = null;
        foreach (var k in byOffset.Keys)
        {
            if (k >= addr) break;
            prev = k;
        }
        if (prev == null) return false;
        var insn = byOffset[prev.Value];
        return addr < prev.Value + insn.Length;
    }

    private static bool IsImplausibleRealMode(in Instruction insn)
    {
        // XLATB es 8086 legítimo pero iced modela su operando implícito [BX+AL] con
        // índice AL y caía en el filtro de registros: cortaba la función a mitad y el
        // caller perdía 4 bytes de pila por llamada (cazado en el SetDrawColor de un driver gráfico).
        if (insn.Mnemonic == Mnemonic.Xlatb) return false;

        // XOR/AND/OR SP with memory operand — no real-mode program does this; data-as-code artifact
        if (insn.Op0Kind == OpKind.Register && insn.GetOpRegister(0) == Register.SP
            && insn.Op1Kind == OpKind.Memory
            && insn.Mnemonic is Mnemonic.Xor or Mnemonic.And or Mnemonic.Or)
            return true;

        // registros de 32 bits, XMM/MMX, FS/GS, DR/CR/TR → 386+ / SSE → datos
        for (int i = 0; i < insn.OpCount; i++)
            if (insn.GetOpKind(i) == OpKind.Register && !Is8086Reg(insn.GetOpRegister(i))) return true;
        if (insn.MemoryBase != Register.None && !Is8086AddrReg(insn.MemoryBase)) return true;
        if (insn.MemoryIndex != Register.None && !Is8086AddrReg(insn.MemoryIndex)) return true;

        // mnemónicos sin registro delator pero claramente posteriores al 286 real-mode
        switch (insn.Mnemonic)
        {
            case Mnemonic.Bswap: case Mnemonic.Arpl: case Mnemonic.Str: case Mnemonic.Lar:
            case Mnemonic.Lsl: case Mnemonic.Sldt: case Mnemonic.Movsx: case Mnemonic.Movzx:
            case Mnemonic.Shld: case Mnemonic.Shrd: case Mnemonic.Cpuid: case Mnemonic.Rdtsc:
            case Mnemonic.Verr: case Mnemonic.Verw: case Mnemonic.Ud0: case Mnemonic.Ud1: case Mnemonic.Ud2:
                return true;
        }
        string ms = insn.Mnemonic.ToString();
        return ms.StartsWith("Set") || ms.StartsWith("Cmov") || ms.StartsWith("Andn") || ms.StartsWith("Vfm");
    }

    private static bool Is8086Reg(Register r) =>
        (r >= Register.AX && r <= Register.DI) || (r >= Register.AL && r <= Register.BH)
        || r is Register.ES or Register.CS or Register.SS or Register.DS
        || (r >= Register.ST0 && r <= Register.ST7) || r == Register.None;

    private static bool Is8086AddrReg(Register r) =>
        r is Register.BX or Register.BP or Register.SI or Register.DI;

    private static string IndexRegName(Register r) => r switch
    {
        Register.BX => "c.BX", Register.SI => "c.SI", Register.DI => "c.DI", Register.BP => "c.BP",
        Register.AX => "c.AX", Register.CX => "c.CX", Register.DX => "c.DX",
        _ => "c.BX"
    };
}
