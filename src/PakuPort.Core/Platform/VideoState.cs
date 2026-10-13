namespace PakuPort.Core.Platform;

/// <summary>
/// Estado de vídeo EGA/VGA unificado: 4 planos de 64 KB, registros del Graphics Controller,
/// Sequencer, paleta DAC y retrace. Una sola VRAM para CGA/EGA/VGA — la dirección de memoria
/// mapea (GC reg 6 bits 2-3) determina qué rango CPU (A000/B000/B800) accede a los planos.
/// Odd/even mode (GC reg 5 bit 4) enruta bytes pares a planos 0,2 e impares a 1,3.
/// Referencia: Spice86 VideoMemory.cs / Renderer.cs + IBM EGA/VGA Technical Reference.
/// </summary>
public sealed class VideoState
{
    public const int PlaneSize = 0x10000;

    public readonly byte[][] Planes = { new byte[PlaneSize], new byte[PlaneSize], new byte[PlaneSize], new byte[PlaneSize] };
    public readonly byte[] Dac = new byte[768]; // 256 colores × 3 (R,G,B), 6 bits cada uno
    /// <summary>Expande una componente del DAC de 6 bits a los 8 del framebuffer, igual que
    /// dosbox-x src/hardware/vga_dac.cpp `dacexpand()`: los dos bits altos rellenan los dos
    /// bajos (abcdef -> abcdefab), que es `(v*255)/63` sin multiplicar ni dividir. Con el
    /// `v &lt;&lt; 2` de antes el maximo era 252 y no 255, asi que TODOS los colores de TODOS los
    /// juegos salian hasta un 1,2 % mas apagados que en el oraculo — la brecha que quedo
    /// reportada midiendo (0,0,168) aqui contra (0,0,170) alli. Red: tools/dac_expand.</summary>
    public static byte DacExpand(int v) => (byte)(((v & 0x3F) << 2) | ((v & 0x3F) >> 4));

    public byte DacWriteIndex;
    public int DacWriteComponent;

    // Attribute Controller (EGA/VGA palette remapping) — vga_attr.cpp
    public readonly byte[] AttrRegs = new byte[16];
    public byte OverscanColor;
    public bool BlinkEnabled = true;
    public bool AttrFlipFlop;

    // DAC paging — int10_pal.cpp INT10_SelectDACPage
    public byte DacPageMode;
    public byte DacPage;

    // DAC read mode — vga_dac.cpp write_p3c7
    public byte DacReadIndex;
    public byte DacReadComponent;

    // CRTC registers 0x3D4/0x3D5 — vga_crtc.cpp
    public byte CrtcIndex;
    public readonly byte[] CrtcRegs = new byte[32];

    /// <summary>Espejo de vga.config.line_compare de DOSBox-X (10 bits): bits 0-7 del r18,
    /// bit 8 del bit 4 de r07 (se actualiza INCLUSO con el candado de escritura puesto,
    /// vga_crtc.cpp case 0x07 "Line compare bit ignores read only") y bit 9 del bit 6 de
    /// r09. El render lee este latch en vez de rederivar de CrtcRegs para honrar esa
    /// excepción. 0 = nunca programado (sin split, mismo guard de siempre).</summary>
    public int LineCompare;

    /// <summary>
    /// Escritura al puerto de dato del CRTC (3D5/3B5/3D1) con la semántica del hardware —
    /// vga_crtc.cpp vga_write_p3d5. El bit 7 de r11 es el candado de escritura
    /// (crtc(read_only), línea 294): con él puesto las escrituras a r00..r07 se IGNORAN
    /// (los case 0x00..0x07 empiezan con "if (crtc(read_only)) break;"). El set-mode de la
    /// BIOS deja el CRTC bloqueado (r11=8E). Única excepción, verbatim del case 0x07: el
    /// bit 4 (bit 8 del line compare) actualiza el latch LineCompare aunque el candado esté
    /// puesto, sin tocar el byte almacenado (la lectura sigue devolviendo el valor viejo).
    /// </summary>
    public void WriteCrtcRegister(byte val)
    {
        byte idx = CrtcIndex;
        if (idx >= CrtcRegs.Length) return;
        bool locked = (CrtcRegs[0x11] & 0x80) != 0;
        if (LogCrtcWrites)
            Console.Error.WriteLine($"[CRTCW] r{idx:X2}={val:X2}" +
                (locked && idx <= 0x07 ? " (write-protected, ignored)" : ""));
        switch (idx)
        {
            case <= 0x06:
                if (locked) return;
                break;
            case 0x07:
                LineCompare = (LineCompare & 0x6FF) | ((val & 0x10) << 4);
                if (locked) return;
                break;
            case 0x09:
                LineCompare = (LineCompare & 0x5FF) | ((val & 0x40) << 3);
                break;
            case 0x18:
                LineCompare = (LineCompare & 0x700) | val;
                break;
        }
        CrtcRegs[idx] = val;
        if (idx is 0x07 or 0x09 or 0x18) AvisarSplitVivo();
    }

    /// <summary>Avisa por stderr la primera vez que un juego programa un split screen DE VERDAD.
    ///
    /// Es una red, no una traza: el set-mode del BIOS deja la línea de comparación en 1023 (todos
    /// los bits a uno = sin split), un valor inerte, y lo único que importa es cuándo un juego
    /// pone un valor que SÍ parte la pantalla. Existe porque `SplitRow` cambió (contaba scanlines
    /// como si fueran filas) y la pregunta "¿a qué juegos probados les afecta?" no se puede
    /// contestar con un barrido de smokes: un juego puede programar su split en una pantalla a la
    /// que el smoke no llega. Con esto, cualquier partida lo delata en su propio log.
    ///
    /// Tope de avisos: un juego que use el split para efectos de raster lo reprograma por línea, y
    /// entonces esto sería un chorro. Con saber que lo usa basta.</summary>
    int _avisosSplit;
    int _ultimoSplitAvisado = -1;
    void AvisarSplitVivo()
    {
        if (_avisosSplit >= 8 || LineCompare == 0 || LineCompare == _ultimoSplitAvisado) return;
        int fila = SplitRow();
        if (fila >= 400) return;                 // 1023 y compañía: inerte, no es un split
        _ultimoSplitAvisado = LineCompare;
        _avisosSplit++;
        Console.Error.WriteLine($"[SPLIT] el juego parte la pantalla: line compare={LineCompare} " +
                                $"(fila presentada {fila})");
    }

    /// <summary>PAKU_LOG_CRTC=2: traza cada escritura de registro CRTC y cada cambio de modo
    /// (diagnóstico de modos reprogramados a mano por el juego).</summary>
    public static readonly bool LogCrtcWrites = Environment.GetEnvironmentVariable("PAKU_LOG_CRTC") == "2";

    /// <summary>
    /// Líneas realmente activas. El registro Start Vertical Blank (CRTC 0x15 + bit 8 en el
    /// overflow 0x07 y bit 9 en maximum scan line 0x09) puede empezar el blanking ANTES del
    /// final del display: el hardware recorta las líneas de abajo. Portado de DOSBox-X
    /// vga_draw.cpp VGA_SetupDrawing, rama "blanking is used to cut off some lines from the
    /// bottom" (vdend = vbstart, con vbstart += 1).
    /// Un juego de prueba lo usa para volver el modo 12h un 640x400: así le caben DOS páginas de 416 líneas
    /// (0x0000 y 0x8200) en el plano de 64 KB y hace page flipping por CRTC 0x0C.
    /// CRTC virgen (todo a cero, que es como arranca: la BIOS no programa el CRTC) = sin
    /// recorte, comportamiento de siempre para todos los demás modos.
    /// </summary>
    public int ActiveHeight
    {
        get
        {
            int vbstart = CrtcRegs[0x15] | ((CrtcRegs[0x07] & 0x08) << 5) | ((CrtcRegs[0x09] & 0x20) << 4);
            if (vbstart == 0) return Height;
            vbstart++;
            return vbstart < Height ? vbstart : Height;
        }
    }

    // Sequencer registers full array — vga_seq.cpp
    public readonly byte[] SeqRegs = new byte[8];

    // DAC state — vga_dac.cpp
    public byte DacState;
    public byte DacPelMask = 0xFF;

    // Attribute Controller index register and video-enable bit — vga_attr.cpp
    public byte AttrIndex;
    public bool AttrVideoEnabled = true;

    // Miscellaneous Output Register — vga_misc.cpp
    public byte MiscOutput = 0x01;

    // CGA/MDA port registers — vga_other.cpp
    public byte CgaModeControl;
    public byte CgaColorSelect;
    public byte MdaModeControl;

    // Graphics Controller (index 0x3CE, data 0x3CF)
    public byte GcIndex;
    public byte SetReset;
    public byte EnableSetReset;
    public byte ColorCompare;
    public byte DataRotate;
    public byte ReadMapSelect;
    public byte GraphicsMode;     // reg 5: bits [1:0]=writeMode, bit[3]=readMode, bit[4]=odd/even, bits[6:5]=shift
    public byte Miscellaneous;    // reg 6: bit[0]=graphics, bits[3:2]=memory map
    public byte ColorDontCare;
    public byte BitMask = 0xFF;

    // Sequencer (index 0x3C4, data 0x3C5)
    public byte SeqIndex;
    public byte MapMask = 0x0F;

    // Latch: 4 bytes leídos de VRAM en la última lectura (uno por plano)
    public readonly byte[] Latch = new byte[4];

    // CGA palette selection (set via INT 10h AH=0Bh)
    public byte CgaPalette;
    public byte CgaBackground;

    // Modo de vídeo actual. Estado de ARRANQUE = el de un PC recién booteado a DOS:
    // modo texto 3 (80x25, 720x400) con la página de texto llena de espacios 0x20
    // attr 0x07 — igual que el oráculo DOSBox-X (verificado con el arnés del barrido,
    // antes arrancaba en modo 0/320x200 con planos a cero).
    public byte VideoMode = 3;
    public int Width = 720;
    public int Height = 400;

    /// <summary>Notificación de cambio de modo (INT 10h AH=00). El driver de ratón la usa para
    /// rederivar su rango y escala como hace el driver real (DOSBox-X Mouse_AfterNewVideoMode).</summary>
    public Action<byte>? ModeChanged;

    public VideoState()
    {
        for (int i = 0; i < 80 * 25; i++)
        {
            Planes[0][i] = 0x20;
            Planes[1][i] = 0x07;
        }
        // Y la FUENTE en el plano 2, que es donde vive en una VGA: el estado de arranque es el de
        // un PC que ya ha hecho su POST, y el BIOS deja ahí la ROM de 8x16 del modo 3 (32 bytes
        // por carácter). Hace falta porque un juego con `videoMode` en su game.json —o cualquiera
        // que escriba B8000 sin pedir set-mode— nunca pasa por INT 10h AH=00, y sin esto el plano
        // 2 se quedaría a cero: pantalla en blanco con el programa escribiendo tan tranquilo.
        for (int c = 0; c < 256; c++)
        for (int f = 0; f < 16; f++)
            Planes[2][c * 32 + f] = RomFonts.Font8x16[c * 16 + f];
    }
    public bool IsText => VideoMode is 0 or 1 or 2 or 3 or 7;
    public bool IsCga => VideoMode is 4 or 5 or 6;
    public bool IsMode13h => VideoMode == 0x13;

    /// <summary>Modo de 256 COLORES según los REGISTROS, no según el número de modo que dejó el
    /// BIOS: lo dice el bit 6 del registro 5 del Graphics Controller ("256-color shift"), que es
    /// donde lo mira el hardware (dosbox-x <c>VGA_DetermineMode</c> en vga_draw.cpp).
    ///
    /// Hace falta porque un juego puede fijar un modo por <c>INT 10h</c> y luego reprogramar los
    /// registros a mano. Un juego de prueba lo hace: pide el modo 12h (640x480 de 16 colores) y programa un
    /// Mode-X de 320x200x256. Decidiendo por <c>VideoMode == 0x13</c>, su VRAM se escribía por el
    /// camino planar de 16 colores y se pintaba como CGA de cuatro — la ventana salía NEGRA
    /// mientras el juego dibujaba tan contento.
    ///
    /// Para los juegos probados no cambia nada, y no es una esperanza: el set-mode 13h del BIOS deja ese bit
    /// puesto y NINGÚN otro modo lo pone, así que <c>Es256Color == IsMode13h</c> en todos ellos.
    /// El <c>VideoMode == 0x13</c> se conserva delante para el mundo sin set-mode, donde los
    /// registros están vírgenes.</summary>
    public bool Es256Color => VideoMode == 0x13 || ShiftRegisterMode == 2;

    /// <summary>Modo X: 256 colores SIN ENCADENAR. Lo dice el bit 3 (Chain-4) del registro 4 del
    /// Sequencer — a 1 es el modo 13h de toda la vida (memoria encadenada, un byte por píxel en
    /// un solo plano), a 0 el píxel x vive en el plano <c>x &amp; 3</c> del offset <c>x / 4</c>.
    ///
    /// Los juegos lo usan para tener páginas múltiples y scroll por hardware en 256 colores.
    /// Un juego DOS/4GW de prueba lo hace (se habían medido 39 escrituras al Sequencer), y sin esto su
    /// pantalla sale COMPRIMIDA A UN CUARTO Y REPETIDA CUATRO VECES: la firma exacta de leer datos
    /// planares como si fueran lineales.
    ///
    /// La puerta es justo el bit de encadenado, así que un juego encadenado —o sea, todos los
    /// juegos de 16 bits probados— no pasa por aquí ni cambia en nada.</summary>
    public bool IsModeX => Es256Color && (SeqRegs[4] & 0x08) == 0;

    // Spice86 MiscellaneousGraphicsRegister: memory map from GC reg 6 bits 2-3
    public int MemoryMapBase => ((Miscellaneous >> 2) & 3) switch
    {
        0 => 0xA0000,
        1 => 0xA0000,
        2 => 0xB0000,
        3 => 0xB8000,
        _ => 0xA0000,
    };
    public int MemoryMapSize => ((Miscellaneous >> 2) & 3) switch
    {
        0 => 0x20000, // 128K
        1 => 0x10000, // 64K
        2 => 0x8000,  // 32K
        3 => 0x8000,  // 32K
        _ => 0x10000,
    };

    // Spice86 VideoMemory.cs: odd/even mode from GC reg 5 bit 4
    public bool OddEvenMode => (GraphicsMode & 0x10) != 0;
    // Spice86 Renderer.cs: ShiftRegisterMode from GC reg 5 bits 5-6
    public int ShiftRegisterMode => (GraphicsMode >> 5) & 3;
    // Spice86 Renderer.cs: graphics mode from GC reg 6 bit 0
    public bool IsGraphicsFromRegs => (Miscellaneous & 1) != 0;

    public int WriteMode => GraphicsMode & 3;
    public int ReadMode => (GraphicsMode >> 3) & 1;
    public int RotateCount => DataRotate & 7;
    public int LogicOp => (DataRotate >> 3) & 3;

    // --- Acceso lineal para INT 10h (texto/CGA) ---
    // En modo odd/even, offset lineal se traduce: plane = offset & 1, addr = offset >> 1
    // Esto permite que Int10hHandler use offsets lineales (como CgaRam) sin saber de planos.
    public byte ReadLinear(int linearOff)
    {
        int plane = linearOff & 1;
        int addr = linearOff >> 1;
        return (uint)addr < PlaneSize ? Planes[plane][addr] : (byte)0;
    }
    public void WriteLinear(int linearOff, byte val)
    {
        int plane = linearOff & 1;
        int addr = linearOff >> 1;
        if ((uint)addr < PlaneSize) Planes[plane][addr] = val;
    }

    // --- VRAM write trace (solo con PlatformDebug.Trace: diagnóstico, no en hot path release) ---
    public int VramWriteTotal;
    public int VramWriteFF;
    public int VramWriteNonFF;
    public readonly List<(int offset, byte val)> VramTraceFirst = new(32);
    public readonly List<(int offset, byte val)> VramTraceLast = new(32);

    // --- VRAM Write/Read con pipeline EGA/VGA completo (Spice86 VideoMemory.cs) ---

    /// <summary>PAKU_TRAZA_EIP_VRAM=1: cuántas escrituras a la VRAM hace cada trozo de código, por
    /// EIP redondeado a 256 bytes. Es el primer paso de la PROCEDENCIA DE PÍXEL: saber qué píxeles
    /// de la pantalla pintó cada rutina, que es lo que permite sustituir el dibujo de UNA de ellas
    /// dejando intacto lo que las demás pintan encima.
    ///
    /// En un juego 3D de prueba hace falta para separar el fondo —que dibuja su motor 3D— de los
    /// sprites que el juego pinta después: la separación por paleta NO sirve (está
    /// medido: el mapa de color del fondo usa los 256 índices, así que no queda ninguno libre
    /// con el que reconocer un sprite).</summary>
    public static readonly bool TrazaEipVram =
        Environment.GetEnvironmentVariable("PAKU_TRAZA_EIP_VRAM") == "1";
    static readonly Dictionary<uint, long> _eipVram = new();

    /// <summary>PAKU_PROC_PIXEL=1: además de contar, RECUERDA por cada byte de plano qué trozo de
    /// código lo escribió el último. Con eso se reconstruye, píxel a píxel, quién pintó la pantalla
    /// — y eso es lo que permite sustituir el dibujo de UNA rutina dejando encima, intacto, lo que
    /// pintaron las demás: en un juego 3D, cambiar el fondo sin borrar los sprites de encima.
    ///
    /// Cuesta dos bytes por byte de VRAM (512 KB) y sólo se reserva con la palanca puesta. Los
    /// ids son de 16 bits desde que existe la procedencia de RAM (ver Memory.ProcDeLin): ahí
    /// consume un id cada bloque que escribe CUALQUIER byte de la RAM alta, no sólo los que
    /// pintan, y 254 ids se desbordaban seguro — con todo fundido en el id compartido el mapa
    /// lleva a conclusiones falsas.</summary>
    public static readonly bool ProcedenciaPixel =
        Environment.GetEnvironmentVariable("PAKU_PROC_PIXEL") == "1";
    static ushort[][]? _proc;
    static readonly Dictionary<uint, ushort> _idDeBloque = new();
    static readonly List<uint> _bloques = new();

    // El enganche va AQUÍ y no arriba: los inicializadores estáticos corren en orden de texto, y
    // puesto antes de `ProcedenciaPixel` esa palanca todavía valía false al consultarla.
    static readonly bool _eipVramEnganchado = EnganchaVolcadoEip();

    static VideoState? _lastWithProvenance;

    /// <summary>PAKU_PROC_PIXEL_EN=<segundos>: vuelca el mapa de procedencia EN ESE INSTANTE y no
    /// al salir. Hace falta porque al cerrar el juego suele estar en un menú o en la pantalla de
    /// carga, y el mapa sale dominado por lo que pintó esa pantalla en vez de por el juego.</summary>
    static readonly int _procEn =
        int.TryParse(Environment.GetEnvironmentVariable("PAKU_PROC_PIXEL_EN"), out var _pe) ? _pe : 0;
    static readonly long _procT0 = Environment.TickCount64;
    static bool _procVolcado;

    /// <summary>PROCEDENCIA ENCADENADA (la "fase 5" de la doctrina del segundo porteo, ahora
    /// también para el píxel): cuando un movs copia RAM→VRAM, el píxel no lo pintó el movs — lo
    /// pintó quien escribió el byte FUENTE en la RAM. Sin esto, un juego que compone en un buffer
    /// y lo presenta con un blitter (un juego 3D de prueba: el motor dibuja en RAM y `0x8055xx` sube el
    /// buffer a VRAM) sale con TODO atribuido al blitter, y el fondo es indistinguible de los
    /// sprites que el juego pintó encima. El intérprete deja aquí, por cada elemento de movs, el
    /// id del primer byte fuente (0 = fuente sin etiquetar, y entonces vale el EIP del movs).</summary>
    public static ushort IdHeredado;

    /// <summary>Granularidad del bloque de código de la procedencia: 64 bytes, no 256. Medido en
    /// un juego 3D de prueba: en el bloque de 256 de 0x824900 conviven el pintor del horizonte
    /// lejano (0x8248E0-0x824976, que hay que sustituir con el fondo HD) y otra rutina de
    /// dibujo (0x824982+, que hay que conservar) — con bloques de 256 eran inseparables y la
    /// banda del horizonte original cruzaba el fondo HD como una costura.</summary>
    public const uint BloqueMask = 0xFFFFFFC0;
    public const uint BloquePaso = 0x40;

    /// <summary>El id de un bloque de código (dirección redondeada con <see cref="BloqueMask"/>),
    /// creándolo si es la primera vez que escribe. Lo usan la marca de VRAM y la de RAM: los ids
    /// son EL MISMO espacio en las dos, que es lo que permite heredarlos de una a otra.</summary>
    public static ushort IdRegistra(uint bloque)
    {
        if (!_idDeBloque.TryGetValue(bloque, out var id))
        {
            // 0 queda para "nadie lo ha pintado", así que los IDs empiezan en 1. Si algún juego
            // escribiera desde más de 65534 sitios distintos, los que sobran comparten el 65535:
            // se dice en vez de renumerar en silencio, porque un mapa de procedencia con dos
            // rutinas fundidas en una lleva a conclusiones falsas.
            id = (ushort)(_bloques.Count < 65534 ? _bloques.Count + 1 : 65535);
            _idDeBloque[bloque] = id;
            if (id != 65535) _bloques.Add(bloque);
        }
        return id;
    }

    void MarcaProcedencia(int addr, byte mapMask)
    {
        _lastWithProvenance = this;
        if (_procEn > 0 && !_procVolcado && Environment.TickCount64 - _procT0 > _procEn * 1000L)
        {
            _procVolcado = true;
            VuelcaProcedencia("-vuelo");
        }
        var pr = _proc ??= new[] { new ushort[PlaneSize], new ushort[PlaneSize],
                                   new ushort[PlaneSize], new ushort[PlaneSize] };
        ushort id = IdHeredado != 0 ? IdHeredado
                                    : IdRegistra(Memory.UltimoEip32 & BloqueMask);
        for (int p = 0; p < 4; p++)
            if ((mapMask & (1 << p)) != 0) pr[p][addr] = id;
    }

    /// <summary>El ID que le tocó a un trozo de código, o 0 si todavía no ha escrito nada. El
    /// bloque es la dirección redondeada a 256 bytes.</summary>
    public static ushort IdDeBloque(uint bloque) =>
        _idDeBloque.TryGetValue(bloque & BloqueMask, out var id) ? id : (ushort)0;

    /// <summary>Quién pintó cada píxel de la pantalla, con la MISMA geometría con la que se
    /// presenta (start del CRTC, pitch, line compare y Mode-X). Devuelve los IDs y, aparte, a qué
    /// dirección de código corresponde cada uno.</summary>
    public bool TryGetProcedencia(out ushort[] ids, out uint[] bloques, out int ancho, out int alto)
    {
        ids = Array.Empty<ushort>(); bloques = _bloques.ToArray();
        ancho = _indicesW; alto = _indicesH;
        var pr = _proc;
        if (pr == null || !Es256Color || _indicesW <= 0 || _indicesH <= 0) return false;

        int addrShift = (CrtcRegs[0x14] & 0x40) != 0 ? 2
                      : (CrtcRegs[0x17] & 0x40) != 0 ? 0 : 1;
        bool planar = IsModeX;
        int start = planar
            ? ((CrtcRegs[0x0C] << 8) | CrtcRegs[0x0D]) + ((CrtcRegs[0x08] >> 5) & 3)
            : ((CrtcRegs[0x0C] << 8) | CrtcRegs[0x0D]) << addrShift;
        int pitch = planar ? CrtcRegs[0x13] * 2 : CrtcRegs[0x13] * (2 << addrShift);
        if (pitch == 0) pitch = planar ? 80 : 320;
        int lineCompare = SplitRow();

        ids = new ushort[ancho * alto];
        for (int i = 0; i < ids.Length; i++)
        {
            int y = i / ancho, x = i - y * ancho;
            int rowBase = y < lineCompare ? start + y * pitch : (y - lineCompare) * pitch;
            ids[i] = planar
                ? pr[x & 3][(rowBase + (x >> 2)) & (PlaneSize - 1)]
                : pr[0][(rowBase + x) & (PlaneSize - 1)];
        }
        return true;
    }

    static bool EnganchaVolcadoEip()
    {
        if (TrazaEipVram || ProcedenciaPixel)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => VuelcaTrazaEipVram();
        return true;
    }

    public static void VuelcaTrazaEipVram()
    {
        if (TrazaEipVram && _eipVram.Count > 0)
        {
            long total = 0;
            foreach (var v in _eipVram.Values) total += v;
            Console.Error.WriteLine($"[EIPVRAM] {total:N0} escrituras a VRAM desde " +
                                    $"{_eipVram.Count} bloques de codigo; los que mas pintan:");
            int n = 0;
            foreach (var kv in _eipVram.OrderByDescending(k => k.Value))
            {
                if (n++ >= 12) break;
                Console.Error.WriteLine($"  0x{kv.Key:X8}-0x{kv.Key + 0xFF:X8}  {kv.Value,12:N0}" +
                                        $"  ({100.0 * kv.Value / total:F1} %)");
            }
        }
        VuelcaProcedencia("");
    }

    /// <summary>El mapa de procedencia a disco: `ids.bin` son DOS bytes (little-endian) por píxel
    /// con el ID del que lo pintó, y `bloques.txt` dice a qué dirección de código corresponde
    /// cada ID.</summary>
    static void VuelcaProcedencia(string sufijo)
    {
        var v2 = _lastWithProvenance;
        if (v2 == null || !v2.TryGetProcedencia(out var ids, out var bloques, out var w, out var h))
            return;
        try
        {
            Directory.CreateDirectory("logs");
            var crudo = new byte[ids.Length * 2];
            Buffer.BlockCopy(ids, 0, crudo, 0, crudo.Length);
            File.WriteAllBytes(Path.Combine("logs", $"procedencia{sufijo}-ids.bin"), crudo);
            // Y la máscara CAPTURADA con el último front buffer, aparte: es la que casa píxel a
            // píxel con el frame presentado (PAKU_FB_PNG); la de arriba es la viva, un frame más
            // nueva. Cruzar el frame con la viva enseña el halo; con ésta, no debe haberlo.
            if (v2.TryGetFrontProcedencia(out var fids, out var fw2, out var fh2) && fids.Length > 0)
            {
                var crudo2 = new byte[fids.Length * 2];
                Buffer.BlockCopy(fids, 0, crudo2, 0, crudo2.Length);
                File.WriteAllBytes(Path.Combine("logs", $"procedencia{sufijo}-front-ids.bin"), crudo2);
                File.WriteAllText(Path.Combine("logs", $"procedencia{sufijo}-front-dim.txt"),
                                  $"{fw2} {fh2}\n");
            }
            var sb = new System.Text.StringBuilder($"{w} {h}\n");
            for (int i = 0; i < bloques.Length; i++)
                sb.Append(i + 1).Append(' ').Append(bloques[i].ToString("X8")).Append('\n');
            File.WriteAllText(Path.Combine("logs", $"procedencia{sufijo}-bloques.txt"), sb.ToString());
            Console.Error.WriteLine($"[PROCPIXEL] mapa de {w}x{h} -> " +
                                    $"logs/procedencia{sufijo}-ids.bin ({bloques.Length} bloques)");
        }
        catch { }
    }

    // ── SVGA/VBE (VESA 2.0 por INT 10h AH=4Fh, Int10hHandler) ──────────────────────────────
    // Los modos VESA de 8 bpp son memoria LINEAL de hasta 1 MiB vista por una VENTANA de 64 KiB
    // en A000, colocada por el banco (VBE 4F05). Nada de planos ni latches: un byte = un píxel.
    // Primer cliente: un juego DOS/4GW Pro que exige un driver VESA y pinta en el modo 0x101 (640x480).
    /// <summary>VRAM lineal SVGA (1 MiB), viva sólo mientras un modo VESA está puesto.</summary>
    public byte[]? SvgaVram;
    /// <summary>True mientras el modo actual es un modo VESA de 8 bpp (VBE 4F02).</summary>
    public bool SvgaActivo;
    /// <summary>Banco de la ventana A000 en unidades de 64 KiB (VBE 4F05).</summary>
    public int SvgaBank;
    /// <summary>Número de modo VESA vigente (lo devuelve VBE 4F03).</summary>
    public ushort SvgaModo;
    /// <summary>Bytes por línea lógica (VBE 4F06); por defecto, el ancho del modo.</summary>
    public int SvgaStride;
    /// <summary>Primer byte presentado (VBE 4F07, display start), en bytes desde 0.</summary>
    public int SvgaStart;
    /// <summary>Display start por la interfaz PM (puertos 0xE99A/0xE99C, ver VgaPortHandler).</summary>
    public int SvgaStartX, SvgaStartY;
    public void RecalculaSvgaStart() => SvgaStart = SvgaStartY * Math.Max(1, SvgaStride) + SvgaStartX;

    public void WriteVram(int offset, byte cpuData)
    {
        // Perfil de capas (PAKU_CAPAS): quien escribe en la VRAM es la capa de RENDER, y esto es
        // el unico sitio por el que pasa una escritura de video. A diferencia de TrazaEipVram,
        // que se apoya en Memory.UltimoEip32 —que solo escribe Interp32 y por tanto no existe en
        // el mundo de 16 bits—, esto usa el anillo de la caja negra y vale para todos los juegos.
        if (PerfilCapas.Activo) PerfilCapas.Nota(PerfilCapas.TipoVram, 0);
        if (TrazaEipVram)
        {
            uint bloque = Memory.UltimoEip32 & 0xFFFFFF00;
            _eipVram.TryGetValue(bloque, out var _c);
            _eipVram[bloque] = _c + 1;
        }
        if (PlatformDebug.Trace)
        {
            VramWriteTotal++;
            if (cpuData == 0xFF) VramWriteFF++; else VramWriteNonFF++;
            if (VramTraceFirst.Count < 20) VramTraceFirst.Add((offset, cpuData));
            if (VramTraceLast.Count >= 20) VramTraceLast.RemoveAt(0);
            VramTraceLast.Add((offset, cpuData));
        }
        if (SvgaActivo)
        {
            // VESA 8 bpp: memoria lineal a través de la ventana bancada; sin planos ni latches.
            int aSvga = (SvgaBank << 16) + offset;
            var vr = SvgaVram;
            if (vr != null && (uint)aSvga < (uint)vr.Length) vr[aSvga] = cpuData;
            return;
        }
        if (Es256Color)
        {
            // MODO X (256 colores SIN encadenar): NO tiene camino propio, cae al de abajo —
            // que es exactamente el que necesita. Allí el offset ya es el mismo para los cuatro
            // planos, `MapMask` elige a cuáles va, y se respetan los CUATRO modos de escritura con
            // sus latches, su set/reset, su rotación y su máscara de bits. Odd/even está apagado
            // por definición en Mode-X, así que el único ajuste de aquel camino no le aplica.
            //
            // Tener aquí una rama propia que escribía `cpuData` en los cuatro planos era la causa
            // de que las imágenes salieran a un cuarto de resolución: la copia por latches (modo 1)
            // es como se blitea en Mode-X, y esta rama la servía como si fuera una escritura
            // normal. Medido en un juego Mode-X: 70,4 % de cuartetos de píxeles idénticos, contra 42,3 %
            // en el oráculo. Reducirlo a "no hay caso especial" arregla de paso los modos 0 con
            // set/reset, 2 y 3, que tampoco llegaban.
            if (!IsModeX)
            {
                if ((uint)offset < PlaneSize) Planes[0][offset] = cpuData;
                return;
            }
        }

        byte mapMask = MapMask;
        int addr = offset;

        // Spice86: odd/even mode — address bit 0 forces plane pair
        if (OddEvenMode)
        {
            byte forced = (byte)((offset & 1) == 0 ? 0b0101 : 0b1010);
            mapMask &= forced;
            addr = offset >> 1;
        }

        if ((uint)addr >= PlaneSize) return;
        if (ProcedenciaPixel) MarcaProcedencia(addr, mapMask);

        byte bitmask = BitMask;

        switch (WriteMode)
        {
            case 0:
            {
                byte rotated = RotateByte(cpuData, RotateCount);
                for (int p = 0; p < 4; p++)
                {
                    if ((mapMask & (1 << p)) == 0) continue;
                    byte src = ((EnableSetReset >> p) & 1) != 0
                        ? (byte)(((SetReset >> p) & 1) != 0 ? 0xFF : 0x00)
                        : rotated;
                    byte val = ApplyLogic(src, Latch[p]);
                    Planes[p][addr] = (byte)((val & bitmask) | (Latch[p] & ~bitmask));
                }
                break;
            }
            case 1:
            {
                for (int p = 0; p < 4; p++)
                    if ((mapMask & (1 << p)) != 0)
                        Planes[p][addr] = Latch[p];
                break;
            }
            case 2:
            {
                for (int p = 0; p < 4; p++)
                {
                    if ((mapMask & (1 << p)) == 0) continue;
                    byte expanded = (byte)(((cpuData >> p) & 1) != 0 ? 0xFF : 0x00);
                    byte val = ApplyLogic(expanded, Latch[p]);
                    Planes[p][addr] = (byte)((val & bitmask) | (Latch[p] & ~bitmask));
                }
                break;
            }
            case 3:
            {
                byte rotated = RotateByte(cpuData, RotateCount);
                byte dynMask = (byte)(rotated & bitmask);
                for (int p = 0; p < 4; p++)
                {
                    if ((mapMask & (1 << p)) == 0) continue;
                    byte color = (byte)(((SetReset >> p) & 1) != 0 ? 0xFF : 0x00);
                    Planes[p][addr] = (byte)((color & dynMask) | (Latch[p] & ~dynMask));
                }
                break;
            }
        }
    }

    public byte ReadVram(int offset)
    {
        if (SvgaActivo)
        {
            int aSvga = (SvgaBank << 16) + offset;
            var vr = SvgaVram;
            return vr != null && (uint)aSvga < (uint)vr.Length ? vr[aSvga] : (byte)0;
        }
        int addr = offset;
        int readPlane = ReadMapSelect & 3;

        if (Es256Color)
        {
            if (IsModeX)
            {
                if ((uint)offset >= PlaneSize) return 0;
                // TODA lectura de VRAM carga los cuatro LATCHES, uno por plano — pase lo que pase
                // con el byte que se devuelve. Sin esto, la copia por latches del modo de escritura
                // 1 no tiene qué copiar, y es como los juegos blitean en Mode-X: leer aquí para
                // cargar, escribir allí para volcar.
                for (int p = 0; p < 4; p++) Latch[p] = Planes[p][offset];
                return Planes[ReadMapSelect & 3][offset];
            }
            return (uint)offset < PlaneSize ? Planes[0][offset] : (byte)0;
        }

        if (OddEvenMode)
        {
            readPlane = offset & 1;
            addr = offset >> 1;
        }

        if ((uint)addr >= PlaneSize) return 0;
        for (int p = 0; p < 4; p++)
            Latch[p] = Planes[p][addr];

        if (ReadMode == 0)
            return Latch[readPlane];

        byte result = 0xFF;
        for (int p = 0; p < 4; p++)
        {
            if ((ColorDontCare & (1 << p)) == 0) continue;
            byte expected = (byte)(((ColorCompare >> p) & 1) != 0 ? 0xFF : 0x00);
            result &= (byte)~(Latch[p] ^ expected);
        }
        return result;
    }

    public void WriteGcRegister(byte val)
    {
        switch (GcIndex)
        {
            case 0: SetReset = (byte)(val & 0x0F); break;
            case 1: EnableSetReset = (byte)(val & 0x0F); break;
            case 2: ColorCompare = (byte)(val & 0x0F); break;
            case 3: DataRotate = val; break;
            case 4: ReadMapSelect = (byte)(val & 3); break;
            case 5: GraphicsMode = val; break;
            case 6: Miscellaneous = val; break;
            case 7: ColorDontCare = (byte)(val & 0x0F); break;
            case 8: BitMask = val; break;
        }
    }

    public void WriteSeqRegister(byte val)
    {
        switch (SeqIndex)
        {
            case 2: MapMask = (byte)(val & 0x0F); break;
        }
    }

    public byte ReadGcRegister() => GcIndex switch
    {
        0 => SetReset,
        1 => EnableSetReset,
        2 => ColorCompare,
        3 => DataRotate,
        4 => ReadMapSelect,
        5 => GraphicsMode,
        6 => Miscellaneous,
        7 => ColorDontCare,
        8 => BitMask,
        _ => 0,
    };

    // --- Paletas ---

    public void LoadDefaultVgaPalette()
    {
        byte[] ega = {
            0x00,0x00,0x00, 0x00,0x00,0x2A, 0x00,0x2A,0x00, 0x00,0x2A,0x2A,
            0x2A,0x00,0x00, 0x2A,0x00,0x2A, 0x2A,0x15,0x00, 0x2A,0x2A,0x2A,
            0x15,0x15,0x15, 0x15,0x15,0x3F, 0x15,0x3F,0x15, 0x15,0x3F,0x3F,
            0x3F,0x15,0x15, 0x3F,0x15,0x3F, 0x3F,0x3F,0x15, 0x3F,0x3F,0x3F,
        };
        // A real VGA BIOS loads the 64-entry EGA-compatible palette into the DAC on every
        // 16-color set-mode (DOSBox-X int10_modes.cpp ega_palette[64], loaded at 2218-2236).
        // With only 16 entries, BIOS palette regs {10h-17h} (which games set via AX=1002h,
        // e.g. one game's VGA menu, color 17h) map to zeroed DAC entries -> pure black screen.
        // Verified equivalence: ega_palette[i] == ega[(i&7) | ((i&0x10)>>1)].
        for (int i = 0; i < 64; i++)
        {
            int s = ((i & 7) | ((i & 0x10) >> 1)) * 3;
            Dac[i * 3] = ega[s]; Dac[i * 3 + 1] = ega[s + 1]; Dac[i * 3 + 2] = ega[s + 2];
        }
    }

    // Paleta VGA de 256 colores por defecto: 16 EGA + 16 grises (rampa no lineal)
    // + 216 HSV + 8 negras. MEDIDA del oraculo (DOSBox-X svga_s3) con
    // tools/dac_sweep/DAC.COM, no copiada de memoria; regenerable con
    // `python tools/dac_sweep/gen_palette_cs.py` y verificada byte a byte por
    // tools/dac_sweep/judge_dac.py. Valores en unidades de 6 bits del DAC.
    private static readonly byte[] Vga256Palette =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x2A, 0x00, 0x00, 0x2A, 0x2A, 0x2A, 0x00, 0x00, 0x2A, 0x00, 0x2A, 0x2A, 0x15, 0x00, 0x2A, 0x2A, 0x2A,
        0x15, 0x15, 0x15, 0x15, 0x15, 0x3F, 0x15, 0x3F, 0x15, 0x15, 0x3F, 0x3F, 0x3F, 0x15, 0x15, 0x3F, 0x15, 0x3F, 0x3F, 0x3F, 0x15, 0x3F, 0x3F, 0x3F,
        0x00, 0x00, 0x00, 0x05, 0x05, 0x05, 0x08, 0x08, 0x08, 0x0B, 0x0B, 0x0B, 0x0E, 0x0E, 0x0E, 0x11, 0x11, 0x11, 0x14, 0x14, 0x14, 0x18, 0x18, 0x18,
        0x1C, 0x1C, 0x1C, 0x20, 0x20, 0x20, 0x24, 0x24, 0x24, 0x28, 0x28, 0x28, 0x2D, 0x2D, 0x2D, 0x32, 0x32, 0x32, 0x38, 0x38, 0x38, 0x3F, 0x3F, 0x3F,
        0x00, 0x00, 0x3F, 0x10, 0x00, 0x3F, 0x1F, 0x00, 0x3F, 0x2F, 0x00, 0x3F, 0x3F, 0x00, 0x3F, 0x3F, 0x00, 0x2F, 0x3F, 0x00, 0x1F, 0x3F, 0x00, 0x10,
        0x3F, 0x00, 0x00, 0x3F, 0x10, 0x00, 0x3F, 0x1F, 0x00, 0x3F, 0x2F, 0x00, 0x3F, 0x3F, 0x00, 0x2F, 0x3F, 0x00, 0x1F, 0x3F, 0x00, 0x10, 0x3F, 0x00,
        0x00, 0x3F, 0x00, 0x00, 0x3F, 0x10, 0x00, 0x3F, 0x1F, 0x00, 0x3F, 0x2F, 0x00, 0x3F, 0x3F, 0x00, 0x2F, 0x3F, 0x00, 0x1F, 0x3F, 0x00, 0x10, 0x3F,
        0x1F, 0x1F, 0x3F, 0x27, 0x1F, 0x3F, 0x2F, 0x1F, 0x3F, 0x37, 0x1F, 0x3F, 0x3F, 0x1F, 0x3F, 0x3F, 0x1F, 0x37, 0x3F, 0x1F, 0x2F, 0x3F, 0x1F, 0x27,
        0x3F, 0x1F, 0x1F, 0x3F, 0x27, 0x1F, 0x3F, 0x2F, 0x1F, 0x3F, 0x37, 0x1F, 0x3F, 0x3F, 0x1F, 0x37, 0x3F, 0x1F, 0x2F, 0x3F, 0x1F, 0x27, 0x3F, 0x1F,
        0x1F, 0x3F, 0x1F, 0x1F, 0x3F, 0x27, 0x1F, 0x3F, 0x2F, 0x1F, 0x3F, 0x37, 0x1F, 0x3F, 0x3F, 0x1F, 0x37, 0x3F, 0x1F, 0x2F, 0x3F, 0x1F, 0x27, 0x3F,
        0x2D, 0x2D, 0x3F, 0x31, 0x2D, 0x3F, 0x36, 0x2D, 0x3F, 0x3A, 0x2D, 0x3F, 0x3F, 0x2D, 0x3F, 0x3F, 0x2D, 0x3A, 0x3F, 0x2D, 0x36, 0x3F, 0x2D, 0x31,
        0x3F, 0x2D, 0x2D, 0x3F, 0x31, 0x2D, 0x3F, 0x36, 0x2D, 0x3F, 0x3A, 0x2D, 0x3F, 0x3F, 0x2D, 0x3A, 0x3F, 0x2D, 0x36, 0x3F, 0x2D, 0x31, 0x3F, 0x2D,
        0x2D, 0x3F, 0x2D, 0x2D, 0x3F, 0x31, 0x2D, 0x3F, 0x36, 0x2D, 0x3F, 0x3A, 0x2D, 0x3F, 0x3F, 0x2D, 0x3A, 0x3F, 0x2D, 0x36, 0x3F, 0x2D, 0x31, 0x3F,
        0x00, 0x00, 0x1C, 0x07, 0x00, 0x1C, 0x0E, 0x00, 0x1C, 0x15, 0x00, 0x1C, 0x1C, 0x00, 0x1C, 0x1C, 0x00, 0x15, 0x1C, 0x00, 0x0E, 0x1C, 0x00, 0x07,
        0x1C, 0x00, 0x00, 0x1C, 0x07, 0x00, 0x1C, 0x0E, 0x00, 0x1C, 0x15, 0x00, 0x1C, 0x1C, 0x00, 0x15, 0x1C, 0x00, 0x0E, 0x1C, 0x00, 0x07, 0x1C, 0x00,
        0x00, 0x1C, 0x00, 0x00, 0x1C, 0x07, 0x00, 0x1C, 0x0E, 0x00, 0x1C, 0x15, 0x00, 0x1C, 0x1C, 0x00, 0x15, 0x1C, 0x00, 0x0E, 0x1C, 0x00, 0x07, 0x1C,
        0x0E, 0x0E, 0x1C, 0x11, 0x0E, 0x1C, 0x15, 0x0E, 0x1C, 0x18, 0x0E, 0x1C, 0x1C, 0x0E, 0x1C, 0x1C, 0x0E, 0x18, 0x1C, 0x0E, 0x15, 0x1C, 0x0E, 0x11,
        0x1C, 0x0E, 0x0E, 0x1C, 0x11, 0x0E, 0x1C, 0x15, 0x0E, 0x1C, 0x18, 0x0E, 0x1C, 0x1C, 0x0E, 0x18, 0x1C, 0x0E, 0x15, 0x1C, 0x0E, 0x11, 0x1C, 0x0E,
        0x0E, 0x1C, 0x0E, 0x0E, 0x1C, 0x11, 0x0E, 0x1C, 0x15, 0x0E, 0x1C, 0x18, 0x0E, 0x1C, 0x1C, 0x0E, 0x18, 0x1C, 0x0E, 0x15, 0x1C, 0x0E, 0x11, 0x1C,
        0x14, 0x14, 0x1C, 0x16, 0x14, 0x1C, 0x18, 0x14, 0x1C, 0x1A, 0x14, 0x1C, 0x1C, 0x14, 0x1C, 0x1C, 0x14, 0x1A, 0x1C, 0x14, 0x18, 0x1C, 0x14, 0x16,
        0x1C, 0x14, 0x14, 0x1C, 0x16, 0x14, 0x1C, 0x18, 0x14, 0x1C, 0x1A, 0x14, 0x1C, 0x1C, 0x14, 0x1A, 0x1C, 0x14, 0x18, 0x1C, 0x14, 0x16, 0x1C, 0x14,
        0x14, 0x1C, 0x14, 0x14, 0x1C, 0x16, 0x14, 0x1C, 0x18, 0x14, 0x1C, 0x1A, 0x14, 0x1C, 0x1C, 0x14, 0x1A, 0x1C, 0x14, 0x18, 0x1C, 0x14, 0x16, 0x1C,
        0x00, 0x00, 0x10, 0x04, 0x00, 0x10, 0x08, 0x00, 0x10, 0x0C, 0x00, 0x10, 0x10, 0x00, 0x10, 0x10, 0x00, 0x0C, 0x10, 0x00, 0x08, 0x10, 0x00, 0x04,
        0x10, 0x00, 0x00, 0x10, 0x04, 0x00, 0x10, 0x08, 0x00, 0x10, 0x0C, 0x00, 0x10, 0x10, 0x00, 0x0C, 0x10, 0x00, 0x08, 0x10, 0x00, 0x04, 0x10, 0x00,
        0x00, 0x10, 0x00, 0x00, 0x10, 0x04, 0x00, 0x10, 0x08, 0x00, 0x10, 0x0C, 0x00, 0x10, 0x10, 0x00, 0x0C, 0x10, 0x00, 0x08, 0x10, 0x00, 0x04, 0x10,
        0x08, 0x08, 0x10, 0x0A, 0x08, 0x10, 0x0C, 0x08, 0x10, 0x0E, 0x08, 0x10, 0x10, 0x08, 0x10, 0x10, 0x08, 0x0E, 0x10, 0x08, 0x0C, 0x10, 0x08, 0x0A,
        0x10, 0x08, 0x08, 0x10, 0x0A, 0x08, 0x10, 0x0C, 0x08, 0x10, 0x0E, 0x08, 0x10, 0x10, 0x08, 0x0E, 0x10, 0x08, 0x0C, 0x10, 0x08, 0x0A, 0x10, 0x08,
        0x08, 0x10, 0x08, 0x08, 0x10, 0x0A, 0x08, 0x10, 0x0C, 0x08, 0x10, 0x0E, 0x08, 0x10, 0x10, 0x08, 0x0E, 0x10, 0x08, 0x0C, 0x10, 0x08, 0x0A, 0x10,
        0x0B, 0x0B, 0x10, 0x0C, 0x0B, 0x10, 0x0D, 0x0B, 0x10, 0x0F, 0x0B, 0x10, 0x10, 0x0B, 0x10, 0x10, 0x0B, 0x0F, 0x10, 0x0B, 0x0D, 0x10, 0x0B, 0x0C,
        0x10, 0x0B, 0x0B, 0x10, 0x0C, 0x0B, 0x10, 0x0D, 0x0B, 0x10, 0x0F, 0x0B, 0x10, 0x10, 0x0B, 0x0F, 0x10, 0x0B, 0x0D, 0x10, 0x0B, 0x0C, 0x10, 0x0B,
        0x0B, 0x10, 0x0B, 0x0B, 0x10, 0x0C, 0x0B, 0x10, 0x0D, 0x0B, 0x10, 0x0F, 0x0B, 0x10, 0x10, 0x0B, 0x0F, 0x10, 0x0B, 0x0D, 0x10, 0x0B, 0x0C, 0x10,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    /// <summary>Set-mode de 256 colores: el DAC entero, no solo los 16 primeros. Antes se
    /// copiaban las 16 entradas EGA y se hacia Array.Clear del resto, asi que TODO indice
    /// >= 16 se renderizaba negro aunque el juego escribiera el pixel. No mordio a los juegos probados
    /// porque los juegos de 256 colores programan su propia paleta, pero divergia del
    /// oraculo en 612 de 768 bytes (medido por tools/dac_sweep/judge_dac.py).</summary>
    public void LoadDefaultVgaPalette256()
    {
        Array.Copy(Vga256Palette, 0, Dac, 0, Vga256Palette.Length);
    }

    public void ApplyPaletteRemap(byte[] remap)
    {
        byte[] ega = {
            0x00,0x00,0x00, 0x00,0x00,0x2A, 0x00,0x2A,0x00, 0x00,0x2A,0x2A,
            0x2A,0x00,0x00, 0x2A,0x00,0x2A, 0x2A,0x15,0x00, 0x2A,0x2A,0x2A,
            0x15,0x15,0x15, 0x15,0x15,0x3F, 0x15,0x3F,0x15, 0x15,0x3F,0x3F,
            0x3F,0x15,0x15, 0x3F,0x15,0x3F, 0x3F,0x3F,0x15, 0x3F,0x3F,0x3F,
        };
        for (int i = 0; i < 16 && i < remap.Length; i++)
        {
            int src = remap[i] * 3;
            int dst = i * 3;
            Dac[dst] = ega[src]; Dac[dst + 1] = ega[src + 1]; Dac[dst + 2] = ega[src + 2];
        }
    }

    public void LoadPaletteFromFile(string path)
    {
        var data = File.ReadAllBytes(path);
        Array.Copy(data, 0, Dac, 0, Math.Min(data.Length, 768));
    }

    // Retrace
    public bool InVerticalRetrace;
    public long RetraceCounter;
    public bool HostDrivenRetrace;
    /// <summary>Reloj de pared para el retrace VGA real (70 Hz): los busy-waits de vsync
    /// (juego e intros: poll de 0x3DA bit 3) deben ver el ciclo del HARDWARE, no el frame
    /// del host (30 FPS con toggle por frame = ciclo de 1/15 s → intro ~5× lenta).</summary>
    public readonly System.Diagnostics.Stopwatch RetraceClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>El <c>CyclesPerMs</c> del governor, refrescado por <see cref="Machine.MachineTimer"/>
    /// en cada poll. Es lo que convierte instrucciones en milisegundos VIRTUALES, y con eso el
    /// retrace puede ir por el reloj del invitado en vez de por el del anfitrion cuando hay una
    /// cinta puesta (ver <see cref="VgaPortHandler"/>). Cero si no hay governor calibrado.</summary>
    public int CyclesPerMs;

    // --- VGA frame presentation (front buffer), hardware model ---
    // On real hardware the CRTC scans VRAM out to the monitor at a fixed refresh rate (~70 Hz
    // for the standard 320x200/text modes) INDEPENDENTLY of what the program does. The monitor
    // only ever shows whatever is in VRAM at scan-out time; a program that erases and redraws a
    // sprite/cursor does so between scan-outs, and because the redraw is far shorter than the
    // frame period the monitor practically never catches the "erased" instant. Reading the
    // Input Status register (0x3DA bit 3) only REPORTS the retrace phase — it does not trigger a
    // scan-out.
    //
    // We reproduce this with a SINGLE front buffer captured on the game thread by a clock-driven
    // refresh tick (MachineTimer, 70 Hz real time), and the host render thread always presents
    // that buffer. There is exactly one capturer and one presenter, so no two samples race for
    // the same buffer (the previous design had a wall-clock retrace latch AND an INT33-poll latch
    // fighting over one buffer, plus a staleness fallback to live VRAM — three sources sampling
    // the erase/redraw at different phases, which is what made an asynchronous software cursor
    // flicker). Because the capture is clock-driven and not tied to the game polling 0x3DA, a
    // static menu that stops polling stays presented (the last captured frame persists, exactly
    // as a real monitor keeps scanning unchanged VRAM) — no staleness heuristic needed. A game
    // that blocks in an input wait (INT 16h) captures once before parking so the completed menu
    // is guaranteed on screen. Capture is inert unless the host drives the display (HostDrivenRetrace
    // is false in the verifier/explorer/headless dumps, which call RenderFramebuffer directly).
    private byte[]? _frontBuffer;
    private int _frontW, _frontH;
    private readonly object _frontLock = new();
    public long FrameGeneration;   // increments on each capture (diagnostics / present-changed checks)

    /// <summary>Capture the current VRAM as the presented frame ("front buffer"). Called on the
    /// GAME thread by the clock-driven VGA refresh tick (MachineTimer, ~70 Hz real time) and once
    /// before an INT 16h blocking wait. No-op unless the host drives the display, so it never runs
    /// in the deterministic verifier path.</summary>
    // Capture is CLOCK-DRIVEN and ASYNCHRONOUS to the game's cursor cycle — that is the whole
    // point, and it mirrors how a real VGA CRTC scans VRAM out to the monitor on its own clock.
    // Do NOT capture at the INT 33h AH=03 poll. Measured on a test game in the oracle (DOSBox):
    // the software cursor is a save-under sprite whose move cycle is HIDE (restore background) then
    // SHOW, and SHOW *polls INT 33h AH=03 and only AFTER that draws the sprite*. So at the instant
    // of the poll the cursor is ERASED, not "freshly drawn" — capturing at the poll snapshots the
    // one frame per redraw that is guaranteed cursorless, which is exactly the move-flicker the
    // user reported. The cursor is present in VRAM the overwhelming majority of the time (redraw
    // is rare and brief — invisible even to a PC-sampling profiler on the oracle), so an async
    // wall-clock snapshot almost never lands in the tiny HIDE->SHOW gap, exactly like the oracle,
    // which shows no flicker. Throttle only coalesces a refresh tick that coincides with the
    // INT 16h pre-park capture.
    // SIN acelerador: el ritmo lo marca el tick de refresco, que ya es periódico por su propio
    // acumulador de tiempo real (MachineTimer). Hubo aquí un throttle de 10 ms medido con
    // Environment.TickCount64 y era un colador: la resolución de ese reloj en Windows es
    // ~15,6 ms, MAYOR que el periodo VGA de 14,286 ms, así que dos refrescos consecutivos caían
    // a menudo en la misma ranura del reloj y el segundo se TIRABA — ~10 % de los frames
    // perdidos y el intervalo p90 al doble del nominal (60 capturas/s con 9-11 descartadas,
    // medido con tools/present_rate_test). En un menú quieto no se nota; con la imagen en
    // movimiento es un tirón varias veces por segundo. Y ya había mordido antes: por él se
    // quedaba sin presentar el menú terminado antes del park de INT 16h (ver Int16hHandler).
    // Nada que coalescer: la ruta del park sólo captura en el modo legacy.
    /// <summary>PAKU_PRESENT_CAPTURE=1: captura los fotogramas presentados SIN ventana.
    ///
    /// Hasta aquí, la captura colgaba entera de <see cref="HostDrivenRetrace"/>, que sólo lo pone
    /// la plantilla del host con ventana. Consecuencias, las dos medidas: <c>FrameGeneration</c>
    /// se quedaba congelado a cero en cualquier corrida de captura —así que el ORDEN DE DIBUJO,
    /// que es lo que decide si un sprite va delante o detrás, había que estimarlo por huecos de
    /// tiempo— y <c>PAKU_PRESENT_DUMP</c> no podía volcar nada, así que el juez de fidelidad no
    /// podía correr solo.
    ///
    /// Va en una bandera APARTE y no reutilizando <c>HostDrivenRetrace</c> a propósito: aquélla
    /// además conmuta el retrace del VGA a reloj de pared (VgaPortHandler), y el juez necesita
    /// justo lo contrario — que las dos corridas de la misma cinta caigan en los mismos puntos
    /// del programa. Encenderla aquí habría hecho no determinista lo que se quería comparar.</summary>
    public static readonly bool PresentCapture =
        Environment.GetEnvironmentVariable("PAKU_PRESENT_CAPTURE") == "1"
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PAKU_PRESENT_DUMP"));

    public void CaptureFrontBuffer()
    {
        if (!HostDrivenRetrace && !PresentCapture) return;
        LogPresentRate();
        var fb = RenderFramebuffer();
        int w = Width, h = Height;
        // La máscara de procedencia se captura EN EL MISMO INSTANTE que el frame, y las dos
        // viajan juntas bajo el mismo lock. Si el host la leyera viva al presentar, la máscara
        // sería de un frame más nuevo que los píxeles —el juego sigue componiendo entre la
        // captura y la presentación— y en un sprite en movimiento ese desfase es un halo de
        // desalineación de 1-2 px alrededor (medido en un juego 3D: ~la mitad de los píxeles de un
        // sprite salían con la identidad del fondo al cruzar frame final contra máscara final).
        ushort[]? pids = null; int pw = 0, ph = 0;
        if (ProcedenciaPixel) TryGetProcedencia(out pids, out _, out pw, out ph);
        lock (_frontLock)
        {
            _frontBuffer = fb; _frontW = w; _frontH = h; FrameGeneration++;
            if (pids != null && pids.Length > 0) { _frontProc = pids; _frontProcW = pw; _frontProcH = ph; }
        }
        DumpPresentedFrame(fb, w, h);
    }

    ushort[]? _frontProc;
    int _frontProcW, _frontProcH;

    /// <summary>La máscara de procedencia capturada JUNTO al front buffer (misma captura, mismo
    /// lock). Es la que debe usar quien compone sobre el frame presentado; la viva
    /// (<see cref="TryGetProcedencia"/>) va un frame por delante de los píxeles.</summary>
    public bool TryGetFrontProcedencia(out ushort[] ids, out int ancho, out int alto)
    {
        lock (_frontLock)
        {
            if (_frontProc == null) { ids = Array.Empty<ushort>(); ancho = 0; alto = 0; return false; }
            ids = _frontProc; ancho = _frontProcW; alto = _frontProcH; return true;
        }
    }

    // PAKU_LOG_PRESENT=1: mide el RITMO REAL de presentación. Un frame que llega tarde o que se
    // pierde se ve como un tirón, y con una escena en movimiento (un simulador de vuelo) un
    // ritmo irregular se percibe como parpadeo. Reporta cada segundo cuántas capturas se
    // hicieron, cuántas descartó el acelerador y la distribución de los intervalos.
    private static readonly bool _logPresent = Environment.GetEnvironmentVariable("PAKU_LOG_PRESENT") == "1";
    private long _capDone;
    private readonly System.Diagnostics.Stopwatch _capClock = System.Diagnostics.Stopwatch.StartNew();
    private double _capLastMs, _capWindowStartMs;
    private readonly List<double> _capIntervals = new();

    private void LogPresentRate()
    {
        if (!_logPresent) return;
        double t = _capClock.Elapsed.TotalMilliseconds;
        _capDone++;
        if (_capLastMs > 0) _capIntervals.Add(t - _capLastMs);
        _capLastMs = t;
        if (t - _capWindowStartMs < 1000.0) return;
        _capWindowStartMs = t;
        if (_capIntervals.Count > 0)
        {
            var v = _capIntervals.OrderBy(x => x).ToList();
            Console.Error.WriteLine(
                $"[PRESENT] capturas={_capDone} " +
                $"intervalo ms: min={v[0]:F1} p50={v[v.Count / 2]:F1} p90={v[v.Count * 9 / 10]:F1} max={v[^1]:F1}");
            _capIntervals.Clear();
        }
        _capDone = 0;
    }

    // PAKU_PRESENT_DUMP=<prefijo>: vuelca los frames PRESENTADOS (uno por captura, que es
    // justo lo que ve el usuario) como RGBA crudo, para poder MIRAR una secuencia en vez de
    // fiarse de un solo pantallazo. Arranca a los PAKU_PRESENT_DUMP_AFTER_MS y para tras
    // PAKU_PRESENT_DUMP_N ficheros. Existe porque un parpadeo sólo se diagnostica viendo
    // frames CONSECUTIVOS: un fotograma suelto de un juego que redibuja toda la pantalla no
    // distingue "va bien" de "presentamos estados intermedios del dibujado".
    private static readonly string? _presentDump = Environment.GetEnvironmentVariable("PAKU_PRESENT_DUMP");
    private static readonly long _presentDumpAfterMs =
        long.TryParse(Environment.GetEnvironmentVariable("PAKU_PRESENT_DUMP_AFTER_MS"), out var _pa) ? _pa : 0;
    private static readonly int _presentDumpN =
        int.TryParse(Environment.GetEnvironmentVariable("PAKU_PRESENT_DUMP_N"), out var _pn) ? _pn : 24;
    /// <summary>PAKU_PRESENT_DUMP_AFTER_FRAMES=N: empieza a volcar tras N fotogramas CAPTURADOS
    /// en vez de tras N milisegundos de reloj de pared.
    ///
    /// Hace falta para comparar dos corridas de la misma cinta. Bajo `PAKU_TAPE_REPLAY` la
    /// CAPTURA ya es determinista (MachineTimer acumula tiempo VIRTUAL cuando el reloj está
    /// anclado), pero la puerta del volcado seguía siendo un cronómetro de pared: las dos
    /// corridas volcaban momentos distintos de la partida y salían distintas aunque el juego
    /// hubiera hecho exactamente lo mismo. Medido: con 40 s coincidían (el juego estaba en una
    /// pantalla quieta) y con 20 s no — o sea, el juez decía "el remake toca al juego" cuando
    /// lo único que pasaba era que miraba dos instantes diferentes.</summary>
    private static readonly long _presentDumpAfterFrames =
        long.TryParse(Environment.GetEnvironmentVariable("PAKU_PRESENT_DUMP_AFTER_FRAMES"), out var _pf) ? _pf : -1;
    private readonly System.Diagnostics.Stopwatch _presentClock = System.Diagnostics.Stopwatch.StartNew();
    private int _presentDumped;

    // PAKU_PRESENT_DUMP_EVERY=N: volcar uno de cada N fotogramas en vez de los N primeros
    // seguidos. Sirve para MUESTREAR una partida entera con un coste acotado, que es lo que
    // necesita el kit de remakes para poder recortar la referencia de un asset del fotograma
    // REAL en que se veía — reconstruirla desde índices + paleta obliga a acertar cuál de las
    // (254, en un minuto de un juego de prueba) paletas del fundido tocaba, y no hay regla que
    // acierte siempre.
    private readonly int _presentDumpEvery =
        int.TryParse(Environment.GetEnvironmentVariable("PAKU_PRESENT_DUMP_EVERY"), out var _pe) && _pe > 0 ? _pe : 0;

    private void DumpPresentedFrame(byte[] fb, int w, int h)
    {
        if (_presentDump == null || _presentDumped >= _presentDumpN) return;
        if (_presentDumpEvery > 0)
        {
            if (FrameGeneration % _presentDumpEvery != 0) return;
        }
        else if (_presentDumpAfterFrames >= 0)
        {
            if (FrameGeneration < _presentDumpAfterFrames) return;
        }
        else if (_presentClock.ElapsedMilliseconds < _presentDumpAfterMs) return;
        try
        {
            var dir = Path.GetDirectoryName(_presentDump);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // El FOTOGRAMA va en el nombre: es la única forma de atar este volcado a la línea de
            // `blit.log` que dibujó el asset, que trae ese mismo contador.
            File.WriteAllBytes($"{_presentDump}{_presentDumped:D3}.f{FrameGeneration}.{w}x{h}.rgba", fb);
            if (++_presentDumped == _presentDumpN)
                Console.Error.WriteLine($"[PRESENT] {_presentDumpN} frames volcados a {_presentDump}*.rgba");
        }
        catch (Exception ex) { Console.Error.WriteLine("[PRESENT] volcado fallido: " + ex.Message); }
    }

    /// <summary>Hand the latest captured frame to the host presenter. False until the first
    /// capture (host renders live VRAM once as warm-up). Never null-races: single writer.</summary>
    public bool TryGetFrontBuffer(out byte[] fb, out int fw, out int fh)
    {
        lock (_frontLock)
        {
            if (_frontBuffer == null) { fb = System.Array.Empty<byte>(); fw = 0; fh = 0; return false; }
            fb = _frontBuffer; fw = _frontW; fh = _frontH; return true;
        }
    }

    /// <summary>Drop the captured frame on a video-mode change (INT 10h AH=00) so a
    /// graphics->text transition is not briefly presented on a stale graphics frame.</summary>
    public void ResetFrontBuffer()
    {
        lock (_frontLock) { _frontBuffer = null; _frontW = 0; _frontH = 0; }
    }

    // --- Renderers ---

    /// <summary>Alta de un modo VESA de 8 bpp (VBE 4F02). La VRAM lineal se crea al primer uso
    /// y se limpia salvo que el programa pida lo contrario (bit 15 del número de modo).</summary>
    public void PonModoSvga(ushort modo, int w, int h, bool limpiar)
    {
        SvgaVram ??= new byte[1 << 20];
        if (limpiar) Array.Clear(SvgaVram, 0, SvgaVram.Length);
        SvgaActivo = true;
        SvgaModo = modo;
        // La ventana vive en A000 con 64 KiB (bits 2-3 de GC6) y el modo es gráfico de 256
        // colores a ojos del resto del equipo (paleta de 256 por el DAC, presentación gráfica).
        Miscellaneous = 0x41;
        GraphicsMode = 0x40;
        SvgaBank = 0;
        SvgaStride = w;
        SvgaStart = 0;
        Width = w; Height = h;
    }

    /// <summary>Baja del modo VESA: la llama el set-mode clásico del INT 10h (AH=00), que es
    /// como un programa VESA vuelve al texto o al 13h. La VRAM queda para la próxima.</summary>
    public void QuitaModoSvga() { SvgaActivo = false; SvgaModo = 0; }

    private byte[] RenderSvga8()
    {
        int w = Width > 0 ? Width : 640;
        int h = Height > 0 ? Height : 480;
        var fb = new byte[w * h * 4];
        var vr = SvgaVram;
        if (vr == null) return fb;
        int stride = SvgaStride > 0 ? SvgaStride : w;
        for (int y = 0; y < h; y++)
        {
            int fila = SvgaStart + y * stride;
            for (int x = 0; x < w; x++)
            {
                int a = fila + x;
                byte c = (uint)a < (uint)vr.Length ? vr[a] : (byte)0;
                int di = c * 3;
                int fbIdx = (y * w + x) * 4;
                fb[fbIdx + 0] = DacExpand(di < Dac.Length ? Dac[di + 0] : 0);
                fb[fbIdx + 1] = DacExpand(di + 1 < Dac.Length ? Dac[di + 1] : 0);
                fb[fbIdx + 2] = DacExpand(di + 2 < Dac.Length ? Dac[di + 2] : 0);
                fb[fbIdx + 3] = 0xFF;
            }
        }
        return fb;
    }

    public byte[] RenderFramebuffer()
    {
        LogPaletteIfChanged();
        if (SvgaActivo) return RenderSvga8();
        if (!IsGraphicsFromRegs) return RenderText();
        if (Es256Color) return RenderMode13h();
        if (VideoMode == 6) return RenderCga2();
        if (ShiftRegisterMode == 0) return RenderEgaPlanar();
        return RenderCga();
    }

    // PAKU_LOG_PALETTE=1: traza la paleta EFECTIVA — el RGB que acaba en pantalla para cada
    // índice de color del juego (índice -> Attribute Controller -> DAC -> DacExpand) — cada
    // vez que cambia, con el número de frames que llevaba puesta la anterior. Existe porque
    // un juego que carga imágenes con su propia tabla de color reprograma la paleta ANTES de
    // pintar cada una: la paleta buena para una imagen concreta no es "la del final de la
    // partida" sino la que estaba puesta mientras esa imagen se veía, y sin traza hay que
    // adivinar cuál era. Sale por stderr en hex para poder parsearla.
    private static readonly bool _logPalette = Environment.GetEnvironmentVariable("PAKU_LOG_PALETTE") == "1";
    private byte[]? _lastPalette;
    private int _paletteGen;
    private int _paletteFrames;

    /// <summary>Paleta efectiva: para cada índice de color del juego, el RGB de 8 bits que se
    /// presenta. En 256 colores el índice va directo al DAC; en los modos EGA/CGA pasa antes
    /// por el Attribute Controller. Es exactamente lo que hacen los renderers.</summary>
    public byte[] PaletaEfectiva()
    {
        int n = Es256Color ? 256 : 16;
        var pal = new byte[n * 3];
        for (int i = 0; i < n; i++)
        {
            int di = (Es256Color ? i : AttrRegs[i]) * 3;
            for (int c = 0; c < 3; c++)
                pal[i * 3 + c] = DacExpand(di + c < Dac.Length ? Dac[di + c] : 0);
        }
        return pal;
    }

    private void LogPaletteIfChanged()
    {
        if (!_logPalette) return;
        _paletteFrames++;
        var pal = PaletaEfectiva();
        if (_lastPalette != null && _lastPalette.AsSpan().SequenceEqual(pal)) return;
        Console.Error.WriteLine(
            $"[PALETA] #{_paletteGen++} n={pal.Length / 3} tras={_paletteFrames}f " +
            Convert.ToHexString(pal));
        _lastPalette = pal;
        _paletteFrames = 0;
    }

    private byte[] RenderEgaPlanar()
    {
        int w = Width > 0 ? Width : 320;
        int h = Height > 0 ? Height : 200;
        var fb = new byte[w * h * 4];
        int bytesPerRow = w / 8;
        // CRTC start address (0x0C/0x0D) y line compare (0x18 + bits de overflow): los scrolls
        // por hardware panean el start address con wrap del plano de 64K (un juego copia las filas de
        // arriba al final del buffer para el wrap de sus títulos; sin esto se ve la banda
        // duplicada abajo). Line compare parte la pantalla: desde esa línea se lee offset 0
        // (vga_draw de DOSBox-X). CRTC virgen (todo ceros) = comportamiento de siempre.
        int dispStart = (CrtcRegs[0x0C] << 8) | CrtcRegs[0x0D];
        // LineCompare es el latch tipo vga.config.line_compare (ver WriteCrtcRegister): igual
        // que derivarlo de r18/r07/r09 salvo cuando el juego escribe r07 con el candado puesto.
        // SplitRow lo pasa de SCANLINES a filas presentadas (ver allí): en los modos de 200
        // líneas, que van doblados, no es lo mismo.
        int lineCompare = SplitRow();
        // Blanking vertical anticipado: las líneas a partir de ActiveHeight no se emiten.
        int active = Math.Min(ActiveHeight, h);
        if (Environment.GetEnvironmentVariable("PAKU_LOG_CRTC") == "1")
            Console.Error.WriteLine($"[CRTC] start={dispStart} lc={(lineCompare == int.MaxValue ? -1 : lineCompare)} active={active} r07={CrtcRegs[0x07]:X2} r09={CrtcRegs[0x09]:X2} r15={CrtcRegs[0x15]:X2} r18={CrtcRegs[0x18]:X2} offs={CrtcRegs[0x13]:X2}");
        for (int i = active * w; i < w * h; i++) fb[i * 4 + 3] = 0xFF; // blanking: negro opaco
        for (int y = 0; y < active; y++)
        for (int x = 0; x < w; x++)
        {
            int rowBase = y < lineCompare ? dispStart + y * bytesPerRow
                                          : (y - lineCompare) * bytesPerRow;
            int byteIdx = (rowBase + (x / 8)) & (PlaneSize - 1);
            int bitIdx = 7 - (x & 7);
            int color = 0;
            for (int p = 0; p < 4; p++)
                color |= ((Planes[p][byteIdx] >> bitIdx) & 1) << p;

            int remapped = AttrRegs[color & 0x0F];
            int di = remapped * 3;
            int fbIdx = (y * w + x) * 4;
            fb[fbIdx + 0] = DacExpand(di < Dac.Length ? Dac[di + 0] : 0);
            fb[fbIdx + 1] = DacExpand(di + 1 < Dac.Length ? Dac[di + 1] : 0);
            fb[fbIdx + 2] = DacExpand(di + 2 < Dac.Length ? Dac[di + 2] : 0);
            fb[fbIdx + 3] = 0xFF;
        }
        return fb;
    }

    /// <summary>Columnas, filas y altura de carácter del modo texto, TAL COMO LOS DICE EL CRTC
    /// (r01+1, r09 bits 0-4 +1, y (VDE+1)/altura con el VDE de r12 más los dos bits del overflow
    /// r07 — la misma cuenta que <see cref="RenderMode13h"/>). Con el CRTC virgen o diciendo algo
    /// absurdo se vuelve al 80x25 (40x25 en los modos 0/1) de toda la vida.</summary>
    public (int Cols, int Rows, int CharHeight) GeometriaTexto()
    {
        int cols = CrtcRegs[0x01] + 1;
        if (cols < 20 || cols > 200) cols = VideoMode is 0 or 1 ? 40 : 80;
        int alto = (CrtcRegs[0x09] & 0x1F) + 1;
        if (alto < 2 || alto > 32) alto = 16;
        int vde = (CrtcRegs[0x12] | ((CrtcRegs[0x07] & 0x02) << 7) | ((CrtcRegs[0x07] & 0x40) << 3)) + 1;
        int rows = vde / alto;
        if (rows < 10 || rows > 60) rows = 25;
        return (cols, rows, alto);
    }

    /// <summary>
    /// Modo texto. Tres cosas que ANTES estaban clavadas y ahora salen del hardware:
    ///
    /// 1. LA GEOMETRÍA ES DEL CRTC (ver <see cref="GeometriaTexto"/>). Se pintaban siempre 25
    ///    filas de 16 píxeles; un programa en 80x50 —un tracker de música, y cualquier utilidad que
    ///    pida AH=11h AL=12h— sólo enseñaba la MITAD DE ARRIBA de su interfaz.
    /// 2. LA FUENTE ESTÁ EN EL PLANO 2, 32 bytes por carácter, como en una VGA de verdad. Antes
    ///    se usaba una tabla interna de 8x8 duplicando cada fila, así que además de salir todo
    ///    alto y estrecho, una fuente cargada por el programa (INT 10h AH=11h, o escribiendo el
    ///    plano 2 por puertos) NO SE VEÍA NUNCA.
    /// 3. EL BLOQUE DE FUENTE lo elige el registro 3 del Sequencer (character map select): el
    ///    bit 3 del atributo escoge entre el bloque A y el B, que es como un programa tiene 512
    ///    glifos a la vez. Fórmula de dosbox-x <c>vga_seq.cpp:189</c> (case 3).
    ///
    /// El color se mantiene como estaba: índice del atributo -> Attribute Controller -> DAC. Y
    /// el foreground sigue usando los CUATRO bits bajos del atributo aunque el bit 3 elija fuente,
    /// que es lo que hace el hardware (dosbox-x <c>vga_draw.cpp:2111</c>: la fuente se indexa con
    /// <c>(attr>>3)&amp;1</c> y el color sigue siendo <c>attr&amp;0xF</c>).
    ///
    /// NO se respeta el start address (r0C/r0D): tampoco se respetaba antes, y cambiarlo sin una
    /// medida contra el oráculo entra en el terreno del scroll por hardware en texto.
    /// </summary>
    private byte[] RenderText()
    {
        var (cols, rows, charH) = GeometriaTexto();
        int pixW = cols * 8;
        int pixH = rows * charH;
        // Publicar el tamaño real del framebuffer de texto: el host lo usa para
        // dimensionar la textura (sin esto la ventana pinta el texto entrelazado).
        Width = pixW;
        Height = pixH;
        var fb = new byte[pixW * pixH * 4];

        // Bloques de fuente A y B, en unidades de 8 KB dentro del plano 2 (vga_seq.cpp:192-197).
        byte csel = SeqRegs[3];
        int baseA = (((csel & 0x03) << 1) | ((csel & 0x10) >> 4)) * 8192;
        int baseB = (((csel & 0x0C) >> 1) | ((csel & 0x20) >> 5)) * 8192;
        var plano2 = Planes[2];

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            int i = r * cols + c;
            byte ch = (uint)i < PlaneSize ? Planes[0][i] : (byte)0x20;
            byte attr = (uint)i < PlaneSize ? Planes[1][i] : (byte)0x07;
            byte fg = (byte)(attr & 0x0F);
            byte bg = (byte)((attr >> 4) & 0x07);
            int baseFuente = (attr & 0x08) == 0 ? baseA : baseB;
            int glifo = baseFuente + ch * 32;

            for (int py = 0; py < charH; py++)
            {
                byte fontByte = plano2[(glifo + py) & (PlaneSize - 1)];
                for (int px = 0; px < 8; px++)
                {
                    bool set = ((fontByte >> (7 - px)) & 1) != 0;
                    byte colorIdx = set ? fg : bg;
                    int di = AttrRegs[colorIdx & 0x0F] * 3;
                    int fbIdx = ((r * charH + py) * pixW + c * 8 + px) * 4;
                    if (fbIdx + 3 < fb.Length)
                    {
                        fb[fbIdx + 0] = DacExpand(Dac[di + 0]);
                        fb[fbIdx + 1] = DacExpand(Dac[di + 1]);
                        fb[fbIdx + 2] = DacExpand(Dac[di + 2]);
                        fb[fbIdx + 3] = 0xFF;
                    }
                }
            }
        }
        return fb;
    }

    /// <summary>Fila PRESENTADA en la que parte la pantalla el split screen, o
    /// <see cref="int.MaxValue"/> si no hay split.
    ///
    /// La línea de comparación del CRTC cuenta SCANLINES, no filas presentadas, y en un modo de
    /// doble barrido —los de 200 líneas de VGA, que se emiten en 400 scanlines— hay que dividirla:
    /// <c>split_line = (line_compare + 1) / render_max</c>, con <c>render_max = 2</c> cuando
    /// <c>((r09 &amp; 0x1F) + 1)</c> es par y no estamos en texto (dosbox-x
    /// <c>vga_draw.cpp</c>: <c>VGA_Update_SplitLineCompare</c> y el bloque que fija
    /// <c>render_max</c>).
    ///
    /// Sin esta conversión el split NO OCURRÍA NUNCA en los modos de 200 líneas: un juego de prueba
    /// programa <c>lc=299</c> sobre 200 filas, la comparación <c>y &lt; 299</c> era siempre cierta,
    /// y su panel de instrumentos —que el juego
    /// dibuja en el offset 0 de la VRAM— salía de la página que hace scroll. Se veía un panel
    /// correcto que no se movía JAMÁS.</summary>
    public int SplitRow()
    {
        if (LineCompare == 0) return int.MaxValue;   // nadie lo programó: sin split, como siempre
        int alturaDeFila = (CrtcRegs[0x09] & 0x1F) + 1;
        int renderMax = (alturaDeFila & 1) == 0 ? 2 : 1;
        return (LineCompare + 1) / renderMax;
    }

    private byte[] RenderMode13h()
    {
        // ALTURA REAL, del CRTC: las líneas de display (r12 + los dos bits del overflow r07)
        // divididas por los scanlines que ocupa cada fila (r09 bits 0-4, +1). El modo 13h de toda
        // la vida da 400/2 = 200 y no cambia nada; **un Mode-X de 320x240 da 480/2 = 240**, que es
        // lo que usa un juego de prueba — y con la altura clavada a 200 sus CUARENTA ÚLTIMAS FILAS no se
        // pintaban: el panel de instrumentos se salía por abajo de la pantalla.
        //
        // Guard por si el CRTC está virgen o dice algo absurdo: se vuelve a los 200 de siempre.
        int vde = (CrtcRegs[0x12] | ((CrtcRegs[0x07] & 0x02) << 7) | ((CrtcRegs[0x07] & 0x40) << 3)) + 1;
        int porFila = (CrtcRegs[0x09] & 0x1F) + 1;
        int alto = porFila > 0 ? vde / porFila : 200;
        if (alto < 100 || alto > 480) alto = 200;
        // Publicar el tamaño real, igual que RenderText: el host dimensiona su textura con
        // Width/Height. Con un juego que fija el modo 12h por INT 10h y programa a mano un Mode-X
        // de 256 colores (un juego de prueba), Width/Height se quedaban en 640x480, el framebuffer no casaba
        // con la textura y la ventana NO SE ACTUALIZABA NUNCA: negra con el juego dibujando.
        Width = 320;
        Height = alto;
        var fb = new byte[320 * alto * 4];
        // CRTC start address (0x0C/0x0D), stride (0x13) y line compare, igual que el camino
        // planar. Sin esto el modo 13h se leía SIEMPRE desde el offset 0, así que cualquier
        // juego con doble buffer o scroll por hardware veía eternamente la página 0.
        //
        // Modelo del hardware (dosbox-x vga_draw.cpp VGA_SetupDrawing, case M_VGA -> M_LIN8,
        // y vga_crtc.cpp VGA_CheckAddrShift):
        //   addr_shift = 2 si r14 bit 6 (dword mode) | 0 si r17 bit 6 (byte mode) | 1 si no
        //   address = (real_start << addr_shift) * 4       <- byte_panning_shift = 4
        //   address_add = r13 * (8 << addr_shift)
        // Esas direcciones son de la memoria ENCADENADA de DOSBox, donde el píxel lineal P
        // vive en (P & ~3) << 2 | (P & 3); dividiendo entre 4 quedan en píxeles, que es como
        // guardamos el modo 13h (Planes[0] lineal, ver WriteVram). De ahí el <<addrShift a
        // secas para el inicio y el (2 << addrShift) para el paso de línea.
        //
        // Con el CRTC recién programado por el BIOS (r13=28h, r14=40h, r0C/r0D=0) esto da
        // start=0 y pitch=320: EXACTAMENTE lo de antes. Y con el CRTC virgen (todo ceros,
        // mundo sin set-mode) el guard de pitch mantiene los 320 de siempre.
        int addrShift = (CrtcRegs[0x14] & 0x40) != 0 ? 2
                      : (CrtcRegs[0x17] & 0x40) != 0 ? 0 : 1;
        int start = ((CrtcRegs[0x0C] << 8) | CrtcRegs[0x0D]) << addrShift;
        // BYTE PANNING (bits 5-6 del r08): NO se aplica, y no por olvido. Se probó —dosbox-x
        // vga_crtc.cpp `bytes_skip=(val>>5)&3`, sumado al start— y una prueba manual dijo
        // que el temblor de la imagen EMPEORABA. Además está medido que en pleno juego los frames
        // presentados salen IDÉNTICOS entre sí (8 seguidos, 0 filas distintas), así que lo que el
        // usuario ve moverse no está en el framebuffer: es la presentación. Aplicar un ajuste de
        // panning aquí era arreglar lo que no estaba roto. Si algún día hace falta, en Mode-X la
        // unidad no es el píxel —cada byte de plano son cuatro— y eso hay que MEDIRLO antes.
        int pitch = CrtcRegs[0x13] * (2 << addrShift);
        if (pitch == 0) pitch = 320;             // CRTC virgen: comportamiento de siempre
        int lineCompare = SplitRow();
        // Sin encadenar el paso de linea va en BYTES POR PLANO (80 para 320 pixeles), no en
        // pixeles: r13 cuenta palabras y el hardware no multiplica por el numero de planos.
        bool planar = IsModeX;
        if (planar)
        {
            // BYTE PANNING (bits 5-6 del r08), en unidades de PLANO y sumado AQUÍ, en la rama de
            // Mode-X — que es donde se recalcula el start y donde antes se perdía.
            //
            // El juego lo usa en cada volteo de página: su rutina de flip (offset 0x1379C de la
            // imagen descifrada) calcula la dirección en BYTES y la parte en dos, `r08 = (S&3)<<5`
            // y `r0C:r0D = S & ~3`. Sin volver a juntarlas, la imagen sale corrida hasta 4 píxeles.
            // dosbox-x hace lo mismo en vga_draw.cpp (`VGA_VerticalTimer`, caso M_VGA -> M_LIN8):
            // `address += bytes_skip`.
            //
            // Hubo un intento anterior de esto que EMPEORÓ y se revirtió: estaba puesto arriba,
            // antes de este bloque, así que lo pisaba el recálculo — y además el temblor de la
            // copia por latches lo tapaba todo y no dejaba juzgar.
            start = ((CrtcRegs[0x0C] << 8) | CrtcRegs[0x0D]) + ((CrtcRegs[0x08] >> 5) & 3);
            pitch = CrtcRegs[0x13] * 2;
            if (pitch == 0) pitch = 80;
        }
        if (Environment.GetEnvironmentVariable("PAKU_LOG_CRTC") == "1")
            Console.Error.WriteLine($"[CRTC13] start={start} pitch={pitch} " +
                $"lc={(lineCompare == int.MaxValue ? -1 : lineCompare)} modeX={planar} " +
                $"r07={CrtcRegs[0x07]:X2} r09={CrtcRegs[0x09]:X2} r13={CrtcRegs[0x13]:X2} " +
                $"r14={CrtcRegs[0x14]:X2} r17={CrtcRegs[0x17]:X2} r18={CrtcRegs[0x18]:X2} " +
                $"r12={CrtcRegs[0x12]:X2} vde={((CrtcRegs[0x12] | ((CrtcRegs[0x07] & 2) << 7) | ((CrtcRegs[0x07] & 0x40) << 3)) + 1)} filas={ActiveHeight}");
        // Los ÍNDICES de paleta se guardan de camino, que es gratis: aquí ya se calcula `color`
        // para cada píxel y sólo hay que apuntarlo. Verlos importa porque el RGB de pantalla ya
        // ha perdido la identidad del color — dos índices distintos pueden dar el mismo RGB —, y
        // hay preguntas que sólo contesta el índice: en un juego 3D, QUÉ píxeles de la vista
        // son fondo y cuáles son sprites que el juego pinta encima.
        if (_indices == null || _indices.Length != 320 * alto) _indices = new byte[320 * alto];
        var idx = _indices;
        for (int i = 0; i < 320 * alto; i++)
        {
            int y = i / 320, x = i - y * 320;
            int rowBase = y < lineCompare ? start + y * pitch : (y - lineCompare) * pitch;
            int color = planar
                ? Planes[x & 3][(rowBase + (x >> 2)) & (PlaneSize - 1)]
                : Planes[0][(rowBase + x) & (PlaneSize - 1)];
            idx[i] = (byte)color;
            int fi = i * 4;
            fb[fi + 0] = DacExpand(Dac[color * 3 + 0]);
            fb[fi + 1] = DacExpand(Dac[color * 3 + 1]);
            fb[fi + 2] = DacExpand(Dac[color * 3 + 2]);
            fb[fi + 3] = 0xFF;
        }
        _indicesW = 320; _indicesH = alto;
        return fb;
    }

    byte[]? _indices;
    int _indicesW, _indicesH;

    /// <summary>Los índices de paleta del último frame de 256 colores renderizado, con su tamaño.
    /// Devuelve false si todavía no se ha renderizado ninguno o el modo no es de 256 colores.
    ///
    /// El framebuffer RGBA que se presenta ya no sirve para preguntar "¿qué es este píxel?": el
    /// DAC puede dar el mismo color a índices distintos, y el índice ES la identidad que el juego
    /// manejaba. Con él se puede separar lo que dibujó una parte del juego de lo que dibujó otra.</summary>
    public bool TryGetIndices(out byte[] indices, out int ancho, out int alto)
    {
        indices = _indices!; ancho = _indicesW; alto = _indicesH;
        return _indices != null && _indicesW > 0 && _indicesH > 0;
    }

    private static readonly byte[][] CgaPalettes =
    {
        new byte[] { 0x00,0xAA,0x00, 0xAA,0x00,0x00, 0xAA,0x55,0x00 },
        new byte[] { 0x00,0xAA,0xAA, 0xAA,0x00,0xAA, 0xAA,0xAA,0xAA },
        new byte[] { 0x55,0xFF,0x55, 0xFF,0x55,0x55, 0xFF,0xFF,0x55 },
        new byte[] { 0x55,0xFF,0xFF, 0xFF,0x55,0xFF, 0xFF,0xFF,0xFF },
    };

    private static readonly byte[][] CgaBackgroundColors =
    {
        new byte[] {0x00,0x00,0x00}, new byte[] {0x00,0x00,0xAA}, new byte[] {0x00,0xAA,0x00}, new byte[] {0x00,0xAA,0xAA},
        new byte[] {0xAA,0x00,0x00}, new byte[] {0xAA,0x00,0xAA}, new byte[] {0xAA,0x55,0x00}, new byte[] {0xAA,0xAA,0xAA},
        new byte[] {0x55,0x55,0x55}, new byte[] {0x55,0x55,0xFF}, new byte[] {0x55,0xFF,0x55}, new byte[] {0x55,0xFF,0xFF},
        new byte[] {0xFF,0x55,0x55}, new byte[] {0xFF,0x55,0xFF}, new byte[] {0xFF,0xFF,0x55}, new byte[] {0xFF,0xFF,0xFF},
    };

    /// <summary>
    /// CGA renderer: lee de Planes en modo odd/even.
    /// En odd/even, byte lineal N se almacenó en Planes[N&amp;1][N>>1].
    /// </summary>
    private byte[] RenderCga()
    {
        var fb = new byte[320 * 200 * 4];
        // Los 2 bits del píxel NO son un color fijo: en EGA/VGA entran en el Attribute
        // Controller y de ahí al DAC, igual que en el camino planar, así que los cuatro
        // colores del modo 4/5 son REDEFINIBLES — el BIOS los programa al fijar el modo
        // (00/13/15/17 = la paleta CGA de siempre) y el juego los cambia con INT 10h AH=0B.
        // Antes se pintaba con las tablas fijas CgaPalettes/CgaBackgroundColors y por eso el
        // título de un juego CGA salía en cian/magenta donde el original va azul/rojo.
        // Red: tools/crtc_sweep/CGAPAL.COM + judge_cgapal.py (AC 48/48, DAC 192/192).
        // CgaPalette/CgaBackground siguen vivos: los escribe el puerto 0x3D9, que es del CGA
        // real y no existe en una EGA/VGA — de ahí que ya no manden en el color.
        for (int y = 0; y < 200; y++)
        {
            int rowBase = (y & 1) * 0x2000 + (y >> 1) * 80;
            for (int x = 0; x < 320; x++)
            {
                int byteOff = rowBase + (x >> 2);
                byte data = Planes[byteOff & 1][byteOff >> 1];
                int shift = 6 - ((x & 3) << 1);
                int colorIdx = (data >> shift) & 3;

                int di = AttrRegs[colorIdx & 0x0F] * 3;
                int fbIdx = (y * 320 + x) * 4;
                fb[fbIdx + 0] = DacExpand(di < Dac.Length ? Dac[di + 0] : 0);
                fb[fbIdx + 1] = DacExpand(di + 1 < Dac.Length ? Dac[di + 1] : 0);
                fb[fbIdx + 2] = DacExpand(di + 2 < Dac.Length ? Dac[di + 2] : 0);
                fb[fbIdx + 3] = 0xFF;
            }
        }
        return fb;
    }

    /// <summary>
    /// Modo 6 (CGA 640x200 2 colores): en EGA/VGA la VRAM es LINEAL en el plano 0 y el
    /// entrelazado de bancos 0x2000 lo hace el CRTC en scan-out (row-scan bit 0 sustituye
    /// el address bit 13, int10_modes.cpp:1920-1921). Color por AttrRegs (0 / 0x17), como
    /// el resto de renderers EGA — el puerto 0x3D9 no pinta en EGA/VGA (vga_other.cpp).
    /// </summary>
    private byte[] RenderCga2()
    {
        var fb = new byte[640 * 200 * 4];
        for (int y = 0; y < 200; y++)
        {
            int rowBase = (y & 1) * 0x2000 + (y >> 1) * 80;
            for (int x = 0; x < 640; x++)
            {
                int byteOff = rowBase + (x >> 3);
                byte data = (uint)byteOff < PlaneSize ? Planes[0][byteOff] : (byte)0;
                int bit = (data >> (7 - (x & 7))) & 1;
                int di = AttrRegs[bit == 0 ? 0 : 1] * 3;
                int fbIdx = (y * 640 + x) * 4;
                fb[fbIdx + 0] = DacExpand(di < Dac.Length ? Dac[di + 0] : 0);
                fb[fbIdx + 1] = DacExpand(di + 1 < Dac.Length ? Dac[di + 1] : 0);
                fb[fbIdx + 2] = DacExpand(di + 2 < Dac.Length ? Dac[di + 2] : 0);
                fb[fbIdx + 3] = 0xFF;
            }
        }
        return fb;
    }

    private byte ApplyLogic(byte src, byte latch) => LogicOp switch
    {
        1 => (byte)(src & latch),
        2 => (byte)(src | latch),
        3 => (byte)(src ^ latch),
        _ => src,
    };

    private static byte RotateByte(byte v, int count)
    {
        count &= 7;
        return count == 0 ? v : (byte)((v >> count) | (v << (8 - count)));
    }
}
