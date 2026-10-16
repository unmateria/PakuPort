using Xunit;

namespace PakuPort.Tests;

public sealed class RemakeEvidenceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "paku-evidence-" + Guid.NewGuid().ToString("N"));
    string Input => Path.Combine(root, "cinta.tape");
    string Output => Path.Combine(root, "informe.json");
    string Stamp => Path.Combine(root, "procedencia.json");

    void Prepare()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Input, "capture-A");
        File.WriteAllText(Output, "result-A");
    }

    bool Complete() => RemakeEvidence.Complete(Stamp, [Input], RemakeEvidence.Fingerprint([Input]), [Output]);

    [Fact]
    public void UnchangedEvidenceIsCurrent()
    {
        Prepare();
        Assert.True(Complete());
        Assert.Equal("vigente", RemakeEvidence.Status(Stamp));
    }

    [Fact]
    public void SameSizeCaptureEditInvalidatesResult()
    {
        Prepare();
        Assert.True(Complete());
        var time = File.GetLastWriteTimeUtc(Input);
        File.WriteAllText(Input, "capture-B");
        File.SetLastWriteTimeUtc(Input, time);
        Assert.StartsWith("antiguo:", RemakeEvidence.Status(Stamp));
    }

    [Fact]
    public void ModifiedOrDeletedReportIsNotCurrent()
    {
        Prepare();
        Assert.True(Complete());
        File.WriteAllText(Output, "result-B");
        Assert.StartsWith("modificado:", RemakeEvidence.Status(Stamp));
        File.Delete(Output);
        Assert.StartsWith("modificado:", RemakeEvidence.Status(Stamp));
    }

    [Fact]
    public void CaptureChangedDuringWorkDoesNotGetStamp()
    {
        Prepare();
        var before = RemakeEvidence.Fingerprint([Input]);
        File.WriteAllText(Input, "capture-B");
        Assert.False(RemakeEvidence.Complete(Stamp, [Input], before, [Output]));
        Assert.False(File.Exists(Stamp));
    }

    [Fact]
    public void MissingRequiredReportDoesNotGetStamp()
    {
        Prepare();
        File.Delete(Output);
        Assert.False(Complete());
    }

    [Fact]
    public void DirectoryFingerprintDetectsNewAndChangedFiles()
    {
        Prepare();
        string dir = Path.Combine(root, "captura");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "blit.log"), "blits");
        Assert.True(RemakeEvidence.Complete(Stamp, [dir], RemakeEvidence.Fingerprint([dir]), [Output]));
        File.WriteAllText(Path.Combine(dir, "frame.png"), "frame");
        Assert.StartsWith("antiguo:", RemakeEvidence.Status(Stamp));
    }

    [Fact]
    public void LegacyOrCorruptStampDoesNotClaimCurrentEvidence()
    {
        Prepare();
        Assert.Equal("sin procedencia verificable", RemakeEvidence.Status(Stamp));
        File.WriteAllText(Stamp, "{broken");
        Assert.Equal("procedencia ilegible", RemakeEvidence.Status(Stamp));
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Directorio de prueba fuera de temp.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
