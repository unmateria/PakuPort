namespace PakuPort.Core.Platform;

/// <summary>
/// The DOS file system: drives mounted on host folders, a current directory PER DRIVE, DOS 8.3
/// short names for host files whose names are not 8.3, and the findfirst/findnext engine.
///
/// Ported from DOSBox-X (source tree of DOSBox-X):
///   src/dos/dos_files.cpp    DOS_MakeName:226, DOS_GetCurrentDir:490, DOS_ChangeDir:520,
///                            DOS_FindFirst:687, DOS_FindNext:782
///   src/dos/drives.cpp       WildFileCmp:60
///   src/dos/drive_cache.cpp  CreateShortName:587, CreateShortNameID:435, CompareShortname:403,
///                            RemoveSpaces:576, RemoveTrailingDot:459, SortByName:46
///   src/dos/drive_fat.cpp    filename_not_8x3:99
///   src/dos/drive_local.cpp  localDrive::FindFirst:1765, localDrive::FindNext:1836
///   src/dos/dos_programs.cpp default volume label "&lt;LETTER&gt;_DRIVE":1786
///
/// Why it exists: Int21hHandler used to serve _gameDir and only _gameDir — chdir was a no-op,
/// getcwd always answered "root", findfirst threw away the PATH of the pattern, `*.*` was
/// translated to the regex `^.*\..*$` (which does NOT match an extension-less name, so
/// subdirectories like `S` and `I` never showed up) and the DTA name was the host name cut to
/// 12 characters (`a-long-host-name.s3m` -> `A-LONG-HOST-`, which is not 8.3 and cannot be
/// opened afterwards). A music tracker's load dialog exposed it, but it is a CLASS bug: every
/// game with saves in a subfolder or a findfirst with a path suffers it.
/// </summary>
public sealed class DosFileSystem
{
    public const byte AttrReadOnly = 0x01;
    public const byte AttrHidden = 0x02;
    public const byte AttrSystem = 0x04;
    public const byte AttrVolume = 0x08;
    public const byte AttrDirectory = 0x10;
    public const byte AttrArchive = 0x20;

    /// <summary>One mounted drive. `Cwd` is a DOS path relative to `HostRoot` ("" = root),
    /// already in 8.3 uppercase form — DOSBox-X keeps the same in Drives[]->curdir.</summary>
    public sealed class Drive
    {
        public required string HostRoot;
        public string Cwd = "";
        public required string Label;
    }

    /// <summary>One entry of a host directory as DOS sees it.</summary>
    public sealed class Entry
    {
        public required string HostName;   // real name on the host
        public required string HostPath;
        public required string ShortName;  // uppercase 8.3, what the DTA carries
        public bool IsDir;
        public int ShortNr;                // the N of ~N, 0 when no short name was needed
        public byte Attr;
        public long Size;
        public DateTime Mtime;
    }

    private readonly Drive?[] _drives = new Drive?[26];

    /// <summary>Current drive, 0 = A. C: is the game directory, like every PakuPort export.</summary>
    public byte CurrentDrive = 2;

    public DosFileSystem(string gameDir) => Mount(2, gameDir);

    private bool _entornoLeido;

    /// <summary>The extra drives are read the FIRST time the guest touches the file system, not in
    /// the constructor: in a published game the machine is built BEFORE the splash, and the splash
    /// is where the player adds a folder as a drive (Platform/DiscosExtra.cs). Reading them early
    /// would mean a drive added there only worked on the NEXT run.</summary>
    private void EnsureDrives()
    {
        if (_entornoLeido) return;
        _entornoLeido = true;
        MountFromEnvironment();
    }

    /// <summary>
    /// PALANCA: <c>PAKU_DISCO_&lt;LETRA&gt;=&lt;ruta del host&gt;</c> (letters D..Z) mounts a host
    /// folder as that DOS drive. C: is always the game directory. Example:
    /// <c>PAKU_DISCO_D=C:\ruta\modulos</c>. The generated host also fills these variables from
    /// a <c>DISCOS.txt</c> file next to the exe (one <c>D=C:\ruta</c> per line), so the player never
    /// has to touch environment variables.
    /// </summary>
    public void MountFromEnvironment()
    {
        for (int d = 3; d < 26; d++)   // D..Z; A/B/C are not configurable
        {
            string? path = Environment.GetEnvironmentVariable($"PAKU_DISCO_{(char)('A' + d)}");
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Mount((byte)d, path);
        }
    }

    public void Mount(byte drive, string hostRoot)
    {
        if (drive >= 26) return;
        _drives[drive] = new Drive
        {
            HostRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostRoot)),
            Label = $"{(char)('A' + drive)}_DRIVE",   // dos_programs.cpp:1786
        };
    }

    public Drive? GetDrive(byte drive)
    {
        EnsureDrives();
        return drive < 26 ? _drives[drive] : null;
    }
    public bool IsMounted(byte drive) => GetDrive(drive) != null;

    /// <summary>Number of logical drives, what AH=0E returns in AL (dos.cpp DOS_SetDrive).
    /// DOSBox-X answers DOS_DRIVES (26); we answer the highest mounted letter, with a floor of
    /// 3 so that a plain C:-only game keeps seeing at least A, B and C.</summary>
    public byte DriveCount
    {
        get
        {
            EnsureDrives();
            int last = 2;
            for (int d = 0; d < 26; d++) if (_drives[d] != null) last = d;
            return (byte)(last + 1);
        }
    }

    // ------------------------------------------------------------------ path resolution

    /// <summary>Result of resolving a DOS path against the drive table and the host folder.</summary>
    public sealed class ResolvedPath
    {
        public byte Drive;
        public bool DriveMounted;
        public string DosDir = "";     // 8.3 uppercase, relative to the drive root, no leading '\'
        public string HostDir = "";    // host folder the path lives in
        public string Leaf = "";       // last component as given (may hold wildcards); "" for a pure directory path
        public string HostPath = "";   // HostDir + resolved leaf (raw leaf when it does not exist)
        public bool DirExists;         // every directory component of the path exists
        public bool HadTrailingSlash;
    }

    /// <summary>
    /// DOS_MakeName (dos_files.cpp:226) plus the per-component lookup DOSBox-X does through its
    /// directory cache: exact host name, case-insensitive host name, or generated 8.3 short name.
    /// Relative paths hang from the drive's current directory, absolute ones ('\') from its root.
    /// </summary>
    public ResolvedPath Resolve(string dosPath)
    {
        EnsureDrives();
        var r = new ResolvedPath();
        string p = dosPath.Replace('/', '\\').Trim();

        r.Drive = CurrentDrive;
        if (p.Length >= 2 && p[1] == ':')
        {
            char c = char.ToUpperInvariant(p[0]);
            if (c is >= 'A' and <= 'Z') r.Drive = (byte)(c - 'A');
            p = p[2..];
        }
        var drive = GetDrive(r.Drive);
        r.DriveMounted = drive != null;
        if (drive == null)
        {
            // A: and B: are the only letters PakuPort never mounts, and a rip that still names
            // its original floppy ("A:\SETUP.DAT") used to resolve against the game folder,
            // because there was no drive table at all. That is kept: a path on A:/B: falls back
            // to the CURRENT drive. C:..Z: do NOT fall back -- once a letter can be mounted,
            // silently serving another drive's file would hide a wrong DISCOS.txt.
            // The explicit drive queries (AH=0E/36/47, chdir) look at DriveMounted and say
            // "no such drive" either way.
            if (r.Drive > 1) return r;
            drive = GetDrive(CurrentDrive);
            if (drive == null) return r;
        }

        bool absolute = p.StartsWith('\\');
        r.HadTrailingSlash = p.Length > 0 && p.EndsWith('\\');

        var components = new List<string>();
        if (!absolute && drive.Cwd.Length > 0)
            components.AddRange(drive.Cwd.Split('\\', StringSplitOptions.RemoveEmptyEntries));

        string[] parts = p.Split('\\');
        string leaf = "";
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].TrimEnd(' ');
            if (part.Length == 0) continue;
            if (part == ".") continue;
            if (part == "..")
            {
                if (components.Count > 0) components.RemoveAt(components.Count - 1);
                continue;
            }
            if (i == parts.Length - 1) leaf = part;
            else components.Add(part);
        }
        r.Leaf = leaf;

        // Walk the directory chain, mapping each DOS component to its real host name.
        string hostDir = drive.HostRoot;
        var dosParts = new List<string>();
        r.DirExists = true;
        foreach (string comp in components)
        {
            var e = r.DirExists ? Lookup(hostDir, comp) : null;
            if (e != null && e.IsDir)
            {
                hostDir = e.HostPath;
                dosParts.Add(e.ShortName);
            }
            else
            {
                r.DirExists = false;
                hostDir = Path.Combine(hostDir, comp);
                dosParts.Add(comp.ToUpperInvariant());
            }
        }
        r.HostDir = hostDir;
        r.DosDir = string.Join('\\', dosParts);

        if (leaf.Length == 0) r.HostPath = hostDir;
        else
        {
            var le = r.DirExists ? Lookup(hostDir, leaf) : null;
            r.HostPath = le != null ? le.HostPath : Path.Combine(hostDir, leaf);
        }
        return r;
    }

    /// <summary>Host path for a DOS path, or the would-be path when it does not exist yet
    /// (create/rename need that). Never null.</summary>
    public string ResolveHostPath(string dosPath) => Resolve(dosPath).HostPath;

    /// <summary>One component: exact host name, case-insensitive host name, or short name.</summary>
    public Entry? Lookup(string hostDir, string name)
    {
        var list = List(hostDir);
        foreach (var e in list) if (string.Equals(e.HostName, name, StringComparison.Ordinal)) return e;
        foreach (var e in list) if (string.Equals(e.HostName, name, StringComparison.OrdinalIgnoreCase)) return e;
        foreach (var e in list) if (string.Equals(e.ShortName, name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    // ------------------------------------------------------------------ current directory

    /// <summary>DOS_ChangeDir (dos_files.cpp:520): resolve, check it is a directory, and only then
    /// move the drive's curdir. A trailing '\' is PATH_NOT_FOUND in DOS.</summary>
    public bool ChangeDir(string dosPath, out byte errorCode)
    {
        errorCode = 3; // DOSERR_PATH_NOT_FOUND
        string bare = dosPath.Replace('/', '\\');
        if (bare.Length >= 2 && bare[1] == ':') bare = bare[2..];
        if (bare.Trim().Length == 0) return false;

        var r = Resolve(dosPath);
        if (!r.DriveMounted) { errorCode = 15; return false; }   // DOSERR_INVALID_DRIVE
        if (r.HadTrailingSlash && (r.DosDir.Length > 0 || r.Leaf.Length > 0)) return false;
        if (!r.DirExists) return false;

        string newDosDir = r.DosDir;
        if (r.Leaf.Length > 0)
        {
            var e = Lookup(r.HostDir, r.Leaf);
            if (e == null || !e.IsDir) return false;
            newDosDir = newDosDir.Length > 0 ? newDosDir + "\\" + e.ShortName : e.ShortName;
        }
        GetDrive(r.Drive)!.Cwd = newDosDir;
        return true;
    }

    /// <summary>DOS_GetCurrentDir (dos_files.cpp:490): the path WITHOUT drive and WITHOUT the
    /// leading backslash, in 8.3 uppercase.</summary>
    public string? GetCurrentDir(byte drive)
    {
        var d = GetDrive(drive);
        return d?.Cwd;
    }

    // ------------------------------------------------------------------ directory listing + 8.3

    private readonly Dictionary<string, (DateTime Stamp, List<Entry> Items)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public void Invalidate(string hostDir) => _cache.Remove(hostDir);
    public void InvalidateAll() => _cache.Clear();

    /// <summary>Host directory as DOS sees it, sorted by short name (drive_cache.cpp SortByName:46).
    /// The short names are generated in host enumeration order, exactly like DOS_Drive_Cache::ReadDir
    /// (drive_cache.cpp:876) does before sorting, so the ~N numbering is the same.</summary>
    public List<Entry> List(string hostDir)
    {
        if (!Directory.Exists(hostDir)) return new List<Entry>();
        DateTime stamp;
        try { stamp = Directory.GetLastWriteTimeUtc(hostDir); } catch { stamp = DateTime.MinValue; }
        if (_cache.TryGetValue(hostDir, out var hit) && hit.Stamp == stamp) return hit.Items;

        var items = new List<Entry>();
        try
        {
            foreach (var fsi in new DirectoryInfo(hostDir).EnumerateFileSystemInfos())
            {
                bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                var e = new Entry
                {
                    HostName = fsi.Name,
                    HostPath = fsi.FullName,
                    ShortName = "",
                    IsDir = isDir,
                    // drive_local.cpp:1893 on Windows: find_attr |= attribs & 0x3f
                    Attr = (byte)((int)fsi.Attributes & 0x3F),
                    Size = isDir ? 0 : ((FileInfo)fsi).Length,
                    Mtime = fsi.LastWriteTime,
                };
                var (shortName, nr) = CreateShortName(fsi.Name, items);
                e.ShortName = shortName;
                e.ShortNr = nr;
                items.Add(e);
            }
        }
        catch { }
        items.Sort((a, b) => string.CompareOrdinal(a.ShortName, b.ShortName));
        _cache[hostDir] = (stamp, items);
        return items;
    }

    // Characters DOS refuses in a file name — drive_fat.cpp:107 (non-DBCS branch).
    private static bool InvalidChar(char c) =>
        c <= (char)32 || c == (char)127 || "\"+=,;:<>[]|\\?*".IndexOf(c) >= 0;

    /// <summary>filename_not_8x3 — drive_fat.cpp:99.</summary>
    public static bool FilenameNot8x3(string n)
    {
        int i = 0;
        while (i < n.Length && n[i] != '.')
        {
            if (InvalidChar(n[i])) return true;
            i++;
        }
        if (i == 0 || i > 8) return true;
        if (i == n.Length) return false;   // 8 or fewer normal chars and end of string: normal
        int ext = 0;
        for (int j = i + 1; j < n.Length; j++)
        {
            if (n[j] == '.') return true;  // another '.' means LFN
            if (InvalidChar(n[j])) return true;
            ext++;
        }
        return ext > 3;
    }

    /// <summary>RemoveTrailingDot — drive_cache.cpp:459.</summary>
    private static string RemoveTrailingDot(string s)
    {
        if (s.Length == 0 || s[^1] != '.') return s;
        if (s.Length == 1) return s;
        if (s.Length == 2 && s[0] == '.') return s;
        return s[..^1];
    }

    /// <summary>strncmp with C semantics (stops at the implicit NUL).</summary>
    private static int Strncmp(string a, string b, int n)
    {
        for (int i = 0; i < n; i++)
        {
            char ca = i < a.Length ? a[i] : '\0';
            char cb = i < b.Length ? b[i] : '\0';
            if (ca != cb) return ca - cb;
            if (ca == '\0') return 0;
        }
        return 0;
    }

    private static int Cspn(string s, char stop)
    {
        int i = s.IndexOf(stop);
        return i < 0 ? s.Length : i;
    }

    /// <summary>CompareShortname — drive_cache.cpp:403.</summary>
    private static int CompareShortname(string compareName, string shortName)
    {
        int tilde = shortName.IndexOf('~');
        if (tilde < 0) return string.CompareOrdinal(compareName, shortName);
        int compareCount1 = tilde;
        int numberSize = Cspn(shortName[tilde..], '.');
        int compareCount2 = Cspn(compareName, '.');
        if (compareCount2 > 8) compareCount2 = 8;
        if (compareCount2 > compareCount1 + numberSize) compareCount1 = compareCount2 - numberSize;
        return Strncmp(compareName, shortName, compareCount1);
    }

    /// <summary>CreateShortNameID — drive_cache.cpp:435. DOSBox binary-searches its list, which is
    /// kept sorted by short name, and takes the LAST entry whose short name compares equal; a
    /// linear pass over the entries created so far gives the same answer with less machinery.</summary>
    private static int CreateShortNameId(string name, List<Entry> created)
    {
        int foundNr = 0;
        foreach (var e in created)
            if (e.ShortNr > 0 && CompareShortname(name, e.ShortName) == 0) foundNr = e.ShortNr;
        return foundNr + 1;
    }

    /// <summary>
    /// CreateShortName — drive_cache.cpp:587. Returns the 8.3 name and the ~N number used (0 when
    /// the host name already was a valid 8.3 and is kept verbatim).
    /// </summary>
    public static (string ShortName, int ShortNr) CreateShortName(string orgName, List<Entry> created)
    {
        string tmp = orgName.ToUpperInvariant();

        // RemoveSpaces — drive_cache.cpp:576
        string noSpaces = tmp.Replace(" ", "");
        bool createShort = noSpaces.Length != tmp.Length;
        tmp = noSpaces;

        int len;
        int dot = tmp.IndexOf('.');
        if (dot >= 0)
        {
            string pos = tmp[dot..];
            // ignore preceding '.' if the extension is longer than "3"
            if (pos.Length > 4 || (pos.Length == tmp.Length && tmp != "." && tmp != ".."))
            {
                tmp = tmp.TrimStart('.');
                createShort = true;
            }
            dot = tmp.IndexOf('.');
            len = dot >= 0 ? dot : tmp.Length;
        }
        else len = tmp.Length;

        if (tmp != "." && tmp != ".." && FilenameNot8x3(tmp))
        {
            createShort = true;
            var chars = tmp.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (InvalidChar(chars[i])) chars[i] = '_';
            tmp = new string(chars);
        }

        createShort = createShort || len > 8;
        if (!createShort)
        {
            // GetLongName(curDir, tmp) >= 0: a previous entry already owns this short name.
            foreach (var e in created)
                if (string.Equals(e.ShortName, tmp, StringComparison.Ordinal)) { createShort = true; break; }
        }

        if (!createShort) return (RemoveTrailingDot(tmp), 0);

        int nr = CreateShortNameId(tmp, created);
        string num = nr.ToString();
        int toCopy = (len + num.Length + 1 > 8) ? 8 - num.Length - 1 : len;
        if (toCopy < 0) toCopy = 0;
        if (toCopy > tmp.Length) toCopy = tmp.Length;
        string sn = tmp[..toCopy] + "~" + num;
        int lastDot = tmp.LastIndexOf('.');
        if (lastDot >= 0)
        {
            string ext = tmp[lastDot..];
            sn += ext.Length > 4 ? ext[..4] : ext;   // strncat(shortname, pos, 4)
            if (sn.Length > 12) sn = sn[..12];       // shortname[DOS_NAMELENGTH] = 0
        }
        return (RemoveTrailingDot(sn), nr);
    }

    // ------------------------------------------------------------------ wildcards

    /// <summary>WildFileCmp — drives.cpp:60, verbatim (non-LFN branch). Name and extension are
    /// matched SEPARATELY over 8 and 3 slots, so `*.*` matches everything (including names with
    /// no extension) while a bare `*` only matches names WITHOUT extension — the DOS quirk that
    /// falls out of the '*' filling only the field it appears in.</summary>
    public static bool WildFileCmp(string file, string wild)
    {
        var fileName = new char[9];
        var fileExt = new char[4];
        var wildName = new char[10];
        var wildExt = new char[5];

        int findExt = file.LastIndexOf('.');
        if (findExt >= 0)
        {
            int size = Math.Min(findExt, 8);
            for (int i = 0; i < size; i++) fileName[i] = file[i];
            string ext = file[(findExt + 1)..];
            for (int i = 0; i < Math.Min(ext.Length, 3); i++) fileExt[i] = ext[i];
        }
        else for (int i = 0; i < Math.Min(file.Length, 8); i++) fileName[i] = file[i];

        findExt = wild.LastIndexOf('.');
        if (findExt >= 0)
        {
            int size = Math.Min(findExt, 9);
            for (int i = 0; i < size; i++) wildName[i] = wild[i];
            string ext = wild[(findExt + 1)..];
            for (int i = 0; i < Math.Min(ext.Length, 4); i++) wildExt[i] = ext[i];
        }
        else for (int i = 0; i < Math.Min(wild.Length, 9); i++) wildName[i] = wild[i];

        for (int i = 0; i < 9; i++) fileName[i] = char.ToUpperInvariant(fileName[i]);
        for (int i = 0; i < 4; i++) fileExt[i] = char.ToUpperInvariant(fileExt[i]);
        for (int i = 0; i < 10; i++) wildName[i] = char.ToUpperInvariant(wildName[i]);
        for (int i = 0; i < 5; i++) wildExt[i] = char.ToUpperInvariant(wildExt[i]);

        int r = 0;
        bool skipToExt = false;
        while (r < 8)
        {
            if (wildName[r] == '*') { skipToExt = true; break; }
            if (wildName[r] != '?' && wildName[r] != fileName[r]) return false;
            r++;
        }
        if (!skipToExt && wildName[8] != 0 && wildName[8] != '*') return false;

        r = 0;
        while (r < 3)
        {
            if (wildExt[r] == '*') return true;
            if (wildExt[r] != '?' && wildExt[r] != fileExt[r]) return false;
            r++;
        }
        return wildExt[3] == 0 || wildExt[3] == '*';
    }

    // ------------------------------------------------------------------ findfirst / findnext

    /// <summary>State of one open search, addressed by the id stored in the DTA at +0x0D — DOSBox-X
    /// keeps the same in localDrive::srchInfo[id] (drive_local.cpp:1790).</summary>
    public sealed class Search
    {
        public byte Drive;
        public string HostDir = "";
        public List<Entry> Items = new();
        public int Index;
    }

    private readonly Dictionary<ushort, Search> _searches = new();
    private ushort _nextSearchId;
    private const int MaxSearches = 256;

    /// <summary>Opens a search over the resolved directory. Returns null when the directory does
    /// not exist (DOSERR_PATH_NOT_FOUND).</summary>
    public Search? OpenSearch(ResolvedPath r, out ushort id)
    {
        id = 0;
        if (!r.DriveMounted || !r.DirExists || !Directory.Exists(r.HostDir)) return null;

        var items = new List<Entry>(List(r.HostDir));
        if (r.DosDir.Length > 0)
        {
            // "." and ".." exist in every DOS subdirectory (never in the root of a drive).
            var di = new DirectoryInfo(r.HostDir);
            items.Insert(0, new Entry { HostName = "..", HostPath = di.Parent?.FullName ?? r.HostDir, ShortName = "..", IsDir = true, Attr = AttrDirectory, Mtime = di.LastWriteTime });
            items.Insert(0, new Entry { HostName = ".", HostPath = r.HostDir, ShortName = ".", IsDir = true, Attr = AttrDirectory, Mtime = di.LastWriteTime });
        }

        if (_searches.Count >= MaxSearches) _searches.Clear();   // DOSBox resets when all slots are full
        id = _nextSearchId++;
        var s = new Search { Drive = r.Drive, HostDir = r.HostDir, Items = items, Index = 0 };
        _searches[id] = s;
        return s;
    }

    public Search? GetSearch(ushort id) => _searches.TryGetValue(id, out var s) ? s : null;

    /// <summary>Next entry that passes the pattern and the attribute mask, or null when the search
    /// is exhausted. localDrive::FindNext — drive_local.cpp:1836.</summary>
    public Entry? NextMatch(Search s, string pattern, byte searchAttr)
    {
        while (s.Index < s.Items.Count)
        {
            var e = s.Items[s.Index++];
            if (!WildFileCmp(e.ShortName, pattern) && !WildFileCmp(e.HostName, pattern)) continue;
            // drive_local.cpp:1907 — an entry whose attribute bits are not in the mask is skipped.
            byte find = (byte)(e.Attr & (AttrDirectory | AttrHidden | AttrSystem));
            if ((~searchAttr & find) != 0) continue;
            return e;
        }
        return null;
    }

    public string LabelOf(byte drive) => GetDrive(drive)?.Label ?? "";
}
