using System.Diagnostics;
using PakuPort.Core;
using PakuPort.Core.Platform;

// Probe for the PBR1 group marks of PAKU_LOG_BLIT=3 (logs/blitraw.bin).
//
// A `rep movsb` whose destination is RAM opens a group ('A') but never reaches LogBlit, which is
// where groups were closed. Left open, every loose VRAM write after it looked like part of that
// blit, and tools/remake_kit/build_texto.py — which counts exactly the loose writes — lost them.
// Expected record sequence: A, B (closed with an empty key), then M (written before the first
// pixel) and W (the loose write).
// Before the fix: A M W, with the group still open at the end.
//
// The log level is read once per process, so the probe re-runs itself with PAKU_LOG_BLIT=3.
// Usage: dotnet run -c Release --project tools/blitraw_grupo   -> VERDE / ROJO, exit code 0 / 1

if (args.Length > 0 && args[0] == "hijo")
{
    var (platform, _, _, _) = PlatformFactory.CreateFull(Directory.GetCurrentDirectory());
    var m = new Memory { Video = platform.Video };
    platform.AttachMemory(m);
    var c = new Cpu { Platform = platform, SS = 0x8000, SP = 0x1000, Budget = 1000 };
    c.AX = 0x0013; platform.Int(0x10, c, m);
    byte[] code =
    {
        0xB9, 0x10, 0x00,                         // mov cx,16
        0xF3, 0xA4,                               // rep movsb      DS:SI -> ES:DI, both in RAM
        0xB8, 0x00, 0xA0,                         // mov ax,A000h
        0x8E, 0xC0,                               // mov es,ax
        0x26, 0xC6, 0x06, 0x10, 0x00, 0x05,       // mov byte es:[0010h],5   loose VRAM write
        0xC3,                                     // ret
    };
    code.CopyTo(m.Raw, 0x10000);
    c.DS = 0x2000; c.ES = 0x3000; c.SI = 0; c.DI = 0;
    Interp.Run(c, m, 0x1000, 0, null, null);
    return 0;
}

string dir = Path.Combine(Path.GetTempPath(), "pakuport-blitraw-grupo-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var psi = new ProcessStartInfo(Environment.ProcessPath!, "hijo")
{
    WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
};
psi.Environment["PAKU_LOG_BLIT"] = "3";
psi.Environment["PAKU_HEADLESS"] = "1";
psi.Environment["PAKU_NOAUDIO"] = "1";
using (var p = Process.Start(psi)!)
{
    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
    p.WaitForExit();
}

byte[] raw = File.ReadAllBytes(Path.Combine(dir, "logs", "blitraw.bin"));
var seq = new System.Text.StringBuilder();
for (int i = 4; i < raw.Length;)
{
    char k = (char)raw[i++];
    seq.Append(k);
    i += k switch
    {
        'M' => 1,
        'W' => 13,
        'P' => 16,
        'A' => 0,
        'B' => 4 + 1 + raw[i + 4] + 1 + raw[i + 5 + raw[i + 4]],
        _ => raw.Length,
    };
}
Directory.Delete(dir, true);
bool ok = seq.ToString() == "ABMW";
Console.WriteLine($"{(ok ? "VERDE" : "ROJO ")}  registros de blitraw.bin: {seq} (se espera ABMW)");
return ok ? 0 : 1;
