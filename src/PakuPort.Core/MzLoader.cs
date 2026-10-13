using System.Buffers.Binary;

namespace PakuPort.Core;

/// <summary>
/// Loader MZ/EXE genérico para DOS real-mode. Parsea el header MZ, aplica relocaciones de
/// segmento y monta la imagen en un Memory de 1 MiB. Genérico: funciona con cualquier EXE
/// DOS 16-bit sin hardcodear nada del juego. El entry point (CS:IP) y SS:SP salen del header.
/// </summary>
public sealed class MzLoader
{
    public ushort LoadSegment { get; private set; }
    public ushort InitCS { get; private set; }
    public ushort InitIP { get; private set; }
    public ushort InitSS { get; private set; }
    public ushort InitSP { get; private set; }
    public ushort MinAlloc { get; private set; }
    public ushort MaxAlloc { get; private set; }
    public int ImageSize { get; private set; }
    public int HeaderSize { get; private set; }
    public int RelocCount { get; private set; }

    /// <summary>
    /// Carga un EXE DOS en Memory. Devuelve el loader con los metadatos del header.
    /// <paramref name="loadSegment"/> es el segmento donde DOS cargaría el programa
    /// (típicamente justo después del PSP: pspSeg + 0x10).
    /// </summary>
    public static MzLoader Load(byte[] exeBytes, Memory mem, ushort loadSegment = 0x0100)
    {
        if (exeBytes.Length < 28 || exeBytes[0] != 'M' || exeBytes[1] != 'Z')
            throw new InvalidOperationException("no es un ejecutable MZ válido");

        var loader = new MzLoader { LoadSegment = loadSegment };

        int lastPageBytes = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(2));
        int totalPages = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(4));
        loader.RelocCount = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(6));
        loader.HeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(8)) * 16;
        loader.MinAlloc = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(10));
        loader.MaxAlloc = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(12));
        loader.InitSS = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(14));
        loader.InitSP = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(16));
        // skip checksum at 18
        loader.InitIP = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(20));
        loader.InitCS = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(22));
        int relocTableOffset = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(24));

        int imageFileSize = totalPages * 512;
        if (lastPageBytes > 0) imageFileSize += lastPageBytes - 512;
        loader.ImageSize = imageFileSize - loader.HeaderSize;

        // Copiar imagen del programa a Memory en loadSegment
        int loadBase = loadSegment * 16;
        int copyLen = Math.Min(loader.ImageSize, exeBytes.Length - loader.HeaderSize);
        copyLen = Math.Min(copyLen, Memory.Size - loadBase);
        Array.Copy(exeBytes, loader.HeaderSize, mem.Raw, loadBase, copyLen);

        // Aplicar relocaciones de segmento
        for (int i = 0; i < loader.RelocCount; i++)
        {
            int rOff = relocTableOffset + i * 4;
            if (rOff + 3 >= exeBytes.Length) break;
            ushort off = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(rOff));
            ushort seg = BinaryPrimitives.ReadUInt16LittleEndian(exeBytes.AsSpan(rOff + 2));
            int fixAddr = loadBase + seg * 16 + off;
            if (fixAddr + 1 < Memory.Size)
            {
                ushort val = (ushort)(mem.Raw[fixAddr] | (mem.Raw[fixAddr + 1] << 8));
                val += loadSegment;
                mem.Raw[fixAddr] = (byte)val;
                mem.Raw[fixAddr + 1] = (byte)(val >> 8);
            }
        }

        return loader;
    }

    /// <summary>
    /// Carga un fichero COM en Memory. Un COM es un binario plano sin header que se carga
    /// en segment:0100h. CS=DS=ES=SS=segment, IP=0100, SP=FFFE.
    /// </summary>
    public static MzLoader LoadCom(byte[] comBytes, Memory mem, ushort loadSegment = 0x0100)
    {
        int loadBase = loadSegment * 16 + 0x100;
        int copyLen = Math.Min(comBytes.Length, Memory.Size - loadBase);
        Array.Copy(comBytes, 0, mem.Raw, loadBase, copyLen);

        return new MzLoader
        {
            LoadSegment = loadSegment,
            InitCS = 0,
            InitIP = 0x0100,
            InitSS = 0,
            InitSP = 0xFFFE,
            ImageSize = comBytes.Length,
            HeaderSize = 0,
            RelocCount = 0,
            MinAlloc = 0,
            MaxAlloc = 0,
        };
    }

    /// <summary>
    /// Prepara un PSP mínimo en pspSegment (256 bytes antes del programa).
    /// El PSP es estándar DOS: INT 20h en offset 0, tamaño de memoria en offset 2, etc.
    /// </summary>
    public static void SetupPsp(Memory mem, ushort pspSegment, ushort envSegment, ushort memTopSeg = 0x9FFF,
                                string cmdTail = "")
    {
        int psp = pspSegment * 16;
        // INT 20h (terminate) al inicio del PSP
        mem.Raw[psp + 0] = 0xCD; mem.Raw[psp + 1] = 0x20;
        // Memory size at offset 2: primer segmento más allá del bloque del proceso
        // (SetupPSP de dos_execute.cpp escribe pspseg+memsize; 0x9FFF = legado "todo").
        mem.Raw[psp + 2] = (byte)memTopSeg; mem.Raw[psp + 3] = (byte)(memTopSeg >> 8);
        // Environment segment at offset 0x2C
        mem.Raw[psp + 0x2C] = (byte)envSegment;
        mem.Raw[psp + 0x2D] = (byte)(envSegment >> 8);
        // Command tail at offset 0x80: length + text + CR, exactly as COMMAND.COM builds it
        // (a leading space before the first switch, like "GAME V S ..." produces).
        string tail = string.IsNullOrEmpty(cmdTail) ? "" : " " + cmdTail.TrimStart();
        if (tail.Length > 126) tail = tail[..126];
        mem.Raw[psp + 0x80] = (byte)tail.Length;
        for (int i = 0; i < tail.Length; i++) mem.Raw[psp + 0x81 + i] = (byte)tail[i];
        mem.Raw[psp + 0x81 + tail.Length] = 0x0D;
    }

    /// <summary>
    /// Prepara un bloque de entorno mínimo (COMSPEC + path del programa).
    /// </summary>
    public static ushort SetupEnvironment(Memory mem, ushort envSegment, string programPath)
    {
        int env = envSegment * 16;
        string exe = System.IO.Path.GetFileName(programPath).ToUpperInvariant();
        // VROOMM (DOS 2.x self-loader) reconstruye su ruta como unidad + PRIMERA cadena
        // del entorno; con un env normal (COMSPEC primero) construye una ruta basura y
        // se cuelga reintentando abrir su propio EXE. Poner la ruta self del programa
        // como primera cadena (driveless) + la cola DOS 3+ (doble cero, word, ruta
        // completa) para que ambos estilos de búsqueda resuelvan. Verificado en el
        // explorador (tools/explore_exec.build_env). Genérico: basename del exe.
        int i = 0;
        void Put(string s) { foreach (char c in s) mem.Raw[env + i++] = (byte)c; mem.Raw[env + i++] = 0; }
        Put("\\" + exe);                 // primera cadena = ruta self (driveless)
        mem.Raw[env + i++] = 0;          // doble cero = fin del bloque de entorno
        mem.Raw[env + i++] = 1; mem.Raw[env + i++] = 0;   // word: count=1
        Put("C:\\" + exe);               // ruta completa (DOS 3+)
        return envSegment;
    }

    /// <summary>CS:IP absolutos (sumados al load segment).</summary>
    public ushort AbsCS => (ushort)(LoadSegment + InitCS);
    public ushort AbsIP => InitIP;
    public ushort AbsSS => (ushort)(LoadSegment + InitSS);
}
