namespace PakuPort.Core.Platform;

/// <summary>
/// Central switch for platform-layer diagnostics. Off by default so release
/// runs (and the byte-exact judge) never pay for stderr noise or hot-path
/// instrumentation. Enable with the PAKU_PLATFORM_TRACE=1 environment variable
/// or by setting <see cref="Trace"/> directly from a host.
/// </summary>
public static class PlatformDebug
{
    public static bool Trace =
        System.Environment.GetEnvironmentVariable("PAKU_PLATFORM_TRACE") == "1";

    public static void Log(string message)
    {
        if (Trace) System.Console.Error.WriteLine("  [plat] " + message);
    }
}
