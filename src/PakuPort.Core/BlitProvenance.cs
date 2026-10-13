namespace PakuPort.Core;

/// <summary>File provenance for blit telemetry (second-port doctrine, phase 2).
/// Circular buffer of RAM ranges filled by INT 21h ReadFile; LogBlit queries it to tag
/// a blit source as "FILE@offset". Active only under PAKU_LOG_BLIT=1; otherwise Record
/// is an immediate no-op and nothing is allocated.</summary>
public static class BlitProvenance
{
    /// <summary>Off by default. Also on under PAKU_REMAKE: the HD replacement layer needs
    /// the chain to identify an asset composed in RAM, not just the file->VRAM shortcut.</summary>
    public static readonly bool Enabled =
        (int.TryParse(Environment.GetEnvironmentVariable("PAKU_LOG_BLIT"), out var level) && level > 0) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PAKU_REMAKE"));

    static readonly bool _enabled = Enabled;

    const int Cap = 1024;
    struct Range { public int LinStart, Len; public string File; public long FileOff; }
    static Range[]? _ranges;
    static int _next;      // next slot to overwrite
    static int _count;

    public static void Record(int linStart, int len, string file, long fileOff)
    {
        if (!_enabled || len <= 0) return;
        _ranges ??= new Range[Cap];
        _ranges[_next] = new Range { LinStart = linStart, Len = len, File = file, FileOff = fileOff };
        _next = (_next + 1) % Cap;
        if (_count < Cap) _count++;
    }

    /// <summary>Most-recent-first search; first range covering lin wins.</summary>
    public static bool TryFind(int lin, out string file, out long off)
    {
        file = ""; off = 0;
        if (_ranges == null) return false;
        for (int i = 1; i <= _count; i++)
        {
            int idx = (_next - i + Cap) % Cap;
            ref var r = ref _ranges[idx];
            if (lin >= r.LinStart && lin < r.LinStart + r.Len)
            {
                file = r.File;
                off = r.FileOff + (lin - r.LinStart);
                return true;
            }
        }
        return false;
    }
}
