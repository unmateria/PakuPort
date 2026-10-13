namespace PakuPort.Core.Platform;

/// <summary>
/// Registro de los drivers que el juego carga por EXEC (INT21 AH=4B03) — los drivers de
/// gráficos, sonido y utilidades que el juego trae aparte. En la imagen prístina el juego los llama vía thunks
/// `call far 0000:off` (seg 0); tras el startup real los punteros far del DGROUP apuntan al
/// segmento donde DOS cargó el driver. El host registra (segmento -> keyBase del código
/// LIFTEADO del driver) al arrancar de un snapshot y FarDispatch enruta esas llamadas al
/// código real del driver traducido — NO a una reimplementación.
/// </summary>
public static class DriverRegistry
{
    public const int ScreenWidth = 320;
    public const int ScreenHeight = 200;
    public const int PageSize = ScreenWidth * ScreenHeight;

    const ushort PageAllocBase = 0x4000;
    const int MaxPages = 16;

    /// <summary>
    /// Initialize offscreen page buffers in Memory.Raw and write their segment addresses
    /// into the DGROUP page descriptors so game code can find them (port-nativo mode, i.e.
    /// no snapshot). This is memory/descriptor setup the lifted game code reads directly.
    /// Call this after DGROUP injection but before game code runs.
    /// </summary>
    public static void InitPageBuffers(Memory m, ushort ds, (ushort descriptorOffset, int pageId)[] pages)
    {
        foreach (var (descOff, pageId) in pages)
        {
            if (pageId <= 0 || pageId >= MaxPages) continue;
            ushort seg = (ushort)(PageAllocBase + pageId * 0x1000);
            m.WrU16(ds, (ushort)(descOff + 2), seg);
            m.WrU16(ds, (ushort)(descOff + 4), 0);
            int lin = seg * 16;
            for (int i = 0; i < PageSize; i++)
                m.Raw[(lin + i) & 0xFFFFF] = 0;
        }
    }

    // --- Drivers cargados por EXEC (AH=4B03) en un arranque real/snapshot ---
    static readonly Dictionary<ushort, (string Name, int KeyBase)> _driverSegs = new();

    /// <summary>keyBase del código LIFTEADO de cada driver, por nombre de fichero (sin ruta,
    /// case-insensitive). El host exportado lo puebla en el arranque; RegisterDriverSegment
    /// lo consulta cuando el juego carga el driver por EXEC en runtime — así el despacho al
    /// código lifteado es inmune al segmento concreto que asigne el allocator.</summary>
    public static readonly Dictionary<string, int> DriverKeyBases = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterDriverSegment(ushort seg, string name, int keyBase = -1)
    {
        if (keyBase < 0)
        {
            string fn = Path.GetFileNameWithoutExtension(name);
            foreach (var (k, v) in DriverKeyBases)
                if (Path.GetFileNameWithoutExtension(k).Equals(fn, StringComparison.OrdinalIgnoreCase))
                { keyBase = v; break; }
        }
        _driverSegs[seg] = (name.ToLowerInvariant(), keyBase);
    }

    /// <summary>Base de keys del código LIFTEADO del driver (imagen compuesta): las
    /// llamadas far a seg:off del driver se despachan a Fn_(keyBase+off) — el código
    /// real del driver traducido, no una reimplementación.</summary>
    public static bool TryGetDriverBase(ushort seg, out int keyBase)
    {
        if (_driverSegs.TryGetValue(seg, out var d) && d.KeyBase >= 0) { keyBase = d.KeyBase; return true; }
        keyBase = -1;
        return false;
    }

    /// <summary>Inverso de <see cref="TryGetDriverBase"/>: file key del composite → dónde
    /// vive ese byte en runtime (seg:off del driver cargado por EXEC). Para que el
    /// intérprete de huecos (Interp) ejecute bytes de un driver desde la memoria real.</summary>
    public static bool TryGetSegOffForKey(int key, out ushort seg, out ushort off)
    {
        int best = -1; ushort bestSeg = 0;
        foreach (var (s, d) in _driverSegs)
            if (d.KeyBase >= 0 && d.KeyBase <= key && key - d.KeyBase < 0x10000 && d.KeyBase > best)
            { best = d.KeyBase; bestSeg = s; }
        seg = bestSeg;
        off = best >= 0 ? (ushort)(key - best) : (ushort)0;
        return best >= 0;
    }
}
