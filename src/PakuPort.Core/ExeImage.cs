namespace PakuPort.Core;

/// <summary>
/// Loader-lite: expone los bytes de CÓDIGO de una función residente del
/// .EXE por su <c>ghidra_offset</c>, usando la fórmula calibrada sobre un juego de prueba
/// <c>residentFileOffset = 0x200 + ghidra_offset</c>. NO es un loader MZ completo
/// (relocaciones/PSP son Fase 3/5): para una función-hoja de lógica pura basta con sus bytes, y el
/// DGROUP de entrada lo aporta el caso .pfc del corpus.
///
/// Validado contra el oráculo: los bytes en <c>0x200+0x7c12</c> coinciden byte-a-byte con el
/// prólogo de FUN_1000_7c12 del volcado de DOSBox (código residente sin SMC ni relocación interna).
/// </summary>
public sealed class ExeImage
{
    /// <summary>Desplazamiento del segmento residente dentro del fichero (MZ header de 0x200).</summary>
    public const int ResidentBase = 0x200;

    private readonly byte[] _bytes;

    /// <summary>CodeMap estático de radare2 (offsets de fichero que son instrucciones válidas).
    /// Si está, el decoder lo usa de whitelist para no decodificar datos como código.</summary>
    public HashSet<int>? CodeMap { get; set; }

    public ushort LoadSegment { get; set; }

    /// <summary>Sesgo del mundo de memoria (xOfGhidra0) cuando la imagen es híbrida
    /// (residente copiado de memimage/snapshot): las resoluciones estáticas seg:off →
    /// key deben restarlo (linear = loadSeg*16 + bias + ghidra). 0 en la imagen normal.</summary>
    public int ImageBias { get; set; }

    public ExeImage(byte[] exeBytes) => _bytes = exeBytes;

    /// <summary>Regiones de driver EXEC pegadas en la imagen COMPUESTA (0x40000+). Cada una:
    /// (segmento real del MZ relocado, keyBase = offset de fichero donde se pegó, Len = tamaño del
    /// body). Permite resolver ESTÁTICAMENTE una `call far seg:off` interna del driver (opcode 9A
    /// con el segmento ya relocado) a su key en el composite, para que el discover siga esas
    /// internas y las emita como Fn_. Null en la imagen base (no compuesta).</summary>
    public List<(ushort Seg, int KeyBase, int Len)>? DriverRegions { get; set; }

    /// <summary>Resuelve un `call far sel:off` cuyo <paramref name="sel"/> cae dentro de una región
    /// de driver a su key en el composite: key = KeyBase + (sel-Seg)*16 + off. Cubre el segundo
    /// segmento de un driver de dos segmentos (p.ej. 0x4A7A = 0x4956+0x124) de forma natural. false si no hay región o el
    /// desplazamiento resultante se sale del body.</summary>
    public bool TryResolveDriverFar(ushort sel, ushort off, out int key)
    {
        key = -1;
        if (DriverRegions == null) return false;
        foreach (var (seg, keyBase, len) in DriverRegions)
        {
            int segSpan = (len + 15) / 16;
            if (sel >= seg && sel < seg + segSpan)
            {
                int rel = (sel - seg) * 16 + off;
                if (rel >= 0 && rel < len) { key = keyBase + rel; return true; }
                return false;
            }
        }
        return false;
    }

    public int Length => _bytes.Length;

    /// <summary>Acceso directo al buffer (firmas de overlays para WindowDispatch).</summary>
    public byte[] Bytes => _bytes;

    /// <summary>Byte en un offset de fichero (0 si fuera de rango). Para distinguir thunk vs función real.</summary>
    public byte ByteAt(int fileOffset) => (uint)fileOffset < (uint)_bytes.Length ? _bytes[fileOffset] : (byte)0;

    /// <summary>Lee un u16 little-endian en un offset de FICHERO (para tablas de salto en código).</summary>
    public ushort RdU16File(int fileOffset) => (ushort)(_bytes[fileOffset] | (_bytes[fileOffset + 1] << 8));

    public static ExeImage Load(string exePath, ushort loadSegment = 0)
    {
        var raw = File.ReadAllBytes(exePath);
        if (loadSegment != 0)
        {
            var normalized = NormalizeMzImage(raw, loadSegment);
            var img = new ExeImage(normalized);
            img.LoadSegment = loadSegment;
            return img;
        }
        return new ExeImage(raw);
    }

    /// <summary>
    /// Normaliza bytes MZ crudos para que el código empiece siempre en offset ResidentBase (0x200).
    /// Si el header MZ es mayor que 0x200 (ej. ficheros con muchas relocaciones), se aplican las
    /// relocaciones in-place y se reempaqueta el código con padding de 0x200 al inicio.
    /// Devuelve los bytes normalizados listos para new ExeImage(result).
    /// </summary>
    public static byte[] NormalizeMzImage(byte[] rawExe, ushort loadSegment)
    {
        if (rawExe.Length < 28 || rawExe[0] != (byte)'M' || rawExe[1] != (byte)'Z')
            return rawExe;

        // Apply relocations on raw bytes (needs the full MZ header with reloc table)
        var tmp = new ExeImage(rawExe);
        tmp.ApplyMzRelocations(loadSegment);

        int headerSize = (rawExe[8] | (rawExe[9] << 8)) * 16;
        if (headerSize == ResidentBase)
            return rawExe; // already normalized (relocs applied in-place)

        // Strip header, prepend ResidentBase padding so code starts at 0x200
        int codeSize = rawExe.Length - headerSize;
        var normalized = new byte[ResidentBase + codeSize];
        Array.Copy(rawExe, headerSize, normalized, ResidentBase, codeSize);
        return normalized;
    }

    /// <summary>Offset físico en el fichero del código de una función por su ghidra_offset.</summary>
    public static int ResidentFileOffset(int ghidraOffset) => ResidentBase + ghidraOffset;

    /// <summary>
    /// Devuelve hasta <paramref name="maxLen"/> bytes de código a partir de la función indicada.
    /// El decoder se detiene en el <c>ret</c>; este corte solo acota la ventana de lectura.
    /// </summary>
    public byte[] GetFunctionBytes(int ghidraOffset, int maxLen = 0x4000)
        => GetBytesAtFile(ResidentFileOffset(ghidraOffset), maxLen);

    /// <summary>Bytes a partir de un offset de FICHERO. Ventana amplia (16 KB): el descenso recursivo
    /// solo decodifica lo alcanzable, así que de más no decodifica basura, pero cubre funciones grandes.</summary>
    public byte[] GetBytesAtFile(int fileOffset, int maxLen = 0x4000)
    {
        if (fileOffset < 0 || fileOffset >= _bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(fileOffset),
                $"offset 0x{fileOffset:X} fuera del fichero (len 0x{_bytes.Length:X})");
        int len = Math.Min(maxLen, _bytes.Length - fileOffset);
        return _bytes[fileOffset..(fileOffset + len)];
    }

    /// <summary>Bytes de una función por su CLAVE (= offset de fichero de la entrada).</summary>
    public byte[] GetBytesForKey(int key, int maxLen = 0x4000) => GetBytesAtFile(key, maxLen);

    /// <summary>
    /// Aplica relocaciones MZ in-place: lee la tabla de relocs del header y suma loadSegment
    /// a cada word apuntado. Después de esto, iced-x86 decodifica los segmentos correctos
    /// en call far / jmp far directos (en vez de 0000:offset).
    /// </summary>
    public void ApplyMzRelocations(ushort loadSegment)
    {
        LoadSegment = loadSegment;
        if (_bytes.Length < 28 || _bytes[0] != (byte)'M' || _bytes[1] != (byte)'Z') return;
        int relocCount = _bytes[6] | (_bytes[7] << 8);
        int headerSize = (_bytes[8] | (_bytes[9] << 8)) * 16;
        int relocTableOffset = _bytes[24] | (_bytes[25] << 8);
        for (int i = 0; i < relocCount; i++)
        {
            int rOff = relocTableOffset + i * 4;
            if (rOff + 3 >= _bytes.Length) break;
            ushort off = (ushort)(_bytes[rOff] | (_bytes[rOff + 1] << 8));
            ushort seg = (ushort)(_bytes[rOff + 2] | (_bytes[rOff + 3] << 8));
            int fixAddr = headerSize + seg * 16 + off;
            if (fixAddr + 1 < _bytes.Length)
            {
                ushort val = (ushort)(_bytes[fixAddr] | (_bytes[fixAddr + 1] << 8));
                val += loadSegment;
                _bytes[fixAddr] = (byte)val;
                _bytes[fixAddr + 1] = (byte)(val >> 8);
            }
        }
    }

    /// <summary>
    /// Scan the MZ relocation table for far pointers to the code segment (seg == loadSeg after
    /// relocation). For each such pointer, read the offset word (at fixAddr-2) and return
    /// ResidentBase + offset as a potential function entry. This catches function pointers in
    /// data tables that are only called via indirect far calls.
    /// </summary>
    public HashSet<int> GetFarPointerTargets()
    {
        var targets = new HashSet<int>();
        if (LoadSegment == 0 || _bytes.Length < 28 || _bytes[0] != (byte)'M' || _bytes[1] != (byte)'Z')
            return targets;
        int relocCount = _bytes[6] | (_bytes[7] << 8);
        int headerSize = (_bytes[8] | (_bytes[9] << 8)) * 16;
        int relocTableOffset = _bytes[24] | (_bytes[25] << 8);
        for (int i = 0; i < relocCount; i++)
        {
            int rOff = relocTableOffset + i * 4;
            if (rOff + 3 >= _bytes.Length) break;
            ushort off = (ushort)(_bytes[rOff] | (_bytes[rOff + 1] << 8));
            ushort seg = (ushort)(_bytes[rOff + 2] | (_bytes[rOff + 3] << 8));
            int fixAddr = headerSize + seg * 16 + off;
            if (fixAddr + 1 >= _bytes.Length || fixAddr < 2) continue;
            ushort patchedSeg = (ushort)(_bytes[fixAddr] | (_bytes[fixAddr + 1] << 8));
            if (patchedSeg != LoadSegment) continue;
            // Skip direct call far / jmp far instructions (already covered by 9A scan)
            if (fixAddr >= 3)
            {
                byte prefix = _bytes[fixAddr - 3];
                if (prefix == 0x9A || prefix == 0xEA) continue;
            }
            ushort fnOff = (ushort)(_bytes[fixAddr - 2] | (_bytes[fixAddr - 1] << 8));
            int target = ResidentBase + fnOff;
            if (target >= ResidentBase && target < _bytes.Length)
                targets.Add(target);
        }
        return targets;
    }
}
