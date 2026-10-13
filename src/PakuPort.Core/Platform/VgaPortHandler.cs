namespace PakuPort.Core.Platform;

/// <summary>
/// Puertos de E/S del controlador VGA/EGA: Graphics Controller (0x3CE/0x3CF),
/// Sequencer (0x3C4/0x3C5), DAC (0x3C6/0x3C7/0x3C8/0x3C9), Attribute (0x3C0/0x3C1),
/// CRTC (0x3D4/0x3D5), Input Status (0x3BA/0x3DA).
/// Adaptado de: hardware/vga_dac.cpp, vga_crtc.cpp, vga_seq.cpp, vga_gfx.cpp,
/// vga_attr.cpp, vga_misc.cpp (DOSBox-X).
/// </summary>
public static class VgaPortHandler
{
    public static void Register(DosPlatform p)
    {
        var v = p.Video;

        // --- Puertos SVGA de PakuPort (interfaz PM del VBE, Int10hHandler 4F0A) ---
        // El "hardware" que atacan los stubs de modo protegido: banco de la ventana A000 y
        // display start. out dx,ax llega como DOS escrituras de 8 bits (puerto y puerto+1).
        p.RegisterPortOut(0xE998, (_, val) => v.SvgaBank = (v.SvgaBank & ~0xFF) | val);
        p.RegisterPortOut(0xE999, (_, val) => v.SvgaBank = (v.SvgaBank & 0xFF) | (val << 8));
        p.RegisterPortOut(0xE99A, (_, val) => { v.SvgaStartX = (v.SvgaStartX & ~0xFF) | val; v.RecalculaSvgaStart(); });
        p.RegisterPortOut(0xE99B, (_, val) => { v.SvgaStartX = (v.SvgaStartX & 0xFF) | (val << 8); v.RecalculaSvgaStart(); });
        p.RegisterPortOut(0xE99C, (_, val) => { v.SvgaStartY = (v.SvgaStartY & ~0xFF) | val; v.RecalculaSvgaStart(); });
        p.RegisterPortOut(0xE99D, (_, val) => { v.SvgaStartY = (v.SvgaStartY & 0xFF) | (val << 8); v.RecalculaSvgaStart(); });

        // --- Graphics Controller (0x3CE index, 0x3CF data) ---
        // vga_gfx.cpp vga_write_p3ce / vga_write_p3cf
        p.RegisterPortOut(0x3CE, (_, val) => v.GcIndex = val);
        p.RegisterPortIn (0x3CE, _ => v.GcIndex);
        p.RegisterPortOut(0x3CF, (_, val) => v.WriteGcRegister(val));
        p.RegisterPortIn (0x3CF, _ => v.ReadGcRegister());

        // --- Sequencer (0x3C4 index, 0x3C5 data) ---
        // vga_seq.cpp vga_write_p3c4 / vga_write_p3c5
        p.RegisterPortOut(0x3C4, (_, val) => v.SeqIndex = val);
        p.RegisterPortIn (0x3C4, _ => v.SeqIndex);
        p.RegisterPortOut(0x3C5, (_, val) =>
        {
            if (v.SeqIndex < v.SeqRegs.Length) v.SeqRegs[v.SeqIndex] = val;
            v.WriteSeqRegister(val);
        });
        p.RegisterPortIn (0x3C5, _ => v.SeqIndex < v.SeqRegs.Length ? v.SeqRegs[v.SeqIndex] : (byte)0);

        // --- DAC PEL Mask (0x3C6) ---
        // vga_dac.cpp write_p3c6 / read_p3c6
        p.RegisterPortOut(0x3C6, (_, val) => v.DacPelMask = val);
        p.RegisterPortIn (0x3C6, _ => v.DacPelMask);

        // --- DAC Read Address (0x3C7 write sets read mode) ---
        // vga_dac.cpp write_p3c7 / read_p3c7
        p.RegisterPortOut(0x3C7, (_, val) =>
        {
            v.DacReadIndex = val;
            v.DacReadComponent = 0;
            v.DacState = 0; // DAC_READ state
        });
        p.RegisterPortIn (0x3C7, _ => v.DacState == 0 ? (byte)0x03 : (byte)0x00);

        // --- DAC Write Address (0x3C8) ---
        // vga_dac.cpp write_p3c8 / read_p3c8
        p.RegisterPortOut(0x3C8, (_, val) =>
        {
            v.DacWriteIndex = val;
            v.DacWriteComponent = 0;
            v.DacState = 1; // DAC_WRITE state
        });
        p.RegisterPortIn (0x3C8, _ => v.DacWriteIndex);

        // --- DAC Data (0x3C9) read/write ---
        // vga_dac.cpp write_p3c9 / read_p3c9
        p.RegisterPortOut(0x3C9, (_, val) =>
        {
            int idx = v.DacWriteIndex * 3 + v.DacWriteComponent;
            if (idx < v.Dac.Length)
                v.Dac[idx] = (byte)(val & 0x3F);
            v.DacWriteComponent++;
            if (v.DacWriteComponent >= 3)
            {
                v.DacWriteComponent = 0;
                v.DacWriteIndex++;
            }
        });
        p.RegisterPortIn (0x3C9, _ =>
        {
            byte result = 0;
            int idx = v.DacReadIndex * 3 + v.DacReadComponent;
            if (idx < v.Dac.Length) result = v.Dac[idx];
            v.DacReadComponent++;
            if (v.DacReadComponent >= 3)
            {
                v.DacReadComponent = 0;
                v.DacReadIndex++;
            }
            return result;
        });

        // --- Attribute Controller (0x3C0 write index/data, 0x3C1 read data) ---
        // vga_attr.cpp vga_write_p3c0 / vga_read_p3c1
        // Flip-flop: reset by reading 0x3DA/0x3BA. false=index mode, true=data mode
        p.RegisterPortOut(0x3C0, (_, val) =>
        {
            if (!v.AttrFlipFlop) // index write
            {
                v.AttrIndex = (byte)(val & 0x1F);
                v.AttrVideoEnabled = (val & 0x20) != 0;
            }
            else // data write
            {
                if (v.AttrIndex < v.AttrRegs.Length)
                    v.AttrRegs[v.AttrIndex] = (byte)(val & 0x3F);
            }
            v.AttrFlipFlop = !v.AttrFlipFlop;
        });
        p.RegisterPortIn(0x3C1, _ =>
            v.AttrIndex < v.AttrRegs.Length ? v.AttrRegs[v.AttrIndex] : (byte)0);

        // --- CRTC index+data (color: 0x3D4/0x3D5; mono: 0x3B4/0x3B5) ---
        // vga_crtc.cpp vga_write_p3d4 / vga_write_p3d5 / vga_read_p3d4 / vga_read_p3d5
        p.RegisterPortOut(0x3D4, (_, val) => v.CrtcIndex = val);
        p.RegisterPortIn (0x3D4, _ => v.CrtcIndex);
        p.RegisterPortOut(0x3D5, (_, val) => v.WriteCrtcRegister(val));
        p.RegisterPortIn (0x3D5, _ =>
            v.CrtcIndex < v.CrtcRegs.Length ? v.CrtcRegs[v.CrtcIndex] : (byte)0);
        p.RegisterPortOut(0x3B4, (_, val) => v.CrtcIndex = val);
        p.RegisterPortIn (0x3B4, _ => v.CrtcIndex);
        p.RegisterPortOut(0x3B5, (_, val) => v.WriteCrtcRegister(val));
        p.RegisterPortIn (0x3B5, _ =>
            v.CrtcIndex < v.CrtcRegs.Length ? v.CrtcRegs[v.CrtcIndex] : (byte)0);

        // --- Input Status Register 1 (0x3DA color, 0x3BA mono) ---
        // vga_misc.cpp — bit3=vertical retrace, bit0=display enable
        // Also resets Attribute Controller flip-flop (AttrFlipFlop=false) on read.
        // Many games busy-wait on bit3 toggling for vsync.
        static byte StatusRead(VideoState vs)
        {
            vs.RetraceCounter++;
            vs.AttrFlipFlop = false; // reset AC flip-flop on status read (vga_misc.cpp)
            // Con host: retrace del RELOJ DE PARED a 70 Hz reales (periodo 14.286 ms,
            // ventana ~10% = 1.4 ms, como vga_misc.cpp) — los busy-waits de vsync corren
            // al ritmo del hardware, no del frame del host (un toggle por frame a 30 FPS
            // daba ciclos de 1/15 s → intros ~5× lentas y animación invisible). El
            // fallback por contador solo aplica sin host (headless/verifier: determinista).
            // CON CINTA PUESTA el reloj es el del INVITADO. Un busy-wait de vsync sale del bucle
            // cuando ve el bit 3, asi que la fase la decide el reloj: con el del anfitrion, dos
            // corridas de la MISMA cinta gastan un numero distinto de instrucciones en el mismo
            // trabajo y a la instruccion N el juego esta en dos sitios distintos. Medido en
            // un juego de prueba: la PRIMERA divergencia de dos corridas identicas caia justo en el
            // bucle de espera de 0x3DA (099E:0A44, dx=03DA, tick 112.566.272), y con ella el
            // estado final difiere en decenas de bytes. Con el reloj virtual la fase sale del
            // contador de instrucciones, que avanza igual en las dos corridas.
            // El periodo y la ventana no cambian: son los del hardware (70 Hz, ~10 %).
            const double periodoMs = 1000.0 / 70.0;
            bool retrace;
            if (InputTape.RelojDelInvitado("retrace") && vs.CyclesPerMs > 0)
                retrace = (Cpu.TickCount / (double)vs.CyclesPerMs) % periodoMs < 1.4;
            else if (vs.HostDrivenRetrace)
                retrace = vs.RetraceClock.Elapsed.TotalMilliseconds % periodoMs < 1.4;
            else
                retrace = (vs.RetraceCounter % 400) < 20;
            // Reading 0x3DA only REPORTS the retrace phase; it does NOT trigger frame
            // presentation (hardware model — the CRTC scans out on its own clock). Frame
            // capture lives on the VGA refresh tick (MachineTimer -> VideoState.CaptureFrontBuffer),
            // decoupled from how often (or whether) the game polls this register.
            vs.InVerticalRetrace = retrace;
            return (byte)((retrace ? 0x08 : 0x00) |
                         ((vs.RetraceCounter & 1) != 0 ? 0x01 : 0x00));
        }
        p.RegisterPortIn(0x3DA, _ => StatusRead(v));
        p.RegisterPortIn(0x3BA, _ => StatusRead(v));

        // --- Miscellaneous Output Register (0x3C2 write, 0x3CC read) ---
        // vga_misc.cpp — games write this during mode set; store it
        p.RegisterPortOut(0x3C2, (_, val) => v.MiscOutput = val);
        p.RegisterPortIn (0x3CC, _ => v.MiscOutput);

        // --- Input Status Register 0 (0x3C2 read) ---
        p.RegisterPortIn(0x3C2, _ => 0x00);

        // --- Feature Control (0x3CA read, 0x3DA write in mono) ---
        p.RegisterPortIn(0x3CA, _ => 0x00);
        p.RegisterPortOut(0x3BA, (_, _) => { }); // mono feature write, ignore
    }
}
