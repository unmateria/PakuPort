// JUEZ DEL SISTEMA DE FICHEROS DOS: directorio actual, nombres 8.3 y findfirst con RUTA.
//
// Por que existe: el `Int21hHandler` servia _gameDir y solo _gameDir. `AH=3B` (chdir) era un
// no-op que decia OK, `AH=47` (getcwd) contestaba siempre raiz, `FindFirst` tiraba la RUTA del
// patron y listaba siempre _gameDir, `*.*` se traducia a la regex `^.*\..*$` (que NO casa un
// nombre sin extension, asi que las carpetas `S` e `I` no aparecian), y el nombre del DTA era
// el del host truncado a 12 caracteres (`tracker-song_long.s3m` -> `TRACKER-SONG`, que no es
// 8.3 y luego no se puede abrir). Lo destapo un tracker de musica en su dialogo de carga, pero es
// de CLASE: cualquier juego con partidas en subcarpeta o findfirst con ruta lo sufre.
//
// La semantica portada es la de DOSBox-X (fichero y linea citados en el codigo del nucleo):
//   src/dos/dos_files.cpp  DOS_MakeName / DOS_ChangeDir / DOS_GetCurrentDir / DOS_FindFirst / DOS_FindNext
//   src/dos/drives.cpp     WildFileCmp
//   src/dos/drive_cache.cpp CreateShortName / CreateShortNameID / filename_not_8x3 / SortByName
//   src/dos/drive_local.cpp localDrive::FindFirst / localDrive::FindNext
//   src/dos/dos_classes.cpp DOS_DTA::SetupSearch / GetSearchParams / SetResult
//
// Uso:  dotnet run -c Release --project tools\dosfs_test
using PakuPort.Core;
using PakuPort.Core.Platform;

int fallos = 0;

void Caso(string nombre, bool ok, string detalle)
{
    Console.WriteLine($"[{(ok ? "VERDE" : "ROJO ")}] {nombre}: {detalle}");
    if (!ok) fallos++;
}

// ---------------------------------------------------------------- disco de pruebas
string raiz = Path.Combine(Path.GetTempPath(), "pakuport-dosfs-" + Guid.NewGuid().ToString("N")[..8]);
string raiz2 = raiz + "-d";
Directory.CreateDirectory(raiz);
byte[] contenidoLargo = System.Text.Encoding.ASCII.GetBytes("SCRM-TRACKER-SONG-LONG");
File.WriteAllBytes(Path.Combine(raiz, "tracker-song_long.s3m"), contenidoLargo);
File.WriteAllText(Path.Combine(raiz, "LONG FILE NAME.S3M"), "largo");
File.WriteAllText(Path.Combine(raiz, "a.b.c.txt"), "puntos");
File.WriteAllText(Path.Combine(raiz, "NORMAL.MOD"), "normal");
Directory.CreateDirectory(Path.Combine(raiz, "S"));
byte[] contenidoSong = System.Text.Encoding.ASCII.GetBytes("SONG-EN-LA-SUBCARPETA");
File.WriteAllBytes(Path.Combine(raiz, "S", "SONG.S3M"), contenidoSong);
Directory.CreateDirectory(Path.Combine(raiz, "I"));
Directory.CreateDirectory(Path.Combine(raiz, "NOEXT"));

// Segunda unidad (fase 2): PAKU_DISCO_D
Directory.CreateDirectory(raiz2);
Directory.CreateDirectory(Path.Combine(raiz2, "X"));
byte[] contenidoY = System.Text.Encoding.ASCII.GetBytes("Y-EN-LA-UNIDAD-D");
File.WriteAllBytes(Path.Combine(raiz2, "X", "Y.S3M"), contenidoY);
File.WriteAllText(Path.Combine(raiz2, "Z.MOD"), "zeta");
Environment.SetEnvironmentVariable("PAKU_DISCO_D", raiz2);

// ---------------------------------------------------------------- maquina minima
var platform = new DosPlatform();
var dos = new Int21hHandler(raiz);
dos.Register(platform);
var mem = new Memory();
platform.AttachMemory(mem);
var cpu = new Cpu();

const ushort SEG_STR = 0x1000;   // cadenas ASCIIZ de entrada
const ushort SEG_DTA1 = 0x2000;
const ushort SEG_DTA2 = 0x2200;
const ushort SEG_BUF = 0x3000;   // buffers de salida (getcwd, read)

void PonCadena(ushort seg, ushort off, string s)
{
    int a = Memory.Lin(seg, off);
    for (int i = 0; i < s.Length; i++) mem.Raw[a + i] = (byte)s[i];
    mem.Raw[a + s.Length] = 0;
}

string LeeCadena(ushort seg, ushort off)
{
    int a = Memory.Lin(seg, off);
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < 260 && mem.Raw[a + i] != 0; i++) sb.Append((char)mem.Raw[a + i]);
    return sb.ToString();
}

void Int21() => platform.Int(0x21, cpu, mem);

void PonDta(ushort seg, ushort off)
{
    cpu.AH = 0x1A; cpu.DS = seg; cpu.DX = off; Int21();
}

bool FindFirst(string patron, ushort attr)
{
    PonCadena(SEG_STR, 0, patron);
    cpu.AH = 0x4E; cpu.DS = SEG_STR; cpu.DX = 0; cpu.CX = attr; cpu.CF = false;
    Int21();
    return !cpu.CF;
}

bool FindNext()
{
    cpu.AH = 0x4F; cpu.CF = false;
    Int21();
    return !cpu.CF;
}

(string Nombre, byte Attr) LeeDta(ushort seg, ushort off)
{
    int d = Memory.Lin(seg, off);
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < 13 && mem.Raw[d + 0x1E + i] != 0; i++) sb.Append((char)mem.Raw[d + 0x1E + i]);
    return (sb.ToString(), mem.Raw[d + 0x15]);
}

// Barrido completo de una busqueda con un DTA propio.
List<(string Nombre, byte Attr)> Buscar(string patron, ushort attr, ushort dtaSeg = SEG_DTA1)
{
    var res = new List<(string, byte)>();
    PonDta(dtaSeg, 0);
    if (!FindFirst(patron, attr)) return res;
    res.Add(LeeDta(dtaSeg, 0));
    while (FindNext()) res.Add(LeeDta(dtaSeg, 0));
    return res;
}

int Abrir(string nombre)
{
    PonCadena(SEG_STR, 0, nombre);
    cpu.AH = 0x3D; cpu.AL = 0; cpu.DS = SEG_STR; cpu.DX = 0; cpu.CF = false;
    Int21();
    return cpu.CF ? -1 : cpu.AX;
}

byte[] Leer(int handle, int n)
{
    cpu.AH = 0x3F; cpu.BX = (ushort)handle; cpu.CX = (ushort)n;
    cpu.DS = SEG_BUF; cpu.DX = 0; cpu.CF = false;
    Int21();
    if (cpu.CF) return Array.Empty<byte>();
    int leidos = cpu.AX;
    var b = new byte[leidos];
    Array.Copy(mem.Raw, Memory.Lin(SEG_BUF, 0), b, 0, leidos);
    return b;
}

void Cerrar(int handle) { cpu.AH = 0x3E; cpu.BX = (ushort)handle; Int21(); }

(bool Ok, ushort Ax) Chdir(string ruta)
{
    PonCadena(SEG_STR, 0, ruta);
    cpu.AH = 0x3B; cpu.DS = SEG_STR; cpu.DX = 0; cpu.CF = false;
    Int21();
    return (!cpu.CF, cpu.AX);
}

string Getcwd(byte unidad = 0)
{
    int a = Memory.Lin(SEG_BUF, 0x100);
    for (int i = 0; i < 80; i++) mem.Raw[a + i] = 0xCC;   // veneno: si nadie escribe, se nota
    cpu.AH = 0x47; cpu.DL = unidad; cpu.DS = SEG_BUF; cpu.SI = 0x100; cpu.CF = false;
    Int21();
    if (cpu.CF) return "<CF>";
    return LeeCadena(SEG_BUF, 0x100);
}

string Lista(List<(string Nombre, byte Attr)> l) => string.Join(",", l.Select(x => x.Nombre));

// ---------------------------------------------------------------- (a) atributos de busqueda
{
    var todo = Buscar("*.*", 0x10);
    var esperadoTodo = new[] { ".", "..", "A~1.TXT", "I", "LONGFI~1.S3M", "NOEXT", "NORMAL.MOD", "S", "TRACKE~1.S3M" };
    // La raiz de una unidad NO tiene "." ni ".." en DOS real, asi que se esperan 7 entradas.
    var esperado = esperadoTodo.Where(n => n != "." && n != "..").ToArray();
    bool okDirs = Lista(todo) == string.Join(",", esperado);
    Caso("a1) findfirst *.* CX=0x10 lista las 3 carpetas y los 4 ficheros",
         okDirs, $"{todo.Count} entradas: {Lista(todo)}");

    var soloFich = Buscar("*.*", 0x00);
    var esperadoFich = new[] { "A~1.TXT", "LONGFI~1.S3M", "NORMAL.MOD", "TRACKE~1.S3M" };
    Caso("a2) findfirst *.* CX=0 lista SOLO los 4 ficheros",
         Lista(soloFich) == string.Join(",", esperadoFich), $"{soloFich.Count} entradas: {Lista(soloFich)}");
}

// ---------------------------------------------------------------- (b) nombres 8.3
// Los esperados salen de aplicar A MANO CreateShortName (drive_cache.cpp:587):
//  * "tracker-song_long.s3m" -> mayusculas, sin espacios; parte antes del primer '.' = 17 > 8
//    => hay que acortar. shortNr=1 ("1", 1 digito), 17+1+1 > 8 => se copian 8-1-1 = 6 caracteres
//    "TRACKE", + "~1", + la extension FINAL recortada a 4 (".S3M")  ->  TRACKE~1.S3M
//  * "LONG FILE NAME.S3M" -> RemoveSpaces (drive_cache.cpp:576) => "LONGFILENAME.S3M", 12 > 8
//    => 6 caracteres "LONGFI" + "~1" + ".S3M"  ->  LONGFI~1.S3M
//  * "a.b.c.txt" -> "A.B.C.TXT"; `len` es lo que hay ANTES del PRIMER punto = 1 ("A"), y como
//    1+1+1 <= 8 se copia entero: "A" + "~1" + la extension FINAL ".TXT"  ->  A~1.TXT
//    (por eso NO es "AB~1.TXT": DOSBox no junta los trozos intermedios, solo usa el primer
//    tramo como nombre y strrchr para la extension — drive_cache.cpp:659-668)
//  * "NORMAL.MOD" ya es 8.3 valido: filename_not_8x3 dice false y se queda igual.
{
    var todo = Buscar("*.*", 0x10);
    var nombres = todo.Select(x => x.Nombre).ToHashSet();
    string[] esperados = { "TRACKE~1.S3M", "LONGFI~1.S3M", "A~1.TXT", "NORMAL.MOD" };
    var faltan = esperados.Where(e => !nombres.Contains(e)).ToArray();
    Caso("b) los nombres del DTA son 8.3 de DOSBox (TRACKE~1.S3M, LONGFI~1.S3M, A~1.TXT, NORMAL.MOD)",
         faltan.Length == 0, faltan.Length == 0 ? "los cuatro" : "faltan: " + string.Join(",", faltan));

    bool todos83 = todo.All(x =>
    {
        string n = x.Nombre;
        if (n == "." || n == "..") return true;
        int p = n.IndexOf('.');
        string b = p < 0 ? n : n[..p], e = p < 0 ? "" : n[(p + 1)..];
        return b.Length is > 0 and <= 8 && e.Length <= 3 && !e.Contains('.')
               && n == n.ToUpperInvariant();
    });
    Caso("b2) TODOS los nombres del DTA son 8.3 validos y en mayusculas", todos83, Lista(todo));
}

// ---------------------------------------------------------------- (c) abrir por nombre corto
{
    int h = Abrir("TRACKE~1.S3M");
    byte[] leido = h >= 0 ? Leer(h, contenidoLargo.Length) : Array.Empty<byte>();
    if (h >= 0) Cerrar(h);
    Caso("c) open del nombre CORTO TRACKE~1.S3M abre el fichero largo real",
         h >= 0 && leido.SequenceEqual(contenidoLargo),
         h < 0 ? "open fallo" : $"h={h} bytes='{System.Text.Encoding.ASCII.GetString(leido)}'");
}

// ---------------------------------------------------------------- (d) directorio actual
{
    var (ok1, _) = Chdir("S");
    string cwd1 = Getcwd();
    var enS = Buscar("*.S3M", 0x00);
    int h = Abrir("SONG.S3M");
    byte[] leido = h >= 0 ? Leer(h, contenidoSong.Length) : Array.Empty<byte>();
    if (h >= 0) Cerrar(h);
    Caso("d1) chdir S -> getcwd = 'S'", ok1 && cwd1 == "S", $"ok={ok1} cwd='{cwd1}'");
    Caso("d2) en S, findfirst *.S3M -> SONG.S3M", Lista(enS) == "SONG.S3M", Lista(enS));
    Caso("d3) en S, open SONG.S3M funciona", h >= 0 && leido.SequenceEqual(contenidoSong),
         h < 0 ? "open fallo" : $"h={h} '{System.Text.Encoding.ASCII.GetString(leido)}'");

    var (okNo, axNo) = Chdir("NOEXISTE");
    string cwdNo = Getcwd();
    Caso("d4) chdir NOEXISTE -> CF=1 AX=3 y el cwd NO cambia", !okNo && axNo == 3 && cwdNo == "S",
         $"ok={okNo} ax={axNo} cwd='{cwdNo}'");

    var (ok2, _) = Chdir("..");
    string cwd2 = Getcwd();
    Caso("d5) chdir .. vuelve a la raiz", ok2 && cwd2 == "", $"ok={ok2} cwd='{cwd2}'");
}

// ---------------------------------------------------------------- (e) findfirst con RUTA
{
    var conRuta = Buscar("S\\*.S3M", 0x00);
    Caso("e1) desde la raiz, findfirst S\\*.S3M -> SONG.S3M", Lista(conRuta) == "SONG.S3M", Lista(conRuta));

    var subdir = Buscar("\\S\\*.*", 0x10);
    Caso("e2) \\S\\*.* con CX=0x10 -> '.', '..' y SONG.S3M",
         Lista(subdir) == ".,..,SONG.S3M", Lista(subdir));
}

// ---------------------------------------------------------------- (f) busquedas entrelazadas
{
    PonDta(SEG_DTA1, 0);
    bool a1 = FindFirst("*.S3M", 0x00);
    var n1 = LeeDta(SEG_DTA1, 0).Nombre;

    PonDta(SEG_DTA2, 0);
    bool b1 = FindFirst("*.MOD", 0x00);
    var m1 = LeeDta(SEG_DTA2, 0).Nombre;

    PonDta(SEG_DTA1, 0);
    bool a2 = FindNext();
    var n2 = LeeDta(SEG_DTA1, 0).Nombre;

    PonDta(SEG_DTA2, 0);
    bool b2 = FindNext();

    // *.S3M en la raiz = LONGFI~1.S3M y TRACKE~1.S3M (ordenados por nombre corto).
    bool ok = a1 && b1 && a2 && !b2 && n1 == "LONGFI~1.S3M" && m1 == "NORMAL.MOD" && n2 == "TRACKE~1.S3M";
    Caso("f) dos busquedas con DTAs distintos (AH=1A entre llamadas) no se pisan",
         ok, $"S3M: {n1} -> {n2} (2o findnext ok={a2}) | MOD: {m1} (findnext ok={b2})");
}

// ---------------------------------------------------------------- (g) el comodin
{
    var s3m = Buscar("*.S3M", 0x10);
    Caso("g1) *.S3M no devuelve NORMAL.MOD ni carpetas",
         Lista(s3m) == "LONGFI~1.S3M,TRACKE~1.S3M", Lista(s3m));

    // "*" SIN punto: DOSBox-X lo guarda en el DTA como spname="*" y spext vacio
    // (dos_classes.cpp:461 SetupSearch) y WildFileCmp (drives.cpp:60) exige entonces que la
    // extension del fichero tambien sea vacia -> solo casan los nombres SIN extension.
    // Es el mismo comportamiento del parse de nombre de DOS (el '*' rellena de '?' SOLO el
    // campo en el que aparece; el campo extension se queda en blancos).
    var estrella = Buscar("*", 0x10);
    Caso("g2) '*' (sin punto) casa como en DOS: solo lo que NO tiene extension",
         Lista(estrella) == "I,NOEXT,S", Lista(estrella));
}

// ---------------------------------------------------------------- (h) unidades adicionales
// PAKU_DISCO_D apunta a un segundo directorio del host con X\Y.S3M y Z.MOD.
{
    byte SeleccionaUnidad(byte dl) { cpu.AH = 0x0E; cpu.DL = dl; Int21(); return cpu.AL; }
    byte UnidadActual() { cpu.AH = 0x19; Int21(); return cpu.AL; }
    (bool Ok, ushort Ax) Espacio(byte dl) { cpu.AH = 0x36; cpu.DL = dl; cpu.CF = false; Int21(); return (!cpu.CF, cpu.AX); }

    byte nUnidades = SeleccionaUnidad(3);      // DL=3 -> D:
    byte actual = UnidadActual();
    Caso("h1) AH=0E a D: devuelve >=4 unidades y AH=19 contesta 3 (D:)",
         nUnidades >= 4 && actual == 3, $"AL(0E)={nUnidades} AL(19)={actual}");

    var enD = Buscar("*.*", 0x10);
    Caso("h2) en D:, findfirst *.* lista lo de la segunda carpeta", Lista(enD) == "X,Z.MOD", Lista(enD));

    var (okX, _) = Chdir("X");
    string cwdD = Getcwd(4);
    string cwdC = Getcwd(3);
    Caso("h3) chdir X en D: -> getcwd(DL=4)='X' y el cwd de C: no se toca",
         okX && cwdD == "X" && cwdC == "", $"ok={okX} D:'{cwdD}' C:'{cwdC}'");

    SeleccionaUnidad(2);
    int hd = Abrir("D:\\X\\Y.S3M");
    byte[] leidoD = hd >= 0 ? Leer(hd, contenidoY.Length) : Array.Empty<byte>();
    if (hd >= 0) Cerrar(hd);
    Caso("h4) desde C:, open D:\\X\\Y.S3M funciona y NO cambia de unidad",
         hd >= 0 && leidoD.SequenceEqual(contenidoY) && UnidadActual() == 2,
         hd < 0 ? "open fallo" : $"h={hd} '{System.Text.Encoding.ASCII.GetString(leidoD)}' unidad={UnidadActual()}");

    var (okRaizD, _) = Chdir("D:\\");
    var mod = Buscar("D:*.MOD", 0x00);
    Caso("h5) chdir D:\\ desde C: cambia el cwd de D sin cambiar la unidad actual, y D:*.MOD -> Z.MOD",
         okRaizD && UnidadActual() == 2 && Getcwd(4) == "" && Lista(mod) == "Z.MOD",
         $"ok={okRaizD} unidad={UnidadActual()} cwdD='{Getcwd(4)}' -> {Lista(mod)}");

    SeleccionaUnidad(7);   // H:, sin montar
    Caso("h6) AH=0E a una letra no montada no cambia la unidad actual", UnidadActual() == 2,
         $"unidad={UnidadActual()}");

    var (okEsp, axEsp) = Espacio(9);   // H: (DL=9 -> I:), sin montar
    Caso("h7) AH=36 de una unidad no montada devuelve error (AX=0xFFFF)",
         !okEsp && axEsp == 0xFFFF, $"ok={okEsp} ax={axEsp:X4}");

    SeleccionaUnidad(2);
}

// ---------------------------------------------------------------- limpieza
try { Directory.Delete(raiz, true); } catch { }
try { Directory.Delete(raiz2, true); } catch { }

Console.WriteLine();
Console.WriteLine(fallos == 0 ? "TODO VERDE" : $"{fallos} CASO(S) EN ROJO");
return fallos == 0 ? 0 : 1;
