namespace PakuPort.Core;

/// <summary>
/// FPU x87 software (pila de 8 registros). Modela el coprocesador 8087/80287 que el C-runtime de
/// Borland usa para coma flotante. <b>Precisión:</b> se usa <see cref="double"/> (64 bits) en vez de
/// los 80 bits del x87; para el uso típico del juego (enteros y aritmética simple convertidos a
/// entero con <c>fistp</c>) coincide byte-a-byte, y el juez lo confirma o lo refuta — no se asume.
///
/// Redondeo de <c>fist/fistp</c>: round-to-nearest-even (modo por defecto del x87, control word
/// 0x037F). Comparaciones (<c>fcom</c>) fijan los códigos C0/C2/C3 que <c>fnstsw</c> lleva a AX para
/// que <c>sahf</c>+<c>jcc</c> ramifiquen igual que el original.
/// </summary>
public sealed class Fpu
{
    private readonly double[] _st = new double[8];
    private int _top = 0;                 // índice físico de ST(0)
    public bool C0, C1, C2, C3;           // códigos de condición (status word)

    public void Push(double v) { _top = (_top - 1) & 7; _st[_top] = v; }
    public double Pop() { double v = _st[_top]; _top = (_top + 1) & 7; return v; }
    public double St(int i) => _st[(_top + i) & 7];
    public void SetSt(int i, double v) => _st[(_top + i) & 7] = v;

    /// <summary>fcom: fija C3/C2/C0 según ST(0) vs val (no ordenado si NaN). [SDM]</summary>
    public void Compare(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) { C3 = C2 = C0 = true; }
        else if (a > b) { C3 = false; C2 = false; C0 = false; }
        else if (a < b) { C3 = false; C2 = false; C0 = true; }
        else { C3 = true; C2 = false; C0 = false; }
        C1 = false;
    }

    /// <summary>Status word (para fnstsw): bits C3(14) C2(10) C1(9) C0(8) + TOP(11-13).</summary>
    public ushort StatusWord()
    {
        ushort s = 0;
        if (C0) s |= 1 << 8;
        if (C1) s |= 1 << 9;
        if (C2) s |= 1 << 10;
        if (C3) s |= 1 << 14;
        s |= (ushort)((_top & 7) << 11);
        return s;
    }

    /// <summary>Conversión float→entero con redondeo del x87 (nearest-even). Usado por fist/fistp.</summary>
    public static long ToInt(double v) => (long)System.Math.Round(v, System.MidpointRounding.ToEven);

    /// <summary>FCOMI/FUCOMI: compara ST(0) vs val y fija EFLAGS (ZF, PF, CF) directamente.</summary>
    public void CompareToFlags(Cpu c, double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) { c.ZF = true; c.PF = true; c.CF = true; }
        else if (a > b) { c.ZF = false; c.PF = false; c.CF = false; }
        else if (a < b) { c.ZF = false; c.PF = false; c.CF = true; }
        else { c.ZF = true; c.PF = false; c.CF = false; }
        c.OF = false;
    }

    /// <summary>FXAM: classify ST(0) and set C3/C2/C0/C1.</summary>
    public void Examine()
    {
        double v = St(0);
        C1 = double.IsNegative(v);
        if (double.IsNaN(v)) { C3 = false; C2 = false; C0 = true; }
        else if (double.IsInfinity(v)) { C3 = false; C2 = true; C0 = true; }
        else if (v == 0.0) { C3 = true; C2 = false; C0 = false; }
        else { C3 = false; C2 = true; C0 = false; } // normal
    }

    public void DecTop() => _top = (_top - 1) & 7;
    public void IncTop() => _top = (_top + 1) & 7;
    public void Free(int i) => _st[(_top + i) & 7] = 0.0;

    /// <summary>80-bit extended real -> double. Mirrors DOSBox-X FPU_FLD80
    /// (fpu_instructions.h:119): mant = 64-bit mantissa word, se = sign+exponent word.</summary>
    public static double FromF80(ulong mant, ushort se)
    {
        long exp64 = (se & 0x7FFF) - 16383;
        long clamped = ((exp64 > 0) ? exp64 : -exp64) & 0x3FF;
        long exp64final = ((exp64 > 0) ? clamped : -clamped) + 1023;
        long mant64 = (long)((mant >> 11) & 0xFFFFFFFFFFFFFUL);
        long sign = (se & 0x8000) != 0 ? 1 : 0;
        double d = BitConverter.Int64BitsToDouble((sign << 63) | (exp64final << 52) | mant64);
        if (mant == 0x8000000000000000UL && (se & 0x7FFF) == 0x7FFF)
            d = sign != 0 ? double.NegativeInfinity : double.PositiveInfinity;
        return d;
    }

    /// <summary>double -> 80-bit extended real. Mirrors DOSBox-X FPU_ST80
    /// (fpu_instructions.h, double branch).</summary>
    public static (ulong mant, ushort se) ToF80(double v)
    {
        long ll = BitConverter.DoubleToInt64Bits(v);
        long sign80 = (ll >> 63) & 1;
        long exp80final = (ll & 0x7FF0000000000000L) >> 52;
        long mant80final = (ll & 0x000FFFFFFFFFFFFFL) << 11;
        if (v != 0)
        {
            mant80final |= unchecked((long)0x8000000000000000UL);
            exp80final += 16383 - 1023;
        }
        return ((ulong)mant80final, (ushort)((sign80 << 15) | exp80final));
    }
}
