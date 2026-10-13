// MEDIDOR de la fase de captura del frame (no es un gate: no dictamina, mide).
//
// Por que existe: una prueba manual de un juego 3D informo de que "de vez en cuando se ve algun
// redibujado de cielo que flashea". Ese juego espera al retrace para dibujar (3 sitios con
// `mov dx,03DAh` en su EXE), que es lo correcto. Pero nuestra presentacion captura el frame con
// un reloj DISTINTO del que define la fase de vblank:
//
//   - fase de retrace (0x3DA bit 3): VideoState.RetraceClock, `elapsed % 14.286 < 1.4`
//   - captura del frame:             MachineTimer._vgaAccumMs (con clamps y re-anclajes)
//                                    + throttle sobre Environment.TickCount64, cuya resolucion
//                                      en Windows es de ~15 ms
//
// Misma frecuencia nominal (70 Hz) y fase LIBRE. Cuando la captura cae dentro del vblank puede
// atrapar al juego a medio repintar y se presenta un fotograma incompleto: un parpadeo.
//
// POR QUE NO SE HA "ARREGLADO" FIJANDO LA FASE: hay una decision en contra, tomada con medida
// sobre el oraculo (ver el comentario largo en VideoState.CaptureFrontBuffer). El cursor
// software de un juego de prueba es un sprite save-under cuyo ciclo es BORRAR -> (poll INT 33h)
// -> DIBUJAR: con fase libre, una instantanea casi nunca cae en ese hueco; con fase FIJA, si
// cayera, caeria SIEMPRE y el cursor desapareceria del todo. Cambiar esto sin una prueba manual
// arriesga los juegos que ya estaban validados.
//
// Que reporta: cuantas capturas por segundo salen y como se reparten dentro del ciclo de refresco.
//
// Uso:  dotnet run -c Release --project tools\vsync_test
using System.Diagnostics;
using PakuPort.Core.Platform;

const double PeriodoMs = 1000.0 / 70.0;   // mismo refresco que StatusRead
const double VblankMs = 1.4;              // misma ventana que StatusRead
const int VentanaMs = 2000;

var vs = new VideoState();
vs.HostDrivenRetrace = true;
vs.VideoMode = 0x13;

var fases = new List<double>();
int dobles = 0, cicloPrev = -1;
long genPrev = vs.FrameGeneration;
var sw = Stopwatch.StartNew();
while (sw.ElapsedMilliseconds < VentanaMs)
{
    vs.CaptureFrontBuffer();
    if (vs.FrameGeneration != genPrev)
    {
        genPrev = vs.FrameGeneration;
        double ms = vs.RetraceClock.Elapsed.TotalMilliseconds;
        int ciclo = (int)(ms / PeriodoMs);
        fases.Add(ms - ciclo * PeriodoMs);
        if (ciclo == cicloPrev) dobles++;
        cicloPrev = ciclo;
    }
    Thread.Sleep(0);   // ceder: el bucle imita los polls frecuentes del hilo del juego
}
sw.Stop();

double porSegundo = fases.Count * 1000.0 / sw.ElapsedMilliseconds;
int enVblank = fases.Count(f => f < VblankMs);
// Reparto de las fases en 10 cubos del ciclo: plano = fase libre, un solo cubo = fase fijada.
var cubos = new int[10];
foreach (var f in fases) cubos[Math.Min(9, (int)(f / PeriodoMs * 10))]++;

Console.WriteLine($"capturas          : {fases.Count} en {sw.ElapsedMilliseconds} ms = {porSegundo:F1}/s (refresco nominal 70)");
Console.WriteLine($"dentro del vblank : {enVblank} ({(fases.Count > 0 ? 100.0 * enVblank / fases.Count : 0):F1} %) — ahi el juego puede estar dibujando");
Console.WriteLine($"repetidas en ciclo: {dobles}");
Console.WriteLine($"reparto de fase   : {string.Join(" ", cubos)}   (10 cubos de {PeriodoMs / 10:F2} ms; plano = fase libre)");
