using Xunit;

namespace PakuPort.Tests;

public sealed class CapturePublicationTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "paku-capture-" + Guid.NewGuid().ToString("N"));
    string Attempts => Path.Combine(root, "intentos");
    string Attempt => Path.Combine(Attempts, "20261016-120000-nuevo");

    void Prepare()
    {
        Directory.CreateDirectory(Path.Combine(root, "captura"));
        File.WriteAllText(Path.Combine(root, "captura", "blit.log"), "traza anterior");
        Directory.CreateDirectory(Path.Combine(Attempt, "captura"));
        File.WriteAllText(Path.Combine(Attempt, "captura", "blit.log"), "traza nueva");
        File.WriteAllText(Path.Combine(Attempt, "cinta.tape"), "cinta nueva");
    }

    [Fact]
    public void SuccessfulPublicationReplacesThePairAndLeavesNoCopyBehind()
    {
        Prepare();
        File.WriteAllText(Path.Combine(root, "cinta.tape"), "cinta anterior");
        CapturePublication.Publish(Attempt, root);
        Assert.Equal("cinta nueva", File.ReadAllText(Path.Combine(root, "cinta.tape")));
        Assert.Equal("traza nueva", File.ReadAllText(Path.Combine(root, "captura", "blit.log")));
        Assert.False(Directory.Exists(Attempt));
    }

    [Fact]
    public void EmptyTapeCannotReplacePreviousCapture()
    {
        Prepare();
        File.WriteAllText(Path.Combine(root, "cinta.tape"), "cinta anterior");
        File.WriteAllText(Path.Combine(Attempt, "cinta.tape"), "");
        Assert.Throws<IOException>(() => CapturePublication.Publish(Attempt, root));
        Assert.Equal("cinta anterior", File.ReadAllText(Path.Combine(root, "cinta.tape")));
        Assert.Equal("traza anterior", File.ReadAllText(Path.Combine(root, "captura", "blit.log")));
    }

    [Fact]
    public void FailureAfterMovingCaptureRestoresPreviousTrace()
    {
        Prepare();
        // Blocks publishing the tape after both trace directories have moved.
        Directory.CreateDirectory(Path.Combine(root, "cinta.tape"));
        Assert.ThrowsAny<IOException>(() => CapturePublication.Publish(Attempt, root));
        Assert.Equal("traza anterior", File.ReadAllText(Path.Combine(root, "captura", "blit.log")));
        Assert.Equal("traza nueva", File.ReadAllText(Path.Combine(Attempt, "captura", "blit.log")));
        Assert.Equal("cinta nueva", File.ReadAllText(Path.Combine(Attempt, "cinta.tape")));
        Assert.False(Directory.Exists(Path.Combine(Attempt, "anterior")));
    }

    [Fact]
    public void PruneKeepsOnlyTheMostRecentAttempts()
    {
        foreach (var name in new[] { "20261001-000000-a", "20261002-000000-b", "20261003-000000-c", "20261004-000000-d" })
            Directory.CreateDirectory(Path.Combine(Attempts, name));
        CapturePublication.Prune(Attempts, keep: 3);
        Assert.Equal(new[] { "20261002-000000-b", "20261003-000000-c", "20261004-000000-d" },
                     Directory.GetDirectories(Attempts).Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Directorio de prueba fuera de temp.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
