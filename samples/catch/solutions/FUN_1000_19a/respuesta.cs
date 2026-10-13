using PakuPort.Core;
using PakuPort.Core.Verify;

// check_catch: AX = 1 if the star overlaps the paddle horizontally, else 0.
public static class Respuesta
{
    const ushort Px = 0x0438, StarX = 0x043A;
    const ushort PaddleWidth = 24, StarSize = 7;

    public static void Ejecuta(Cpu c, Memory m, Original o)
    {
        c.AX = c.Add16(m.RdU16(c.DS, StarX), StarSize);
        c.Cmp16(c.AX, m.RdU16(c.DS, Px));
        if (LessOrEqual(c)) { Miss(c, o, 4); return; }      // star ends left of the paddle

        c.AX = c.Add16(m.RdU16(c.DS, Px), PaddleWidth);
        c.Cmp16(m.RdU16(c.DS, StarX), c.AX);
        if (!Less(c)) { Miss(c, o, 8); return; }            // star starts right of the paddle

        c.AX = 1;
        c.SP += 2;                                          // ret
        o.Instrucciones(10);
    }

    static void Miss(Cpu c, Original o, int before)
    {
        c.AX = c.Xor16(c.AX, c.AX);
        c.SP += 2;                                          // ret
        o.Instrucciones(before + 2);
    }

    static bool Less(Cpu c) => c.SF != c.OF;                // jl / jge
    static bool LessOrEqual(Cpu c) => c.ZF || c.SF != c.OF; // jle
}
