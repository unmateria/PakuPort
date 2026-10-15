using System.Diagnostics;
using PakuPort.Core;
using PakuPort.Core.Platform;

// Probe for the guest clock under a tape. Two checks, each with its negative control
// (PAKU_RELOJ_PARED gives that piece back to the host's clock, which must turn it RED):
//
//  hora    INT 21h AH=2A/2C and INT 1Ah AH=02/04 read the fixed epoch plus the guest's time:
//          after 5,000,000 instructions at 1000/ms they must say 1994-06-01 12:00:05, in any run.
//          Before, they read the host's clock and two runs of the same tape diverged.
//  espera  A game waiting on INT 16h AH=00 with no key must still reach PAKU_AUTOEXIT_INSN.
//          Before, the guest clock stood still in that wait and the run never ended.
//
// The tape and the cut are read once per process, so every case runs in a child.
// Usage: dotnet run -c Release --project tools/reloj_cinta   -> VERDE / ROJO, exit code 0 / 1

const int Rate = 1000;

if (args.Length > 0)
{
    var (p, _, _, _) = PlatformFactory.CreateFull(Directory.GetCurrentDirectory());
    var m = new Memory { Video = p.Video };
    p.AttachMemory(m);
    var c = new Cpu { Platform = p, SS = 0x8000, SP = 0x1000, CyclesPerMs = Rate, IF = true };
    p.MachineTimer.Poll(p, c, m);
    if (args[0] == "hora")
    {
        Cpu.TickCount += 5000L * Rate;
        c.AH = 0x2A; p.Int(0x21, c, m);
        string fecha = $"{c.CX:D4}-{c.DH:D2}-{c.DL:D2}";
        c.AH = 0x2C; p.Int(0x21, c, m);
        string hora = $"{c.CH:D2}:{c.CL:D2}:{c.DH:D2}";
        c.AH = 0x02; p.Int(0x1A, c, m);
        string rtc = $"{c.CH:X2}:{c.CL:X2}:{c.DH:X2}";
        c.AH = 0x04; p.Int(0x1A, c, m);
        string rtcFecha = $"{c.CH:X2}{c.CL:X2}-{c.DH:X2}-{c.DL:X2}";
        Console.WriteLine($"{fecha} {hora} {rtcFecha} {rtc}");
    }
    else
    {
        c.AH = 0x00; p.Int(0x16, c, m);     // no key ever comes: only the cut can end this
        Console.WriteLine("returned");
    }
    return 0;
}

string dir = Path.Combine(Path.GetTempPath(), "pakuport-reloj-cinta-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
string tape = Path.Combine(dir, "vacia.tape");
File.WriteAllText(tape, "");

(string salida, bool acabo) Hijo(string caso, string? pared, int timeoutMs)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!, caso)
    {
        WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    psi.Environment["PAKU_TAPE_REPLAY"] = tape;
    psi.Environment["PAKU_HEADLESS"] = "1";
    psi.Environment["PAKU_NOAUDIO"] = "1";
    if (caso == "espera") psi.Environment["PAKU_AUTOEXIT_INSN"] = "200000";
    if (pared != null) psi.Environment["PAKU_RELOJ_PARED"] = pared;
    using var p = Process.Start(psi)!;
    var outTask = p.StandardOutput.ReadToEndAsync();
    _ = p.StandardError.ReadToEndAsync();
    bool acabo = p.WaitForExit(timeoutMs);
    if (!acabo) p.Kill(true);
    return (acabo ? outTask.Result.Trim() : "", acabo);
}

const string Esperada = "1994-06-01 12:00:05 1994-06-01 12:00:05";
bool todo = true;
void Caso(string nombre, bool ok, string detalle)
{
    todo &= ok;
    Console.WriteLine($"{(ok ? "VERDE" : "ROJO ")}  {nombre}: {detalle}");
}

var h1 = Hijo("hora", null, 30000);
Thread.Sleep(1300);
var h2 = Hijo("hora", null, 30000);
Caso("hora bajo cinta = epoca + tiempo del invitado, igual en dos corridas",
     h1.salida == Esperada && h2.salida == Esperada, $"'{h1.salida}' / '{h2.salida}' (se espera '{Esperada}')");
var hp = Hijo("hora", "hora", 30000);
Caso("control: con PAKU_RELOJ_PARED=hora sale la del anfitrion", hp.acabo && hp.salida != Esperada, $"'{hp.salida}'");

var e = Hijo("espera", null, 15000);
Caso("esperando tecla bajo cinta, el corte por instrucciones llega", e.acabo, e.acabo ? "corto" : "colgado a los 15 s");
var ep = Hijo("espera", "espera", 5000);
Caso("control: con PAKU_RELOJ_PARED=espera se queda esperando", !ep.acabo, ep.acabo ? "corto (el control no ve nada)" : "colgado, como debe");

Directory.Delete(dir, true);
return todo ? 0 : 1;
