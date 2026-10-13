using PakuPort.Core;
using PakuPort.Core.Verify;

// to_decimal: the score as four ASCII digits, written right to left into `digits`.
public static class Respuesta
{
    const ushort Score = 0x0446, LastDigit = 0x0457;

    public static void Ejecuta(Cpu c, Memory m, Original o)
    {
        c.AX = m.RdU16(c.DS, Score);
        c.SI = LastDigit;
        c.CX = 4;
        c.BX = 10;
        do
        {
            c.DX = c.Xor16(c.DX, c.DX);
            c.Div16(c.BX);                                  // AX = AX / 10, DX = remainder
            c.DL = c.Add8(c.DL, (byte)'0');
            m.WrU8(c.DS, c.SI, c.DL);
            c.SI = c.Dec16(c.SI);
            c.CX--;                                         // loop (no flags)
        } while (c.CX != 0);
        c.SP += 2;                                          // ret
        o.Instrucciones(4 + 4 * 6 + 1);
    }
}
