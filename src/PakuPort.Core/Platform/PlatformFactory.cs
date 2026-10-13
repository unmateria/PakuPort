namespace PakuPort.Core.Platform;

/// <summary>
/// Crea una plataforma DOS completa con todos los handlers registrados.
/// El host de render solo necesita: alimentar teclado/ratón y leer el framebuffer.
/// </summary>
public static class PlatformFactory
{
    /// <summary>
    /// Crea una DosPlatform con INT 10h (vídeo), INT 16h (teclado), INT 21h (ficheros),
    /// INT 33h (ratón) y puertos VGA/EGA. Devuelve también los handlers individuales
    /// para que el host pueda alimentar input (teclado, ratón).
    /// </summary>
    public static (DosPlatform platform, Int16hHandler keyboard, Int33hHandler mouse, Int1AhHandler timer)
        CreateFull(string gameDir, string? selfExe = null)
    {
        var platform = new DosPlatform();

        Int10hHandler.Register(platform);
        var keyboard = new Int16hHandler();
        keyboard.Register(platform);
        var dos = new Int21hHandler(gameDir) { SelfExePath = selfExe };
        dos.Register(platform);
        var mouse = new Int33hHandler();
        mouse.Register(platform);
        platform.Video.ModeChanged += mouse.OnVideoModeChanged;
        var timer = new Int1AhHandler();
        timer.Register(platform);
        VgaPortHandler.Register(platform);
        CgaPortHandler.Register(platform);
        BiosInterrupts.Register(platform);

        return (platform, keyboard, mouse, timer);
    }
}
