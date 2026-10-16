using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PakuPort.Tests;

public sealed class ExportCapabilitiesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "paku-host-" + Guid.NewGuid().ToString("N"));
    string Export => Path.Combine(root, "export");
    string Config => Path.Combine(root, "game.json");
    string Exe => Path.Combine(root, "GAME.COM");

    void Prepare(bool capture = true)
    {
        Directory.CreateDirectory(Export);
        File.WriteAllText(Exe, "original");
        File.WriteAllText(Config, JsonSerializer.Serialize(new { id = "fixture", exePath = Exe, cyclesPerMs = 315 }));
        File.WriteAllText(Path.Combine(Export, "Program.cs"), "host");
        File.WriteAllText(Path.Combine(Export, "fixture.csproj"), "project");
        ExportCapabilities.Write(Export, "fixture", Config, Exe, 315, "dos16",
                                 "pakuport export --game g --out e", capture);
    }

    ExportCapabilities.Diagnosis Inspect() => ExportCapabilities.Inspect(Export, "fixture", Config);

    [Fact]
    public void CurrentHostIsReady()
    {
        Prepare();
        var report = Inspect();
        Assert.True(report.ReadyForCapture, string.Join(" | ", report.Problems));
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void LegacyExportIsRefusedWithTheCommandThatRegeneratesIt()
    {
        Directory.CreateDirectory(Export);
        File.WriteAllText(Exe, "original");
        File.WriteAllText(Config, JsonSerializer.Serialize(new { id = "fixture", exePath = Exe, cyclesPerMs = 315 }));
        string host = Path.Combine(Export, "Program.cs");
        File.WriteAllText(host, "legacy");
        var report = Inspect();
        Assert.False(report.ReadyForCapture);
        Assert.StartsWith("pakuport export --game", report.NextCommand);
        Assert.Equal("legacy", File.ReadAllText(host));               // doctor never touches the export
    }

    [Fact]
    public void RecordedExportCommandIsTheOneSuggested()
    {
        Prepare();
        File.AppendAllText(Path.Combine(Export, "Program.cs"), "changed");
        Assert.Equal("pakuport export --game g --out e", Inspect().NextCommand);
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("fixture.csproj")]
    public void HandEditedHostIsRefused(string file)
    {
        Prepare();
        File.AppendAllText(Path.Combine(Export, file), "changed");
        Assert.False(Inspect().ReadyForCapture);
    }

    [Fact]
    public void ChangedConfigurationOrGameBinaryIsRefused()
    {
        Prepare();
        File.AppendAllText(Config, " ");
        Assert.False(Inspect().ReadyForCapture);
        Prepare();
        File.AppendAllText(Exe, "changed");
        Assert.False(Inspect().ReadyForCapture);
    }

    [Fact]
    public void HostWithoutCaptureSupportSaysWhatIsMissing()
    {
        Prepare(capture: false);
        var report = Inspect();
        Assert.False(report.ReadyForCapture);
        Assert.Contains(report.Problems, p => p.Contains("input-tape"));
    }

    [Fact]
    public void OtherTemplateIsAWarningNotARefusal()
    {
        Prepare();
        string path = Path.Combine(Export, ExportCapabilities.FileName);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["templateSha256"] = "00";
        File.WriteAllText(path, node.ToJsonString());
        var report = Inspect();
        Assert.True(report.ReadyForCapture);
        Assert.Single(report.Warnings);
        Assert.Equal("pakuport export --game g --out e", report.NextCommand);
    }

    [Theory]
    [InlineData("schemaVersion", "99")]
    [InlineData("capabilities", "[]")]
    [InlineData("cyclesPerMs", "0")]
    [InlineData("projectFile", "\"../outside.csproj\"")]
    [InlineData("gameId", "\"otro\"")]
    public void InvalidContractIsRefused(string key, string json)
    {
        Prepare();
        string path = Path.Combine(Export, ExportCapabilities.FileName);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node[key] = JsonNode.Parse(json);
        File.WriteAllText(path, node.ToJsonString());
        Assert.False(Inspect().ReadyForCapture);
    }

    [Fact]
    public void CorruptJsonProducesDiagnosis()
    {
        Prepare();
        File.WriteAllText(Path.Combine(Export, ExportCapabilities.FileName), "{broken");
        var report = Inspect();
        Assert.False(report.ReadyForCapture);
        Assert.NotEmpty(report.Problems);
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Directorio de prueba fuera de temp.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
