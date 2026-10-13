// Juez de los MICKEYS CONTRA EL BORDE: ¿sigue contando el driver el movimiento del raton
// cuando el cursor ya no puede avanzar mas?
//
// Por que existe: una prueba manual de un juego de modo 13h informo de una regresion: todo
// funcionaba salvo el raton, que no se movia por toda la pantalla. Ese juego no lee la
// posicion del cursor: integra
// los mickeys de AH=0B en SU PROPIO espacio. Nuestro driver derivaba los mickeys del delta de
// la posicion del cursor, que el host entrega ya recortada al borde de la imagen, asi que todo
// lo que el usuario moviera estando pegado a un borde se perdia. Un driver real cuenta mickeys
// del hardware y recorta el cursor APARTE -- DOSBox-X src\ints\mouse.cpp Mouse_CursorMoved:
//
//     if (user_cursor_locked) { mouse.mickey_accum_x += (dx * mouse.mickeysPerPixel_x); ... }
//     ...
//     if (mouse.x > mouse.max_x) mouse.x = mouse.max_x;      <- despues, y solo la posicion
//
// Que mide, en modo 13h (320x200, el del juego que lo destapo):
//   caso 1  el cursor se para en el borde         (la posicion SI se recorta: no debe cambiar)
//   caso 2  los mickeys siguen contra el borde    (RawMotionMode: 100 empujones de 3 px)
//   caso 3  sin RawMotionMode nada cambia         (el camino de los juegos que leen AH=03
//                                                  sigue dando exactamente los mismos mickeys)
//
// Uso:  dotnet run -c Release --project tools\mouse_raw_test
using PakuPort.Core;
using PakuPort.Core.Platform;

const int Empujones = 100, PxPorEmpujon = 3;
const int MickeysPorPx = 2;      // modo 13h: espacio virtual-640 -> 2 unidades por pixel

var (platform, _, mouse, _) = PlatformFactory.CreateFull(AppContext.BaseDirectory);
var mem = new Memory();
platform.AttachMemory(mem);
var cpu = new Cpu { Platform = platform };
mouse.OnVideoModeChanged(0x13);

// Lee y consume los mickeys por el camino real del juego (INT 33h AH=0B).
(short, short) LeerMickeys()
{
    cpu.AX = 0x000B;
    platform.Int(0x33, cpu, mem);
    return ((short)cpu.CX, (short)cpu.DX);
}

void Mover(ushort x, ushort y)
{
    mouse.SetPosition(x, y);
    mouse.DrainHostEvents();
}

// Reset del driver y posicion de partida (el primer sample tras el reset solo fija la linea base).
cpu.AX = 0x0000;
platform.Int(0x33, cpu, mem);
Mover(300, 100);
Mover(300, 100);
LeerMickeys();

// El host pega el cursor al borde derecho de la imagen y el usuario SIGUE empujando.
mouse.RawMotionMode = true;
Mover(639, 100);            // 639 = maximo del rango virtual en modo 13h
LeerMickeys();              // consumir lo que haya generado ese ultimo salto
ushort xBorde = mouse.MouseX;
int esperado = 0;
for (int i = 0; i < Empujones; i++)
{
    mouse.AddRawMotion(PxPorEmpujon, 0);   // movimiento crudo: el raton se mueve, el cursor no
    Mover(639, 100);                       // el host sigue entregando la posicion recortada
    esperado += PxPorEmpujon * MickeysPorPx;
}
var (mx, my) = LeerMickeys();

bool ok1 = mouse.MouseX == xBorde && xBorde == 639;
bool ok2 = mx == esperado && my == 0;
Console.WriteLine($"caso 1  el cursor se para en el borde : x={mouse.MouseX} (borde {xBorde}) -> {(ok1 ? "VERDE" : "ROJO")}");
Console.WriteLine($"caso 2  mickeys contra el borde       : X={mx} (esperado {esperado}) Y={my} -> {(ok2 ? "VERDE" : "ROJO")}");

// Control: el camino de siempre (juegos que leen la posicion con AH=03) no cambia.
mouse.RawMotionMode = false;
Mover(100, 100);
LeerMickeys();
Mover(150, 120);
var (mx2, my2) = LeerMickeys();
bool ok3 = mx2 == 50 * MickeysPorPx && my2 == 20 * 2;
Console.WriteLine($"caso 3  modo posicion intacto         : X={mx2} (esperado {50 * MickeysPorPx}) Y={my2} (esperado 40) -> {(ok3 ? "VERDE" : "ROJO")}");

int verde = (ok1 ? 1 : 0) + (ok2 ? 1 : 0) + (ok3 ? 1 : 0);
Console.WriteLine($"RESUMEN VERDE={verde} ROJO={3 - verde}");
return verde == 3 ? 0 : 1;
