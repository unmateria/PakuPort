using PakuPort.Core;
using PakuPort.Core.Verify;

// random: seed = seed * 25173 + 13849, returned in AX.
public static class Respuesta
{
    const ushort Seed = 0x044C;

    public static void Ejecuta(Cpu c, Memory m, Original o)
    {
        c.SP -= 2; m.WrU16(c.SS, c.SP, c.DX);      // push dx
        c.AX = m.RdU16(c.DS, Seed);
        c.DX = 25173;
        c.Mul16(c.DX);                              // DX:AX = AX * DX, sets CF/OF
        c.AX = c.Add16(c.AX, 13849);
        m.WrU16(c.DS, Seed, c.AX);
        c.DX = m.RdU16(c.SS, c.SP); c.SP += 2;      // pop dx
        c.SP += 2;                                  // ret
        o.Instrucciones(8);
    }
}
