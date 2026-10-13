// JUEZ DEL MODO TEXTO: la fuente vive en el PLANO 2 y la geometria la manda el CRTC.
//
// Por que existe: VideoState.RenderText pintaba SIEMPRE 25 filas de 8x16 con la fuente ROM de
// 8x8 duplicando cada fila, y el INT 10h AH=11h (cargar fuente) era un no-op. En una VGA de
// verdad la fuente de texto NO es una tabla del emulador: vive en el PLANO 2 de la VRAM con 32
// bytes por caracter, el programa la carga por AH=11h o escribiendo el plano 2 por puertos, y la
// geometria sale del CRTC (columnas r01+1, altura de caracter r09&0x1F+1, filas (VDE+1)/altura).
// Un tracker de musica lo destapo: trabaja en 80x50 con fuente propia y solo se veia la mitad de
// arriba de su interfaz, con glifos equivocados. Es de CLASE: afecta a toda utilidad, tracker o
// menu de juego en modo texto.
//
// Que mide (VERDE/ROJO por caso, sin ojo humano de por medio):
//   (a) set-mode 3 -> 640x400 y la 'A' de (0,0) es la ROM 8x16, no la 8x8 duplicada.
//   (b) AH=11h AL=12h (8x8 double-dot) -> 50 filas, altura 8, BDA 40:84=49 y 40:85=8,
//       y un caracter en la fila 49 se pinta en las filas de pixel 392..399.
//   (c) fuente de USUARIO por AH=11h AL=00 -> el glifo cargado para 0x80 es el que se pinta.
//   (d) carga por PUERTOS: el mismo glifo escrito a A000:(0x80*32) con map mask=4 y odd/even
//       apagado (la via que usa INT10_LoadFont de DOSBox-X, int10_memory.cpp:59) se usa.
//   (e) AH=11h AL=30 -> CX = altura REAL y DL = filas-1, de la BDA, no constantes.
//
// Control negativo: los cinco casos se vieron ROJO con el codigo anterior (fuente 8x8 doblada,
// 25 filas clavadas y AH=11h como `break;`), que es lo que exige la regla "ningun juez entra en
// verde sin haberse visto rojo".
//
// Uso:  dotnet run -c Release --project tools\textmode_test
using PakuPort.Core;
using PakuPort.Core.Platform;

int fallos = 0;

void Caso(string nombre, bool ok, string detalle)
{
    Console.WriteLine($"[{(ok ? "VERDE" : "ROJO ")}] {nombre}: {detalle}");
    if (!ok) fallos++;
}

var (platform, _, _, _) = PlatformFactory.CreateFull(AppContext.BaseDirectory);
var mem = new Memory();
platform.AttachMemory(mem);
mem.Video = platform.Video;   // el desvio A0000-BFFFF a la VRAM lo cablea el host (Program.cs:190)
var cpu = new Cpu { Platform = platform };
var v = platform.Video;

void SetMode(byte modo)
{
    cpu.AH = 0x00; cpu.AL = modo;
    platform.Int(0x10, cpu, mem);
}

// Escribe caracter+atributo en la pagina de texto por la via lineal del BIOS (plano 0 = chr,
// plano 1 = attr), que es como los reparte el odd/even de la VGA.
void PonChar(int fila, int col, byte chr, byte attr, int cols)
{
    int off = (fila * cols + col) * 2;
    v.WriteLinear(off, chr);
    v.WriteLinear(off + 1, attr);
}

// Un pixel "encendido" es el que no es negro: los casos usan attr 0x0F (blanco sobre negro).
bool Encendido(byte[] fb, int w, int x, int y)
{
    int i = (y * w + x) * 4;
    return fb[i] != 0 || fb[i + 1] != 0 || fb[i + 2] != 0;
}

// Compara la celda (fila,col) contra `filas` bytes de patron, uno por fila de barrido.
(bool ok, int malos) CeldaCoincide(byte[] fb, int w, int fila, int col, int alto,
                                   ReadOnlySpan<byte> patron)
{
    int malos = 0;
    for (int py = 0; py < alto; py++)
    for (int px = 0; px < 8; px++)
    {
        bool esperado = ((patron[py] >> (7 - px)) & 1) != 0;
        if (Encendido(fb, w, col * 8 + px, fila * alto + py) != esperado) malos++;
    }
    return (malos == 0, malos);
}

Console.WriteLine("=== textmode_test: fuente en el plano 2 y geometria del CRTC ===");

// ── (a) set-mode 3: 640x400 y la 'A' con la ROM de 8x16 ───────────────────────────────────
{
    SetMode(0x03);
    PonChar(0, 0, (byte)'A', 0x0F, 80);
    var fb = v.RenderFramebuffer();
    bool dim = v.Width == 640 && v.Height == 400;
    var esperado = RomFonts.Font8x16.AsSpan('A' * 16, 16);
    var (ok, malos) = dim ? CeldaCoincide(fb, v.Width, 0, 0, 16, esperado) : (false, -1);
    Caso("a) modo 3: 640x400 y 'A' = ROM 8x16", dim && ok,
         $"{v.Width}x{v.Height}, pixeles distintos del glifo 8x16 = {malos}");
}

// ── (b) AH=11h AL=12h: 50 filas de 8 px ───────────────────────────────────────────────────
{
    SetMode(0x03);
    cpu.AH = 0x11; cpu.AL = 0x12; cpu.BL = 0x00;
    platform.Int(0x10, cpu, mem);

    byte filasMenos1 = mem.RdU8(0x40, 0x84);
    ushort alturaBda = mem.RdU16(0x40, 0x85);
    int alturaCrtc = (v.CrtcRegs[0x09] & 0x1F) + 1;
    int vde = (v.CrtcRegs[0x12] | ((v.CrtcRegs[0x07] & 0x02) << 7) | ((v.CrtcRegs[0x07] & 0x40) << 3)) + 1;
    int filas = alturaCrtc > 0 ? vde / alturaCrtc : 0;

    PonChar(49, 0, (byte)'A', 0x0F, 80);
    var fb = v.RenderFramebuffer();
    bool geo = filasMenos1 == 49 && alturaBda == 8 && filas == 50 && alturaCrtc == 8
               && v.Height == 400;
    var esperado = RomFonts.Font8x8.AsSpan('A' * 8, 8);
    var (ok, malos) = geo ? CeldaCoincide(fb, v.Width, 49, 0, 8, esperado) : (false, -1);
    Caso("b) AH=11h AL=12h: 50 filas de 8 px", geo && ok,
         $"BDA 40:84={filasMenos1} 40:85={alturaBda} CRTC alto={alturaCrtc} filas={filas} " +
         $"fb={v.Width}x{v.Height} pixeles malos en la fila 49 (392..399) = {malos}");

    // ── (e) AH=11h AL=30: altura y filas REALES, no constantes ────────────────────────────
    cpu.AH = 0x11; cpu.AL = 0x30; cpu.BH = 0x06;
    cpu.CX = 0xFFFF; cpu.DX = 0xFFFF;
    platform.Int(0x10, cpu, mem);
    bool okE = cpu.CX == 8 && cpu.DL == 49;
    Caso("e) AH=11h AL=30 tras pasar a 50 filas", okE, $"CX={cpu.CX} DL={cpu.DL} (se esperaba CX=8 DL=49)");
}

// El glifo distintivo: todas las filas a 0xFF menos la de abajo, que va a 0x00.
static byte[] GlifoDistintivo(int alto)
{
    var g = new byte[alto];
    for (int i = 0; i < alto - 1; i++) g[i] = 0xFF;
    return g;
}

// ── (c) fuente de usuario por AH=11h AL=00 ────────────────────────────────────────────────
{
    SetMode(0x03);
    var glifo = GlifoDistintivo(16);
    for (int i = 0; i < glifo.Length; i++) mem.WrU8(0x2000, (ushort)i, glifo[i]);

    cpu.AH = 0x11; cpu.AL = 0x00;
    cpu.ES = 0x2000; cpu.BP = 0x0000;
    cpu.CX = 1;          // un caracter
    cpu.DX = 0x0080;     // el primero es el 0x80
    cpu.BL = 0;          // bloque 0
    cpu.BH = 16;         // bytes por caracter
    platform.Int(0x10, cpu, mem);

    PonChar(3, 5, 0x80, 0x0F, 80);
    var fb = v.RenderFramebuffer();
    var (ok, malos) = CeldaCoincide(fb, v.Width, 3, 5, 16, glifo);
    Caso("c) fuente de usuario AH=11h AL=00 para el 0x80", ok, $"pixeles distintos = {malos}");
}

// ── (d) carga por PUERTOS al plano 2 ──────────────────────────────────────────────────────
{
    SetMode(0x03);
    var glifo = GlifoDistintivo(16);

    // Modo planar, tal cual INT10_LoadFont (dosbox-x src/ints/int10_memory.cpp:71-76).
    platform.PortOut8(0x3C4, 0x02); platform.PortOut8(0x3C5, 0x04); // plano 2 para escribir
    platform.PortOut8(0x3C4, 0x04); platform.PortOut8(0x3C5, 0x07); // odd/even off en el SEQ
    platform.PortOut8(0x3CE, 0x04); platform.PortOut8(0x3CF, 0x02); // plano 2 para leer
    platform.PortOut8(0x3CE, 0x05); platform.PortOut8(0x3CF, 0x00); // write mode 0, odd/even off
    platform.PortOut8(0x3CE, 0x06); platform.PortOut8(0x3CF, 0x04); // ventana A0000-AFFFF

    for (int i = 0; i < glifo.Length; i++)
        mem.WrU8(0xA000, (ushort)(0x80 * 32 + i), glifo[i]);

    // Vuelta al modo texto (int10_memory.cpp:87-92).
    platform.PortOut8(0x3C4, 0x02); platform.PortOut8(0x3C5, 0x03);
    platform.PortOut8(0x3C4, 0x04); platform.PortOut8(0x3C5, 0x03);
    platform.PortOut8(0x3CE, 0x04); platform.PortOut8(0x3CF, 0x00);
    platform.PortOut8(0x3CE, 0x05); platform.PortOut8(0x3CF, 0x10);
    platform.PortOut8(0x3CE, 0x06); platform.PortOut8(0x3CF, 0x0E);

    PonChar(7, 11, 0x80, 0x0F, 80);
    var fb = v.RenderFramebuffer();
    var (ok, malos) = CeldaCoincide(fb, v.Width, 7, 11, 16, glifo);
    Caso("d) carga por PUERTOS al plano 2", ok, $"pixeles distintos = {malos}");
}

// ── (f) ESTADO DE ARRANQUE: la fuente ya esta en el plano 2 sin pasar por INT 10h ─────────
// Un juego con `videoMode` en su game.json nunca llama al set-mode (el host se lo fija a mano,
// ver la plantilla en PakuPort.Cli/Program.cs), asi que si el plano 2 solo se llenara en el
// set-mode se quedaria vacio y no habria con que dibujar.
{
    var arranque = new VideoState();
    int malos = 0;
    for (int c = 0; c < 256; c++)
    for (int f = 0; f < 16; f++)
        if (arranque.Planes[2][c * 32 + f] != RomFonts.Font8x16[c * 16 + f]) malos++;
    Caso("f) estado de arranque: ROM 8x16 ya en el plano 2", malos == 0, $"bytes distintos = {malos}");
}

// ── Extra sin coste: Font8x8 de Int10hHandler vs int10_font_08 de DOSBox-X ────────────────
{
    int comunes = Math.Min(Int10hHandler.Font8x8.Length, RomFonts.Font8x8.Length);
    int malos = 0;
    for (int i = 0; i < comunes; i++)
        if (Int10hHandler.Font8x8[i] != RomFonts.Font8x8[i]) malos++;
    Console.WriteLine($"[NOTA ] Int10hHandler.Font8x8: {Int10hHandler.Font8x8.Length} bytes " +
                      $"({Int10hHandler.Font8x8.Length / 8} caracteres), {malos} bytes distintos " +
                      $"de int10_font_08 en el tramo comun. Solo se usa para texto en modos " +
                      $"GRAFICOS; el modo texto ya no la mira.");
}

Console.WriteLine(fallos == 0 ? "=== TODO VERDE ===" : $"=== {fallos} CASO(S) EN ROJO ===");
return fallos == 0 ? 0 : 1;
