using System.Runtime.InteropServices;

namespace PakuPort.Core.Platform;

/// <summary>
/// The native Windows folder picker, by P/Invoke to shell32!SHBrowseForFolderW. No new
/// dependency: the published game is a self-contained exe and pulling in a UI framework just to
/// pick a folder would be a new thing that has to keep working for twenty years.
///
/// It runs on its OWN STA thread. The shell dialog is COM and demands a single-threaded
/// apartment, and raylib/GLFW owns the process' main thread: mixing the two is how a modal
/// dialog ends up not painting, or the game loop ends up blocked in a way that looks like a hang.
/// </summary>
public static class SelectorDeCarpeta
{
    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_EDITBOX = 0x0010;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;
    private const uint BIF_USENEWUI = BIF_NEWDIALOGSTYLE | BIF_EDITBOX;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo bi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, IntPtr pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    /// <summary>Opens the picker and returns the chosen folder, or null if cancelled or if this is
    /// not Windows. Blocks until the dialog closes.</summary>
    public static string? Elegir(string titulo)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? elegida = null;
        var hilo = new Thread(() =>
        {
            IntPtr pidl = IntPtr.Zero;
            IntPtr display = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            bool ole = false;
            try
            {
                ole = OleInitialize(IntPtr.Zero) >= 0;   // BIF_NEWDIALOGSTYLE needs OLE on this thread
                display = Marshal.AllocHGlobal(520);     // MAX_PATH wide chars
                var bi = new BrowseInfo
                {
                    hwndOwner = IntPtr.Zero,
                    pidlRoot = IntPtr.Zero,
                    pszDisplayName = display,
                    lpszTitle = titulo,
                    ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI,
                    lpfn = IntPtr.Zero,
                    lParam = IntPtr.Zero,
                    iImage = 0,
                };
                pidl = SHBrowseForFolderW(ref bi);
                if (pidl != IntPtr.Zero)
                {
                    buffer = Marshal.AllocHGlobal(520);
                    if (SHGetPathFromIDListW(pidl, buffer))
                        elegida = Marshal.PtrToStringUni(buffer);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[DISCOS] selector de carpeta: " + ex.Message);
            }
            finally
            {
                if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
                if (display != IntPtr.Zero) Marshal.FreeHGlobal(display);
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (ole) OleUninitialize();
            }
        });
        hilo.SetApartmentState(ApartmentState.STA);
        hilo.Start();
        hilo.Join();
        return string.IsNullOrWhiteSpace(elegida) ? null : elegida;
    }
}
