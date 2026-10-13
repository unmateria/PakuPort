// Juez del governor de ciclos ante un CAMBIO DE VELOCIDAD EN CALIENTE (Ctrl+F1/F2/F3).
//
// Por que existe: el governor mide una DEUDA acumulada desde un ancla (Steps ejecutados menos
// los debidos a N ciclos/ms). Esa cuenta solo vale a velocidad CONSTANTE. Al bajar la velocidad
// sin reanclar, todo el pasado se reevalua con el numero nuevo y el governor concluye que va
// adelantado una barbaridad de trabajo: se mete a dormir hasta que el reloj real le alcance.
// Sintoma en una prueba manual: varios juegos se colgaban al cambiar de velocidad con
// Ctrl+F1 o Ctrl+F2.
//
// Que mide, sin juego de por medio y en el camino de codigo REAL (MachineTimer.Poll):
//   caso 1  cambio en caliente  -> el Poll siguiente al cambio no puede bloquear el hilo.
//   caso 2  el freno sigue vivo -> tras el cambio, adelantar trabajo SI debe dormir lo suyo
//                                  (una red que solo pidiera "no bloquees" la aprobaria un
//                                  governor desactivado).
//
// Uso:  dotnet run -c Release --project tools\governor_test
using System.Diagnostics;
using PakuPort.Core;
using PakuPort.Core.Platform;

const int Fast = 3000;   // velocidad de partida (la tipica de los juegos probados)
const int Slow = 100;    // primer escalon de Ctrl+F1 (el salto mas brusco posible)
const int WarmMs = 400;  // tiempo de juego a Fast antes de tocar la velocidad

// Sin fix, el Poll del cambio duerme WarmMs * Fast / Slow = 12 s. Con fix, ni un ms.
const double MaxBlockedMs = 100.0;
// Tras el cambio se adelanta trabajo a proposito: el governor debe frenarlo ~AheadMs.
const int AheadMs = 300;
const double MinThrottleMs = 200.0, MaxThrottleMs = 600.0;

var (platform, _, _, _) = PlatformFactory.CreateFull(AppContext.BaseDirectory);
var mem = new Memory();
platform.AttachMemory(mem);
var cpu = new Cpu { Platform = platform, CyclesPerMs = Fast };
var mt = platform.MachineTimer;

Console.WriteLine($"=== governor_test: {Fast} -> {Slow} ciclos/ms en caliente ===");

Cpu.TickCount = 0;
mt.Poll(platform, cpu, mem);          // primer Poll: fija el ancla
long baseTicks = Cpu.TickCount;

// Juego normal a Fast: Polls frecuentes (como Cpu.OnPeriodicPoll cada 4096 Steps) con el
// contador de Steps avanzando al ritmo que el governor espera -> deuda ~0, no frena.
var warm = Stopwatch.StartNew();
while (warm.Elapsed.TotalMilliseconds < WarmMs)
{
    Thread.Sleep(2);
    Cpu.TickCount = baseTicks + (long)(warm.Elapsed.TotalMilliseconds * Fast);
    mt.Poll(platform, cpu, mem);
}
warm.Stop();

// --- caso 1: el usuario pulsa Ctrl+F1 (el hilo de render escribe cpu.CyclesPerMs) ---
cpu.CyclesPerMs = Slow;
var sw = Stopwatch.StartNew();
mt.Poll(platform, cpu, mem);
sw.Stop();
double blockedMs = sw.Elapsed.TotalMilliseconds;
bool ok1 = blockedMs <= MaxBlockedMs;
Console.WriteLine($"caso 1  cambio en caliente : bloqueo={blockedMs:F1} ms (limite {MaxBlockedMs:F0}) -> {(ok1 ? "VERDE" : "ROJO")}");

// --- caso 2: con la velocidad nueva, adelantar trabajo debe seguir frenando ---
Cpu.TickCount += (long)Slow * AheadMs;
sw.Restart();
mt.Poll(platform, cpu, mem);
sw.Stop();
double throttleMs = sw.Elapsed.TotalMilliseconds;
bool ok2 = throttleMs >= MinThrottleMs && throttleMs <= MaxThrottleMs;
Console.WriteLine($"caso 2  el freno sigue vivo: espera={throttleMs:F1} ms (esperado {MinThrottleMs:F0}-{MaxThrottleMs:F0}) -> {(ok2 ? "VERDE" : "ROJO")}");

int verde = (ok1 ? 1 : 0) + (ok2 ? 1 : 0);
Console.WriteLine($"RESUMEN VERDE={verde} ROJO={2 - verde}");
return verde == 2 ? 0 : 1;
