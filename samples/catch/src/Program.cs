// CATCH.COM — a tiny, original MS-DOS game that serves as PakuPort's tutorial subject.
//
// Stars fall from the top of the screen; move the paddle with the arrow keys to catch them.
// Three misses and the game is over. ESC quits. It starts in DEMO mode (an autopilot plays),
// like the attract mode of many games of the era, so it can be recorded with nobody at the
// keyboard; the first arrow key hands control to the player.
//
// It is written with Iced's code assembler instead of a classic .asm file so that building it
// needs nothing but the .NET SDK. Each routine below is a plain near procedure ending in `ret`,
// which is exactly the shape the corpus recorder and the remake board work with.
//
// What it exercises on purpose: mode 13h, palette writes through the DAC ports, sprites copied
// with `rep movsb` (visible to the blit telemetry), rectangles cleared with `rep stosb`, BIOS
// text (INT 10h AH=02/0Eh), the keyboard BIOS (INT 16h), the vertical retrace (port 3DAh), the
// PC speaker (PIT channel 2), and three pure-logic routines (random, check_catch, to_decimal)
// that are ideal first targets for a translation to C#.
//
// Build:  dotnet run --project samples/catch/src -- samples/catch/dos/CATCH.COM

using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

string outPath = args.Length > 0 ? args[0] : "CATCH.COM";

const int Origin = 0x100;
const int StarSize = 7, PaddleW = 24, PaddleH = 5, PaddleY = 184, GroundY = 192, StarTop = 16;
const int PaddleMaxX = 320 - PaddleW, StarCatchY = PaddleY - StarSize;

// Initialised data, laid out after the code in this order.
var data = new List<(string Name, byte[] Bytes)>
{
    ("px",       W16(148)),
    ("star_x",   W16(100)),
    ("star_y",   W16(StarTop)),
    ("old_px",   W16(148)),
    ("old_sx",   W16(100)),
    ("old_sy",   W16(StarTop)),
    ("speed",    W16(1)),
    ("score",    W16(0)),
    ("lives",    W16(3)),
    ("catches",  W16(0)),
    ("seed",     W16(0x2F1D)),
    ("demo",     W16(1)),
    ("quit",     W16(0)),
    ("beep_t",   W16(0)),
    ("digits",   Z("0000")),
    ("s_score",  Z("SCORE")),
    ("s_lives",  Z("LIVES ")),
    ("s_title",  Z("CATCH!  arrows move, ESC quits")),
    ("s_over",   Z("GAME OVER")),
    // DAC entries 32..35 (6-bit R,G,B): the paddle gradient.
    ("pal",      new byte[] { 20, 40, 63, 12, 30, 58, 6, 20, 48, 2, 10, 36 }),
    ("spr_star", Sprite(StarSize, StarSize,
        "...F...",
        "..EFE..",
        ".EEFEE.",
        "FFFFFFF",
        ".EEFEE.",
        "..EFE..",
        "...F...")),
    ("spr_paddle", Sprite(PaddleW, PaddleH,
        ".0000000000000000000000.",
        "111111111111111111111111",
        "222222222222222222222222",
        "333333333333333333333333",
        ".3333333333333333333333.")),
};

// Two passes: the first only measures where the data block lands. Every instruction that
// refers to data uses a 16-bit absolute address or immediate, so its size does not depend on
// the value and the second pass must produce exactly the same layout (checked below).
var addr = new Dictionary<string, int>();
var first = Assemble(addr, out int dataStart);
int at = dataStart;
foreach (var (name, bytes) in data) { addr[name] = at; at += bytes.Length; }
var final = Assemble(addr, out int dataStart2);
if (dataStart2 != dataStart || final.Length != first.Length)
    throw new InvalidOperationException("layout changed between passes");

File.WriteAllBytes(outPath, final);
Console.WriteLine($"{outPath}: {final.Length} bytes (code {dataStart - Origin}, data {final.Length - (dataStart - Origin)})");
return 0;

byte[] Assemble(Dictionary<string, int> symbols, out int dataRip)
{
    var a = new Assembler(16);
    int Sym(string n) => symbols.TryGetValue(n, out var v) ? v : 0x8000;
    AssemblerMemoryOperand Wv(string n) => __word_ptr[(long)Sym(n)];
    ushort Ofs(string n) => (ushort)Sym(n);

    var start = a.CreateLabel("start");
    var screenOffset = a.CreateLabel("screen_offset");
    var blit = a.CreateLabel("blit");
    var fill = a.CreateLabel("fill");
    var random = a.CreateLabel("random");
    var checkCatch = a.CreateLabel("check_catch");
    var toDecimal = a.CreateLabel("to_decimal");
    var printAt = a.CreateLabel("print_at");
    var drawHud = a.CreateLabel("draw_hud");
    var soundOn = a.CreateLabel("sound_on");
    var soundOff = a.CreateLabel("sound_off");
    var tickSound = a.CreateLabel("tick_sound");
    var waitVsync = a.CreateLabel("wait_vsync");
    var readInput = a.CreateLabel("read_input");
    var autopilot = a.CreateLabel("autopilot");
    var clampPaddle = a.CreateLabel("clamp_paddle");
    var updateStar = a.CreateLabel("update_star");
    var drawFrame = a.CreateLabel("draw_frame");
    var setPalette = a.CreateLabel("set_palette");
    var respawnStar = a.CreateLabel("respawn_star");
    var newGame = a.CreateLabel("new_game");
    var gameOver = a.CreateLabel("game_over");
    var dataBlock = a.CreateLabel("data");

    // ---- main ----------------------------------------------------------------------------
    a.Label(ref start);
    a.mov(ax, (ushort)0x0013); a.@int(0x10);            // mode 13h, 320x200x256
    a.call(setPalette);
    a.call(newGame);
    var loop = a.CreateLabel(); var noPilot = a.CreateLabel(); var exit = a.CreateLabel();
    a.Label(ref loop);
    a.call(waitVsync);
    a.call(readInput);
    a.cmp(Wv("quit"), (short)0); a.jne(exit);
    a.cmp(Wv("demo"), (short)0); a.je(noPilot);
    a.call(autopilot);
    a.Label(ref noPilot);
    a.call(updateStar);
    a.call(drawFrame);
    a.call(tickSound);
    a.cmp(Wv("lives"), (short)0); a.jne(loop);
    a.call(gameOver);
    a.cmp(Wv("quit"), (short)0); a.je(loop);
    a.Label(ref exit);
    a.call(soundOff);
    a.mov(ax, (ushort)0x0003); a.@int(0x10);            // back to text mode
    a.mov(ax, (ushort)0x4C00); a.@int(0x21);            // exit to DOS

    // ---- screen_offset: AX=y, BX=x -> DI = y*320 + x ---------------------------------------
    a.Label(ref screenOffset);
    a.push(dx);
    a.mov(dx, (ushort)320);
    a.mul(dx);
    a.add(ax, bx);
    a.mov(di, ax);
    a.pop(dx);
    a.ret();

    // ---- blit: copy a CX x DX sprite from DS:SI to the screen at DI ------------------------
    a.Label(ref blit);
    a.push(es); a.push(ax); a.push(bx);
    a.mov(ax, (ushort)0xA000); a.mov(es, ax);
    a.mov(bx, cx);
    var blitRow = a.CreateLabel();
    a.Label(ref blitRow);
    a.mov(cx, bx);
    a.rep.movsb();
    a.add(di, (ushort)320); a.sub(di, bx);
    a.dec(dx); a.jnz(blitRow);
    a.pop(bx); a.pop(ax); a.pop(es);
    a.ret();

    // ---- fill: paint a CX x DX rectangle of colour AL at DI --------------------------------
    a.Label(ref fill);
    a.push(es); a.push(bx);
    a.mov(bx, (ushort)0xA000); a.mov(es, bx);
    a.mov(bx, cx);
    var fillRow = a.CreateLabel();
    a.Label(ref fillRow);
    a.mov(cx, bx);
    a.rep.stosb();
    a.add(di, (ushort)320); a.sub(di, bx);
    a.dec(dx); a.jnz(fillRow);
    a.pop(bx); a.pop(es);
    a.ret();

    // ---- random: seed = seed*25173 + 13849; returns the new seed in AX ---------------------
    a.Label(ref random);
    a.push(dx);
    a.mov(ax, Wv("seed"));
    a.mov(dx, (ushort)25173);
    a.mul(dx);
    a.add(ax, (ushort)13849);
    a.mov(Wv("seed"), ax);
    a.pop(dx);
    a.ret();

    // ---- check_catch: AX=1 if the star overlaps the paddle horizontally, else 0 ------------
    a.Label(ref checkCatch);
    var miss = a.CreateLabel();
    a.mov(ax, Wv("star_x"));
    a.add(ax, (ushort)StarSize);
    a.cmp(ax, Wv("px"));
    a.jle(miss);
    a.mov(ax, Wv("px"));
    a.add(ax, (ushort)PaddleW);
    a.cmp(Wv("star_x"), ax);
    a.jge(miss);
    a.mov(ax, (ushort)1);
    a.ret();
    a.Label(ref miss);
    a.xor(ax, ax);
    a.ret();

    // ---- to_decimal: score -> four ASCII digits in `digits` --------------------------------
    a.Label(ref toDecimal);
    a.mov(ax, Wv("score"));
    a.mov(si, (ushort)(Sym("digits") + 3));
    a.mov(cx, (ushort)4);
    a.mov(bx, (ushort)10);
    var digit = a.CreateLabel();
    a.Label(ref digit);
    a.xor(dx, dx);
    a.div(bx);
    a.add(dl, (byte)'0');
    a.mov(__byte_ptr[si], dl);
    a.dec(si);
    a.loop(digit);
    a.ret();

    // ---- print_at: DH=row, DL=column, BL=colour, DS:SI = zero-terminated text --------------
    a.Label(ref printAt);
    a.mov(bh, (byte)0);
    a.mov(ah, (byte)0x02); a.@int(0x10);                // set cursor
    var chr = a.CreateLabel(); var printed = a.CreateLabel();
    a.Label(ref chr);
    a.lodsb();
    a.or(al, al); a.jz(printed);
    a.mov(ah, (byte)0x0E); a.@int(0x10);                // teletype output
    a.jmp(chr);
    a.Label(ref printed);
    a.ret();

    // ---- draw_hud: score and lives on the top text row -------------------------------------
    a.Label(ref drawHud);
    a.call(toDecimal);
    a.mov(dx, (ushort)0x0001); a.mov(si, Ofs("s_score")); a.mov(bl, (byte)15); a.call(printAt);
    a.mov(dx, (ushort)0x0007); a.mov(si, Ofs("digits")); a.mov(bl, (byte)14); a.call(printAt);
    a.mov(dx, (ushort)0x001E); a.mov(si, Ofs("s_lives")); a.mov(bl, (byte)15); a.call(printAt);
    a.mov(al, __byte_ptr[(long)Sym("lives")]);
    a.add(al, (byte)'0');
    a.mov(ah, (byte)0x0E); a.mov(bx, (ushort)0x000C); a.@int(0x10);
    a.ret();

    // ---- sound_on: BX = PIT divisor; speaker on through PIT channel 2 ----------------------
    a.Label(ref soundOn);
    a.mov(al, (byte)0xB6); a.@out((byte)0x43, al);
    a.mov(ax, bx);
    a.@out((byte)0x42, al);
    a.mov(al, ah); a.@out((byte)0x42, al);
    a.@in(al, (byte)0x61); a.or(al, (byte)3); a.@out((byte)0x61, al);
    a.ret();

    a.Label(ref soundOff);
    a.@in(al, (byte)0x61); a.and(al, (byte)0xFC); a.@out((byte)0x61, al);
    a.ret();

    // ---- tick_sound: count the current beep down, one frame at a time ----------------------
    a.Label(ref tickSound);
    var tickDone = a.CreateLabel();
    a.cmp(Wv("beep_t"), (short)0); a.je(tickDone);
    a.dec(Wv("beep_t")); a.jnz(tickDone);
    a.call(soundOff);
    a.Label(ref tickDone);
    a.ret();

    // ---- wait_vsync: wait for the start of the next vertical retrace -----------------------
    a.Label(ref waitVsync);
    a.mov(dx, (ushort)0x3DA);
    var inRetrace = a.CreateLabel(); var notYet = a.CreateLabel();
    a.Label(ref inRetrace);
    a.@in(al, dx); a.test(al, (byte)8); a.jnz(inRetrace);
    a.Label(ref notYet);
    a.@in(al, dx); a.test(al, (byte)8); a.jz(notYet);
    a.ret();

    // ---- read_input: arrows move the paddle (and end the demo), ESC quits ------------------
    a.Label(ref readInput);
    var noKey = a.CreateLabel(); var notEsc = a.CreateLabel(); var notLeft = a.CreateLabel();
    a.mov(ah, (byte)0x01); a.@int(0x16); a.jz(noKey);   // key waiting?
    a.mov(ah, (byte)0x00); a.@int(0x16);                // take it
    a.cmp(ah, (byte)0x01); a.jne(notEsc);
    a.mov(Wv("quit"), (ushort)1);
    a.ret();
    a.Label(ref notEsc);
    a.cmp(ah, (byte)0x4B); a.jne(notLeft);
    a.mov(Wv("demo"), (ushort)0);
    a.sub(Wv("px"), (short)6);
    a.jmp(clampPaddle);
    a.Label(ref notLeft);
    a.cmp(ah, (byte)0x4D); a.jne(noKey);
    a.mov(Wv("demo"), (ushort)0);
    a.add(Wv("px"), (short)6);
    a.jmp(clampPaddle);
    a.Label(ref noKey);
    a.ret();

    // ---- autopilot: move the paddle towards the star, 2 pixels per frame at most -----------
    a.Label(ref autopilot);
    var goLeft = a.CreateLabel(); var onTarget = a.CreateLabel();
    a.mov(ax, Wv("star_x"));
    a.sub(ax, (ushort)((PaddleW - StarSize) / 2));
    a.cmp(ax, Wv("px"));
    a.je(onTarget);
    a.jl(goLeft);
    a.add(Wv("px"), (short)2);
    a.jmp(clampPaddle);
    a.Label(ref goLeft);
    a.sub(Wv("px"), (short)2);
    a.jmp(clampPaddle);
    a.Label(ref onTarget);
    a.ret();

    // ---- clamp_paddle: keep px inside 0..PaddleMaxX (shared tail of the two movers) --------
    a.Label(ref clampPaddle);
    var notBelow = a.CreateLabel(); var clamped = a.CreateLabel();
    a.cmp(Wv("px"), (short)0); a.jge(notBelow);
    a.mov(Wv("px"), (ushort)0);
    a.Label(ref notBelow);
    a.cmp(Wv("px"), (short)PaddleMaxX); a.jle(clamped);
    a.mov(Wv("px"), (ushort)PaddleMaxX);
    a.Label(ref clamped);
    a.ret();

    // ---- update_star: fall, then score or lose a life when it reaches the paddle -----------
    a.Label(ref updateStar);
    var falling = a.CreateLabel(); var missed = a.CreateLabel(); var respawn = a.CreateLabel();
    a.mov(ax, Wv("speed"));
    a.add(Wv("star_y"), ax);
    a.cmp(Wv("star_y"), (short)StarCatchY);
    a.jl(falling);
    a.call(checkCatch);
    a.or(ax, ax); a.jz(missed);
    a.inc(Wv("score"));
    a.mov(bx, (ushort)1356); a.call(soundOn);           // ~880 Hz
    a.mov(Wv("beep_t"), (ushort)4);
    a.inc(Wv("catches"));
    a.cmp(Wv("catches"), (short)5); a.jb(respawn);      // every five catches...
    a.mov(Wv("catches"), (ushort)0);
    a.cmp(Wv("speed"), (short)4); a.jae(respawn);
    a.inc(Wv("speed"));                                 // ...fall faster, up to 4 px/frame
    a.jmp(respawn);
    a.Label(ref missed);
    a.dec(Wv("lives"));
    a.mov(bx, (ushort)5423); a.call(soundOn);           // ~220 Hz
    a.mov(Wv("beep_t"), (ushort)10);
    a.Label(ref respawn);
    a.call(respawnStar);
    a.call(drawHud);
    a.Label(ref falling);
    a.ret();

    // ---- respawn_star: back to the top at a random column ----------------------------------
    a.Label(ref respawnStar);
    a.call(random);
    a.xor(dx, dx);
    a.mov(cx, (ushort)(320 - StarSize + 1));
    a.div(cx);
    a.mov(Wv("star_x"), dx);
    a.mov(Wv("star_y"), (ushort)StarTop);
    a.ret();

    // ---- draw_frame: erase both sprites where they were, draw them where they are ---------
    a.Label(ref drawFrame);
    a.mov(ax, Wv("old_sy")); a.mov(bx, Wv("old_sx")); a.call(screenOffset);
    a.mov(cx, (ushort)StarSize); a.mov(dx, (ushort)StarSize); a.mov(al, (byte)0); a.call(fill);
    a.mov(ax, (ushort)PaddleY); a.mov(bx, Wv("old_px")); a.call(screenOffset);
    a.mov(cx, (ushort)PaddleW); a.mov(dx, (ushort)PaddleH); a.mov(al, (byte)0); a.call(fill);
    a.mov(ax, Wv("star_y")); a.mov(bx, Wv("star_x")); a.call(screenOffset);
    a.mov(si, Ofs("spr_star")); a.mov(cx, (ushort)StarSize); a.mov(dx, (ushort)StarSize); a.call(blit);
    a.mov(ax, (ushort)PaddleY); a.mov(bx, Wv("px")); a.call(screenOffset);
    a.mov(si, Ofs("spr_paddle")); a.mov(cx, (ushort)PaddleW); a.mov(dx, (ushort)PaddleH); a.call(blit);
    a.mov(ax, Wv("star_x")); a.mov(Wv("old_sx"), ax);
    a.mov(ax, Wv("star_y")); a.mov(Wv("old_sy"), ax);
    a.mov(ax, Wv("px")); a.mov(Wv("old_px"), ax);
    a.ret();

    // ---- set_palette: DAC entries 32..35 through ports 3C8h/3C9h ---------------------------
    a.Label(ref setPalette);
    a.mov(dx, (ushort)0x3C8); a.mov(al, (byte)32); a.@out(dx, al);
    a.inc(dx);
    a.mov(si, Ofs("pal")); a.mov(cx, (ushort)12);
    var palByte = a.CreateLabel();
    a.Label(ref palByte);
    a.lodsb(); a.@out(dx, al);
    a.loop(palByte);
    a.ret();

    // ---- new_game: reset the state and draw the static scene -------------------------------
    a.Label(ref newGame);
    a.mov(Wv("score"), (ushort)0);
    a.mov(Wv("lives"), (ushort)3);
    a.mov(Wv("speed"), (ushort)1);
    a.mov(Wv("catches"), (ushort)0);
    a.call(respawnStar);
    a.xor(di, di); a.mov(cx, (ushort)320); a.mov(dx, (ushort)200); a.mov(al, (byte)0); a.call(fill);
    a.mov(ax, (ushort)GroundY); a.xor(bx, bx); a.call(screenOffset);
    a.mov(cx, (ushort)320); a.mov(dx, (ushort)(200 - GroundY)); a.mov(al, (byte)6); a.call(fill);
    a.mov(dx, (ushort)0x0105); a.mov(si, Ofs("s_title")); a.mov(bl, (byte)7); a.call(printAt);
    a.call(drawHud);
    a.ret();

    // ---- game_over: in demo mode wait two seconds (or a key); otherwise wait for a key ----
    a.Label(ref gameOver);
    var waitKey = a.CreateLabel(); var demoWait = a.CreateLabel(); var player = a.CreateLabel();
    var restart = a.CreateLabel();
    a.call(soundOff);
    a.mov(dx, (ushort)0x0C0F); a.mov(si, Ofs("s_over")); a.mov(bl, (byte)12); a.call(printAt);
    a.cmp(Wv("demo"), (short)0); a.je(waitKey);
    a.mov(cx, (ushort)140);                             // two seconds at 70 Hz
    a.Label(ref demoWait);
    a.push(cx); a.call(waitVsync); a.pop(cx);
    a.mov(ah, (byte)0x01); a.@int(0x16); a.jnz(waitKey);
    a.loop(demoWait);
    a.jmp(restart);
    a.Label(ref waitKey);
    a.mov(ah, (byte)0x00); a.@int(0x16);
    a.cmp(ah, (byte)0x01); a.jne(player);
    a.mov(Wv("quit"), (ushort)1);
    a.ret();
    a.Label(ref player);
    a.mov(Wv("demo"), (ushort)0);
    a.Label(ref restart);
    a.call(newGame);
    a.ret();

    // ---- data ------------------------------------------------------------------------------
    a.Label(ref dataBlock);
    foreach (var (_, bytes) in data) a.db(bytes);

    var ms = new MemoryStream();
    var result = a.Assemble(new StreamCodeWriter(ms), Origin, BlockEncoderOptions.ReturnNewInstructionOffsets);
    dataRip = (int)result.GetLabelRIP(dataBlock);
    return ms.ToArray();
}

static byte[] W16(int v) => new[] { (byte)v, (byte)(v >> 8) };

static byte[] Z(string s) => System.Text.Encoding.ASCII.GetBytes(s + "\0");

// Sprite rows as text: '.' = colour 0, 'E' = 14 (yellow), 'F' = 15 (white),
// '0'..'3' = DAC entries 32..35 (the paddle gradient set by set_palette).
static byte[] Sprite(int w, int h, params string[] rows)
{
    if (rows.Length != h || rows.Any(r => r.Length != w)) throw new ArgumentException("sprite size");
    return rows.SelectMany(r => r.Select(c => c switch
    {
        '.' => (byte)0,
        'E' => (byte)14,
        'F' => (byte)15,
        >= '0' and <= '3' => (byte)(32 + c - '0'),
        _ => throw new ArgumentException($"sprite colour '{c}'"),
    })).ToArray();
}
