namespace PakuPort.Core.Platform;

/// <summary>
/// Extra DOS drives of a published game, and their file: <c>DISCOS.txt</c> next to the exe.
///
/// By default a game only sees the folder its exe lives in (C:) and its subfolders — nothing
/// else of the host is reachable. This is the door to mount MORE: one line per drive,
/// <c>D=C:\ruta\modulos</c>, letters D..Z, blank lines and lines starting with '#' ignored.
/// The first screen of the host (the splash) edits this file with the D / Supr keys, so the
/// player never has to touch a text file or an environment variable.
///
/// Each line becomes the <c>PAKU_DISCO_&lt;LETTER&gt;</c> variable that
/// <see cref="DosFileSystem"/> reads when the game makes its first INT 21h call — which is AFTER
/// the splash, so a drive added there is live for that same run.
/// </summary>
public sealed class DiscosExtra
{
    public string Fichero { get; }
    public List<(char Letra, string Ruta)> Unidades { get; } = new();

    /// <summary>DISCOS.txt next to the exe; if there is none there but there is one in the game
    /// folder, that one is used instead.</summary>
    public DiscosExtra(string gameDir)
    {
        string juntoAlExe = Path.Combine(AppContext.BaseDirectory, "DISCOS.txt");
        string enElJuego = Path.Combine(gameDir, "DISCOS.txt");
        Fichero = !File.Exists(juntoAlExe) && File.Exists(enElJuego) ? enElJuego : juntoAlExe;
    }

    public void Cargar()
    {
        Unidades.Clear();
        try
        {
            if (!File.Exists(Fichero)) return;
            foreach (string linea in File.ReadAllLines(Fichero))
            {
                string l = linea.Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                int eq = l.IndexOf('=');
                if (eq < 1) continue;
                string letra = l[..eq].Trim().ToUpperInvariant();
                string ruta = l[(eq + 1)..].Trim();
                if (letra.Length != 1 || letra[0] < 'D' || letra[0] > 'Z' || ruta.Length == 0) continue;
                if (Unidades.Any(u => u.Letra == letra[0])) continue;
                Unidades.Add((letra[0], ruta));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[DISCOS] " + ex.Message); }
    }

    /// <summary>Publishes the list as PAKU_DISCO_* variables. A path that no longer exists is NOT
    /// mounted (it still shows in the splash, marked).</summary>
    public void Aplicar()
    {
        for (char c = 'D'; c <= 'Z'; c++) Environment.SetEnvironmentVariable("PAKU_DISCO_" + c, null);
        foreach (var u in Unidades)
        {
            if (!Directory.Exists(u.Ruta)) continue;
            Environment.SetEnvironmentVariable("PAKU_DISCO_" + u.Letra, u.Ruta);
            Console.Error.WriteLine($"[DISCOS] {u.Letra}: -> {u.Ruta}");
        }
    }

    public void Guardar()
    {
        try
        {
            var lineas = new List<string>
            {
                "# Unidades DOS extra. Una linea por unidad: LETRA=ruta de una carpeta del host.",
                "# La C: es siempre la carpeta del juego. Se edita con la tecla D de la primera pantalla.",
            };
            foreach (var u in Unidades) lineas.Add($"{u.Letra}={u.Ruta}");
            File.WriteAllLines(Fichero, lineas);
        }
        catch (Exception ex) { Console.Error.WriteLine("[DISCOS] " + ex.Message); }
    }

    /// <summary>Mounts a host folder on the next free letter. Returns the letter, or null when
    /// there is none free or the folder is already mounted.</summary>
    public char? Anadir(string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta) || !Directory.Exists(ruta)) return null;
        if (Unidades.Any(u => string.Equals(u.Ruta, ruta, StringComparison.OrdinalIgnoreCase))) return null;
        for (char c = 'D'; c <= 'Z'; c++)
        {
            if (Unidades.Any(u => u.Letra == c)) continue;
            Unidades.Add((c, ruta));
            Aplicar();
            Guardar();
            return c;
        }
        return null;
    }

    public bool QuitarUltima()
    {
        if (Unidades.Count == 0) return false;
        Unidades.RemoveAt(Unidades.Count - 1);
        Aplicar();
        Guardar();
        return true;
    }

    /// <summary>What the splash prints. C: is always first because it is always there.</summary>
    public IEnumerable<string> LineasParaPantalla()
    {
        yield return "C: = carpeta del juego (y sus subcarpetas)";
        foreach (var u in Unidades)
            yield return $"{u.Letra}: = {Recortar(u.Ruta)}{(Directory.Exists(u.Ruta) ? "" : "   (no existe)")}";
    }

    private static string Recortar(string ruta) =>
        ruta.Length <= 48 ? ruta : "..." + ruta[^45..];
}
