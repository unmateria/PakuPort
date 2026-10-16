using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Procedencia de un resultado: entradas y salidas exactas, también para capturas antiguas.</summary>
internal static class RemakeEvidence
{
    internal sealed record Stamp(int SchemaVersion, string[] Inputs, string InputHash, string[] Outputs, string OutputHash);

    public static string Fingerprint(IEnumerable<string> paths)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string source in paths)
        {
            string path = Path.GetFullPath(source);
            if (File.Exists(path)) values[path] = FileHash(path);
            else if (Directory.Exists(path))
            {
                values[path] = "directory";
                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    values[Path.GetFullPath(file)] = FileHash(file);
            }
            else values[path] = "missing";
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    }

    static string FileHash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static bool Complete(string stampFile, string[] inputs, string before, string[] outputs)
    {
        if (Fingerprint(inputs) != before || outputs.Any(p => !File.Exists(p) && !Directory.Exists(p))) return false;
        var stamp = new Stamp(1, inputs.Select(Path.GetFullPath).ToArray(), before,
            outputs.Select(Path.GetFullPath).ToArray(), Fingerprint(outputs));
        File.WriteAllText(stampFile, JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    public static string Status(string stampFile)
    {
        if (!File.Exists(stampFile)) return "sin procedencia verificable";
        try
        {
            var stamp = JsonSerializer.Deserialize<Stamp>(File.ReadAllText(stampFile));
            if (stamp is null || stamp.SchemaVersion != 1 || stamp.Inputs is null || stamp.Outputs is null)
                return "procedencia inválida";
            if (Fingerprint(stamp.Inputs) != stamp.InputHash) return "antiguo: cambiaron sus entradas";
            if (Fingerprint(stamp.Outputs) != stamp.OutputHash) return "modificado: cambiaron sus resultados";
            return "vigente";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return "procedencia ilegible";
        }
    }
}
