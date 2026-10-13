namespace PakuPort.Core;

/// <summary>
/// Directorio de overlays VROOMM (Borland), resuelto ESTÁTICAMENTE — sin emulador.
/// Un `INT 3F` es un thunk de 5 bytes
/// <c>CD 3F &lt;ovno&gt; &lt;offset&gt;</c>: el destino = <c>overlayCodeStart[ovno] + offset</c>. Los
/// overlays comparten el DS del residente (misma memoria plana).
///
/// CLAVE DE FUNCIÓN unificada = offset de FICHERO de la entrada. base(módulo) = 0x200 (residente) u
/// <c>overlayCodeStart[ovno]</c> (overlay). El offset relativo al segmento (16-bit) = clave − base.
/// El residente vive en file 0x200..0x15745; los overlays en 0x15A00+, así que el umbral los separa.
/// </summary>
public sealed class OverlayDirectory
{
    public const int ResidentThreshold = 0x15800;
    public const int ResidentBase = 0x200;

    /// <summary>Key base del pseudo-overlay "boot": la zona [loadSeg*16, +ImageBias) del
    /// mundo snapshot/memimage (código del bootstrap VROOMM/EXEPACK creado en memoria,
    /// ANTES del inicio de la imagen — p.ej. el file_exists de la auto-carga en 1000:0002).
    /// FarDispatch resuelve seg==loadSeg &amp;&amp; off&lt;ImageBias → BootKeyBase+off.</summary>
    public const int BootKeyBase = 0x78000;

    private readonly Dictionary<int, int> _codeStart = new();   // ovno -> file code start
    private readonly Dictionary<int, int> _codeSize = new();   // ovno -> code size in bytes
    private List<int> _starts = new();                          // bases de overlay ordenadas

    public void Add(int ovno, int fileStart, int codeSize = 0)
    {
        _codeStart[ovno] = fileStart;
        if (codeSize > 0) _codeSize[ovno] = codeSize;
        _starts = _codeStart.Values.OrderBy(x => x).ToList();
    }

    /// <summary>Clave (offset de fichero) del overlay destino de un INT 3F, o -1 si ovno desconocido.</summary>
    public int Resolve(int ovno, int offset) => _codeStart.TryGetValue(ovno, out var f) ? f + offset : -1;

    public static bool IsResident(int key) => key < ResidentThreshold;

    /// <summary>base del módulo (file) que contiene la clave: 0x200 si residente, si no el
    /// overlayCodeStart mayor ≤ clave.</summary>
    public int BaseOf(int key)
    {
        if (IsResident(key)) return ResidentBase;
        int best = ResidentBase;
        foreach (var s in _starts) { if (s <= key) best = s; else break; }
        return best;
    }

    public int Count => _codeStart.Count;
    public IEnumerable<int> OverlayNumbers() => _codeStart.Keys;
    public int CodeStartOf(int ovno) => _codeStart.TryGetValue(ovno, out var s) ? s : -1;

    /// <summary>Enumera (ovno, start, size) — para la tabla de firmas de WindowDispatch.</summary>
    public IEnumerable<(int Ovno, int Start, int Size)> Entries =>
        _codeStart.Select(kv => (kv.Key, kv.Value, _codeSize.TryGetValue(kv.Key, out var s) ? s : 0));

    /// <summary>Tamaño del código para el módulo cuya base (file) coincide; 0 si desconocido.</summary>
    public int CodeSizeOf(int moduleBase)
    {
        if (moduleBase == ResidentBase) return 0;
        foreach (var (ovno, start) in _codeStart)
            if (start == moduleBase)
                return _codeSize.TryGetValue(ovno, out var sz) ? sz : 0;
        return 0;
    }
}
