namespace PakuPort.Core.Platform;

/// <summary>
/// BIOS Data Area (BDA). Segmento 0x0040, offset 0x0000 → lineal 0x0400.
/// Los campos se respaldan directamente en Memory.Raw para que el código del juego
/// que los lee vía memoria (ej. mov ax,[0x40:0x6C]) obtenga los valores correctos.
/// Adaptado de dosbox-x src/ints/bios.cpp y src/ints/bios_keyboard.cpp.
/// </summary>
public sealed class BiosDataArea
{
    private const int BdaBase = 0x0400; // lineal: 0x40:0x00

    private readonly Memory _m;

    public BiosDataArea(Memory m) => _m = m;

    // -------- helpers de acceso directo a Raw --------

    private byte  Rd8 (int off)         => _m.Raw[BdaBase + off];
    private void  Wr8 (int off, byte v)  => _m.Raw[BdaBase + off] = v;
    private ushort Rd16(int off)         => (ushort)(_m.Raw[BdaBase + off] | (_m.Raw[BdaBase + off + 1] << 8));
    private void   Wr16(int off, ushort v) { _m.Raw[BdaBase + off] = (byte)v; _m.Raw[BdaBase + off + 1] = (byte)(v >> 8); }
    private uint  Rd32(int off)          => (uint)(Rd16(off) | (Rd16(off + 2) << 16));
    private void  Wr32(int off, uint v)  { Wr16(off, (ushort)v); Wr16(off + 2, (ushort)(v >> 16)); }

    // -------- campos públicos --------

    /// <summary>0x40:0x10 — Equipment word (bit0=floppy, bit4-5=video, bit9-11=COM).</summary>
    public ushort EquipmentWord    { get => Rd16(0x10); set => Wr16(0x10, value); }

    /// <summary>0x40:0x13 — Memoria convencional en KB (640).</summary>
    public ushort MemorySizeKB     { get => Rd16(0x13); set => Wr16(0x13, value); }

    /// <summary>0x40:0x17 — Keyboard shift flags 1 (Ins/CapsLock/NumLock/ScrollLock/Alt/Ctrl/LShift/RShift).</summary>
    public byte KeyboardFlags1    { get => Rd8(0x17);  set => Wr8(0x17, value); }

    /// <summary>0x40:0x18 — Keyboard shift flags 2 (extended).</summary>
    public byte KeyboardFlags2    { get => Rd8(0x18);  set => Wr8(0x18, value); }

    /// <summary>0x40:0x3F — Estado del motor de disquete (bits de unidad activa).
    /// dosbox-x include/bios.h BIOS_DRIVE_RUNNING.</summary>
    public byte DriveRunning      { get => Rd8(0x3F);  set => Wr8(0x3F, value); }

    /// <summary>0x40:0x40 — Cuenta atrás del motor de disquete, decrementada por la ISR de
    /// INT 8 de la BIOS en cada tick. dosbox-x include/bios.h BIOS_DISK_MOTOR_TIMEOUT.</summary>
    public byte DiskMotorTimeout  { get => Rd8(0x40);  set => Wr8(0x40, value); }

    /// <summary>0x40:0x49 — Modo de vídeo actual (3 = texto 80x25 color).</summary>
    public byte   VideoMode        { get => Rd8(0x49);  set => Wr8(0x49, value); }

    /// <summary>0x40:0x4A — Columnas de pantalla (80).</summary>
    public ushort ScreenCols       { get => Rd16(0x4A); set => Wr16(0x4A, value); }

    /// <summary>0x40:0x4C — Tamaño en bytes de una página de vídeo del modo actual.</summary>
    public ushort VideoPageSize    { get => Rd16(0x4C); set => Wr16(0x4C, value); }

    /// <summary>0x40:0x4E — Offset de la página visible dentro de la VRAM.</summary>
    public ushort VideoPageStart   { get => Rd16(0x4E); set => Wr16(0x4E, value); }

    /// <summary>0x40:0x60 — Última línea de barrido del cursor de texto.</summary>
    public byte   CursorEnd        { get => Rd8(0x60);  set => Wr8(0x60, value); }

    /// <summary>0x40:0x61 — Primera línea de barrido del cursor de texto.</summary>
    public byte   CursorStart      { get => Rd8(0x61);  set => Wr8(0x61, value); }

    /// <summary>0x40:0x65 — Copia del registro de modo del CGA (puerto 3D8h). El BIOS sólo lo
    /// reescribe en los modos compatibles CGA (0-7); en los EGA/VGA conserva el valor del
    /// último de aquéllos, y por eso un juego que pasa por el modo 6 y salta al 0Dh lo deja
    /// en 3Eh. Medido contra el oráculo: red tools/bda_video.</summary>
    public byte   CrtModeControl   { get => Rd8(0x65);  set => Wr8(0x65, value); }

    /// <summary>0x40:0x62 — Página de vídeo activa (0).</summary>
    public byte   VideoPage        { get => Rd8(0x62);  set => Wr8(0x62, value); }

    /// <summary>0x40:0x63 — Base CRTC (0x3D4 para color, 0x3B4 para mono).</summary>
    public ushort CrtcBasePort     { get => Rd16(0x63); set => Wr16(0x63, value); }

    /// <summary>0x40:0x66 — Color select / paleta actual (dosbox-x include/bios.h
    /// BIOSMEM_CURRENT_PAL). Bit 5 = paleta CGA elegida, bit 4 = intensidad, bits 0-3 = color
    /// de fondo. De aquí sacan su valor `INT10_SetColorSelect` y `INT10_SetBackgroundBorder`
    /// para recalcular los registros 1..3 del Attribute Controller en los modos CGA.</summary>
    public byte   CurrentPalette   { get => Rd8(0x66);  set => Wr8(0x66, value); }

    /// <summary>Acceso sin instancia al mismo byte: el handler de INT 10h recibe sólo
    /// VideoState y Memory, y AH=0B necesita leerlo y escribirlo.</summary>
    public static byte CurrentPaletteOf(Memory m) => m.Raw[BdaBase + 0x66];
    public static void SetCurrentPalette(Memory m, byte val) => m.Raw[BdaBase + 0x66] = val;

    /// <summary>0x40:0x84 — Filas de texto menos una (24 en los modos de 25 filas).</summary>
    public byte   VideoRowsMinus1  { get => Rd8(0x84);  set => Wr8(0x84, value); }

    /// <summary>0x40:0x85 — Altura en líneas de barrido del carácter del modo actual.</summary>
    public ushort CharHeight       { get => Rd16(0x85); set => Wr16(0x85, value); }

    /// <summary>0x40:0x87 — Flags de control EGA/VGA. El BIOS deja 0x60 tras un set-mode que
    /// limpia la pantalla, y añade el bit 7 cuando el juego pide NO limpiarla (AL bit 7).</summary>
    public byte   VideoCtl         { get => Rd8(0x87);  set => Wr8(0x87, value); }

    /// <summary>0x40:0x6C — Contador de ticks del timer (IRQ0, incrementado ~18.2/s).</summary>
    public uint   TimerTicks       { get => Rd32(0x6C); set => Wr32(0x6C, value); }

    /// <summary>0x40:0x70 — Flag de medianoche (se pone a 1 cuando TimerTicks desborda 0x1800B0).</summary>
    public byte   MidnightFlag     { get => Rd8(0x70);  set => Wr8(0x70, value); }

    // -------- buffer de teclado circular --------
    // KbBufHead y KbBufTail son offsets DENTRO del segmento 0x40 (no offsets en BdaBase).
    // El buffer ocupa 0x40:0x1E–0x3D (16 entradas de 2 bytes cada una).

    private const int KbBufStart = 0x1E; // offset en seg 0x40
    private const int KbBufEnd   = 0x3E; // offset en seg 0x40 (exclusivo)

    /// <summary>0x40:0x1A — Head del buffer de teclado (offset en seg 0x40).</summary>
    public ushort KbBufHead { get => Rd16(0x1A); set => Wr16(0x1A, value); }

    /// <summary>0x40:0x1C — Tail del buffer de teclado (offset en seg 0x40).</summary>
    public ushort KbBufTail { get => Rd16(0x1C); set => Wr16(0x1C, value); }

    /// <summary>True si hay al menos una tecla en el buffer.</summary>
    public bool HasKey => KbBufHead != KbBufTail;

    /// <summary>
    /// Inserta una entrada scancode+ASCII en el buffer circular.
    /// El parámetro es (scancode << 8) | ascii — el orden de la BDA.
    /// Devuelve false si el buffer está lleno.
    /// </summary>
    public bool PushKey(ushort scanAscii)
    {
        ushort tail = KbBufTail;
        ushort nextTail = (ushort)(tail + 2);
        if (nextTail >= KbBufEnd) nextTail = KbBufStart;
        if (nextTail == KbBufHead) return false; // lleno

        // escribir en BdaBase + tail
        _m.Raw[BdaBase + tail]     = (byte)(scanAscii & 0xFF);
        _m.Raw[BdaBase + tail + 1] = (byte)(scanAscii >> 8);
        KbBufTail = nextTail;
        return true;
    }

    /// <summary>
    /// Extrae una entrada del buffer. Devuelve false si está vacío.
    /// </summary>
    public bool PopKey(out ushort scanAscii)
    {
        if (!HasKey) { scanAscii = 0; return false; }
        ushort head = KbBufHead;
        scanAscii = (ushort)(_m.Raw[BdaBase + head] | (_m.Raw[BdaBase + head + 1] << 8));
        ushort nextHead = (ushort)(head + 2);
        if (nextHead >= KbBufEnd) nextHead = KbBufStart;
        KbBufHead = nextHead;
        return true;
    }

    /// <summary>
    /// Peek (sin consumir) la primera entrada del buffer.
    /// </summary>
    public bool PeekKey(out ushort scanAscii)
    {
        if (!HasKey) { scanAscii = 0; return false; }
        ushort head = KbBufHead;
        scanAscii = (ushort)(_m.Raw[BdaBase + head] | (_m.Raw[BdaBase + head + 1] << 8));
        return true;
    }

    /// <summary>
    /// Inicializa la BDA con valores típicos de arranque IBM PC/AT.
    /// Adaptado de DOSBox-X bios.cpp BIOS_SetupKeyboard / BIOS_SetupDisks.
    /// </summary>
    public void Init()
    {
        // Palabra de equipo idéntica al oráculo DOSBox-X (machine=svga_s3, verificado
        // con el arnés del barrido): 0x4467 = floppy(b0) +
        // FPU(b1) + PS/2 mouse(b2) + vídeo EGA/VGA(b4-5=00... aquí 10 heredado del POST
        // de DOSBox) + 2 floppies(b6-7) + 2 series(b9-11) + 1 paralelo(b14).
        EquipmentWord = 0x4467;
        MemorySizeKB  = 640;
        VideoMode     = 3;        // modo texto 80x25 color
        ScreenCols    = 80;
        VideoPage     = 0;
        CrtcBasePort  = 0x3D4;   // base CRTC color
        TimerTicks    = 0;
        MidnightFlag  = 0;
        KbBufHead     = KbBufStart;
        KbBufTail     = KbBufStart;
        // Flags de teclado BIOS (0x40:0x17): NumLock ON de serie, como DOSBox-X y el
        // BIOS AT real (el host de export ya asumía NumLock ON; la BDA debe decir lo mismo).
        _m.Raw[BdaBase + 0x17] = 0x20;

        // Identificación de la ROM BIOS al final del megabyte. No la escribía nadie, y hay
        // programas que la leen A PELO en vez de por INT 15h AH=C0 — los extensores DOS son
        // el caso claro: DOS/16M (varios juegos de prueba) decide con esto si la
        // máquina es "AT- or PS/2- compatible" antes de elegir cómo cambiar a modo protegido.
        // Valores copiados de DOSBox-X (src/ints/bios.cpp:12035-12055): fecha en F000:FFF5,
        // byte de modelo 0xFC (AT) en F000:FFFE y firma 0x55 en F000:FFFF.
        const string fechaBios = "01/01/92";
        for (int i = 0; i < fechaBios.Length; i++) _m.Raw[0xFFFF5 + i] = (byte)fechaBios[i];
        _m.Raw[0xFFFFE] = 0xFC;
        _m.Raw[0xFFFFF] = 0x55;
    }
}
