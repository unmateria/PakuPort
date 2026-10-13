using System.Text.Json;

namespace PakuPort.Core;

/// <summary>
/// Config por-juego: separa lo genérico (motor) de lo específico del juego desde el
/// día 1. Lee <c>games/&lt;game&gt;/game.json</c>. Los datos de calibración de cada juego son
/// medidas verbatim (NO recalcular).
/// </summary>
public sealed class GameConfig
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public required string ExePath { get; init; }
    public required string CorpusDir { get; init; }
    public ushort LoadSegment { get; init; }
    public uint DsBase { get; init; }
    public string EntryOverride { get; init; } = "";
    public List<string> ExtraSeeds { get; init; } = new();
    public string Format { get; init; } = "exe";
    /// <summary>"real" = mundo boot con mapa de memoria convencional fiel al DOS real
    /// (Int21hHandler.BootRealDos); loadSegment debe ser el PSP que produce ese mapa
    /// (COM: 0x0814). Vacío = mundo legado (raíz en loadSegment-0x10, arena perezoso).</summary>
    public string DosMap { get; init; } = "";
    public (ushort Seg, ushort Off)? VroommStub { get; init; }
    public OverlayDirectory Overlays { get; init; } = new();
    /// <summary>CodeMap estático de radare2 (direcciones de instrucción válidas, offsets de fichero).
    /// Whitelist contra basura de datos-en-código. Vacío si no hay codemap.json.</summary>
    public HashSet<int>? CodeMap { get; init; }
    /// <summary>Semillas de ENTRADA de función (file offsets) derivadas del dump de Spice86
    /// (destinos de CallsFromTo). DiscoverAll las usa como puntos de inicio fiables, incluidos
    /// destinos de indirectos que el análisis estático no alcanza. Vacío si no hay seeds.spice86.json.</summary>
    public HashSet<int>? SpiceSeeds { get; init; }
    public byte VideoMode { get; init; }
    /// <summary>DOS command tail (without leading length byte/CR) baked into the PSP at boot.
    /// Some games take their whole INSTALL configuration as CLI switches (e.g. "V S 5 N N Y 16"
    /// via a generated batch file) and refuse to start without them.</summary>
    public string CmdTail { get; init; } = "";
    public byte[]? PaletteRemap { get; init; }
    public Dictionary<int, ushort>? PageDescriptors { get; init; }
    public Dictionary<int, int>? VroommStubTable { get; set; }
    public ushort OverlayLoadBase { get; set; }
    /// <summary>INT 3F thunk redirects for FarDispatch: fk → overlay function key.
    /// Auto-computed from decompressed EXEPACK image.</summary>
    public Dictionary<int, int>? ThunkRedirects { get; set; }
    /// <summary>Bucle de sesión para el arranque por gameplay-snapshot: tras retornar la
    /// función snapshoteada, el host llama Fn(Each) en bucle hasta que la word de DGROUP
    /// UntilNonZero deja de ser 0 (transcrito del bucle supervisor real del juego).</summary>
    public string? SnapshotLoopEach { get; init; }
    public string? SnapshotLoopUntil { get; init; }
    /// <summary>Presupuesto de pasos (visitas a label) del hilo de lógica por frame de host
    /// (30 fps). Gobierna la velocidad de los delays por software del juego: un 8088 a
    /// 4.77 MHz ejecutaba ~10-15k instrucciones por frame de 33 ms; el default 500k es
    /// adecuado para juegos de era 286/386. Ajustable por juego en game.json.</summary>
    public int BudgetPerFrame { get; init; } = 500_000;

    /// <summary>Governor de ciclos (modelo DOSBox cycles=N): Steps por ms de tiempo real que
    /// se permite ejecutar a la VM. 0 (default) = sin governor (velocidad libre; ningún juego
    /// existente cambia sin decidirlo). Los juegos CPU-paced de la época (botón Turbo) se
    /// desbocan sin esto; DOSBox los frena con cycles (p.ej. un juego de prueba usa cycles=500).</summary>
    public int CyclesPerMs { get; init; } = 0;

    /// <summary>Escala horizontal del ratón del host (Int33hHandler.XScale): en modos de 320 de
    /// ancho el driver Microsoft real presenta X virtual 0..639 y los juegos hacen <c>shr cx,1</c>
    /// (contrato virtual-640) → 2; un snapshot congelado cuya bandera 640-virtual estaba OFF
    /// necesita 1. 0 (default) = auto: lo resuelve el generador del host según el mundo
    /// (snapshot/memimage congelado → 1; game.json/boot-real fresco, fiel a DOSBox → 2).</summary>
    public int MouseXScale { get; init; } = 0;

    /// <summary>Nombre del ejecutable publicado (AssemblyName) que produce <c>pakuport package</c>.
    /// Vacío = default <c>"Jugar" + Id</c> con la 1ª letra en mayúscula. Se fija a mano cuando la
    /// derivación no da el nombre en uso (p.ej. "JugarJuegoA").</summary>
    public string PackageName { get; init; } = "";

    /// <summary>Líneas de "teclas del JUEGO" para la pantalla de arranque del host (splash).
    /// Vacío = el splash solo enseña las teclas del host, como siempre. Se rellenan a mano desde
    /// el manual del juego (no inventar: una tecla mal listada es peor que ninguna).</summary>
    public string[] SplashKeys { get; init; } = System.Array.Empty<string>();
    /// <summary>Nombre de la carpeta de entrega bajo <c>entrega\</c>. Vacío = default
    /// <c>&lt;PackageName sin el prefijo "Jugar"&gt;-PakuPort</c>. Se fija a mano cuando el nombre
    /// histórico de la carpeta no coincide con la derivación (p.ej. "JuegoA-PakuPort").</summary>
    public string PackageDir { get; init; } = "";

    /// <summary>Cómo se republica este juego. Vacío = el camino normal (<c>port</c>: init →
    /// dump-image → export → build → package).
    ///
    /// <c>"solo-nucleo"</c> = NO regenerar el export. Se refresca <c>Core/</c> desde <c>src/</c>,
    /// se compila y se empaqueta. Es el caso de un juego de prueba: su <c>Program.cs</c> está
    /// validado a mano y su <c>exePath</c> vive fuera del repo, así que <c>port</c> falla y lo
    /// marcaba ROJO en CADA republicación de los juegos probados — un rojo permanente y falso, que es justo
    /// lo que impide ver un rojo de verdad.</summary>
    public string PublishFlow { get; init; } = "";

    /// <summary>True: PakuPort se presenta como host DPMI (<c>INT 2Fh AX=1687h</c>). Para juegos
    /// con EXTENSOR EMBEBIDO, que arrancan en modo real y preguntan por un host antes de intentar
    /// el cambio de modo por su cuenta. JSON: <c>"dpmiHost": true</c>. Por defecto NO: los 21
    /// juegos con los que se probó están validados recibiendo "no hay host".</summary>
    public bool DpmiHost { get; init; }

    /// <summary>True: el DOS de 32 bits sirve la API PROPIETARIA del extensor embebido en
    /// <c>INT 21h AH=25/35</c> (número de interrupción en CL, memoria en 350B/350C) en vez de
    /// tratarlas como las de DOS. JSON: <c>"apiExtensor": true</c>.
    ///
    /// Va con interruptor y no por detección porque COLISIONAN: un programa que corre sobre
    /// DOS/4GW usa el <c>AH=25</c> estándar por este mismo camino, y ahí AL sí es
    /// el número de interrupción.</summary>
    public bool ApiExtensor { get; init; }

    /// <summary>Dedicatoria opcional de una entrega personalizada: si no está vacía, el host
    /// la pinta en el splash y la añade al título de la ventana. JSON:
    /// <c>"dedication": "Version especial para Fulano"</c>. Vacía = entrega normal.</summary>
    public string Dedication { get; init; } = "";

    /// <summary>True: el host exportado usa el motor intérprete-puro POR DEFECTO (sin env vars).
    /// <c>PAKU_LIFTED=1</c> vuelve al mundo lifteado; <c>PAKU_INTERP_ONLY=1</c> fuerza el intérprete
    /// siempre. JSON: <c>"interpDefault": true</c>. Solo surte efecto en exports boot-real.</summary>
    public bool InterpDefault { get; init; }

    /// <summary>True: la máquina exportada no lleva tarjeta de juegos (0x201 lee 0xFF, como
    /// DOSBox sin joystick físico). Para juegos que autodetectan el joystick emulado y lo
    /// suman al teclado. JSON: <c>"gamePortAbsent": true</c>.</summary>
    public bool GamePortAbsent { get; init; }

    /// <summary>True si game.json declara un bloque <c>sb</c> (SoundBlaster digital). Cuando es
    /// false el SB no se instancia: los puertos SB/DMA quedan en el stub de reposo (null-op
    /// total para todo juego no-SB → gates intactos).</summary>
    public bool HasSb { get; init; }
    /// <summary>Puerto base del SoundBlaster (default 0x220). Derivado de la configuración de sonido de un juego de prueba.</summary>
    public ushort SbBase { get; init; } = 0x220;
    /// <summary>Línea de IRQ del SoundBlaster (default 7).</summary>
    public int SbIrq { get; init; } = 7;
    /// <summary>Canal DMA 8-bit del SoundBlaster (default 1).</summary>
    public int SbDma { get; init; } = 1;

    public static GameConfig Load(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var r = doc.RootElement;
        var overlays = new OverlayDirectory();
        Dictionary<int, int> codeSizes = new();
        if (r.TryGetProperty("overlayCodeSize", out var csz))
            foreach (var p in csz.EnumerateObject())
                codeSizes[ParseHexStr(p.Name)] = ParseHexStr(p.Value.GetString()!);
        if (r.TryGetProperty("overlayCodeStart", out var ov))
            foreach (var p in ov.EnumerateObject())
            {
                int no = ParseHexStr(p.Name);
                int start = ParseHexStr(p.Value.GetString()!);
                codeSizes.TryGetValue(no, out int sz);
                overlays.Add(no, start, sz);
            }
        Dictionary<int, int>? stubTable = null;
        if (r.TryGetProperty("vroommStubTable", out var vst))
        {
            stubTable = new();
            foreach (var p in vst.EnumerateObject())
                stubTable[ParseHexStr(p.Name)] = ParseHexStr(p.Value.GetString()!);
        }

        // CodeMap estático (codemap.json junto a game.json), si existe.
        HashSet<int>? codeMap = null;
        string cmPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!, "codemap.json");
        if (File.Exists(cmPath))
        {
            using var cm = JsonDocument.Parse(File.ReadAllText(cmPath));
            codeMap = new HashSet<int>();
            foreach (var a in cm.RootElement.GetProperty("code").EnumerateArray())
                codeMap.Add(a.GetInt32());
        }

        // Semillas de entrada del dump de Spice86 (seeds.spice86.json junto a game.json), si existe.
        HashSet<int>? spiceSeeds = null;
        string ssPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!, "seeds.spice86.json");
        if (File.Exists(ssPath))
        {
            using var ss = JsonDocument.Parse(File.ReadAllText(ssPath));
            spiceSeeds = new HashSet<int>();
            foreach (var a in ss.RootElement.GetProperty("seeds").EnumerateArray())
                spiceSeeds.Add(a.GetInt32());
        }

        var config = new GameConfig
        {
            Id = r.GetProperty("id").GetString()!,
            Title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            Format = r.TryGetProperty("format", out var fmt) ? fmt.GetString() ?? "exe" : "exe",
            DosMap = r.TryGetProperty("dosMap", out var dm) ? dm.GetString() ?? "" : "",
            EntryOverride = r.TryGetProperty("entryOverride", out var eo) ? eo.GetString() ?? "" : "",
            ExtraSeeds = r.TryGetProperty("extraSeeds", out var es)
                ? es.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string>(),
            VroommStub = ParseVroommStub(r),
            ExePath = r.GetProperty("exePath").GetString()!,
            CorpusDir = r.GetProperty("corpusDir").GetString()!,
            LoadSegment = (ushort)ParseHex(r, "loadSegment"),
            DsBase = ParseHex(r, "dsBase"),
            Overlays = overlays,
            CodeMap = codeMap,
            SpiceSeeds = spiceSeeds,
            VideoMode = r.TryGetProperty("videoMode", out var vm) ? (byte)ParseHex(r, "videoMode") : (byte)0,
            CmdTail = r.TryGetProperty("cmdTail", out var ctl) ? ctl.GetString() ?? "" : "",
            PaletteRemap = ParsePaletteRemap(r),
            PageDescriptors = ParsePageDescriptors(r),
            VroommStubTable = stubTable,
            OverlayLoadBase = (ushort)ParseHex(r, "overlayLoadBase"),
            SnapshotLoopEach = r.TryGetProperty("snapshotLoop", out var sl) && sl.TryGetProperty("each", out var sle)
                ? sle.GetString() : null,
            SnapshotLoopUntil = r.TryGetProperty("snapshotLoop", out var sl2) && sl2.TryGetProperty("untilNonZero", out var slu)
                ? slu.GetString() : null,
            BudgetPerFrame = r.TryGetProperty("budgetPerFrame", out var bpf) ? bpf.GetInt32() : 500_000,
            CyclesPerMs = r.TryGetProperty("cyclesPerMs", out var cpm) ? cpm.GetInt32() : 0,
            MouseXScale = r.TryGetProperty("mouseXScale", out var mxs) ? mxs.GetInt32() : 0,
            PackageName = r.TryGetProperty("packageName", out var pkn) ? pkn.GetString() ?? "" : "",
            SplashKeys = r.TryGetProperty("splashKeys", out var spk) && spk.ValueKind == JsonValueKind.Array
                ? spk.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                : System.Array.Empty<string>(),
            PackageDir = r.TryGetProperty("packageDir", out var pkd) ? pkd.GetString() ?? "" : "",
            PublishFlow = r.TryGetProperty("publishFlow", out var pf) ? pf.GetString() ?? "" : "",
            DpmiHost = r.TryGetProperty("dpmiHost", out var dph) && dph.GetBoolean(),
            ApiExtensor = r.TryGetProperty("apiExtensor", out var apx) && apx.GetBoolean(),
            Dedication = r.TryGetProperty("dedication", out var ded) ? ded.GetString() ?? "" : "",
            InterpDefault = r.TryGetProperty("interpDefault", out var idf) && idf.GetBoolean(),
            GamePortAbsent = r.TryGetProperty("gamePortAbsent", out var gpa) && gpa.GetBoolean(),
            HasSb = r.TryGetProperty("sb", out _),
            SbBase = r.TryGetProperty("sb", out var sbB) && sbB.TryGetProperty("base", out var sbBase)
                ? (ushort)ParseHexStr(sbBase.GetString()!) : (ushort)0x220,
            SbIrq = r.TryGetProperty("sb", out var sbI) && sbI.TryGetProperty("irq", out var sbIrq)
                ? sbIrq.GetInt32() : 7,
            SbDma = r.TryGetProperty("sb", out var sbD) && sbD.TryGetProperty("dma", out var sbDma)
                ? sbDma.GetInt32() : 1,
        };

        // Auto-detect EXEPACK + VROOMM: compute stub table and overlay load base
        // when not provided in game.json. This makes PakuPort fully autonomous.
        if (config.VroommStubTable == null && config.Overlays.Count > 0 &&
            config.Format != "com" && File.Exists(config.ExePath))
        {
            var exeBytes = File.ReadAllBytes(config.ExePath);
            if (ExePackDecompressor.IsExePacked(exeBytes))
            {
                var (autoTable, autoBase, autoThunks) = ExePackDecompressor.BuildVroommStubTable(exeBytes, config.Overlays);
                if (autoTable != null && autoTable.Count > 0)
                {
                    config.VroommStubTable = autoTable;
                    if (config.OverlayLoadBase == 0)
                        config.OverlayLoadBase = autoBase;
                }
                if (autoThunks != null && autoThunks.Count > 0)
                    config.ThunkRedirects = autoThunks;
            }
        }

        return config;
    }

    private static (ushort, ushort)? ParseVroommStub(JsonElement r)
    {
        if (!r.TryGetProperty("vroommEntryStub", out var v)) return null;
        string s = v.GetString() ?? "";
        int colon = s.IndexOf(':');
        if (colon < 0) return null;
        return ((ushort)ParseHexStr(s[..colon]), (ushort)ParseHexStr(s[(colon + 1)..]));
    }

    private static int ParseHexStr(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return Convert.ToInt32(s, 16);
    }

    private static byte[]? ParsePaletteRemap(JsonElement r)
    {
        if (!r.TryGetProperty("paletteRemap", out var arr)) return null;
        var list = new List<byte>();
        foreach (var e in arr.EnumerateArray())
            list.Add((byte)e.GetInt32());
        return list.ToArray();
    }

    private static Dictionary<int, ushort>? ParsePageDescriptors(JsonElement r)
    {
        if (!r.TryGetProperty("pageDescriptors", out var obj)) return null;
        var dict = new Dictionary<int, ushort>();
        foreach (var p in obj.EnumerateObject())
            dict[int.Parse(p.Name)] = (ushort)ParseHexStr(p.Value.GetString()!);
        return dict;
    }

    private static uint ParseHex(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var e)) return 0;
        string s = e.GetString() ?? "0";
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return Convert.ToUInt32(s, 16);
    }
}
