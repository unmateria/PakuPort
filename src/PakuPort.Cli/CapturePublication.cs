/// <summary>
/// `remake capture` records into an attempt directory (`remake/intentos/&lt;stamp&gt;`) and only
/// publishes the tape and its telemetry TOGETHER, once the run ended well. Before, a failed run
/// left the previous tape paired with the new traces, and study read a capture that never existed.
/// A published attempt is deleted (its tape and traces now live in `remake/`); a failed one is
/// kept for diagnosis, and only the most recent <see cref="KeepFailed"/> of those.
/// </summary>
internal static class CapturePublication
{
    public const int KeepFailed = 3;

    public static void Publish(string attempt, string remake)
    {
        string tape = Path.Combine(attempt, "cinta.tape");
        string capture = Path.Combine(attempt, "captura");
        if (!File.Exists(tape) || new FileInfo(tape).Length == 0 || !Directory.Exists(capture))
            throw new IOException("El intento no contiene cinta y telemetría completas.");

        string finalTape = Path.Combine(remake, "cinta.tape");
        string finalCapture = Path.Combine(remake, "captura");
        // The previous pair is parked inside the attempt only while publishing, for the rollback.
        string backup = Path.Combine(attempt, "anterior");
        if (Directory.Exists(backup)) throw new IOException("Este intento ya tiene una publicación a medias: revísalo a mano.");
        Directory.CreateDirectory(backup);
        string oldTape = Path.Combine(backup, "cinta.tape");
        string oldCapture = Path.Combine(backup, "captura");
        bool movedCapture = false;
        try
        {
            if (File.Exists(finalTape)) File.Move(finalTape, oldTape);
            if (Directory.Exists(finalCapture)) Directory.Move(finalCapture, oldCapture);
            Directory.Move(capture, finalCapture);
            movedCapture = true;
            File.Move(tape, finalTape);              // last step: nothing can fail after it
        }
        catch
        {
            if (movedCapture) Directory.Move(finalCapture, capture);
            if (Directory.Exists(oldCapture)) Directory.Move(oldCapture, finalCapture);
            if (File.Exists(oldTape)) File.Move(oldTape, finalTape);
            Directory.Delete(backup, true);
            throw;
        }
        Directory.Delete(attempt, true);
    }

    /// <summary>Deletes all but the most recent <paramref name="keep"/> attempts (names start with a
    /// sortable UTC stamp).</summary>
    public static void Prune(string attempts, int keep = KeepFailed)
    {
        if (!Directory.Exists(attempts)) return;
        foreach (var old in Directory.GetDirectories(attempts).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(keep))
            Directory.Delete(old, true);
    }
}
