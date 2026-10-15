using PakuPort.Core;
using PakuPort.Core.Platform;
using Xunit;

namespace PakuPort.Tests;

/// <summary>
/// BIOS and DOS services checked against DOSBox-X's behaviour, through a full platform as a
/// ported game sees it.
/// </summary>
public class BiosDosServiceTests
{
    static (DosPlatform platform, Int16hHandler keyboard, Cpu cpu, Memory mem) NewMachine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pakuport-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var (platform, keyboard, _, _) = PlatformFactory.CreateFull(dir);
        var cpu = new Cpu { Platform = platform };
        var mem = new Memory { Video = platform.Video };
        platform.AttachMemory(mem);                           // BDA, IVT stubs and the timer, as a host does
        return (platform, keyboard, cpu, mem);
    }

    static void Int10(DosPlatform p, Cpu c, Memory m, ushort ax, ushort bx = 0, ushort dx = 0, ushort cx = 0)
    {
        c.AX = ax; c.BX = bx; c.DX = dx; c.CX = cx;
        p.Int(0x10, c, m);
    }

    static byte[] Cell(DosPlatform p, int col, int row)
    {
        var cell = new byte[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                cell[y * 8 + x] = p.Video.Planes[0][(row * 8 + y) * 320 + col * 8 + x];
        return cell;
    }

    // dosbox-x int10_char.cpp WriteChar, case M_VGA: every pixel of the 8x8 cell is written,
    // glyph bits in the attribute colour and the rest in the background, which in 256-colour
    // modes is the "page" argument (back=page). Drawing only the glyph bits made a counter
    // rewritten in place pile its digits.
    [Fact]
    public void Mode13h_Teletype_RewritingACellLeavesOnlyTheNewCharacter()
    {
        var (p, _, c, m) = NewMachine();
        Int10(p, c, m, 0x0013);                               // mode 13h
        Int10(p, c, m, 0x0200, dx: 0x0000);                   // cursor to 0,0
        Int10(p, c, m, 0x0E00 | 'I', bx: 0x000E);              // 'I' alone
        var alone = Cell(p, 0, 0);

        Int10(p, c, m, 0x0200, dx: 0x0001);                   // cursor to column 1
        Int10(p, c, m, 0x0E00 | 'W', bx: 0x000E);              // 'W' first...
        Int10(p, c, m, 0x0200, dx: 0x0001);
        Int10(p, c, m, 0x0E00 | 'I', bx: 0x000E);              // ...then 'I' over it
        var rewritten = Cell(p, 1, 0);

        Assert.Contains(alone, b => b == 0x0E);               // the glyph is there at all
        Assert.Equal(alone, rewritten);
    }

    // AH=09 passes BH as the page (int10.cpp case 0x09), so BH is the background in mode 13h.
    [Fact]
    public void Mode13h_WriteChar_BackgroundComesFromBH()
    {
        var (p, _, c, m) = NewMachine();
        Int10(p, c, m, 0x0013);
        Int10(p, c, m, 0x0200, dx: 0x0000);
        Int10(p, c, m, 0x0900 | 'I', bx: 0x050E, cx: 1);       // BL=14 glyph, BH=5 background
        var cell = Cell(p, 0, 0);

        Assert.All(cell, b => Assert.True(b == 0x0E || b == 0x05, $"pixel {b:X2}"));
        Assert.Contains(cell, b => b == 0x05);
        Assert.Contains(cell, b => b == 0x0E);
    }

    // AH=0E ignores BH: INT10_TeletypeOutput passes the CURRENT page from the BDA
    // (int10_char.cpp:1879), so with page 0 active the background is 0 whatever BH holds.
    [Fact]
    public void Mode13h_Teletype_IgnoresBH_BackgroundIsTheCurrentPage()
    {
        var (p, _, c, m) = NewMachine();
        Int10(p, c, m, 0x0013);
        Int10(p, c, m, 0x0200, dx: 0x0000);
        Int10(p, c, m, 0x0E00 | 'I', bx: 0x050E);              // garbage BH=5 must not paint
        var cell = Cell(p, 0, 0);

        Assert.All(cell, b => Assert.True(b == 0x0E || b == 0x00, $"pixel {b:X2}"));
        Assert.Contains(cell, b => b == 0x0E);
    }

    // dosbox-x dos.cpp: INT 21h AH=01/07/08 read from the console device, which waits for a key.
    // Returning AL=0 at once when the buffer is empty told the program "extended key follows".
    [Fact]
    public void Int21_AH08_WaitsForAKeyInsteadOfReturningZero()
    {
        var (p, keyboard, c, m) = NewMachine();
        c.AX = 0x0800;
        var call = Task.Run(() => p.Int(0x21, c, m));

        Assert.False(call.Wait(200), "AH=08 returned with no key pressed");
        keyboard.EnqueueKey(0x1E, (byte)'a');
        Assert.True(call.Wait(5000), "AH=08 did not return after the key arrived");
        Assert.Equal((byte)'a', c.AL);
    }
}
