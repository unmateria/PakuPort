namespace PakuPort.Core.Platform;

/// <summary>
/// Puertos de E/S del controlador CGA/MDA: modo (0x3D8), selección de color (0x3D9),
/// modo MDA (0x3B8). Guarda estado y actualiza VideoState cuando afecta a colores/modo.
/// Adaptado de: hardware/vga_other.cpp write_cga / read_cga (DOSBox-X).
/// </summary>
public static class CgaPortHandler
{
    public static void Register(DosPlatform p)
    {
        var v = p.Video;

        // --- CGA Mode Control Register (0x3D8) ---
        // vga_other.cpp write_cga: controls graphics/text mode, color burst, etc.
        // bit0: 80-col text; bit1: graphics; bit2: B&W; bit3: enable video; bit4: 640px; bit5: blink
        p.RegisterPortOut(0x3D8, (_, val) =>
        {
            v.CgaModeControl = val;
            // If bit1 set → graphics mode; update IsCga state via VideoMode if needed
            // In practice the game will have called INT 10h/AH=00 first to set v.VideoMode
        });
        p.RegisterPortIn(0x3D8, _ => v.CgaModeControl);

        // --- CGA Color Select Register (0x3D9) ---
        // vga_other.cpp write_cga 0x3D9:
        // bits[1:0] = border/background color index in text or graphics
        // bit2 = 1 → intense background in text; in graphics selects palette 0 or 1 via bit5
        // bit3 = intensity bit for border color
        // bit4 = intense palette (bright CGA colors)
        // bit5 = palette select (0=green/red/yellow, 1=cyan/magenta/white)
        p.RegisterPortOut(0x3D9, (_, val) =>
        {
            v.CgaColorSelect = val;
            v.CgaBackground = (byte)(val & 0x0F);         // low 4 bits = background
            v.CgaPalette = (byte)((val >> 4) & 0x03);     // bits 4-5 = palette index + bright
            // bright: bit4 of CgaColorSelect (matches CgaPalette bit used in RenderCga)
            if ((val & 0x10) != 0)
                v.CgaPalette |= 0x10; // set bright flag in palette byte
        });
        p.RegisterPortIn(0x3D9, _ => v.CgaColorSelect);

        // --- MDA Mode Control Register (0x3B8) ---
        // vga_other.cpp write_cga for MCH_HERC/MDA — just store it
        p.RegisterPortOut(0x3B8, (_, val) => v.MdaModeControl = val);
        p.RegisterPortIn(0x3B8, _ => v.MdaModeControl);

        // --- CGA CRTC (0x3D0-0x3D3 are aliases for CGA MC6845) ---
        // These are handled by VgaPortHandler via 0x3D4/0x3D5, but some games
        // address the CGA CRTC directly at 0x3D0-0x3D3. Alias them.
        p.RegisterPortOut(0x3D0, (_, val) => v.CrtcIndex = val);
        p.RegisterPortIn (0x3D0, _ => v.CrtcIndex);
        p.RegisterPortOut(0x3D1, (_, val) => v.WriteCrtcRegister(val));
        p.RegisterPortIn(0x3D1, _ =>
            v.CrtcIndex < v.CrtcRegs.Length ? v.CrtcRegs[v.CrtcIndex] : (byte)0);
    }
}
