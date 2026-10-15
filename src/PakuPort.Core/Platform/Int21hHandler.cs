using System.Text.RegularExpressions;

namespace PakuPort.Core.Platform;

/// <summary>
/// INT 21h — servicios DOS. Implementa los servicios de ficheros y sistema que un juego DOS
/// típico usa: open/read/write/close/seek, fecha/hora, memoria, salida de texto, exit.
/// Los ficheros se sirven desde un directorio del host configurado en gameDir.
/// Adaptado de dosbox-x: src/dos/dos.cpp, dos_files.cpp, dos_execute.cpp, dos_memory.cpp
/// </summary>
public sealed class Int21hHandler
{
    /// <summary>Cargas por EXEC 4B03 observadas (nombre, seg, relocFactor). El export las
    /// usa en boot-real para liftear los drivers como pseudo-overlays (BuildDriverComposite)
    /// con los mismos segmentos que asignará el runtime (allocator determinista).</summary>
    public static readonly List<(string Name, ushort Seg, ushort Reloc)> ExecLoads = new();

    private readonly string _gameDir;
    private readonly Dictionary<int, FileStream> _handles = new();
    private readonly Random _tmpRand = new(); // AH=5Ah temp names, like DOSBox-X rand()

    // DOS asigna SIEMPRE el MENOR handle libre de la JFT (0-4 reservados para los
    // estándar). Sin reciclado, un init con >15 open/close agota el rango y el CRT
    // Borland (_NFILE=20) rechaza el handle: open() devuelve -1 y el juego aborta
    // (medido en un juego de prueba: exit(10) al abrir un fichero de datos). dos_files.cpp asigna igual.
    private int AllocHandle()
    {
        int h = 5;
        while (_handles.ContainsKey(h)) h++;
        return h;
    }

    // DEBUG: ring buffer of last file ops (PAKU_FILE_TRACE=1)
    public static readonly bool FileTrace = Environment.GetEnvironmentVariable("PAKU_FILE_TRACE") == "1";
    public static readonly System.Collections.Generic.List<string> FileOps = new();
    private void FileLog(string s) { if (FileTrace) { FileOps.Add(s); if (FileOps.Count > 60) FileOps.RemoveAt(0); } }

    // MCB chain state (dos_memory.cpp)
    private McbChain? _mcb;

    /// <summary>¿Cae el segmento en el arena de memoria dinámica DOS (AH=48/SETBLOCK)?
    /// Un far call ahí apunta a contenido cargado en runtime (drivers, overlays): su clave
    /// lineal NO es una clave de imagen válida — no debe sembrarse como gap.</summary>
    public bool IsArenaSegment(ushort seg) => _mcb != null && seg >= _mcb.Start && seg < _mcb.Top;

    /// <summary>Techo del arena, configurable POR MUNDO desde el host (ver McbChain.ArenaTopSeg).</summary>
    public static ushort ArenaTopSegment { get => McbChain.ArenaTopSeg; set => McbChain.ArenaTopSeg = value; }
    private ushort _pspSeg;

    // Tablas DOS sintéticas en el área privada de DOS (barrido de plataforma):
    // DPB (AH=1F/32) y SysVars/lista-de-listas (AH=52). Los juegos consultan el
    // puntero, no suelen leer el contenido; el campo útil [sysvars-2]=primer MCB
    // se rellena al vuelo en el case 0x52.
    private const ushort DosTablesSeg = 0x0060;
    private const ushort DpbOff = 0x0000;
    private const ushort SysVarsOff = 0x0080;
    private byte _lastExitCode;

    // DTA (Data Transfer Area) — DS:DX set via AH=1A; default PSP+0x80
    private ushort _dtaSeg;
    private ushort _dtaOff = 0x80;

    /// <summary>The DOS file system: drive table, current directory per drive, 8.3 short names
    /// and the findfirst/findnext engine. See Platform/DosFileSystem.cs.</summary>
    private readonly DosFileSystem _fs;

    public Int21hHandler(string gameDir)
    {
        _gameDir = gameDir;
        _fs = new DosFileSystem(gameDir);
    }

    /// <summary>The drive table, for the host and for the diagnosis harnesses.</summary>
    public DosFileSystem FileSystem => _fs;

    /// <summary>Ruta del EXE principal del juego. Fallback de auto-carga: los loaders VROOMM
    /// abren su propio EXE para leer overlays; si la ruta que construyen no resuelve (nombre
    /// vacío o con basura) se sirve este EXE, para que la contabilidad de auto-carga complete
    /// (en el port los overlays ya son C#, así que los bytes leídos no se ejecutan de disco).</summary>
    public string? SelfExePath { get; set; }

    private static bool LooksGarbage(string name)
    {
        string n = name.Replace("\\", "").Replace("/", "").Replace(":", "").Trim();
        if (n.Length == 0) return true;
        foreach (char c in n) if (c < 0x20 || c > 0x7E) return true;  // no imprimible = basura
        return false;
    }

    /// <summary>Debe llamarse al inicializar la plataforma con el segmento PSP del juego.</summary>
    public void SetPsp(ushort pspSeg, Memory mem)
    {
        _pspSeg = pspSeg;
        _dtaSeg = pspSeg;
        _dtaOff = 0x80;
        // Inicializar cadena MCB si no se ha hecho aún
        _mcb ??= new McbChain(mem);
    }

    /// <summary>Modela una máquina DOS recién arrancada con un PROGRAMA cargado que posee TODA
    /// la memoria convencional (cero libre), tal como hace DOS al cargar un COM/EXE
    /// (dos_execute.cpp asigna el bloque mayor al programa). Lo usa el arnés de plataforma
    /// (platform-test) para reproducir el estado exacto del oráculo DOSBox; los juegos NO lo
    /// llaman (usan el arena perezoso de SetPsp/AllocMemory).</summary>
    public void SetupLoadedProgramMemory(ushort pspSeg, ushort topSeg, Memory mem)
    {
        _pspSeg = pspSeg;
        _dtaSeg = pspSeg;
        _dtaOff = 0x80;
        _mcb = new McbChain(mem);
        _mcb.InitLoadedProgram(pspSeg, topSeg);
    }

    private DosPlatform? _platform;
    public void Register(DosPlatform p) { _platform = p; p.Int21 = this; p.RegisterInt(0x21, Handle); }

    // Every [int21]/[4A] line carries t=<Cpu.TickCount>, the machine-wide time axis shared by
    // blit.log / blitraw.bin / audio-events.log / input.log / the input tape — without it the DOS
    // trace cannot be crossed with the rest of the telemetry.
    // The stamp is appended at the END of each line (and before the trailing "(CS=...)" of the
    // AH=3D lines) so the existing field parsers (tools/dossier/build_dossier.py) keep working.
    private static readonly bool LogCalls = Environment.GetEnvironmentVariable("PAKU_LOG_INT21") == "1";
    private static readonly bool TraceConOut = Environment.GetEnvironmentVariable("PAKU_TRACE") == "1";

    /// <summary>DOS console output routed to the emulated screen via BIOS teletype, like
    /// dosbox-x: dos.cpp INT21 AH=02/06/09 → DOS_WriteFile(STDOUT) → device CON →
    /// Real_INT10_TeletypeOutput (dev_con.h:484). Without this, text written through DOS
    /// (e.g. a game's setup screen clearing prompt rows with a '$'-string of blanks) never reaches the
    /// screen and stale text piles up.</summary>
    private void ConOut(byte ch)
    {
        var v = _platform?.Video;
        if (v != null) Int10hHandler.DosConOut(ch, v);
    }

    private void Handle(Cpu cpu, Memory mem)
    {
        if (LogCalls)
            Console.Error.WriteLine($"[int21] AH={cpu.AH:X2} AL={cpu.AL:X2} BX={cpu.BX:X4} CX={cpu.CX:X4} DX={cpu.DX:X4} DS={cpu.DS:X4} CS={cpu.CS:X4} t={Cpu.TickCount}");
        switch (cpu.AH)
        {
            // ---- CONSOLA ----

            case 0x01: // Read character from stdin with echo — dos.cpp INT21_ReadChar
                cpu.AL = ReadConsoleChar(cpu, mem, blocking: true);
                cpu.CF = false;
                break;

            case 0x02: // Write character to stdout — dos.cpp:1138 → DOS_WriteFile(STDOUT) → CON teletype
                Console.Write((char)cpu.DL);
                ConOut(cpu.DL);
                break;

            case 0x05: // Printer output — dos.cpp, no-op en emulación
                // Impresora no emulada; ignorar silenciosamente
                cpu.CF = false;
                break;

            case 0x06: // Direct console I/O — dos.cpp INT21_DirectConsoleIO
                if (cpu.DL == 0xFF)
                {
                    // Non-blocking input poll: same source/protocol as AH=07/08 (extended
                    // keys split across two reads) — was previously a hardcoded stub that
                    // NEVER reported input, which stalls any polling loop that uses this
                    // function instead of AH=0B+08.
                    if (ConsoleInputPending())
                    {
                        cpu.AL = ReadConsoleChar();
                        cpu.ZF = false;
                    }
                    else
                    {
                        cpu.AL = 0; cpu.ZF = true; // no input available
                    }
                }
                else
                {
                    Console.Write((char)cpu.DL);
                    ConOut(cpu.DL); // dos.cpp INT21_DirectConsoleIO output → CON teletype
                    cpu.ZF = false;
                }
                break;

            case 0x07: // Read char without echo, no Ctrl-C check — dos.cpp
                cpu.AL = ReadConsoleChar(cpu, mem, blocking: true);
                cpu.CF = false;
                break;

            case 0x08: // Read char without echo — dos.cpp
                cpu.AL = ReadConsoleChar(cpu, mem, blocking: true);
                cpu.CF = false;
                break;

            case 0x09: // Write string to stdout (DS:DX → '$'-terminated) — dos.cpp:1228:
                       // every byte until '$' goes to DOS_WriteFile(STDOUT) → CON teletype.
            {
                int addr = Memory.Lin(cpu.DS, cpu.DX);
                var sb = TraceConOut ? new System.Text.StringBuilder() : null;
                for (int i = 0; i < 4096; i++)
                {
                    byte b = mem.Raw[addr + i];
                    if (b == (byte)'$') break;
                    ConOut(b);
                    sb?.Append((char)b);
                }
                if (sb != null) Console.Error.WriteLine($"[DOS 09h] {sb}");
                break;
            }

            case 0x0A: // Buffered keyboard input — dos.cpp
                cpu.CF = false;
                break;

            case 0x0B: // Check input status — dos.cpp INT21_GetInputStatus
                // 0xFF si hay tecla, 0x00 si no — incluye el segundo byte pendiente de una
                // tecla extendida (ver ConsoleInputPending / ReadConsoleChar).
                cpu.AL = ConsoleInputPending() ? (byte)0xFF : (byte)0x00;
                break;

            case 0x0C: // Flush buffer and read — dos.cpp INT21_FlushAndRead
            {
                // Invoca la función del AL si es válida (01, 06, 07, 08, 0A)
                byte fn = cpu.AL;
                cpu.AH = fn;
                if (fn == 0x01 || fn == 0x06 || fn == 0x07 || fn == 0x08 || fn == 0x0A)
                    Handle(cpu, mem);
                else
                    cpu.CF = false;
                cpu.AH = 0x0C;
                break;
            }

            // ---- FECHA / HORA ----

            case 0x0D: // Disk reset — dos.cpp
                cpu.CF = false;
                break;

            case 0x0E: // Select disk — dos.cpp DOS_SetDrive
                // DL = drive (0 = A). A drive that is not mounted does NOT change the current one
                // (dos.cpp: DOS_SetDrive returns false and the current drive stays put).
                if (_fs.IsMounted(cpu.DL)) _fs.CurrentDrive = cpu.DL;
                cpu.AL = _fs.DriveCount;   // number of logical drives
                cpu.CF = false;
                break;

            case 0x0F: // Open file using FCB — dos_files.cpp DOS_FCBOpen
            case 0x10: // Close file using FCB — dos_files.cpp DOS_FCBClose
                cpu.AL = 0x00; // success
                break;

            case 0x11: // Find first using FCB — dos_files.cpp DOS_FCBFindFirst
            {
                // FCB at DS:DX: drive byte + 11-char blank-padded NAME.EXT ('?' wildcards).
                // Real lookup against gameDir: the VROOMM bootstrap's file_exists probe
                // (1000:0002) relies on AL=0 when the file is on disk; a hardcoded 0xFF
                // made the game believe its own resource files were missing.
                int fcb = Memory.Lin(cpu.DS, cpu.DX);
                var pat = new char[11];
                for (int i = 0; i < 11; i++) pat[i] = char.ToUpperInvariant((char)mem.Raw[fcb + 1 + i]);
                bool found = false;
                try
                {
                    foreach (string f in Directory.EnumerateFiles(_fs.Resolve("").HostDir))
                    {
                        string nm = Path.GetFileNameWithoutExtension(f).ToUpperInvariant();
                        string ex = Path.GetExtension(f).TrimStart('.').ToUpperInvariant();
                        if (nm.Length > 8 || ex.Length > 3) continue;
                        bool ok = true;
                        for (int i = 0; i < 8 && ok; i++)
                        {
                            char c = i < nm.Length ? nm[i] : ' ';
                            if (pat[i] != '?' && pat[i] != c) ok = false;
                        }
                        for (int i = 0; i < 3 && ok; i++)
                        {
                            char c = i < ex.Length ? ex[i] : ' ';
                            if (pat[8 + i] != '?' && pat[8 + i] != c) ok = false;
                        }
                        if (ok) { found = true; break; }
                    }
                }
                catch { }
                cpu.AL = found ? (byte)0x00 : (byte)0xFF;
                break;
            }

            case 0x12: // Find next using FCB — dos_files.cpp DOS_FCBFindNext
                cpu.AL = 0xFF; // no more files
                break;

            case 0x13: // Delete file using FCB — dos_files.cpp DOS_FCBDeleteFile
                cpu.AL = 0x00;
                break;

            case 0x14: // Sequential read from FCB — dos_files.cpp DOS_FCBRead
            case 0x15: // Sequential write to FCB — dos_files.cpp DOS_FCBWrite
            case 0x21: // Random read from FCB
            case 0x22: // Random write to FCB
            case 0x27: // Random block read from FCB
            case 0x28: // Random block write from FCB
                cpu.AL = 0x01; // end of file
                break;

            case 0x16: // Create file using FCB — dos_files.cpp DOS_FCBCreate
                cpu.AL = 0x00; // success
                break;

            case 0x17: // Rename file using FCB
                cpu.AL = 0x00;
                break;

            case 0x19: // Get current default drive — dos.cpp DOS_GetDefaultDrive
                cpu.AL = _fs.CurrentDrive;
                break;

            case 0x1A: // Set DTA address — dos_files.cpp
                _dtaSeg = cpu.DS;
                _dtaOff = cpu.DX;
                break;

            case 0x25: // Set interrupt vector — dos.cpp (escribe IVT en mem baja)
            {
                // Single funnel for IVT writes (Ivt.SetVector) so PAKU_LOG_IVT sees DOS hooks too.
                if (_platform != null) _platform.Ivt.SetVector(mem, cpu.AL, cpu.DS, cpu.DX);
                else
                {
                    int ivt = Memory.Lin(0, (ushort)(cpu.AL * 4));
                    mem.Raw[ivt + 0] = (byte)cpu.DX;
                    mem.Raw[ivt + 1] = (byte)(cpu.DX >> 8);
                    mem.Raw[ivt + 2] = (byte)cpu.DS;
                    mem.Raw[ivt + 3] = (byte)(cpu.DS >> 8);
                }
                cpu.CF = false;
                break;
            }

            case 0x1B: // Get allocation info for default drive — dos.cpp
            case 0x1C: // Get allocation info for specific drive — dos.cpp
                cpu.AL = 0x40; // sectors per cluster
                cpu.CX = 0x0200; // bytes per sector
                cpu.DX = 0x4000; // total clusters
                // DS:BX → media descriptor byte (we don't set this)
                cpu.CF = false;
                break;

            case 0x23: // Get file size for FCB — dos_files.cpp
                cpu.AL = 0xFF; // file not found
                break;

            case 0x24: // Set random record number for FCB
                cpu.CF = false;
                break;

            case 0x26: // Create new PSP — dos.cpp
                cpu.CF = false;
                break;

            case 0x29: // Parse filename to FCB — dos_files.cpp DOS_ParseFCBName
            {
                // AL = parse control; DS:SI = source; ES:DI = FCB buffer
                // Implementación mínima: extrae nombre.ext, rellena 11 bytes FCB
                int src = Memory.Lin(cpu.DS, cpu.SI);
                int dst = Memory.Lin(cpu.ES, cpu.DI);
                // FCB byte 0 = drive, 1-based (0 = current). A "D:" prefix sets it.
                byte fcbDrive = 0;
                if (mem.Raw[src + 1] == ':')
                {
                    char dl = char.ToUpperInvariant((char)mem.Raw[src]);
                    if (dl is >= 'A' and <= 'Z') fcbDrive = (byte)(dl - 'A' + 1);
                    src += 2;
                }
                mem.Raw[dst] = fcbDrive;
                byte[] fcbName = new byte[11];
                Array.Fill(fcbName, (byte)' ');
                int ni = 0;
                while (ni < 8)
                {
                    byte b = mem.Raw[src];
                    if (b == 0 || b == '.' || b == ' ' || b == '/' || b == '\\') break;
                    fcbName[ni++] = (byte)char.ToUpperInvariant((char)b);
                    src++;
                }
                if (mem.Raw[src] == '.') { src++; }
                int ei = 0;
                while (ei < 3)
                {
                    byte b = mem.Raw[src];
                    if (b == 0 || b == ' ' || b == '/' || b == '\\') break;
                    fcbName[8 + ei++] = (byte)char.ToUpperInvariant((char)b);
                    src++;
                }
                mem.CopiaDeServicio(dst + 1, fcbName, 0, 11); // FCB: byte 0 = drive, 1-11 = name
                cpu.AL = 0; cpu.CF = false;
                break;
            }

            case 0x2A: // Get date
                var now = GuestClock.Now(cpu.CyclesPerMs);
                cpu.CX = (ushort)now.Year;
                cpu.DH = (byte)now.Month;
                cpu.DL = (byte)now.Day;
                cpu.AL = (byte)now.DayOfWeek;
                break;

            case 0x2B: // Set date — dos.cpp (no-op, devuelve éxito)
                cpu.AL = 0; cpu.CF = false;
                break;

            case 0x2C: // Get time
                var t = GuestClock.Now(cpu.CyclesPerMs);
                cpu.CH = (byte)t.Hour;
                cpu.CL = (byte)t.Minute;
                cpu.DH = (byte)t.Second;
                cpu.DL = (byte)(t.Millisecond / 10);
                break;

            case 0x2D: // Set time — dos.cpp (no-op, devuelve éxito)
                cpu.AL = 0; cpu.CF = false;
                break;

            case 0x30: // Get DOS version — dos.cpp: BH=0xFF (Microsoft), BL/CX=serial 0
                if (cpu.AL == 0) cpu.BH = 0xFF;
                cpu.AL = 5; cpu.AH = 0; // DOS 5.0
                cpu.BL = 0; cpu.CX = 0;
                break;

            case 0x31: // Terminate and stay resident — dos.cpp (no toca CF)
                // dos.cpp case 0x31: DOS_ResizeMemory(psp, &dx) y DOS_Terminate(psp, tsr=true):
                // el bloque del PSP se encoge a DX párrafos y la memoria del proceso NO se
                // libera (dos_execute.cpp:150 sólo llama DOS_FreeProcessMemory si !tsr).
                // Sin esto, el driver de sonido de un juego de prueba (hijo EXEC que queda
                // residente con AH=31 DX=08FD) quedaba en memoria "libre" y el siguiente
                // AH=48 del juego se lo daba como buffer del .MOD: se sobreescribía a sí
                // mismo y descarrilaba en ceros.
                _mcb?.Resize(_pspSeg, cpu.DX, out _);
                throw new ProgramExitException(cpu.AL, tsr: true);

            case 0x2E: // Set verify flag — dos.cpp
                // AL = 0 (off) or 1 (on). No-op in emulation.
                cpu.CF = false;
                break;

            case 0x2F: // Get DTA address — dos.cpp
                cpu.ES = _dtaSeg;
                cpu.BX = _dtaOff;
                cpu.CF = false;
                break;

            case 0x33: // Get/set break flag — dos.cpp
                if (cpu.AL == 0)
                    cpu.DL = 0; // break off
                else if (cpu.AL == 1)
                    cpu.CF = false; // set break (no-op)
                else if (cpu.AL == 2)
                    { cpu.DL = 0; cpu.CF = false; }
                else if (cpu.AL == 5) // Get boot drive
                    cpu.DL = 3; // C:
                else if (cpu.AL == 6) // Get true DOS version
                    { cpu.BL = 5; cpu.BH = 0; cpu.DL = 0; cpu.DH = 0; }
                break;

            case 0x34: // Get InDOS flag — dos.cpp
                // ES:BX → pointer to InDOS flag (byte). We place it at a fixed location.
                cpu.ES = 0x0050;
                cpu.BX = 0x0000;
                mem.Raw[Memory.Lin(0x0050, 0x0000)] = 0; // not in DOS
                cpu.CF = false;
                break;

            case 0x35: // Get interrupt vector — dos.cpp
            {
                int ivt = Memory.Lin(0, (ushort)(cpu.AL * 4));
                cpu.BX = (ushort)(mem.Raw[ivt] | (mem.Raw[ivt + 1] << 8));
                cpu.ES = (ushort)(mem.Raw[ivt + 2] | (mem.Raw[ivt + 3] << 8));
                cpu.CF = false;
                break;
            }

            case 0x36: // Get free disk space — dos.cpp DOS_GetFreeDiskSpace
            {
                // DL = drive (0=default, 1=A, 2=B, 3=C...). An unmounted drive answers AX=0xFFFF
                // (dos.cpp case 0x36: reg_ax = 0xffff on failure), which is how DOS says "no drive".
                byte spcDrive = cpu.DL == 0 ? _fs.CurrentDrive : (byte)(cpu.DL - 1);
                if (!_fs.IsMounted(spcDrive)) { cpu.AX = 0xFFFF; cpu.CF = true; break; }
                cpu.AX = 0x0040; // sectors per cluster
                cpu.BX = 0x3000; // free clusters (ficticios, ~384 MB)
                cpu.CX = 0x0200; // bytes per sector (512)
                cpu.DX = 0x4000; // total clusters
                cpu.CF = false;
                break;
            }

            case 0x37: // Get/set switch character — dos.cpp
                cpu.DL = (byte)'/';
                cpu.AL = 0; cpu.CF = false;
                break;

            case 0x38: // Country info — dos.cpp DOS_GetCountryInfo
            {
                // AL=0 → devolver info de país; BX=país (0=actual). DS:DX = buffer 34 bytes.
                // Devolver datos mínimos de USA/inglés.
                if (cpu.DX == 0xFFFF)
                {
                    cpu.BX = 0x0001; // país USA
                    cpu.CF = false;
                }
                else
                {
                    int buf = Memory.Lin(cpu.DS, cpu.DX);
                    // Date format (0=USA MM/DD/YY)
                    mem.Raw[buf + 0] = 0; mem.Raw[buf + 1] = 0;
                    // Currency '$'
                    mem.Raw[buf + 2] = (byte)'$'; mem.Raw[buf + 3] = 0;
                    // Thousands sep ','
                    mem.Raw[buf + 4] = (byte)','; mem.Raw[buf + 5] = 0;
                    // Decimal sep '.'
                    mem.Raw[buf + 6] = (byte)'.'; mem.Raw[buf + 7] = 0;
                    // Date sep '/'
                    mem.Raw[buf + 8] = (byte)'/'; mem.Raw[buf + 9] = 0;
                    // Time sep ':'
                    mem.Raw[buf + 10] = (byte)':'; mem.Raw[buf + 11] = 0;
                    // Currency format, digits
                    mem.Raw[buf + 12] = 0; mem.Raw[buf + 13] = 2;
                    // Time format (12h=0)
                    mem.Raw[buf + 14] = 0;
                    // Case map far call (null)
                    mem.Raw[buf + 15] = 0; mem.Raw[buf + 16] = 0;
                    mem.Raw[buf + 17] = 0; mem.Raw[buf + 18] = 0;
                    // Data-list sep ','
                    mem.Raw[buf + 19] = (byte)','; mem.Raw[buf + 20] = 0;
                    cpu.BX = 0x0001;
                    cpu.CF = false;
                }
                break;
            }

            // ---- FICHEROS ----

            case 0x3B: // Set current directory (chdir) — dos_files.cpp:520 DOS_ChangeDir
            {
                string dir = ReadDosString(mem, cpu.DS, cpu.DX);
                if (_fs.ChangeDir(dir, out byte errChdir)) cpu.CF = false;
                else { cpu.CF = true; cpu.AX = errChdir; }
                if (LogCalls)
                    Console.Error.WriteLine($"[int21] 3B chdir '{dir}' -> {(cpu.CF ? "ERR " + cpu.AX : "cwd=" + _fs.GetCurrentDir(_fs.CurrentDrive))} t={Cpu.TickCount}");
                break;
            }

            case 0x3C: // Create file
                CreateFile(cpu, mem);
                break;

            case 0x3D: // Open file
                OpenFile(cpu, mem);
                break;

            case 0x3E: // Close file
                CloseFile(cpu);
                break;

            case 0x3F: // Read file
                ReadFile(cpu, mem);
                break;

            case 0x40: // Write file
                WriteFile(cpu, mem);
                break;

            case 0x41: // Delete file — dos_files.cpp DOS_UnlinkFile
            {
                try
                {
                    string path = ResolvePath(cpu, mem);
                    if (!File.Exists(path)) { cpu.CF = true; cpu.AX = 2; break; }
                    File.Delete(path);
                    InvalidateDir(path);
                    cpu.CF = false;
                }
                catch { cpu.CF = true; cpu.AX = 5; }
                break;
            }

            case 0x42: // Seek (lseek)
                SeekFile(cpu);
                break;

            case 0x43: // Get/set file attributes — dos_files.cpp DOS_GetFileAttr / DOS_SetFileAttr
            {
                if (cpu.AL == 0) // get
                {
                    try
                    {
                        string path = ResolvePath(cpu, mem);
                        if (!File.Exists(path) && !Directory.Exists(path)) { cpu.CF = true; cpu.AX = 2; break; }
                        var attr = File.GetAttributes(path);
                        ushort dosAttr = 0x0020; // archive
                        if (attr.HasFlag(FileAttributes.ReadOnly)) dosAttr |= 0x0001;
                        if (attr.HasFlag(FileAttributes.Hidden))   dosAttr |= 0x0002;
                        if (attr.HasFlag(FileAttributes.System))   dosAttr |= 0x0004;
                        if (attr.HasFlag(FileAttributes.Directory)) dosAttr = 0x0010;
                        cpu.CX = dosAttr; cpu.CF = false;
                    }
                    catch { cpu.CF = false; cpu.CX = 0x0020; }
                }
                else // set (AL==1) — ignorar silenciosamente
                {
                    cpu.CF = false;
                }
                break;
            }

            case 0x44: // IOCTL — dos_ioctl.cpp
                HandleIOCTL(cpu, mem);
                break;

            case 0x45: // Duplicate file handle — dos_files.cpp DOS_DupHandle
            {
                int h = cpu.BX;
                if (!_handles.TryGetValue(h, out var fs))
                {
                    cpu.CF = true; cpu.AX = 6; break;
                }
                // Crear una nueva entrada apuntando al mismo FileStream (dup comparte posición)
                int newH = AllocHandle();
                _handles[newH] = fs; // shared reference — posición compartida como en DOS
                cpu.AX = (ushort)newH;
                cpu.CF = false;
                break;
            }

            case 0x46: // Force duplicate handle — dos_files.cpp DOS_ForceDupHandle
            {
                int srcH = cpu.BX;
                int dstH = cpu.CX;
                if (!_handles.TryGetValue(srcH, out var srcFs))
                {
                    cpu.CF = true; cpu.AX = 6; break;
                }
                // Si destino ya abierto, cerrarlo
                if (_handles.TryGetValue(dstH, out var old) && old != srcFs)
                {
                    try { old.Dispose(); } catch { }
                }
                _handles[dstH] = srcFs;
                cpu.CF = false;
                break;
            }

            case 0x47: // Get current directory — dos_files.cpp:490 DOS_GetCurrentDir
            {
                // DL = drive (0 = current, 1 = A). The buffer gets the path WITHOUT the drive and
                // WITHOUT the leading backslash, ASCIIZ, in 8.3 uppercase.
                byte cwdDrive = cpu.DL == 0 ? _fs.CurrentDrive : (byte)(cpu.DL - 1);
                string? cwd = _fs.GetCurrentDir(cwdDrive);
                if (cwd == null) { cpu.CF = true; cpu.AX = 15; break; }   // DOSERR_INVALID_DRIVE
                int dst = Memory.Lin(cpu.DS, cpu.SI);
                for (int i = 0; i < cwd.Length && i < 63; i++) mem.Raw[dst + i] = (byte)cwd[i];
                mem.Raw[dst + Math.Min(cwd.Length, 63)] = 0;
                cpu.AX = 0x0100;   // dos.cpp: AX destroyed; MS-DOS leaves 0100h
                cpu.CF = false;
                break;
            }

            case 0x48: // Allocate memory — dos_memory.cpp DOS_AllocateMemory
                AllocMemory(cpu, mem);
                break;

            case 0x49: // Free memory — dos_memory.cpp DOS_FreeMemory
                FreeMemory(cpu, mem);
                break;

            case 0x4A: // Resize memory block — dos_memory.cpp DOS_ResizeMemory
                ResizeMemory(cpu, mem);
                break;

            case 0x4B: // EXEC — dos_execute.cpp DOS_Execute
                HandleExec(cpu, mem);
                break;

            case 0x4C: // Terminate program
                _lastExitCode = cpu.AL;
                throw new ProgramExitException(cpu.AL);

            case 0x4D: // Get exit code of child — dos.cpp
                cpu.AX = _lastExitCode; // exit code in AL, exit type in AH (0=normal)
                cpu.AH = 0;
                cpu.CF = false;
                break;

            case 0x4E: // Find first matching file — dos_files.cpp DOS_FindFirst
                FindFirst(cpu, mem);
                break;

            case 0x4F: // Find next matching file — dos_files.cpp DOS_FindNext
                FindNext(cpu, mem);
                break;

            case 0x50: // Set PSP segment — dos.cpp
                _pspSeg = cpu.BX;
                cpu.CF = false;
                break;

            case 0x51: // Get PSP segment — dos.cpp
            case 0x62: // Get PSP segment (DOS 3+) — dos.cpp
                cpu.BX = _pspSeg;
                cpu.CF = false;
                break;

            case 0x39: // Make directory — dos_files.cpp DOS_MakeDir
            {
                try
                {
                    string path = ResolvePath(cpu, mem);
                    Directory.CreateDirectory(path);
                    InvalidateDir(path);
                    cpu.CF = false;
                }
                catch { cpu.CF = true; cpu.AX = 3; }
                break;
            }

            case 0x3A: // Remove directory — dos_files.cpp DOS_RemoveDir
            {
                try
                {
                    string path = ResolvePath(cpu, mem);
                    if (!Directory.Exists(path)) { cpu.CF = true; cpu.AX = 2; break; }
                    Directory.Delete(path);
                    InvalidateDir(path);
                    cpu.CF = false;
                }
                catch { cpu.CF = true; cpu.AX = 5; }
                break;
            }

            case 0x56: // Rename file — dos_files.cpp DOS_Rename
            {
                try
                {
                    string oldPath = ResolvePath(cpu, mem);
                    // ES:DI = new name
                    int newAddr = Memory.Lin(cpu.ES, cpu.DI);
                    var sb2 = new System.Text.StringBuilder();
                    for (int i = 0; i < 260; i++)
                    {
                        byte b = mem.Raw[newAddr + i];
                        if (b == 0) break;
                        sb2.Append((char)b);
                    }
                    string newDos = sb2.ToString().Replace('\\', Path.DirectorySeparatorChar);
                    if (newDos.Length > 2 && newDos[1] == ':') newDos = newDos[2..];
                    string newPath = Path.Combine(_gameDir, newDos.TrimStart(Path.DirectorySeparatorChar));
                    File.Move(oldPath, newPath);
                    cpu.CF = false;
                }
                catch { cpu.CF = true; cpu.AX = 2; }
                break;
            }

            case 0x57: // Get/set file date-time — dos_files.cpp
            {
                int h = cpu.BX;
                if (cpu.AL == 0) // get
                {
                    if (_handles.TryGetValue(h, out var fs))
                    {
                        var fi = new FileInfo(fs.Name);
                        var lw = fi.LastWriteTime;
                        cpu.CX = DosTime(lw);
                        cpu.DX = DosDate(lw);
                        cpu.CF = false;
                    }
                    else { cpu.CF = true; cpu.AX = 6; }
                }
                else // set (AL==1) — aceptar pero ignorar
                {
                    cpu.CF = false;
                }
                break;
            }

            case 0x5A: // Create temporary file — dos_files.cpp DOS_CreateTempFile
            {
                // Real DOS APPENDS a generated name to the ASCIIZ path the caller left at DS:DX
                // and the name carries NO extension: DOSBox-X builds eight random A-Z letters and
                // retries while the file exists (dos_files.cpp:1571). Both details are load-
                // bearing, and both were wrong here. We answered a fixed "TMP0000.TMP" from a
                // per-process counter, written OVER the caller's path instead of after it:
                //   - the extension made DOS/4GW's swap file "TMP0000.TMP.SWP" — two dots, not a
                //     valid 8.3 name (the real one is the rip's "AIBOBHDA.SWP" shape);
                //   - the counter restarts at zero in every process and never checked the disk,
                //     so a second instance in the same folder got the same name, could not create
                //     it while the first held it open, and DOS/4GW Pro died with
                //     "error (2504): can't create swap file" -> "fatal error (1011)".
                try
                {
                    string dosDir = ReadDosString(mem, cpu.DS, cpu.DX);
                    if (dosDir.Length > 0 && dosDir[^1] != '\\' && dosDir[^1] != '/') dosDir += '\\';
                    string dosPath, host;
                    do
                    {
                        var name = new char[8];
                        for (int i = 0; i < 8; i++) name[i] = (char)('A' + _tmpRand.Next(26));
                        dosPath = dosDir + new string(name);
                        host = _fs.ResolveHostPath(dosPath);
                    } while (File.Exists(host) || Directory.Exists(host));

                    var fs = new FileStream(host, FileMode.CreateNew, FileAccess.ReadWrite);
                    InvalidateDir(host);
                    int h = AllocHandle();
                    _handles[h] = fs;
                    int dst = Memory.Lin(cpu.DS, cpu.DX);
                    int j = 0;
                    while (dst + j < Memory.Size && j < dosPath.Length)
                    {
                        mem.Raw[dst + j] = (byte)dosPath[j]; j++;
                    }
                    if (dst + j < Memory.Size) mem.Raw[dst + j] = 0;
                    cpu.AX = (ushort)h; cpu.CF = false;
                }
                catch { cpu.CF = true; cpu.AX = 5; }
                break;
            }

            case 0x5B: // Create new file (fail if exists) — dos_files.cpp
            {
                try
                {
                    string path = ResolvePath(cpu, mem);
                    if (File.Exists(path)) { cpu.CF = true; cpu.AX = 80; break; } // ERROR_FILE_EXISTS
                    var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
                    int h = AllocHandle();
                    _handles[h] = fs;
                    cpu.AX = (ushort)h; cpu.CF = false;
                }
                catch { cpu.CF = true; cpu.AX = 5; }
                break;
            }

            case 0x5C: // Lock/unlock file region — dos_files.cpp (no-op en emulación)
                cpu.CF = false;
                break;

            case 0x54: // Get verify flag — dos.cpp
                cpu.AL = 0; // verify off
                cpu.CF = false;
                break;

            case 0x58: // Get/set memory allocation strategy — dos_memory.cpp
                if (cpu.AL == 0) // get
                    cpu.AX = 0x0000; // first fit
                else // set (AL=1)
                    cpu.CF = false;
                cpu.CF = false;
                break;

            case 0x59: // Get extended error information — dos.cpp DOS_GetExtendedError
                cpu.AX = 0; // no error
                cpu.BH = 0; // class: out of resource
                cpu.BL = 0; // action: retry
                cpu.CH = 0; // locus: unknown
                cpu.CF = false;
                break;

            case 0x60: // Canonicalize path — dos_files.cpp DOS_Canonicalize
            {
                // The canonical name is DRIVE:\ + the FULL path, so it has to go through the same
                // resolver as everything else: a relative name hangs from the current directory of
                // its drive, and every component comes back in 8.3 uppercase.
                string dosPath = ReadDosString(mem, cpu.DS, cpu.SI);
                var rc = _fs.Resolve(dosPath);
                if (!rc.DriveMounted) { cpu.CF = true; cpu.AX = 15; break; }
                string canon = $"{(char)('A' + rc.Drive)}:\\";
                if (rc.DosDir.Length > 0) canon += rc.DosDir + "\\";
                if (rc.Leaf.Length > 0)
                {
                    var le = _fs.Lookup(rc.HostDir, rc.Leaf);
                    canon += le != null ? le.ShortName : rc.Leaf.ToUpperInvariant();
                }
                else if (canon.Length > 3) canon = canon[..^1];
                int dst = Memory.Lin(cpu.ES, cpu.DI);
                for (int i = 0; i < canon.Length && i < 127; i++)
                    mem.Raw[dst + i] = (byte)canon[i];
                mem.Raw[dst + Math.Min(canon.Length, 127)] = 0;
                cpu.AX = 0;
                cpu.CF = false;
                break;
            }

            case 0x63: // Get lead byte table — dos.cpp (DBCS)
                // Return empty table for non-DBCS systems
                cpu.CF = false;
                break;

            case 0x65: // Get extended country info — dos.cpp DOS_GetExtCountryInfo
                if (cpu.AL == 0x01) // get general internationalization info
                {
                    int buf = Memory.Lin(cpu.ES, cpu.DI);
                    mem.Raw[buf] = 0x01; // info ID
                    // size (word)
                    mem.Raw[buf + 1] = 38; mem.Raw[buf + 2] = 0;
                    // country code (word)
                    mem.Raw[buf + 3] = 1; mem.Raw[buf + 4] = 0; // USA
                    // code page (word)
                    mem.Raw[buf + 5] = 0xE1; mem.Raw[buf + 6] = 0x01; // 437
                    cpu.CX = 41;
                    cpu.CF = false;
                }
                else { cpu.CF = false; }
                break;

            case 0x66: // Get/set code page — dos.cpp DOS_GetCodePage
                if (cpu.AL == 1) // get
                {
                    cpu.BX = 437; // active code page
                    cpu.DX = 437; // system code page
                    cpu.CF = false;
                }
                else // set (AL=2)
                    cpu.CF = false;
                break;

            case 0x67: // Set handle count — dos.cpp (no-op, aceptamos)
                cpu.CF = false;
                break;

            case 0x68: // Commit/flush file — dos_files.cpp DOS_FlushFile
            case 0x6A: // Commit file — alias exacto de 68 en dos.cpp
            {
                int h = cpu.BX;
                if (_handles.TryGetValue(h, out var fs))
                {
                    try { fs.Flush(); cpu.CF = false; }
                    catch { cpu.CF = true; cpu.AX = 6; }
                }
                else if (h < 5) cpu.CF = false;   // handles estándar (con/aux/prn): flush ok
                else { cpu.CF = true; cpu.AX = 6; }
                break;
            }

            // ── Barrido de plataforma: resto de la tabla
            //    de dos.cpp, con su semántica exacta ──────────────────────────────────

            case 0x18: case 0x1D: case 0x1E: case 0x20: // NULL functions CP/M — AL=0
            case 0x61:                                  // reserved (network) — AL=0
            case 0x6B:                                  // NULL function — AL=0
                cpu.AL = 0;
                break;

            case 0x1F: // Get default drive parameter block — dos.cpp -> case 0x32
            case 0x32: // Get drive parameter block: AL=0 ok (DS:BX -> DPB sintético)
            {
                byte drv = cpu.AH == 0x1F ? (byte)0 : cpu.DL;
                if (drv <= 3) // default o A:..C:
                {
                    cpu.AL = 0x00;
                    cpu.DS = DosTablesSeg; cpu.BX = DpbOff;
                }
                else cpu.AL = 0xFF;
                break;
            }

            case 0x52: // Get list of lists (SysVars) — ES:BX al bloque sintético.
                // Campo más consultado por juegos/utilidades: [sysvars-2] = primer MCB.
                _mcb ??= new McbChain(mem);
                mem.WrU16(DosTablesSeg, SysVarsOff - 2, _mcb.Start);
                cpu.ES = DosTablesSeg; cpu.BX = SysVarsOff;
                break;

            case 0x55: // Create child PSP — dos.cpp: copia el PSP a DX:0000, AL destruido
            {
                int src = Memory.Lin(_pspSeg, 0);
                int dst = Memory.Lin(cpu.DX, 0);
                mem.CopiaDeServicio(dst, src, 256);
                _pspSeg = cpu.DX; // dos.psp(reg_dx)
                cpu.AL = 0xF0;
                break;
            }

            case 0x5D: // Server/network functions — solo AL=06/07/08 stub, resto error
                // dos.cpp: sin red, las subfunciones útiles no aplican -> CF=1 error
                cpu.CF = true; cpu.AX = 1; // function number invalid
                break;

            case 0x5E: // Network machine name / printer — sin red: CF=1
            case 0x5F: // Network redirection — sin red: CF=1
                cpu.CF = true; cpu.AX = 1;
                break;

            case 0x64: // Set device driver lookahead flag — dos.cpp: no-op
                break;

            case 0x69: // Get/set disk serial number — dos.cpp
                if (cpu.AL == 0)
                {
                    // Get: escribe el bloque info en DS:DX (word 0, serial dword, vol label)
                    int p = Memory.Lin(cpu.DS, cpu.DX);
                    mem.WrU16(cpu.DS, cpu.DX, 0);
                    for (int i = 2; i < 6; i++) mem.Raw[p + i] = 0x00;            // serial 0
                    for (int i = 6; i < 17; i++) mem.Raw[p + i] = (byte)' ';      // label
                    for (int i = 17; i < 25; i++) mem.Raw[p + i] = (byte)' ';     // FAT type
                    cpu.CL = 0x66; // IOCTL function usada (dos.cpp)
                    cpu.CF = false;
                }
                else if (cpu.AL == 1) cpu.CF = false; // set: aceptar
                else { cpu.CF = true; cpu.AX = 1; }
                break;

            case 0x6C: // Extended open/create (DOS 4+) — mapear a open/create clásicos
            {
                // BX=modo, DX=acción (bit0=abrir si existe, bit1=truncar si existe,
                // bit4=crear si no existe), DS:SI=nombre. dos.cpp DOS_OpenFileExtended.
                ushort action = cpu.DX;
                ushort mode = cpu.BX;
                ushort savedDx = cpu.DX;
                cpu.DX = cpu.SI; // ResolvePath/Open/Create leen el nombre de DS:DX
                bool exists;
                try { exists = File.Exists(ResolvePath(cpu, mem)); }
                catch { cpu.CF = true; cpu.AX = 2; cpu.DX = savedDx; break; }
                if (exists && (action & 0x03) == 0) { cpu.CF = true; cpu.AX = 0x50; cpu.DX = savedDx; break; }
                if (!exists && (action & 0x10) == 0) { cpu.CF = true; cpu.AX = 2; cpu.DX = savedDx; break; }
                if (!exists || (action & 0x02) != 0)
                {
                    ushort savedCx = cpu.CX;
                    cpu.CX = 0; CreateFile(cpu, mem);          // 3C
                    cpu.CX = savedCx;
                    if (!cpu.CF) cpu.CX = (ushort)(exists ? 3 : 2); // 3=truncado, 2=creado
                }
                else
                {
                    cpu.AL = (byte)(mode & 3); OpenFile(cpu, mem);  // 3D
                    if (!cpu.CF) cpu.CX = 1;                        // 1=abierto existente
                }
                cpu.DX = savedDx;
                break;
            }

            default:
                // dos.cpp default_fallthrough: AL=0, resto de registros intactos
                cpu.AL = 0;
                cpu.CF = false;
                break;
        }
    }

    // ---- IOCTL (AH=44h) — dos_ioctl.cpp ----
    private void HandleIOCTL(Cpu cpu, Memory mem)
    {
        switch (cpu.AL)
        {
            case 0x00: // Get device info — dos_ioctl.cpp IOCTL_GetDeviceInfo
            {
                int h = cpu.BX;
                if (h < 5)
                {
                    // Handles estándar: consola/char device
                    cpu.DX = 0x80D3; // char device, CON, STDIN/STDOUT bits
                    cpu.CF = false;
                }
                else if (_handles.ContainsKey(h))
                {
                    cpu.DX = 0x0002; // file, en drive C:
                    cpu.CF = false;
                }
                else { cpu.CF = true; cpu.AX = 6; }
                break;
            }

            case 0x01: // Set device info — dos_ioctl.cpp (no-op)
                cpu.CF = false;
                break;

            case 0x06: // Get input status — dos_ioctl.cpp
            {
                int h = cpu.BX;
                if (h < 3)
                    cpu.AL = 0xFF; // char device, always ready
                else if (_handles.TryGetValue(h, out var fs))
                    cpu.AL = (fs.Position < fs.Length) ? (byte)0xFF : (byte)0x00;
                else { cpu.CF = true; cpu.AX = 6; break; }
                cpu.CF = false;
                break;
            }

            case 0x07: // Get output status — dos_ioctl.cpp
                cpu.AL = 0xFF; // always ready
                cpu.CF = false;
                break;

            case 0x08: // Check if removable — dos_ioctl.cpp (BL = drive, 0 = current)
            {
                byte rmDrive = cpu.BL == 0 ? _fs.CurrentDrive : (byte)(cpu.BL - 1);
                if (!_fs.IsMounted(rmDrive)) { cpu.CF = true; cpu.AX = 15; break; }  // DOSERR_INVALID_DRIVE
                cpu.AX = 1; // every mounted drive is a fixed disk
                cpu.CF = false;
                break;
            }

            case 0x09: // Check if remote (network) — dos_ioctl.cpp (BL = drive, 0 = current)
            {
                byte rmtDrive = cpu.BL == 0 ? _fs.CurrentDrive : (byte)(cpu.BL - 1);
                if (!_fs.IsMounted(rmtDrive)) { cpu.CF = true; cpu.AX = 15; break; }
                cpu.DX = 0x0000; // local
                cpu.CF = false;
                break;
            }

            default:
                cpu.CF = true; cpu.AX = 1; // invalid function
                break;
        }
    }

    // ---- EXEC (AH=4Bh) — dos_execute.cpp DOS_Execute ----
    private void HandleExec(Cpu cpu, Memory mem)
    {
        switch (cpu.AL)
        {
            case 0x03: // Load overlay — dos_execute.cpp flags==OVERLAY
            {
                // ES:BX → overlay param block: [loadseg u16, reloc u16]
                int pbAddr = Memory.Lin(cpu.ES, cpu.BX);
                ushort loadSeg = (ushort)(mem.Raw[pbAddr] | (mem.Raw[pbAddr + 1] << 8));
                ushort relocFactor = (ushort)(mem.Raw[pbAddr + 2] | (mem.Raw[pbAddr + 3] << 8));

                string path = ResolvePathFromPtr(cpu, mem, cpu.DS, cpu.DX);
                // ResolvePathFromPtr already matched case-insensitively and by 8.3 short name.
                if (!File.Exists(path)) { cpu.CF = true; cpu.AX = 2; break; }
                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    bool isMz = data.Length >= 2 && data[0] == 'M' && data[1] == 'Z';

                    if (isMz)
                    {
                        // EXE overlay: parse header, load image, apply relocations
                        int headerSize = ((data[8] | (data[9] << 8)) * 16);
                        int lastPage   = data[2] | (data[3] << 8);
                        int totalPages = data[4] | (data[5] << 8);
                        int relocCount = data[6] | (data[7] << 8);
                        int relocTable = data[24] | (data[25] << 8);
                        int imageSize  = totalPages * 512 - headerSize;
                        if (lastPage > 0) imageSize = (totalPages - 1) * 512 + lastPage - headerSize;
                        if (imageSize < 0) imageSize = 0;

                        int loadBase = loadSeg * 16;
                        int copyLen = Math.Min(imageSize, data.Length - headerSize);
                        copyLen = Math.Min(copyLen, Memory.Size - loadBase);
                        if (copyLen > 0)
                            mem.CopiaDeServicio(loadBase, data, headerSize, copyLen);

                        // Aplicar relocaciones sumando relocFactor
                        for (int i = 0; i < relocCount; i++)
                        {
                            int rOff = relocTable + i * 4;
                            if (rOff + 3 >= data.Length) break;
                            ushort rRelOff = (ushort)(data[rOff] | (data[rOff + 1] << 8));
                            ushort rSeg    = (ushort)(data[rOff + 2] | (data[rOff + 3] << 8));
                            int fixAddr = loadBase + rSeg * 16 + rRelOff;
                            if (fixAddr + 1 < Memory.Size)
                            {
                                ushort val = (ushort)(mem.Raw[fixAddr] | (mem.Raw[fixAddr + 1] << 8));
                                val += relocFactor;
                                mem.Raw[fixAddr] = (byte)val;
                                mem.Raw[fixAddr + 1] = (byte)(val >> 8);
                            }
                        }
                    }
                    else
                    {
                        // COM / binario plano: cargar directamente en loadSeg:0000
                        int loadBase = loadSeg * 16;
                        int copyLen = Math.Min(data.Length, Memory.Size - loadBase);
                        mem.CopiaDeServicio(loadBase, data, 0, copyLen);
                    }
                    // Registrar el segmento como driver EXEC: las llamadas far del juego a
                    // este segmento caen en FarDispatch, que sin registro las descarta como
                    // MISS (el codigo cargado en runtime no esta transpilado) y el juego
                    // cree que el driver fallo. Con el registro, DriverRegistry resuelve al
                    // codigo LIFTEADO del driver (DriverKeyBases por nombre) — igual que el
                    // mundo snapshot.
                    DriverRegistry.RegisterDriverSegment(loadSeg, Path.GetFileNameWithoutExtension(path));
                    ExecLoads.Add((Path.GetFileName(path), loadSeg, relocFactor));
                    cpu.CF = false;
                }
                catch { cpu.CF = true; cpu.AX = 5; }
                break;
            }

            case 0x00: // Load and execute child — dos_execute.cpp flags==LOADNGO
                ExecLoadAndGo(cpu, mem);
                break;

            default:
                cpu.CF = true; cpu.AX = 1;
                break;
        }
    }

    // ---- EXEC LOADNGO (AH=4B AL=00) — dos_execute.cpp DOS_Execute, flags==LOADNGO ----
    // Carga un proceso hijo (EXE o COM) y transfiere el flujo a su entry. En el port
    // "ejecutar el hijo" = seguir corriendo en el MISMO Cpu/Memory: guardamos el contexto del
    // padre, montamos PSP/entorno del hijo con memoria propia (por encima del padre, como DOS
    // real) y corremos el hijo con el intérprete propio (WholeProgram) hasta su 4C/31. El 4C
    // del hijo lanza ProgramExitException, que capturamos AQUÍ (no mata el host): restauramos
    // el contexto del padre y devolvemos con AX=exit code disponible vía 4D. Un hijo no
    // lifteado corre 100% interpretado (correcto: la autocuración --seeds lo lifta después).
    /// <summary>MakeEnv de dos_execute.cpp: el hijo recibe SIEMPRE una COPIA nueva del entorno
    /// de <paramref name="srcEnvSeg"/> (0 = sin entorno: un solo \0), dimensionada
    /// strings+2+ENV_KEEPFREE (1024, default DOSBox-X "additional environment block size on
    /// exec") y con la cola DOS 3+: \0\0, word 0001, ruta canónica del programa ("C:\NOMBRE.EXT";
    /// los juegos van montados en la raíz de C:). Devuelve 0 si no hay memoria.</summary>
    private static ushort MakeChildEnv(Memory mem, McbChain mcb, ushort srcEnvSeg, string fileName)
    {
        const int EnvKeepFree = 1024;
        int envsize = 1;
        if (srcEnvSeg != 0)
        {
            int src = Memory.Lin(srcEnvSeg, 0);
            envsize = 0;
            while (!(mem.Raw[src + envsize] == 0 && mem.Raw[src + envsize + 1] == 0))
                if (++envsize >= 32768 - EnvKeepFree) return 0;
            envsize += 2; // incluye el \0\0 final
        }
        ushort paras = (ushort)((envsize + EnvKeepFree + 15) >> 4);
        if (!mcb.Alloc(paras, out ushort seg, out _)) return 0;
        int dst = Memory.Lin(seg, 0);
        if (srcEnvSeg != 0) mem.CopiaDeServicio(dst, Memory.Lin(srcEnvSeg, 0), envsize);
        else mem.Raw[dst] = 0;
        int w = dst + envsize;
        mem.Raw[w++] = 1; mem.Raw[w++] = 0;
        foreach (char ch in "C:\\" + fileName.ToUpperInvariant()) mem.Raw[w++] = (byte)ch;
        mem.Raw[w] = 0;
        return seg;
    }

    /// <summary>Bloque de programa de DOS_Execute: un EXE pide PSP+imagen+MAXALLOC y si no
    /// cabe cae al mayor bloque libre (mínimo PSP+imagen+MINALLOC); un COM recibe el mayor
    /// bloque entero (el crt0 encoge luego con SETBLOCK). El caso habitual maxalloc=0xFFFF
    /// equivale a "dame todo" = mayor bloque.</summary>
    private static bool AllocProgramBlock(McbChain mcb, byte[] bytes, bool isMz, out ushort pspSeg, out ushort allocParas)
    {
        pspSeg = 0; allocParas = 0;
        mcb.Alloc(0xFFFF, out _, out ushort largest); // falla a propósito: deja el mayor libre
        if (isMz)
        {
            int lastPage = bytes[2] | (bytes[3] << 8);
            int pages    = bytes[4] | (bytes[5] << 8);
            int hdrParas = bytes[8] | (bytes[9] << 8);
            ushort minAlloc = (ushort)(bytes[10] | (bytes[11] << 8));
            ushort maxAlloc = (ushort)(bytes[12] | (bytes[13] << 8));
            int imgBytes = pages * 512 + (lastPage > 0 ? lastPage - 512 : 0) - hdrParas * 16;
            uint imgParas = (uint)((imgBytes + 15) / 16);
            uint needMin = 0x10 + imgParas + minAlloc;      // PSP + imagen + MINALLOC
            uint needMax = 0x10 + imgParas + maxAlloc;      // PSP + imagen + MAXALLOC
            if (largest < needMin) return false;
            allocParas = needMax <= largest ? (ushort)needMax : largest;
        }
        else
        {
            if (largest < 0x100) return false;
            allocParas = largest;
        }
        return mcb.Alloc(allocParas, out pspSeg, out _);
    }

    /// <summary>Boot "DOS mínimo real" para mundos boot-real fieles: construye el mapa
    /// convencional del DOS real (McbChain.InitRealDosBootMap) y asigna entorno + bloque del
    /// programa raíz con la MISMA semántica que un EXEC (copia del entorno de la shell,
    /// first-fit). El host coloca después imagen/PSP/registros (COM fiel: PSP=CS, imagen en
    /// PSP:0100, PSP[2]=Psp+Granted) y entrega el estado al Int21 de la plataforma con
    /// AdoptBootState. Con este mapa un raíz COM cae en PSP 0x0814, como en el oráculo.</summary>
    public static (ushort Psp, ushort Env, ushort Granted, McbChain Mcb) BootRealDos(Memory mem, string fileName, byte[] exeBytes)
    {
        var mcb = new McbChain(mem);
        mcb.InitRealDosBootMap();
        ushort env = MakeChildEnv(mem, mcb, McbChain.RealDosShellEnvSeg, fileName);
        bool isMz = exeBytes.Length >= 2 && exeBytes[0] == 'M' && exeBytes[1] == 'Z';
        if (env == 0 || !AllocProgramBlock(mcb, exeBytes, isMz, out ushort psp, out ushort granted))
            throw new InvalidOperationException("BootRealDos: sin memoria para el programa raíz");
        string name = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        mcb.SetOwner(env, psp); mcb.SetOwner(psp, psp);
        mcb.SetBlockName(env, name); mcb.SetBlockName(psp, name);
        return (psp, env, granted, mcb);
    }

    /// <summary>Adopta el estado creado por BootRealDos (cadena MCB + PSP del raíz).</summary>
    public void AdoptBootState(McbChain mcb, ushort pspSeg)
    {
        _mcb = mcb;
        _pspSeg = pspSeg;
        _dtaSeg = pspSeg; _dtaOff = 0x80;
    }

    private void ExecLoadAndGo(Cpu cpu, Memory mem)
    {
        _mcb ??= new McbChain(mem);

        // Nombre del hijo (DS:DX) y bloque de parámetros (ES:BX).
        string childPath;
        try { childPath = ResolvePath(cpu, mem); }
        catch { cpu.CF = true; cpu.AX = 2; return; }
        // ResolvePath already matched case-insensitively and by 8.3 short name.
        if (!File.Exists(childPath)) { cpu.CF = true; cpu.AX = 2; return; } // file not found

        int pb = Memory.Lin(cpu.ES, cpu.BX);
        ushort blkEnvSeg = (ushort)(mem.Raw[pb] | (mem.Raw[pb + 1] << 8));
        ushort cmdOff = (ushort)(mem.Raw[pb + 2] | (mem.Raw[pb + 3] << 8));
        ushort cmdSeg = (ushort)(mem.Raw[pb + 4] | (mem.Raw[pb + 5] << 8));

        byte[] childBytes;
        try { childBytes = File.ReadAllBytes(childPath); }
        catch { cpu.CF = true; cpu.AX = 5; return; }
        bool isMz = childBytes.Length >= 2 && childBytes[0] == 'M' && childBytes[1] == 'Z';

        // Asignar memoria del hijo: primero el entorno (MakeEnv), luego el bloque del PSP+imagen
        // (AllocProgramBlock) — semántica de DOS_Execute (dos_execute.cpp). El entorno es
        // SIEMPRE una copia nueva del bloque que indique el param-block (o del entorno del
        // padre en PSP:2C si trae 0); su tamaño forma parte del MAPA de memoria: con el
        // sintético de 0x40 la cadena EXEC entera quedaba corrida 9 párrafos vs el DOS real.
        ushort srcEnv = blkEnvSeg != 0 ? blkEnvSeg
            : (ushort)(mem.Raw[Memory.Lin(_pspSeg, 0x2C)] | (mem.Raw[Memory.Lin(_pspSeg, 0x2C) + 1] << 8));
        ushort envSeg = MakeChildEnv(mem, _mcb, srcEnv, Path.GetFileName(childPath));
        if (envSeg == 0) { cpu.CF = true; cpu.AX = 8; return; }
        if (!AllocProgramBlock(_mcb, childBytes, isMz, out ushort pspSeg, out ushort allocParas))
        { _mcb.Free(envSeg); cpu.CF = true; cpu.AX = 8; return; }
        // Dueño real de los bloques del hijo = su PSP (para FreeOwnedBy al terminar) y nombre
        // de programa en ambos MCB, como escribe DOS.
        string mcbName = Path.GetFileNameWithoutExtension(childPath).ToUpperInvariant();
        _mcb.SetOwner(envSeg, pspSeg);
        _mcb.SetOwner(pspSeg, pspSeg);
        _mcb.SetBlockName(envSeg, mcbName);
        _mcb.SetBlockName(pspSeg, mcbName);

        // Guardar el contexto del padre (registros + PSP/DTA) para restaurarlo al terminar el
        // hijo — el hijo pisa todo el estado arquitectónico mientras corre.
        ushort pAX = cpu.AX, pBX = cpu.BX, pCX = cpu.CX, pDX = cpu.DX;
        ushort pSI = cpu.SI, pDI = cpu.DI, pBP = cpu.BP, pSP = cpu.SP;
        ushort pCS = cpu.CS, pDS = cpu.DS, pES = cpu.ES, pSS = cpu.SS;
        ushort pFlags = cpu.FlagsWord();
        ushort parentPsp = _pspSeg;
        ushort parentDtaSeg = _dtaSeg; ushort parentDtaOff = _dtaOff;

        // Entorno del hijo: la copia recién creada (MakeChildEnv, semántica MakeEnv de DOS).
        ushort childEnv = envSeg;

        // Cargar la imagen y calcular el entry.
        ushort childCs, childIp, childSs, childSp, childDsEs;
        if (isMz)
        {
            ushort loadSeg = (ushort)(pspSeg + 0x10);
            var mz = MzLoader.Load(childBytes, mem, loadSeg);
            childCs = mz.AbsCS; childIp = mz.AbsIP;
            childSs = mz.AbsSS; childSp = mz.InitSP;
            childDsEs = pspSeg;
        }
        else
        {
            // COM: PSP y código comparten segmento; código en pspSeg:0x100, pila al tope del
            // bloque (acotada al bloque asignado, como DOSBox para COM en poca RAM).
            MzLoader.LoadCom(childBytes, mem, pspSeg);
            childCs = pspSeg; childIp = 0x0100;
            childSs = pspSeg;
            childSp = (ushort)((allocParas * 16UL - 2) > 0xFFFE ? 0xFFFE : (allocParas * 16UL - 2));
            childDsEs = pspSeg;
            mem.WrU16(childSs, childSp, 0); // palabra 0 al tope de la pila (retorno del PSP)
        }

        // PSP del hijo: INT20/memsize/entorno + cola de comandos heredada del bloque.
        // PSP[2] = primer segmento más allá del bloque concedido (SetupPSP de dos_execute.cpp).
        MzLoader.SetupPsp(mem, pspSeg, childEnv, (ushort)(pspSeg + allocParas));
        if (cmdSeg != 0 || cmdOff != 0)
        {
            int src = Memory.Lin(cmdSeg, cmdOff);
            int dst = Memory.Lin(pspSeg, 0x80);
            byte n = mem.Raw[src];
            for (int i = 0; i <= n + 1 && i < 128; i++) mem.Raw[dst + i] = mem.Raw[src + i];
        }

        // Registros de arranque del hijo (dos_execute.cpp LOADNGO): DS=ES=PSP, DX=PSP,
        // CX=0xFF, AX=0 (unidades FCB válidas), SI/DI/BP del relict de DOS.
        cpu.AX = 0; cpu.BX = allocParas; cpu.CX = 0x00FF; cpu.DX = pspSeg;
        cpu.SI = childIp; cpu.DI = childSp; cpu.BP = 0x091C;
        cpu.CS = childCs; cpu.SS = childSs; cpu.SP = childSp;
        cpu.DS = childDsEs; cpu.ES = childDsEs;
        cpu.CF = false;

        _pspSeg = pspSeg;
        _dtaSeg = pspSeg; _dtaOff = 0x80;

        if (LogCalls)
        {
            // Dump inherited command tail (PSP:0x80 = len byte + text) and environment strings
            int ctBase = Memory.Lin(pspSeg, 0x80);
            int ctLen = mem.Raw[ctBase];
            var ctSb = new System.Text.StringBuilder();
            for (int i = 1; i <= ctLen && i < 128; i++)
            {
                byte b = mem.Raw[ctBase + i];
                ctSb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
            }
            var envSb = new System.Text.StringBuilder();
            if (childEnv != 0)
            {
                int e = Memory.Lin(childEnv, 0);
                for (int guard = 0; guard < 2048; guard++)
                {
                    byte b = mem.Raw[e + guard];
                    if (b == 0)
                    {
                        if (mem.Raw[e + guard + 1] == 0) break; // double-NUL terminates env
                        envSb.Append('|');
                    }
                    else envSb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
                }
            }
            Console.Error.WriteLine($"[int21] 4B00 EXEC child='{Path.GetFileName(childPath)}' " +
                $"{(isMz ? "EXE" : "COM")} psp={pspSeg:X4} entry={childCs:X4}:{childIp:X4} env={childEnv:X4} " +
                $"granted=0x{allocParas:X4} ({allocParas * 16} B) cmdtail[{ctLen}]='{ctSb}' envblk='{envSb}' t={Cpu.TickCount}");
        }

        // Correr el hijo en el intérprete propio (WholeProgram: ret/retf/iret no terminan el
        // frame, corre el programa entero) hasta su 4C/31. El intérprete no puede referenciar
        // el código lifteado del padre — el hijo corre 100% interpretado (callKey/farDispatch
        // nulos = emulación pura de las transferencias far).
        byte childExit = 0;
        bool childTsr = false;
        bool savedWp = Interp.WholeProgram;
        Interp.WholeProgram = true;
        // DOS_Terminate closes the terminating process's open files (dos_execute.cpp:119
        // curpsp.CloseFiles()). There is no per-PSP JFT here, so snapshot the handle table
        // and close whatever the child leaves open on return — otherwise every leaky child
        // raises the monotonic handle floor until an open crosses the Borland CRT _NFILE
        // limit (one test game: its intro leaks a handle -> a later data file gets handle >= 20 -> exit(10)).
        var preHandles = new HashSet<int>(_handles.Keys);
        try
        {
            Interp.Run(cpu, mem, childCs, childIp, null, null);
        }
        catch (ProgramExitException pex)
        {
            childExit = pex.ExitCode;
            childTsr = pex.Tsr;
        }
        finally
        {
            Interp.WholeProgram = savedWp;
        }
        // AH=31 (TSR): el hijo queda residente — su memoria (ya encogida a DX párrafos en el
        // case 0x31) y sus handles abiertos se CONSERVAN (dos_execute.cpp: CloseFiles y
        // DOS_FreeProcessMemory sólo si !tsr).
        if (!childTsr)
        {
            var leaked = _handles.Keys.Where(h => !preHandles.Contains(h)).ToList();
            foreach (int h in leaked)
            {
                _handles.Remove(h, out var lfs);
                // DUP (AH=45/46) shares the FileStream object between handles: only dispose
                // when no surviving handle still references it.
                if (lfs != null && !_handles.Values.Contains(lfs)) lfs.Dispose();
            }
            if (leaked.Count > 0 && LogCalls)
                Console.Error.WriteLine($"[int21] 4B00 child left {leaked.Count} open handle(s) [{string.Join(",", leaked)}] — closed (DOS_Terminate CloseFiles) t={Cpu.TickCount}");

            // El hijo terminó: liberar TODA su memoria (PSP+imagen, entorno Y sus reservas AH=48 —
            // FreeProcessMemory de dos_execute.cpp; sin esto cada hijo fuga sus buffers: un juego se
            // quedaba sin memoria para su framebuffer de 64000 bytes al empezar partida) y
            // restaurar el contexto del padre. Volvemos por retorno síncrono de la función.
            _mcb.FreeOwnedBy(pspSeg);
            _mcb.Free(envSeg);
        }
        else if (LogCalls)
            Console.Error.WriteLine($"[int21] 4B00 child stayed resident (AH=31), memory kept t={Cpu.TickCount}");
        _lastExitCode = childExit;

        cpu.AX = pAX; cpu.BX = pBX; cpu.CX = pCX; cpu.DX = pDX;
        cpu.SI = pSI; cpu.DI = pDI; cpu.BP = pBP; cpu.SP = pSP;
        cpu.CS = pCS; cpu.DS = pDS; cpu.ES = pES; cpu.SS = pSS;
        cpu.SetFlagsWord(pFlags);
        _pspSeg = parentPsp;
        _dtaSeg = parentDtaSeg; _dtaOff = parentDtaOff;
        cpu.CF = false; // EXEC completado con éxito (el padre lee el código con 4D)

        if (LogCalls)
            Console.Error.WriteLine($"[int21] 4B00 EXEC returned to parent, child exit={childExit:X2} t={Cpu.TickCount}");
    }

    // ---- MEMORIA MCB (AH=48/49/4A) — dos_memory.cpp ----
    private void AllocMemory(Cpu cpu, Memory mem)
    {
        // Inicializar cadena MCB si aún no está lista
        _mcb ??= new McbChain(mem);

        ushort paras = cpu.BX;
        ushort seg = 0;
        ushort largest = 0;
        if (LogCalls && paras >= 0x0F00 && paras <= 0x1100)
        {
            // Dump the return chain on the stack so we can locate chargen's buffer-alloc loop
            int sp = Memory.Lin(cpu.SS, cpu.SP);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 12; i++)
                sb.Append($"{(ushort)(mem.Raw[sp + i * 2] | (mem.Raw[sp + i * 2 + 1] << 8)):X4} ");
            Console.Error.WriteLine($"[int21] 48 BIGALLOC paras=0x{paras:X4} SS:SP={cpu.SS:X4}:{cpu.SP:X4} stack=[{sb}] t={Cpu.TickCount}");
        }
        // owner = PSP actual (DOS): imprescindible para que FreeOwnedBy libere lo del
        // proceso al terminar (los hijos EXEC reservan framebuffers de 64K con AH=48)
        bool ok = _mcb.Alloc(paras, out seg, out largest, _pspSeg != 0 ? _pspSeg : (ushort)0x0001);
        if (ok)
        {
            cpu.AX = seg;
            cpu.CF = false;
        }
        else
        {
            cpu.CF = true;
            cpu.AX = 8; // insufficient memory
            cpu.BX = largest; // largest available block
            if (LogCalls)
            {
                Console.Error.WriteLine($"[int21] 48 ALLOC FAIL: pedido=0x{paras:X4} ({paras * 16} B) " +
                    $"mayor_libre=0x{largest:X4} ({largest * 16} B) psp=0x{_pspSeg:X4} t={Cpu.TickCount}");
                _mcb.DumpChain("alloc-48-fail");
            }
        }
    }

    private void FreeMemory(Cpu cpu, Memory mem)
    {
        _mcb ??= new McbChain(mem);
        bool ok = _mcb.Free(cpu.ES);
        if (ok)
            cpu.CF = false;
        else
        {
            cpu.CF = true; cpu.AX = 9; // invalid memory block address
        }
    }

    private void ResizeMemory(Cpu cpu, Memory mem)
    {
        _mcb ??= new McbChain(mem);
        ushort largest = 0;
        bool ok = _mcb.Resize(cpu.ES, cpu.BX, out largest);
        if (LogCalls)   // was re-reading the env var on EVERY AH=4A call
        {
            Console.Error.WriteLine($"[4A] ES={cpu.ES:X4} BX={cpu.BX:X4} ok={ok} largest={largest:X4} " +
                                    $"mcbStart={_mcb.Start:X4} mcbTop={_mcb.Top:X4} t={Cpu.TickCount}");
            // Un SETBLOCK que falla es donde muere un juego por memoria (medido en un juego de prueba): volcar la
            // cadena entera AQUÍ ahorra la siguiente ronda de instrumentación a mano.
            if (!ok) _mcb.DumpChain($"fallo 4A ES={cpu.ES:X4} BX={cpu.BX:X4}");
        }
        if (ok)
            cpu.CF = false;
        else if (cpu.ES < _mcb.Start)
        {
            // El programa redimensiona su propio bloque (fuera del arena que gestiona McbChain).
            int newEnd = cpu.ES + cpu.BX; // techo pedido para el bloque del programa (en paras)
            if (newEnd < _mcb.Start)
            {
                // SHRINK (SETBLOCK del crt0): la cola por encima del bloque encogido pasa al
                // pool libre — el crt0 define así dónde empieza la memoria libre de DOS.
                if (newEnd > 0x100) _mcb.ExtendBase((ushort)newEnd);
                cpu.CF = false;
            }
            else if (newEnd <= _mcb.Top)
            {
                // GROW dentro de la memoria convencional libre: DOS real lo concede. No lo
                // modelamos en la cadena (el bloque del programa vive fuera del arena); basta
                // conceder para que el crt0 siga.
                cpu.CF = false;
            }
            else
            {
                // GROW más allá del techo de memoria convencional (idiom "sondear máximo",
                // p.ej. AH=4A BX=FFFF del crt0 de un juego DOS/4GW): imposible. dos_memory.cpp
                // DOS_ResizeMemory devuelve CF=1, AX=8 (insuficiente) y BX=máximo en párrafos
                // (del bloque del programa al techo de la convencional).
                cpu.CF = true;
                cpu.AX = 8;
                cpu.BX = (ushort)(_mcb.Top - cpu.ES);
            }
        }
        else
        {
            cpu.CF = true;
            cpu.AX = 8;
            cpu.BX = largest;
        }
    }

    // ---- FIND FIRST/NEXT — dos_files.cpp:687 DOS_FindFirst / :782 DOS_FindNext ----
    // DTA layout (sDTA struct, dos_inc.h):
    //  +0x00  sdrive  (1)
    //  +0x01  spname  (8)
    //  +0x09  spext   (3)
    //  +0x0C  sattr   (1)
    //  +0x0D  dirID   (2)  — id of the open search
    //  +0x0F  dirCluster (4)
    //  +0x13  fill    (2)
    //  +0x15  attr    (1)
    //  +0x16  time    (2)
    //  +0x18  date    (2)
    //  +0x1A  size    (4)
    //  +0x1E  name    (13)  — 8.3 null-terminated
    //
    // The state of a search lives in the DTA (drive, pattern, attribute and search id), NOT in a
    // single field of the handler: DOS lets a program interleave searches by moving the DTA with
    // AH=1A between calls, and a music tracker's load dialog does exactly that.

    /// <summary>DOS_DTA::SetupSearch — dos_classes.cpp:444. The pattern is stored SPLIT into an
    /// 8-byte name and a 3-byte extension; that split is why a bare "*" only fills the field it
    /// appears in and therefore matches names WITHOUT extension only.</summary>
    private static void DtaSetupSearch(Memory mem, int dta, byte drive, byte attr, string pattern)
    {
        for (int i = 0; i < 11; i++) mem.Raw[dta + 0x01 + i] = 0;
        mem.Raw[dta + 0x00] = drive;
        mem.Raw[dta + 0x0C] = attr;
        int dot = pattern.IndexOf('.');
        string name = dot >= 0 ? pattern[..dot] : pattern;
        string ext = dot >= 0 ? pattern[(dot + 1)..] : "";
        for (int i = 0; i < Math.Min(name.Length, 8); i++) mem.Raw[dta + 0x01 + i] = (byte)name[i];
        for (int i = 0; i < Math.Min(ext.Length, 3); i++) mem.Raw[dta + 0x09 + i] = (byte)ext[i];
    }

    /// <summary>DOS_DTA::GetSearchParams — dos_classes.cpp:547: rebuilds "NAME.EXT" from the two
    /// fields, always inserting the dot.</summary>
    private static string DtaSearchPattern(Memory mem, int dta)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 8 && mem.Raw[dta + 0x01 + i] != 0; i++) sb.Append((char)mem.Raw[dta + 0x01 + i]);
        sb.Append('.');
        for (int i = 0; i < 3 && mem.Raw[dta + 0x09 + i] != 0; i++) sb.Append((char)mem.Raw[dta + 0x09 + i]);
        return sb.ToString();
    }

    private void FindFirst(Cpu cpu, Memory mem)
    {
        // DS:DX -> DOS search spec: "*.PIC", "C:\\SAVE\\*.SAV", "S\\*.S3M"...
        string spec = ReadDosString(mem, cpu.DS, cpu.DX);
        ushort searchAttr = cpu.CX;
        int dta = Memory.Lin(_dtaSeg, _dtaOff);

        // dos_files.cpp:693 — a spec ending in '\' is "no more files" (volume searches aside).
        if (spec.Length > 0 && (spec[^1] == '\\' || spec[^1] == '/') && searchAttr != DosFileSystem.AttrVolume)
        {
            cpu.CF = true; cpu.AX = 18; return;
        }

        var r = _fs.Resolve(spec);
        if (!r.DriveMounted) { cpu.CF = true; cpu.AX = 3; return; }

        // attr == DOS_ATTR_VOLUME exactly: the label and NO files (drive_local.cpp:1807).
        if (searchAttr == DosFileSystem.AttrVolume)
        {
            DtaSetupSearch(mem, dta, r.Drive, (byte)searchAttr, "*.*");
            mem.Raw[dta + 0x0D] = 0; mem.Raw[dta + 0x0E] = 0;
            WriteFindResult(mem, dta, _fs.LabelOf(r.Drive), DosFileSystem.AttrVolume, 0, GuestClock.Now(cpu.CyclesPerMs));
            cpu.CF = false; return;
        }

        string pattern = r.Leaf.Length > 0 ? r.Leaf : "*.*";
        var search = _fs.OpenSearch(r, out ushort id);
        if (search == null)
        {
            if (LogCalls)
                Console.Error.WriteLine($"[int21] 4E findfirst '{spec}' -> RUTA NO EXISTE ({r.HostDir}) t={Cpu.TickCount}");
            cpu.CF = true; cpu.AX = 3; return;   // DOSERR_PATH_NOT_FOUND
        }
        DtaSetupSearch(mem, dta, r.Drive, (byte)searchAttr, pattern);
        mem.Raw[dta + 0x0D] = (byte)id; mem.Raw[dta + 0x0E] = (byte)(id >> 8);

        // The pattern is read BACK from the DTA so the 8.3 truncation of SetupSearch applies.
        string effective = DtaSearchPattern(mem, dta);
        var e = _fs.NextMatch(search, effective, (byte)searchAttr);
        if (LogCalls)
            Console.Error.WriteLine($"[int21] 4E findfirst '{spec}' pat='{effective}' attr={searchAttr:X2} dir='{r.HostDir}' entradas={search.Items.Count} -> {(e == null ? "SIN RESULTADOS" : e.ShortName)} t={Cpu.TickCount}");
        if (e == null) { cpu.CF = true; cpu.AX = 2; return; }   // DOSERR_FILE_NOT_FOUND
        WriteFindResult(mem, dta, e);
        cpu.CF = false;
    }

    private void FindNext(Cpu cpu, Memory mem)
    {
        // The search continues the one recorded in the DTA THAT IS SET NOW, which need not be the
        // one findfirst used: AH=1A can move it between calls.
        int dta = Memory.Lin(_dtaSeg, _dtaOff);
        ushort id = (ushort)(mem.Raw[dta + 0x0D] | (mem.Raw[dta + 0x0E] << 8));
        var search = _fs.GetSearch(id);
        if (search == null) { cpu.CF = true; cpu.AX = 18; return; }
        byte attr = mem.Raw[dta + 0x0C];
        var e = _fs.NextMatch(search, DtaSearchPattern(mem, dta), attr);
        if (e == null) { cpu.CF = true; cpu.AX = 18; return; }   // DOSERR_NO_MORE_FILES
        WriteFindResult(mem, dta, e);
        cpu.CF = false;
    }

    private static void WriteFindResult(Memory mem, int dta, DosFileSystem.Entry e)
    {
        // Size and time are re-read from the host: the directory listing is cached and a file the
        // game just wrote would otherwise be reported with its old length.
        long size = e.Size;
        DateTime mtime = e.Mtime;
        try
        {
            if (!e.IsDir)
            {
                var fi = new FileInfo(e.HostPath);
                if (fi.Exists) { size = fi.Length; mtime = fi.LastWriteTime; }
            }
        }
        catch { }
        byte attr = e.IsDir ? (byte)(e.Attr | DosFileSystem.AttrDirectory) : (e.Attr == 0 ? DosFileSystem.AttrArchive : e.Attr);
        WriteFindResult(mem, dta, e.ShortName, attr, size, mtime);
    }

    private static void WriteFindResult(Memory mem, int dta, string name, byte attr, long size, DateTime mtime)
    {
        ushort dosTime = DosTime(mtime);
        ushort dosDate = DosDate(mtime);
        mem.Raw[dta + 0x15] = attr;
        mem.Raw[dta + 0x16] = (byte)dosTime;
        mem.Raw[dta + 0x17] = (byte)(dosTime >> 8);
        mem.Raw[dta + 0x18] = (byte)dosDate;
        mem.Raw[dta + 0x19] = (byte)(dosDate >> 8);
        uint sz = (uint)Math.Min(size, 0xFFFFFFFFu);
        mem.Raw[dta + 0x1A] = (byte)sz;
        mem.Raw[dta + 0x1B] = (byte)(sz >> 8);
        mem.Raw[dta + 0x1C] = (byte)(sz >> 16);
        mem.Raw[dta + 0x1D] = (byte)(sz >> 24);
        // +0x1E: 8.3 name, uppercase, ASCIIZ, 13 bytes
        for (int i = 0; i < 13; i++)
            mem.Raw[dta + 0x1E + i] = i < name.Length && i < 12 ? (byte)name[i] : (byte)0;
    }

    // ---- HELPERS ----

    /// <summary>Restaura un handle del kernel del ORÁCULO al reanudar un snapshot: abre
    /// el fichero en gameDir (resolución como resolve_file del explorador: quita unidad y
    /// ruta, case-insensitive) registrándolo con EXACTAMENTE ese número de handle y hace
    /// seek a pos. El juego guarda los números de handle en su propia memoria (FILE._file),
    /// así que el número debe coincidir. Devuelve false si el fichero no se encuentra.</summary>
    public bool OpenAtHandle(int handle, string dosName, long pos)
    {
        string baseName = dosName.Replace('\\', '/').TrimEnd('\0').Trim().TrimEnd('/');
        if (baseName.Contains('/')) baseName = baseName[(baseName.LastIndexOf('/') + 1)..];
        if (baseName.Contains(':')) baseName = baseName[(baseName.LastIndexOf(':') + 1)..];
        if (baseName.Length == 0) return false;
        string path = _fs.ResolveHostPath(baseName);
        if (!File.Exists(path)) return false;
        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (pos > 0) fs.Seek(Math.Min(pos, fs.Length), SeekOrigin.Begin);
            if (_handles.Remove(handle, out var old)) old.Dispose();
            _handles[handle] = fs; // AllocHandle esquiva los ocupados: no hace falta reservar
            FileLog($"preopen h={handle} '{baseName}' pos={pos}");
            return true;
        }
        catch { return false; }
    }

    private string ResolvePath(Cpu cpu, Memory mem)
        => ResolvePathFromPtr(cpu, mem, cpu.DS, cpu.DX);

    /// <summary>Every DOS path — open, create, delete, attr, rename, mkdir, rmdir, chdir and the
    /// directory part of findfirst — goes through the SAME resolver: drive letter, current
    /// directory of that drive, "." / "..", case-insensitive host names and 8.3 short names.</summary>
    private string ResolvePathFromPtr(Cpu cpu, Memory mem, ushort seg, ushort off)
        => _fs.ResolveHostPath(ReadDosString(mem, seg, off));

    /// <summary>Drops the cached listing of the folder a path lives in, after a create/delete/
    /// rename — DOSBox-X does the same with DOS_Drive_Cache::CacheOut/DeleteEntry.</summary>
    private void InvalidateDir(string hostPath)
    {
        string? d = Directory.Exists(hostPath) ? hostPath : Path.GetDirectoryName(hostPath);
        if (d != null) _fs.Invalidate(d);
    }

    private static string ReadDosString(Memory mem, ushort seg, ushort off)
    {
        int addr = Memory.Lin(seg, off);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 260; i++)
        {
            byte b = mem.Raw[addr + i];
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private void CreateFile(Cpu cpu, Memory mem)
    {
        try
        {
            string path = ResolvePath(cpu, mem);
            var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            InvalidateDir(path);
            int h = AllocHandle();
            _handles[h] = fs;
            cpu.AX = (ushort)h;
            cpu.CF = false;
        }
        catch
        {
            cpu.CF = true; cpu.AX = 2;
        }
    }

    private void OpenFile(Cpu cpu, Memory mem)
    {
        try
        {
            string rawName = ReadDosString(mem, cpu.DS, cpu.DX);
            string path = ResolvePath(cpu, mem);
            if (PlatformDebug.Trace)
                PlatformDebug.Log($"INT21h open: DS={cpu.DS:X4} DX={cpu.DX:X4} raw='{rawName}' → {path} exists={File.Exists(path)}");
            if (!File.Exists(path))
            {
                // Fallback de auto-carga VROOMM: nombre basura -> servir el EXE propio.
                if (SelfExePath != null && File.Exists(SelfExePath) && LooksGarbage(rawName))
                {
                    path = SelfExePath;
                    if (PlatformDebug.Trace) PlatformDebug.Log($"  -> fallback self-load: {path}");
                }
                else
                {
                    cpu.CF = true; cpu.AX = 2; FileLog($"open '{rawName}' -> NOT FOUND ({path})");
                    if (LogCalls)
                        Console.Error.WriteLine($"[int21] 3D open '{rawName}' -> NOT FOUND ({path}) selfExe={(SelfExePath ?? "null")} t={Cpu.TickCount} (CS={cpu.CS:X4})");
                    return;
                }
            }
            var mode = (cpu.AL & 3) switch
            {
                0 => FileAccess.Read,
                1 => FileAccess.Write,
                _ => FileAccess.ReadWrite,
            };
            var fs = new FileStream(path, FileMode.Open, mode, FileShare.Read);
            int h = AllocHandle();
            _handles[h] = fs;
            cpu.AX = (ushort)h;
            cpu.CF = false;
            FileLog($"open '{rawName}' -> h={h}");
            if (LogCalls)
                Console.Error.WriteLine($"[int21] 3D open '{rawName}' -> h={h} t={Cpu.TickCount} (CS={cpu.CS:X4})");
        }
        catch
        {
            cpu.CF = true; cpu.AX = 2;
        }
    }

    private void CloseFile(Cpu cpu)
    {
        int h = cpu.BX;
        if (h < 5) { cpu.CF = false; return; } // handles estándar
        // Close is lenient like the reference DOS (explore_exec.py int21 AH=3E:
        // dos.handles.pop(bx, None) + CF=0): closing an untracked handle succeeds.
        // A snapshot resumes with the game's stdio referencing DOS handles that were
        // open in the oracle (e.g. 9, 15) but absent from this fresh handle table;
        // the game closes them during resource cleanup and must not see EBADF.
        if (_handles.Remove(h, out var fs))
            fs.Dispose();
        cpu.CF = false;
        FileLog($"close h={h}{(fs != null ? " OK" : " stale->ok")}");
    }

    private void ReadFile(Cpu cpu, Memory mem)
    {
        int h = cpu.BX;
        if (h < 3)
        {
            cpu.AX = 0; cpu.CF = false; return;
        }
        if (!_handles.TryGetValue(h, out var fs))
        {
            cpu.CF = true; cpu.AX = 6; FileLog($"read h={h} EBADF"); return;
        }
        int count = cpu.CX;
        // El destino en MODO PROTEGIDO no es `seg×16`: DS es un selector. LinDeSegmento resuelve
        // los dos mundos (en modo real da exactamente lo de siempre).
        uint dst = Memory.LinDeSegmento(cpu.DS, cpu.DX);
        var buf = new byte[count];
        long pos = fs.Position;   // stream position BEFORE the read, for provenance
        // DOS devuelve TODO lo pedido salvo que se acabe el fichero; `FileStream.Read` puede
        // devolver menos por su cuenta, y un programa que compara lo leído con lo pedido —el
        // extensor embebido de un juego de prueba lo hace y aborta con "Fatal error reading disk"— se lo tragaría
        // como un fichero corrupto. Se relee hasta completar o hasta el final de verdad.
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(buf, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        if (dst + read <= (uint)Memory.Size)
            mem.CopiaDeServicio((int)dst, buf, 0, read);
        else
            for (int i = 0; i < read; i++) mem.WrLin8((uint)(dst + i), buf[i]);
        BlitProvenance.Record((int)dst, read, Path.GetFileName(fs.Name), pos);
        FileLog($"read h={h} pedidos={count} leidos={read} pos={pos}");
        // Una lectura CORTA es el final del fichero, y muchos programas la tratan como error de
        // disco. Se dice siempre: es de las cosas que, calladas, parecen un fallo del juego.
        if (read < count)
        {
            Console.Error.WriteLine($"[DOS] lectura corta h={h} '{Path.GetFileName(fs.Name)}': " +
                $"pedidos {count}, leidos {read}, desde {pos} (tamano {fs.Length})");
            // DIAGNOSTICO (PAKU_LECTURA_COMPLETA=1): contesta que se leyo todo. NO es fidelidad —
            // DOS devuelve lo que hay— pero responde una pregunta que no se puede responder de
            // otra forma: si a un programa que aborta por una lectura corta le importan de verdad
            // esos bytes, o si el fichero que tenemos esta simplemente recortado al final.
            if (Environment.GetEnvironmentVariable("PAKU_LECTURA_COMPLETA") == "1") read = count;
        }
        cpu.AX = (ushort)read;
        cpu.CF = false;
    }

    private void WriteFile(Cpu cpu, Memory mem)
    {
        int h = cpu.BX;
        if (h < 3)
        {
            // stdout/stderr — same CON route as AH=02/09 (dosbox-x DOS_WriteFile(STDOUT))
            int src2 = Memory.Lin(cpu.DS, cpu.DX);
            for (int i = 0; i < cpu.CX; i++)
            {
                Console.Write((char)mem.Raw[src2 + i]);
                ConOut(mem.Raw[src2 + i]);
            }
            cpu.AX = cpu.CX; cpu.CF = false;
            return;
        }
        if (!_handles.TryGetValue(h, out var fs))
        {
            // Sink like the reference DOS (explore_exec.py int21 AH=40: always AX=CX, CF=0):
            // after a snapshot resume the game flushes stdio buffers backed by DOS handles
            // that were open in the oracle but absent here; the write must appear to succeed
            // so the C runtime does not perror+exit on a phantom I/O error.
            cpu.AX = cpu.CX; cpu.CF = false; FileLog($"write h={h} sink"); return;
        }
        int count = cpu.CX;
        int src = Memory.Lin(cpu.DS, cpu.DX);
        fs.Write(mem.Raw, src, count);
        cpu.AX = (ushort)count;
        cpu.CF = false;
    }

    private void SeekFile(Cpu cpu)
    {
        int h = cpu.BX;
        if (!_handles.TryGetValue(h, out var fs))
        {
            cpu.CF = true; cpu.AX = 6;
            if (FileTrace)
            {
                var fns = string.Join("<", new System.Diagnostics.StackTrace().ToString()
                    .Split('\n').Where(l => l.Contains("Fn_")).Take(8)
                    .Select(l => l.Trim().Replace("at PakuPort.Generated.Gen.", "").Replace("(Cpu c, Memory m)", "")));
                FileLog($"seek h={h} al={cpu.AL} off={((long)cpu.CX << 16) | cpu.DX:X} EBADF via {fns}");
            }
            return;
        }
        long offset = ((long)cpu.CX << 16) | cpu.DX;
        // Tratar como signed para soportar offsets negativos (seek desde End)
        if (cpu.CX >= 0x8000) offset = (long)(int)((uint)((cpu.CX << 16) | cpu.DX));
        var origin = cpu.AL switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin,
        };
        long pos = fs.Seek(offset, origin);
        cpu.DX = (ushort)(pos >> 16);
        cpu.AX = (ushort)pos;
        cpu.CF = false;
    }

    // DOS extended-key protocol (documented behavior of INT 21h AH=01/06/07/08/0A and the
    // Borland/Turbo C getch()/bioskey() pair built on it): a key with ascii==0 (arrows,
    // numpad-as-arrows, function keys) is NOT returned in one call. The first console read
    // returns AL=0x00; the scancode is held back and returned RAW by the very next console
    // read, without going through the keyboard queue again. Genuine games (this one included,
    // for its numpad-driven track building) rely on this exact two-call sequence to recover
    // the scancode — returning only the truncated low byte of the queued entry (as before)
    // silently drops the scancode forever, so the game never sees which direction was pressed.
    private byte? _pendingExtendedScancode;

    /// <summary>True if a console read (AH=01/06/07/08/0A) would return data right now —
    /// either the second half of a pending extended key, or a fresh key in the queue. Used
    /// by AH=0Bh (check input status) so it doesn't report "no key" while an extended key's
    /// scancode is still waiting to be read.</summary>
    private bool ConsoleInputPending()
        => _pendingExtendedScancode != null || (_platform?.Int16 is { } kb && kb.TryPeek(out _));

    private static readonly bool _int21KeyTrace = Environment.GetEnvironmentVariable("PAKU_INT21_KEY_TRACE") == "1";

    private byte ReadConsoleChar(Cpu? cpu = null, Memory? mem = null, bool blocking = false)
    {
        if (_pendingExtendedScancode is byte sc)
        {
            _pendingExtendedScancode = null;
            if (_int21KeyTrace) Console.Error.WriteLine($"[INT21-KEY] 2nd call -> AL={sc:X2} (scancode)");
            return sc;
        }
        // Leer del MISMO teclado que alimenta la ventana del host (vía INT 16h),
        // no de stdin: dentro de la ventana no hay consola. Fallback a stdin solo
        // si no hay plataforma con teclado (modo headless/tests).
        if (_platform?.Int16 is { } kb)
        {
            if (!kb.TryPop(out ushort key))
            {
                if (!blocking || cpu == null || mem == null) return 0;
                key = kb.ReadBlocking(cpu, mem);
            }
            byte ascii = (byte)key;
            if (ascii == 0) _pendingExtendedScancode = (byte)(key >> 8);
            if (_int21KeyTrace) Console.Error.WriteLine($"[INT21-KEY] 1st call -> AL={ascii:X2} scancode={(byte)(key >> 8):X2} extended={ascii == 0}");
            return ascii;
        }
        try
        {
            if (Console.KeyAvailable)
                return (byte)Console.ReadKey(intercept: true).KeyChar;
        }
        catch { }
        return 0;
    }

    // Codificación de fecha DOS: bits 15-9=año-1980, 8-5=mes, 4-0=día
    private static ushort DosDate(DateTime dt)
        => (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);

    // Codificación de hora DOS: bits 15-11=hora, 10-5=minuto, 4-0=segundo/2
    private static ushort DosTime(DateTime dt)
        => (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
}

/// <summary>
/// Cadena de Memory Control Blocks (MCBs) mínima funcional.
/// Adaptado de dos_memory.cpp (DOSBox-X): estructura MCB = 16 bytes en memoria real.
/// MCB layout: [type(1), owner(2), size(2), reserved(3), name(8)]
///   type: 'M'=0x4D mid-chain, 'Z'=0x5A end-of-chain
///   owner: 0=libre, PSP=ocupado
///   size: párrafos del bloque de datos (sin contar el MCB header)
/// La cadena comienza en MCB_START_SEG y el primer bloque libre llega hasta MCB_TOP.
/// </summary>
public sealed class McbChain
{
    // Rango de memoria convencional para el arena del juego.
    // Base por defecto 0x7000 (zona segura si el programa nunca hace SETBLOCK); cuando el
    // programa encoge su propio bloque con AH=4A (SETBLOCK del crt0), ExtendBase baja la
    // base hasta el final real del programa — como DOS de verdad, sin calibrar por juego.
    // Techo justo bajo los trampolines IVT de DosPlatform (seg 0x9F00), como el oráculo
    // (explore_exec.py MEM_TOP=0x9EFF): VROOMM dimensiona su ventana del bloque mayor.
    internal const ushort McbStartSeg = 0x7000; // base por defecto del primer MCB header
    private ushort _start = McbStartSeg;
    /// <summary>Techo del arena (excluido), POR MUNDO: 0x9F00 para boot-real (el juego
    /// necesita la convencional entera; = oráculo MEM_TOP). El host snapshot lo baja a
    /// 0x9000: la memoria alta congelada contiene datos VIVOS del mundo del oráculo
    /// (p.ej. la música en ~0x98000) y el arena no debe pisarla (medido:
    /// subir el techo global corrompía una melodía del juego → idiv/0).</summary>
    internal static ushort ArenaTopSeg = 0x9F00;
    private static ushort ArenaTop => ArenaTopSeg;

    /// <summary>Base actual del arena (movible hacia abajo por SETBLOCK del programa).</summary>
    public ushort Start => _start;

    /// <summary>Techo del arena (excluido).</summary>
    public ushort Top => ArenaTop;

    private const byte McbMid  = 0x4D; // 'M'
    private const byte McbEnd  = 0x5A; // 'Z'
    private const ushort McbFree = 0x0000; // owner=0 significa libre

    private readonly Memory _mem;
    private bool _initialized;

    public McbChain(Memory mem)
    {
        _mem = mem;
        Init();
    }

    private void Init()
    {
        if (_initialized) return;
        _initialized = true;
        // Un único MCB libre que abarca toda la arena
        ushort size = (ushort)(ArenaTop - _start - 1); // size en párrafos
        WriteMcb(_start, McbEnd, McbFree, size);
    }

    // ---- "DOS mínimo real" (mundo boot fiel) ----
    // Mapa convencional MEDIDO contra el oráculo (DOSBox-X, conf canónica ems/xms/umb=true,
    // volcado MCB 2026-07-15 con un juego de prueba en su menú principal):
    //   0450 M owner=0008 size=02AF   área privada del kernel DOS (buffers/SFT; contenido no modelado)
    //   0700 M owner=072F size=002D   entorno de COMMAND (strings de la shell en 0701:0)
    //   072E M owner=072F size=009A   COMMAND.COM residente
    //   07C9 Z libre hasta 9FFF       pool donde DOS_Execute carga el programa raíz (first-fit)
    // Con este prólogo + la semántica fiel de EXEC/entorno, un raíz COM cae en PSP 0x0814
    // exactamente como en el DOS real.
    public const ushort RealDosFirstMcbSeg = 0x0450;
    public const ushort RealDosShellEnvSeg = 0x0701;
    public const ushort RealDosShellPspSeg = 0x072F;
    public const ushort RealDosTopSeg      = 0x9FFF;
    /// <summary>Strings del entorno de la shell, byte-exactas al oráculo (terminan en \0\0).</summary>
    internal static readonly byte[] RealDosShellEnv = System.Text.Encoding.ASCII.GetBytes(
        "PATH=Z:\\;Z:\\SYSTEM;Z:\\BIN;Z:\\DOS;Z:\\4DOS;Z:\\DEBUG;Z:\\TEXTUTIL;Z:\\PATCHING\0" +
        "COMSPEC=Z:\\COMMAND.COM\0PROMPT=$P$G\0BLASTER=A220 I7 D1 H5 P330 T6\0\0");

    /// <summary>Construye el mapa convencional del DOS real (prólogo de arriba) y deja el
    /// arena listo para que el programa raíz se cargue por first-fit como en DOS_Execute.</summary>
    public void InitRealDosBootMap()
    {
        // El ctor ya escribió el MCB del arena por defecto en McbStartSeg; limpiarlo (queda
        // dentro del pool libre del mapa real).
        Array.Clear(_mem.Raw, Memory.Lin(_start, 0), 16);
        _initialized = true;
        _start = RealDosFirstMcbSeg;
        ArenaTopSeg = RealDosTopSeg;
        WriteMcb(0x0450, McbMid, 0x0008, 0x02AF);
        WriteMcb(0x0700, McbMid, RealDosShellPspSeg, 0x002D, "COMMAND");
        _mem.CopiaDeServicio(Memory.Lin(RealDosShellEnvSeg, 0), RealDosShellEnv, 0, RealDosShellEnv.Length);
        WriteMcb(0x072E, McbMid, RealDosShellPspSeg, 0x009A, "COMMAND");
        WriteMcb(0x07C9, McbEnd, McbFree, (ushort)(RealDosTopSeg - 0x07C9 - 1));
    }

    /// <summary>Nombre de programa en el MCB (+8, 8 bytes) como escribe DOS al hacer EXEC.</summary>
    public void SetBlockName(ushort seg, string name)
    {
        ushort mcbSeg = (ushort)(seg - 1);
        byte type = _mem.Raw[Memory.Lin(mcbSeg, 0)];
        if (type != McbMid && type != McbEnd) return;
        int lin = Memory.Lin(mcbSeg, 8);
        for (int i = 0; i < 8; i++)
            _mem.Raw[lin + i] = (byte)(i < name.Length ? name[i] : 0);
    }

    /// <summary>Estado "programa recién cargado que posee toda la memoria convencional"
    /// (cero libre): un único MCB de fin de cadena (Z) en pspSeg-1, propietario=pspSeg, que
    /// abarca [pspSeg, topSeg). Espejo de DOSBox-X al cargar un COM/EXE. Un SETBLOCK posterior
    /// (Resize/AH=4A) encoge el bloque y libera la cola al pool, como el crt0 real.</summary>
    public void InitLoadedProgram(ushort pspSeg, ushort topSeg)
    {
        _initialized = true;
        _start = (ushort)(pspSeg - 1);
        ArenaTopSeg = topSeg;
        WriteMcb((ushort)(pspSeg - 1), McbEnd, pspSeg, (ushort)(topSeg - pspSeg));
    }

    /// <summary>Baja la base del arena a <paramref name="newStart"/> (SETBLOCK del programa:
    /// todo lo que hay por encima de su bloque encogido pasa a ser pool libre de DOS).</summary>
    public void ExtendBase(ushort newStart)
    {
        Init();
        if (newStart >= _start || newStart <= 0x100) return;
        // MCB libre que cubre [newStart+1, _start); el MCB antiguo en _start sigue la cadena
        WriteMcb(newStart, McbMid, McbFree, (ushort)(_start - newStart - 1));
        _start = newStart;
        Compress();
    }

    /// <summary>Intenta asignar <paramref name="paras"/> párrafos. Devuelve el segmento de datos (MCB+1).
    /// <paramref name="owner"/> = PSP dueño (DOS lo usa para liberar TODO lo del proceso al terminar).</summary>
    public bool Alloc(ushort paras, out ushort segment, out ushort largest, ushort owner = 0x0001)
    {
        Init();
        Compress();   // ver Compress(): DOS fusiona los libres ANTES de servir una petición
        largest = 0;
        ushort mcbSeg = _start;
        for (int guard = 0; guard < 10000; guard++)
        {
            byte type  = _mem.Raw[Memory.Lin((ushort)mcbSeg, 0)];
            ushort blockOwner = ReadU16(mcbSeg, 1);
            ushort size  = ReadU16(mcbSeg, 3);

            if (blockOwner == McbFree)
            {
                if (size > largest) largest = size;
                if (size >= paras)
                {
                    // Dividir si hay espacio sobrante
                    if (size > paras + 1)
                    {
                        ushort newMcb = (ushort)(mcbSeg + paras + 1);
                        byte newType = type; // heredar si Z
                        WriteMcb(newMcb, newType, McbFree, (ushort)(size - paras - 1));
                        // El bloque actual pierde el estado Z
                        WriteMcb(mcbSeg, McbMid, owner, paras);
                    }
                    else
                    {
                        WriteMcb(mcbSeg, type, owner, size);
                    }
                    // Limpiar datos del bloque asignado
                    segment = (ushort)(mcbSeg + 1);
                    return true;
                }
            }

            if (type == McbEnd) break;
            mcbSeg = (ushort)(mcbSeg + size + 1);
        }
        segment = 0;
        return false;
    }

    /// <summary>Libera TODOS los bloques del dueño <paramref name="owner"/> (DOS_Terminate /
    /// FreeProcessMemory: al morir un proceso se libera todo lo que su PSP posea — sin esto,
    /// cada hijo EXEC que reserva con AH=48 fuga memoria al terminar).</summary>
    public void FreeOwnedBy(ushort owner)
    {
        Init();
        ushort mcbSeg = _start;
        for (int guard = 0; guard < 10000; guard++)
        {
            byte type = _mem.Raw[Memory.Lin(mcbSeg, 0)];
            ushort blockOwner = ReadU16(mcbSeg, 1);
            ushort size = ReadU16(mcbSeg, 3);
            if (blockOwner == owner) WriteU16(mcbSeg, 1, McbFree);
            if (type == McbEnd) break;
            mcbSeg = (ushort)(mcbSeg + size + 1);
        }
        Compress();
    }

    /// <summary>Cambia el dueño del bloque cuyo segmento de datos es <paramref name="seg"/>.</summary>
    public void SetOwner(ushort seg, ushort owner)
    {
        ushort mcbSeg = (ushort)(seg - 1);
        byte type = _mem.Raw[Memory.Lin(mcbSeg, 0)];
        if (type == McbMid || type == McbEnd) WriteU16(mcbSeg, 1, owner);
    }

    /// <summary>Libera el bloque cuyo segmento de datos es <paramref name="seg"/>.</summary>
    public bool Free(ushort seg)
    {
        Init();
        ushort mcbSeg = (ushort)(seg - 1);
        // Validar que es un MCB conocido
        byte type = _mem.Raw[Memory.Lin(mcbSeg, 0)];
        if (type != McbMid && type != McbEnd) return false;
        WriteU16(mcbSeg, 1, McbFree); // owner = 0
        Compress(); // fusionar bloques libres adyacentes
        return true;
    }

    /// <summary>Redimensiona el bloque en <paramref name="seg"/> a <paramref name="newSize"/> párrafos.</summary>
    public bool Resize(ushort seg, ushort newSize, out ushort largest)
    {
        Init();
        Compress();   // ver Compress(): sin fusionar libres adyacentes, crecer falla con memoria de sobra
        largest = 0;
        ushort mcbSeg = (ushort)(seg - 1);
        byte type = _mem.Raw[Memory.Lin(mcbSeg, 0)];
        if (type != McbMid && type != McbEnd) return false;
        ushort curSize = ReadU16(mcbSeg, 3);

        if (newSize <= curSize)
        {
            // Encoger: liberar el resto
            if (newSize < curSize)
            {
                ushort freeSeg = (ushort)(mcbSeg + newSize + 1);
                byte freeType = type;
                WriteMcb(freeSeg, freeType, McbFree, (ushort)(curSize - newSize - 1));
                WriteMcb(mcbSeg, McbMid, ReadU16(mcbSeg, 1), newSize);
            }
            largest = newSize;
            return true;
        }
        else
        {
            // Crecer: verificar que el siguiente MCB es libre y suficiente
            if (type == McbEnd) { largest = curSize; return false; }
            ushort nextSeg  = (ushort)(mcbSeg + curSize + 1);
            byte nextType   = _mem.Raw[Memory.Lin(nextSeg, 0)];
            ushort nextOwner = ReadU16(nextSeg, 1);
            ushort nextSize  = ReadU16(nextSeg, 3);
            if (nextOwner == McbFree && (curSize + 1 + nextSize) >= newSize)
            {
                ushort totalFree = (ushort)(curSize + 1 + nextSize);
                byte newType = (nextType == McbEnd) ? McbEnd : McbMid;
                if (totalFree > newSize)
                {
                    // Dejar un MCB libre con el resto
                    ushort remainSeg = (ushort)(mcbSeg + newSize + 1);
                    WriteMcb(remainSeg, nextType, McbFree, (ushort)(totalFree - newSize - 1));
                    WriteMcb(mcbSeg, McbMid, ReadU16(mcbSeg, 1), newSize);
                }
                else
                {
                    WriteMcb(mcbSeg, newType, ReadU16(mcbSeg, 1), newSize);
                }
                largest = newSize;
                return true;
            }
            // Calcular el mayor bloque libre disponible
            largest = GetLargest();
            return false;
        }
    }

    /// <summary>DEBUG: walk the whole MCB chain and dump it to stderr (segment/type/owner/size,
    /// running total). Used to diagnose memory-map divergences at an alloc failure.</summary>
    public void DumpChain(string tag)
    {
        Console.Error.WriteLine($"[mcb] === chain dump ({tag}) start=0x{_start:X4} top=0x{ArenaTop:X4} ===");
        ushort mcbSeg = _start;
        int totalFree = 0, totalUsed = 0;
        for (int g = 0; g < 10000; g++)
        {
            byte type = _mem.Raw[Memory.Lin(mcbSeg, 0)];
            ushort owner = ReadU16(mcbSeg, 1);
            ushort size = ReadU16(mcbSeg, 3);
            string kind = owner == McbFree ? "FREE" : "USED";
            if (owner == McbFree) totalFree += size; else totalUsed += size;
            Console.Error.WriteLine($"[mcb]  seg=0x{mcbSeg:X4} type={(char)type} owner=0x{owner:X4} " +
                $"size=0x{size:X4} ({size * 16} B) {kind} data=[0x{mcbSeg + 1:X4}..0x{mcbSeg + 1 + size:X4})");
            if (type != McbMid && type != McbEnd) { Console.Error.WriteLine("[mcb]  !! broken chain (bad type)"); break; }
            if (type == McbEnd) break;
            mcbSeg = (ushort)(mcbSeg + size + 1);
        }
        Console.Error.WriteLine($"[mcb] === total FREE=0x{totalFree:X4} ({totalFree * 16} B) USED=0x{totalUsed:X4} ({totalUsed * 16} B) largest_free=0x{GetLargest():X4} ===");
    }

    private ushort GetLargest()
    {
        ushort best = 0;
        ushort mcbSeg = _start;
        for (int g = 0; g < 10000; g++)
        {
            byte type  = _mem.Raw[Memory.Lin(mcbSeg, 0)];
            ushort owner = ReadU16(mcbSeg, 1);
            ushort size  = ReadU16(mcbSeg, 3);
            if (owner == McbFree && size > best) best = size;
            if (type == McbEnd) break;
            mcbSeg = (ushort)(mcbSeg + size + 1);
        }
        return best;
    }

    /// <summary>Fusiona bloques libres contiguos (DOS_CompressMemory).</summary>
    /// <summary>Fusiona los bloques libres ADYACENTES, como DOS_CompressMemory de
    /// dos_memory.cpp. DOS lo ejecuta antes de servir CUALQUIER petición de memoria, no sólo al
    /// liberar: encoger y crecer el mismo bloque repetidamente va troceando la cola de la cadena
    /// en libres contiguos que nadie vuelve a unir, y entonces un SETBLOCK falla teniendo
    /// memoria de sobra, porque sólo se mira el MCB inmediatamente siguiente.
    ///
    /// Aquí estaba implementado desde siempre pero sólo se llamaba desde Free y ExtendBase, así
    /// que Alloc y Resize decidían sobre una cadena fragmentada. Es lo que mataba a un juego
    /// de prueba que crece su bloque decenas de veces mientras carga sus datos y acababa pidiendo
    /// 704 párrafos más con esta cadena delante:
    ///   0x8A94 FREE 0x003F (1 KB)  ·  0x8AD4 FREE 0x152A (86 KB)
    /// El primero no llegaba, el segundo ni se miraba, y el juego abortaba con "Insufficient
    /// memory error!" teniendo 87 KB libres pegados a su propio bloque.</summary>
    private void Compress()
    {
        ushort mcbSeg = _start;
        for (int g = 0; g < 10000; g++)
        {
            byte type  = _mem.Raw[Memory.Lin(mcbSeg, 0)];
            ushort owner = ReadU16(mcbSeg, 1);
            ushort size  = ReadU16(mcbSeg, 3);
            if (type == McbEnd) break;
            ushort nextSeg   = (ushort)(mcbSeg + size + 1);
            byte nextType    = _mem.Raw[Memory.Lin(nextSeg, 0)];
            ushort nextOwner = ReadU16(nextSeg, 1);
            ushort nextSize  = ReadU16(nextSeg, 3);
            if (owner == McbFree && nextOwner == McbFree)
            {
                // Fusionar
                WriteMcb(mcbSeg, nextType, McbFree, (ushort)(size + 1 + nextSize));
                // No avanzar, intentar fusionar otra vez desde aquí
            }
            else
            {
                mcbSeg = nextSeg;
            }
        }
    }

    private void WriteMcb(ushort seg, byte type, ushort owner, ushort size, string? name = null)
    {
        int lin = Memory.Lin(seg, 0);
        _mem.Raw[lin + 0] = type;
        _mem.Raw[lin + 1] = (byte)owner;
        _mem.Raw[lin + 2] = (byte)(owner >> 8);
        _mem.Raw[lin + 3] = (byte)size;
        _mem.Raw[lin + 4] = (byte)(size >> 8);
        if (name != null)
            for (int i = 0; i < 8; i++)
                _mem.Raw[lin + 8 + i] = (byte)(i < name.Length ? name[i] : 0);
    }

    private ushort ReadU16(ushort seg, int off)
    {
        int lin = Memory.Lin(seg, 0);
        return (ushort)(_mem.Raw[lin + off] | (_mem.Raw[lin + off + 1] << 8));
    }

    private void WriteU16(ushort seg, int off, ushort val)
    {
        int lin = Memory.Lin(seg, 0);
        _mem.Raw[lin + off] = (byte)val;
        _mem.Raw[lin + off + 1] = (byte)(val >> 8);
    }
}
