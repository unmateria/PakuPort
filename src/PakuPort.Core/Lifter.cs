using Iced.Intel;

namespace PakuPort.Core;

/// <summary>
/// Lifter: traduce cada instrucción x86-16 a C# con su semántica EXACTA del Intel SDM.
/// La aritmética/flags se delega en métodos del <see cref="Cpu"/> (centralizados y testeados vs
/// Unicorn). El control de flujo LOCAL se traduce a <c>goto L_addr</c>. Solo se implementan los
/// opcodes que el corpus usa de verdad; un opcode/operando no soportado <b>se reporta</b> en
/// <see cref="NotSupported"/> y se emite un trap — NUNCA se inventa (contrato anti-invención).
///
/// ModR/M 16-bit: segmento por defecto explícito ([BP]/[SP]→SS, resto→DS), honrando prefijo; el
/// offset efectivo envuelve a 16 bits.
/// </summary>
public sealed class Lifter
{
    public readonly SortedSet<string> OpcodesUsed = new(StringComparer.Ordinal);
    public readonly List<string> NotSupported = new();
    /// <summary>Instrucciones de plataforma modeladas como "sin efecto" (hipótesis nula gated por el
    /// juez): si la función sale VERDE, ese INT/in/out no afectaba al DGROUP verificable — probado,
    /// no supuesto. Una función verde que use stubs se marca como tal (honestidad).</summary>
    public readonly List<string> Stubbed = new();

    /// <summary>base de fichero del módulo de la función que se está liftando (para resolver la clave
    /// de un `call near`: claveDestino = CurrentBase + targetSegOffset). La fija el Emitter por función.</summary>
    public int CurrentBase = OverlayDirectory.ResidentBase;
    /// <summary>Tamaño en bytes del código del módulo actual. Si &gt; 0 y el target de un near call
    /// excede este tamaño, se busca en VroommStubTable o se emite FarDispatch.</summary>
    public int CurrentCodeSize;
    /// <summary>Tabla de stubs VROOMM: stub_IP → file key del destino. Resuelve near calls
    /// cross-overlay estáticamente (generada por el pipeline desde los metadatos del overlay).</summary>
    public Dictionary<int, int>? VroommStubTable;
    public (ushort Seg, ushort Off)? VroommStub;
    /// <summary>Labels (d.Offset) de la función actual — para near indirect jmp, genera switch local
    /// con goto a labels internos + fallback a FarDispatch.</summary>
    public IReadOnlySet<ushort>? CurrentFnLabels;
    /// <summary>Registro del idiom "ret manual" en la función actual, o Register.None. Patrón
    /// (descompresores con SP trucado, p.ej. el lector LZW de un juego de prueba y sus drivers): la función
    /// empieza con <c>pop R</c> (consume la dirección de retorno) y sale con <c>jmp R</c> sin
    /// redefinir R por el camino. Ese <c>jmp R</c> ES el retorno al llamador: se emite
    /// <c>return;</c>. El fallback (FarDispatch dinámico) hace MISS con SP+=4 espurio que
    /// descarrila la pila/stream del llamador. Lo calcula el Emitter con DetectManualRetReg.</summary>
    public Register ManualRetReg = Register.None;
    /// <summary>CS offset donde carga el código overlay (VROOMM). Los near call desde overlay tienen
    /// target IP decodificado sin esta base; real_IP = (decoded_tgt + OverlayLoadBase) &amp; 0xFFFF.</summary>
    public ushort OverlayLoadBase;
    /// <summary>Funciones que usan inline params (patrón Borland: <c>les bx,[bp+2]; add [bp+2],N</c>).
    /// Clave = file key de la función, valor = bytes a saltar tras el CALL (los datos inline).</summary>
    public Dictionary<int, int>? InlineParamFunctions;
    /// <summary>Targets pre-computados para calls a funciones con inline params en la función actual.
    /// Clave = offset del call site, valor = offset de la primera instrucción real tras los datos inline.</summary>
    public Dictionary<ushort, ushort>? InlineSkipTargets;
    /// <summary>Tail-call targets para call sites donde el skip target está en otra función.
    /// Clave = offset del call site, valor = file key de la función a llamar (tail-call + return).</summary>
    public Dictionary<ushort, int>? InlineTailCallTargets;

    public bool TryLift(DecodedInsn d, out string code)
    {
        var insn = d.Iced;
        OpcodesUsed.Add(d.Mnemonic);
        switch (insn.Mnemonic)
        {
            // --- transferencia de datos ---
            case Mnemonic.Mov:  code = LiftMov(insn); break;
            case Mnemonic.Xchg: code = LiftXchg(insn); break;
            case Mnemonic.Lea:  code = LiftLea(insn); break;
            case Mnemonic.Push: code = LiftPush(insn); break;
            case Mnemonic.Pop:  code = LiftPop(insn); break;
            case Mnemonic.Pushf: code = "c.SP -= 2; m.WrU16(c.SS, c.SP, c.FlagsWord());"; break;
            case Mnemonic.Popf:  code = "c.SetFlagsWord(m.RdU16(c.SS, c.SP)); c.SP += 2;"; break;
            case Mnemonic.Xlatb:
            {
                // AL = [seg:BX + AL] (tabla de traducción; seg = DS por defecto, override posible)
                string xseg = insn.SegmentPrefix != Register.None ? Reg(insn.SegmentPrefix) : "c.DS";
                code = $"c.AL = m.RdU8({xseg}, (ushort)(c.BX + c.AL));";
                break;
            }
            case Mnemonic.Cbw: code = "c.AX = (ushort)(short)(sbyte)c.AL;"; break;
            case Mnemonic.Cwd: code = "c.DX = (ushort)(((c.AX & 0x8000) != 0) ? 0xFFFF : 0);"; break;
            case Mnemonic.Lahf: code = "c.AH = (byte)c.FlagsWord();"; break;
            case Mnemonic.Sahf: code = "c.Sahf();"; break;
            case Mnemonic.Salc: code = "c.AL = (byte)(c.CF ? 0xFF : 0x00);"; break;
            case Mnemonic.Daa: code = "c.Daa();"; break;
            case Mnemonic.Das: code = "c.Das();"; break;
            case Mnemonic.Aaa: code = "c.Aaa();"; break;
            case Mnemonic.Aas: code = "c.Aas();"; break;
            case Mnemonic.Aam: code = $"c.Aam(0x{insn.Immediate8:X2});"; break;
            case Mnemonic.Aad: code = $"c.Aad(0x{insn.Immediate8:X2});"; break;
            case Mnemonic.Lds: code = LiftLoadFar(insn, "c.DS"); break;   // reg=[m]; DS=[m+2]
            case Mnemonic.Les: code = LiftLoadFar(insn, "c.ES"); break;   // reg=[m]; ES=[m+2]

            // --- aritmética binaria (RMW con flags) ---
            case Mnemonic.Add: code = LiftBin(insn, "Add"); break;
            case Mnemonic.Adc: code = LiftBin(insn, "Adc"); break;
            case Mnemonic.Sub: code = LiftBin(insn, "Sub"); break;
            case Mnemonic.Sbb: code = LiftBin(insn, "Sbb"); break;
            case Mnemonic.And: code = LiftBin(insn, "And"); break;
            case Mnemonic.Or:  code = LiftBin(insn, "Or"); break;
            case Mnemonic.Xor: code = LiftBin(insn, "Xor"); break;
            case Mnemonic.Cmp: code = LiftCmpTest(insn, "Cmp"); break;
            case Mnemonic.Test: code = LiftCmpTest(insn, "Test"); break;

            // --- unarias (RMW) ---
            case Mnemonic.Inc: code = LiftUnary(insn, "Inc"); break;
            case Mnemonic.Dec: code = LiftUnary(insn, "Dec"); break;
            case Mnemonic.Neg: code = LiftUnary(insn, "Neg"); break;
            case Mnemonic.Not: code = LiftNot(insn); break;

            // --- desplazamientos ---
            case Mnemonic.Shl:
            case Mnemonic.Sal: code = LiftShift(insn, "Shl"); break;
            case Mnemonic.Shr: code = LiftShift(insn, "Shr"); break;
            case Mnemonic.Sar: code = LiftShift(insn, "Sar"); break;
            case Mnemonic.Rol: code = LiftShift(insn, "Rol"); break;
            case Mnemonic.Ror: code = LiftShift(insn, "Ror"); break;
            case Mnemonic.Rcl: code = LiftShift(insn, "Rcl"); break;
            case Mnemonic.Rcr: code = LiftShift(insn, "Rcr"); break;

            // --- multiplicación / división (operando único; imul también 2/3 operandos) ---
            case Mnemonic.Mul:  code = LiftMulDiv(insn, "Mul"); break;
            case Mnemonic.Div:  code = LiftMulDiv(insn, "Div"); break;
            case Mnemonic.Idiv: code = LiftMulDiv(insn, "Idiv"); break;
            case Mnemonic.Imul: code = LiftImul(insn); break;

            // --- instrucciones de cadena (+ REP/REPE/REPNE) ---
            case Mnemonic.Movsb: code = LiftString(insn, 8,  "movs"); break;
            case Mnemonic.Movsw: code = LiftString(insn, 16, "movs"); break;
            case Mnemonic.Stosb: code = LiftString(insn, 8,  "stos"); break;
            case Mnemonic.Stosw: code = LiftString(insn, 16, "stos"); break;
            case Mnemonic.Lodsb: code = LiftString(insn, 8,  "lods"); break;
            case Mnemonic.Lodsw: code = LiftString(insn, 16, "lods"); break;
            case Mnemonic.Scasb: code = LiftString(insn, 8,  "scas"); break;
            case Mnemonic.Scasw: code = LiftString(insn, 16, "scas"); break;
            case Mnemonic.Cmpsb: code = LiftString(insn, 8,  "cmps"); break;
            case Mnemonic.Cmpsw: code = LiftString(insn, 16, "cmps"); break;

            // --- control de flujo local ---
            case Mnemonic.Jmp:
                code = d.JumpTable != null ? LiftJumpTable(d) : LiftJmp(insn);
                break;
            case Mnemonic.Loop:   code = LiftLoop(insn, "(c.CX != 0)"); break;
            case Mnemonic.Loope:  code = LiftLoop(insn, "(c.CX != 0 && c.ZF)"); break;
            case Mnemonic.Loopne: code = LiftLoop(insn, "(c.CX != 0 && !c.ZF)"); break;
            case Mnemonic.Call:   code = LiftCall(d); break;
            case Mnemonic.Int:
            {
                int n = insn.GetOpKind(0) == OpKind.Immediate8 ? insn.Immediate8 : -1;
                if (n == 0x3F)
                {
                    // INT 3F = thunk de overlay VROOMM (resuelto estáticamente). Dispatch al método del
                    // overlay: empuja CS:IP de retorno (el callee retf equilibra) y llama. El dibujo del
                    // overlay va a VRAM (fuera del DGROUP) → el juez lo ignora ("el vídeo sale gratis").
                    if (d.OverlayTargetKey >= 0)
                    {
                        ushort ret = (ushort)(d.Offset + d.Length);
                        // CS induction (clase F4): far-style site — restore the pushed CS on return.
                        // MINIMAL guard (root of the [NREDIR] residuals, retuned after the manual-playtest
                        // regression): consume c.FarRet ONLY when the popped pair is BY VALUE this
                        // site's own (the normal overlay return — the residual that poisoned downstream
                        // guards: getvect(1B) re-ran continuation 1092 → R6003 at boot). Every other
                        // case keeps the LEGACY behaviour byte-exact (flag stays set, control falls
                        // through inline): a full CallGuardRedir here PROPAGATED unwinds the old core
                        // let fall through, and a manual playtest lost in-game lists / crashed the AdLib boot.
                        code = $"{{ ushort _fcs = c.CS; c.SP -= 2; m.WrU16(c.SS, c.SP, c.CS); c.SP -= 2; m.WrU16(c.SS, c.SP, 0x{ret:X4}); Fn_{d.OverlayTargetKey:X5}(c, m);" +
                               $" if (c.FarRet && c.FarRetIp == 0x{ret:X4} && c.FarRetCs == _fcs) c.FarRet = false; c.CS = _fcs; }}";
                    }
                    else
                    {
                        string why = "int 0x3F a overlay fuera del directorio";
                        NotSupported.Add(why);
                        code = $"throw new System.NotSupportedException(\"{Escape(why)}\"); /*UNSUPPORTED*/";
                    }
                }
                else
                {
                    Stubbed.Add($"int 0x{n:X2}");
                    code = $"c.Platform?.Int(0x{n:X2}, c, m); // int 0x{n:X2}";
                }
                break;
            }
            case Mnemonic.Out: Stubbed.Add("out"); code = LiftOut(insn); break;
            case Mnemonic.In:  Stubbed.Add("in");  code = LiftIn(insn); break;
            case Mnemonic.Ret:  code = LiftRet(insn, 2); break;   // near: saca la dir. de retorno
            case Mnemonic.Retf: code = LiftRet(insn, 4); break;   // far: saca cs:ip

            // --- control de flags ---
            case Mnemonic.Clc: code = "c.CF = false;"; break;
            case Mnemonic.Stc: code = "c.CF = true;"; break;
            case Mnemonic.Cmc: code = "c.CF = !c.CF;"; break;
            case Mnemonic.Cld: code = "c.DF = false;"; break;
            case Mnemonic.Std: code = "c.DF = true;"; break;
            case Mnemonic.Cli: code = "c.IF = false;"; break;
            case Mnemonic.Sti: code = "c.IF = true;"; break;
            case Mnemonic.Nop: code = ";"; break;
            case Mnemonic.Hlt: code = "return; // hlt"; break;
            case Mnemonic.Int1: code = "; // int1 (icebp/debug)"; break;
            case Mnemonic.Into: code = "if (c.OF) c.Platform?.Int(0x04, c, m); // into"; break;
            case Mnemonic.Iret: code = "{ c.Iret(m); return; }"; break;
            case Mnemonic.Enter:
            {
                int frameSize = insn.Immediate16;
                int nestLevel = insn.Immediate8_2nd & 0x1F;
                if (nestLevel == 0)
                    code = $"{{ c.SP -= 2; m.WrU16(c.SS, c.SP, c.BP); c.BP = c.SP; c.SP -= {frameSize}; }}";
                else
                    code = $"{{ c.SP -= 2; m.WrU16(c.SS, c.SP, c.BP); ushort _fp = c.SP; for (int _i = 1; _i < {nestLevel}; _i++) {{ c.BP -= 2; c.SP -= 2; m.WrU16(c.SS, c.SP, m.RdU16(c.SS, c.BP)); }} c.SP -= 2; m.WrU16(c.SS, c.SP, _fp); c.BP = _fp; c.SP -= {frameSize}; }}";
                break;
            }
            case Mnemonic.Leave: code = "{ c.SP = c.BP; c.BP = m.RdU16(c.SS, c.SP); c.SP += 2; }"; break;
            case Mnemonic.Insb:  Stubbed.Add("insb");  code = "; // insb (puerto → ES:DI)"; break;
            case Mnemonic.Insw:  Stubbed.Add("insw");  code = "; // insw (puerto → ES:DI)"; break;
            case Mnemonic.Outsb: Stubbed.Add("outsb"); code = "; // outsb (DS:SI → puerto)"; break;
            case Mnemonic.Outsw: Stubbed.Add("outsw"); code = "; // outsw (DS:SI → puerto)"; break;
            case Mnemonic.Pusha: code = "{ ushort _t = c.SP; c.SP -= 2; m.WrU16(c.SS, c.SP, c.AX); c.SP -= 2; m.WrU16(c.SS, c.SP, c.CX); c.SP -= 2; m.WrU16(c.SS, c.SP, c.DX); c.SP -= 2; m.WrU16(c.SS, c.SP, c.BX); c.SP -= 2; m.WrU16(c.SS, c.SP, _t); c.SP -= 2; m.WrU16(c.SS, c.SP, c.BP); c.SP -= 2; m.WrU16(c.SS, c.SP, c.SI); c.SP -= 2; m.WrU16(c.SS, c.SP, c.DI); }"; break;
            case Mnemonic.Popa: code = "{ c.DI = m.RdU16(c.SS, c.SP); c.SP += 2; c.SI = m.RdU16(c.SS, c.SP); c.SP += 2; c.BP = m.RdU16(c.SS, c.SP); c.SP += 2; c.SP += 2; c.BX = m.RdU16(c.SS, c.SP); c.SP += 2; c.DX = m.RdU16(c.SS, c.SP); c.SP += 2; c.CX = m.RdU16(c.SS, c.SP); c.SP += 2; c.AX = m.RdU16(c.SS, c.SP); c.SP += 2; }"; break;
            case Mnemonic.Bound: code = "/* bound: no-op (INT 5 en rango incorrecto, raro en práctica) */"; break;

            // --- 80386 real-mode extensions (Borland/Turbo C 3.x+) ---
            case Mnemonic.Movzx: code = LiftMovExtend(insn, zeroExtend: true); break;
            case Mnemonic.Movsx: code = LiftMovExtend(insn, zeroExtend: false); break;
            case Mnemonic.Shld: code = LiftDoubleShift(insn, "Shld"); break;
            case Mnemonic.Shrd: code = LiftDoubleShift(insn, "Shrd"); break;
            case Mnemonic.Bt:  code = LiftBitTest(insn, "Bt"); break;
            case Mnemonic.Bts: code = LiftBitTest(insn, "Bts"); break;
            case Mnemonic.Btr: code = LiftBitTest(insn, "Btr"); break;
            case Mnemonic.Btc: code = LiftBitTest(insn, "Btc"); break;
            case Mnemonic.Bsf: code = LiftBitScan(insn, forward: true); break;
            case Mnemonic.Bsr: code = LiftBitScan(insn, forward: false); break;
            case Mnemonic.Cwde: code = "c.EAX = (uint)(int)(short)c.AX;"; break;
            case Mnemonic.Cdq:  code = "c.EDX = (uint)(((c.EAX & 0x80000000u) != 0) ? 0xFFFFFFFF : 0);"; break;
            case Mnemonic.Pushad: code = "{ uint _t = c.ESP; c.ESP -= 4; m.WrU32(c.SS, (ushort)c.ESP, c.EAX); c.ESP -= 4; m.WrU32(c.SS, (ushort)c.ESP, c.ECX); c.ESP -= 4; m.WrU32(c.SS, (ushort)c.ESP, c.EDX); c.ESP -= 4; m.WrU32(c.SS, (ushort)c.ESP, c.EBX); c.ESP -= 4; m.WrU32(c.SS, (ushort)c.ESP, _t); c.ESP -= 4; m.WrU32(c.SS, (ushort)c.ESP, c.EBP); c.ESP -= 4; m.WrU32(c.SS, (ushort)c.ESP, c.ESI); c.ESP -= 4; m.WrU32(c.SS, (ushort)c.ESP, c.EDI); }"; break;
            case Mnemonic.Popad: code = "{ c.EDI = m.RdU32(c.SS, (ushort)c.ESP); c.ESP += 4; c.ESI = m.RdU32(c.SS, (ushort)c.ESP); c.ESP += 4; c.EBP = m.RdU32(c.SS, (ushort)c.ESP); c.ESP += 4; c.ESP += 4; c.EBX = m.RdU32(c.SS, (ushort)c.ESP); c.ESP += 4; c.EDX = m.RdU32(c.SS, (ushort)c.ESP); c.ESP += 4; c.ECX = m.RdU32(c.SS, (ushort)c.ESP); c.ESP += 4; c.EAX = m.RdU32(c.SS, (ushort)c.ESP); c.ESP += 4; }"; break;
            case Mnemonic.Pushfd: code = "c.SP -= 4; m.WrU32(c.SS, c.SP, c.EFlagsWord());"; break;
            case Mnemonic.Popfd:  code = "c.SetEFlagsWord(m.RdU32(c.SS, c.SP)); c.SP += 4;"; break;

            // 32-bit string ops
            case Mnemonic.Movsd: code = LiftString(insn, 32, "movs"); break;
            case Mnemonic.Stosd: code = LiftString(insn, 32, "stos"); break;
            case Mnemonic.Lodsd: code = LiftString(insn, 32, "lods"); break;
            case Mnemonic.Scasd: code = LiftString(insn, 32, "scas"); break;
            case Mnemonic.Cmpsd: code = LiftString(insn, 32, "cmps"); break;

            // SETcc (set byte on condition)
            case Mnemonic.Seta:  code = LiftSetcc(insn, "(!c.CF && !c.ZF)"); break;
            case Mnemonic.Setae: code = LiftSetcc(insn, "!c.CF"); break;
            case Mnemonic.Setb:  code = LiftSetcc(insn, "c.CF"); break;
            case Mnemonic.Setbe: code = LiftSetcc(insn, "(c.CF || c.ZF)"); break;
            case Mnemonic.Sete:  code = LiftSetcc(insn, "c.ZF"); break;
            case Mnemonic.Setne: code = LiftSetcc(insn, "!c.ZF"); break;
            case Mnemonic.Setg:  code = LiftSetcc(insn, "(!c.ZF && (c.SF == c.OF))"); break;
            case Mnemonic.Setge: code = LiftSetcc(insn, "(c.SF == c.OF)"); break;
            case Mnemonic.Setl:  code = LiftSetcc(insn, "(c.SF != c.OF)"); break;
            case Mnemonic.Setle: code = LiftSetcc(insn, "(c.ZF || (c.SF != c.OF))"); break;
            case Mnemonic.Sets:  code = LiftSetcc(insn, "c.SF"); break;
            case Mnemonic.Setns: code = LiftSetcc(insn, "!c.SF"); break;
            case Mnemonic.Seto:  code = LiftSetcc(insn, "c.OF"); break;
            case Mnemonic.Setno: code = LiftSetcc(insn, "!c.OF"); break;
            case Mnemonic.Setp:  code = LiftSetcc(insn, "c.PF"); break;
            case Mnemonic.Setnp: code = LiftSetcc(insn, "!c.PF"); break;

            // CMOVcc (conditional move — 686/P6+, pero algunos compiladores lo emiten)
            case Mnemonic.Cmova:  code = LiftCmovcc(insn, "(!c.CF && !c.ZF)"); break;
            case Mnemonic.Cmovae: code = LiftCmovcc(insn, "!c.CF"); break;
            case Mnemonic.Cmovb:  code = LiftCmovcc(insn, "c.CF"); break;
            case Mnemonic.Cmovbe: code = LiftCmovcc(insn, "(c.CF || c.ZF)"); break;
            case Mnemonic.Cmove:  code = LiftCmovcc(insn, "c.ZF"); break;
            case Mnemonic.Cmovne: code = LiftCmovcc(insn, "!c.ZF"); break;
            case Mnemonic.Cmovg:  code = LiftCmovcc(insn, "(!c.ZF && (c.SF == c.OF))"); break;
            case Mnemonic.Cmovge: code = LiftCmovcc(insn, "(c.SF == c.OF)"); break;
            case Mnemonic.Cmovl:  code = LiftCmovcc(insn, "(c.SF != c.OF)"); break;
            case Mnemonic.Cmovle: code = LiftCmovcc(insn, "(c.ZF || (c.SF != c.OF))"); break;
            case Mnemonic.Cmovs:  code = LiftCmovcc(insn, "c.SF"); break;
            case Mnemonic.Cmovns: code = LiftCmovcc(insn, "!c.SF"); break;
            case Mnemonic.Cmovo:  code = LiftCmovcc(insn, "c.OF"); break;
            case Mnemonic.Cmovno: code = LiftCmovcc(insn, "!c.OF"); break;
            case Mnemonic.Cmovp:  code = LiftCmovcc(insn, "c.PF"); break;
            case Mnemonic.Cmovnp: code = LiftCmovcc(insn, "!c.PF"); break;

            // LFS/LGS (386 — load far pointer)
            case Mnemonic.Lfs: code = LiftLoadFar(insn, "c.FS"); break;
            case Mnemonic.Lgs: code = LiftLoadFar(insn, "c.GS"); break;

            // INT3 (breakpoint — no-op en transpiled code)
            case Mnemonic.Int3: code = "; // int3 (breakpoint)"; break;

            default:
                // saltos condicionales (todos comparten forma): if (cond) goto L_target;
                if (TryCond(insn.Mnemonic, out string cond))
                {
                    code = LiftJcc(insn, cond);
                    break;
                }
                // x87 (coma flotante): se modela con una FPU software (double). Ver LiftFpu.
                if (d.Mnemonic.Length > 0 && (d.Mnemonic[0] == 'f' || d.Mnemonic == "wait"))
                {
                    code = LiftFpu(d.Iced);
                    return !code.Contains("/*UNSUPPORTED");
                }
                return Unsupported(d, out code);
        }
        return !code.Contains("/*UNSUPPORTED");
    }

    private bool Unsupported(DecodedInsn d, out string code)
    {
        string reason = $"opcode no soportado: {d.Mnemonic} @0x{d.Offset:X4} ({d.Text})";
        NotSupported.Add(reason);
        code = $"throw new System.NotSupportedException(\"{Escape(reason)}\"); /*UNSUPPORTED*/";
        return false;
    }

    // ================= transferencia de datos =================

    private string LiftMov(in Instruction insn)
    {
        int size = OperandSize(insn, 0);
        string src = Src(insn, 1, size);
        var (prep, _, wf) = Dst(insn, 0, size);
        return Wrap(prep, string.Format(wf, src));
    }

    private string LiftXchg(in Instruction insn)
    {
        int size = OperandSize(insn, 0);
        string cast = size == 16 ? "ushort" : "byte";
        var (p0, r0, w0) = Dst(insn, 0, size);
        var (p1, r1, w1) = Dst(insn, 1, size);
        // lee ambos antes de escribir
        return "{ " + p0 + p1 + $"{cast} _t0 = {r0}; {cast} _t1 = {r1}; " +
               string.Format(w0, "_t1") + " " + string.Format(w1, "_t0") + " }";
    }

    // lds/les: carga puntero far de memoria. reg = [m]; segReg = [m+2]. El segmento far guardado en
    // el DGROUP del corpus YA es runtime (relocado): leerlo da el valor correcto. Si apunta dentro
    // del DGROUP, el juez lo verifica; si apunta a memoria far ausente del snapshot, sale rojo (no
    // falso verde) y se reporta como muro (datos far fuera de la ventana de 64 KB).
    private string LiftLoadFar(in Instruction insn, string segReg)
    {
        if (insn.GetOpKind(0) != OpKind.Register) return UnsupportedExpr(insn, "lds/les destino no-reg") + ";";
        (string seg, string ea) = MemSegEa(insn);
        string r = Reg(insn.Op0Register);
        return $"{{ ushort _ea = {ea}; {r} = m.RdU16({seg}, _ea); {segReg} = m.RdU16({seg}, (ushort)(_ea + 2)); }}";
    }

    private string LiftLea(in Instruction insn)
    {
        if (insn.GetOpKind(0) != OpKind.Register) return UnsupportedExpr(insn, "lea destino no-reg") + ";";
        (string _, string ea) = MemSegEa(insn);
        return $"{Reg(insn.Op0Register)} = {ea};";
    }

    private string LiftPush(in Instruction insn)
    {
        // The source is read BEFORE SP moves, so `push sp` pushes the old SP — 286-and-later
        // semantics, mirroring Interp. Only `push sp` can tell the difference, and it is the
        // second step of the canonical CPU-detection probe (see Interp's Mnemonic.Push).
        string val = Src(insn, 0, 16);
        if (insn.Op0Kind == OpKind.Register && insn.Op0Register == Register.SP)
            return $"{{ ushort _v = {val}; c.SP -= 2; m.WrU16(c.SS, c.SP, _v); }}";
        return $"c.SP -= 2; m.WrU16(c.SS, c.SP, {val});";
    }

    private string LiftPop(in Instruction insn)
    {
        int size = 16;
        var (prep, _, wf) = Dst(insn, 0, size);
        return Wrap(prep, string.Format(wf, "m.RdU16(c.SS, c.SP)")) + " c.SP += 2;";
    }

    // ================= aritmética =================

    private string LiftBin(in Instruction insn, string op)
    {
        int size = OperandSize(insn, 0);
        string src = Src(insn, 1, size);
        var (prep, read, wf) = Dst(insn, 0, size);
        string val = $"c.{op}{size}({read}, {src})";
        return Wrap(prep, string.Format(wf, val));
    }

    private string LiftCmpTest(in Instruction insn, string op)
    {
        int size = OperandSize(insn, 0);
        string a = Src(insn, 0, size);
        string b = Src(insn, 1, size);
        return $"c.{op}{size}({a}, {b});";
    }

    private string LiftUnary(in Instruction insn, string op)
    {
        int size = OperandSize(insn, 0);
        var (prep, read, wf) = Dst(insn, 0, size);
        return Wrap(prep, string.Format(wf, $"c.{op}{size}({read})"));
    }

    private string LiftNot(in Instruction insn)
    {
        int size = OperandSize(insn, 0);
        var (prep, read, wf) = Dst(insn, 0, size);
        return Wrap(prep, string.Format(wf, $"Cpu.Not{size}({read})"));
    }

    private string LiftShift(in Instruction insn, string op)
    {
        int size = OperandSize(insn, 0);
        string cnt = insn.GetOpKind(1) switch
        {
            OpKind.Immediate8 => $"0x{insn.Immediate8:X2}",
            OpKind.Register when insn.Op1Register == Register.CL => "c.CL",
            _ => "?"
        };
        if (cnt == "?") return UnsupportedExpr(insn, "shift cuenta no imm8/CL") + ";";
        var (prep, read, wf) = Dst(insn, 0, size);
        return Wrap(prep, string.Format(wf, $"c.{op}{size}({read}, {cnt})"));
    }

    // mul/div/idiv de operando único: leen AX (y DX en 16-bit) implícitamente.
    private string LiftMulDiv(in Instruction insn, string op)
    {
        int size = OperandSize(insn, 0);
        string call = $"c.{op}{size}({Src(insn, 0, size)});";
        // #DE parity with the interpreter (Interp.cs case Div/Idiv): a divide fault must
        // vector through INT 0 (8086: resume at the next instruction), never surface as a
        // raw exception that kills the host thread (a test game's __ldiv; gate suite DE). Both #DE
        // flavours: zero divisor (DivideByZeroException) and quotient overflow
        // (OverflowException, another test game's boot) — ArithmeticException is their common base.
        if (op is "Div" or "Idiv")
            return $"try {{ {call} }} catch (System.ArithmeticException) {{ c.Platform?.Int(0x00, c, m); }}";
        return call;
    }

    private string LiftImul(in Instruction insn)
    {
        switch (insn.OpCount)
        {
            case 1:
                int size = OperandSize(insn, 0);
                return $"c.Imul{size}({Src(insn, 0, size)});";
            case 2:
                if (insn.GetOpKind(0) != OpKind.Register) return UnsupportedExpr(insn, "imul 2-op destino no-reg") + ";";
                return $"{Reg(insn.Op0Register)} = c.Imul16Two({Reg(insn.Op0Register)}, {Src(insn, 1, 16)});";
            case 3:
                if (insn.GetOpKind(0) != OpKind.Register) return UnsupportedExpr(insn, "imul 3-op destino no-reg") + ";";
                return $"{Reg(insn.Op0Register)} = c.Imul16Two({Src(insn, 1, 16)}, {Src(insn, 2, 16)});";
            default:
                return UnsupportedExpr(insn, "imul forma desconocida") + ";";
        }
    }

    // Instrucciones de cadena. Origen DS:SI (override posible), destino ES:DI; avanzan
    // SI/DI según DF (±tamaño). REP repite CX veces; REPE/REPNE (scas/cmps) terminan también por ZF.
    private string LiftString(in Instruction insn, int size, string kind)
    {
        int n = size / 8;
        string ss = insn.SegmentPrefix != Register.None ? Reg(insn.SegmentPrefix) : "c.DS";
        string rdS = size == 32 ? $"m.RdU32({ss}, c.SI)" : size == 16 ? $"m.RdU16({ss}, c.SI)" : $"m.RdU8({ss}, c.SI)";
        string aSI = $"c.SI = (ushort)(c.SI + (c.DF ? -{n} : {n}));";
        string aDI = $"c.DI = (ushort)(c.DI + (c.DF ? -{n} : {n}));";
        string accR = size == 32 ? "c.EAX" : size == 16 ? "c.AX" : "c.AL";

        string body, advance;
        switch (kind)
        {
            case "movs":
                body = size == 32 ? $"m.WrU32(c.ES, c.DI, {rdS});" :
                       size == 16 ? $"m.WrU16(c.ES, c.DI, {rdS});" : $"m.WrU8(c.ES, c.DI, {rdS});";
                advance = aSI + " " + aDI; break;
            case "stos":
                body = size == 32 ? $"m.WrU32(c.ES, c.DI, {accR});" :
                       size == 16 ? $"m.WrU16(c.ES, c.DI, {accR});" : $"m.WrU8(c.ES, c.DI, {accR});";
                advance = aDI; break;
            case "lods":
                body = $"{accR} = {rdS};";
                advance = aSI; break;
            case "scas":
                body = size == 32 ? $"c.Cmp32({accR}, m.RdU32(c.ES, c.DI));" :
                       size == 16 ? "c.Cmp16(c.AX, m.RdU16(c.ES, c.DI));" : "c.Cmp8(c.AL, m.RdU8(c.ES, c.DI));";
                advance = aDI; break;
            case "cmps":
                body = size == 32 ? $"c.Cmp32({rdS}, m.RdU32(c.ES, c.DI));" :
                       size == 16 ? $"c.Cmp16({rdS}, m.RdU16(c.ES, c.DI));" : $"c.Cmp8({rdS}, m.RdU8(c.ES, c.DI));";
                advance = aSI + " " + aDI; break;
            default: return UnsupportedExpr(insn, "string op desconocida") + ";";
        }

        bool rep = insn.HasRepPrefix || insn.HasRepePrefix;
        bool repne = insn.HasRepnePrefix;
        if (!rep && !repne) return body + " " + advance;

        if (kind is "scas" or "cmps")
        {
            // repe (F3): sigue mientras ZF; repne (F2): mientras !ZF. Romper tras fijar ZF.
            string brk = repne ? "c.ZF" : "!c.ZF";
            return $"while (c.CX != 0) {{ c.CX = (ushort)(c.CX - 1); {body} {advance} if ({brk}) break; }}";
        }
        // rep movs/stos/lods: CX veces
        return $"while (c.CX != 0) {{ c.CX = (ushort)(c.CX - 1); {body} {advance} }}";
    }

    // ================= control de flujo =================

    // call near: empuja la dir. de retorno (para que [bp+N] del callee cuadre) e invoca su método.
    // El 'ret' del callee equilibra SP. call far / indirecto → Fase 5 (overlays).
    private string LiftCall(in DecodedInsn d)
    {
        var insn = d.Iced;
        // A far call-site with inline-params handling already models the post-call jump with its own
        // goto/tail-call (it assumes the retf is paired). Do NOT add co-routine redirection there —
        // keep the proven guard. The co-routine blitter that motivates the fix never uses inline params.
        bool hasInline = (InlineSkipTargets?.ContainsKey(d.Offset) ?? false)
                      || (InlineTailCallTargets?.ContainsKey(d.Offset) ?? false);
        if (insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
        {
            ushort tgt = (ushort)insn.NearBranchTarget;
            ushort ret = (ushort)(insn.IP + (ulong)insn.Length);
            // CS induction (clase F4): a near call leaves CS unchanged on real hardware, but a
            // dispatched callee (or its inner far calls) leaves c.CS at ITS segment — restore the
            // caller's CS after every call-site so far call-sites never push a stale segment.
            if (CurrentCodeSize > 0 && tgt >= CurrentCodeSize)
            {
                if (VroommStubTable != null && VroommStubTable.TryGetValue(tgt, out int resolved))
                    return $"{{ ushort _ncs = c.CS; c.SP -= 2; m.WrU16(c.SS, c.SP, 0x{ret:X4}); ushort _spc = c.SP; Fn_{resolved:X5}(c, m);{CallGuard(ret)} c.CS = _ncs; }}";
                ushort realIP = (ushort)(tgt + OverlayLoadBase);
                return $"{{ ushort _ncs = c.CS; c.SP -= 2; m.WrU16(c.SS, c.SP, 0x{ret:X4}); ushort _spc = c.SP; FarDispatch(c, m, c.CS, 0x{realIP:X4});{CallGuard(ret)} c.CS = _ncs; }}";
            }
            int targetKey = CurrentBase + tgt;
            string nearCall = $"{{ ushort _ncs = c.CS; c.SP -= 2; m.WrU16(c.SS, c.SP, 0x{ret:X4}); ushort _spc = c.SP; Fn_{targetKey:X5}(c, m);{CallGuard(ret)} c.CS = _ncs; }}";
            if (InlineSkipTargets != null && InlineSkipTargets.TryGetValue(d.Offset, out ushort nearSkipTgt))
                nearCall += $" goto L_{nearSkipTgt:X4};";
            else if (InlineTailCallTargets != null && InlineTailCallTargets.TryGetValue(d.Offset, out int nearTcKey))
                nearCall += $" Fn_{nearTcKey:X5}(c, m); return;";
            return nearCall;
        }
        // call far a FUNCIÓN RESIDENTE real (C-runtime): se DESPACHA (lift) para capturar su efecto
        // en el DGROUP — así verdean los rojos cuyo far-call SÍ afecta al estado verificable.
        if (d.FarCallTargetKey >= 0)
        {
            ushort ret = (ushort)(insn.IP + (ulong)insn.Length);
            // CS induction: a paired retf pops the pushed CS (= _fcs) — restore it in c.CS.
            string call = $"{{ ushort _fcs = c.CS; c.SP -= 2; m.WrU16(c.SS, c.SP, c.CS); c.SP -= 2; m.WrU16(c.SS, c.SP, 0x{ret:X4}); Fn_{d.FarCallTargetKey:X5}(c, m);{(hasInline ? CallGuardFar(ret) : CallGuardRedir(ret))} c.CS = _fcs; }}";
            if (InlineSkipTargets != null && InlineSkipTargets.TryGetValue(d.Offset, out ushort farSkipTgt))
                call += $" goto L_{farSkipTgt:X4};";
            else if (InlineTailCallTargets != null && InlineTailCallTargets.TryGetValue(d.Offset, out int farTcKey))
                call += $" Fn_{farTcKey:X5}(c, m); return;";
            return call;
        }
        // VROOMM reload check: call far al entry stub del overlay manager — NOP en port estático
        if (insn.Op0Kind == OpKind.FarBranch16 && VroommStub is var (vs, vo) &&
            insn.FarBranchSelector == vs && insn.FarBranch16 == vo)
            return "; // VROOMM reload check (NOP)";
        // call far directo (cualquier seg:off) — FarDispatch resuelve en runtime
        if (insn.Op0Kind == OpKind.FarBranch16)
        {
            ushort ret = (ushort)(insn.IP + (ulong)insn.Length);
            return $"{{ ushort _fcs = c.CS; c.SP -= 2; m.WrU16(c.SS, c.SP, c.CS); c.SP -= 2; m.WrU16(c.SS, c.SP, 0x{ret:X4}); " +
                   $"FarDispatch(c, m, 0x{insn.FarBranchSelector:X4}, 0x{insn.FarBranch16:X4});{CallGuardRedir(ret)} c.CS = _fcs; }}";
        }
        // call indirecto por memoria: near (word) vs far (dword/far pointer)
        if (insn.Op0Kind == OpKind.Memory)
        {
            (string seg, string ea) = MemSegEa(insn);
            ushort ret = (ushort)(insn.IP + (ulong)insn.Length);
            if (insn.MemorySize is MemorySize.UInt16 or MemorySize.WordOffset)
            {
                return $"{{ ushort _t = m.RdU16({seg}, (ushort)({ea})); ushort _ncs = c.CS; " +
                       $"c.SP -= 2; m.WrU16(c.SS, c.SP, 0x{ret:X4}); ushort _spc = c.SP; FarDispatch(c, m, c.CS, _t);{CallGuard(ret)} c.CS = _ncs; }}";
            }
            return $"{{ ushort _fOff = m.RdU16({seg}, (ushort)({ea})); ushort _fSeg = m.RdU16({seg}, (ushort)({ea} + 2)); " +
                   $"ushort _fcs = c.CS; c.SP -= 2; m.WrU16(c.SS, c.SP, c.CS); c.SP -= 2; m.WrU16(c.SS, c.SP, 0x{ret:X4}); " +
                   $"FarDispatch(c, m, _fSeg, _fOff);{CallGuardRedir(ret)} c.CS = _fcs; }}";
        }
        // call register (raro)
        if (insn.Op0Kind == OpKind.Register)
        {
            string r = Reg(insn.Op0Register);
            ushort ret = (ushort)(insn.IP + (ulong)insn.Length);
            return $"{{ ushort _ncs = c.CS; c.SP -= 2; m.WrU16(c.SS, c.SP, 0x{ret:X4}); ushort _spc = c.SP; FarDispatch(c, m, c.CS, {r});{CallGuard(ret)} c.CS = _ncs; }}";
        }
        Stubbed.Add("call far/indirecto");
        return "; // call far/indirecto: forma no reconocida";
    }

    // ret near saca 2 bytes (dir. retorno); 'ret N' saca 2+N; retf saca 4. __cdecl16near = ret simple.
    // retf además enciende c.FarRet: el marco desapilado puede ser el de un llamador exterior
    // (escape por epílogo compartido); los call-sites lo comprueban con CallGuard.
    private string LiftRet(in Instruction insn, int baseBytes)
    {
        int extra = 0;
        if (insn.OpCount >= 1 && insn.GetOpKind(0) is OpKind.Immediate16 or OpKind.Immediate8 or OpKind.Immediate8to16)
            extra = insn.Immediate16;
        // El ajuste de SP envuelve a 16 bits, como en el 8086: `ret 0FFFFh` da 4+65535 = 65539,
        // que en la máquina real es 3. Sin la máscara se emitía la constante cruda y el export
        // NI SIQUIERA COMPILABA (CS0031: no se puede convertir en 'ushort'). Se llevó por delante
        // el port entero de un juego de prueba, y es un fallo de compilación, no de
        // ejecución: no lo caza ningún gate de runtime.
        int spAdj = (baseBytes + extra) & 0xFFFF;
        // retf: capture the popped destination BEFORE adjusting SP (telemetry for the
        // FarRet-unwind guards; discarded destinations land in gaps.log — TAREA B).
        if (baseBytes == 4)
            return $"c.FarRetIp = m.RdU16(c.SS, c.SP); c.FarRetCs = m.RdU16(c.SS, (ushort)(c.SP + 2)); c.FarRetSs = c.SS; c.FarRetSp = c.SP; c.SP += {spAdj}; c.FarRet = true; return;";
        return $"c.SP += {spAdj}; return;";
    }

    // Guard post-llamada (semántica x86 exacta del retf): si el callee salió por retf y SP quedó
    // por encima del nivel de entrada de ESTA función, su marco fue desapilado por el callee →
    // propagar el return. Si SP no superó el nivel de entrada (retf normal de un far-callee cuyo
    // marco empujó este mismo call-site, o idiom push cs;call near), apagar el flag y seguir.
    // TAREA B (telemetría, cero cambio de flujo): tanto al propagar como al REANUDAR inline con
    // un destino popeado distinto de la continuación de este call-site (0x{ret}), registrar el
    // destino descartado (clase co-rutina del Frente 1) — aparece en logs/gaps.log del host.
    // NEAR call-site guard. The PROPAGATE branch must NOT redirect (at a deep escape the popped
    // "pair" is often not a pair — the cell above a near return is frequently ANOTHER same-segment
    // return address, so CS==CS passes by accident; verified empirically: boot died to DOS).
    // The CONSUMED branch (STOP + mismatch) redirects (5º intento) now that NCORC killed the
    // residual-flag source (the earlier veto: both [NREDIR] counterexamples were residuals,
    // indistinguishable at this guard). Filters: (a) the destination must also differ from the
    // LIVE return cell (_spc — an inline-params callee ADJUSTS that cell and the lifted goto
    // already resumes there, gate NCORB), and (b) the popped CS must equal the CALL-SITE's CS
    // (_ncs, NOT the stale running c.CS — measured). A genuine hand-pushed co-routine
    // continuation (a test game's city/name/menu draw passes die exactly here, gate NCORA) resumes in
    // the interp like the far-site redirects (a co-routine loop must not recurse C# frames).
    private static string CallGuard(ushort ret) =>
        $" if (c.FarRet) {{ if ((short)(c.SP - _spEntry) > 0) {{ c.NoteFarRetUnwind(); return; }} c.FarRet = false;" +
        $" if (c.FarRetIp != 0x{ret:X4}) {{ if (Interp.Enabled && c.FarRetIp != m.RdU16(c.SS, _spc)" +
        $" && c.FarRetSs == c.SS && c.FarRetSp == (ushort)(_spc + 2) && c.ShouldFarRetRedirectTo(_ncs))" +
        $" {{ if (Cpu.GapTrace) System.Console.Error.WriteLine($\"[NREDIR] site={{_ncs:X4}}:{ret:X4} cell={{m.RdU16(c.SS, _spc):X4}} spdelta={{(short)(c.SP - _spc)}} -> {{c.FarRetCs:X4}}:{{c.FarRetIp:X4}}\");" +
        $" if (Interp.Run(c, m, c.FarRetCs, c.FarRetIp, CallKey, FarDispatch)) c.FarRet = true; return; }} c.NoteFarRetUnwind(); }} }}";

    // FAR call-site guard for inline-params sites. These DO push CS (FarRetCs reliable), so the
    // intra-segment co-routine redirect applies in BOTH branches (a test game's city/name draw pass was
    // eaten here: Noted + resumed at the inline jump instead of the hand-pushed continuation).
    private static string CallGuardFar(ushort ret) =>
        $" if (c.FarRet) {{ if ((short)(c.SP - _spEntry) > 0) {{" +
        $" if (c.FarRetIp == 0x{ret:X4} && c.FarRetCs == _fcs) {{ c.FarRet = false; c.CS = _fcs; }}" +
        $" else if (Interp.Enabled && c.ShouldFarRetRedirect()) {{ c.FarRet = false; if (Interp.Run(c, m, c.FarRetCs, c.FarRetIp, CallKey, FarDispatch)) c.FarRet = true; return; }}" +
        $" else {{ c.NoteFarRetUnwind(); return; }} }}" +
        $" else {{ c.FarRet = false; if (c.FarRetIp != 0x{ret:X4}) {{ if (Interp.Enabled && c.ShouldFarRetRedirect())" +
        $" {{ if (Interp.Run(c, m, c.FarRetCs, c.FarRetIp, CallKey, FarDispatch)) c.FarRet = true; return; }} c.NoteFarRetUnwind(); }} }} }}";

    // Guard WITH co-routine redirection (Frente 1), used at FAR call-sites (which push CS, so the
    // popped FarRetCs is reliable). The PROPAGATE branch (SP exceeded the entry level) is untouched:
    // that frame really was unwound by the callee, so we still propagate. Only the STOP branch (this
    // frame survived) is fixed: if the retf returned to an IP OTHER than this call-site's inline
    // continuation (0x{ret}) it is a hand-pushed continuation (co-routine / shared epilogue) → continue
    // AT the real destination instead of resuming inline. We RESUME IT IN THE INTERPRETER, not via
    // FarDispatch: a co-routine loop (FILA's retf jumps back to the loop head every iteration) is a
    // LOOP, not recursion — dispatching the continuation as a lifted call would recurse one C# frame
    // per iteration (blows _farDepth at 256). The interp runs the continuation's loop in a single frame
    // (its own retf-redirect uses ip=...; continue). ShouldFarRetRedirect gates it on CS plausibility
    // (null hook = judge/COM: any differing IP redirects). The near/far ±2 garbage returns to the SAME
    // IP (== ret) and never enters this branch by construction.
    // PROPAGATE refinement (clase #8, gate PSTK): a driver that relocates SS:SP (private stack)
    // makes the (short)(SP - _spEntry) delta a compare across DIFFERENT stacks — meaningless and
    // positive — so a perfectly balanced retf whose popped pair IS this call-site's continuation
    // (by VALUE: FarRetCs:FarRetIp == pushed _fcs:0x{ret}) used to be discarded and the thread
    // unwound to the bottom. Hardware simply continues at the popped pair: consume the flag and
    // resume inline, restoring the caller's CS (the callee left c.CS at its own segment). The
    // STOP branch is byte-identical to the proven guard (CORO/DEEP untouched by construction).
    // PROPAGATE branch, second refinement (parte J "resume at the first propagate", now safe
    // thanks to CS-induction): an INTRA-segment transported destination (ShouldFarRetRedirect:
    // FarRetCs == c.CS, reliable once call-sites restore CS) is a genuine hand-pushed co-routine
    // continuation — redirect to it AT THE FIRST discard instead of unwinding to the bottom
    // (mid-way STOP frames used to eat it and resume inline at the wrong place: a test game's city/name
    // draw pass silently died this way). Cross-segment still propagates (bottom-resume backstop).
    // The redirect CONSUMES the flag before Interp.Run (gate NCORC, [NREDIR] residuals): the
    // redirect IS the attention to this retf; entering the interp with the flag still set poisons
    // its guards, and a continuation ending in a balanced near ret (Run == false) would leave the
    // stale flag to re-redirect the next guard to an already-executed destination.
    private static string CallGuardRedir(ushort ret) =>
        $" if (c.FarRet) {{ if ((short)(c.SP - _spEntry) > 0) {{" +
        $" if (c.FarRetIp == 0x{ret:X4} && c.FarRetCs == _fcs) {{ c.FarRet = false; c.CS = _fcs; }}" +
        $" else if (Interp.Enabled && c.ShouldFarRetRedirect()) {{ c.FarRet = false; if (Interp.Run(c, m, c.FarRetCs, c.FarRetIp, CallKey, FarDispatch)) c.FarRet = true; return; }}" +
        $" else {{ c.NoteFarRetUnwind(); return; }} }}" +
        $" else {{ c.FarRet = false;" +
        $" if (c.FarRetIp != 0x{ret:X4}) {{ if (Interp.Enabled && c.ShouldFarRetRedirect())" +
        $" {{ if (Interp.Run(c, m, c.FarRetCs, c.FarRetIp, CallKey, FarDispatch)) c.FarRet = true; return; }} c.NoteFarRetUnwind(); }} }} }}";

    private string LiftJmp(in Instruction insn)
    {
        if (insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            return $"goto L_{(ushort)insn.NearBranchTarget:X4};";
        // jmp far directo (cualquier seg:off) — FarDispatch (tail-call)
        if (insn.Op0Kind == OpKind.FarBranch16)
        {
            return $"FarDispatch(c, m, 0x{insn.FarBranchSelector:X4}, 0x{insn.FarBranch16:X4}); return;";
        }
        // jmp indirecto por memoria: near (word/WordOffset) vs far (dword/FarPointer)
        if (insn.Op0Kind == OpKind.Memory)
        {
            (string seg, string ea) = MemSegEa(insn);
            if (insn.MemorySize is MemorySize.UInt16 or MemorySize.WordOffset)
                return LiftNearIndirectJmp(seg, ea);
            return $"{{ ushort _fOff = m.RdU16({seg}, (ushort)({ea})); ushort _fSeg = m.RdU16({seg}, (ushort)({ea} + 2)); " +
                   $"FarDispatch(c, m, _fSeg, _fOff); return; }}";
        }
        // jmp register (near indirect)
        if (insn.Op0Kind == OpKind.Register)
        {
            // idiom "ret manual" (pop R al entrar + jmp R): retorno al llamador
            if (ManualRetReg != Register.None && insn.Op0Register.GetFullRegister() == ManualRetReg)
                return "return; // manual ret: jmp <reg> con la dirección de retorno popeada al entrar";
            string r = Reg(insn.Op0Register);
            return $"FarDispatch(c, m, c.CS, {r}); return;";
        }
        Stubbed.Add("jmp far indirecto");
        return "return; // jmp far indirecto: forma no reconocida";
    }

    /// <summary>Detecta el idiom "ret manual": la función empieza con <c>pop R</c> (consume su
    /// dirección de retorno) y contiene <c>jmp R</c> sin que NINGUNA otra instrucción escriba R.
    /// Conservador: cualquier posible escritura a R (o sub-registro) descalifica.</summary>
    public static Register DetectManualRetReg(DecodedFunction fn)
    {
        var insns = fn.Instructions;
        if (insns.Count < 2) return Register.None;
        DecodedInsn? entry = null;
        foreach (var d in insns) if (d.Offset == fn.SegEntry) { entry = d; break; }
        if (entry is null) return Register.None;
        var e = entry.Value.Iced;
        if (e.Mnemonic != Mnemonic.Pop || e.Op0Kind != OpKind.Register) return Register.None;
        Register r = e.Op0Register.GetFullRegister();
        bool hasJmpR = false;
        foreach (var d in insns)
        {
            if (d.Offset == fn.SegEntry) continue; // the entry pop itself
            var i = d.Iced;
            if (i.Mnemonic == Mnemonic.Jmp && i.Op0Kind == OpKind.Register &&
                i.Op0Register.GetFullRegister() == r)
            { hasJmpR = true; continue; }
            if (i.Mnemonic == Mnemonic.Leave && r == Register.BP) return Register.None;
            if (i.Mnemonic == Mnemonic.Xchg &&
                ((i.Op0Kind == OpKind.Register && i.Op0Register.GetFullRegister() == r) ||
                 (i.Op1Kind == OpKind.Register && i.Op1Register.GetFullRegister() == r)))
                return Register.None;
            if (i.OpCount > 0 && i.Op0Kind == OpKind.Register && i.Op0Register.GetFullRegister() == r &&
                i.Mnemonic is not (Mnemonic.Push or Mnemonic.Cmp or Mnemonic.Test or Mnemonic.Call))
                return Register.None;
        }
        return hasJmpR ? r : Register.None;
    }

    /// <summary>near indirect jmp [mem]: lee 1 word (IP), genera switch local sobre labels internos
    /// de la función actual + fallback a FarDispatch para cross-function.</summary>
    private string LiftNearIndirectJmp(string seg, string ea)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{{ ushort _ip = m.RdU16({seg}, (ushort)({ea})); ");
        if (CurrentFnLabels != null && CurrentFnLabels.Count > 0)
        {
            // ImageBias: en mundo memimage/snapshot los punteros near en memoria llevan el
            // sesgo EXEPACK; sin restarlo el switch nunca matchea y cae a FarDispatch (MISS
            // + SP corrupto). No-op en mundo canónico (ImageBias=0).
            sb.Append("int _lbl = c.CS * 16 + (int)_ip - c.LoadBase - c.ImageBias; switch ((ushort)_lbl) { ");
            foreach (var lbl in CurrentFnLabels.OrderBy(x => x))
                sb.Append($"case 0x{lbl:X4}: goto L_{lbl:X4}; ");
            sb.Append("default: FarDispatch(c, m, c.CS, _ip); return; } }");
        }
        else
        {
            sb.Append("FarDispatch(c, m, c.CS, _ip); return; }");
        }
        return sb.ToString();
    }

    // jmp por tabla (dispatcher): switch sobre el registro índice; cada destino es una función
    // handler a la que el dispatcher tail-salta (Fn_<target>; return). El juez valida la semántica.
    private string LiftJumpTable(in DecodedInsn d)
    {
        var sb = new System.Text.StringBuilder($"switch ({d.JumpIndexReg}) {{ ");
        foreach (var (idx, target) in d.JumpTable!)
            sb.Append($"case 0x{idx:X4}: {{ Fn_{target:X5}(c, m); return; }} ");
        sb.Append("default: throw new System.NotSupportedException(\"jmp tabla: indice fuera de rango\"); }");
        return sb.ToString();
    }

    private string LiftJcc(in Instruction insn, string cond)
    {
        if (insn.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
            return UnsupportedExpr(insn, "salto condicional no-near") + ";";
        return $"if ({cond}) goto L_{(ushort)insn.NearBranchTarget:X4};";
    }

    private string LiftLoop(in Instruction insn, string extra)
    {
        if (insn.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
            return UnsupportedExpr(insn, "loop no-near") + ";";
        return $"c.CX = (ushort)(c.CX - 1); if ({extra}) goto L_{(ushort)insn.NearBranchTarget:X4};";
    }

    /// <summary>Condición C# (sobre flags) de cada salto condicional / jcxz. [ESTABLECIDO SDM]</summary>
    private static bool TryCond(Mnemonic m, out string cond)
    {
        cond = m switch
        {
            Mnemonic.Je => "c.ZF",
            Mnemonic.Jne => "!c.ZF",
            Mnemonic.Jb => "c.CF",
            Mnemonic.Jae => "!c.CF",
            Mnemonic.Jbe => "(c.CF || c.ZF)",
            Mnemonic.Ja => "(!c.CF && !c.ZF)",
            Mnemonic.Jl => "(c.SF != c.OF)",
            Mnemonic.Jge => "(c.SF == c.OF)",
            Mnemonic.Jle => "(c.ZF || (c.SF != c.OF))",
            Mnemonic.Jg => "(!c.ZF && (c.SF == c.OF))",
            Mnemonic.Js => "c.SF",
            Mnemonic.Jns => "!c.SF",
            Mnemonic.Jo => "c.OF",
            Mnemonic.Jno => "!c.OF",
            Mnemonic.Jp => "c.PF",
            Mnemonic.Jnp => "!c.PF",
            Mnemonic.Jcxz => "(c.CX == 0)",
            _ => ""
        };
        return cond.Length > 0;
    }

    // ================= x87 (FPU software) =================

    private string LiftFpu(in Instruction insn)
    {
        var m = insn.Mnemonic;
        switch (m)
        {
            // control / estado → no-op (modo por defecto: redondeo nearest-even, control word 0x037F)
            case Mnemonic.Wait: case Mnemonic.Fninit:
            case Mnemonic.Fldcw: case Mnemonic.Fnclex:
                return "; // x87 control (no-op; modo por defecto)";
            case Mnemonic.Fnstcw:
                return FpuMemWrite(insn, "0x037F", forceInt16: true) ?? Unsup(insn);
            case Mnemonic.Fnstsw:
                return insn.Op0Kind == OpKind.Register
                    ? "c.AX = c.Fpu.StatusWord();"
                    : (FpuMemWrite16(insn, "c.Fpu.StatusWord()") ?? Unsup(insn));

            // cargas a la pila
            case Mnemonic.Fld:
                if (insn.Op0Kind == OpKind.Register) return $"c.Fpu.Push(c.Fpu.St({StIdx(insn.Op0Register)}));";
                { string? r = FpuMemReadFloat(insn); return r == null ? Unsup(insn) : $"c.Fpu.Push({r});"; }
            case Mnemonic.Fild:
                { string? r = FpuMemReadInt(insn); return r == null ? Unsup(insn) : $"c.Fpu.Push({r});"; }
            case Mnemonic.Fld1: return "c.Fpu.Push(1.0);";
            case Mnemonic.Fldz: return "c.Fpu.Push(0.0);";
            case Mnemonic.Fldpi: return "c.Fpu.Push(System.Math.PI);";
            case Mnemonic.Fldl2t: return "c.Fpu.Push(System.Math.Log2(10.0));";
            case Mnemonic.Fldl2e: return "c.Fpu.Push(System.Math.Log2(System.Math.E));";
            case Mnemonic.Fldln2: return "c.Fpu.Push(System.Math.Log(2.0));";
            case Mnemonic.Fldlg2: return "c.Fpu.Push(System.Math.Log10(2.0));";

            // almacenamientos
            case Mnemonic.Fst: return FpuStore(insn, pop: false, asInt: false);
            case Mnemonic.Fstp: return FpuStore(insn, pop: true, asInt: false);
            case Mnemonic.Fist: return FpuStore(insn, pop: false, asInt: true);
            case Mnemonic.Fistp: return FpuStore(insn, pop: true, asInt: true);
            case Mnemonic.Fisttp: return FpuStore(insn, pop: true, asInt: true);

            // unarias sobre ST(0)
            case Mnemonic.Fchs: return "c.Fpu.SetSt(0, -c.Fpu.St(0));";
            case Mnemonic.Fabs: return "c.Fpu.SetSt(0, System.Math.Abs(c.Fpu.St(0)));";
            case Mnemonic.Fsqrt: return "c.Fpu.SetSt(0, System.Math.Sqrt(c.Fpu.St(0)));";
            case Mnemonic.Frndint: return "c.Fpu.SetSt(0, System.Math.Round(c.Fpu.St(0), System.MidpointRounding.ToEven));";
            case Mnemonic.Fxch:
                { int i = insn.OpCount > 0 && insn.Op0Kind == OpKind.Register ? StIdx(insn.Op0Register) : 1;
                  return $"{{ double _t = c.Fpu.St(0); c.Fpu.SetSt(0, c.Fpu.St({i})); c.Fpu.SetSt({i}, _t); }}"; }

            // transcendentales
            case Mnemonic.Fpatan: return "{ double _r = System.Math.Atan2(c.Fpu.St(1), c.Fpu.St(0)); c.Fpu.Pop(); c.Fpu.SetSt(0, _r); }";
            case Mnemonic.Fptan: return "c.Fpu.SetSt(0, System.Math.Tan(c.Fpu.St(0))); c.Fpu.Push(1.0);";
            case Mnemonic.Fyl2x: return "{ double _r = c.Fpu.St(1) * System.Math.Log2(c.Fpu.St(0)); c.Fpu.Pop(); c.Fpu.SetSt(0, _r); }";
            case Mnemonic.Fyl2xp1: return "{ double _r = c.Fpu.St(1) * System.Math.Log2(c.Fpu.St(0) + 1.0); c.Fpu.Pop(); c.Fpu.SetSt(0, _r); }";
            case Mnemonic.F2xm1: return "c.Fpu.SetSt(0, System.Math.Pow(2.0, c.Fpu.St(0)) - 1.0);";
            case Mnemonic.Fscale: return "c.Fpu.SetSt(0, c.Fpu.St(0) * System.Math.Pow(2.0, System.Math.Truncate(c.Fpu.St(1))));";
            case Mnemonic.Fprem: return "c.Fpu.SetSt(0, System.Math.IEEERemainder(c.Fpu.St(0), c.Fpu.St(1)));";
            case Mnemonic.Fprem1: return "c.Fpu.SetSt(0, System.Math.IEEERemainder(c.Fpu.St(0), c.Fpu.St(1)));";
            case Mnemonic.Fxtract: return "{ int _exp = (int)System.Math.ILogB(c.Fpu.St(0)); double _sig = c.Fpu.St(0) * System.Math.Pow(2.0, -_exp); c.Fpu.SetSt(0, _exp); c.Fpu.Push(_sig); }";
            case Mnemonic.Fcos: return "c.Fpu.SetSt(0, System.Math.Cos(c.Fpu.St(0)));";
            case Mnemonic.Fsin: return "c.Fpu.SetSt(0, System.Math.Sin(c.Fpu.St(0)));";
            case Mnemonic.Fsincos: return "{ double _v = c.Fpu.St(0); c.Fpu.SetSt(0, System.Math.Sin(_v)); c.Fpu.Push(System.Math.Cos(_v)); }";

            // miscelánea FPU
            case Mnemonic.Ftst: return "c.Fpu.Compare(c.Fpu.St(0), 0.0);";
            case Mnemonic.Fxam: return "c.Fpu.Examine();";
            case Mnemonic.Fnop: return "; // fnop";
            case Mnemonic.Fdecstp: return "c.Fpu.DecTop();";
            case Mnemonic.Fincstp: return "c.Fpu.IncTop();";
            case Mnemonic.Ffree:
                { int i = insn.OpCount > 0 && insn.Op0Kind == OpKind.Register ? StIdx(insn.Op0Register) : 0;
                  return $"c.Fpu.Free({i});"; }

            // unordered compare (387)
            case Mnemonic.Fucom: return FpuCompare(insn, 0);
            case Mnemonic.Fucomp: return FpuCompare(insn, 1);
            case Mnemonic.Fucompp: return FpuCompare(insn, 2);

            // compare + set EFLAGS (P6+)
            case Mnemonic.Fcomi:  return FpuCompareFl(insn, pop: false);
            case Mnemonic.Fcomip: return FpuCompareFl(insn, pop: true);
            case Mnemonic.Fucomi:  return FpuCompareFl(insn, pop: false);
            case Mnemonic.Fucomip: return FpuCompareFl(insn, pop: true);

            // conditional moves (P6+)
            case Mnemonic.Fcmovb:  return FpuCmov(insn, "c.CF");
            case Mnemonic.Fcmove:  return FpuCmov(insn, "c.ZF");
            case Mnemonic.Fcmovbe: return FpuCmov(insn, "(c.CF || c.ZF)");
            case Mnemonic.Fcmovu:  return FpuCmov(insn, "c.PF");
            case Mnemonic.Fcmovnb:  return FpuCmov(insn, "!c.CF");
            case Mnemonic.Fcmovne:  return FpuCmov(insn, "!c.ZF");
            case Mnemonic.Fcmovnbe: return FpuCmov(insn, "(!c.CF && !c.ZF)");
            case Mnemonic.Fcmovnu:  return FpuCmov(insn, "!c.PF");

            // save/restore FPU state (raro en juegos, stub)
            case Mnemonic.Fnsave: Stubbed.Add("fnsave"); return "; // fnsave (stub)";
            case Mnemonic.Frstor: Stubbed.Add("frstor"); return "; // frstor (stub)";
            case Mnemonic.Fnstenv: Stubbed.Add("fnstenv"); return "; // fnstenv (stub)";
            case Mnemonic.Fldenv: Stubbed.Add("fldenv"); return "; // fldenv (stub)";

            // aritmética
            case Mnemonic.Fadd: return FpuArith(insn, "+", false, false);
            case Mnemonic.Faddp: return FpuArith(insn, "+", false, true);
            case Mnemonic.Fiadd: return FpuIArith(insn, "+", false);
            case Mnemonic.Fsub: return FpuArith(insn, "-", false, false);
            case Mnemonic.Fsubp: return FpuArith(insn, "-", false, true);
            case Mnemonic.Fsubr: return FpuArith(insn, "-", true, false);
            case Mnemonic.Fsubrp: return FpuArith(insn, "-", true, true);
            case Mnemonic.Fisub: return FpuIArith(insn, "-", false);
            case Mnemonic.Fisubr: return FpuIArith(insn, "-", true);
            case Mnemonic.Fmul: return FpuArith(insn, "*", false, false);
            case Mnemonic.Fmulp: return FpuArith(insn, "*", false, true);
            case Mnemonic.Fimul: return FpuIArith(insn, "*", false);
            case Mnemonic.Fdiv: return FpuArith(insn, "/", false, false);
            case Mnemonic.Fdivp: return FpuArith(insn, "/", false, true);
            case Mnemonic.Fdivr: return FpuArith(insn, "/", true, false);
            case Mnemonic.Fdivrp: return FpuArith(insn, "/", true, true);
            case Mnemonic.Fidiv: return FpuIArith(insn, "/", false);
            case Mnemonic.Fidivr: return FpuIArith(insn, "/", true);

            // comparaciones (fijan C0/C2/C3)
            case Mnemonic.Fcom: return FpuCompare(insn, 0);
            case Mnemonic.Fcomp: return FpuCompare(insn, 1);
            case Mnemonic.Fcompp: return FpuCompare(insn, 2);
            case Mnemonic.Ficom: return FpuICompare(insn, 0);
            case Mnemonic.Ficomp: return FpuICompare(insn, 1);

            default: return Unsup(insn);
        }
    }

    private string Unsup(in Instruction insn) { NotSupported.Add($"x87 no soportado: {insn.Mnemonic}"); return $"throw new System.NotSupportedException(\"x87 {insn.Mnemonic}\"); /*UNSUPPORTED*/"; }

    private static int StIdx(Register r) => r >= Register.ST0 && r <= Register.ST7 ? r - Register.ST0 : 0;

    // valor del operando como double: memoria (float/int) o ST(i)
    private string? FpuOperand(in Instruction insn, int i)
    {
        var k = insn.GetOpKind(i);
        if (k == OpKind.Register) return $"c.Fpu.St({StIdx(insn.GetOpRegister(i))})";
        if (k == OpKind.Memory) return insn.MemorySize is MemorySize.Int16 or MemorySize.Int32 or MemorySize.Int64 ? FpuMemReadInt(insn) : FpuMemReadFloat(insn);
        return null;
    }

    private string? FpuMemReadFloat(in Instruction insn)
    {
        (string seg, string ea) = MemSegEa(insn);
        return insn.MemorySize switch
        {
            MemorySize.Float32 => $"(double)m.RdF32({seg}, {ea})",
            MemorySize.Float64 => $"m.RdF64({seg}, {ea})",
            _ => null
        };
    }
    private string? FpuMemReadInt(in Instruction insn)
    {
        (string seg, string ea) = MemSegEa(insn);
        return insn.MemorySize switch
        {
            MemorySize.Int16 => $"(double)(short)m.RdU16({seg}, {ea})",
            MemorySize.Int32 => $"(double)(int)m.RdU32({seg}, {ea})",
            // fild qword: same 64-bit form the interpreters use (Interp.FpuMemInt).
            MemorySize.Int64 => $"(double)(long)(m.RdU32({seg}, {ea}) | ((ulong)m.RdU32({seg}, (ushort)(({ea}) + 4)) << 32))",
            _ => null
        };
    }
    private string? FpuMemWrite(in Instruction insn, string val, bool forceInt16 = false)
    {
        (string seg, string ea) = MemSegEa(insn);
        if (forceInt16) return $"m.WrU16({seg}, {ea}, (ushort)({val}));";
        return insn.MemorySize switch
        {
            MemorySize.Float32 => $"m.WrF32({seg}, {ea}, (float)({val}));",
            MemorySize.Float64 => $"m.WrF64({seg}, {ea}, {val});",
            MemorySize.Int16 => $"m.WrU16({seg}, {ea}, (ushort)Fpu.ToInt({val}));",
            MemorySize.Int32 => $"m.WrU32({seg}, {ea}, (uint)Fpu.ToInt({val}));",
            // fistp qword (df /7): two stores, so the address and the value get their own locals
            // inside a block — the emitted statement must stay a single statement.
            MemorySize.Int64 => $"{{ long __i64 = Fpu.ToInt({val}); ushort __ea = {ea}; " +
                                $"m.WrU32({seg}, __ea, (uint)__i64); " +
                                $"m.WrU32({seg}, (ushort)(__ea + 4), (uint)((ulong)__i64 >> 32)); }}",
            _ => null
        };
    }
    private string? FpuMemWrite16(in Instruction insn, string val)
    { (string seg, string ea) = MemSegEa(insn); return $"m.WrU16({seg}, {ea}, (ushort)({val}));"; }

    private string FpuStore(in Instruction insn, bool pop, bool asInt)
    {
        string tail = pop ? " c.Fpu.Pop();" : "";
        if (insn.Op0Kind == OpKind.Register)   // fst/fstp st(i)
            return $"c.Fpu.SetSt({StIdx(insn.Op0Register)}, c.Fpu.St(0));{tail}";
        string? w = FpuMemWrite(insn, "c.Fpu.St(0)");
        return w == null ? Unsup(insn) : $"{w}{tail}";
    }

    private string FpuArith(in Instruction insn, string op, bool rev, bool pop)
    {
        // forma memoria: st(0) op= mem (sin pop). forma st: dst op= src.
        if (insn.OpCount <= 1 || insn.Op0Kind == OpKind.Memory)
        {
            string? mem = FpuMemReadFloat(insn) ?? FpuMemReadInt(insn);
            if (mem == null) return Unsup(insn);
            string e = rev ? $"({mem}) {op} c.Fpu.St(0)" : $"c.Fpu.St(0) {op} ({mem})";
            return $"c.Fpu.SetSt(0, {e});";
        }
        int d = StIdx(insn.Op0Register), s = StIdx(insn.Op1Register);
        string expr = rev ? $"c.Fpu.St({s}) {op} c.Fpu.St({d})" : $"c.Fpu.St({d}) {op} c.Fpu.St({s})";
        string tail = pop ? " c.Fpu.Pop();" : "";
        return $"c.Fpu.SetSt({d}, {expr});{tail}";
    }

    private string FpuIArith(in Instruction insn, string op, bool rev)
    {
        string? mem = FpuMemReadInt(insn);
        if (mem == null) return Unsup(insn);
        string e = rev ? $"({mem}) {op} c.Fpu.St(0)" : $"c.Fpu.St(0) {op} ({mem})";
        return $"c.Fpu.SetSt(0, {e});";
    }

    private string FpuCompare(in Instruction insn, int pops)
    {
        string val = insn.OpCount == 0 ? "c.Fpu.St(1)" : (FpuOperand(insn, 0) ?? "c.Fpu.St(1)");
        string tail = string.Concat(Enumerable.Repeat(" c.Fpu.Pop();", pops));
        return $"c.Fpu.Compare(c.Fpu.St(0), {val});{tail}";
    }
    private string FpuICompare(in Instruction insn, int pops)
    {
        string? mem = FpuMemReadInt(insn);
        if (mem == null) return Unsup(insn);
        string tail = string.Concat(Enumerable.Repeat(" c.Fpu.Pop();", pops));
        return $"c.Fpu.Compare(c.Fpu.St(0), {mem});{tail}";
    }

    private string FpuCompareFl(in Instruction insn, bool pop)
    {
        int i = insn.OpCount > 0 && insn.Op0Kind == OpKind.Register ? StIdx(insn.Op0Register) :
                (insn.OpCount > 1 && insn.Op1Kind == OpKind.Register ? StIdx(insn.Op1Register) : 1);
        string tail = pop ? " c.Fpu.Pop();" : "";
        return $"c.Fpu.CompareToFlags(c, c.Fpu.St(0), c.Fpu.St({i}));{tail}";
    }

    private string FpuCmov(in Instruction insn, string cond)
    {
        int i = insn.OpCount > 1 && insn.Op1Kind == OpKind.Register ? StIdx(insn.Op1Register) : 1;
        return $"if ({cond}) c.Fpu.SetSt(0, c.Fpu.St({i}));";
    }

    // ================= operandos =================

    /// <summary>Lee un operando como fuente (registro / inmediato / memoria). Expresión C#.</summary>
    private string Src(in Instruction insn, int i, int size)
    {
        var k = insn.GetOpKind(i);
        switch (k)
        {
            case OpKind.Register: return Reg(insn.GetOpRegister(i));
            case OpKind.Immediate8: return $"0x{insn.Immediate8:X2}";
            case OpKind.Immediate16: return $"0x{insn.Immediate16:X4}";
            case OpKind.Immediate8to16: return $"unchecked((ushort)0x{(ushort)insn.Immediate8to16:X4})";
            case OpKind.Memory:
                (string seg, string ea) = MemSegEa(insn);
                return $"m.Rd{(size == 16 ? "U16" : "U8")}({seg}, {ea})";
            default: return UnsupportedExpr(insn, $"fuente no soportada: {k}");
        }
    }

    /// <summary>
    /// Destino: prep (EA local si memoria), expresión de lectura y plantilla de escritura ({0}=valor).
    /// Para memoria, la EA se calcula UNA vez (read-modify-write coherente).
    /// </summary>
    private (string prep, string read, string writeFmt) Dst(in Instruction insn, int i, int size)
    {
        var k = insn.GetOpKind(i);
        if (k == OpKind.Register)
        {
            string r = Reg(insn.GetOpRegister(i));
            return ("", r, r + " = {0};");
        }
        if (k == OpKind.Memory)
        {
            (string seg, string ea) = MemSegEa(insn);
            string u = size == 16 ? "U16" : "U8";
            return ($"ushort _ea = {ea}; ", $"m.Rd{u}({seg}, _ea)", $"m.Wr{u}({seg}, _ea, {{0}});");
        }
        string bad = UnsupportedExpr(insn, $"destino no soportado: {k}");
        return ("", bad, bad + ";");
    }

    private static string Wrap(string prep, string stmt) => prep.Length == 0 ? stmt : "{ " + prep + stmt + " }";

    private (string seg, string ea) MemSegEa(in Instruction insn)
    {
        string seg;
        if (insn.SegmentPrefix != Register.None)
            seg = Reg(insn.SegmentPrefix);
        else if (insn.MemoryBase is Register.BP or Register.SP or Register.EBP or Register.ESP)
            seg = "c.SS";   // EBP/ESP tambien: espejo de Interp.MemSeg e Interp32.MemSeg
        else
            seg = "c.DS";

        var terms = new List<string>();
        if (insn.MemoryBase != Register.None) terms.Add(Reg(insn.MemoryBase));
        if (insn.MemoryIndex != Register.None) terms.Add(Reg(insn.MemoryIndex));
        ushort disp = (ushort)insn.MemoryDisplacement64;
        if (disp != 0 || terms.Count == 0) terms.Add($"0x{disp:X4}");
        string ea = $"(ushort)({string.Join(" + ", terms)})";
        return (seg, ea);
    }

    // ================= registros / tamaños =================

    private string UnsupportedExpr(in Instruction insn, string why)
    {
        NotSupported.Add($"{why} ({insn.Mnemonic})");
        return $"(0) /*UNSUPPORTED: {Escape(why)}*/";
    }

    private static int OperandSize(in Instruction insn, int i)
    {
        var k = insn.GetOpKind(i);
        if (k == OpKind.Register) return RegSize(insn.GetOpRegister(i));
        if (k == OpKind.Memory)
            return insn.MemorySize is MemorySize.UInt16 or MemorySize.Int16 ? 16 : 8;
        // inmediato: tamaño del otro operando
        int other = i == 0 ? 1 : 0;
        var ko = insn.GetOpKind(other);
        if (ko == OpKind.Register) return RegSize(insn.GetOpRegister(other));
        if (ko == OpKind.Memory) return insn.MemorySize is MemorySize.UInt16 or MemorySize.Int16 ? 16 : 8;
        return 16;
    }

    private static readonly Dictionary<Register, string> RegMap = new()
    {
        [Register.AX] = "c.AX", [Register.BX] = "c.BX", [Register.CX] = "c.CX", [Register.DX] = "c.DX",
        [Register.SP] = "c.SP", [Register.BP] = "c.BP", [Register.SI] = "c.SI", [Register.DI] = "c.DI",
        [Register.AL] = "c.AL", [Register.BL] = "c.BL", [Register.CL] = "c.CL", [Register.DL] = "c.DL",
        [Register.AH] = "c.AH", [Register.BH] = "c.BH", [Register.CH] = "c.CH", [Register.DH] = "c.DH",
        [Register.ES] = "c.ES", [Register.CS] = "c.CS", [Register.SS] = "c.SS", [Register.DS] = "c.DS",
        [Register.FS] = "c.FS", [Register.GS] = "c.GS",
    };

    private string Reg(Register r) => RegMap.TryGetValue(r, out var s) ? s : UnsupportedExpr(default, $"registro no mapeado: {r}");

    private static readonly HashSet<Register> Reg16 = new()
    {
        Register.AX, Register.BX, Register.CX, Register.DX,
        Register.SP, Register.BP, Register.SI, Register.DI,
        Register.ES, Register.CS, Register.SS, Register.DS, Register.FS, Register.GS,
    };

    private static int RegSize(Register r) => Reg16.Contains(r) ? 16 : 8;

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ================= 386 extensions =================

    private string LiftMovExtend(in Instruction insn, bool zeroExtend)
    {
        if (insn.GetOpKind(0) != OpKind.Register) return UnsupportedExpr(insn, "movzx/sx destino no-reg") + ";";
        int dstSize = RegSize(insn.Op0Register);
        int srcSize = insn.GetOpKind(1) == OpKind.Register ? RegSize(insn.Op1Register) :
                      (insn.MemorySize is MemorySize.UInt8 or MemorySize.Int8 ? 8 : 16);
        string src = Src(insn, 1, srcSize);
        string dst = Reg(insn.Op0Register);
        if (zeroExtend)
            return $"{dst} = ({(dstSize == 16 ? "ushort" : "byte")}){src};";
        return $"{dst} = ({(dstSize == 16 ? "ushort" : "byte")})(short){(srcSize == 8 ? $"(sbyte){src}" : src)};";
    }

    private string LiftDoubleShift(in Instruction insn, string op)
    {
        int size = OperandSize(insn, 0);
        string cnt = insn.GetOpKind(2) switch
        {
            OpKind.Immediate8 => $"0x{insn.Immediate8:X2}",
            OpKind.Register when insn.GetOpRegister(2) == Register.CL => "c.CL",
            _ => "?"
        };
        if (cnt == "?") return UnsupportedExpr(insn, "shld/shrd cuenta no imm8/CL") + ";";
        var (prep, read, wf) = Dst(insn, 0, size);
        string src = Src(insn, 1, size);
        return Wrap(prep, string.Format(wf, $"c.{op}{size}({read}, {src}, {cnt})"));
    }

    private string LiftBitTest(in Instruction insn, string op)
    {
        int size = OperandSize(insn, 0);
        string a = Src(insn, 0, size);
        string bit = Src(insn, 1, size);
        if (op == "Bt")
            return $"c.CF = (({a} >> (({bit}) & {(size == 16 ? "0x0F" : "0x07")})) & 1) != 0;";
        var (prep, read, wf) = Dst(insn, 0, size);
        string mask = $"({(size == 16 ? "ushort" : "byte")})(1 << (({bit}) & {(size == 16 ? "0x0F" : "0x07")}))";
        string setCF = $"c.CF = ({read} & {mask}) != 0; ";
        string newVal = op switch
        {
            "Bts" => $"({(size == 16 ? "ushort" : "byte")})({read} | {mask})",
            "Btr" => $"({(size == 16 ? "ushort" : "byte")})({read} & ~{mask})",
            "Btc" => $"({(size == 16 ? "ushort" : "byte")})({read} ^ {mask})",
            _ => read
        };
        return Wrap(prep, setCF + string.Format(wf, newVal));
    }

    private string LiftBitScan(in Instruction insn, bool forward)
    {
        if (insn.GetOpKind(0) != OpKind.Register) return UnsupportedExpr(insn, "bsf/bsr destino no-reg") + ";";
        int size = OperandSize(insn, 1);
        string src = Src(insn, 1, size);
        string dst = Reg(insn.Op0Register);
        string method = forward ? "Bsf" : "Bsr";
        return $"c.ZF = ({src}) == 0; if (!c.ZF) {dst} = c.{method}{size}({src});";
    }

    private string LiftSetcc(in Instruction insn, string cond)
    {
        var (prep, _, wf) = Dst(insn, 0, 8);
        return Wrap(prep, string.Format(wf, $"(byte)({cond} ? 1 : 0)"));
    }

    private string LiftCmovcc(in Instruction insn, string cond)
    {
        int size = OperandSize(insn, 0);
        string src = Src(insn, 1, size);
        var (prep, _, wf) = Dst(insn, 0, size);
        return Wrap(prep, $"if ({cond}) " + string.Format(wf, src));
    }

    // ================= IN / OUT (puertos E/S) =================

    private string LiftIn(in Instruction insn)
    {
        bool wide = insn.Op0Register == Register.AX;
        string port = insn.Op1Kind == OpKind.Immediate8 ? $"0x{insn.Immediate8:X2}" : "c.DX";
        if (wide)
            return $"if (c.Platform != null) c.AX = c.Platform.PortIn16({port}); // in ax";
        return $"if (c.Platform != null) c.AL = c.Platform.PortIn8({port}); // in al";
    }

    private string LiftOut(in Instruction insn)
    {
        bool wide = insn.Op1Register == Register.AX;
        string port = insn.Op0Kind == OpKind.Immediate8 ? $"0x{insn.Immediate8:X2}" : "c.DX";
        if (wide)
            return $"c.Platform?.PortOut16({port}, c.AX); // out ax";
        return $"c.Platform?.PortOut8({port}, c.AL); // out al";
    }
}
