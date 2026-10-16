using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// What an export was generated from (`pakuport-host.json`), so `remake doctor` and `remake capture`
/// can tell a host that still matches its sources from one edited by hand, generated for another
/// game.json, for a binary that has since changed, or by an older template. `capture` refreshes
/// Core and rebuilds, but the host's Program.cs is only rewritten by `export`: a stale one ignores a
/// lever the kit sets and the capture comes out poorer without saying so.
///
/// <para>It certifies the sources, not that a capture will be good.</para>
/// </summary>
internal static class ExportCapabilities
{
    public const string FileName = "pakuport-host.json";
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // readable commands
    };

    internal sealed record Contract(int SchemaVersion, string GameId, string HostKind, int CyclesPerMs,
        string ConfigSha256, string ExeSha256, string ProgramSha256, string ProjectFile, string ProjectSha256,
        string TemplateSha256, string ExportCommand, string[] Capabilities);

    internal sealed record Diagnosis(string GameId, string Export, bool ReadyForCapture,
        string[] Capabilities, string[] Problems, string[] Warnings, string NextCommand);

    /// <summary>What the remake capture needs from the host template (the 16-bit DOS one has it all;
    /// the LE host records no tape and no blits).</summary>
    internal static readonly string[] CaptureCapabilities =
        ["input-tape", "instruction-clock", "interp-only", "blit-v3", "palette-log", "presented-frames"];

    /// <summary>The host templates live in the CLI's Program.cs, embedded in the assembly as a resource:
    /// its hash changes whenever a template can have changed. It also changes with any other edit
    /// to that file, which is why a mismatch is a warning and not a refusal.</summary>
    internal static string TemplateHash()
    {
        using var s = typeof(ExportCapabilities).Assembly.GetManifestResourceStream("PakuPort.HostTemplate.cs");
        return s == null ? "unknown" : Convert.ToHexString(SHA256.HashData(s));
    }

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void Write(string export, string gameId, string config, string exe, int cycles,
                             string hostKind, string exportCommand, bool supportsCapture)
    {
        string project = gameId + ".csproj";
        var contract = new Contract(1, gameId, hostKind, cycles, Hash(config), Hash(exe),
            Hash(Path.Combine(export, "Program.cs")), project, Hash(Path.Combine(export, project)),
            TemplateHash(), exportCommand, supportsCapture ? CaptureCapabilities : []);
        File.WriteAllText(Path.Combine(export, FileName), JsonSerializer.Serialize(contract, Json));
    }

    /// <summary>The command `port` itself would run for this game: boot-real when there is a frozen
    /// memory image, and the self-healing seeds when they exist.</summary>
    internal static string RegenerateCommand(string gameId, string config, string export)
    {
        var cmd = $"pakuport export --game \"{config}\" --out \"{export.TrimEnd('/', '\\')}\"";
        string gameDir = Path.GetDirectoryName(Path.GetFullPath(config))!;
        if (File.Exists(Path.Combine(gameDir, "explorer", "memimage.bin"))) cmd += " --boot-real";
        string seeds = Path.Combine(gameDir, "seeds-runtime.log");
        if (File.Exists(seeds)) cmd += $" --seeds \"{Path.Combine("games", gameId, "seeds-runtime.log")}\"";
        return cmd;
    }

    public static Diagnosis Inspect(string export, string gameId, string config)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        Contract? contract = null;
        try
        {
            string manifest = Path.Combine(export, FileName);
            if (!File.Exists(manifest))
                problems.Add($"Falta {FileName}: el export es anterior a este contrato o no lo generó `pakuport export`.");
            else contract = JsonSerializer.Deserialize<Contract>(File.ReadAllText(manifest), Json);
            if (contract is not null)
            {
                if (contract.SchemaVersion != 1) problems.Add($"Versión de contrato no admitida: {contract.SchemaVersion}.");
                if (contract.GameId != gameId) problems.Add($"El export es de otro juego ({contract.GameId}).");
                if (contract.CyclesPerMs <= 0) problems.Add("Se generó sin cyclesPerMs: la cinta no se puede reproducir igual.");
                Verify(config, contract.ConfigSha256, "game.json cambió desde que se generó el export", problems);
                Verify(Path.Combine(export, "Program.cs"), contract.ProgramSha256, "el Program.cs del host se tocó a mano", problems);
                if (!SafeName(contract.ProjectFile))
                    problems.Add("El contrato nombra un proyecto con una ruta inválida.");
                else
                    Verify(Path.Combine(export, contract.ProjectFile), contract.ProjectSha256, "el .csproj del host se tocó a mano", problems);
                using var doc = JsonDocument.Parse(File.ReadAllText(config));
                Verify(doc.RootElement.GetProperty("exePath").GetString() ?? "", contract.ExeSha256,
                       "el ejecutable del juego cambió desde que se generó el export", problems);
                foreach (string capability in CaptureCapabilities)
                    if (contract.Capabilities is null || !contract.Capabilities.Contains(capability))
                    {
                        problems.Add($"Este host ({contract.HostKind}) no tiene lo que la captura necesita: falta {capability}.");
                        break;
                    }
                if (contract.TemplateSha256 != TemplateHash())
                    warnings.Add("Lo generó otra versión de la plantilla del host: si cambió algo que la captura " +
                                 "use, regenéralo antes (la orden de abajo).");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            problems.Add("No se puede verificar el contrato: " + e.Message);
        }
        string next = contract is not null && !string.IsNullOrEmpty(contract.ExportCommand)
            ? contract.ExportCommand
            : RegenerateCommand(gameId, config, export);
        if (problems.Count == 0 && warnings.Count == 0) next = $"pakuport remake capture {gameId}";
        return new(gameId, Path.GetFullPath(export), problems.Count == 0, contract?.Capabilities ?? [],
                   problems.ToArray(), warnings.ToArray(), next);
    }

    static bool SafeName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".." && !name.Contains('/') && !name.Contains('\\')
        && !Path.IsPathRooted(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    static void Verify(string path, string expected, string message, List<string> problems)
    {
        if (!File.Exists(path)) problems.Add($"Falta {path}.");
        else if (!string.Equals(Hash(path), expected, StringComparison.OrdinalIgnoreCase)) problems.Add(char.ToUpper(message[0]) + message[1..] + ".");
    }

    public static void Print(Diagnosis report, bool json)
    {
        if (json) { Console.WriteLine(JsonSerializer.Serialize(report, Json)); return; }
        Console.WriteLine(report.ReadyForCapture
            ? "El export coincide con sus fuentes: se puede capturar."
            : "No se puede capturar con este export:");
        foreach (var problem in report.Problems) Console.WriteLine("  - " + problem);
        foreach (var warning in report.Warnings) Console.WriteLine("  aviso: " + warning);
        Console.WriteLine("Siguiente: " + report.NextCommand);
    }
}
