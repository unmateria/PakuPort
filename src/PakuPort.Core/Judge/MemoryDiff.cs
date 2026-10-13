namespace PakuPort.Judge.Diff;

/// <summary>Un tramo de bytes que difiere entre dos buffers.</summary>
public readonly record struct DiffRange(int Start, int Length, byte[] Before, byte[] After)
{
    public override string ToString()
    {
        string hex(byte[] b) => b.Length <= 16
            ? Convert.ToHexString(b)
            : Convert.ToHexString(b.AsSpan(0, 16).ToArray()) + "…";
        return $"@0x{Start:X4} len {Length}: {hex(Before)} -> {hex(After)}";
    }
}

public static class MemoryDiff
{
    /// <summary>
    /// Compara dos buffers de igual longitud y devuelve los tramos que difieren, coalescidos
    /// (se fusionan diffs separados por menos de <paramref name="gap"/> bytes iguales).
    /// </summary>
    public static List<DiffRange> Compare(byte[] a, byte[] b, int gap = 4)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"longitudes distintas: {a.Length} vs {b.Length}");
        var ranges = new List<DiffRange>();
        int i = 0, n = a.Length;
        while (i < n)
        {
            if (a[i] == b[i]) { i++; continue; }
            int start = i;
            int lastDiff = i;
            i++;
            // extender mientras la separación de bytes iguales no supere 'gap'
            while (i < n && (a[i] != b[i] || i - lastDiff <= gap))
            {
                if (a[i] != b[i]) lastDiff = i;
                i++;
            }
            int end = lastDiff + 1;
            int len = end - start;
            ranges.Add(new DiffRange(start, len, a[start..end], b[start..end]));
        }
        return ranges;
    }

    public static bool Equal(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
}
