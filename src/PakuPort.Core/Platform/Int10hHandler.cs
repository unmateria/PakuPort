namespace PakuPort.Core.Platform;

/// <summary>
/// INT 10h — servicios de vídeo BIOS completos: text modes, CGA, EGA, VGA mode 13h.
/// Adaptado de dosbox-x src/ints/int10_char.cpp, int10_modes.cpp, int10_pal.cpp, int10_misc.cpp.
/// </summary>
public static class Int10hHandler
{
    private static byte _cursorRow, _cursorCol;
    private static byte _cursorStart = 6, _cursorEnd = 7;
    private static byte _activePage;

    public static void Register(DosPlatform p) => p.RegisterInt(0x10, (cpu, mem) => Handle(cpu, mem, p.Video));

    private static bool IsText(byte m) => m is 0 or 1 or 2 or 3 or 7;
    private static bool IsEga(byte m) => m is >= 0x0D and <= 0x12;
    private static int TextCols(byte m) => m is 0 or 1 ? 40 : 80;

    // Altura de carácter del modo (valor que el BIOS deja en BDA 0x485 al hacer set mode)
    private static ushort CharHeight(byte m) => m switch
    {
        0x00 or 0x01 or 0x02 or 0x03 or 0x07 => 16, // texto VGA: fuente 8x16
        0x0F or 0x10 => 14,                          // EGA 350 líneas
        0x11 or 0x12 => 16,                          // VGA 480 líneas
        _ => 8,                                      // CGA / mode 13h
    };

    private static int ScreenCols(byte m) => m switch
    {
        0 or 1 => 40, 4 or 5 => 40, 0x0D => 40, 0x13 => 40, _ => 80
    };

    private static void Handle(Cpu cpu, Memory mem, VideoState v)
    {
        switch (cpu.AH)
        {
            case 0x00: // Set video mode
            {
                byte mode = (byte)(cpu.AL & 0x7F);
                bool noClear = (cpu.AL & 0x80) != 0;
                if (VideoState.LogCrtcWrites) Console.Error.WriteLine($"[MODE] AL={cpu.AL:X2} mode={mode:X2}");
                v.QuitaModoSvga();   // el set-mode clásico saca del modo VESA (VBE spec)
                v.VideoMode = mode;
                switch (mode)
                {
                    case 0x00: case 0x01: v.Width = 360;  v.Height = 400; break;
                    case 0x02: case 0x03: v.Width = 720;  v.Height = 400; break;
                    case 0x04: case 0x05: v.Width = 320;  v.Height = 200; break;
                    case 0x06:            v.Width = 640;  v.Height = 200; break;
                    case 0x07:            v.Width = 720;  v.Height = 400; break;
                    case 0x0D:            v.Width = 320;  v.Height = 200; break;
                    case 0x0E:            v.Width = 640;  v.Height = 200; break;
                    case 0x0F:            v.Width = 640;  v.Height = 350; break;
                    case 0x10:            v.Width = 640;  v.Height = 350; break;
                    case 0x11:            v.Width = 640;  v.Height = 480; break;
                    case 0x12:            v.Width = 640;  v.Height = 480; break;
                    case 0x13:            v.Width = 320;  v.Height = 200; break;
                    default:              v.Width = 320;  v.Height = 200; break;
                }
                if (!noClear)
                {
                    if (mode == 0x06)
                    {
                        // Modo 6 = lineal en plano 0 (16K), no partido odd/even como 4/5.
                        Array.Clear(v.Planes[0], 0, 0x4000);
                        v.CgaPalette = 1; v.CgaBackground = 0;
                    }
                    else if (v.IsCga)
                    {
                        Array.Clear(v.Planes[0], 0, 0x2000);
                        Array.Clear(v.Planes[1], 0, 0x2000);
                        v.CgaPalette = 1; v.CgaBackground = 0;
                    }
                    else if (IsText(mode))
                    {
                        int cols = TextCols(mode);
                        for (int i = 0; i < cols * 25; i++)
                        {
                            v.Planes[0][i] = 0x20;     // space
                            v.Planes[1][i] = 0x07;     // light gray on black
                        }
                    }
                    else
                    {
                        ClearPlanes(v);
                    }
                }
                if (v.IsMode13h) v.LoadDefaultVgaPalette256();
                else v.LoadDefaultVgaPalette();
                // BIOS-default palette regs for 200-line text/EGA modes: {00-07,10-17}
                // (DOSBox-X int10_modes.cpp:2100-2106, att_data[ct]=ct; att_data[ct+8]=ct+0x10).
                // With the EGA-compatible 64-entry DAC, entries 8-15 are DARK duplicates —
                // identity regs would render bright attributes (8-15) dark (text mode washed out).
                for (int i = 0; i < 8; i++) { v.AttrRegs[i] = (byte)i; v.AttrRegs[i + 8] = (byte)(i + 0x10); }
                // Modo 6: att_data[0]=0, att_data[1..15]=0x17 (blanco) — int10_modes.cpp:2156-2161.
                if (mode == 0x06) { v.AttrRegs[0] = 0; for (int i = 1; i < 16; i++) v.AttrRegs[i] = 0x17; }
                // Modos 4/5 (M_CGA4): en una EGA/VGA los 2 bits del píxel CGA pasan por el
                // Attribute Controller, así que sus cuatro colores NO son fijos — el BIOS los
                // programa aquí y el juego los puede recolorear luego con AH=0B (un juego de
                // prueba lo hace: sin esto su título salía en cian/magenta donde el original va
                // en azul/rojo). Tabla literal de int10_modes.cpp:2163-2177, case M_CGA4.
                else if (mode == 0x04 || mode == 0x05)
                {
                    v.AttrRegs[0] = 0x00; v.AttrRegs[1] = 0x13; v.AttrRegs[2] = 0x15; v.AttrRegs[3] = 0x17;
                    v.AttrRegs[4] = 0x02; v.AttrRegs[5] = 0x04; v.AttrRegs[6] = 0x06; v.AttrRegs[7] = 0x07;
                    for (int ct = 8; ct < 16; ct++) v.AttrRegs[ct] = (byte)(ct + 8);
                }
                // ── BDA de vídeo (int10_modes.cpp, "Setup the BIOS" de FinishSetMode) ────
                // No la escribía NADIE: el set-mode cambiaba el estado interno del emulador y
                // dejaba estos campos con los valores del arranque, así que un juego que le
                // pregunte al BIOS en qué modo está, cuántas filas hay o cuánto mide la fuente
                // —por AH=0F, por AH=11 AL=30 o leyendo 0040:0049 a pelo, que es lo más común—
                // recibía una respuesta falsa. Medido con un juego EGA ya pintando en modo 0Dh:
                // 9 de 18 campos distintos del oráculo. Red: tools/bda_video (67 → 0).
                SetupVideoBda(mem, mode, noClear);
                v.AttrFlipFlop = false;
                // Program GC/Seq registers as a real BIOS would (dosbox-x int10_modes.cpp)
                SetModeRegisters(v, mode);
                // LA FUENTE DE TEXTO VIVE EN LA VRAM, no en una tabla del emulador: en una VGA
                // son 32 bytes por caracter en el PLANO 2, y el BIOS la carga ahi al final de
                // todo set-mode de texto (dosbox-x int10_modes.cpp:2519 INT10_ReloadFont). Sin
                // esto el plano 2 quedaba a cero y no habia con que dibujar, asi que el render
                // tiraba de una tabla propia y cualquier fuente cargada por el programa —la de
                // un tracker de música, por ejemplo— no se veia nunca.
                if (IsText(mode))
                {
                    int altoFuente = CharHeight(mode);
                    LoadFont(v, mem,
                             altoFuente == 8  ? RomFonts.Font8x8
                           : altoFuente == 14 ? RomFonts.Font8x14
                                              : RomFonts.Font8x16,
                             reload: false, count: 256, offset: 0, map: 0, height: altoFuente);
                }
                _cursorRow = 0; _cursorCol = 0;
                _activePage = 0;
                // Drop the captured frame so a mode change (e.g. graphics->text) is not
                // briefly presented on a stale frame from the previous mode.
                v.ResetFrontBuffer();
                // El driver de ratón real reajusta rango/granularidad con el modo nuevo.
                v.ModeChanged?.Invoke(mode);
                break;
            }

            case 0x01: // Set cursor shape
                _cursorStart = (byte)(cpu.CH & 0x1F);
                _cursorEnd = (byte)(cpu.CL & 0x1F);
                break;

            case 0x02: // Set cursor position
                _cursorRow = cpu.DH;
                _cursorCol = cpu.DL;
                break;

            case 0x03: // Get cursor position and shape
                cpu.DH = _cursorRow; cpu.DL = _cursorCol;
                cpu.CH = _cursorStart; cpu.CL = _cursorEnd;
                break;

            case 0x04: // Light pen
                cpu.AH = 0;
                break;

            case 0x05: // Set active display page
                _activePage = cpu.AL;
                break;

            case 0x06: // Scroll up
                Scroll(cpu, v, up: true);
                break;

            case 0x07: // Scroll down
                Scroll(cpu, v, up: false);
                break;

            case 0x08: // Read character/attribute at cursor
                if (IsText(v.VideoMode))
                {
                    int cols = TextCols(v.VideoMode);
                    int i = _cursorRow * cols + _cursorCol;
                    if ((uint)i < VideoState.PlaneSize)
                    {
                        cpu.AL = v.Planes[0][i];
                        cpu.AH = v.Planes[1][i];
                    }
                    else { cpu.AL = 0; cpu.AH = 0; }
                }
                else
                {
                    cpu.AH = 0; cpu.AL = 0;
                }
                break;

            case 0x09: // Write character/attribute at cursor (CX = count)
                WriteCharAtCursor(cpu.AL, cpu.BL, cpu.CX, v, withAttr: true, back: cpu.BH);
                break;

            case 0x0A: // Write character only at cursor (CX = count)
                WriteCharAtCursor(cpu.AL, cpu.BL, cpu.CX, v, withAttr: false, back: cpu.BH);
                break;

            case 0x0B: // Set CGA palette / background — dosbox-x int10_pal.cpp
                // Se conservan CgaBackground/CgaPalette (los lee el puerto 0x3D9 y el modo 6),
                // pero en EGA/VGA lo que manda el color de los modos 4/5 son los registros
                // 1..3 del Attribute Controller, recalculados desde la BDA 40:66.
                //
                // Se aplica a TODO modo gráfico, como dosbox-x (`if (CurMode->mode <= 3)
                // return;` en int10_pal.cpp). Una versión anterior lo dejó acotado a los modos 4/5 porque
                // aplicarlo a los EGA de 16 colores puso turquesa el logo de un juego EGA, que en
                // el oráculo es verde; la causa no estaba aquí sino en que la BDA de vídeo no
                // la escribía nadie (40:49 decía "modo 3" con el juego pintando en 0Dh, y el
                // color de estos registros se deriva de 40:66). Con la BDA ya correcta —red
                // tools/bda_video, 67 campos mal → 0— se volvió a quitar la acotación y se
                // midió: ese logo no cambia ni un píxel, y es **idéntico al oráculo,
                // 64000/64000**. La red CGAPAL sigue verde (AC 48/48, DAC 192/192).
                bool cgaGraphics = v.VideoMode > 0x03;
                if (cpu.BH == 0x00)
                {
                    v.CgaBackground = cpu.BL;
                    // INT10_SetBackgroundBorder (int10_pal.cpp:369-400), rama EGAVGA
                    byte sel = (byte)((BiosDataArea.CurrentPaletteOf(mem) & 0xE0) | (cpu.BL & 0x1F));
                    BiosDataArea.SetCurrentPalette(mem, sel);
                    byte back = (byte)(((cpu.BL << 1) & 0x10) | (cpu.BL & 0x07));
                    if (cgaGraphics)
                    {
                        v.AttrRegs[0] = (byte)(back & 0x3F);
                        byte val = (byte)((sel & 0x10) | 2 | ((sel & 0x20) >> 5));
                        v.AttrRegs[1] = (byte)(val & 0x3F);
                        v.AttrRegs[2] = (byte)((val + 2) & 0x3F);
                        v.AttrRegs[3] = (byte)((val + 4) & 0x3F);
                    }
                }
                else if (cpu.BH == 0x01)
                {
                    v.CgaPalette = cpu.BL;
                    // INT10_SetColorSelect (int10_pal.cpp:434-475), rama EGAVGA
                    byte temp = (byte)((BiosDataArea.CurrentPaletteOf(mem) & 0xDF) | ((cpu.BL & 1) != 0 ? 0x20 : 0));
                    BiosDataArea.SetCurrentPalette(mem, temp);
                    if (cgaGraphics)
                    {
                        byte val = (byte)((temp & 0x10) | 2 | cpu.BL);
                        v.AttrRegs[1] = (byte)(val & 0x3F);
                        v.AttrRegs[2] = (byte)((val + 2) & 0x3F);
                        v.AttrRegs[3] = (byte)((val + 4) & 0x3F);
                    }
                }
                break;

            case 0x0C: // Write pixel
                WritePixel(cpu.CX, cpu.DX, cpu.AL, v);
                break;

            case 0x0D: // Read pixel
                cpu.AL = ReadPixel(cpu.CX, cpu.DX, v);
                break;

            case 0x0E: // Teletype write
                // dosbox-x int10_char.cpp INT10_TeletypeOutput: in text modes the BIOS
                // teletype writes the character ONLY (cell attribute preserved);
                // useattr = CurMode->type != M_TEXT.
                Teletype(cpu.AL, cpu.BL, v, useAttr: !IsText(v.VideoMode), back: cpu.BH);
                break;

            case 0x0F: // Get video mode
                cpu.AL = v.VideoMode;
                cpu.AH = (byte)ScreenCols(v.VideoMode);
                cpu.BH = _activePage;
                break;

            case 0x10: // Palette/DAC
                HandlePalette(cpu, mem, v);
                break;

            case 0x11: // Character generator
                HandleCharGen(cpu, mem, v);
                break;

            case 0x12: // Alternate select — int10.cpp (tabla completa BL=10..36)
                switch (cpu.BL)
                {
                    case 0x10: // Get EGA info
                        cpu.BH = 0x00; // color mode (CRTC 3D4)
                        cpu.BL = 0x03; // 256KB video memory
                        // int10.cpp: CL/CH = BDA switches (0x40:0x88 = 0xF9 en VGA)
                        cpu.CL = 0x09;
                        cpu.CH = 0x0F;
                        break;
                    case 0x20: // Set alternate printscreen — no-op (int10.cpp)
                        break;
                    case 0x30: // Select vertical resolution (efectivo en el próximo set mode)
                    case 0x31: // Palette loading on modeset on/off
                    case 0x32: // Video addressing on/off
                    case 0x33: // Gray-scale summing on/off
                    case 0x34: // Cursor emulation on/off
                    case 0x35: // Display switch
                    case 0x36: // Refresh control on/off
                        // int10.cpp: el estado se anota y devuelve AL=12h (función soportada)
                        cpu.AL = 0x12;
                        break;
                    // BL desconocido: sin cambios (igual que int10.cpp)
                }
                break;

            case 0x13: // Write string
            {
                int ptr = Memory.Lin(cpu.ES, cpu.BP);
                bool moveAttr = (cpu.AL & 2) != 0;
                bool moveCursor = (cpu.AL & 1) != 0;
                byte attr = cpu.BL;
                byte savedRow = _cursorRow, savedCol = _cursorCol;
                _cursorRow = cpu.DH; _cursorCol = cpu.DL;
                for (int i = 0; i < cpu.CX; i++)
                {
                    byte ch = mem.Raw[ptr++];
                    if (moveAttr) attr = mem.Raw[ptr++];
                    Teletype(ch, attr, v);
                }
                if (!moveCursor) { _cursorRow = savedRow; _cursorCol = savedCol; }
                break;
            }

            case 0x1A: // Get/set display combination code
                if (cpu.AL < 2)
                {
                    cpu.BL = 0x08; // VGA + color analog
                    cpu.BH = 0x00;
                    cpu.AX = 0x001A; // int10.cpp: reg_ax=0x1A (AH destruido/cero)
                }
                break;

            case 0x1B: // Functionality/state info
                if (cpu.BX == 0x0000)
                {
                    int dst = Memory.Lin(cpu.ES, cpu.DI);
                    for (int i = 0; i < 64; i++) mem.Raw[dst + i] = 0;
                    mem.Raw[dst + 0x04] = v.VideoMode;
                    mem.Raw[dst + 0x05] = (byte)ScreenCols(v.VideoMode);
                    mem.Raw[dst + 0x06] = 0;
                    mem.Raw[dst + 0x22] = 24; // rows-1
                    mem.Raw[dst + 0x23] = 16; mem.Raw[dst + 0x24] = 0;
                    mem.Raw[dst + 0x25] = 0x08;
                    ushort colors = v.IsMode13h ? (ushort)256 : v.IsCga ? (ushort)4 : (ushort)16;
                    mem.Raw[dst + 0x27] = (byte)(colors & 0xFF);
                    mem.Raw[dst + 0x28] = (byte)(colors >> 8);
                    mem.Raw[dst + 0x29] = 1;
                    mem.Raw[dst + 0x2A] = v.Height switch { 200 => 0, 350 => 1, 400 => 2, _ => 3 };
                    mem.Raw[dst + 0x2D] = 0x01;
                    mem.Raw[dst + 0x31] = 0x03;
                    cpu.AL = 0x1B;
                }
                break;

            case 0x1C: // Save/restore video state
                if (cpu.AL == 0x00) { cpu.BX = 0x0010; }
                cpu.AL = 0x1C;
                break;

            // ── VBE (VESA BIOS Extension) 2.0 ─────────────────────────────────────────────
            // Modelo DOSBox (int10_vesa.cpp): modos de 8 bpp con memoria LINEAL vista por la
            // ventana bancada de 64 KiB en A000. Convención de retorno: AL=4F "función
            // soportada", AH=0 éxito / 1 fallo. Primer cliente: un juego DOS/4GW Pro, que sin VBE
            // aborta con "could not find a VESA video driver" (su UVBELib embebida detecta un
            // "Cirrus" fantasma en los puertos y no arranca; con un VBE 2.0 de BIOS usa esto).
            case 0x4F:
                HandleVbe(cpu, mem, v);
                break;
        }
    }

    /// <summary>Modos VESA servidos: número VBE → (ancho, alto). Todos de 8 bpp empaquetado.</summary>
    private static readonly (ushort modo, ushort w, ushort h)[] ModosVbe =
    {
        (0x100, 640, 400), (0x101, 640, 480), (0x103, 800, 600),
    };

    /// <summary>PAKU_LOG_VBE=1: traza cada llamada VBE (función, registros, buffer).</summary>
    private static readonly bool _logVbe = Environment.GetEnvironmentVariable("PAKU_LOG_VBE") == "1";

    private static void HandleVbe(Cpu cpu, Memory mem, VideoState v)
    {
        if (_logVbe) Console.Error.WriteLine(
            $"[VBE] 4F{cpu.AL:X2} BX={cpu.BX:X4} CX={cpu.CX:X4} DX={cpu.DX:X4} ES:DI={cpu.ES:X4}:{cpu.DI:X4}");
        switch (cpu.AL)
        {
            case 0x00: // VbeInfoBlock en ES:DI
            {
                int b = Memory.Lin(cpu.ES, cpu.DI);
                for (int i = 0; i < 256; i++) mem.Raw[b + i] = 0;
                mem.Raw[b + 0] = (byte)'V'; mem.Raw[b + 1] = (byte)'E';
                mem.Raw[b + 2] = (byte)'S'; mem.Raw[b + 3] = (byte)'A';
                W16(mem, b + 0x04, 0x0200);                       // VBE 2.0
                // OEM string y lista de modos DENTRO del propio bloque (área reservada),
                // como hace DOSBox: los punteros far apuntan a ES:DI+offset.
                W32(mem, b + 0x06, ((uint)cpu.ES << 16) | (ushort)(cpu.DI + 0xD0));
                W32(mem, b + 0x0A, 0);                            // capabilities: DAC de 6 bits
                W32(mem, b + 0x0E, ((uint)cpu.ES << 16) | (ushort)(cpu.DI + 0x22));
                W16(mem, b + 0x12, 16);                           // 16 x 64 KiB = 1 MiB
                int p = b + 0x22;
                foreach (var (modo, _, _) in ModosVbe) { W16(mem, p, modo); p += 2; }
                W16(mem, p, 0xFFFF);
                var oem = System.Text.Encoding.ASCII.GetBytes("PakuPort VBE\0");
                oem.CopyTo(mem.Raw, b + 0xD0);
                cpu.AX = 0x004F;
                break;
            }
            case 0x01: // ModeInfoBlock del modo CX en ES:DI
            {
                int idx = Array.FindIndex(ModosVbe, e => e.modo == (cpu.CX & 0x1FF));
                if (idx < 0) { cpu.AX = 0x014F; break; }
                var (_, w, h) = ModosVbe[idx];
                int b = Memory.Lin(cpu.ES, cpu.DI);
                for (int i = 0; i < 256; i++) mem.Raw[b + i] = 0;
                W16(mem, b + 0x00, 0x001B);   // soportado, color, gráfico; SIN framebuffer lineal
                mem.Raw[b + 0x02] = 0x07;     // ventana A: reubicable, legible, escribible
                W16(mem, b + 0x04, 64);       // granularidad (KiB)
                W16(mem, b + 0x06, 64);       // tamaño de ventana (KiB)
                W16(mem, b + 0x08, 0xA000);   // segmento de la ventana A
                // WinFuncPtr: el código VESA de Watcom LLAMA a este puntero para cambiar de
                // banco en vez de pasar por INT 10h AX=4F05 (medido en un juego de prueba: con un
                // puntero nulo el juego saltaba a la lineal 0 y ejecutaba la IVT). El stub es
                // `int 10h; retf` en la zona BIOS, detrás de los puntos de entrada del DPMI.
                mem.Raw[0xF0000 + 0x140A] = 0xCD; mem.Raw[0xF0000 + 0x140B] = 0x10;
                mem.Raw[0xF0000 + 0x140C] = 0xCB;
                W32(mem, b + 0x0C, 0xF000_140A);
                W16(mem, b + 0x10, w);        // bytes por línea
                W16(mem, b + 0x12, w); W16(mem, b + 0x14, h);
                mem.Raw[b + 0x16] = 8; mem.Raw[b + 0x17] = 16;   // celda de carácter
                mem.Raw[b + 0x18] = 1;        // planos
                mem.Raw[b + 0x19] = 8;        // bpp
                mem.Raw[b + 0x1A] = 1;        // bancos
                mem.Raw[b + 0x1B] = 4;        // modelo de memoria: packed pixel
                mem.Raw[b + 0x1D] = (byte)Math.Max(0, (1 << 20) / (w * h) - 1);   // páginas
                mem.Raw[b + 0x1E] = 1;        // reservado, siempre 1
                cpu.AX = 0x004F;
                break;
            }
            case 0x02: // Set VBE mode: BX = modo (bit14 LFB se ignora, bit15 = no limpiar)
            {
                ushort pedida = (ushort)(cpu.BX & 0x1FF);
                if (pedida < 0x100)
                {
                    // Modo clásico pedido por la puerta VESA: se delega en el set-mode normal.
                    cpu.AH = 0; cpu.AL = (byte)(pedida | ((cpu.BX & 0x8000) >> 8));
                    Handle(cpu, mem, v);
                    cpu.AX = 0x004F;
                    break;
                }
                int idx2 = Array.FindIndex(ModosVbe, e => e.modo == pedida);
                if (idx2 < 0) { cpu.AX = 0x014F; break; }
                var m2 = ModosVbe[idx2];
                v.PonModoSvga(pedida, m2.w, m2.h, limpiar: (cpu.BX & 0x8000) == 0);
                v.LoadDefaultVgaPalette256();
                mem.Raw[0x449] = 0x62;        // BDA: modo de vídeo "SVGA" (valor de DOSBox)
                v.ResetFrontBuffer();
                v.ModeChanged?.Invoke(0x62);
                cpu.AX = 0x004F;
                break;
            }
            case 0x03: // Get current VBE mode
                cpu.BX = v.SvgaActivo ? v.SvgaModo : mem.Raw[0x449];
                cpu.AX = 0x004F;
                break;
            case 0x05: // Window control: BH=0 set / BH=1 get, BL=ventana, DX=banco (granularidad)
                if (cpu.BH == 0) { v.SvgaBank = cpu.DX; cpu.AX = 0x004F; }
                else if (cpu.BH == 1) { cpu.DX = (ushort)v.SvgaBank; cpu.AX = 0x004F; }
                else cpu.AX = 0x014F;
                break;
            case 0x06: // Logical scan line: BL=0 set en píxeles / 1 get / 2 set en bytes
                if (!v.SvgaActivo) { cpu.AX = 0x014F; break; }
                if (cpu.BL == 0) v.SvgaStride = cpu.CX;         // 8 bpp: 1 píxel = 1 byte
                else if (cpu.BL == 2) v.SvgaStride = cpu.CX;
                else if (cpu.BL != 1) { cpu.AX = 0x014F; break; }
                cpu.BX = (ushort)v.SvgaStride;                  // bytes por línea
                cpu.CX = (ushort)v.SvgaStride;                  // píxeles por línea (8 bpp)
                cpu.DX = (ushort)((1 << 20) / Math.Max(1, v.SvgaStride));   // líneas máximas
                cpu.AX = 0x004F;
                break;
            case 0x07: // Display start: BH=0 BL=0 set (CX=x, DX=y) / BL=1 get
                if (!v.SvgaActivo) { cpu.AX = 0x014F; break; }
                if (cpu.BL == 0) { v.SvgaStart = cpu.DX * v.SvgaStride + cpu.CX; cpu.AX = 0x004F; }
                else if (cpu.BL == 1)
                {
                    cpu.CX = (ushort)(v.SvgaStart % Math.Max(1, v.SvgaStride));
                    cpu.DX = (ushort)(v.SvgaStart / Math.Max(1, v.SvgaStride));
                    cpu.AX = 0x004F;
                }
                else cpu.AX = 0x014F;
                break;
            case 0x08: // DAC palette format: sólo 6 bits por componente
                cpu.BH = 6; cpu.AX = 0x004F;
                break;
            case 0x09: // Set/get palette data. Entradas de 4 bytes en orden B,G,R,align (¡al
                       // revés que los puertos 3C8/3C9!). Sin esto un juego de prueba saltaba a un
                       // puntero alternativo nulo tras el fallo y ejecutaba la lineal 0.
            {
                int n = cpu.CX, ini = cpu.DX;
                int t = Memory.Lin(cpu.ES, cpu.DI);
                if (cpu.BL == 0x00 || cpu.BL == 0x80)
                {
                    for (int j = 0; j < n && ini + j < 256; j++)
                    {
                        v.Dac[(ini + j) * 3 + 2] = (byte)(mem.Raw[t + j * 4 + 0] & 0x3F);   // B
                        v.Dac[(ini + j) * 3 + 1] = (byte)(mem.Raw[t + j * 4 + 1] & 0x3F);   // G
                        v.Dac[(ini + j) * 3 + 0] = (byte)(mem.Raw[t + j * 4 + 2] & 0x3F);   // R
                    }
                    cpu.AX = 0x004F;
                }
                else if (cpu.BL == 0x01)
                {
                    for (int j = 0; j < n && ini + j < 256; j++)
                    {
                        mem.Raw[t + j * 4 + 0] = v.Dac[(ini + j) * 3 + 2];
                        mem.Raw[t + j * 4 + 1] = v.Dac[(ini + j) * 3 + 1];
                        mem.Raw[t + j * 4 + 2] = v.Dac[(ini + j) * 3 + 0];
                        mem.Raw[t + j * 4 + 3] = 0;
                    }
                    cpu.AX = 0x004F;
                }
                else cpu.AX = 0x014F;
                break;
            }
            case 0x0A: // Interfaz de MODO PROTEGIDO (VBE 2.0): tabla + código de 32 bits que el
                       // cliente copia a su segmento y llama en near. Los stubs trabajan por
                       // PUERTOS (banco en 0xE998, display start en 0xE99A/0xE99C, paleta por el
                       // DAC 3C8/3C9 de toda la vida — VgaPortHandler). Sin esto un juego de prueba
                       // guardaba un puntero NULO como "cambiar de banco" y lo llamaba: salto a
                       // la lineal 0 y a ejecutar la IVT como código.
            {
                if (cpu.BL != 0) { cpu.AX = 0x014F; break; }
                byte[] blk =
                {
                    // tabla de offsets (desde el inicio del bloque)
                    0x08, 0x00,   // set window
                    0x11, 0x00,   // set display start
                    0x22, 0x00,   // set primary palette
                    0x00, 0x00,   // sin lista de recursos
                    // +0x08 set window: in DX=banco (AX y DX se pueden destruir, VBE spec)
                    0x66, 0x92,                     // xchg ax,dx
                    0x66, 0xBA, 0x98, 0xE9,         // mov dx,0E998h
                    0x66, 0xEF,                     // out dx,ax
                    0xC3,                           // ret
                    // +0x11 set display start: in CX=x, DX=y
                    0x66, 0x8B, 0xC2,               // mov ax,dx
                    0x66, 0xBA, 0x9C, 0xE9,         // mov dx,0E99Ch
                    0x66, 0xEF,                     // out dx,ax (y)
                    0x66, 0x8B, 0xC1,               // mov ax,cx
                    0x66, 0xBA, 0x9A, 0xE9,         // mov dx,0E99Ah
                    0x66, 0xEF,                     // out dx,ax (x)
                    0xC3,                           // ret
                    // +0x22 set palette: in CX=n, DX=inicio, ES:EDI=tabla (B,G,R,align)
                    0x51,                           // push ecx
                    0x57,                           // push edi
                    0x66, 0x8B, 0xC2,               // mov ax,dx
                    0x66, 0xBA, 0xC8, 0x03,         // mov dx,3C8h
                    0xEE,                           // out dx,al (índice)
                    0x42,                           // inc edx -> 3C9
                    0x26, 0x8A, 0x47, 0x02,         // bucle: mov al,es:[edi+2] (R)
                    0xEE,
                    0x26, 0x8A, 0x47, 0x01,         // mov al,es:[edi+1] (G)
                    0xEE,
                    0x26, 0x8A, 0x07,               // mov al,es:[edi] (B)
                    0xEE,
                    0x83, 0xC7, 0x04,               // add edi,4
                    0x66, 0x49,                     // dec cx
                    0x75, 0xEB,                     // jnz bucle
                    0x5F,                           // pop edi
                    0x59,                           // pop ecx
                    0xC3,                           // ret
                };
                blk.CopyTo(mem.Raw, 0xF0000 + 0x1410);
                cpu.ES = 0xF000; cpu.DI = 0x1410;
                cpu.CX = (ushort)blk.Length;
                cpu.AX = 0x004F;
                break;
            }
            default:
                cpu.AX = 0x014F;   // función VBE no soportada (AL=4F reconocido, AH=1 fallo)
                break;
        }
    }

    private static void W16(Memory mem, int a, ushort v) { mem.Raw[a] = (byte)v; mem.Raw[a + 1] = (byte)(v >> 8); }
    private static void W32(Memory mem, int a, uint v) { W16(mem, a, (ushort)v); W16(mem, a + 2, (ushort)(v >> 16)); }

    // ── Pixel operations for all modes ──────────────────────────────────────────

    private static void WritePixel(ushort x, ushort y, byte color, VideoState v)
    {
        if (v.IsCga) { CgaWritePixel(x, y, color, v); return; }
        if (v.IsMode13h)
        {
            if (x >= 320 || y >= 200) return;
            int off = y * 320 + x;
            if ((color & 0x80) != 0)
                v.Planes[0][off] ^= (byte)(color & 0x7F);
            else
                v.Planes[0][off] = color;
            return;
        }
        if (IsEga(v.VideoMode))
        {
            if (x >= v.Width || y >= v.Height) return;
            int byteIdx = y * (v.Width / 8) + (x / 8);
            int bitIdx = 7 - (x & 7);
            if ((color & 0x80) != 0)
            {
                byte c = (byte)(color & 0x0F);
                for (int p = 0; p < 4; p++)
                    v.Planes[p][byteIdx] ^= (byte)(((c >> p) & 1) << bitIdx);
            }
            else
            {
                for (int p = 0; p < 4; p++)
                {
                    if (((color >> p) & 1) != 0)
                        v.Planes[p][byteIdx] |= (byte)(1 << bitIdx);
                    else
                        v.Planes[p][byteIdx] &= (byte)~(1 << bitIdx);
                }
            }
        }
    }

    private static byte ReadPixel(ushort x, ushort y, VideoState v)
    {
        if (v.IsCga) return CgaReadPixel(x, y, v);
        if (v.IsMode13h)
        {
            if (x >= 320 || y >= 200) return 0;
            return v.Planes[0][y * 320 + x];
        }
        if (IsEga(v.VideoMode))
        {
            if (x >= v.Width || y >= v.Height) return 0;
            int byteIdx = y * (v.Width / 8) + (x / 8);
            int bitIdx = 7 - (x & 7);
            byte color = 0;
            for (int p = 0; p < 4; p++)
                color |= (byte)(((v.Planes[p][byteIdx] >> bitIdx) & 1) << p);
            return color;
        }
        return 0;
    }

    // ── Scroll for all modes ────────────────────────────────────────────────────

    private static void Scroll(Cpu cpu, VideoState v, bool up)
    {
        if (v.IsCga) { CgaScroll(cpu, v, up); return; }
        if (IsText(v.VideoMode)) { TextScroll(cpu, v, up); return; }
        if (v.IsMode13h) { Mode13hScroll(cpu, v, up); return; }
        if (IsEga(v.VideoMode)) { EgaScroll(cpu, v, up); return; }
    }

    private static void TextScroll(Cpu cpu, VideoState v, bool up)
    {
        int cols = TextCols(v.VideoMode);
        int lines = cpu.AL;
        int top = cpu.CH, left = cpu.CL;
        int bottom = cpu.DH, right = cpu.DL;
        if (right >= cols) right = cols - 1;
        if (bottom >= 25) bottom = 24;
        byte fillAttr = cpu.BH;
        int width = right - left + 1;
        if (width <= 0) return;

        if (lines == 0)
        {
            for (int r = top; r <= bottom; r++)
                for (int c = left; c <= right; c++)
                {
                    int i = r * cols + c;
                    v.Planes[0][i] = 0x20;
                    v.Planes[1][i] = fillAttr;
                }
            return;
        }

        if (up)
        {
            for (int r = top; r <= bottom - lines; r++)
                for (int c = left; c <= right; c++)
                {
                    int di = r * cols + c;
                    int si = (r + lines) * cols + c;
                    v.Planes[0][di] = v.Planes[0][si];
                    v.Planes[1][di] = v.Planes[1][si];
                }
            for (int r = bottom - lines + 1; r <= bottom; r++)
                for (int c = left; c <= right; c++)
                {
                    int i = r * cols + c;
                    v.Planes[0][i] = 0x20;
                    v.Planes[1][i] = fillAttr;
                }
        }
        else
        {
            for (int r = bottom; r >= top + lines; r--)
                for (int c = left; c <= right; c++)
                {
                    int di = r * cols + c;
                    int si = (r - lines) * cols + c;
                    v.Planes[0][di] = v.Planes[0][si];
                    v.Planes[1][di] = v.Planes[1][si];
                }
            for (int r = top; r < top + lines; r++)
                for (int c = left; c <= right; c++)
                {
                    int i = r * cols + c;
                    v.Planes[0][i] = 0x20;
                    v.Planes[1][i] = fillAttr;
                }
        }
    }

    private static void Mode13hScroll(Cpu cpu, VideoState v, bool up)
    {
        int lines = cpu.AL;
        int top = cpu.CH * 8, bottom = (cpu.DH + 1) * 8;
        int left = cpu.CL * 8, right = (cpu.DL + 1) * 8;
        if (bottom > 200) bottom = 200;
        if (right > 320) right = 320;
        int pixelShift = lines * 8;

        if (lines == 0)
        {
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                    v.Planes[0][y * 320 + x] = 0;
            return;
        }

        if (up)
        {
            for (int y = top; y < bottom - pixelShift; y++)
                Array.Copy(v.Planes[0], (y + pixelShift) * 320 + left,
                           v.Planes[0], y * 320 + left, right - left);
            for (int y = bottom - pixelShift; y < bottom; y++)
                Array.Fill(v.Planes[0], (byte)0, y * 320 + left, right - left);
        }
        else
        {
            for (int y = bottom - 1; y >= top + pixelShift; y--)
                Array.Copy(v.Planes[0], (y - pixelShift) * 320 + left,
                           v.Planes[0], y * 320 + left, right - left);
            for (int y = top; y < top + pixelShift; y++)
                Array.Fill(v.Planes[0], (byte)0, y * 320 + left, right - left);
        }
    }

    private static void EgaScroll(Cpu cpu, VideoState v, bool up)
    {
        int rowPitch = v.Width / 8;
        int lines = cpu.AL;
        int top = cpu.CH * 14, bottom = (cpu.DH + 1) * 14;
        int left = cpu.CL, right = cpu.DL + 1;
        if (bottom > v.Height) bottom = v.Height;
        if (right > rowPitch) right = rowPitch;
        int pixelShift = lines * 14;
        int width = right - left;
        if (width <= 0) return;

        if (lines == 0)
        {
            for (int p = 0; p < 4; p++)
                for (int y = top; y < bottom; y++)
                    Array.Fill(v.Planes[p], (byte)0, y * rowPitch + left, width);
            return;
        }

        if (up)
        {
            for (int p = 0; p < 4; p++)
            {
                for (int y = top; y < bottom - pixelShift; y++)
                    Array.Copy(v.Planes[p], (y + pixelShift) * rowPitch + left,
                               v.Planes[p], y * rowPitch + left, width);
                for (int y = bottom - pixelShift; y < bottom; y++)
                    Array.Fill(v.Planes[p], (byte)0, y * rowPitch + left, width);
            }
        }
        else
        {
            for (int p = 0; p < 4; p++)
            {
                for (int y = bottom - 1; y >= top + pixelShift; y--)
                    Array.Copy(v.Planes[p], (y - pixelShift) * rowPitch + left,
                               v.Planes[p], y * rowPitch + left, width);
                for (int y = top; y < top + pixelShift; y++)
                    Array.Fill(v.Planes[p], (byte)0, y * rowPitch + left, width);
            }
        }
    }

    // ── Write char at cursor for all modes ──────────────────────────────────────

    private static void WriteCharAtCursor(byte ch, byte attr, ushort count, VideoState v, bool withAttr, byte back = 0)
    {
        if (IsText(v.VideoMode))
        {
            int cols = TextCols(v.VideoMode);
            for (int i = 0; i < count; i++)
            {
                int ci = _cursorRow * cols + _cursorCol;
                if ((uint)ci < VideoState.PlaneSize)
                {
                    v.Planes[0][ci] = ch;
                    if (withAttr) v.Planes[1][ci] = attr;
                }
            }
            return;
        }
        if (v.IsCga)
        {
            for (int i = 0; i < count; i++)
                CgaTeletype(ch, attr, v);
            return;
        }
        if (v.IsMode13h)
        {
            for (int i = 0; i < count; i++)
                DrawCharMode13h(ch, attr, v, back);
            return;
        }
        if (IsEga(v.VideoMode))
        {
            for (int i = 0; i < count; i++)
                DrawCharEga(ch, attr, v);
        }
    }

    // ── Teletype for all modes ──────────────────────────────────────────────────

    /// <summary>
    /// DOS console output (INT 21h AH=02/06/09, write to STDOUT handle): every byte goes
    /// through the BIOS teletype, exactly like dosbox-x routes it — dos.cpp:1228 (AH=09)
    /// → DOS_WriteFile(STDOUT) → dev_con.h:1076 device_CON::Write → Output() (dev_con.h:1681)
    /// → Real_INT10_TeletypeOutput(chr, 7) (dev_con.h:484) which is INT 10h AH=0E with BL=7.
    /// In text modes the teletype preserves the cell attribute (useattr=false).
    /// </summary>
    public static void DosConOut(byte ch, VideoState v)
        => Teletype(ch, 0x07, v, useAttr: !IsText(v.VideoMode));

    private static void Teletype(byte ch, byte attr, VideoState v, bool useAttr = true, byte back = 0)
    {
        if (IsText(v.VideoMode)) { TextTeletype(ch, attr, v, useAttr); return; }
        if (v.IsCga) { CgaTeletype(ch, attr, v); return; }
        if (v.IsMode13h) { Mode13hTeletype(ch, attr, v, back); return; }
        if (IsEga(v.VideoMode)) { EgaTeletype(ch, attr, v); return; }
    }

    private static void TextTeletype(byte ch, byte attr, VideoState v, bool useAttr = true)
    {
        int cols = TextCols(v.VideoMode);
        if (ch == 0x0D) { _cursorCol = 0; return; }
        if (ch == 0x0A)
        {
            _cursorRow++;
            if (_cursorRow >= 25)
            {
                _cursorRow = 24;
                TextScrollUp1(cols, 0x07, v);
            }
            return;
        }
        if (ch == 0x08) { if (_cursorCol > 0) _cursorCol--; return; }
        if (ch == 0x07) return; // bell

        int ci = _cursorRow * cols + _cursorCol;
        if ((uint)ci < VideoState.PlaneSize)
        {
            v.Planes[0][ci] = ch;
            if (useAttr) v.Planes[1][ci] = attr;
        }
        _cursorCol++;
        if (_cursorCol >= cols)
        {
            _cursorCol = 0;
            _cursorRow++;
            if (_cursorRow >= 25)
            {
                _cursorRow = 24;
                TextScrollUp1(cols, 0x07, v);
            }
        }
    }

    private static void TextScrollUp1(int cols, byte fillAttr, VideoState v)
    {
        Array.Copy(v.Planes[0], cols, v.Planes[0], 0, cols * 24);
        Array.Copy(v.Planes[1], cols, v.Planes[1], 0, cols * 24);
        int lastRow = 24 * cols;
        for (int i = 0; i < cols; i++)
        {
            v.Planes[0][lastRow + i] = 0x20;
            v.Planes[1][lastRow + i] = fillAttr;
        }
    }

    private static void Mode13hTeletype(byte ch, byte attr, VideoState v, byte back)
    {
        if (ch == 0x0D) { _cursorCol = 0; return; }
        if (ch == 0x0A) { _cursorRow++; return; }
        if (ch == 0x08) { if (_cursorCol > 0) _cursorCol--; return; }
        if (ch == 0x07) return;

        DrawCharMode13h(ch, attr, v, back);
        _cursorCol++;
        if (_cursorCol >= 40) { _cursorCol = 0; _cursorRow++; }
    }

    /// <summary>Draws the whole 8x8 cell, background included. In the 256-colour modes the BIOS
    /// takes the background colour from BH (the "page" argument), not from the attribute:
    /// dosbox-x int10_char.cpp WriteChar, case M_VGA: <c>back=page; page=0;</c>, and every
    /// pixel goes through INT10_PutPixel with <c>(bitline&amp;bitsel) ? attr : back</c>.
    /// Drawing only the set bits left the old glyph underneath, so rewriting a counter in
    /// place piled the digits up.</summary>
    private static void DrawCharMode13h(byte ch, byte color, VideoState v, byte back)
    {
        int charIdx = ch * 8;
        for (int row = 0; row < 8; row++)
        {
            int y = _cursorRow * 8 + row;
            if (y >= 200) break;
            byte fontByte = (charIdx + row < Font8x8.Length) ? Font8x8[charIdx + row] : (byte)0;
            for (int bit = 0; bit < 8; bit++)
            {
                int x = _cursorCol * 8 + bit;
                if (x >= 320) break;
                v.Planes[0][y * 320 + x] = ((fontByte >> (7 - bit)) & 1) != 0 ? color : back;
            }
        }
    }

    private static void EgaTeletype(byte ch, byte attr, VideoState v)
    {
        if (ch == 0x0D) { _cursorCol = 0; return; }
        if (ch == 0x0A) { _cursorRow++; return; }
        if (ch == 0x08) { if (_cursorCol > 0) _cursorCol--; return; }
        if (ch == 0x07) return;

        DrawCharEga(ch, attr, v);
        _cursorCol++;
        int maxCols = v.Width / 8;
        if (_cursorCol >= maxCols) { _cursorCol = 0; _cursorRow++; }
    }

    private static void DrawCharEga(byte ch, byte color, VideoState v)
    {
        int rowPitch = v.Width / 8;
        int charIdx = ch * 8;
        for (int row = 0; row < 8; row++)
        {
            int y = _cursorRow * 8 + row;
            if (y >= v.Height) break;
            byte fontByte = (charIdx + row < Font8x8.Length) ? Font8x8[charIdx + row] : (byte)0;
            for (int bit = 0; bit < 8; bit++)
            {
                int x = _cursorCol * 8 + bit;
                if (x >= v.Width) break;
                int byteIdx = y * rowPitch + (x / 8);
                int bitIdx = 7 - (x & 7);
                if (((fontByte >> (7 - bit)) & 1) != 0)
                {
                    for (int p = 0; p < 4; p++)
                    {
                        if (((color >> p) & 1) != 0)
                            v.Planes[p][byteIdx] |= (byte)(1 << bitIdx);
                        else
                            v.Planes[p][byteIdx] &= (byte)~(1 << bitIdx);
                    }
                }
            }
        }
    }

    // ── Character generator (AH=11h) ────────────────────────────────────────────

    private static void HandleCharGen(Cpu cpu, Memory mem, VideoState v)
    {
        switch (cpu.AL)
        {
            case 0x30: // Get font info — int10.cpp: ES:BP=fuente ROM; CX=altura de
                       // carácter del MODO ACTUAL (BDA 0x485), DL=filas-1 (BDA 0x484)
                switch (cpu.BH)
                {
                    case 0x00: // INT 1Fh pointer (user font)
                    case 0x01: // INT 43h pointer (8x14)
                    case 0x02: // 8x14 ROM font
                    case 0x03: // 8x8 ROM font (first half)
                    case 0x04: // 8x8 ROM font (second half)
                    case 0x05: // 9x14 ROM alternate
                    case 0x06: // 8x16 ROM font
                    case 0x07: // 9x16 ROM alternate
                        // CX y DL salen de la BDA, que es donde el BIOS deja el estado VIVO: una
                        // recarga de fuente (AL=1x) cambia las filas y la altura, y devolver
                        // aqui la altura nominal del modo y un 24 clavado le mentia a todo
                        // programa que preguntara tras ponerse en 80x50.
                        var bdaFuente = new BiosDataArea(mem);
                        cpu.ES = 0xF000;
                        cpu.BP = 0x0100;
                        cpu.CX = bdaFuente.CharHeight != 0 ? bdaFuente.CharHeight : CharHeight(v.VideoMode);
                        cpu.DL = bdaFuente.VideoRowsMinus1;
                        break;
                }
                break;

            // Las variantes 0x cargan la fuente y nada mas; las 1x ademas RECARGAN el CRTC, la
            // BDA y el cursor, o sea CAMBIAN EL NUMERO DE FILAS (el 80x50 clasico es AL=12h).
            // Antes eran todas `break;` — no-ops — asi que un programa con fuente propia
            // dibujaba con la del BIOS y uno de 50 filas se quedaba en 25.
            case 0x00: // Cargar fuente de USUARIO desde ES:BP
            case 0x10:
            {
                int cuantos = cpu.CX, alto = cpu.BH;
                var datos = new byte[Math.Max(0, cuantos * alto)];
                for (int i = 0; i < datos.Length; i++)
                    datos[i] = mem.RdU8(cpu.ES, (ushort)(cpu.BP + i));
                LoadFont(v, mem, datos, cpu.AL == 0x10, cuantos, cpu.DX, cpu.BL & 0x7F, alto);
                break;
            }
            case 0x01: // Fuente ROM de 8x14
            case 0x11:
                LoadFont(v, mem, RomFonts.Font8x14, cpu.AL == 0x11, 256, 0, cpu.BL & 0x7F, 14);
                break;
            case 0x02: // Fuente ROM de 8x8 (double dot)
            case 0x12:
                LoadFont(v, mem, RomFonts.Font8x8, cpu.AL == 0x12, 256, 0, cpu.BL & 0x7F, 8);
                break;
            case 0x04: // Fuente ROM de 8x16
            case 0x14:
                LoadFont(v, mem, RomFonts.Font8x16, cpu.AL == 0x14, 256, 0, cpu.BL & 0x7F, 16);
                break;
        }
    }

    /// <summary>Offsets de los ocho BLOQUES de fuente dentro del plano 2, en el orden raro del
    /// hardware (mapa 0 a 0K, 1 a 16K, 2 a 32K, 3 a 48K, 4 a 8K, 5 a 24K, 6 a 40K, 7 a 56K).
    /// Copiado de dosbox-x <c>src/ints/int10_memory.cpp:54</c> <c>map_offset</c>.</summary>
    private static readonly int[] FontMapOffset =
        { 0x0000, 0x4000, 0x8000, 0xC000, 0x2000, 0x6000, 0xA000, 0xE000 };

    /// <summary>Lineas de barrido NOMINALES del modo, la columna <c>sheight</c> de
    /// <c>ModeList_VGA</c> (dosbox-x int10_modes.cpp:83+). El recuento de filas de una recarga de
    /// fuente sale de aqui y NO del CRTC vivo: si saliera del CRTC, dos recargas seguidas se
    /// realimentarian (28 filas de 14 dejan 392 lineas, y el 80x50 siguiente daria 49 filas en
    /// vez de 50).</summary>
    private static int NominalScanlines(byte m) => m switch
    {
        0x0F or 0x10 => 350,
        0x11 or 0x12 => 480,
        _            => 400,
    };

    /// <summary>
    /// Carga un generador de caracteres en el PLANO 2 de la VRAM, que es donde vive la fuente de
    /// texto en una EGA/VGA: 32 bytes por caracter, ocho bloques de 8 KB que el registro 3 del
    /// Sequencer elige al dibujar. Puerto de <c>INT10_LoadFont</c> (dosbox-x
    /// <c>src/ints/int10_memory.cpp:59</c>).
    ///
    /// Con <paramref name="reload"/> (las variantes 1x del servicio) ademas se reprograma la
    /// geometria: altura de caracter en el r09 del CRTC, fin de display vertical en el r12, y las
    /// filas y la altura en la BDA (40:84 y 40:85). Eso es lo que convierte un 80x25 en un 80x50.
    ///
    /// DESVIACION CONSCIENTE respecto al original: DOSBox-X deja el adaptador en los registros de
    /// TEXTO al terminar (plano 0/1, odd/even, ventana Bx000), porque eso es lo que hace el BIOS
    /// real. Aqui se restauran los que hubiera ANTES de entrar. Motivo: el unico sitio desde el
    /// que se llama con un modo grafico puesto es el propio programa, y dejarle el adaptador en
    /// modo texto le romperia el dibujo; ademas, restaurar a "texto" cambiaria la ventana de
    /// memoria del modo 7 de B8000 a B0000 para todos los juegos probados, que es un efecto colateral que
    /// esta tarea no ha medido. En un set-mode de texto —el caso normal— las dos opciones dejan
    /// exactamente los mismos valores.
    /// </summary>
    internal static void LoadFont(VideoState v, Memory mem, ReadOnlySpan<byte> glifos,
                                  bool reload, int count, int offset, int map, int height)
    {
        if (height <= 0 || count <= 0) return;

        byte mapMaskPrevio = v.MapMask, seq2Previo = v.SeqRegs[2], seq4Previo = v.SeqRegs[4];
        byte readMapPrevio = v.ReadMapSelect, gcModePrevio = v.GraphicsMode, miscPrevio = v.Miscellaneous;

        // Modo planar, verbatim de int10_memory.cpp:71-76: plano 2 para escribir y para leer,
        // odd/even apagado en el Sequencer y en el Graphics Controller, write mode 0, y la
        // ventana de la CPU en A0000-AFFFF. Es EXACTAMENTE la via por puertos que usa un
        // programa que se carga la fuente a mano.
        v.MapMask = 0x04; v.SeqRegs[2] = 0x04;
        v.SeqRegs[4] = 0x07;      // 0x05 | m64k, con m64k = 0x02 en una VGA
        v.ReadMapSelect = 2;
        v.GraphicsMode = 0x00;
        v.Miscellaneous = 0x04;

        int destino = FontMapOffset[map & 7] + offset * 32;
        for (int i = 0; i < count; i++)
        {
            for (int j = 0; j < height; j++)
            {
                int k = i * height + j;
                if (k >= glifos.Length) break;
                v.WriteVram((destino + i * 32 + j) & (VideoState.PlaneSize - 1), glifos[k]);
            }
        }

        v.MapMask = mapMaskPrevio; v.SeqRegs[2] = seq2Previo; v.SeqRegs[4] = seq4Previo;
        v.ReadMapSelect = readMapPrevio; v.GraphicsMode = gcModePrevio; v.Miscellaneous = miscPrevio;

        if (!reload) return;

        var bda = new BiosDataArea(mem);
        int sheight = NominalScanlines(v.VideoMode);
        int filas = sheight / height;
        if (filas < 1) filas = 1;

        // Max scan line: altura-1 en los 5 bits bajos, conservando los altos (bits del line
        // compare y del double scan) — int10_memory.cpp:100-102.
        v.CrtcIndex = 0x09;
        v.WriteCrtcRegister((byte)((v.CrtcRegs[0x09] & 0xE0) | ((height - 1) & 0x1F)));
        // Vertical display end. Igual que el original, los bits de overflow del r07 NO se tocan
        // ("FIXME: Does not change overflow bits", int10_memory.cpp:106).
        int vdend = filas * height * (sheight == 200 ? 2 : 1) - 1;
        v.CrtcIndex = 0x12;
        v.WriteCrtcRegister((byte)vdend);
        // El modo 7 lleva ademas la posicion del subrayado (int10_memory.cpp:108-111).
        if (v.VideoMode == 0x07)
        {
            v.CrtcIndex = 0x14;
            v.WriteCrtcRegister((byte)((v.CrtcRegs[0x14] & ~0x1F) | ((height - 1) & 0x1F)));
        }

        bda.VideoRowsMinus1 = (byte)(filas - 1);
        bda.CharHeight = (ushort)height;
        // "bios adds extra on reload" (int10_memory.cpp:117).
        bda.VideoPageSize = (ushort)(filas * bda.ScreenCols * 2 + 0x100);

        int alturaCursor = height >= 14 ? height - 1 : height;   // "move up one line on 14+"
        SetCursorShape(v, mem, (byte)(alturaCursor - 2), (byte)(alturaCursor - 1));

        if (_cursorRow >= filas) _cursorRow = (byte)(filas - 1);
    }

    /// <summary>Forma del cursor de texto con la conversion "estilo CGA" del BIOS real, verbatim
    /// de <c>INT10_SetCursorShape</c> (dosbox-x <c>src/ints/int10_char.cpp:1215</c>), rama
    /// EGA/VGA con el subsistema de video activo. Se llama al recargar la fuente porque el
    /// cursor se mide en lineas de barrido y una fuente mas baja lo dejaria fuera de la celda.
    /// </summary>
    private static void SetCursorShape(VideoState v, Memory mem, byte first, byte last)
    {
        var bda = new BiosDataArea(mem);
        int f = first, l = last;
        bool convertir = true;
        if ((f & 0x60) == 0x20) { f = 0x1E | 0x20; l = 0x00; convertir = false; } // invisible
        if (convertir && (f & 0xE0) == 0 && (l & 0xE0) == 0)
        {
            int cheight = bda.CharHeight - 1;
            if (l < f)
            {
                if (l != 0) { f = l; l = cheight; }
            }
            else if (((f | l) >= cheight) || l != cheight - 1 || f != cheight)
            {
                if (l > 3)
                {
                    if (f + 2 < l)
                    {
                        if (f > 2) { f = (cheight + 1) / 2; l = cheight; }
                        else l = cheight;
                    }
                    else
                    {
                        f = (f - l) + cheight; l = cheight;
                        if (cheight > 0x0C) { f--; l--; }
                    }
                }
            }
        }
        bda.CursorStart = (byte)f;
        bda.CursorEnd = (byte)l;
        v.CrtcIndex = 0x0A; v.WriteCrtcRegister((byte)f);
        v.CrtcIndex = 0x0B; v.WriteCrtcRegister((byte)l);
        _cursorStart = (byte)(f & 0x1F);
        _cursorEnd = (byte)(l & 0x1F);
    }

    // ── Palette handler (unchanged from before) ─────────────────────────────────

    // PAKU_LOG_PALBIOS=1: traza cada servicio de paleta del BIOS (INT 10h AH=10h) con sus
    // argumentos, y para las cargas por bloque (AL=02/12) el contenido que se carga. Un juego
    // que pinta imágenes con tabla de color propia programa la paleta desde aquí, así que sin
    // esta traza no se distingue "el juego pidió estos colores" de "son los que dejó el
    // set-mode y nadie los tocó" — y esa diferencia es justo la que decide si un color medido
    // en pantalla viene del juego o de nuestro propio valor por defecto.
    private static readonly bool _logPalBios =
        Environment.GetEnvironmentVariable("PAKU_LOG_PALBIOS") == "1";

    private static void TrazaPalBios(Cpu cpu, Memory mem)
    {
        if (!_logPalBios) return;
        string extra = "";
        if (cpu.AL == 0x12 || cpu.AL == 0x02)
        {
            // Una traza no puede tumbar al juego que está diagnosticando: si el puntero de origen
            // apunta fuera de la memoria, se dice y no se copia nada.
            int n = cpu.AL == 0x12 ? cpu.CX * 3 : 17;
            int src = Memory.Lin(cpu.ES, cpu.DX);
            if (src < 0 || src >= mem.Raw.Length)
                extra = " datos=(origen fuera de la memoria)";
            else
            {
                var b = new byte[Math.Min(n, mem.Raw.Length - src)];
                Array.Copy(mem.Raw, src, b, 0, b.Length);
                extra = " datos=" + Convert.ToHexString(b);
            }
        }
        Console.Error.WriteLine(
            $"[PALBIOS] AX={cpu.AX:X4} BX={cpu.BX:X4} CX={cpu.CX:X4} DX={cpu.DX:X4} ES={cpu.ES:X4}{extra}");
    }

    private static void HandlePalette(Cpu cpu, Memory mem, VideoState v)
    {
        TrazaPalBios(cpu, mem);
        switch (cpu.AL)
        {
            case 0x00:
                // BL = palette register number, BH = value (DOSBox-X int10.cpp:
                // INT10_SetSinglePaletteRegister(reg_bl, reg_bh))
                if (cpu.BL < 16) v.AttrRegs[cpu.BL] = (byte)(cpu.BH & 0x3F);
                break;
            case 0x01:
                v.OverscanColor = (byte)(cpu.BH & 0x3F);
                break;
            case 0x02:
            {
                int src = Memory.Lin(cpu.ES, cpu.DX);
                for (int i = 0; i < 16; i++)
                    v.AttrRegs[i] = (byte)(mem.Raw[src + i] & 0x3F);
                v.OverscanColor = (byte)(mem.Raw[src + 16] & 0x3F);
                break;
            }
            case 0x03:
                v.BlinkEnabled = (cpu.BL & 1) != 0;
                break;
            case 0x07:
                // BL = palette register number, returns BH = value (BIOS/DOSBox-X)
                cpu.BH = cpu.BL < 16 ? v.AttrRegs[cpu.BL] : (byte)0;
                break;
            case 0x08:
                cpu.BH = v.OverscanColor;
                break;
            case 0x09:
            {
                int dst = Memory.Lin(cpu.ES, cpu.DX);
                for (int i = 0; i < 16; i++) mem.Raw[dst + i] = v.AttrRegs[i];
                mem.Raw[dst + 16] = v.OverscanColor;
                break;
            }
            case 0x10:
            {
                int idx = cpu.BX * 3;
                if (idx + 2 < v.Dac.Length)
                {
                    v.Dac[idx + 0] = (byte)(cpu.DH & 0x3F);
                    v.Dac[idx + 1] = (byte)(cpu.CH & 0x3F);
                    v.Dac[idx + 2] = (byte)(cpu.CL & 0x3F);
                }
                break;
            }
            case 0x12:
            {
                int start = cpu.BX, count = cpu.CX;
                int src = Memory.Lin(cpu.ES, cpu.DX);
                for (int i = 0; i < count && (start + i) < 256; i++)
                {
                    int di = (start + i) * 3;
                    v.Dac[di + 0] = (byte)(mem.Raw[src++] & 0x3F);
                    v.Dac[di + 1] = (byte)(mem.Raw[src++] & 0x3F);
                    v.Dac[di + 2] = (byte)(mem.Raw[src++] & 0x3F);
                }
                break;
            }
            case 0x13:
                v.DacPageMode = (byte)(cpu.BL & 1);
                v.DacPage = (byte)(cpu.BH & 0x0F);
                break;
            case 0x18: // SET PEL MASK (undocumented) — int10.cpp INT10_SetPelMask
                v.DacPelMask = cpu.BL;
                break;
            case 0x19: // GET PEL MASK (undocumented) — int10.cpp: BL=mask, BH=0
                cpu.BL = v.DacPelMask;
                cpu.BH = 0;
                break;
            case 0x15:
            {
                int di = cpu.BX * 3;
                if (di + 2 < v.Dac.Length)
                {
                    cpu.DH = v.Dac[di + 0];
                    cpu.CH = v.Dac[di + 1];
                    cpu.CL = v.Dac[di + 2];
                }
                break;
            }
            case 0x17:
            {
                int start = cpu.BX, count = cpu.CX;
                int dst = Memory.Lin(cpu.ES, cpu.DX);
                for (int i = 0; i < count && (start + i) < 256; i++)
                {
                    int si = (start + i) * 3;
                    mem.Raw[dst++] = v.Dac[si + 0];
                    mem.Raw[dst++] = v.Dac[si + 1];
                    mem.Raw[dst++] = v.Dac[si + 2];
                }
                break;
            }
            case 0x1A:
                if (cpu.BL == 0x00)
                {
                    cpu.BH = v.DacPageMode;
                    cpu.BL = v.DacPage;
                }
                else
                {
                    v.DacPageMode = (byte)(cpu.BL & 1);
                    v.DacPage = (byte)(cpu.BH & 0x0F);
                }
                break;
            case 0x1B:
            {
                int start = cpu.BX, count = cpu.CX;
                for (int i = 0; i < count && (start + i) < 256; i++)
                {
                    int di = (start + i) * 3;
                    uint lum = (77u * v.Dac[di] + 151u * v.Dac[di + 1] + 28u * v.Dac[di + 2] + 0x80u) >> 8;
                    byte gray = lum > 0x3F ? (byte)0x3F : (byte)lum;
                    v.Dac[di] = v.Dac[di + 1] = v.Dac[di + 2] = gray;
                }
                break;
            }
        }
    }

    // ── CGA helpers (preserved from original) ───────────────────────────────────

    private static void CgaWritePixel(ushort col, ushort row, byte color, VideoState v)
    {
        if (col >= 320 || row >= 200) return;
        int rowBase = (row & 1) * 0x2000 + (row >> 1) * 80;
        int byteOff = rowBase + (col >> 2);
        int shift = 6 - ((col & 3) << 1);
        byte mask = (byte)(3 << shift);
        byte old = v.ReadLinear(byteOff);
        if ((color & 0x80) != 0)
            v.WriteLinear(byteOff, (byte)(old ^ ((color & 3) << shift)));
        else
            v.WriteLinear(byteOff, (byte)((old & ~mask) | ((color & 3) << shift)));
    }

    private static byte CgaReadPixel(ushort col, ushort row, VideoState v)
    {
        if (col >= 320 || row >= 200) return 0;
        int rowBase = (row & 1) * 0x2000 + (row >> 1) * 80;
        int byteOff = rowBase + (col >> 2);
        int shift = 6 - ((col & 3) << 1);
        return (byte)((v.ReadLinear(byteOff) >> shift) & 3);
    }

    private static void CgaScroll(Cpu cpu, VideoState v, bool up)
    {
        int lines = cpu.AL;
        int top = cpu.CH * 8;
        int bottom = (cpu.DH + 1) * 8;
        if (bottom > 200) bottom = 200;

        if (lines == 0)
        {
            for (int y = top; y < bottom; y++)
            {
                int rowBase = (y & 1) * 0x2000 + (y >> 1) * 80;
                int left = cpu.CL * 2;
                int right = (cpu.DL + 1) * 2;
                for (int b = left; b < right && b < 80; b++)
                    v.WriteLinear(rowBase + b, 0);
            }
            return;
        }

        int pixelShift = lines * 8;
        if (up)
        {
            for (int y = top; y < bottom - pixelShift; y++)
            {
                int src = ((y + pixelShift) & 1) * 0x2000 + ((y + pixelShift) >> 1) * 80;
                int dst = (y & 1) * 0x2000 + (y >> 1) * 80;
                CopyLinear(v, src, dst, 80);
            }
            for (int y = bottom - pixelShift; y < bottom; y++)
            {
                int dst = (y & 1) * 0x2000 + (y >> 1) * 80;
                FillLinear(v, dst, 80, 0);
            }
        }
        else
        {
            for (int y = bottom - 1; y >= top + pixelShift; y--)
            {
                int src = ((y - pixelShift) & 1) * 0x2000 + ((y - pixelShift) >> 1) * 80;
                int dst = (y & 1) * 0x2000 + (y >> 1) * 80;
                CopyLinear(v, src, dst, 80);
            }
            for (int y = top; y < top + pixelShift; y++)
            {
                int dst = (y & 1) * 0x2000 + (y >> 1) * 80;
                FillLinear(v, dst, 80, 0);
            }
        }
    }

    private static void CopyLinear(VideoState v, int src, int dst, int count)
    {
        int half = count >> 1;
        Array.Copy(v.Planes[0], src >> 1, v.Planes[0], dst >> 1, half);
        Array.Copy(v.Planes[1], src >> 1, v.Planes[1], dst >> 1, half);
    }

    private static void FillLinear(VideoState v, int off, int count, byte val)
    {
        int half = count >> 1;
        Array.Fill(v.Planes[0], val, off >> 1, half);
        Array.Fill(v.Planes[1], val, off >> 1, half);
    }

    private static void CgaTeletype(byte ch, byte color, VideoState v)
    {
        if (ch == 0x0D) { _cursorCol = 0; return; }
        if (ch == 0x0A) { _cursorRow++; return; }
        if (ch == 0x08) { if (_cursorCol > 0) _cursorCol--; return; }
        if (ch == 0x07) return;

        int cols = 40;
        byte colorBits = (byte)(color & 3);
        int charIdx = ch * 8;
        for (int row = 0; row < 8; row++)
        {
            int y = _cursorRow * 8 + row;
            if (y >= 200) break;
            byte fontByte = (charIdx + row < Font8x8.Length) ? Font8x8[charIdx + row] : (byte)0;
            int rowBase = (y & 1) * 0x2000 + (y >> 1) * 80;
            for (int bit = 0; bit < 8; bit++)
            {
                int x = _cursorCol * 8 + bit;
                if (x >= 320) break;
                int byteOff = rowBase + (x >> 2);
                int shift = 6 - ((x & 3) << 1);
                byte mask = (byte)(3 << shift);
                byte pixel = ((fontByte >> (7 - bit)) & 1) != 0 ? colorBits : (byte)0;
                byte old = v.ReadLinear(byteOff);
                v.WriteLinear(byteOff, (byte)((old & ~mask) | (pixel << shift)));
            }
        }
        _cursorCol++;
        if (_cursorCol >= cols) { _cursorCol = 0; _cursorRow++; }
    }

    private static void ClearPlanes(VideoState v)
    {
        for (int p = 0; p < 4; p++) Array.Clear(v.Planes[p]);
    }

    // ── 8x8 ROM font ────────────────────────────────────────────────────────────

    public static readonly byte[] Font8x8 =
    {
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x7E,0x81,0xA5,0x81,0xBD,0x99,0x81,0x7E,
        0x7E,0xFF,0xDB,0xFF,0xC3,0xE7,0xFF,0x7E,
        0x6C,0xFE,0xFE,0xFE,0x7C,0x38,0x10,0x00,
        0x10,0x38,0x7C,0xFE,0x7C,0x38,0x10,0x00,
        0x38,0x7C,0x38,0xFE,0xFE,0x7C,0x38,0x7C,
        0x10,0x10,0x38,0x7C,0xFE,0x7C,0x38,0x7C,
        0x00,0x00,0x18,0x3C,0x3C,0x18,0x00,0x00,
        0xFF,0xFF,0xE7,0xC3,0xC3,0xE7,0xFF,0xFF,
        0x00,0x3C,0x66,0x42,0x42,0x66,0x3C,0x00,
        0xFF,0xC3,0x99,0xBD,0xBD,0x99,0xC3,0xFF,
        0x0F,0x07,0x0F,0x7D,0xCC,0xCC,0xCC,0x78,
        0x3C,0x66,0x66,0x66,0x3C,0x18,0x7E,0x18,
        0x3F,0x33,0x3F,0x30,0x30,0x70,0xF0,0xE0,
        0x7F,0x63,0x7F,0x63,0x63,0x67,0xE6,0xC0,
        0x99,0x5A,0x3C,0xE7,0xE7,0x3C,0x5A,0x99,
        0x80,0xE0,0xF8,0xFE,0xF8,0xE0,0x80,0x00,
        0x02,0x0E,0x3E,0xFE,0x3E,0x0E,0x02,0x00,
        0x18,0x3C,0x7E,0x18,0x18,0x7E,0x3C,0x18,
        0x66,0x66,0x66,0x66,0x66,0x00,0x66,0x00,
        0x7F,0xDB,0xDB,0x7B,0x1B,0x1B,0x1B,0x00,
        0x3E,0x63,0x38,0x6C,0x6C,0x38,0xCC,0x78,
        0x00,0x00,0x00,0x00,0x7E,0x7E,0x7E,0x00,
        0x18,0x3C,0x7E,0x18,0x7E,0x3C,0x18,0xFF,
        0x18,0x3C,0x7E,0x18,0x18,0x18,0x18,0x00,
        0x18,0x18,0x18,0x18,0x7E,0x3C,0x18,0x00,
        0x00,0x18,0x0C,0xFE,0x0C,0x18,0x00,0x00,
        0x00,0x30,0x60,0xFE,0x60,0x30,0x00,0x00,
        0x00,0x00,0xC0,0xC0,0xC0,0xFE,0x00,0x00,
        0x00,0x24,0x66,0xFF,0x66,0x24,0x00,0x00,
        0x00,0x18,0x3C,0x7E,0xFF,0xFF,0x00,0x00,
        0x00,0xFF,0xFF,0x7E,0x3C,0x18,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // space
        0x30,0x78,0x78,0x30,0x30,0x00,0x30,0x00, // !
        0x6C,0x6C,0x6C,0x00,0x00,0x00,0x00,0x00, // "
        0x6C,0x6C,0xFE,0x6C,0xFE,0x6C,0x6C,0x00, // #
        0x30,0x7C,0xC0,0x78,0x0C,0xF8,0x30,0x00, // $
        0x00,0xC6,0xCC,0x18,0x30,0x66,0xC6,0x00, // %
        0x38,0x6C,0x38,0x76,0xDC,0xCC,0x76,0x00, // &
        0x60,0x60,0xC0,0x00,0x00,0x00,0x00,0x00, // '
        0x18,0x30,0x60,0x60,0x60,0x30,0x18,0x00, // (
        0x60,0x30,0x18,0x18,0x18,0x30,0x60,0x00, // )
        0x00,0x66,0x3C,0xFF,0x3C,0x66,0x00,0x00, // *
        0x00,0x30,0x30,0xFC,0x30,0x30,0x00,0x00, // +
        0x00,0x00,0x00,0x00,0x00,0x30,0x30,0x60, // ,
        0x00,0x00,0x00,0xFC,0x00,0x00,0x00,0x00, // -
        0x00,0x00,0x00,0x00,0x00,0x30,0x30,0x00, // .
        0x06,0x0C,0x18,0x30,0x60,0xC0,0x80,0x00, // /
        0x7C,0xC6,0xCE,0xDE,0xF6,0xE6,0x7C,0x00, // 0
        0x30,0x70,0x30,0x30,0x30,0x30,0xFC,0x00, // 1
        0x78,0xCC,0x0C,0x38,0x60,0xCC,0xFC,0x00, // 2
        0x78,0xCC,0x0C,0x38,0x0C,0xCC,0x78,0x00, // 3
        0x1C,0x3C,0x6C,0xCC,0xFE,0x0C,0x1E,0x00, // 4
        0xFC,0xC0,0xF8,0x0C,0x0C,0xCC,0x78,0x00, // 5
        0x38,0x60,0xC0,0xF8,0xCC,0xCC,0x78,0x00, // 6
        0xFC,0xCC,0x0C,0x18,0x30,0x30,0x30,0x00, // 7
        0x78,0xCC,0xCC,0x78,0xCC,0xCC,0x78,0x00, // 8
        0x78,0xCC,0xCC,0x7C,0x0C,0x18,0x70,0x00, // 9
        0x00,0x30,0x30,0x00,0x00,0x30,0x30,0x00, // :
        0x00,0x30,0x30,0x00,0x00,0x30,0x30,0x60, // ;
        0x18,0x30,0x60,0xC0,0x60,0x30,0x18,0x00, // <
        0x00,0x00,0xFC,0x00,0x00,0xFC,0x00,0x00, // =
        0x60,0x30,0x18,0x0C,0x18,0x30,0x60,0x00, // >
        0x78,0xCC,0x0C,0x18,0x30,0x00,0x30,0x00, // ?
        0x7C,0xC6,0xDE,0xDE,0xDE,0xC0,0x78,0x00, // @
        0x30,0x78,0xCC,0xCC,0xFC,0xCC,0xCC,0x00, // A
        0xFC,0x66,0x66,0x7C,0x66,0x66,0xFC,0x00, // B
        0x3C,0x66,0xC0,0xC0,0xC0,0x66,0x3C,0x00, // C
        0xF8,0x6C,0x66,0x66,0x66,0x6C,0xF8,0x00, // D
        0xFE,0x62,0x68,0x78,0x68,0x62,0xFE,0x00, // E
        0xFE,0x62,0x68,0x78,0x68,0x60,0xF0,0x00, // F
        0x3C,0x66,0xC0,0xC0,0xCE,0x66,0x3E,0x00, // G
        0xCC,0xCC,0xCC,0xFC,0xCC,0xCC,0xCC,0x00, // H
        0x78,0x30,0x30,0x30,0x30,0x30,0x78,0x00, // I
        0x1E,0x0C,0x0C,0x0C,0xCC,0xCC,0x78,0x00, // J
        0xE6,0x66,0x6C,0x78,0x6C,0x66,0xE6,0x00, // K
        0xF0,0x60,0x60,0x60,0x62,0x66,0xFE,0x00, // L
        0xC6,0xEE,0xFE,0xFE,0xD6,0xC6,0xC6,0x00, // M
        0xC6,0xE6,0xF6,0xDE,0xCE,0xC6,0xC6,0x00, // N
        0x38,0x6C,0xC6,0xC6,0xC6,0x6C,0x38,0x00, // O
        0xFC,0x66,0x66,0x7C,0x60,0x60,0xF0,0x00, // P
        0x78,0xCC,0xCC,0xCC,0xDC,0x78,0x1C,0x00, // Q
        0xFC,0x66,0x66,0x7C,0x6C,0x66,0xE6,0x00, // R
        0x7C,0xC6,0xE0,0x78,0x0E,0xC6,0x7C,0x00, // S
        0xFC,0xB4,0x30,0x30,0x30,0x30,0x78,0x00, // T
        0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xFC,0x00, // U
        0xCC,0xCC,0xCC,0xCC,0xCC,0x78,0x30,0x00, // V
        0xC6,0xC6,0xC6,0xD6,0xFE,0xEE,0xC6,0x00, // W
        0xC6,0xC6,0x6C,0x38,0x38,0x6C,0xC6,0x00, // X
        0xCC,0xCC,0xCC,0x78,0x30,0x30,0x78,0x00, // Y
        0xFE,0xC6,0x8C,0x18,0x32,0x66,0xFE,0x00, // Z
        0x78,0x60,0x60,0x60,0x60,0x60,0x78,0x00, // [
        0xC0,0x60,0x30,0x18,0x0C,0x06,0x02,0x00, // backslash
        0x78,0x18,0x18,0x18,0x18,0x18,0x78,0x00, // ]
        0x10,0x38,0x6C,0xC6,0x00,0x00,0x00,0x00, // ^
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xFF, // _
        0x30,0x30,0x18,0x00,0x00,0x00,0x00,0x00, // `
        0x00,0x00,0x78,0x0C,0x7C,0xCC,0x76,0x00, // a
        0xE0,0x60,0x60,0x7C,0x66,0x66,0xDC,0x00, // b
        0x00,0x00,0x78,0xCC,0xC0,0xCC,0x78,0x00, // c
        0x1C,0x0C,0x0C,0x7C,0xCC,0xCC,0x76,0x00, // d
        0x00,0x00,0x78,0xCC,0xFC,0xC0,0x78,0x00, // e
        0x38,0x6C,0x60,0xF0,0x60,0x60,0xF0,0x00, // f
        0x00,0x00,0x76,0xCC,0xCC,0x7C,0x0C,0xF8, // g
        0xE0,0x60,0x6C,0x76,0x66,0x66,0xE6,0x00, // h
        0x30,0x00,0x70,0x30,0x30,0x30,0x78,0x00, // i
        0x0C,0x00,0x0C,0x0C,0x0C,0xCC,0xCC,0x78, // j
        0xE0,0x60,0x66,0x6C,0x78,0x6C,0xE6,0x00, // k
        0x70,0x30,0x30,0x30,0x30,0x30,0x78,0x00, // l
        0x00,0x00,0xCC,0xFE,0xFE,0xD6,0xC6,0x00, // m
        0x00,0x00,0xF8,0xCC,0xCC,0xCC,0xCC,0x00, // n
        0x00,0x00,0x78,0xCC,0xCC,0xCC,0x78,0x00, // o
        0x00,0x00,0xDC,0x66,0x66,0x7C,0x60,0xF0, // p
        0x00,0x00,0x76,0xCC,0xCC,0x7C,0x0C,0x1E, // q
        0x00,0x00,0xDC,0x76,0x66,0x60,0xF0,0x00, // r
        0x00,0x00,0x7C,0xC0,0x78,0x0C,0xF8,0x00, // s
        0x10,0x30,0x7C,0x30,0x30,0x34,0x18,0x00, // t
        0x00,0x00,0xCC,0xCC,0xCC,0xCC,0x76,0x00, // u
        0x00,0x00,0xCC,0xCC,0xCC,0x78,0x30,0x00, // v
        0x00,0x00,0xC6,0xD6,0xFE,0xFE,0x6C,0x00, // w
        0x00,0x00,0xC6,0x6C,0x38,0x6C,0xC6,0x00, // x
        0x00,0x00,0xCC,0xCC,0xCC,0x7C,0x0C,0xF8, // y
        0x00,0x00,0xFC,0x98,0x30,0x64,0xFC,0x00, // z
        0x1C,0x30,0x30,0xE0,0x30,0x30,0x1C,0x00, // {
        0x18,0x18,0x18,0x00,0x18,0x18,0x18,0x00, // |
        0xE0,0x30,0x30,0x1C,0x30,0x30,0xE0,0x00, // }
        0x76,0xDC,0x00,0x00,0x00,0x00,0x00,0x00, // ~
        0x00,0x10,0x38,0x6C,0xC6,0xC6,0xFE,0x00, // 0x7F
    };

    /// <summary>
    /// Programs GC, Sequencer, and Miscellaneous Output registers for the given video mode,
    /// like a real BIOS would. Values from dosbox-x int10_modes.cpp / Spice86 VideoState.
    /// </summary>
    private static void SetModeRegisters(VideoState v, byte mode)
    {
        // GC reg 6 (Miscellaneous): bit 0 = graphics mode, bits 2-3 = memory map
        //   00 = A000 128K, 01 = A000 64K, 10 = B000 32K, 11 = B800 32K
        // GC reg 5 (GraphicsMode): bits 5-6 = shift register mode
        //   00 = EGA planar, 01 = CGA interleaved, 10 = 256-color chain-4
        // Seq reg 2 (MapMask): which planes are writable
        // Seq reg 4 (MemoryMode): bit 2 = chain-4, bit 3 = odd/even
        // MiscOutput: bit 0 = color/mono, bits 2-3 = clock select
        //
        // BIOS mode-set resets ALL Graphics Controller registers (dosbox-x
        // int10_modes.cpp:2011-2014: memset(gfx_data,0) + gfx_data[7]=0x0F +
        // gfx_data[8]=0xFF, unconditional for every mode). Without the reg-7
        // reset, read mode 1 (color compare) returns 0xFF for every byte
        // (ColorDontCare=0 means "no plane participates" -> all pixels match):
        // One game's graphics driver menu-highlight recolor (stencil = read-mode-1 result used
        // as bit mask over a SetReset write) then repaints the FULL byte and
        // wipes the text -> solid block instead of "background-only" recolor.
        v.SetReset = 0;
        v.EnableSetReset = 0;
        v.ColorCompare = 0;
        v.DataRotate = 0;
        v.ReadMapSelect = 0;
        v.ColorDontCare = 0x0F;
        v.BitMask = 0xFF;
        switch (mode)
        {
            case 0x00: case 0x01: // 40-col text
                v.Miscellaneous = 0x0E; // text, B800, odd/even
                v.GraphicsMode = 0x10;  // odd/even interleave
                v.MapMask = 0x03;       // planes 0,1
                v.MiscOutput = 0x67;    // color, 25MHz
                break;
            case 0x02: case 0x03: case 0x07: // 80-col text
                v.Miscellaneous = 0x0E;
                v.GraphicsMode = 0x10;
                v.MapMask = 0x03;
                // Misc Output bit 0 = I/O address select: 0 picks the mono port pair
                // (3B4/3B5), 1 the colour one. int10_modes.cpp:1552
                // misc_output = 0x2 | (mono_mode ? 0x0 : 0x1), mono_mode = mode 7 or 0Fh.
                v.MiscOutput = mode == 0x07 ? (byte)0x66 : (byte)0x67;
                break;
            case 0x04: case 0x05: // CGA 320x200 4-color
                v.Miscellaneous = 0x0F; // graphics, B800
                v.GraphicsMode = 0x30;  // CGA interleaved (shift=01), odd/even
                v.MapMask = 0x03;
                v.MiscOutput = 0x63;
                break;
            case 0x06: // CGA 640x200 2-color
                // En EGA/VGA el modo 6 es memoria LINEAL en el plano 0 ("a 640x200 variant of
                // the MCGA 2-color mode", int10_modes.cpp:2051-2055 + vga.cpp:319-331): sin
                // odd/even ni shift CGA; el entrelazado de bancos 0x2000 lo pone el CRTC en
                // scan-out (aquí, RenderCga2). Con GraphicsMode=0x10 se descartaba la mitad
                // de los bytes al escribir (logo de un juego CGA troceado).
                v.Miscellaneous = 0x0D; // graphics, B800
                v.GraphicsMode = 0x00;
                v.MapMask = 0x01;
                v.MiscOutput = 0x63;
                break;
            case 0x0D: // EGA 320x200 16-color
                v.Miscellaneous = 0x05; // graphics, A000
                v.GraphicsMode = 0x00;  // EGA planar
                v.MapMask = 0x0F;
                v.MiscOutput = 0x63;
                break;
            case 0x0E: // EGA 640x200 16-color
                v.Miscellaneous = 0x05;
                v.GraphicsMode = 0x00;
                v.MapMask = 0x0F;
                v.MiscOutput = 0x63;
                break;
            case 0x0F: // EGA 640x350 mono
            case 0x10: // EGA 640x350 16-color
                v.Miscellaneous = 0x05;
                v.GraphicsMode = 0x00;
                v.MapMask = 0x0F;
                v.MiscOutput = mode == 0x0F ? (byte)0xA2 : (byte)0xA3; // mono: bit 0 clear
                break;
            case 0x11: // VGA 640x480 2-color
            case 0x12: // VGA 640x480 16-color
                v.Miscellaneous = 0x05;
                v.GraphicsMode = 0x00;
                v.MapMask = 0x0F;
                v.MiscOutput = 0xE3;
                break;
            case 0x13: // VGA 320x200 256-color
                v.Miscellaneous = 0x41; // graphics, A000, chain-4
                v.GraphicsMode = 0x40;  // 256-color
                v.MapMask = 0x0F;
                v.MiscOutput = 0x63;
                break;
            default:
                v.Miscellaneous = 0x05;
                v.GraphicsMode = 0x00;
                v.MapMask = 0x0F;
                v.MiscOutput = 0x63;
                break;
        }

        // ── Registros del Sequencer. NO LOS ESCRIBÍA NADIE ────────────────────────────────────
        // Misma clase de bug que la BDA de vídeo: el array existía y sólo se rellenaba
        // si el juego escribía el puerto 0x3C5 a mano, así que tras un set-mode del BIOS decía
        // cero — o sea, "sin extender, sin encadenar". Y el bit 3 del registro 4 es justamente el
        // que distingue el modo 13h ENCADENADO del modo X (planar): dejarlo a cero hace que todo
        // juego de 256 colores parezca modo X.
        //
        // Valores de int10_modes.cpp (FinishSetMode): bit 1 = más de 64 KB, bit 2 = odd/even
        // deshabilitado, bit 3 = chain-4.
        v.SeqRegs[2] = v.MapMask;
        v.SeqRegs[4] = mode switch
        {
            0x13 => (byte)0x0E,                                       // 256 colores: chain-4
            0x00 or 0x01 or 0x02 or 0x03 or 0x07 => (byte)0x02,       // texto: odd/even activo
            _ => (byte)0x06,                                          // planares y CGA
        };

        ProgramCrtc(v, mode);
    }

    /// <summary>
    /// Programs the CRTC registers for the given standard mode exactly like the DOSBox-X
    /// BIOS does: int10_modes.cpp "/* Program CRTC */" block (line 1686), machine=svga_s3
    /// path (IS_VGA_ARCH). Verified byte-exact against the oracle capture for the 15
    /// standard modes (tools/crtc_sweep, judge_crtc.py). Writes go through
    /// VideoState.WriteCrtcRegister so the write-protect dance is the real one: unlock
    /// r11 bit 7, clear r00..r18, program, re-lock (the BIOS leaves r11 = 8E/8C/85).
    /// NOT modelled (out of scope, measured as such in tools/crtc_sweep/README.md):
    /// S3 extended registers (r5D/r5E/r51/r69/r67 — indices land outside CrtcRegs),
    /// the CGA/MDA/MCGA machine path (int10_modes.cpp:1054+) and r19..r1F.
    /// </summary>
    /// <summary>Escribe los campos de vídeo de la BDA que deja un set-mode del BIOS.
    /// Los cuatro números por modo (columnas, filas, altura de carácter y tamaño de página)
    /// son las columnas tw/th/ch/plength de ModeList_VGA (int10_modes.cpp:83+).</summary>
    private static void SetupVideoBda(Memory mem, byte mode, bool noClear)
    {
        var bda = new BiosDataArea(mem);
        (int tw, int th, int ch, int plen) = mode switch
        {
            0x00 or 0x01 => (40, 25, 16, 0x0800),
            0x02 or 0x03 => (80, 25, 16, 0x1000),
            0x04 or 0x05 => (40, 25,  8, 0x4000),
            0x06         => (80, 25,  8, 0x4000),
            0x07         => (80, 25, 16, 0x1000),
            0x0D         => (40, 25,  8, 0x2000),
            0x0E         => (80, 25,  8, 0x4000),
            0x0F or 0x10 => (80, 25, 14, 0x8000),
            0x11 or 0x12 => (80, 30, 16, 0xA000),
            0x13         => (40, 25,  8, 0x2000),
            _            => (80, 25, 16, 0x1000),
        };
        bda.VideoMode       = mode;
        bda.ScreenCols      = (ushort)tw;
        bda.VideoPageSize   = (ushort)plen;
        bda.VideoPageStart  = 0;
        bda.VideoPage       = 0;
        bda.CrtcBasePort    = (ushort)(mode == 0x07 || mode == 0x0F ? 0x3B4 : 0x3D4);
        bda.VideoRowsMinus1 = (byte)(th - 1);
        bda.CharHeight      = (ushort)ch;
        bda.VideoCtl        = (byte)(0x60 | (noClear ? 0x80 : 0x00));
        // Posición del cursor de las 8 páginas a 0 (INT10_SetCursorPos(0,0,ct) del original).
        for (int pg = 0; pg < 8; pg++) mem.WrU16(0x40, (ushort)(0x50 + pg * 2), 0);
        // La forma del cursor sólo se toca en los modos de texto.
        if (mode <= 0x03 || mode == 0x07)
        {
            bda.CursorStart = 0x06;   // CURSOR_SCAN_LINE_NORMAL (bios.h)
            bda.CursorEnd   = 0x07;   // CURSOR_SCAN_LINE_END
        }
        // 40:65 (copia del registro de modo del CGA) sólo se reescribe en los modos
        // compatibles CGA; en los EGA/VGA persiste el del último de aquéllos. Los valores son
        // los MEDIDOS en el oráculo (tools/bda_video): el switch por tipo de int10_modes.cpp
        // da 1Eh para el modo 6 y la máquina real deja 3Eh — con el bit 5 (blink) puesto, como
        // en los demás. Se toma la medida, que es la que manda aquí.
        byte msr = mode switch
        {
            0x00 => 0x2C, 0x01 => 0x28, 0x02 => 0x2D, 0x03 => 0x29,
            0x04 => 0x2A, 0x05 => 0x2E, 0x06 => 0x3E, 0x07 => 0x29,
            _    => 0x00,
        };
        if (msr != 0)
        {
            bda.CrtModeControl = msr;
            // 40:66 va con él y sigue la misma regla: 3Fh en el modo 6, 30h en los demás
            // compatibles CGA (int10_modes.cpp:1185), y sin tocar en los EGA/VGA. Medido:
            // volviendo al modo 3 tras pasar por el 6, el oráculo restaura 29h/30h.
            bda.CurrentPalette = (byte)(mode == 0x06 ? 0x3F : 0x30);
        }
    }

    private static void ProgramCrtc(VideoState v, byte mode)
    {
        // ModeList_VGA rows (int10_modes.cpp:83): htotal, vtotal, hdispend, vdispend,
        // cheight and the special flags / VGAModes type folded into bools.
        int htotal, vtotal, hdispend, vdispend, cheight;
        bool halfClock = false, doubleScan = false, repeat1 = false;
        bool text = false, cga2 = false, cga4 = false, vga256 = false;
        switch (mode)
        {
            case 0x00: case 0x01: // M_TEXT 360x400, _EGA_HALF_CLOCK
                text = true; halfClock = true;
                htotal = 50; vtotal = 449; hdispend = 40; vdispend = 400; cheight = 16; break;
            case 0x02: case 0x03: case 0x07: // M_TEXT 720x400
                text = true;
                htotal = 100; vtotal = 449; hdispend = 80; vdispend = 400; cheight = 16; break;
            case 0x04: case 0x05: // M_CGA4 320x200, _EGA_HALF_CLOCK|_DOUBLESCAN|_REPEAT1
                cga4 = true; halfClock = true; doubleScan = true; repeat1 = true;
                htotal = 50; vtotal = 449; hdispend = 40; vdispend = 400; cheight = 8; break;
            case 0x06: // M_CGA2 640x200, _DOUBLESCAN|_REPEAT1
                cga2 = true; doubleScan = true; repeat1 = true;
                htotal = 100; vtotal = 449; hdispend = 80; vdispend = 400; cheight = 8; break;
            case 0x0D: // M_EGA 320x200, _EGA_HALF_CLOCK|_DOUBLESCAN
                halfClock = true; doubleScan = true;
                htotal = 50; vtotal = 449; hdispend = 40; vdispend = 400; cheight = 8; break;
            case 0x0E: // M_EGA 640x200, _DOUBLESCAN
                doubleScan = true;
                htotal = 100; vtotal = 449; hdispend = 80; vdispend = 400; cheight = 8; break;
            case 0x0F: case 0x10: // M_EGA 640x350
                htotal = 100; vtotal = 449; hdispend = 80; vdispend = 350; cheight = 14; break;
            case 0x11: case 0x12: // M_EGA 640x480
                htotal = 100; vtotal = 525; hdispend = 80; vdispend = 480; cheight = 16; break;
            case 0x13: // M_VGA 320x200 256-color, _REPEAT1
                vga256 = true; repeat1 = true;
                htotal = 100; vtotal = 449; hdispend = 80; vdispend = 400; cheight = 8; break;
            default:
                return; // unknown mode: leave the CRTC untouched (previous behaviour)
        }
        bool mono = mode == 0x07 || mode == 0x0F;
        void W(byte idx, byte val) { v.CrtcIndex = idx; v.WriteCrtcRegister(val); }

        // First disable write protection (the previous mode-set left r11 bit 7 set)
        W(0x11, (byte)(v.CrtcRegs[0x11] & 0x7F));
        // Clear all the regs
        for (byte ct = 0; ct <= 0x18; ct++) W(ct, 0);
        byte overflow = 0, maxScanline = 0;
        // Horizontal Total / Display End / Blanking
        W(0x00, (byte)(htotal - 5));
        W(0x01, (byte)(hdispend - 1));
        W(0x02, (byte)hdispend);
        int blankEnd = (htotal - 2) & 0x7F;
        W(0x03, (byte)(0x80 | (blankEnd & 0x1F)));
        // Start/End Horizontal Retrace
        int retStart = halfClock && !cga2 ? hdispend + 3 : text ? hdispend + 5 : hdispend + 4;
        W(0x04, (byte)retStart);
        int retEnd;
        if (halfClock)
            retEnd = cga2 ? 0 : doubleScan ? (htotal - 18) & 0x1F : ((htotal - 18) & 0x1F) | 0x20;
        else
            retEnd = text ? (htotal - 3) & 0x1F : (htotal - 4) & 0x1F;
        W(0x05, (byte)(retEnd | ((blankEnd & 0x20) << 2)));
        // Vertical Total
        W(0x06, (byte)(vtotal - 2));
        overflow |= (byte)(((vtotal - 2) & 0x100) >> 8);
        overflow |= (byte)(((vtotal - 2) & 0x200) >> 4);
        // Vertical Retrace Start/End (IS_VGA_ARCH branch)
        int vretrace = vdispend switch { 480 => vdispend + 10, 350 => vdispend + 37, _ => vdispend + 12 };
        W(0x10, (byte)vretrace);
        overflow |= (byte)((vretrace & 0x100) >> 6);
        overflow |= (byte)((vretrace & 0x200) >> 2);
        W(0x11, (byte)((vretrace + 2) & 0xF)); // bit 7 clear: CRTC still unlocked
        // Vertical Display End
        W(0x12, (byte)(vdispend - 1));
        overflow |= (byte)(((vdispend - 1) & 0x100) >> 7);
        overflow |= (byte)(((vdispend - 1) & 0x200) >> 3);
        // Vertical Blank Start/End (IS_VGA_ARCH branch)
        int vblankTrim = vdispend switch { 480 => 7, 350 => 5, 400 => 6, _ => 8 };
        W(0x15, (byte)(vdispend + vblankTrim));
        overflow |= (byte)(((vdispend + vblankTrim) & 0x100) >> 5);
        maxScanline |= (byte)(((vdispend + vblankTrim) & 0x200) >> 4);
        W(0x16, (byte)(vtotal - vblankTrim - 2));
        // Line Compare
        int lineCompare = vtotal < 1024 ? 1023 : 2047;
        W(0x18, (byte)lineCompare);
        overflow |= (byte)((lineCompare & 0x100) >> 4);
        maxScanline |= (byte)((lineCompare & 0x200) >> 3);
        // Maximum scanline / Underline Location
        if (doubleScan) maxScanline |= 0x80;
        if (repeat1) maxScanline |= 0x01;
        byte underline = 0;
        if (text)
        {
            // modeset_ctl (BDA 40:89) & 0x90 = default branch: 400-line text, 8x16 font
            maxScanline |= (byte)(cheight - 1);
            underline = mono ? (byte)(cheight - 1) : (byte)0x1F; // mode 7 uses underline position
        }
        else if (vga256) underline = 0x40;
        else if (vdispend == 350) underline = 0x0F;
        W(0x09, maxScanline);
        W(0x14, underline);
        // OverFlow
        W(0x07, overflow);
        // Offset register: pitch==0 in every standard mode -> hdispend/2 for all of them
        W(0x13, (byte)(hdispend / 2));
        // Mode Control
        byte modeControl = cga2 ? (byte)0xC2
            : cga4 ? (byte)0xA2
            : text || vga256 ? (byte)0xA3
            : mode == 0x11 ? (byte)0xC3 // 0x11 also sets address wrap
            : (byte)0xE3;
        W(0x17, modeControl);
        // Renable write protection
        W(0x11, (byte)(v.CrtcRegs[0x11] | 0x80));
        // Text-mode cursor shape: INT10_SetVideoMode ends with INT10_SetCursorShape
        // (CGA-style 6,7 converted by int10_char.cpp:1215 with cheight=16 to 13,14).
        if (text) { W(0x0A, 0x0D); W(0x0B, 0x0E); }
    }
}
