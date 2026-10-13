namespace PakuPort.Core.Platform;

public sealed class ProgramExitException : Exception
{
    public byte ExitCode { get; }
    /// <summary>True when the program terminated via INT 21h AH=31 (TSR): its memory
    /// stays allocated and its file handles stay open (dos_execute.cpp DOS_Terminate).</summary>
    public bool Tsr { get; }
    public ProgramExitException(byte exitCode, bool tsr = false)
        : base($"Program exited with code {exitCode}{(tsr ? " (TSR)" : "")}")
    { ExitCode = exitCode; Tsr = tsr; }
}
