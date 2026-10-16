using System.Diagnostics;
using System.Text.RegularExpressions;
using PakuPort.Core;
using PakuPort.Core.Platform;
using PakuPort.Core.Verify;

// PakuPort CLI — comandos Spice86 (record/build/play) + pipeline estático legacy (verify/emit/decode/...).
// Comandos Spice86 (no necesitan game.json):
//   record <exe> [--game-dir <dir>] [--output <dir>] [--max-minutes <n>]
//   build  <dumps-dir> [--output <path.exe>] [--game-name <name>] [--spice86 <csproj>]
//   play   <exe> [--game-dir <dir>] [--output-dir <dir>]
//   doctor <dumps-dir>
//   credits
// Comandos legacy (necesitan --game <game.json>):
//   verify/emit/decode/crosscheck/sanity/render/run/survey/export/discover/init

if (args.Length < 1)
{
    PrintPlanDHelp();
    return 0;
}

string cmd = args[0];

// --help/-h en CUALQUIER posición corta con la ayuda: antes, 'pakuport export --help'
// ignoraba el flag y EJECUTABA el export del juego por defecto
if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
{
    PrintPlanDHelp();
    return 0;
}

// Spice86 commands: no necesitan game.json
switch (cmd)
{
    case "port":    return CmdPort(args[1..]);
    case "package": return CmdPackage(args[1..]);
    case "record":  return CmdRecord(args[1..]);
    case "build":   return CmdBuild(args[1..]);
    case "play":    return CmdPlay(args[1..]);
    case "doctor":  return CmdDoctor(args[1..]);
    // remake: va en ESTE switch y no en el de abajo, porque aquél da por hecho que el segundo
    // argumento es un nombre de función y exige un game.json — el kit resuelve lo suyo solo.
    case "remake":  return RemakeKit.Ejecutar(args[1..]);
    case "banco":   return BancoCiego.Ejecutar(args[1..]);
    case "corpus-diag": return CorpusDiag.Ejecutar(args[1..]);
    // expediente: los verbos que convierten el porteo en material para la IA que viene detras.
    case "expediente":
        if (args.Length > 1 && args[1] == "capas") return Capas.Ejecutar(args[2..]);
        if (args.Length > 1 && args[1] == "tablero") return Tablero.Ejecutar(args[2..]);
        Capas.Uso();
        return Tablero.Uso();
    case "credits": return CmdCredits();
    case "platform-test": return CmdPlatformTest(args[1..]);
    case "le-dump": return CmdLeDump(args[1..]);
    case "le-run": return CmdLeRun(args[1..]);
    case "--help": case "-h": case "help":
        PrintPlanDHelp();
        return 0;
}

// init es especial: no necesita game.json (lo crea)
if (cmd == "init") return CmdInit(args);

bool needsFunc = cmd is not ("survey" or "run" or "export" or "discover" or "dump-image" or "diag-red" or "interp-verify");
if (needsFunc && args.Length < 2)
{
    Console.WriteLine($"uso: pakuport {cmd} <FUN_1000_off> [--game <game.json>]");
    return 1;
}
// Both `<cmd> FUN_x --game g` and `<cmd> --game g --func FUN_x` (the form the board prints).
string func = needsFunc ? (GetOpt(args, "--func") ?? args[1]) : "";
string? gamePath = GetOpt(args, "--game");
if (gamePath is null)
{
    Console.Error.WriteLine($"{cmd}: falta --game <ruta a game.json>");
    return 1;
}
var game = GameConfig.Load(gamePath);

// The lifted-code judge reads functions at MZ file offsets. On a .COM (or an LE image) it decodes
// the wrong bytes and can even come out green on garbage, so it refuses instead of lying.
if (cmd is "verify" or "emit" or "decode" or "crosscheck" or "sanity" or "trace" or "render"
    && game.Format != "exe")
{
    Console.Error.WriteLine($"{cmd}: el juez del código lifteado sólo sabe leer ejecutables MZ (.EXE); " +
                            $"este juego es '{game.Format}'. Usa el banco: pakuport expediente tablero + pakuport banco juzga.");
    return 1;
}

switch (cmd)
{
    case "verify": return CmdVerify(game, func);
    case "emit": return CmdEmit(game, func);
    case "decode": return CmdDecode(game, func);
    case "crosscheck": return CmdCrosscheck(game, func);
    case "sanity": return CmdSanity(game, func);
    case "survey": return CmdSurvey(game);
    case "interp-verify": return CmdInterpVerify(game, gamePath, GetOpt(args, "--corpus"), HasFlag(args, "--strict"), HasFlag(args, "--registros"));
    case "oracle-run": return OracleRun.Ejecutar(game, func, gamePath,
        GetOpt(args, "--entrada"), GetOpt(args, "--base"), GetOpt(args, "--out"), GetOpt(args, "--pfc"));
    case "diag-red": return CmdDiagRed(game, GetOpt(args, "--out"));
    case "trace": return CmdTrace(game, func, int.Parse(GetOpt(args, "--case") ?? "0"));
    case "render": return CmdRender(game, func, GetOpt(args, "--case") ?? "0");
    case "run": return CmdRun(game, GetOpt(args, "--exe"), GetOpt(args, "--entry"), GetOpt(args, "--corpus"),
        bootReal: HasFlag(args, "--boot-real"), gamePath: gamePath, snapshot: HasFlag(args, "--snapshot"));
    case "export": return CmdExport(game, GetOpt(args, "--out") ?? Path.Combine("export", game.Id), gamePath, bootReal: HasFlag(args, "--boot-real"), seedsFile: GetOpt(args, "--seeds"), probeKeys: GetOpt(args, "--probe-keys"), portadasDir: GetOpt(args, "--portadas"));
    case "discover": return CmdDiscover(game, gamePath);
    case "dump-image": return CmdDumpImage(game, gamePath, GetOpt(args, "--output")
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gamePath))!, "explorer"));
    default:
        Console.Error.WriteLine($"comando desconocido: {cmd}");
        return 1;
}

// =====================================================================
// PLATFORM-TEST (barrido de plataforma): ejecuta la tabla tests.json contra la
// Platform real (PlatformFactory.CreateFull) y vuelca los registros de salida por
// caso, en el MISMO formato lógico que TEST.COM produce en el oráculo DOSBox
// El juez (judge.py) diffea ambos mundos.
// =====================================================================

/// <summary>Carga un ejecutable LE de 32 bits y vuelca la imagen plana resultante (fase 1 del camino LE).
///
/// Existe para que el juez del cargador (tools/le_loader/judge_le.py) mida EL CARGADOR DE
/// PRODUCCIÓN contra el volcado del oráculo, y no el de referencia en Python: si sólo se midiera
/// el de Python, el C# podría diverger sin que ninguna red se enterase.
///
/// <c>pakuport le-dump &lt;exe&gt; [--base 0x1E6000] [--out imagen.bin]</c>
/// </summary>
static int CmdLeDump(string[] rest)
{
    if (rest.Length == 0)
    {
        Console.Error.WriteLine("uso: pakuport le-dump <exe> [--base 0xHEX] [--out fichero.bin]");
        return 2;
    }
    string exePath = rest[0];
    string baseTxt = GetOpt(rest, "--base") ?? "0";
    uint loadBase = baseTxt.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt32(baseTxt[2..], 16)
        : uint.Parse(baseTxt);

    var le = LeLoader.Load(File.ReadAllBytes(exePath), loadBase);

    Console.WriteLine($"== LE: {Path.GetFileName(exePath)} ==");
    Console.WriteLine($"  base de carga: 0x{loadBase:X}");
    foreach (var ob in le.Objects)
        Console.WriteLine($"  obj{ob.Number} base=0x{ob.Base:X6} (el fichero pedía 0x{ob.FileBase:X6}) " +
                          $"vsize=0x{ob.VirtualSize:X} páginas={ob.PageCount} " +
                          $"{(ob.Readable ? "R" : "-")}{(ob.Writable ? "W" : "-")}{(ob.Executable ? "X" : "-")}" +
                          $"{(ob.Is32Bit ? " 32BIT" : "")}");
    Console.WriteLine($"  imagen: {le.Image.Length} bytes   EIP=0x{le.Eip:X8}  ESP=0x{le.Esp:X8}");
    Console.WriteLine($"  fixups aplicados: {le.FixupCount}");

    string? outPath = GetOpt(rest, "--out");
    if (outPath != null)
    {
        File.WriteAllBytes(outPath, le.Image);
        Console.WriteLine($"  -> {outPath}");
    }
    return 0;
}

/// <summary>Carga un LE y lo EJECUTA en el mundo plano de 32 bits (fase 1 del camino LE).
///
/// Criterio de la fase: "el juego ejecuta sus primeras N mil instrucciones sin salirse de su
/// propio código". Este comando lo mide: cuenta instrucciones, dice dónde y por qué paró, y
/// comprueba que el EIP no se ha ido fuera de los objetos del ejecutable.
///
/// <c>pakuport le-run &lt;exe&gt; [--insns 100000] [--base 0x10000]</c>
/// </summary>
static int CmdLeRun(string[] rest)
{
    if (rest.Length == 0)
    {
        Console.Error.WriteLine("uso: pakuport le-run <exe> [--insns N] [--base 0xHEX]");
        return 2;
    }
    string exePath = rest[0];
    long maxInsns = long.TryParse(GetOpt(rest, "--insns"), out var mi) ? mi : 100_000;
    // Por encima de 1 MiB, como hace el extensor de verdad (DOS/4GW dejo la imagen de un juego de prueba
    // en 0x1E6000, medido): asi el primer megabyte queda ENTERO para los buffers de DMA y el
    // juego deja de decir "Insufficient base RAM". Antes el defecto era 0x10000 porque con la
    // base alta afloraba una corrupcion sin diagnosticar; era el monton de DPMI pegado detras de
    // la imagen, donde el runtime de C del juego expande el suyo — arreglado en FlatServices.
    string baseTxt = GetOpt(rest, "--base") ?? "0x110000";
    uint loadBase = baseTxt.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt32(baseTxt[2..], 16) : uint.Parse(baseTxt);

    var le = LeLoader.Load(File.ReadAllBytes(exePath), loadBase);
    var mem = new Memory();
    mem.LoadFlatImage(le.Image);
    // Un extensor de verdad le da al programa un espacio lineal AMPLIO, no justo lo que ocupa su
    // imagen. El runtime de C hace crecer su monton justo detras de ella (se midio: leia y
    // escribia en 0x1961A8 con la imagen acabando en 0x18E000), y si esa memoria no existe las
    // lecturas devuelven 0xFF y las escrituras se pierden -- que es como la paleta acababa entera
    // en (63,63,63) y la pantalla en blanco. 16 MiB es de sobra y no cuesta nada hasta que se usa.
    mem.EnableFlat(16 * 1024 * 1024);
    // El hardware es UNO SOLO para los dos mundos: el juego de 32 bits programa la VGA a pelo
    // y tiene que ver los mismos registros que uno de 16 bits.
    var (plataforma, teclado, _, _) = PakuPort.Core.Platform.PlatformFactory.CreateFull(
        Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? ".");
    mem.Video = plataforma.Video;
    // `PlatformFactory.CreateFull` NO instala la Sound Blaster (eso lo hace el host
    // exportado desde el bloque `sb` del game.json). Sin ella un driver de sonido sondea los
    // puertos, no encuentra nada y el juego se planta -- que es lo que le pasaba a un juego de prueba.
    // Base/IRQ/DMA de fabrica, los mismos que usan los hosts exportados.
    // --sin-sb: control negativo. Si el juego se para esperando al sonido, sin tarjeta toma otro
    // camino (el driver del altavoz) y se ve enseguida si el muro es del audio o de otra cosa.
    if (!HasFlag(rest, "--sin-sb")) plataforma.InstallSoundBlaster(0x220, 7, 1, null);

    // El host de servicios: DPMI + el DOS que se llama desde 32 bits (fase 2 del camino LE).
    uint tope = le.Objects.Max(o => o.Base + o.VirtualSize);
    var svc = new PakuPort.Core.Platform.FlatServices(mem, tope,
        Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? ".");
    // `le-run` es una SONDA: se ejecuta una y otra vez explorando el arranque de un juego, y un
    // `AH=3C` del juego truncaría el fichero real. Nada de tocar los datos del usuario.
    svc.SoloLectura = !HasFlag(rest, "--permitir-escritura");
    svc.Plataforma = plataforma;
    plataforma.AttachMemory(mem);            // BDA, stubs de la IVT y temporizador
    Interp32.OnInt = svc.Int;
    Interp32.OnIrq = svc.ServicioIrq;        // interrupciones hardware en el mundo plano

    var cpu = new Cpu { Platform = plataforma,
                        // Con el reloj determinista, el ritmo del invitado lo fija PAKU_IRQ_DET
                        // (instrucciones por milisegundo) y el audio tiene que contar igual.
                        CyclesPerMs = PakuPort.Core.Platform.FlatServices.IrqDeterminista > 0
                            ? (int)PakuPort.Core.Platform.FlatServices.IrqDeterminista : 0,
                        ESP = le.Esp, DS = PakuPort.Core.Platform.FlatServices.SelDatos,
                        ES = PakuPort.Core.Platform.FlatServices.SelDatos,
                        SS = PakuPort.Core.Platform.FlatServices.SelDatos,
                        CS = PakuPort.Core.Platform.FlatServices.SelCodigo };
    Console.WriteLine($"== le-run: {Path.GetFileName(exePath)} ==");
    foreach (var ob in le.Objects)
        Console.WriteLine($"  obj{ob.Number} 0x{ob.Base:X8}-0x{ob.Base + ob.VirtualSize:X8} " +
                          $"{(ob.Executable ? "codigo" : "datos ")}");
    Console.WriteLine($"  EIP=0x{le.Eip:X8}  ESP=0x{le.Esp:X8}  presupuesto={maxInsns} instrucciones");

    // La traza de servicios es EL dato de esta sonda: si una excepcion se la lleva por delante,
    // la herramienta no sirve para lo que existe. Se captura, se informa y se imprime igual.
    // --teclas: se ejecuta POR TROZOS y entre trozo y trozo se mete una tecla en el teclado de
    // la plataforma (el mismo por el que las lee un juego de 16 bits). Enviarlas todas de golpe al
    // principio no vale: el juego vacia el buffer al arrancar y ademas hay que darle tiempo a
    // llegar a la pantalla que las espera.
    string teclas = GetOpt(rest, "--teclas") ?? "";
    // --machacar <tecla> [--cada N]: tras las --teclas, se pulsa ESA tecla una y otra vez durante
    // el resto del presupuesto. Acertar el instante exacto de una pulsacion a ciegas es carisimo
    // —un juego en modo atraccion va dando vueltas entre intro y menu, y cada intento cuesta una
    // ejecucion entera—, y repetir la tecla vale mas que acertar el momento: es la misma leccion
    // que se aprendio con otro juego de prueba.
    string machacar = GetOpt(rest, "--machacar") ?? "";
    long cada = long.TryParse(GetOpt(rest, "--cada"), out var _c) && _c > 0 ? _c : 20_000_000;
    Interp32.Resultado res;
    string? reventon = null;
    try
    {
        if (teclas.Length == 0 && machacar.Length == 0) res = Interp32.Run(cpu, mem, le.Eip, maxInsns);
        else
        {
            // La tecla se MANTIENE PULSADA un trozo entero antes de soltarla. Mandar el make y el
            // break seguidos es lo que hacia antes y no vale: los separaba una sola frontera de
            // interrupcion (4096 instrucciones) mientras el bucle del juego gira una vez por tick
            // (cientos de miles), asi que un juego que escanea "que teclas estan pulsadas AHORA"
            // —y son muchos— no la veia nunca. Medido en un juego de prueba de 32 bits:
            // recibia el scancode 0B en su ISR y la pantalla no cambiaba ni un pixel.
            // Con --machacar, las --teclas iniciales se gastan en la PRIMERA MITAD y la otra queda
            // para la tecla repetida; sin él se reparte todo por igual, como siempre.
            long presupuestoTeclas = machacar.Length > 0 ? maxInsns / 2 : maxInsns;
            long porTrozo = Math.Max(1, presupuestoTeclas / (Math.Max(1, teclas.Length) * 2 + 1));
            res = Interp32.Run(cpu, mem, le.Eip, porTrozo);
            foreach (char ch in teclas)
            {
                if (res.Motivo != Interp32.Fin.Presupuesto) break;
                var (scan, ascii) = MapProbeAutoKey(ch);
                teclado.EnqueueKey(scan, ascii);
                MeterEnBufferBios(mem, scan, ascii);
                // Y por la via del HARDWARE: el juego engancha el vector 09h (se ve en la traza),
                // asi que lo que espera es la IRQ del teclado, no el buffer del BIOS.
                plataforma.Keyboard.EnqueueHostScancode(scan);
                Console.WriteLine($"  tecla '{ch}' -> scan {scan:X2} ascii {ascii:X2} " +
                                  $"PULSADA (tras {res.Instrucciones} instrucciones)");
                res = Interp32.Run(cpu, mem, res.EipOffset, porTrozo);
                if (res.Motivo != Interp32.Fin.Presupuesto) break;
                plataforma.Keyboard.EnqueueHostScancode((byte)(scan | 0x80));   // break: soltada
                res = Interp32.Run(cpu, mem, res.EipOffset, porTrozo);
            }

            if (machacar.Length > 0)
            {
                var (scan, ascii) = MapProbeAutoKey(machacar[0]);
                long gastadas = 0, resto = maxInsns - presupuestoTeclas;
                int veces = 0;
                while (res.Motivo == Interp32.Fin.Presupuesto && gastadas < resto)
                {
                    teclado.EnqueueKey(scan, ascii);
                    MeterEnBufferBios(mem, scan, ascii);
                    plataforma.Keyboard.EnqueueHostScancode(scan);
                    res = Interp32.Run(cpu, mem, res.EipOffset, cada);
                    if (res.Motivo != Interp32.Fin.Presupuesto) break;
                    plataforma.Keyboard.EnqueueHostScancode((byte)(scan | 0x80));
                    res = Interp32.Run(cpu, mem, res.EipOffset, cada);
                    gastadas += cada * 2; veces++;
                }
                Console.WriteLine($"  tecla '{machacar[0]}' machacada {veces} veces cada {cada} instrucciones");
            }
        }
    }
    catch (Exception e)
    {
        reventon = e.Message;
        res = new Interp32.Resultado { Motivo = Interp32.Fin.FueraDeLaImagen, Eip = 0 };
    }

    // ¿Se salió de su propio código en algún momento? El historial es la caja negra.
    var code = le.Objects.First(o => o.Executable);
    int fuera = res.Historial.Count(e => e != 0 && (e < code.Base || e >= code.Base + code.VirtualSize));
    Console.WriteLine();
    if (reventon != null) Console.WriteLine($"  REVENTON: {reventon}");
    Console.WriteLine($"  resultado: {res}");
    Console.WriteLine($"  EIP fuera del objeto de codigo, en las {res.Historial.Length} mas recientes: {fuera}");
    Console.WriteLine($"  ultimos EIP: {string.Join(" < ", res.Historial.Take(8).Select(e => $"{e:X8}"))}");
    Console.WriteLine($"  EAX={cpu.EAX:X8} EBX={cpu.EBX:X8} ECX={cpu.ECX:X8} EDX={cpu.EDX:X8}");
    Console.WriteLine($"  ESI={cpu.ESI:X8} EDI={cpu.EDI:X8} EBP={cpu.EBP:X8} ESP={cpu.ESP:X8}");
    Console.WriteLine($"  CS={cpu.CS:X4}(base 0x{svc.BaseDe(cpu.CS):X}) DS={cpu.DS:X4}(0x{svc.BaseDe(cpu.DS):X}) " +
                      $"ES={cpu.ES:X4}(0x{svc.BaseDe(cpu.ES):X}) SS={cpu.SS:X4}(0x{svc.BaseDe(cpu.SS):X}) " +
                      $"FS={cpu.FS:X4}(0x{svc.BaseDe(cpu.FS):X}) GS={cpu.GS:X4}(0x{svc.BaseDe(cpu.GS):X})");
    // Lo que de verdad importa de un juego: ¿ha dibujado algo? El framebuffer es el juez.
    // El framebuffer viene en RGBA (4 bytes por píxel), no RGB. Contarlo de tres en tres da
    // números sin sentido y una imagen desplazada que parece toda blanca.
    var fb = plataforma.Video.RenderFramebuffer();
    int px = fb.Length / 4;
    int noNegros = 0;
    for (int i = 0; i < px; i++)
        if (fb[i * 4] != 0 || fb[i * 4 + 1] != 0 || fb[i * 4 + 2] != 0) noNegros++;
    Console.WriteLine();
    Console.WriteLine($"  VIDEO: modo 0x{plataforma.Video.VideoMode:X2} {plataforma.Video.Width}x{plataforma.Video.Height}  pixeles no negros = {noNegros}/{px}");
    Console.WriteLine($"  IRQ entregadas al juego: {svc.IrqEntregadas}");
    Console.WriteLine($"  BUFFER BIOS: head=0x{mem.RdU16(0x0040, 0x001A):X4} tail=0x{mem.RdU16(0x0040, 0x001C):X4}  {(mem.RdU16(0x0040, 0x001A) == mem.RdU16(0x0040, 0x001C) ? "VACIO (el juego lo consumio)" : "CON TECLAS SIN LEER (el juego no mira ahi)")}");
    Console.WriteLine($"  MEMORIA: alta reservada = {mem.HighSize} bytes, tope direccionable = 0x{mem.FlatLimit:X}");

    // Histograma de la VRAM y primeras entradas del DAC: una pantalla "toda de un color" puede
    // ser el juego pintando, la paleta mal, o la VRAM sin tocar. Sin mirarlo no se distingue.
    {
        var hist = new Dictionary<byte, int>();
        for (int i = 0; i < 64000; i++)
        {
            byte b = plataforma.Video.ReadVram(i);
            hist[b] = hist.TryGetValue(b, out var n0) ? n0 + 1 : 1;
        }
        var top = hist.OrderByDescending(k => k.Value).Take(5)
                      .Select(k => $"idx {k.Key:X2}: {k.Value}");
        Console.WriteLine($"  VRAM (indices mas frecuentes): {string.Join("  ", top)}");
        Console.Write("  DAC[0..5]:");
        for (int i = 0; i < 6; i++)
            Console.Write($" {i}=({plataforma.Video.Dac[i * 3]},{plataforma.Video.Dac[i * 3 + 1]},{plataforma.Video.Dac[i * 3 + 2]})");
        Console.WriteLine();
    }

    // --raw <fichero>: el framebuffer tal cual (RGBA, ancho x alto del modo). Contar pixeles no
    // negros dice SI dibuja, no QUE dibuja: para eso hay que mirarlo, y para compararlo con el
    // oraculo hace falta el mapa de bits entero. La conversion a PNG se hace fuera, con las
    // mismas herramientas de python que ya juzgan el resto de las pantallas del proyecto.
    string? raw = GetOpt(rest, "--raw");
    if (raw != null)
    {
        File.WriteAllBytes(raw, fb);
        Console.WriteLine($"  framebuffer -> {raw} ({plataforma.Video.Width}x{plataforma.Video.Height} RGBA, {fb.Length} bytes)");
    }

    // --volcar 0xADDR[:LEN] : desensambla memoria VIVA. El codigo que el juego carga en caliente
    // (drivers, overlays) no esta en la imagen del LE, asi que sin esto es invisible.
    string? volcar = GetOpt(rest, "--volcar");
    if (volcar != null)
    {
        var partes = volcar.Split(':');
        uint at = Convert.ToUInt32(partes[0].Replace("0x", ""), 16);
        int len = partes.Length > 1 ? int.Parse(partes[1]) : 64;
        var bytes = new byte[len];
        for (int i = 0; i < len; i++) bytes[i] = mem.RdLin8((uint)(at + i));
        Console.WriteLine($"  VOLCADO 0x{at:X8} ({len} bytes):");
        for (int i = 0; i < len; i += 16)
            Console.WriteLine($"    {at + i:X8}  {Convert.ToHexString(bytes, i, Math.Min(16, len - i))}");
        var dec = Iced.Intel.Decoder.Create(32, new Iced.Intel.ByteArrayCodeReader(bytes), at);
        Console.WriteLine("  desensamblado:");
        for (int k = 0; k < 14 && dec.IP < at + (uint)len; k++)
        {
            dec.Decode(out var ins);
            Console.WriteLine($"    {ins.IP:X8}  {ins}");
        }
    }

    string ppm = GetOpt(rest, "--imagen");
    if (ppm != null)
    {
        // PPM en crudo: sin dependencias y cualquier visor lo abre. La sonda no necesita más.
        using var f = new FileStream(ppm, FileMode.Create);
        var cab = System.Text.Encoding.ASCII.GetBytes(
            "P6\n" + plataforma.Video.Width + " " + plataforma.Video.Height + "\n255\n");
        f.Write(cab);
        var rgb = new byte[px * 3];
        for (int i = 0; i < px; i++)
        { rgb[i * 3] = fb[i * 4]; rgb[i * 3 + 1] = fb[i * 4 + 1]; rgb[i * 3 + 2] = fb[i * 4 + 2]; }
        f.Write(rgb);
        Console.WriteLine($"  -> {ppm}");
    }

    Console.WriteLine();
    Console.WriteLine($"  servicios pedidos ({svc.Traza.Count}):");
    foreach (var p in svc.Traza) Console.WriteLine($"    {p}");
    if (svc.Rechazados.Count > 0)
    {
        Console.WriteLine($"  rechazados ({svc.Rechazados.Count}):");
        foreach (var p in svc.Rechazados.Distinct()) Console.WriteLine($"    {p}");
    }
    Interp32.OnInt = null;
    return 0;
}

/// <summary>Mete una tecla en el buffer de teclado DE LA BDA (40:1A head, 40:1C tail, 40:1E el
/// anillo de 16 entradas), que es donde la deja el INT 9 del BIOS de verdad.
///
/// `Int16hHandler.EnqueueKey` la mete en un buffer PRIVADO suyo, y un juego que lea la BDA a pelo
/// —que es lo normal en un juego que no usa INT 16h— no la ve. Es el espejo del gotcha que ya
/// estaba documentado al reves para el oraculo.</summary>
static void MeterEnBufferBios(Memory mem, byte scan, byte ascii)
{
    // La BDA no la inicializa nadie en el mundo plano, asi que head y tail valen CERO y la
    // escritura acabaria en la IVT en vez de en el anillo. Se dejan como los pone el BIOS.
    if (mem.RdU16(0x0040, 0x001A) < 0x001E || mem.RdU16(0x0040, 0x001A) >= 0x003E)
    {
        mem.WrU16(0x0040, 0x001A, 0x001E);
        mem.WrU16(0x0040, 0x001C, 0x001E);
        mem.WrU16(0x0040, 0x0080, 0x001E);
        mem.WrU16(0x0040, 0x0082, 0x003E);
    }
    ushort cola = mem.RdU16(0x0040, 0x001C);
    ushort sig = (ushort)(cola + 2);
    if (sig >= 0x003E) sig = 0x001E;                 // el anillo va de 40:1E a 40:3D
    if (sig == mem.RdU16(0x0040, 0x001A)) return;    // lleno: se descarta, como el BIOS
    mem.WrU16(0x0040, cola, (ushort)((scan << 8) | ascii));
    mem.WrU16(0x0040, 0x001C, sig);
}

static int CmdPlatformTest(string[] rest)
{
    if (rest.Length < 1)
    {
        Console.WriteLine("uso: pakuport platform-test <tests.json> [--out <result_paku.json>]");
        return 1;
    }
    string testsPath = rest[0];
    string outPath = GetOpt(rest, "--out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(testsPath))!, "result_paku.json");
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(testsPath));
    var root = doc.RootElement;
    var tests = root.GetProperty("tests");

    var mem = new PakuPort.Core.Memory();
    var (platform, _, _, _) = PlatformFactory.CreateFull(Path.GetDirectoryName(Path.GetFullPath(testsPath))!);
    platform.AttachMemory(mem);
    // SoundBlaster instalado como en el oráculo (oracle.conf: sbtype=sb2, base 220, irq 7,
    // dma 1) para el handshake DSP del barrido. Sin audio (la detección no reproduce nada).
    platform.InstallSoundBlaster(0x220, 7, 1, null);
    const ushort pspSeg = 0x0814;   // mismo segmento de carga que el COM en el oráculo
    const ushort mbufAddr = 0x6000; // zona libre para los buffers comparables (mbuf)
    // Reproducir el estado de memoria del oráculo: el COM (TEST.COM) posee TODA la memoria
    // convencional (640K -> techo 0x9FFF), cero libre. Así el protocolo del allocator
    // (AH=48/4A) coincide byte-exacto con DOSBox-X (dos_memory.cpp) sin tocar el arena de
    // los juegos.
    platform.Int21?.SetupLoadedProgramMemory(pspSeg, 0x9FFF, mem);
    var cpu = new Cpu { Platform = platform, CS = pspSeg, DS = pspSeg, ES = pspSeg, SS = pspSeg, SP = 0xFFF8 };

    // Cadenas/buffers embebidos: el COM los tiene en su CS; aquí los replicamos en pspSeg al
    // mismo offset (str_off), para que un caso con dx="&SWEEP.TMP" apunte al mismo contenido.
    var strOff = new Dictionary<string, ushort>();
    if (root.TryGetProperty("strings", out var strs))
        foreach (var s in strs.EnumerateObject())
        {
            ushort off = (ushort)s.Value.GetProperty("off").GetInt32();
            byte[] raw = Convert.FromHexString(s.Value.GetProperty("hex").GetString()!);
            Array.Copy(raw, 0, mem.Raw, PakuPort.Core.Memory.Lin(pspSeg, off), raw.Length);
            strOff[s.Name] = off;
        }

    // Estado entre casos (handles de fichero, etc.): save=(reg,slot) lo guarda; "@slot" lo carga.
    var vars = new Dictionary<string, ushort>();
    var results = new List<Dictionary<string, object>>();
    int idx = 0;
    foreach (var tc in tests.EnumerateArray())
    {
        idx++;
        string id = tc.GetProperty("id").GetString()!;
        byte intNo = (byte)tc.GetProperty("int").GetInt32();
        var regs = tc.GetProperty("regs");
        bool hasMbuf = tc.TryGetProperty("mbuf_reg", out var mbufRegEl);
        string? mbufReg = hasMbuf ? mbufRegEl.GetString() : null;
        // Resuelve un valor de registro: int (inmediato), "@slot" (estado), "&str" (offset de cadena),
        // o la dir del buffer comparable si este reg es el mbuf.
        ushort R(string name, ushort dflt)
        {
            if (hasMbuf && name == mbufReg) return mbufAddr;
            if (!regs.TryGetProperty(name, out var v)) return dflt;
            if (v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                string sv = v.GetString()!;
                if (sv.StartsWith("@")) return vars.TryGetValue(sv[1..], out var vv) ? vv : (ushort)0;
                if (sv.StartsWith("&")) return strOff.TryGetValue(sv[1..], out var so) ? so : (ushort)0;
            }
            return (ushort)v.GetInt32();
        }
        // Mismo protocolo determinista que emit_case() de gen_tests.py
        cpu.SetFlagsWord(0x0002);
        cpu.DS = R("ds", pspSeg);
        cpu.ES = R("es", pspSeg);
        cpu.BX = R("bx", 0); cpu.CX = R("cx", 0); cpu.DX = R("dx", 0);
        cpu.SI = R("si", 0); cpu.DI = R("di", 0); cpu.AX = R("ax", 0);
        try
        {
            if (tc.TryGetProperty("io", out var ioEl))
            {
                // Caso de PUERTO IO (chips): mismo comportamiento que in/out del COM.
                string kind = ioEl.GetString()!;
                ushort port = cpu.DX;
                if (kind == "in8") cpu.AL = platform.PortIn8(port);
                else if (kind == "in16") cpu.AX = platform.PortIn16(port);
                else if (kind == "out8") platform.PortOut8(port, cpu.AL);
            }
            else
            {
                platform.Int(intNo, cpu, mem);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [{id}] EXCEPCION: {ex.Message}");
        }
        // save: guardar un registro de salida en un slot de estado
        if (tc.TryGetProperty("save", out var saveEl))
        {
            string sreg = saveEl[0].GetString()!, sname = saveEl[1].GetString()!;
            vars[sname] = sreg switch { "ax" => cpu.AX, "bx" => cpu.BX, "cx" => cpu.CX, "dx" => cpu.DX, _ => (ushort)0 };
        }
        var rec = new Dictionary<string, object>
        {
            ["id"] = id, ["marker"] = idx,
            ["ax"] = cpu.AX, ["bx"] = cpu.BX, ["cx"] = cpu.CX, ["dx"] = cpu.DX,
            ["si"] = cpu.SI, ["di"] = cpu.DI, ["es"] = cpu.ES, ["ds"] = cpu.DS,
            ["flags"] = cpu.FlagsWord(),
        };
        // mbuf: volcar el buffer comparable que el INT rellenó
        if (hasMbuf)
        {
            int nb = tc.GetProperty("mbuf_nbytes").GetInt32();
            var buf = new byte[nb];
            Array.Copy(mem.Raw, PakuPort.Core.Memory.Lin(pspSeg, mbufAddr), buf, 0, nb);
            rec["mbuf"] = Convert.ToHexString(buf);
        }
        results.Add(rec);
        cpu.DS = pspSeg;   // TEST.COM restaura ds=cs tras cada caso
    }
    File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(results,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"platform-test: {results.Count} casos -> {outPath}");
    return 0;
}

// =====================================================================
// DUMP-IMAGE: vuelca la imagen canónica de memoria del juego
// (la MISMA que ve el emitter) para que el explorador Unicorn ejecute los mismos
// bytes. Salida: <outDir>/image.bin + image.json (coordenadas linear<->file).
// =====================================================================

static int CmdDumpImage(GameConfig game, string gamePath, string outDir)
{
    Console.WriteLine($"== PakuPort dump-image: {game.Title} ==");
    if (game.Format == "com")
    {
        Console.Error.WriteLine("dump-image: solo soportado para MZ/EXE; COM no aplica.");
        return 1;
    }

    // Imagen canónica: EXACTAMENTE la carga del emitter (ver CmdExport, mismo patrón).
    var exeBytes = File.ReadAllBytes(game.ExePath);
    ushort loadSeg = game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110;
    // Copia PRISTINA antes de normalizar: NormalizeMzImage aplica relocaciones
    // in-place sobre rawExe y puede devolver el MISMO array; memimage.bin debe
    // construirse del packed sin tocar o las relocaciones se aplican DOS veces
    // (bug cazado 2026-07-03: DGROUP seg 0x364B en vez de 0x264B).
    var exeRaw = (byte[])exeBytes.Clone();
    byte[] imageBytes = ExeImage.NormalizeMzImage(exeBytes, loadSeg);
    int patchedThunks = ExePackDecompressor.PatchResidentThunks(imageBytes);
    Console.WriteLine($"  Imagen normalizada: {imageBytes.Length} bytes (thunks parcheados: {patchedThunks})");
    // EXEPACK difiere las relocaciones a runtime; el emitter no las necesita (resuelve
    // far calls por tabla) pero el explorador Unicorn EJECUTA los bytes -> aplicarlas.
    int relocsApplied = ExePackDecompressor.ApplyExepackRelocations(imageBytes, loadSeg);
    Console.WriteLine($"  EXEPACK relocaciones aplicadas: {relocsApplied}");


    // Coordenadas del explorador (verificadas en fuente, NO inventadas):
    //   image.bin[0] se mapea en lineal imageLoadLinear; file 0x200 -> resBaseLinear (= loadSeg*16).
    //   file_offset = linear - imageLoadLinear.
    int resBaseLinear = loadSeg * 16;
    int imageLoadLinear = resBaseLinear - ExeImage.ResidentBase;
    int entryGhidra = string.IsNullOrEmpty(game.EntryOverride) ? 0 : HexToInt(game.EntryOverride);
    // Entry y pila REALES del programa descomprimido (header EXEPACK), relocados a loadSeg.
    // Es lo que el explorador necesita para EJECUTAR la imagen desde el arranque de verdad
    // (el entryOverride 0x19E5 es el main de C, que asume el startup ya corrido).
    int realCs = 0, realIp = 0, realSs = 0, realSp = 0, xOfGhidra0 = 0;
    byte[]? memImage = null;
    int memRelocs = 0;

    // TAREA A — Unpack dinámico como PASO ADICIONAL. Su salida SUSTITUYE al
    // memimage/realEntry SOLO cuando el corte se produce (transfer far descendente). El
    // camino EXEPACK estático (EXEPACK/VROOMM) NO se toca: por defecto ("auto") el dinámico
    // solo se intenta cuando el estático no detecta EXEPACK; PAKU_DYNUNPACK=force lo fuerza
    // siempre (para el oráculo interno), =off lo desactiva.
    string dynMode = (Environment.GetEnvironmentVariable("PAKU_DYNUNPACK") ?? "auto").ToLowerInvariant();
    bool exepacked = ExePackDecompressor.IsExePacked(exeRaw);
    DynUnpackResult? dyn = null;
    if (dynMode != "off" && (dynMode == "force" || !exepacked))
    {
        dyn = TryDynamicUnpack(game, exeRaw, loadSeg);
        Console.WriteLine($"  unpack dinámico: {(dyn.Cut ? "CORTE" : "no")} [{dyn.Reason}] {dyn.Detail}");
    }

    if (dyn != null && dyn.Cut)
    {
        // El unpacker aplicó sus propias relocaciones -> la memoria capturada ya está
        // relocada a loadSeg. Imagen natural (ghidra 0 = memimage[0], sin sesgo X-space).
        memImage = dyn.MemImage;
        realCs = dyn.RealCs; realIp = dyn.RealIp;
        realSs = dyn.RealSs; realSp = dyn.RealSp;
        xOfGhidra0 = dyn.XOfGhidra0;
        memRelocs = 0;
    }
    else if (exepacked)
    {
        var dec = ExePackDecompressor.Decompress(exeRaw);
        realCs = (dec.RealCS + loadSeg) & 0xFFFF; realIp = dec.RealIP;
        realSs = (dec.RealSS + loadSeg) & 0xFFFF; realSp = dec.RealSP;
        // memimage.bin: la imagen de MEMORIA prístina (descomprimida, X-space) con las
        // relocaciones EXEPACK aplicadas en sitio. Es lo que el DOS real deja en RAM al
        // arrancar (a diferencia de image.bin, que conserva el layout del fichero packed
        // y no es ejecutable linealmente). El explorador --record ejecuta ESTA imagen.
        memImage = (byte[])dec.Decompressed.Clone();
        foreach (int off in ExePackDecompressor.ExepackRelocOffsets(exeRaw))
        {
            if (off < 0 || off + 1 >= memImage.Length) continue;
            ushort w = (ushort)(memImage[off] | (memImage[off + 1] << 8));
            w = (ushort)(w + loadSeg);
            memImage[off] = (byte)w;
            memImage[off + 1] = (byte)(w >> 8);
            memRelocs++;
        }
        // xOfGhidra0: posición en memimage del byte cuyo file offset (packed) es
        // ResidentBase — la correspondencia EXACTA X <-> ghidra, del propio mapping
        // de descompresión (nada de prólogos mágicos ni deltas medidos a mano).
        // El mapping tiene bytes FILL (-1) y un prefijo no lineal: tomar el delta
        // MAYORITARIO sobre el cuerpo residente (el código real, decenas de KB).
        var deltaCounts = new Dictionary<int, int>();
        for (int x = 0; x < dec.DecompToPacked.Length; x++)
        {
            int p = dec.DecompToPacked[x];
            if (p >= ExeImage.ResidentBase)
            {
                int d = x - (p - ExeImage.ResidentBase);
                deltaCounts[d] = deltaCounts.GetValueOrDefault(d) + 1;
            }
        }
        if (deltaCounts.Count > 0)
            xOfGhidra0 = deltaCounts.OrderByDescending(kv => kv.Value).First().Key;
    }
    else
    {
        // No-EXEPACK: la imagen normalizada (relocs MZ aplicadas) YA es la imagen de
        // memoria; el cuerpo empieza en file ResidentBase => X = ghidra (xOfGhidra0=0).
        // Entry/stack reales: del header MZ del exe crudo, relocados a loadSeg.
        memImage = new byte[Math.Max(0, imageBytes.Length - ExeImage.ResidentBase)];
        Array.Copy(imageBytes, ExeImage.ResidentBase, memImage, 0, memImage.Length);
        realIp = exeRaw[20] | (exeRaw[21] << 8);
        realCs = ((exeRaw[22] | (exeRaw[23] << 8)) + loadSeg) & 0xFFFF;
        realSp = exeRaw[16] | (exeRaw[17] << 8);
        realSs = ((exeRaw[14] | (exeRaw[15] << 8)) + loadSeg) & 0xFFFF;
    }
    // DS/ES iniciales: misma fórmula que el runtime canónico (Program.cs §"6. Preparar CPU").
    ushort dsSeg = game.DsBase > 0 ? (ushort)(loadSeg + ((int)game.DsBase - 0x10000) / 16) : loadSeg;

    // residentCodeEnd y rango de overlays: verbatim de game.json (GameConfig no los expone).
    int residentCodeEnd, ovMinStart = int.MaxValue, ovMaxEnd = 0;
    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(gamePath)))
    {
        var r = doc.RootElement;
        residentCodeEnd = r.TryGetProperty("residentCodeEnd", out var rce)
            ? HexToInt(rce.GetString()!)
            : imageBytes.Length - ExeImage.ResidentBase;   // sin overlays: todo residente
        if (r.TryGetProperty("overlayCodeStart", out var ovs) && r.TryGetProperty("overlayCodeSize", out var ovz))
        {
            foreach (var p in ovs.EnumerateObject())
            {
                int start = HexToInt(p.Value.GetString()!);
                int size = ovz.TryGetProperty(p.Name, out var sv) ? HexToInt(sv.GetString()!) : 0;
                ovMinStart = Math.Min(ovMinStart, start);
                ovMaxEnd = Math.Max(ovMaxEnd, start + size);
            }
        }
    }
    int residentFileStart = ExeImage.ResidentBase;                 // 0x200
    int residentFileEnd = ExeImage.ResidentBase + residentCodeEnd; // [start, end)

    Directory.CreateDirectory(outDir);
    string binPath = Path.Combine(outDir, "image.bin");
    string jsonPath = Path.Combine(outDir, "image.json");
    File.WriteAllBytes(binPath, imageBytes);
    if (memImage != null)
    {
        File.WriteAllBytes(Path.Combine(outDir, "memimage.bin"), memImage);
        Console.WriteLine($"  memimage.bin -> {memImage.Length} bytes (relocs X-space: {memRelocs})");
    }

    string j =
        "{\n" +
        $"  \"_comment\": \"Imagen canónica de PakuPort (NormalizeMzImage + PatchResidentThunks), la misma que ve el emitter. Coordenadas verificadas en fuente.\",\n" +
        $"  \"imageBytesLen\": {imageBytes.Length},\n" +
        $"  \"exepackRelocationsApplied\": {relocsApplied},\n" +
        $"  \"loadSegment\": \"0x{loadSeg:X4}\",\n" +
        $"  \"residentBase\": \"0x{ExeImage.ResidentBase:X}\",\n" +
        $"  \"resBaseLinear\": \"0x{resBaseLinear:X}\",\n" +
        $"  \"imageLoadLinear\": \"0x{imageLoadLinear:X}\",\n" +
        $"  \"linearToFileFormula\": \"file_offset = linear - 0x{imageLoadLinear:X}\",\n" +
        $"  \"entry\": {{ \"cs\": \"0x{loadSeg:X4}\", \"ip\": \"0x{entryGhidra:X4}\", \"ghidraOffset\": \"0x{entryGhidra:X4}\", \"fileOffset\": \"0x{ExeImage.ResidentBase + entryGhidra:X4}\" }},\n" +
        $"  \"realEntry\": {{ \"cs\": \"0x{realCs:X4}\", \"ip\": \"0x{realIp:X4}\" }},\n" +
        $"  \"realStack\": {{ \"ss\": \"0x{realSs:X4}\", \"sp\": \"0x{realSp:X4}\" }},\n" +
        $"  \"memImage\": {{ \"len\": {memImage?.Length ?? 0}, \"relocs\": {memRelocs}, \"xOfGhidra0\": \"0x{xOfGhidra0:X}\" }},\n" +
        $"  \"overlayIpBase\": \"0x{game.OverlayLoadBase:X4}\",\n" +
        $"  \"ds\": \"0x{dsSeg:X4}\",\n" +
        $"  \"es\": \"0x{dsSeg:X4}\",\n" +
        $"  \"_ssSpNote\": \"Propuesta para v0 (SS=DGROUP, SP tope); refinar en Tarea 1 con evidencia. GAME.EXE es EXEPACK: el SS del header MZ apunta al stub, no al juego.\",\n" +
        $"  \"ss\": \"0x{dsSeg:X4}\",\n" +
        $"  \"sp\": \"0xFFEE\",\n" +
        $"  \"resident\": {{ \"fileStart\": \"0x{residentFileStart:X}\", \"fileEnd\": \"0x{residentFileEnd:X}\", \"ghidraCodeEnd\": \"0x{residentCodeEnd:X}\" }},\n" +
        $"  \"overlays\": {{ \"_note\": \"bonus, NO entra en la métrica\", \"fileStart\": \"0x{ovMinStart:X}\", \"fileEnd\": \"0x{ovMaxEnd:X}\" }}\n" +
        "}\n";
    File.WriteAllText(jsonPath, j);

    Console.WriteLine($"  image.bin  -> {binPath} ({imageBytes.Length} bytes)");
    Console.WriteLine($"  image.json -> {jsonPath}");
    Console.WriteLine($"  entry CS:IP = 0x{loadSeg:X4}:0x{entryGhidra:X4} | DS = 0x{dsSeg:X4} | residente file [0x{residentFileStart:X},0x{residentFileEnd:X})");
    return 0;
}

// TAREA A — Unpack dinámico (packer-agnóstico). Ejecuta el startup del EXE con el Interp
// propio sobre Memory+Cpu+Platform limpios (cargado con MzLoader como el runtime, desde el
// entry del header MZ) hasta el PRIMER transfer de control far DESCENDENTE (el stub del
// packer vive al final de la imagen y salta hacia abajo, al programa descomprimido). En ese
// punto la memoria YA está descomprimida y relocada: se captura como memimage y el destino
// del salto es el realEntry verdadero. Cero código por-packer: el criterio es el
// comportamiento (salto far descendente), no firmas LZEXE/PKLITE/EXEPACK.
//
// Aborta (=> "no packed", usar el camino estático) si ANTES del corte el startup hace una INT
// de "programa real" (10h vídeo / 16h teclado / 33h ratón / 21h AH=3C/3D/4B ficheros) o agota
// el presupuesto: los unpackers no hacen nada de eso.
static DynUnpackResult TryDynamicUnpack(GameConfig game, byte[] exeRaw, ushort loadSeg)
{
    var res = new DynUnpackResult();
    long budget = long.TryParse(Environment.GetEnvironmentVariable("PAKU_DYNUNPACK_BUDGET"), out var bb) && bb > 0
        ? bb : 8_000_000;

    var mem = new Memory();
    MzLoader mz;
    try { mz = MzLoader.Load(exeRaw, mem, loadSeg); }
    catch (Exception ex) { res.Reason = "mzload:" + ex.Message; return res; }

    ushort pspSeg = (ushort)(loadSeg - 0x10);
    MzLoader.SetupPsp(mem, pspSeg, (ushort)(pspSeg - 0x10));
    string exePath = game.ExePath;
    string gameDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? ".";
    MzLoader.SetupEnvironment(mem, (ushort)(pspSeg - 0x10), exePath);

    var (platform, _, _, _) = PlatformFactory.CreateFull(gameDir, exePath);
    mem.Video = platform.Video;
    platform.AttachMemory(mem);

    int loadBase = loadSeg * 16;
    var cpu = new Cpu
    {
        CS = mz.AbsCS, SS = mz.AbsSS, SP = mz.InitSP,
        DS = pspSeg, ES = pspSeg,          // DOS entrega DS=ES=PSP al programa
        Platform = platform, LoadBase = loadBase, Budget = budget,
    };

    ushort csEntry = mz.AbsCS;
    const int margin = 0x20;   // paras: el programa descomprimido vive MUY por debajo del stub

    bool savedWp = Interp.WholeProgram;
    var savedOnStep = Interp.OnStep;
    var savedOnInt = Interp.OnInt;
    long insn0 = Interp.InsnCount;
    try
    {
        Interp.WholeProgram = true;
        Interp.OnStep = (cs, ip) =>
        {
            if (cs < csEntry - margin && cs >= loadSeg)
                throw new UnpackCutSignal(cs, ip);
        };
        Interp.OnInt = (n, c) =>
        {
            bool symptom = n is 0x10 or 0x16 or 0x33
                || (n == 0x21 && c.AH is 0x3C or 0x3D or 0x4B);
            if (symptom) throw new UnpackAbortSignal($"int {n:X2} AH={c.AH:X2}");
        };
        Interp.Run(cpu, mem, mz.AbsCS, mz.AbsIP, null, null);
        // Run devolvió sin corte: presupuesto agotado o hlt -> no es un packer.
        res.Reason = "run-ended (sin corte)";
    }
    catch (UnpackCutSignal cut)
    {
        res.Cut = true;
        res.RealCs = cut.Cs; res.RealIp = cut.Ip;
        res.RealSs = cpu.SS; res.RealSp = cpu.SP;
        res.XOfGhidra0 = 0;   // imagen natural loadSeg:0 (ghidra 0 = memimage[0])

        // memimage = memoria descomprimida desde loadBase. Ventana acotada al entorno de la
        // pila real + margen (excluye el stub que el packer reubica cerca del techo de la
        // asignación DOS), recortando ceros de cola; suelo = ventana residente completa.
        int scanTop = Math.Min(0xA0000, Math.Max(cpu.SS * 16 + 0x10000, loadBase + 0x16000));
        int last = loadBase;
        for (int i = scanTop - 1; i >= loadBase; i--)
            if (mem.Raw[i] != 0) { last = i + 1; break; }
        int len = Math.Max(last - loadBase, OverlayDirectory.ResidentThreshold);
        len = Math.Min((len + 15) & ~15, 0xA0000 - loadBase);
        res.MemImage = new byte[len];
        Array.Copy(mem.Raw, loadBase, res.MemImage, 0, len);

        res.Reason = "cut";
        res.Detail = $"corte {cut.Cs:X4}:{cut.Ip:X4} (stub {csEntry:X4}), SS:SP {cpu.SS:X4}:{cpu.SP:X4}, "
            + $"insns {Interp.InsnCount - insn0:N0}, memimage {len:N0} bytes";
    }
    catch (UnpackAbortSignal ab) { res.Reason = "abort:" + ab.Message; }
    catch (Exception ex) { res.Reason = "exc:" + ex.GetType().Name + ":" + ex.Message; }
    finally
    {
        Interp.WholeProgram = savedWp;
        Interp.OnStep = savedOnStep;
        Interp.OnInt = savedOnInt;
    }
    return res;
}

static int HexToInt(string s)
{
    s = s.Trim();
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
    return Convert.ToInt32(s, 16);
}

// =====================================================================
// EXPORT: genera un proyecto .NET standalone con todo el código traducido
// =====================================================================

// Set-1 scancodes for letters and digits, by keyboard row. Before this table they all fell through
// to 0x39 (space): a game that reads raw scancodes off port 0x60 instead of INT 16h ASCII
// (two test games) never got the key. Shift state is not modelled — an uppercase char sends
// the unshifted scancode and its own ASCII, which is what INT 16h consumers read anyway.
static byte QwertyScancode(char c)
{
    const string Row1 = "1234567890", Row2 = "qwertyuiop", Row3 = "asdfghjkl", Row4 = "zxcvbnm";
    char lc = char.ToLowerInvariant(c);
    int i;
    if ((i = Row1.IndexOf(lc)) >= 0) return (byte)(0x02 + i);
    if ((i = Row2.IndexOf(lc)) >= 0) return (byte)(0x10 + i);
    if ((i = Row3.IndexOf(lc)) >= 0) return (byte)(0x1E + i);
    if ((i = Row4.IndexOf(lc)) >= 0) return (byte)(0x2C + i);
    return 0x39;
}

// Char -> (scancode, ascii) for the discovery probe's autokeys. MUST stay identical to the
// generated host's PAKU_AUTOKEYS mapping (template: "Input: teclas automáticas") so the probe
// drives the game exactly as the smoke does. Letters/digits go through QwertyScancode; note
// that 'v' and 'W'/'E'/'N'/'S' are taken above by the arrow/numpad shorthands.
static (byte scan, byte ascii) MapProbeAutoKey(char c) => c switch
{
    '\r' => ((byte)0x1C, (byte)0x0D),
    '<'  => ((byte)0x4B, (byte)0x00),
    '>'  => ((byte)0x4D, (byte)0x00),
    '^'  => ((byte)0x48, (byte)0x00),
    'v'  => ((byte)0x50, (byte)0x00),
    '+'  => ((byte)0x51, (byte)0x00),  // PgDn / PgUp: los briefings paginados de algun juego no se pasan
    '-'  => ((byte)0x49, (byte)0x00),  // con Enter, y sin ellas el arnés no llega al juego
    '\x1b' => ((byte)0x01, (byte)0x1B), // ESC: abre los menús en juego (algun juego elige ahí el mando)
    '{'  => ((byte)0x3B, (byte)0x00),  // F1..F6 = '{' '}' '[' ']' '(' ')' (espejo de la plantilla)
    '}'  => ((byte)0x3C, (byte)0x00),
    '['  => ((byte)0x3D, (byte)0x00),
    ']'  => ((byte)0x3E, (byte)0x00),
    '('  => ((byte)0x3F, (byte)0x00),
    ')'  => ((byte)0x40, (byte)0x00),
    'W'  => ((byte)0x4B, (byte)'4'),
    'E'  => ((byte)0x4D, (byte)'6'),
    'N'  => ((byte)0x48, (byte)'8'),
    'S'  => ((byte)0x50, (byte)'2'),
    _    => (QwertyScancode(c), (byte)c),
};

/// <summary>Copia PakuPort.Core al export (Core/ + Core/Platform + Core/Platform/Machine) y lo
/// deja compilable en solitario: fuera los .csproj colados y las dependencias del juez de casos (PakuPort.Judge).
/// Lo usan las DOS plantillas (16 bits y LE): la lista es una sola para que no diverjan.</summary>
// La lista de ficheros de Core que van al export vive en UN SOLO SITIO (ExportCore):
// duplicarla fue lo que dejo los 21 exports sin compilar en la s47D, y ningun gate lo vio.
static void CopyCoreToExport(string outDir) => ExportCore.Copiar(outDir);

/// <summary>Export del host LE (modo protegido de 32 bits): la plantilla que convierte lo que hoy
/// hace la sonda `le-run` en un Jugar&lt;Juego&gt;.exe con ventana y teclado de verdad.
///
/// El mundo es el de la sonda, CALCADO: LeLoader en 0x110000 (por encima de 1 MiB, como DOS/4GW),
/// espacio plano de 16 MiB, FlatServices como sustituto del extensor e Interp32 con las IRQ
/// entregadas por ServicioIrq. Lo nuevo es el bucle de producto: hilo de juego por trozos con
/// compuerta de frame (el governor sale solo: cyclesPerMs*1000/60 instrucciones por frame de
/// 60 fps), teclado del host por la ruta hardware (make/break + typematic) y presentación de la
/// VRAM viva a 60 fps. MachineTimer.Poll NO se engancha a propósito: dispararía las ISR por el
/// camino de 16 bits y se comería las IRQ antes de que el intérprete de 32 las viera
/// (FlatServices.ServicioIrq, comentario del reloj propio).</summary>
static int ExportLeHost(GameConfig game, string outDir, string gamePath)
{
    string exeName = Path.GetFileName(game.ExePath);
    Console.WriteLine($"  formato LE: host de 32 bits (sin lifter; el juego corre en Interp32)");
    Console.WriteLine($"  Salida: {Path.GetFullPath(outDir)}");
    Directory.CreateDirectory(outDir);
    CopyCoreToExport(outDir);

    string sbSetup = game.HasSb
        ? $@"// SoundBlaster digital: game.json declara `sb`. En el mundo plano el SB se bombea desde
// `FlatServices.ServicioIrq` (`LevantarIrqSiVence` -> avance del DMA + dosificado al anillo),
// no desde `MachineTimer`, que aqui no se engancha.
platform.InstallSoundBlaster(0x{game.SbBase:X4}, {game.SbIrq}, {game.SbDma}, _hostAudio);"
        : "// game.json sin bloque `sb`: la Sound Blaster no se instala (el camino --sin-sb de la sonda).";

    string programCs = $@"using PakuPort.Core;
using PakuPort.Core.Platform;
using Raylib_cs;

// Host LE generado por PakuPort — {game.Title}
// El juego de MODO PROTEGIDO de 32 bits corre TAL CUAL (sin traducir) sobre el sustituto de
// DOS/4GW del proyecto: Interp32 (interprete de 32 bits) + FlatServices (DPMI + DOS de 32 bits).

// Entregable portable: si los ficheros del juego están junto al ejecutable (publish single-file)
// pero no en el cwd, trabajar desde el directorio del ejecutable.
static bool ExistsExactCase(string dir, string name)
{{
    if (!Directory.Exists(dir)) return false;
    foreach (var f in Directory.EnumerateFiles(dir))
        if (string.Equals(Path.GetFileName(f), name, StringComparison.Ordinal)) return true;
    return false;
}}
if (!ExistsExactCase(""."", ""{exeName}"") && ExistsExactCase(AppContext.BaseDirectory, ""{exeName}""))
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Rotación de logs por partida (mismo contrato que la plantilla de 16 bits).
try
{{
    string[] _prevLogs = {{ ""logs/exit.log"", ""logs/exit.png"", ""logs/input.log"" }};
    if (_prevLogs.Any(File.Exists))
    {{
        DateTime _pt = _prevLogs.Where(File.Exists).Select(File.GetLastWriteTime).Max();
        string _pdir = $""logs/partida-{{_pt:yyyyMMdd-HHmmss}}"";
        Directory.CreateDirectory(_pdir);
        foreach (var _pf in _prevLogs.Where(File.Exists))
            File.Move(_pf, Path.Combine(_pdir, Path.GetFileName(_pf)), overwrite: true);
        foreach (var _old in Directory.GetDirectories(""logs"", ""partida-*"").OrderByDescending(d => d).Skip(10))
            Directory.Delete(_old, recursive: true);
    }}
}}
catch (Exception _rex) {{ Console.Error.WriteLine(""[LOGS] rotacion fallida: "" + _rex.Message); }}

var gameDir = ExistsExactCase(AppContext.BaseDirectory, ""{exeName}"")
    ? AppContext.BaseDirectory
    : ExistsExactCase(""."", ""{exeName}"")
        ? Directory.GetCurrentDirectory()
        : Path.GetDirectoryName(Path.GetFullPath(""{game.ExePath.Replace("\\", "/")}"")) ?? ""."";

// El mundo plano, calcado de la sonda `le-run` (que es donde este juego JUEGA):
// imagen por encima de 1 MiB como DOS/4GW (el primer MiB entero para el DMA) y 16 MiB de
// espacio lineal para que el monton del runtime de C crezca detras de la imagen.
var exeBytes = File.ReadAllBytes(Path.Combine(gameDir, ""{exeName}""));
var le = LeLoader.Load(exeBytes, 0x110000);
var mem = new Memory();
mem.LoadFlatImage(le.Image);
mem.EnableFlat(16 * 1024 * 1024);
// Unidades DOS extra. Por defecto el juego SOLO ve la carpeta de este exe (C:) y sus
// subcarpetas. DISCOS.txt (junto al exe, una linea ""D=C:\ruta"" por unidad) monta mas carpetas
// del host como D:..Z:, y la primera pantalla las anade con la tecla D. Las unidades las recoge
// el Int21hHandler en su PRIMER servicio, ya pasado el splash (Platform/DosFileSystem.cs).
var _discos = new PakuPort.Core.Platform.DiscosExtra(gameDir);
_discos.Cargar();
_discos.Aplicar();
var (platform, keyboard, mouse, timer) = PlatformFactory.CreateFull(gameDir, ""{exeName}"");
mem.Video = platform.Video;
uint tope = le.Objects.Max(o => o.Base + o.VirtualSize);
var svc = new FlatServices(mem, tope, gameDir);
svc.SoloLectura = false;    // producto: el juego puede grabar sus partidas
svc.Plataforma = platform;
platform.AttachMemory(mem);
Interp32.OnInt = svc.Int;
Interp32.OnIrq = svc.ServicioIrq;   // interrupciones hardware en el mundo plano

var cpu = new Cpu
{{
    Platform = platform,
    // El audio y los plazos de la SB cuentan en instrucciones/ms; el mismo numero gobierna el
    // presupuesto por frame del hilo de juego (el governor del mundo plano ES la compuerta).
    CyclesPerMs = int.TryParse(Environment.GetEnvironmentVariable(""PAKU_CYCLES""), out var _cycOv) ? _cycOv : {game.CyclesPerMs},
    ESP = le.Esp,
    DS = FlatServices.SelDatos, ES = FlatServices.SelDatos, SS = FlatServices.SelDatos,
    CS = FlatServices.SelCodigo,
}};

Console.WriteLine(""Port de {game.Title} generado por PakuPort (host LE de 32 bits)"");

// --- Ventana Raylib (mismo contrato que la plantilla de 16 bits) ---
const int RenderFps = 60;
int w = platform.Video.Width;
int h = platform.Video.Height;
if (PakuPort.Core.CorteDeterminista.SinVentana)
    Raylib.SetConfigFlags(ConfigFlags.HiddenWindow);
bool _initialAspect = Environment.GetEnvironmentVariable(""PAKU_NO_ASPECT"") != ""1"";
float _initialPixelAspect = _initialAspect ? ((float)w / h) * (3f / 4f) : 1f;
Raylib.InitWindow(Math.Min(960, w * 2), Math.Min(720, (int)MathF.Round(h * _initialPixelAspect * 2)),
    ""{game.Title} — PakuPort"");
int _monitor = Raylib.GetCurrentMonitor();
float _fitScale = Math.Min(3f, Math.Min((Raylib.GetMonitorWidth(_monitor) - 80f) / w,
                                        (Raylib.GetMonitorHeight(_monitor) - 120f) / (h * _initialPixelAspect)));
_fitScale = Math.Max(1f, _fitScale);
Raylib.SetWindowSize((int)MathF.Round(w * _fitScale), (int)MathF.Round(h * _initialPixelAspect * _fitScale));
if (Environment.GetEnvironmentVariable(""PAKU_WINDOWED"") != ""1"" &&
    !PakuPort.Core.CorteDeterminista.SinVentana)
    Raylib.ToggleBorderlessWindowed();
Raylib.SetExitKey(KeyboardKey.Null);   // ESC es del juego; salir = F12 o la X
Raylib.SetTargetFPS(RenderFps);
var img = new Image {{ Width = w, Height = h, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 }};
var tex = Raylib.LoadTextureFromImage(img);
bool aspectCorrection = Environment.GetEnvironmentVariable(""PAKU_NO_ASPECT"") != ""1"";
// Escala ENTERA: la imagen se amplia un numero exacto de veces y se centra. Sin esto el
// factor sale fraccionario (320x240 a 1080p son x4,5) y con filtro de vecino mas proximo
// unas filas se duplican y otras no: se ve como si media pantalla estuviera a media
// resolucion, y el patron BAILA cuando el juego mueve la imagen. Ctrl+F12 conmuta.
bool escalaEntera = Environment.GetEnvironmentVariable(""PAKU_ESCALA_ENTERA"") == ""1"";

// --- Audio del host: anillo mezclable que escribe el hilo del juego y drena este ---
AudioStream _sbStream = default;
bool _sbStreamReady = false;
short[] _audioBuf = new short[2048];
HostAudio? _hostAudio = null;
bool _noAudio = Environment.GetEnvironmentVariable(""PAKU_NOAUDIO"") == ""1"";
if (!_noAudio)
{{
    try
    {{
        Raylib.InitAudioDevice();
        if (Raylib.IsAudioDeviceReady())
        {{
            _hostAudio = new HostAudio(22050, 22050);
            Raylib.SetAudioStreamBufferSizeDefault(_audioBuf.Length);
            _sbStream = Raylib.LoadAudioStream(22050, 16, 1);
            Raylib.PlayAudioStream(_sbStream);
            _sbStreamReady = true;
        }}
    }}
    catch (Exception _aex) {{ Console.Error.WriteLine(""[audio] backend deshabilitado: "" + _aex.Message); }}
}}
// PAKU_WAV_DUMP=<ruta>: vuelca el PCM drenado a un WAV mono 16-bit 22050. Es la unica forma
// OBJETIVA de decir si suena algo y qué (el oido no vale de juez); sin dispositivo de audio se
// crea el anillo igual para que el volcado funcione headless.
string? _wavPath = Environment.GetEnvironmentVariable(""PAKU_WAV_DUMP"");
System.IO.FileStream? _wavFs = null;
byte[]? _wavBuf = null;
long _wavData = 0;
if (!string.IsNullOrEmpty(_wavPath))
{{
    try
    {{
        var _wd = System.IO.Path.GetDirectoryName(_wavPath);
        if (!string.IsNullOrEmpty(_wd)) System.IO.Directory.CreateDirectory(_wd);
        _wavFs = new System.IO.FileStream(_wavPath, System.IO.FileMode.Create);
        _wavFs.Write(new byte[44], 0, 44);
    }}
    catch (Exception _we) {{ Console.Error.WriteLine(""[wav] no pude abrir "" + _wavPath + "": "" + _we.Message); _wavFs = null; }}
}}
if (_wavFs != null && _hostAudio == null) _hostAudio = new HostAudio(22050, 22050);
{sbSetup}

// Velocidad en caliente (Ctrl+F1/F2/F3), misma escalera que los hosts de 16 bits.
int[] cyclesSteps = {{ 100, 150, 200, 250, 315, 400, 500, 650, 800, 1000, 1250, 1600,
                      2000, 2500, 3000, 4000, 5000, 6500, 8000, 12000, 20000,
                      30000, 40000, 60000 }};
int cyclesIdx = 0;
int _cycRef = cpu.CyclesPerMs > 0 ? cpu.CyclesPerMs : 20000;
for (int _i = 1; _i < cyclesSteps.Length; _i++)
    if (Math.Abs(cyclesSteps[_i] - _cycRef) < Math.Abs(cyclesSteps[cyclesIdx] - _cycRef))
        cyclesIdx = _i;
int cyclesFactory = cpu.CyclesPerMs;
double speedOsdUntil = 0;

bool finished = false;
string? error = null;
string exitReason = """";
bool exitHandled = false;
bool splashDone = Environment.GetEnvironmentVariable(""PAKU_NOSPLASH"") == ""1"";
long autoExitMs = long.TryParse(Environment.GetEnvironmentVariable(""PAKU_AUTOEXIT_MS""), out var _ae) ? _ae : 0;
if (autoExitMs > 0) splashDone = true;
var runClock = System.Diagnostics.Stopwatch.StartNew();
long _nextHb = 5000;
bool numLock = true;
var keysDown = new Dictionary<int, byte>();
var keyRepeatAt = new Dictionary<int, double>();
const double TypematicDelay = 0.500, TypematicPeriod = 1.0 / 10.9;
bool typematic = Environment.GetEnvironmentVariable(""PAKU_NO_TYPEMATIC"") != ""1"";
var autoKeys = new Queue<char>((Environment.GetEnvironmentVariable(""PAKU_AUTOKEYS"") ?? """").Replace(""\\r"", ""\r"").ToCharArray());
int autoKeyTick = 0;
byte autoKeyBreak = 0;

// --- Splash ---
while (!Raylib.WindowShouldClose() && !splashDone)
{{
    // D y Supr gestionan las unidades extra y NO arrancan el juego; cualquier otra tecla si.
    int _sk;
    while ((_sk = Raylib.GetKeyPressed()) != 0)
    {{
        if (_sk == (int)KeyboardKey.D)
        {{
            string? _sruta = PakuPort.Core.Platform.SelectorDeCarpeta.Elegir(""Elige una carpeta del PC para que el juego la vea como una unidad DOS"");
            if (_sruta != null) _discos.Anadir(_sruta);
        }}
        else if (_sk == (int)KeyboardKey.Delete) _discos.QuitarUltima();
        else splashDone = true;
    }}
    if (Raylib.IsMouseButtonPressed(MouseButton.Left)) splashDone = true;
    Raylib.BeginDrawing();
    Raylib.ClearBackground(Color.Black);
    Raylib.DrawText(""Portado por PakuPort"", Raylib.GetScreenWidth() / 2 - 140, Raylib.GetScreenHeight() / 2 - 70, 24, Color.White);
    Raylib.DrawText(""{game.Title}"", Raylib.GetScreenWidth() / 2 - 100, Raylib.GetScreenHeight() / 2 - 30, 20, Color.Green);
    Raylib.DrawText(""Pulsa cualquier tecla"", Raylib.GetScreenWidth() / 2 - 130, Raylib.GetScreenHeight() / 2 + 10, 16, Color.Gray);
    Raylib.DrawText(""F11 = pantalla completa   F12 = salir (ESC es del juego)"", Raylib.GetScreenWidth() / 2 - 130, Raylib.GetScreenHeight() / 2 + 44, 14, Color.DarkGray);
    Raylib.DrawText(""Ctrl+F1 / Ctrl+F2 = mas lento / mas rapido"", Raylib.GetScreenWidth() / 2 - 130, Raylib.GetScreenHeight() / 2 + 62, 14, Color.DarkGray);
    int _dy = Raylib.GetScreenHeight() / 2 + 96;
    Raylib.DrawText(""Unidades:"", Raylib.GetScreenWidth() / 2 - 130, _dy, 14, Color.Gold);
    foreach (string _dl in _discos.LineasParaPantalla())
    {{
        _dy += 18;
        Raylib.DrawText(_dl, Raylib.GetScreenWidth() / 2 - 118, _dy, 14, Color.LightGray);
    }}
    Raylib.DrawText(""D = anadir una carpeta como unidad   Supr = quitar la mas reciente"", Raylib.GetScreenWidth() / 2 - 130, _dy + 26, 14, Color.DarkGray);
    Raylib.EndDrawing();
}}

// --- Hilo del juego: Interp32 por trozos, gobernado por TIEMPO REAL ---
// El ritmo NO lo marca el frame de la ventana sino el reloj, que es el modelo de DOSBox y el
// mismo que el governor de MachineTimer en el mundo de 16 bits: se ejecuta un trozo corto y, si
// el tiempo del invitado (instrucciones/cyclesPerMs) se ha adelantado al real, se duerme.
//
// Pacer por frames era lo de antes y tenia dos defectos MEDIDOS: (1) si un trozo tardaba mas que
// un frame —una lectura grande de disco durante la intro— los avisos de frame se solapaban en un
// solo evento y el juego PERDIA presupuesto, que es el tironeo de la intro; (2) daba por hecho
// que siempre cabian cyclesPerMs*1000/60 instrucciones en un frame, y cuando no cabian el tiempo
// virtual se quedaba atras sin que nadie se enterase.
//
// El reloj del PIT y la entrega de IRQ van DENTRO de Interp32.Run (frontera de 4096
// instrucciones -> FlatServices.ServicioIrq), asi que aqui no hay MachineTimer: engancharlo
// dispararia las ISR por el camino de 16 bits y se comeria las IRQ antes de que el interprete
// de 32 las viera.
const long TrozoInsns = 65536;   // ~3 ms a 20000 instrucciones/ms: fino para pacer, barato
new Thread(() =>
{{
    var relojJuego = System.Diagnostics.Stopwatch.StartNew();
    long anclaInsns = Interp32.Ejecutadas;
    double anclaMs = 0;
    int cycAncla = cpu.CyclesPerMs;
    try
    {{
        uint eipOff = le.Eip;
        while (true)
        {{
            // Reanclar al cambiar la velocidad EN CALIENTE (Ctrl+F1/F2). La deuda acumulada solo
            // significa algo a velocidad constante: sin esto, bajar el numero reevalua todo el
            // pasado y el governor se duerme hasta que el reloj real le alcance (medido: minutos
            // congelado).
            if (cpu.CyclesPerMs != cycAncla)
            {{
                cycAncla = cpu.CyclesPerMs;
                anclaInsns = Interp32.Ejecutadas;
                anclaMs = relojJuego.Elapsed.TotalMilliseconds;
            }}
            var res = Interp32.Run(cpu, mem, eipOff, TrozoInsns);
            if (res.Motivo == Interp32.Fin.Presupuesto)
            {{
                eipOff = res.EipOffset;
                if (cycAncla > 0)
                {{
                    double vMs = (double)(Interp32.Ejecutadas - anclaInsns) / cycAncla;
                    double rMs = relojJuego.Elapsed.TotalMilliseconds - anclaMs;
                    // Solo se frena si vamos ADELANTADOS. Ir por detras no se recupera: la
                    // maquina da lo que da y correr a destiempo seria peor que ir lento.
                    while (vMs > rMs + 2.0)
                    {{
                        Thread.Sleep(1);
                        rMs = relojJuego.Elapsed.TotalMilliseconds - anclaMs;
                    }}
                }}
                continue;
            }}
            if (res.Motivo == Interp32.Fin.IntSinServicio && res.IntNo == 0x21 && cpu.AH == 0x4C)
                exitReason = ""El juego volvio a DOS (salida limpia)"";
            else
                exitReason = ""El interprete de 32 bits se detuvo: "" + res;
            break;
        }}
    }}
    catch (PakuPort.Core.Platform.ProgramExitException) {{ exitReason = ""El juego volvio a DOS (INT 21h/4Ch)""; }}
    catch (Exception ex)
    {{
        error = ex.GetType().Name + "": "" + ex.Message;
        exitReason = ""Error: "" + ex.GetType().Name + ""  "" + ex.Message;
        if (Environment.GetEnvironmentVariable(""PAKU_TRACE_EX"") == ""1"")
            Console.Error.WriteLine(ex.ToString());
    }}
    finished = true;
}}) {{ IsBackground = true }}.Start();

while (!Raylib.WindowShouldClose())
{{
    if (Raylib.IsKeyPressed(KeyboardKey.F12)) break;

    // --- Input: teclado (la ruta que este mundo USA es la hardware: el juego engancha el
    // vector 09h y lee el 0x60 desde su propia ISR; ServicioIrq drena la cola y levanta IRQ1).
    // Las otras dos rutas (INT 16h + buffer de la BDA) se alimentan igual que en la sonda, por
    // si un juego LE mirara ahi.
    int rKey;
    while ((rKey = Raylib.GetKeyPressed()) != 0)
    {{
        if ((KeyboardKey)rKey == KeyboardKey.NumLock)
        {{
            numLock = !numLock;
            platform.Keyboard.EnqueueHostScancode(0x45);
            keysDown[rKey] = 0x45;
            continue;
        }}
        if ((KeyboardKey)rKey == KeyboardKey.F11)
        {{
            if (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl))
                aspectCorrection = !aspectCorrection;
            else
                Raylib.ToggleBorderlessWindowed();
            continue;
        }}
        if (((KeyboardKey)rKey == KeyboardKey.F1 || (KeyboardKey)rKey == KeyboardKey.F2 ||
             (KeyboardKey)rKey == KeyboardKey.F3) &&
            (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl)))
        {{
            if ((KeyboardKey)rKey == KeyboardKey.F1 && cyclesIdx > 0) cyclesIdx--;
            else if ((KeyboardKey)rKey == KeyboardKey.F2 && cyclesIdx < cyclesSteps.Length - 1) cyclesIdx++;
            else if ((KeyboardKey)rKey == KeyboardKey.F3)
                for (int _i = 0; _i < cyclesSteps.Length; _i++)
                    if (Math.Abs(cyclesSteps[_i] - _cycRef) < Math.Abs(cyclesSteps[cyclesIdx] - _cycRef))
                        cyclesIdx = _i;
            cpu.CyclesPerMs = cyclesSteps[cyclesIdx];
            speedOsdUntil = Raylib.GetTime() + 3.0;
            Console.Error.WriteLine($""[VELOCIDAD] cyclesPerMs={{cpu.CyclesPerMs}} (fabrica={{cyclesFactory}})"");
            continue;
        }}
        var (sc, ascii) = MapKey((KeyboardKey)rKey,
            Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift),
            numLock);
        if (sc != 0)
        {{
            if (!IsModifierScancode(sc))
            {{
                keyboard.EnqueueKey(sc, ascii);
                BiosKeyBuffer(mem, sc, ascii);
            }}
            platform.Keyboard.EnqueueHostScancode(sc);
            keysDown[rKey] = sc;
            keyRepeatAt[rKey] = Raylib.GetTime() + TypematicDelay;
        }}
    }}

    // Typematic: el hardware repite el make mientras la tecla siga pulsada.
    if (typematic && keysDown.Count > 0)
    {{
        double _tnow = Raylib.GetTime();
        foreach (var (k, _) in keysDown)
        {{
            if (!Raylib.IsKeyDown((KeyboardKey)k)) continue;
            if (!keyRepeatAt.TryGetValue(k, out double _due) || _tnow < _due) continue;
            var (rsc, rascii) = MapKey((KeyboardKey)k,
                Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift),
                numLock);
            if (rsc != 0)
            {{
                if (!IsModifierScancode(rsc))
                {{
                    keyboard.EnqueueKey(rsc, rascii);
                    BiosKeyBuffer(mem, rsc, rascii);
                }}
                platform.Keyboard.EnqueueHostScancode(rsc);
            }}
            keyRepeatAt[k] = _tnow + TypematicPeriod;
        }}
    }}

    // Break code al soltar: la ISR del juego mantiene su tabla de teclas pulsadas con los breaks.
    if (keysDown.Count > 0)
    {{
        List<int>? released = null;
        foreach (var (k, sc) in keysDown)
        {{
            if (Raylib.IsKeyUp((KeyboardKey)k))
            {{
                platform.Keyboard.EnqueueHostScancode((byte)(sc | 0x80));
                (released ??= new List<int>()).Add(k);
            }}
        }}
        if (released != null) foreach (var k in released) {{ keysDown.Remove(k); keyRepeatAt.Remove(k); }}
    }}

    // PAKU_AUTOKEYS (verificacion): una tecla cada ~10 frames; el make se MANTIENE 5 frames
    // antes del break (la clase de juegos que escanean que teclas estan pulsadas AHORA).
    autoKeyTick++;
    int autoKeyPhase = ((autoKeyTick % 10) + 10) % 10;
    if (autoKeys.Count > 0 && autoKeyPhase == 0)
    {{
        char c = autoKeys.Dequeue();
        if (c == '~') autoKeyTick -= 40;    // pausa ~30 frames
        else
        {{
            var (scan, ascii) = MapAutoKey(c);
            if (scan != 0)
            {{
                keyboard.EnqueueKey(scan, ascii);
                BiosKeyBuffer(mem, scan, ascii);
                platform.Keyboard.EnqueueHostScancode(scan);
                autoKeyBreak = scan;
            }}
        }}
    }}
    else if (autoKeyBreak != 0 && autoKeyPhase == 5)
    {{
        platform.Keyboard.EnqueueHostScancode((byte)(autoKeyBreak | 0x80));
        autoKeyBreak = 0;
    }}

    // --- Render: VRAM viva a 60 fps (Mode-X incluido; RenderFramebuffer es el mismo juez
    // que valido la pantalla de la sonda pixel a pixel contra DOSBox) ---
    var fb = platform.Video.RenderFramebuffer();
    int vw = platform.Video.Width, vh = platform.Video.Height;
    if (fb.Length == vw * vh * 4 && (vw != w || vh != h))
    {{
        // Un cambio de tamano de presentacion RECREA la textura y reescala todo. Si ocurre a
        // menudo —un juego cuyo CRTC baila entre dos geometrias— la imagen SALTA en pantalla, y
        // sin decirlo es indistinguible de un problema de render.
        Console.Error.WriteLine($""[VIDEO] presentacion {{w}}x{{h}} -> {{vw}}x{{vh}}"");
        w = vw; h = vh;
        Raylib.UnloadTexture(tex);
        img = new Image {{ Width = w, Height = h, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 }};
        tex = Raylib.LoadTextureFromImage(img);
    }}
    float pixelAspect = aspectCorrection ? ((float)w / h) * (3f / 4f) : 1f;
    float viewScale = Math.Min((float)Raylib.GetScreenWidth() / w,
                               (float)Raylib.GetScreenHeight() / (h * pixelAspect));
    if (escalaEntera && viewScale >= 1f) viewScale = (float)Math.Floor(viewScale);
    float viewOx = (Raylib.GetScreenWidth() - w * viewScale) / 2;
    float viewOy = (Raylib.GetScreenHeight() - h * pixelAspect * viewScale) / 2;
    if (fb.Length == w * h * 4)
        unsafe
        {{
            fixed (byte* ptr = fb)
                Raylib.UpdateTexture(tex, ptr);
        }}

    Raylib.BeginDrawing();
    Raylib.ClearBackground(Color.Black);
    Raylib.DrawTexturePro(tex,
        new Rectangle(0, 0, w, h),
        new Rectangle(viewOx, viewOy, w * viewScale, h * pixelAspect * viewScale),
        new System.Numerics.Vector2(0, 0), 0, Color.White);
    if (finished)
    {{
        int _oy = Raylib.GetScreenHeight() - 64;
        Raylib.DrawRectangle(0, _oy, Raylib.GetScreenWidth(), 64, new Color((byte)0, (byte)0, (byte)0, (byte)210));
        string _ov = exitReason.Length > 0 ? exitReason : ""El juego terminó"";
        if (_ov.Length > 74) _ov = _ov.Substring(0, 74) + ""..."";
        Raylib.DrawText(_ov, 12, _oy + 8, 18, exitReason.StartsWith(""Error"") ? Color.Red : Color.Yellow);
        Raylib.DrawText(""Pulsa F12 o cierra la ventana"", 12, _oy + 40, 14, Color.Gray);
    }}
    if (Raylib.GetTime() < speedOsdUntil)
    {{
        string _sp = $""velocidad {{cpu.CyclesPerMs}}  (DOSBox cycles)"";
        Raylib.DrawRectangle(8, 8, 12 + Raylib.MeasureText(_sp, 20), 32,
                             new Color((byte)0, (byte)0, (byte)0, (byte)190));
        Raylib.DrawText(_sp, 14, 14, 20, Color.Yellow);
    }}
    Raylib.EndDrawing();

    // Bombeo del stream de audio: por cada sub-buffer ya reproducido, drenar el anillo (silencio
    // si no hay bastante) y subirlo. El ritmo lo marca el avance del DMA de la tarjeta, acompasado
    // al reloj del invitado, asi que no se desborda ni se acelera.
    if (_hostAudio != null)
    {{
        if (_sbStreamReady)
        {{
            while (Raylib.IsAudioStreamProcessed(_sbStream))
            {{
                int _got = _hostAudio.Read(_audioBuf, _audioBuf.Length);
                for (int _i = _got; _i < _audioBuf.Length; _i++) _audioBuf[_i] = 0;
                Raylib.UpdateAudioStream(_sbStream, new ReadOnlySpan<short>(_audioBuf), _audioBuf.Length);
                if (_wavFs != null)
                {{
                    if (_wavBuf == null) _wavBuf = new byte[_audioBuf.Length * 2];
                    System.Buffer.BlockCopy(_audioBuf, 0, _wavBuf, 0, _audioBuf.Length * 2);
                    _wavFs.Write(_wavBuf, 0, _audioBuf.Length * 2);
                    _wavData += _audioBuf.Length * 2;
                }}
            }}
        }}
        else if (_wavFs != null)
        {{
            // SIN dispositivo de audio (PAKU_NOAUDIO) se escribe SOLO lo que el anillo tenia, sin
            // rellenar con silencio hasta completar el bufer. La rama de arriba SI rellena, y
            // debe: alli hay un dispositivo esperando un bloque entero cada vez. Aqui no hay
            // nadie a quien alimentar, y rellenar convertia el WAV en un reloj de FOTOGRAMAS en
            // vez de un reloj de AUDIO: la duracion del volcado salia proporcional a los fps del
            // juego. Medido — un juego a ~59 fps daba 247 s de WAV para 45 s de partida (2665
            // bufers de 2048 = 122.880 muestras/s) mientras otro, a ~11 fps, daba 45,0 s
            // clavados y parecia que el que estaba roto era el primero. El 84,9 % de silencio de
            // aquel volcado era este relleno, no el juego.
            int _got = _hostAudio.Read(_audioBuf, _audioBuf.Length);
            if (_got > 0)
            {{
                if (_wavBuf == null) _wavBuf = new byte[_audioBuf.Length * 2];
                System.Buffer.BlockCopy(_audioBuf, 0, _wavBuf, 0, _got * 2);
                _wavFs.Write(_wavBuf, 0, _got * 2);
                _wavData += _got * 2;
            }}
        }}
    }}

    if (finished && !exitHandled)
    {{
        exitHandled = true;
        try
        {{
            Directory.CreateDirectory(""logs"");
            File.WriteAllText(""logs/exit.log"",
                $""timestamp: {{DateTime.Now:yyyy-MM-dd HH:mm:ss}}\nmotivo: {{(exitReason.Length > 0 ? exitReason : ""terminado"")}}\ninstrucciones: {{Interp32.Ejecutadas}}\nirq_entregadas: {{svc.IrqEntregadas}}\n"");
            Raylib.TakeScreenshot(""logs/exit.png"");
            Console.Error.WriteLine($""[EXIT] {{(exitReason.Length > 0 ? exitReason : ""terminado"")}} -> logs/exit.log"");
        }}
        catch (Exception _lex) {{ Console.Error.WriteLine(""[EXIT] no pude volcar logs/exit.log: "" + _lex.Message); }}
    }}

    if (runClock.ElapsedMilliseconds >= _nextHb)
    {{
        _nextHb += 5000;
        Console.Error.WriteLine($""[HB] t={{runClock.ElapsedMilliseconds / 1000.0:F1}}s instr={{Interp32.Ejecutadas:N0}} irq={{svc.IrqEntregadas}} fps={{Raylib.GetFPS()}} cyc={{cpu.CyclesPerMs}} modo=0x{{platform.Video.VideoMode:X2}} {{platform.Video.Width}}x{{platform.Video.Height}}"");
    }}

    if (autoExitMs > 0 && runClock.ElapsedMilliseconds >= autoExitMs)
    {{
        int nonBlack = 0;
        for (int i = 0; i + 3 < fb.Length; i += 4)
            if (fb[i] != 0 || fb[i + 1] != 0 || fb[i + 2] != 0) nonBlack++;
        Console.WriteLine($""[autoexit] logica=arrancada terminada={{finished}} error={{error ?? ""ninguno""}} pixeles_no_negros={{nonBlack}}/{{w * h}}"");
        string? _memDump = Environment.GetEnvironmentVariable(""PAKU_MEMDUMP"");
        if (!string.IsNullOrEmpty(_memDump) && PakuPort.Core.Memory.Last != null)
            File.WriteAllBytes(_memDump, PakuPort.Core.Memory.Last.Raw);
        // PAKU_MEMDUMP_ALTO=<fichero>: la memoria PLANA (por encima de 1 MiB). En un juego de 32
        // bits todo lo que importa vive ahi —lo que descomprime al cargar un mapa, sus tablas—, y
        // PAKU_MEMDUMP solo ve el MiB de abajo: buscar ahi y no encontrar nada se lee como
        // «no esta» cuando lo que pasa es que no se ha mirado donde es.
        string? _memDumpAlto = Environment.GetEnvironmentVariable(""PAKU_MEMDUMP_ALTO"");
        if (!string.IsNullOrEmpty(_memDumpAlto) && PakuPort.Core.Memory.Last?.High is byte[] _alto)
        {{
            File.WriteAllBytes(_memDumpAlto, _alto);
            Console.WriteLine($""[MEMDUMP] {{_alto.Length / (1024 * 1024)}} MiB de memoria plana -> {{_memDumpAlto}}"");
        }}
        Raylib.TakeScreenshot(""screenshot.png"");
        string? _fbPng = Environment.GetEnvironmentVariable(""PAKU_FB_PNG"");
        if (!string.IsNullOrEmpty(_fbPng) && fb.Length == w * h * 4)
            unsafe
            {{
                fixed (byte* _fp = fb)
                {{
                    var _fi = new Image {{ Data = _fp, Width = w, Height = h, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 }};
                    Raylib.ExportImage(_fi, _fbPng);
                }}
            }}
        break;
    }}
}}

if (_wavFs != null)
{{
    try
    {{
        _wavFs.Seek(0, System.IO.SeekOrigin.Begin);
        var _bw = new System.IO.BinaryWriter(_wavFs);
        _bw.Write(new[] {{ 'R', 'I', 'F', 'F' }}); _bw.Write((uint)(36 + _wavData));
        _bw.Write(new[] {{ 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }});
        _bw.Write((uint)16); _bw.Write((ushort)1); _bw.Write((ushort)1);
        _bw.Write((uint)22050); _bw.Write((uint)44100); _bw.Write((ushort)2); _bw.Write((ushort)16);
        _bw.Write(new[] {{ 'd', 'a', 't', 'a' }}); _bw.Write((uint)_wavData);
        _bw.Flush(); _wavFs.Close();
        Console.Error.WriteLine(""[wav] "" + _wavData + "" bytes PCM -> "" + _wavPath);
    }}
    catch (Exception _we) {{ Console.Error.WriteLine(""[wav] cierre: "" + _we.Message); }}
}}
Raylib.UnloadTexture(tex);
if (_sbStreamReady) Raylib.UnloadAudioStream(_sbStream);
if (!_noAudio && Raylib.IsAudioDeviceReady()) Raylib.CloseAudioDevice();
platform.Mpu.Cerrar();   // el sintetizador del sistema es un recurso del SO: se suelta
Raylib.CloseWindow();

// --- Informe de la partida: TODO lo que el juego pidio y no le dimos ---
// El mundo de 16 bits deja `logs/gaps.log` con sus huecos y sus misses, y de ahi sale la
// autocuracion. Aqui no hay codigo lifteado ni huecos que sembrar —el juego se ejecuta entero—,
// pero SI hay tres cosas que pueden faltar y que el jugador no tiene por que saber diagnosticar:
// servicios de DOS/DPMI que rechazamos, ficheros que el juego busco y no estan, y peticiones a la
// tarjeta de sonido que no modelamos. Se escribe SIEMPRE al cerrar, no solo al petar: una partida
// que va bien tambien puede estar perdiendose algo en silencio, y de eso solo se entera quien
// juega. (Asi se supo que a la copia de un juego de prueba le faltaban cuatro efectos del original.)
try
{{
    Directory.CreateDirectory(""logs"");
    var _inf = new List<string>();
    _inf.Add($""partida del {{DateTime.Now:yyyy-MM-dd HH:mm:ss}}"");
    _inf.Add($""instrucciones={{Interp32.Ejecutadas}}  irq_entregadas={{svc.IrqEntregadas}}  "" +
             $""servicios_pedidos={{svc.Traza.Count}}  velocidad={{cpu.CyclesPerMs}}"");
    _inf.Add($""salida: {{(exitReason.Length > 0 ? exitReason : ""el usuario cerro la ventana"")}}"");
    _inf.Add("""");
    _inf.Add(""== servicios de DOS/DPMI NO implementados (el juego los pidio y le dijimos que no) =="");
    if (svc.Rechazados.Count == 0) _inf.Add(""  (ninguno)"");
    else foreach (var _g in svc.Rechazados.GroupBy(s => s).OrderByDescending(g => g.Count()))
        _inf.Add($""  x{{_g.Count(),-6}} {{_g.Key}}"");
    _inf.Add("""");
    _inf.Add(""== ficheros que el juego busco y no estan =="");
    if (svc.NoEncontrados.Count == 0) _inf.Add(""  (ninguno)"");
    else foreach (var _g in svc.NoEncontrados.GroupBy(s => s).OrderBy(g => g.Key))
        _inf.Add($""  x{{_g.Count(),-6}} {{_g.Key}}"");
    _inf.Add("""");
    _inf.Add(""== peticiones a la tarjeta de sonido NO modeladas =="");
    if (PakuPort.Core.Platform.SoundBlaster.NoModelado.Count == 0) _inf.Add(""  (ninguna)"");
    else foreach (var _g in PakuPort.Core.Platform.SoundBlaster.NoModelado.GroupBy(s => s))
        _inf.Add($""  x{{_g.Count(),-6}} {{_g.Key}}"");
    _inf.Add("""");
    _inf.Add(""== musica MIDI NO modelada =="");
    if (PakuPort.Core.Platform.Mpu401.NoModelado.Count == 0) _inf.Add(""  (nada)"");
    else foreach (var _g in PakuPort.Core.Platform.Mpu401.NoModelado.GroupBy(s => s))
        _inf.Add($""  x{{_g.Count(),-6}} {{_g.Key}}"");
    File.WriteAllLines(""logs/gaps.log"", _inf);
    Console.Error.WriteLine($""[GAP] informe de la partida -> logs/gaps.log "" +
        $""({{svc.Rechazados.Distinct().Count()}} servicios sin implementar, "" +
        $""{{svc.NoEncontrados.Distinct().Count()}} ficheros ausentes)"");
}}
catch (Exception _gex) {{ Console.Error.WriteLine(""[GAP] no pude escribir logs/gaps.log: "" + _gex.Message); }}

// Tecla al anillo del buffer de teclado de la BDA (40:1A head, 40:1C tail, 40:1E el anillo),
// que es donde la deja el INT 9 del BIOS de verdad. En el mundo plano la BDA no la inicializa
// nadie: head/tail a cero mandarian la escritura a la IVT (mismo helper que la sonda le-run).
static void BiosKeyBuffer(Memory mem, byte scan, byte ascii)
{{
    if (mem.RdU16(0x0040, 0x001A) < 0x001E || mem.RdU16(0x0040, 0x001A) >= 0x003E)
    {{
        mem.WrU16(0x0040, 0x001A, 0x001E);
        mem.WrU16(0x0040, 0x001C, 0x001E);
        mem.WrU16(0x0040, 0x0080, 0x001E);
        mem.WrU16(0x0040, 0x0082, 0x003E);
    }}
    ushort cola = mem.RdU16(0x0040, 0x001C);
    ushort sig = (ushort)(cola + 2);
    if (sig >= 0x003E) sig = 0x001E;
    if (sig == mem.RdU16(0x0040, 0x001A)) return;
    mem.WrU16(0x0040, cola, (ushort)((scan << 8) | ascii));
    mem.WrU16(0x0040, 0x001C, sig);
}}

// PAKU_AUTOKEYS: mismo mapa que la sonda le-run (MapProbeAutoKey del CLI) para que el arnes
// conduzca el juego exactamente igual que las sondas que lo validaron.
static (byte scan, byte ascii) MapAutoKey(char c) => c switch
{{
    '\r' => ((byte)0x1C, (byte)0x0D),
    ' '  => ((byte)0x39, (byte)0x20),
    '<'  => ((byte)0x4B, (byte)0x00),
    '>'  => ((byte)0x4D, (byte)0x00),
    '^'  => ((byte)0x48, (byte)0x00),
    'v'  => ((byte)0x50, (byte)0x00),
    '\x1b' => ((byte)0x01, (byte)0x1B),
    >= '0' and <= '9' => ((byte)(c == '0' ? 0x0B : 0x02 + (c - '1')), (byte)c),
    >= 'a' and <= 'z' => ((byte)""\x1e\x30\x2e\x20\x12\x21\x22\x23\x17\x24\x25\x26\x32\x31\x18\x19\x10\x13\x1f\x14\x16\x2f\x11\x2d\x15\x2c""[c - 'a'], (byte)c),
    _    => ((byte)0, (byte)0),
}};

static (byte scancode, byte ascii) MapKey(KeyboardKey k, bool shift, bool numLock)
{{
    bool kpDigit = shift ^ numLock;
    ReadOnlySpan<byte> QwertyScan = new byte[]
    {{
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
    }};
    return k switch
    {{
        KeyboardKey.Escape    => (0x01, 0x1B),
        KeyboardKey.Enter     => (0x1C, 0x0D),
        KeyboardKey.Space     => (0x39, 0x20),
        KeyboardKey.Backspace => (0x0E, 0x08),
        KeyboardKey.Tab       => (0x0F, 0x09),
        KeyboardKey.Up        => (0x48, 0x00),
        KeyboardKey.Down      => (0x50, 0x00),
        KeyboardKey.Left      => (0x4B, 0x00),
        KeyboardKey.Right     => (0x4D, 0x00),
        KeyboardKey.F1        => (0x3B, 0x00),
        KeyboardKey.F2        => (0x3C, 0x00),
        KeyboardKey.F3        => (0x3D, 0x00),
        KeyboardKey.F4        => (0x3E, 0x00),
        KeyboardKey.F5        => (0x3F, 0x00),
        KeyboardKey.F6        => (0x40, 0x00),
        KeyboardKey.F7        => (0x41, 0x00),
        KeyboardKey.F8        => (0x42, 0x00),
        KeyboardKey.F9        => (0x43, 0x00),
        KeyboardKey.F10       => (0x44, 0x00),
        >= KeyboardKey.A and <= KeyboardKey.Z =>
            (QwertyScan[k - KeyboardKey.A],
             (byte)(shift ? 'A' + (k - KeyboardKey.A) : 'a' + (k - KeyboardKey.A))),
        >= KeyboardKey.Zero and <= KeyboardKey.Nine =>
            ((byte)(k == KeyboardKey.Zero ? 0x0B : 0x02 + (int)(k - KeyboardKey.One)),
             (byte)(shift ? "")!@#$%^&*(""[k - KeyboardKey.Zero] : '0' + (k - KeyboardKey.Zero))),
        KeyboardKey.Home      => (0x47, 0x00),
        KeyboardKey.End       => (0x4F, 0x00),
        KeyboardKey.PageUp    => (0x49, 0x00),
        KeyboardKey.PageDown  => (0x51, 0x00),
        KeyboardKey.Insert    => (0x52, 0x00),
        KeyboardKey.Delete    => (0x53, 0x00),
        KeyboardKey.Grave     => (0x29, (byte)(shift ? '~' : '`')),
        KeyboardKey.Apostrophe   => (0x28, (byte)(shift ? '""' : '\'')),
        KeyboardKey.Backslash    => (0x2B, (byte)(shift ? '|' : '\\')),
        KeyboardKey.KpMultiply   => (0x37, (byte)'*'),
        KeyboardKey.KpDivide     => (0x35, (byte)'/'),
        KeyboardKey.Kp7        => (0x47, (byte)(kpDigit ? '7' : 0x00)),
        KeyboardKey.Kp8        => (0x48, (byte)(kpDigit ? '8' : 0x00)),
        KeyboardKey.Kp9        => (0x49, (byte)(kpDigit ? '9' : 0x00)),
        KeyboardKey.Kp4        => (0x4B, (byte)(kpDigit ? '4' : 0x00)),
        KeyboardKey.Kp5        => (0x4C, (byte)(kpDigit ? '5' : 0xF0)),
        KeyboardKey.Kp6        => (0x4D, (byte)(kpDigit ? '6' : 0x00)),
        KeyboardKey.Kp1        => (0x4F, (byte)(kpDigit ? '1' : 0x00)),
        KeyboardKey.Kp2        => (0x50, (byte)(kpDigit ? '2' : 0x00)),
        KeyboardKey.Kp3        => (0x51, (byte)(kpDigit ? '3' : 0x00)),
        KeyboardKey.Kp0        => (0x52, (byte)(kpDigit ? '0' : 0x00)),
        KeyboardKey.KpDecimal  => (0x53, (byte)(kpDigit ? '.' : 0x00)),
        KeyboardKey.KpAdd      => (0x4E, (byte)'+'),
        KeyboardKey.KpSubtract => (0x4A, (byte)'-'),
        KeyboardKey.KpEnter    => (0x1C, 0x0D),
        KeyboardKey.Minus      => (0x0C, (byte)(shift ? '_' : '-')),
        KeyboardKey.Equal      => (0x0D, (byte)(shift ? '+' : '=')),
        KeyboardKey.LeftBracket  => (0x1A, (byte)(shift ? '{{' : '[')),
        KeyboardKey.RightBracket => (0x1B, (byte)(shift ? '}}' : ']')),
        KeyboardKey.Semicolon  => (0x27, (byte)(shift ? ':' : ';')),
        KeyboardKey.Comma      => (0x33, (byte)(shift ? '<' : ',')),
        KeyboardKey.Period     => (0x34, (byte)(shift ? '>' : '.')),
        KeyboardKey.Slash      => (0x35, (byte)(shift ? '?' : '/')),
        KeyboardKey.LeftShift    => (0x2A, 0x00),
        KeyboardKey.RightShift   => (0x36, 0x00),
        KeyboardKey.LeftControl  => (0x1D, 0x00),
        KeyboardKey.RightControl => (0x1D, 0x00),
        KeyboardKey.LeftAlt      => (0x38, 0x00),
        KeyboardKey.RightAlt     => (0x38, 0x00),
        KeyboardKey.CapsLock     => (0x3A, 0x00),
        KeyboardKey.NumLock      => (0x45, 0x00),
        KeyboardKey.ScrollLock   => (0x46, 0x00),
        _ => (0, 0),
    }};
}}

static bool IsModifierScancode(byte sc) =>
    sc is 0x2A or 0x36 or 0x1D or 0x38 or 0x3A or 0x45 or 0x46;
";
    File.WriteAllText(Path.Combine(outDir, "Program.cs"), programCs);

    string csproj = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyName>{game.Id}-host</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Raylib-cs"" Version=""8.0.0"" />
    <PackageReference Include=""Iced"" Version=""1.21.0"" />
  </ItemGroup>
</Project>
";
    File.WriteAllText(Path.Combine(outDir, $"{game.Id}.csproj"), csproj);
    ExportCapabilities.Write(outDir, game.Id, gamePath, game.ExePath, game.CyclesPerMs, "le32",
        $"pakuport export --game \"{gamePath}\" --out \"{outDir}\"", supportsCapture: false);

    File.WriteAllText(Path.Combine(outDir, "README.md"),
        $"# {game.Title} — host LE de 32 bits generado por PakuPort\n\n" +
        $"El juego (formato LE + extensor DOS) corre TAL CUAL sobre Interp32 + FlatServices.\n" +
        $"Generado el {DateTime.Now:yyyy-MM-dd}.\n\n" +
        $"1. Copia los datos del juego a esta carpeta (junto a `{exeName}`)\n" +
        $"2. `dotnet run -c Release`\n");

    Console.WriteLine("  Proyecto generado:");
    Console.WriteLine($"    {game.Id}.csproj");
    Console.WriteLine("    Program.cs (host LE de 32 bits)");
    Console.WriteLine("    Core/ (Cpu, Memory, Interp32, LeLoader, Platform)");
    return 0;
}

static int CmdExport(GameConfig game, string outDir, string gamePath, bool bootReal = false, string? seedsFile = null, string? probeKeys = null, string? portadasDir = null)
{
    Console.WriteLine($"== PakuPort export: {game.Title} ==");
    // Mundo plano de 32 bits (LE + extensor DOS): no hay lifter ni corpus ni memimage — el juego
    // corre TAL CUAL en Interp32 sobre FlatServices. Rama aparte a propósito: la plantilla de 16
    // bits queda intacta (es la que sostiene a los juegos de 16 bits con los que se probo).
    if (game.Format == "le") return ExportLeHost(game, outDir, gamePath);
    if (!string.IsNullOrEmpty(probeKeys)) Console.WriteLine($"  --probe-keys: el probe de descubrimiento conduce con \"{probeKeys}\" (determinista por presupuesto)");
    Console.WriteLine($"  Salida: {Path.GetFullPath(outDir)}");
    if (bootReal) Console.WriteLine("  --boot-real: arranque por startup real (se ignora gameplay_snapshot)");

    // 1. Descubrir y traducir todas las funciones (igual que run)
    var exeBytes = File.ReadAllBytes(game.ExePath);
    byte[] imageBytes;
    if (game.Format == "com")
    {
        imageBytes = new byte[ExeImage.ResidentBase + 0x100 + exeBytes.Length];
        Array.Copy(exeBytes, 0, imageBytes, ExeImage.ResidentBase + 0x100, exeBytes.Length);
    }
    else
    {
        ushort exportLoadSeg2 = game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110;
        imageBytes = ExeImage.NormalizeMzImage(exeBytes, exportLoadSeg2);
        // EXEPACK+VROOMM: parchear la tabla de thunks residente con bytes descomprimidos reales
        int patchedThunks = PakuPort.Core.ExePackDecompressor.PatchResidentThunks(imageBytes);
        if (patchedThunks > 0) Console.WriteLine($"  EXEPACK: tabla de thunks parcheada ({patchedThunks} bytes)");
    }
    var exe = new ExeImage(imageBytes);
    exe.LoadSegment = game.LoadSegment > 0 ? game.LoadSegment : (ushort)0;
    exe.CodeMap = game.CodeMap;

    // Descubrir TODAS las funciones (corpus + CodeMap + call targets + prólogos)
    var seeds = CollectSeeds(game, exeBytes);
    bool isCom = game.Format == "com";

    // boot-real (opcional, genérico): si `dump-image` produjo memimage.bin/image.json,
    // el port exportado arrancará del startup REAL sobre la imagen de memoria
    // descomprimida (como `run --boot-real`), no del `main` C que descarrila.
    // Aquí: sembrar el realEntry para que su función se descubra y emita.
    byte[]? bootMem = null;
    int bootEntryKey = -1, bootRealCs = 0, bootRealIp = 0, bootRealSs = 0, bootRealSp = 0, bootLoadSeg = 0;
    {
        string exDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gamePath))!, "explorer");
        string memP = Path.Combine(exDir, "memimage.bin");
        string ijP = Path.Combine(exDir, "image.json");
        if (!isCom && File.Exists(memP) && File.Exists(ijP))
        {
            bootMem = File.ReadAllBytes(memP);
            using var ijDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ijP));
            var ijR = ijDoc.RootElement;
            int ijLs = HexToInt(ijR.GetProperty("loadSegment").GetString()!);
            // El MUNDO del memimage es el de image.json (regs, relocs EXEPACK y constantes
            // lifteadas relocan a ESE loadSegment) — el probe y el host deben cargarlo ahí,
            // no en el loadSegment de game.json (misma clase del bug "mundo equivocado" del
            // export snapshot).
            bootLoadSeg = ijLs;
            var ijRe = ijR.GetProperty("realEntry"); var ijRs = ijR.GetProperty("realStack");
            bootRealCs = HexToInt(ijRe.GetProperty("cs").GetString()!);
            bootRealIp = HexToInt(ijRe.GetProperty("ip").GetString()!);
            bootRealSs = HexToInt(ijRs.GetProperty("ss").GetString()!);
            bootRealSp = HexToInt(ijRs.GetProperty("sp").GetString()!);
            int bootBias = 0;
            if (ijR.TryGetProperty("memImage", out var miE) && miE.TryGetProperty("xOfGhidra0", out var xogE))
                bootBias = HexToInt(xogE.GetString()!);
            int bootGhidra = bootRealCs * 16 + bootRealIp - ijLs * 16 - bootBias;
            bootEntryKey = ExeImage.ResidentBase + bootGhidra;
            if (!seeds.Contains(bootEntryKey)) seeds.Insert(0, bootEntryKey);
            Console.WriteLine($"  boot-real: memimage {bootMem.Length} bytes, realEntry ghidra 0x{bootGhidra:X4} sembrado");

            // Imagen híbrida (como run --boot-real): el residente se decodifica del MUNDO
            // memimage, con las relocs del startup EXEPACK ya corridas. Sin esto el
            // lifteado lleva selectores/inmediatos de segmento PRE-reloc (p.ej. el crt0
            // carga DS=164B en vez de 264B) y el startup corre sobre datos basura.
            int bootResidentLen = Math.Min(OverlayDirectory.ResidentThreshold, imageBytes.Length) - ExeImage.ResidentBase;
            bootResidentLen = Math.Min(bootResidentLen, bootMem.Length - bootBias);
            if (bootResidentLen > 0)
            {
                Array.Copy(bootMem, bootBias, imageBytes, ExeImage.ResidentBase, bootResidentLen);
                exe = new ExeImage(imageBytes) { LoadSegment = exe.LoadSegment, CodeMap = exe.CodeMap, ImageBias = bootBias };
                Console.WriteLine($"  imagen híbrida: residente ({bootResidentLen:N0} bytes) del mundo memimage, overlays del fichero");
            }
        }
    }

    // gameplay-snapshot: si el explorador capturó un
    // estado DENTRO del juego (--gameplay --snap-at), el port arranca ahí — salta
    // el startup entero. Tiene prioridad sobre boot-real.
    byte[]? snapExpMem = null;
    System.Text.Json.JsonElement snapExpRoot = default;
    int snapEntryKey = -1, expImageBias = 0, snapEachKeyExp = -1, snapUntilOffExp = -1, expLoadSeg = 0;
    var expDriverRegions = new List<(ushort Seg, string Name, int KeyBase, int Len)>();
    {
        string exDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gamePath))!, "explorer");
        string sP = Path.Combine(exDir, "gameplay_snapshot.bin");
        string rP = Path.Combine(exDir, "gameplay_regs.json");
        string ijP2 = Path.Combine(exDir, "image.json");
        if (!isCom && File.Exists(ijP2))
        {
            using var ijD2 = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ijP2));
            if (ijD2.RootElement.TryGetProperty("memImage", out var mi2) &&
                mi2.TryGetProperty("xOfGhidra0", out var xog2))
                expImageBias = HexToInt(xog2.GetString()!);
            // El MUNDO del explorador (Unicorn) carga en SU loadSegment (image.json), NO en el
            // de game.json (mundo Plan-D/Spice86). Leer el snapshot con el segmento equivocado
            // compone un residente de datos basura (entrada Fn = adds/sysret) — es lo que
            // bloqueaba todo export gameplay-snapshot sin --boot-real.
            if (ijD2.RootElement.TryGetProperty("loadSegment", out var ijLsE))
                expLoadSeg = HexToInt(ijLsE.GetString()!);
        }
        if (!isCom && !bootReal && File.Exists(sP) && File.Exists(rP))
        {
            snapExpMem = File.ReadAllBytes(sP);
            using var sDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rP));
            snapExpRoot = sDoc.RootElement.Clone();
            int snapGhidra = HexToInt(snapExpRoot.GetProperty("func").GetProperty("ghidra").GetString()!);
            snapEntryKey = ExeImage.ResidentBase + snapGhidra;
            if (!seeds.Contains(snapEntryKey)) seeds.Insert(0, snapEntryKey);
            Console.WriteLine($"  gameplay-snapshot: {snapExpMem.Length:N0} bytes, func ghidra 0x{snapGhidra:X4} sembrado");

            // Callback de ratón (INT 33h AX=000C) congelado en el snapshot: es un entry
            // point real (el driver lo llama por eventos, nunca aparece en una traza de
            // ejecución del oráculo) — sembrarlo para que se liftee.
            if (snapExpRoot.TryGetProperty("int33Callback", out var mcbE))
            {
                ushort ls0 = expLoadSeg > 0 ? (ushort)expLoadSeg
                    : game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110;
                int mcbLin = HexToInt(mcbE.GetProperty("seg").GetString()!) * 16
                           + HexToInt(mcbE.GetProperty("off").GetString()!);
                int mcbKey = ExeImage.ResidentBase + mcbLin - ls0 * 16 - expImageBias;
                if (mcbKey > 0 && !seeds.Contains(mcbKey))
                {
                    seeds.Add(mcbKey);
                    Console.WriteLine($"  int33 callback: key 0x{mcbKey:X5} sembrado");
                }
            }

            // Imagen híbrida: el residente se decodifica del MUNDO snapshot (la imagen
            // normalizada difiere en la zona de thunks) + drivers EXEC lifteados.
            ushort ls2 = expLoadSeg > 0 ? (ushort)expLoadSeg
                : game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110;
            int srcOff = ls2 * 16 + expImageBias;
            int residentLen = Math.Min(OverlayDirectory.ResidentThreshold, imageBytes.Length) - ExeImage.ResidentBase;
            residentLen = Math.Min(residentLen, snapExpMem.Length - srcOff);
            if (residentLen > 0)
                Array.Copy(snapExpMem, srcOff, imageBytes, ExeImage.ResidentBase, residentLen);
            var expDriverSeeds = new List<int>();
            imageBytes = BuildDriverComposite(imageBytes, game, snapExpRoot,
                Path.GetDirectoryName(game.ExePath) ?? ".", expDriverRegions, expDriverSeeds,
                snapExpMem, ls2, expImageBias);
            foreach (int dsd in expDriverSeeds) if (!seeds.Contains(dsd)) seeds.Add(dsd);
            exe = new ExeImage(imageBytes) { LoadSegment = exe.LoadSegment, CodeMap = exe.CodeMap, ImageBias = expImageBias };

            if (game.SnapshotLoopEach != null)
            {
                snapEachKeyExp = ExeImage.ResidentBase + HexToInt(game.SnapshotLoopEach);
                if (!seeds.Contains(snapEachKeyExp)) seeds.Add(snapEachKeyExp);
                if (game.SnapshotLoopUntil != null) snapUntilOffExp = HexToInt(game.SnapshotLoopUntil);
            }
        }
    }
    // Fase 3 (autocuración): --seeds <fichero> siembra las direcciones
    // que el port tocó en RUNTIME (logs/gaps.log del export: "0xNNNNN" = hueco,
    // "miss 0xNNNNN" = FarDispatch/WindowDispatch sin case). El bucle de probe de abajo
    // solo ve lo que el probe ejercita; esto cierra el ciclo con lo que el JUGADOR tocó.
    if (seedsFile != null)
    {
        int added = 0, skipped = 0;
        foreach (string line in File.ReadAllLines(seedsFile))
        {
            var mHex = System.Text.RegularExpressions.Regex.Match(line, @"0x([0-9A-Fa-f]+)");
            if (!mHex.Success || !long.TryParse(mHex.Groups[1].Value,
                System.Globalization.NumberStyles.HexNumber, null, out long fkL)) continue;
            // Claves negativas (WindowDispatch en región no mapeada, Emitter.cs) y fuera
            // de imagen no son sembrables — se reportan para no ocultar trabajo pendiente.
            if (fkL < ExeImage.ResidentBase || fkL >= exe.Length) { skipped++; continue; }
            int fk = (int)fkL;
            if (!seeds.Contains(fk)) { seeds.Add(fk); added++; }
        }
        Console.WriteLine($"  --seeds {seedsFile}: {added} semillas nuevas" +
            (skipped > 0 ? $", {skipped} fuera de imagen (NO sembradas)" : ""));
    }
    Console.WriteLine($"  Semillas: {seeds.Count}");
    var allFuncs = Emitter.DiscoverAll(exe, game.Overlays, seeds, comMode: isCom, vroommStubTable: game.VroommStubTable);

    if (allFuncs.Count == 0)
    {
        Console.Error.WriteLine("  ERROR: no se descubrio ninguna funcion");
        return 2;
    }

    Console.WriteLine($"  Funciones descubiertas: {allFuncs.Count}");

    // Determinar entry point: entryOverride > primera semilla > cualquiera
    int firstKey;
    if (!string.IsNullOrEmpty(game.EntryOverride))
    {
        string eo = game.EntryOverride;
        if (eo.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) eo = eo[2..];
        firstKey = ExeImage.ResidentBase + Convert.ToInt32(eo, 16);
    }
    else
    {
        firstKey = seeds.FirstOrDefault(s => allFuncs.ContainsKey(s));
        if (firstKey == 0) firstKey = allFuncs.Keys.First();
    }

    // Discovery iterativo: compilar + probe para encontrar funciones llamadas indirectamente
    int imageEnd = Math.Min(exe.Length, ExeImage.ResidentBase + 0x30000);
    ushort exportLoadSeg = game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110;

    // COM con RETF trick: CS post-RETF para que tablas de salto indirecto resuelvan correctamente
    ushort comRetfCS = exportLoadSeg;
    if (isCom && !string.IsNullOrEmpty(game.EntryOverride))
    {
        string eo2 = game.EntryOverride;
        if (eo2.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) eo2 = eo2[2..];
        int entryOff2 = Convert.ToInt32(eo2, 16);
        if (entryOff2 > 0x100 && (entryOff2 & 0xF) == 0)
            comRetfCS = (ushort)(exportLoadSeg + entryOff2 / 16);
    }
    EmitResult em = default!;
    Action<Cpu, Memory>? entryDelegate = null;
    // Set compartido de claves ya decodificadas: DiscoverAll lo recibe y lo va ampliando, asi
    // cada iteracion del probe loop NO redescubre las funciones que ya conocemos.
    var knownKeys = new HashSet<int>();

    // Probe con teclas: descubrimiento por INTERSECCIÓN codemap∩ejecución. El código de
    // CONDUCCIÓN de un runtime enhebrado (p-code Turbo Basic de un juego de prueba) corre INLINE en el
    // intérprete (int-threading), invisible a los FarDispatch misses. Pero SÍ tiene entradas en
    // el codemap (radare/emulación = código verificado). Registramos qué claves del codemap
    // EJECUTA el intérprete durante la corrida conducida por --keys y las sembramos: doble
    // verificación (estática = codemap + dinámica = ejecutada), keys-driven, anti-envenenamiento
    // (nunca sembramos algo no ejecutado ni fuera del codemap). Determinista: interp + feeder por
    // presupuesto son deterministas. Sólo con --keys; el resto de juegos: null (cero cambio).
    var savedInterpOnStep = PakuPort.Core.Interp.OnStep;
    HashSet<int>? probeCodemapKeys = null;
    HashSet<int>? probeExecKeys = null;
    if (!string.IsNullOrEmpty(probeKeys) && snapExpMem == null && game.CodeMap != null && game.CodeMap.Count > 0)
    {
        // game.CodeMap ya está en el espacio de claves de fichero (ResidentBase + ghidra), el
        // MISMO que produce Interp.KeyOf / el descubrimiento. Sin transformación.
        probeCodemapKeys = new HashSet<int>(game.CodeMap);
        probeExecKeys = new HashSet<int>();
    }

    for (int iteration = 0; ; iteration++)
    {
        var funcList = allFuncs.Values.ToList();
        var entryFunc = funcList.FirstOrDefault(f => f.Key == firstKey);
        if (entryFunc != null) { funcList.Remove(entryFunc); funcList.Insert(0, entryFunc); }

        em = Emitter.EmitProgram(funcList, firstKey, vroommStub: game.VroommStub, softStubs: true,
            overlayDir: game.Overlays, vroommStubTable: game.VroommStubTable,
            overlayLoadBase: game.OverlayLoadBase, exe: exe, thunkRedirects: game.ThunkRedirects,
            imageBias: (snapExpMem != null || bootMem != null) ? expImageBias : 0);

        try { entryDelegate = FunctionVerifier.Compile(em.Source, em.Namespace, em.ClassName, em.MethodName); }
        catch (Exception cex) { Console.WriteLine($"  probe: compile en memoria fallo ({cex.Message.Split('\n')[0]}) — sin descubrimiento iterativo"); break; }

        if (iteration >= 3) break;

        // Probe run para recoger FarDispatch MISSes
        var probeMem = new Memory();
        if (isCom)
            Array.Copy(exeBytes, 0, probeMem.Raw, exportLoadSeg * 16 + 0x100, Math.Min(exeBytes.Length, 0xFFF0));
        else
            MzLoader.Load(exeBytes, probeMem, exportLoadSeg);
        MzLoader.SetupPsp(probeMem, (ushort)(exportLoadSeg - 0x10), (ushort)(exportLoadSeg - 0x20));
        MzLoader.SetupEnvironment(probeMem, (ushort)(exportLoadSeg - 0x20), game.ExePath);
        var (probePlatform, probeKeyboard, _, _) = PlatformFactory.CreateFull(Path.GetDirectoryName(game.ExePath) ?? ".");
        probeMem.Video = probePlatform.Video;
        // Discovery probe runs on a finite budget and must terminate. A lifted blocking
        // key read (INT 16h AH=0) would deadlock here — no key feeder runs in snapshot
        // mode, so make the probe's keyboard never block (returns "no key" on empty).
        probeKeyboard.NonBlockingRead = true;

        // Inyectar teclas en un hilo para que el probe no se bloquee en INT 16h.
        // NO con snapshot: el probe debe reproducir la sesión sin input (las teclas
        // mandan al juego por caminos de menú/salida y el probe termina en 5 pasos).
        // Con --probe-keys tampoco: el feeder DETERMINISTA por presupuesto (instalado tras
        // crear probeCpu) reemplaza a este hilo de reloj de pared (que es racy) para no
        // perturbar el conjunto descubierto — el determinismo del export es sagrado.
        bool legacyKeyThread = snapExpMem == null && string.IsNullOrEmpty(probeKeys);
        var probeKeysCts = new System.Threading.CancellationTokenSource();
        if (!legacyKeyThread) probeKeysCts.Cancel();
        var probeKeysTask = legacyKeyThread ? System.Threading.Tasks.Task.Run(() =>
        {
            var keys = new (byte scan, byte ascii)[]
            {
                // dígitos para menús de setup (p.ej. gráficos/sonido/controles); los juegos
                // sin setup los consumen como teclas normales sin efecto
                (0x03, (byte)'2'), (0x1C, 0x0D), (0x02, (byte)'1'), (0x1C, 0x0D),
                (0x02, (byte)'1'), (0x1C, 0x0D),
                (0x39, 0x20), (0x1C, 0x0D), // espacio, enter (start)
                (0x4B, 0x00), (0x4D, 0x00), (0x39, 0x20), // izq, der, fire
                (0x4B, 0x00), (0x4B, 0x00), (0x39, 0x20),
                (0x4D, 0x00), (0x4D, 0x00), (0x39, 0x20),
            };
            try
            {
                for (int round = 0; round < 50 && !probeKeysCts.Token.IsCancellationRequested; round++)
                    foreach (var (scan, ascii) in keys)
                    {
                        if (probeKeysCts.Token.IsCancellationRequested) return;
                        probeKeyboard.EnqueueKey(scan, ascii);
                        probeKeysCts.Token.WaitHandle.WaitOne(5);
                    }
            }
            catch (OperationCanceledException) { }
        }) : System.Threading.Tasks.Task.CompletedTask;

        var probeCpu = new Cpu
        {
            CS = isCom ? comRetfCS : (ushort)(exportLoadSeg + (exeBytes[22] | (exeBytes[23] << 8))),
            DS = exportLoadSeg, ES = exportLoadSeg,
            SS = isCom ? exportLoadSeg : (ushort)(exportLoadSeg + (exeBytes[14] | (exeBytes[15] << 8))),
            SP = isCom ? (ushort)0xFFFE : (ushort)(exeBytes[16] | (exeBytes[17] << 8)),
            Platform = probePlatform,
            LoadBase = exportLoadSeg * 16,
            FarDispatchMisses = new HashSet<int>(),
            // 50M: el startup real tarda decenas de millones de insns en llegar a cargar
            // sus drivers por EXEC (p.ej. un driver gráfico); con 5M el probe moría antes y los
            // drivers quedaban sin liftear (misses de pool irresolubles en runtime).
            Budget = 50_000_000,
        };
        if (isCom) probeCpu.AX = exportLoadSeg;

        // --probe-keys / port --keys: feeder de autokeys DETERMINISTA para el probe de
        // descubrimiento. Conduce el probe boot-real/plano tan hondo como el smoke
        // (portada→menú→gameplay), así el código sólo alcanzable tras input de menú (p.ej. el
        // bucle de partida de un juego de prueba) se descubre — no sólo lo que el probe sin teclas tropieza.
        // El probe no tiene reloj de frames del host; su noción de tiempo es el PRESUPUESTO de
        // instrucciones (Cpu.Budget): se alimenta una tecla cada `akSlotSteps` de presupuesto
        // consumido y '~' es un slot de pausa (semántica del host: un char por ~10 frames, '~' =
        // pausa). Al ir por presupuesto (no por reloj de pared en un hilo), dos corridas encolan
        // las mismas teclas en las mismas marcas → el conjunto descubierto es determinista. Sólo
        // el buffer INT 16h (única ruta de teclado que el probe sirve: la del 8042/IRQ1 necesita
        // MachineTimer.Poll + ServicePendingInterrupts, que el probe no cablea). OnPeriodicPoll
        // se dispara cada 4096 Steps en el hilo del juego (sin carrera).
        if (!string.IsNullOrEmpty(probeKeys) && snapExpMem == null)
        {
            // El probe con teclas conduce el juego HONDO (como el smoke). Para eso necesita el
            // MISMO runtime que el host donde el probe sin teclas no llega:
            //  1) Intérprete de respaldo ON: los huecos del código lifteado ejecutan los bytes
            //     ORIGINALES en vez de quedar mudos. Sin esto un VM enhebrado (p-code Turbo Basic
            //     de un juego de prueba) descarrila en el 1er hueco y sale en ~965 steps (INT21 4C). Espejo de la
            //     plantilla del host (Interp.Enabled/OverlayWindow*). FarRetRedirect sigue OFF en
            //     el probe (Cpu.FarRetRedirectEnabled sólo lo enciende el host) → guard probado,
            //     determinista.
            //  2) Timer del PIT (OnPeriodicPoll=MachineTimer.Poll): el intro del juego cronometra
            //     con el tick del PIT/BDA; sin él la lógica de espera no avanza. El feeder de
            //     autokeys se acopla al MISMO poll (una tecla cada akSlotSteps de presupuesto).
            PakuPort.Core.Interp.Enabled = true;
            PakuPort.Core.Interp.OverlayWindowSegDelta = (ushort)(game.VroommStub?.Seg ?? 0);
            PakuPort.Core.Interp.OverlayWindowIpBase = game.OverlayLoadBase;
            if (game.Overlays.Count > 0)
                PakuPort.Core.Interp.OverlayStarts = game.Overlays.Entries.ToDictionary(e => e.Ovno, e => e.Start);
            probeCpu.GapHits = new HashSet<int>();

            // Registrar las claves del codemap que el intérprete EJECUTA (misma fórmula que
            // Interp.KeyOf para código residente; drivers EXEC no aplican a juegos con --keys).
            // Contains por instrucción es barato y el set queda pequeño (sólo entradas del codemap).
            if (probeCodemapKeys != null)
                PakuPort.Core.Interp.OnStep = (cs, ip) =>
                {
                    int k = cs * 16 + ip - probeCpu.LoadBase - probeCpu.ImageBias + ExeImage.ResidentBase;
                    if (probeCodemapKeys.Contains(k)) probeExecKeys!.Add(k);
                };

            var akQ = new Queue<char>(probeKeys!.Replace("\\r", "\r").ToCharArray());
            long akBudget0 = probeCpu.Budget;
            const long akSlotSteps = 500_000; // coste en presupuesto de un slot de autokey (host: ~10 frames)
            long akNextSlot = akSlotSteps;
            var probeTimer = probePlatform.MachineTimer;
            probeCpu.OnFrameBoundary = c => probePlatform.ServicePendingInterrupts(c, probeMem);
            probeCpu.OnPeriodicPoll = c =>
            {
                probeTimer.Poll(probePlatform, c, probeMem); // avanza PIT/BDA + sirve IRQs pendientes
                long elapsed = akBudget0 - c.Budget;
                while (akQ.Count > 0 && elapsed >= akNextSlot)
                {
                    akNextSlot += akSlotSteps;
                    char ch = akQ.Dequeue();
                    if (ch == '~') continue; // slot de pausa: avanza el tiempo, no manda tecla
                    var (sc, ascii) = MapProbeAutoKey(ch);
                    probeKeyboard.EnqueueKey(sc, ascii);
                }
            };
        }
        if (snapExpMem != null)
        {
            // Probe sobre el MUNDO snapshot: los far targets dependen de datos del
            // juego (tablas de despacho, epílogos de cola) y solo aparecen ahí.
            Array.Copy(snapExpMem, 0, probeMem.Raw, 0, 0xA0000);
            probePlatform.AttachMemory(probeMem);
            PakuPort.Core.Platform.DosPlatform.RestoreGameVectors(probeMem, snapExpMem);
            var er = snapExpRoot.GetProperty("regs");
            ushort ER(string k) => (ushort)HexToInt(er.GetProperty(k).GetString()!);
            probeCpu.AX = ER("ax"); probeCpu.BX = ER("bx"); probeCpu.CX = ER("cx"); probeCpu.DX = ER("dx");
            probeCpu.SI = ER("si"); probeCpu.DI = ER("di"); probeCpu.BP = ER("bp"); probeCpu.SP = ER("sp");
            probeCpu.CS = ER("cs"); probeCpu.DS = ER("ds"); probeCpu.ES = ER("es"); probeCpu.SS = ER("ss");
            probeCpu.ImageBias = expImageBias;
            foreach (var e in snapExpRoot.GetProperty("execOvls").EnumerateArray())
            {
                ushort pseg = (ushort)e.GetProperty("seg").GetInt32();
                string pnm = e.GetProperty("name").GetString()!;
                var prg = expDriverRegions.FirstOrDefault(dr => dr.Seg == pseg && dr.Name.Equals(pnm, StringComparison.OrdinalIgnoreCase));
                PakuPort.Core.Platform.DriverRegistry.RegisterDriverSegment(pseg, pnm, prg.Name != null ? prg.KeyBase : -1);
            }
            var snapFn = PakuPort.Core.Verify.FunctionVerifier.GetCompiledMethod($"Fn_{snapEntryKey:X5}");
            if (snapFn == null) Console.WriteLine($"  probe-snapshot: Fn_{snapEntryKey:X5} NO encontrada en el compilado");
            try { (snapFn ?? entryDelegate)(probeCpu, probeMem); }
            catch (Exception pex) { Console.WriteLine($"  probe-snapshot: {pex.GetType().Name}: {pex.Message}"); }
            Console.WriteLine($"  probe-snapshot: budget restante {probeCpu.Budget}, misses {probeCpu.FarDispatchMisses!.Count}");
        }
        else if (bootMem != null)
        {
            // Probe boot-real: mismo mundo que el host real (memimage + realEntry +
            // ImageBias). Sin el bias, los MISSes se registran 0x129 arriba y el
            // bucle siembra claves de bytes corridos (la función real nunca se emite).
            // El mundo es el de image.json (bootLoadSeg): memimage, regs y constantes
            // lifteadas relocan a ese loadSegment, no al de game.json.
            ushort bootSeg = bootLoadSeg > 0 ? (ushort)bootLoadSeg : exportLoadSeg;
            Array.Copy(bootMem, 0, probeMem.Raw, bootSeg * 16, Math.Min(bootMem.Length, probeMem.Raw.Length - bootSeg * 16));
            probeCpu.CS = (ushort)bootRealCs;
            probeCpu.SS = (ushort)bootRealSs;
            probeCpu.SP = (ushort)bootRealSp;
            probeCpu.DS = (ushort)(bootSeg - 0x10);
            probeCpu.ES = (ushort)(bootSeg - 0x10);
            probeCpu.LoadBase = bootSeg * 16;
            probeCpu.ImageBias = expImageBias;
            probePlatform.AttachMemory(probeMem);
            PakuPort.Core.Platform.Int21hHandler.ExecLoads.Clear();
            // keyBases de drivers ya lifteados en iteraciones previas: el 4B03 del probe
            // los resolverá por nombre (igual que hará el host real)
            foreach (var drk in expDriverRegions)
                PakuPort.Core.Platform.DriverRegistry.DriverKeyBases[drk.Name] = drk.KeyBase;
            var bootFn = PakuPort.Core.Verify.FunctionVerifier.GetCompiledMethod($"Fn_{bootEntryKey:X5}");
            if (bootFn == null) Console.WriteLine($"  probe-boot: Fn_{bootEntryKey:X5} NO encontrada en el compilado");
            // OJO: si el juego enhebra su bucle principal con llamadas far que el lifteado
            // convierte en llamadas C# anidadas en vez de retornos, la pila crece sin límite y el
            // StackOverflow resultante NO es capturable en .NET: se lleva el proceso entero y el
            // port muere sin exportar. Es el muro que destapo un juego de prueba: ciclo cerrado
            // Fn_0A8B6 -> Fn_3141F -> Fn_28CAD -> Fn_33489 -> Fn_0A8B6. Probado y descartado con
            // medida: un hilo con 256 MB de pila tampoco llega — la recursión es ILIMITADA, no
            // profunda, así que el Budget nunca se agota antes. Hace falta cortar por profundidad.
            try { (bootFn ?? entryDelegate)(probeCpu, probeMem); } catch (Exception pex) { Console.WriteLine($"  probe-boot: {pex.GetType().Name}: {pex.Message}"); }
            Console.WriteLine($"  probe-boot: budget restante {probeCpu.Budget}, misses {probeCpu.FarDispatchMisses!.Count} [{string.Join(",", probeCpu.FarDispatchMisses.Select(k => k.ToString("X")))}]");

            // Drivers cargados por EXEC durante el probe (drivers graficos, de sonido...): liftearlos
            // como pseudo-overlays con las relocs del segmento que asignó el allocator
            // (determinista: el runtime real asignará el mismo). El host los registra por
            // NOMBRE (DriverKeyBases), así el seg concreto del runtime da igual.
            var mzLoads = PakuPort.Core.Platform.Int21hHandler.ExecLoads
                .Where(l => !expDriverRegions.Any(r => r.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(l => l.Name.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => (g.Last().Seg, (int)g.Last().Reloc));
            if (mzLoads.Count > 0)
            {
                var newSeedsDrv = new List<int>();
                imageBytes = BuildDriverCompositeFromList(imageBytes, game, mzLoads,
                    Path.GetDirectoryName(game.ExePath) ?? ".", expDriverRegions, newSeedsDrv,
                    probeMem.Raw, bootSeg, expImageBias);
                exe = new ExeImage(imageBytes) { LoadSegment = exe.LoadSegment, CodeMap = exe.CodeMap, ImageBias = expImageBias };
                int added = 0;
                foreach (int ds in newSeedsDrv) if (!seeds.Contains(ds)) { seeds.Add(ds); added++; }
                if (expDriverRegions.Count > 0 && added > 0)
                {
                    Console.WriteLine($"  drivers EXEC del probe: {mzLoads.Count} módulos, {added} semillas — re-descubriendo...");
                    var deltaDrv = Emitter.DiscoverAll(exe, game.Overlays, seeds, comMode: isCom, vroommStubTable: game.VroommStubTable, known: knownKeys);
                    foreach (var fn in deltaDrv.Values) { allFuncs[fn.Key] = fn; knownKeys.Add(fn.Key); }
                    Console.WriteLine($"  Funciones descubiertas: {allFuncs.Count} (+{deltaDrv.Count} drivers)");
                    continue; // re-emit con los drivers lifteados
                }
            }
        }
        else
        {
            try { entryDelegate(probeCpu, probeMem); } catch { }
        }
        probeKeysCts.Cancel();
        try { probeKeysTask.Wait(1000); } catch { }

        int newSeeds = 0;
        foreach (int fk in probeCpu.FarDispatchMisses)
        {
            // exe.Length dinámico: la imagen crece con pseudo-overlays de drivers (0x40000+)
            // y zona boot; el cap estático imageEnd los dejaría fuera y sus misses jamás
            // se sembrarían.
            if (fk < ExeImage.ResidentBase || fk >= Math.Max(imageEnd, exe.Length)) continue;
            if (knownKeys.Contains(fk)) continue;
            if (!seeds.Contains(fk)) { seeds.Add(fk); newSeeds++; }
        }
        // Probe con teclas: sembrar las entradas del codemap que el intérprete EJECUTÓ (código de
        // conducción enhebrado, invisible a los misses). Doble-verificadas (codemap + ejecución).
        int execSeeds = 0;
        if (probeExecKeys != null)
            foreach (int ek in probeExecKeys)
            {
                if (ek < ExeImage.ResidentBase || ek >= Math.Max(imageEnd, exe.Length)) continue;
                if (knownKeys.Contains(ek)) continue;
                if (!seeds.Contains(ek)) { seeds.Add(ek); execSeeds++; }
            }
        if (execSeeds > 0)
            Console.WriteLine($"  {execSeeds} entradas codemap ejecutadas por el intérprete conducido (--keys)");
        newSeeds += execSeeds;
        if (newSeeds == 0) break;

        Console.WriteLine($"  {newSeeds} funciones descubiertas via probe (iteración {iteration + 1})");
        // DiscoverAll(known:) devuelve SOLO el delta (funciones aún no conocidas).
        // Acumular en allFuncs — NO reemplazar, o el emit siguiente perdería el corpus.
        var delta = Emitter.DiscoverAll(exe, game.Overlays, seeds, comMode: isCom, vroommStubTable: game.VroommStubTable, known: knownKeys);
        foreach (var fn in delta.Values) { allFuncs[fn.Key] = fn; knownKeys.Add(fn.Key); }
        Console.WriteLine($"  Funciones descubiertas: {allFuncs.Count} (+{delta.Count} nuevas)");
    }
    PakuPort.Core.Interp.OnStep = savedInterpOnStep; // no filtrar el hook al resto del export

    Console.WriteLine($"  Opcodes: {em.OpcodesUsed.Count} tipos, {em.NotSupported.Count} no soportados");
    if (em.NotSupported.Count > 0)
        Console.WriteLine("  No soportados: " + string.Join("; ", em.NotSupported.Distinct().Take(20)));

    // 2. Generar el proyecto .NET
    Directory.CreateDirectory(outDir);

    // 2a. Código traducido del juego. Particionar en varios ficheros si es grande:
    // un juego grande son miles de funciones = decenas de MB en un solo .cs que
    // ahoga a Roslyn. La clase es `partial`, así que N ficheros compilan en una clase.
    foreach (var old in Directory.GetFiles(outDir, "GameCode*.cs")) File.Delete(old);
    int gcParts = WritePartitionedGameCode(outDir, em.Source);
    Console.WriteLine($"  GameCode: {em.Source.Length:N0} caracteres, {allFuncs.Count} funciones, {gcParts} fichero(s)");

    // 2a2. Exportar DGROUP snapshot del corpus (estado inicial del juego)
    bool hasDgroup = false;
    if (Directory.Exists(game.CorpusDir))
    {
        // DGROUP snapshot = estado de datos inicial del juego. Preferir la función
        // de ENTRADA (su EntryMem es el estado "arranque"), derivada genéricamente
        // del config/MZ — NO de nombres de un juego concreto.
        var preferred = new List<string>();
        if (!string.IsNullOrEmpty(game.EntryOverride))
            preferred.Add($"FUN_1000_{HexToInt(game.EntryOverride):x4}");
        if (!isCom)
        {
            int mzEntryGhidra = (exeBytes[22] | (exeBytes[23] << 8)) * 16 + (exeBytes[20] | (exeBytes[21] << 8));
            preferred.Add($"FUN_1000_{mzEntryGhidra:x4}");
        }
        string? usedFunc = null;
        foreach (string pref in preferred)
        {
            var cases = PakuPort.Core.Verify.FunctionVerifier.LoadCorpus(game.CorpusDir, pref);
            if (cases.Count > 0 && cases[0].EntryMem.Length > 0)
            {
                File.WriteAllBytes(Path.Combine(outDir, "dgroup.bin"), cases[0].EntryMem);
                usedFunc = pref;
                hasDgroup = true;
                break;
            }
        }
        if (!hasDgroup)
        {
            foreach (var dir in Directory.GetDirectories(game.CorpusDir))
            {
                string funcName = Path.GetFileName(dir);
                var cases = PakuPort.Core.Verify.FunctionVerifier.LoadCorpus(game.CorpusDir, funcName);
                if (cases.Count > 0 && cases[0].EntryMem.Length > 0)
                {
                    File.WriteAllBytes(Path.Combine(outDir, "dgroup.bin"), cases[0].EntryMem);
                    usedFunc = funcName;
                    hasDgroup = true;
                    break;
                }
            }
        }
        if (hasDgroup)
            Console.WriteLine($"  dgroup.bin: {new FileInfo(Path.Combine(outDir, "dgroup.bin")).Length:N0} bytes (de {usedFunc} case 0)");
    }

    // 2a3. boot-real: llevar la imagen de memoria al port
    if (bootMem != null)
    {
        File.WriteAllBytes(Path.Combine(outDir, "memimage.bin"), bootMem);
        Console.WriteLine($"  memimage.bin: {bootMem.Length:N0} bytes (boot-real: arranque del startup)");
    }

    // 2a4. gameplay-snapshot: llevar el estado de gameplay al port
    if (snapExpMem != null)
    {
        File.WriteAllBytes(Path.Combine(outDir, "gameplay_snapshot.bin"), snapExpMem);
        string exDirC = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gamePath))!, "explorer");
        File.Copy(Path.Combine(exDirC, "gameplay_regs.json"), Path.Combine(outDir, "gameplay_regs.json"), overwrite: true);
        Console.WriteLine($"  gameplay_snapshot.bin + gameplay_regs.json copiados (arranque dentro del juego)");
    }

    // 2b. Copiar los archivos core de PakuPort que el juego necesita
    CopyCoreToExport(outDir);

    // 2d. Para COM con RETF trick: CS debe apuntar al segmento post-RETF para que
    //     las tablas de salto indirecto resuelvan contra las labels correctas.
    string comCsExpr = "loadSeg";
    if (game.Format == "com" && !string.IsNullOrEmpty(game.EntryOverride))
    {
        string eo = game.EntryOverride;
        if (eo.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) eo = eo[2..];
        int entryOff = Convert.ToInt32(eo, 16);
        if (entryOff > 0x100 && (entryOff & 0xF) == 0)
            comCsExpr = $"(ushort)(loadSeg + 0x{entryOff / 16:X})";
    }

    // Mundo "DOS mínimo real" (game.json dosMap:"real"): el host construye el mapa convencional
    // del DOS real (Int21hHandler.BootRealDos) y el raíz se carga por first-fit como en
    // DOS_Execute. Para COM el PSP fiel ES el segmento de código (CS=DS=SS=ES=PSP, imagen en
    // PSP:0100) — game.json.loadSegment debe ser ese PSP.
    //
    // Antes se exigía además `bootMem == null`, y como TODOS los juegos porteados tienen
    // memimage, el mapa real no se construía NUNCA: el programa raíz corría sin bloque propio de
    // memoria y con la arena por defecto (0x7000-0x9F00, 188 KB) en vez del pool del DOS. Los
    // primeros juegos probados no lo notaron porque no reservan memoria de verdad; uno que sí lo
    // hace abortaba con su propio mensaje, "Insufficient memory by 190496 bytes". La condición sobraba: el
    // mundo boot-real usa el loadSegment de image.json, que en estos juegos ES el PSP+0x10 que
    // produce el mapa real (0x0814 → 0x0824, verificado en el dump-image), y si algún día no
    // coincidiera el `throw` de abajo lo dice a gritos en vez de divergir en silencio. Además, en
    // interp-only —el motor por defecto— el memimage ni se carga: el juego es un EXE cargado
    // normalmente, que es justo el caso para el que existe el mapa.
    bool realDosMap = game.DosMap == "real" && snapExpMem == null;

    // Política XScale del ratón (knob por juego, sesiones 18-19): en modos de 320 de ancho el
    // driver Microsoft real presenta X virtual 0..639 (los juegos hacen shr cx,1). Se resuelve
    // AQUÍ, en generación, según el mundo del export: un snapshot/memimage CONGELADO capturó su
    // estado con la bandera 640-virtual ya horneada (medido en un juego: OFF → 1); un arranque fresco por
    // game.json/boot-real reproduce el driver DOS real, así que lo fiel a DOSBox es virtual-640
    // (un juego con dosMap=real → 2). game.json manda si mouseXScale != 0.
    int resolvedMouseXScale = game.MouseXScale != 0
        ? game.MouseXScale
        : (snapExpMem != null || bootMem != null) ? 1 : 2;

    // Entrada del hilo de lógica. PAKU_INTERP_ONLY=1 corre el juego 100% en el intérprete
    // (semántica 8086, el lifteado ni se toca); default = llamada al entry lifteado.
    string liftedEntryCall = $"{em.Namespace}.{em.ClassName}." +
        (snapEntryKey >= 0 ? $"Fn_{snapEntryKey:X5}" : bootMem != null ? $"Fn_{bootEntryKey:X5}" : em.MethodName) +
        "(cpu, mem);";
    string interpOnlyEntry;
    if (bootMem != null)
    {
        // boot-real (variante validada en dos juegos de prueba): NO copiar memimage; arrancar por el header
        // del MZ y correr el startup REAL (EXEPACK+VROOMM) dentro del intérprete.
        interpOnlyEntry =
            "                // interpOnly: motor 100% interprete (semantica 8086 real para\n" +
            "                // todo el flujo; el lifteado ni se toca). Resuelto arriba (env + interpDefault).\n" +
            "                if (interpOnly)\n" +
            "                {\n" +
            "                    // Boot 100% REAL: sin memimage (la ventana VROOMM debe arrancar como la\n" +
            "                    // deja el MZ load); entrada del header, EXEPACK+VROOMM corren de verdad.\n" +
            "                    cpu.CS = (ushort)(mz.InitCS + loadSeg);\n" +
            "                    cpu.SS = (ushort)(mz.InitSS + loadSeg);\n" +
            "                    cpu.SP = mz.InitSP;\n" +
            "                    cpu.DS = pspSeg; cpu.ES = pspSeg;\n" +
            "                    PakuPort.Core.Interp.WholeProgram = true;\n" +
            "                    PakuPort.Core.Interp.Run(cpu, mem, cpu.CS, mz.InitIP, null, null);\n" +
            "                }\n" +
            "                else\n" +
            "                " + liftedEntryCall;
    }
    else if (snapEntryKey < 0)
    {
        // Mundo lifteado plano (COM o main MZ): el intérprete entra donde entraría el
        // lifteado. COM = entrada canónica loadSeg:0100 (el shift de comCsExpr es artefacto
        // del lifter); MZ = cpu.CS(header):mz.AbsIP. Las CPU regs ya están puestas arriba.
        string entrySeg = game.Format == "com" ? "loadSeg" : "cpu.CS";
        string entryIp = game.Format == "com" ? "0x0100" : "mz.AbsIP";
        interpOnlyEntry =
            "                // interpOnly: motor 100% interprete (semantica 8086, lifteado sin tocar).\n" +
            "                if (interpOnly)\n" +
            "                {\n" +
            "                    PakuPort.Core.Interp.WholeProgram = true;\n" +
            $"                    PakuPort.Core.Interp.Run(cpu, mem, {entrySeg}, {entryIp}, null, null);\n" +
            "                }\n" +
            "                else\n" +
            "                " + liftedEntryCall;
    }
    else
    {
        // gameplay-snapshot: captura a mitad de partida con bucle de sesión posterior que
        // asume ejecución lifteada. Un interp-only aquí requiere decisión de diseño (entrada
        // a mitad, recuperación de IP): PAKU_INTERP_ONLY se ignora, comportamiento intacto.
        interpOnlyEntry = liftedEntryCall;
    }

    // Dedicatoria opcional (game.json "dedication"): entrega personalizada. Vacía = el
    // host sale exactamente igual que siempre, así que no altera a los demás juegos.
    string dedicationTitle = string.IsNullOrEmpty(game.Dedication) ? "" : " — " + game.Dedication;
    string dedicationSplash = string.IsNullOrEmpty(game.Dedication) ? "" :
        $@"    Raylib.DrawText(""{game.Dedication}"", Raylib.GetScreenWidth() / 2 - 150, Raylib.GetScreenHeight() / 2 - 110, 18, Color.Gold);";

    // Program.cs — host con ventana Raylib
    string programCs = $@"using PakuPort.Core;
using PakuPort.Core.Platform;
using Raylib_cs;
using System.Runtime.InteropServices;

// Port generado por PakuPort — {game.Title}
// {allFuncs.Count} funciones traducidas de x86-16 a C#

// Entregable portable: si los ficheros del juego están junto al ejecutable (publish
// single-file) pero no en el cwd, trabajar desde el directorio del ejecutable.
// OJO: File.Exists es insensible a mayusculas en Windows -- si el nombre del juego DOS
// coincide (salvo mayusculas) con el del propio host .NET generado (mismo nombre de
// proyecto), un chequeo normal confundiria el host consigo mismo. ExistsExactCase exige
// coincidencia EXACTA de mayusculas/minusculas para evitar esa colision.
static bool ExistsExactCase(string dir, string name)
{{
    if (!Directory.Exists(dir)) return false;
    foreach (var f in Directory.EnumerateFiles(dir))
        if (string.Equals(Path.GetFileName(f), name, StringComparison.Ordinal)) return true;
    return false;
}}
if (!ExistsExactCase(""."", ""{Path.GetFileName(game.ExePath)}"") &&
    ExistsExactCase(AppContext.BaseDirectory, ""{Path.GetFileName(game.ExePath)}""))
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var exeBytes = File.ReadAllBytes(""{Path.GetFileName(game.ExePath).Replace("\\", "\\\\")}"");
// Motor por defecto: interpOnly=true corre el juego 100% en el interprete (semantica 8086 real).
// PAKU_INTERP_ONLY=1 lo fuerza siempre; con interpDefault horneado, sin env vars = interprete y
// PAKU_LIFTED=1 vuelve al mundo lifteado de siempre. INTERP_DEFAULT se hornea desde game.json.
// Y si el remake trae el PROGRAMA entero traducido (FuncionesPortadas.RegistraPrograma), el motor
// es el interprete: es donde vive el gancho de las traducciones.
bool interpOnly = Environment.GetEnvironmentVariable(""PAKU_INTERP_ONLY"") == ""1""
    || PakuPort.Core.FuncionesPortadas.HayPrograma
    || ({(game.InterpDefault ? "true" : "false")} && Environment.GetEnvironmentVariable(""PAKU_LIFTED"") != ""1"");
var mem = new Memory();
ushort loadSeg = 0x{((snapExpMem != null || bootMem != null) && expLoadSeg > 0 ? (ushort)expLoadSeg : game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110):X4}; // {((snapExpMem != null || bootMem != null) && expLoadSeg > 0 ? "mundo snapshot/memimage (image.json): LoadBase/Interp dependen de él" : realDosMap ? "mundo game.json (dosMap=real: PSP fiel del mapa DOS)" : "mundo game.json")}
{(realDosMap ? $@"// Mundo ""DOS mínimo real"": mapa convencional fiel al oráculo + raíz por first-fit
// (semántica DOS_Execute). {(game.Format == "com" ? "COM fiel: PSP = segmento de código, imagen en PSP:0100." : "PSP = loadSeg-0x10 (módulo MZ en loadSeg).")}
// El código lifteado está relocado contra loadSegment: si el mapa produce otro PSP,
// fallar RUIDOSO aquí antes que divergir mudo.
ushort pspSeg = {(game.Format == "com" ? "loadSeg" : "(ushort)(loadSeg - 0x10)")};
var bootDos = PakuPort.Core.Platform.Int21hHandler.BootRealDos(mem, ""{Path.GetFileName(game.ExePath)}"", exeBytes);
if (bootDos.Psp != pspSeg)
    throw new InvalidOperationException($""dosMap=real: PSP del mapa 0x{{bootDos.Psp:X4}} != loadSegment de game.json — re-exportar con loadSegment=PSP"");
{(game.Format == "com"
? @"var mz = MzLoader.LoadCom(exeBytes, mem, loadSeg);
mem.WrU16(pspSeg, 0xFFFE, 0); // palabra 0 al tope de la pila del COM (retorno al PSP), como DOS"
: @"var mz = MzLoader.Load(exeBytes, mem, loadSeg);")}
MzLoader.SetupPsp(mem, pspSeg, bootDos.Env, (ushort)(bootDos.Psp + bootDos.Granted), cmdTail: ""{game.CmdTail}"");" : $@"ushort pspSeg = (ushort)(loadSeg - 0x10);
{(game.Format == "com"
? @"var mz = MzLoader.LoadCom(exeBytes, mem, loadSeg);"
: @"var mz = MzLoader.Load(exeBytes, mem, loadSeg);")}
MzLoader.SetupPsp(mem, pspSeg, (ushort)(pspSeg - 0x10), cmdTail: ""{game.CmdTail}"");
MzLoader.SetupEnvironment(mem, (ushort)(pspSeg - 0x10), ""{Path.GetFileName(game.ExePath)}"");")}
{(snapEntryKey >= 0 ? @"
// gameplay-snapshot (Camino A): arrancar DENTRO del juego con el estado capturado
// por el explorador en la entrada de una función de gameplay. Salta el startup.
byte[]? snapBytes = null;
ushort[] snapRegs = new ushort[13];
byte[]? snapPal = null;
var snapDrivers = new List<(ushort seg, string name)>();
var snapHandles = new List<(int h, string name, long pos)>();
(ushort seg, ushort off, ushort mask)? snapMouseCb = null;
if (File.Exists(""gameplay_snapshot.bin"") && File.Exists(""gameplay_regs.json""))
{
    snapBytes = File.ReadAllBytes(""gameplay_snapshot.bin"");
    Array.Copy(snapBytes, 0, mem.Raw, 0, 0xA0000);
    using var sd = System.Text.Json.JsonDocument.Parse(File.ReadAllText(""gameplay_regs.json""));
    var sr = sd.RootElement.GetProperty(""regs"");
    string[] rn = { ""ax"", ""bx"", ""cx"", ""dx"", ""si"", ""di"", ""bp"", ""sp"", ""cs"", ""ds"", ""es"", ""ss"", ""flags"" };
    for (int i = 0; i < rn.Length; i++)
        snapRegs[i] = (ushort)Convert.ToInt32(sr.GetProperty(rn[i]).GetString()!, 16);
    string ph = sd.RootElement.GetProperty(""palette"").GetString() ?? """";
    if (ph.Length == 1536 && ph.Any(c0 => c0 != '0'))
    {
        snapPal = new byte[768];
        for (int i = 0; i < 768; i++) snapPal[i] = Convert.ToByte(ph.Substring(i * 2, 2), 16);
    }
    foreach (var e in sd.RootElement.GetProperty(""execOvls"").EnumerateArray())
        snapDrivers.Add(((ushort)e.GetProperty(""seg"").GetInt32(), e.GetProperty(""name"").GetString()!));
    // handles del kernel DOS del oráculo: reabrir con el MISMO número (el juego los
    // guarda en su memoria como FILE._file)
    if (sd.RootElement.TryGetProperty(""handles"", out var hs))
        foreach (var hp in hs.EnumerateObject())
            snapHandles.Add((int.Parse(hp.Name), hp.Value.GetProperty(""name"").GetString() ?? """",
                             hp.Value.GetProperty(""pos"").GetInt64()));
    // Callback de ratón (INT 33h AX=000C) instalado por el juego ANTES del snapshot:
    // el port debe re-sembrarlo (la llamada de instalación quedó congelada en el pasado).
    if (sd.RootElement.TryGetProperty(""int33Callback"", out var mcb))
        snapMouseCb = ((ushort)Convert.ToInt32(mcb.GetProperty(""seg"").GetString()!, 16),
                       (ushort)Convert.ToInt32(mcb.GetProperty(""off"").GetString()!, 16),
                       (ushort)Convert.ToInt32(mcb.GetProperty(""mask"").GetString()!, 16));
}" : bootMem != null ? @"
// boot-real: sustituir por la imagen de MEMORIA descomprimida (X-space) y correr el
// startup REAL del juego (puebla su DGROUP, engancha sus ISR), como `run --boot-real`.
if (File.Exists(""memimage.bin"") && !interpOnly)
{
    var mi = File.ReadAllBytes(""memimage.bin"");
    Array.Copy(mi, 0, mem.Raw, loadSeg * 16, Math.Min(mi.Length, mem.Raw.Length - loadSeg * 16));
}" : hasDgroup && game.DsBase > 0 ? $@"
// Inyectar DGROUP snapshot (estado inicial del juego capturado del original)
if (File.Exists(""dgroup.bin""))
{{
    var dg = File.ReadAllBytes(""dgroup.bin"");
    int dgroupLinear = (loadSeg + {(int)(game.DsBase - 0x10000) / 16}) * 16;
    Array.Copy(dg, 0, mem.Raw, dgroupLinear, dg.Length);
}}" : "")}

// gameDir: preferir el directorio del propio host si los datos del juego están al lado
// (entregable portable: exe + GAME.EXE + datos + snapshot juntos); si no, cwd; si no,
// la ruta absoluta de desarrollo.
var gameDir = ExistsExactCase(AppContext.BaseDirectory, ""{Path.GetFileName(game.ExePath)}"")
    ? AppContext.BaseDirectory
    : ExistsExactCase(""."", ""{Path.GetFileName(game.ExePath)}"")
        ? Directory.GetCurrentDirectory()
        : Path.GetDirectoryName(Path.GetFullPath(""{game.ExePath.Replace("\\", "/")}"")) ?? ""."";
// Rotación de logs por partida: los registros de la partida ANTERIOR (gaps.log, exit.log,
// exit.png, input.log) se mueven a logs\partida-<fecha>\ al arrancar. La partida actual escribe
// logs\gaps.log fresco, así el tooling (autocuración --seeds, gates) sigue leyendo siempre
// el de la última partida en la ruta de siempre. Retención: las 10 partidas más recientes.
// VA ANTES DE CreateFull A PROPÓSITO: el driver de ratón abre logs\input.log en su constructor,
// y rotar después significaba intentar mover un fichero que este mismo proceso tiene abierto —
// la rotación fallaba SIEMPRE (used by another process), con una instancia o con ocho.
try
{{
    string[] _prevLogs = {{ ""logs/gaps.log"", ""logs/exit.log"", ""logs/exit.png"", ""logs/input.log"", ""logs/blit.log"", ""logs/blitraw.bin"", ""logs/audio-events.log"", ""logs/pcsample.log"" }};
    if (_prevLogs.Any(File.Exists))
    {{
        DateTime _pt = _prevLogs.Where(File.Exists).Select(File.GetLastWriteTime).Max();
        string _pdir = $""logs/partida-{{_pt:yyyyMMdd-HHmmss}}"";
        Directory.CreateDirectory(_pdir);
        foreach (var _pf in _prevLogs.Where(File.Exists))
            File.Move(_pf, Path.Combine(_pdir, Path.GetFileName(_pf)), overwrite: true);
        foreach (var _old in Directory.GetDirectories(""logs"", ""partida-*"").OrderByDescending(d => d).Skip(10))
            Directory.Delete(_old, recursive: true);
    }}
}}
catch (Exception _rex) {{ Console.Error.WriteLine(""[LOGS] rotacion fallida: "" + _rex.Message); }}

// Unidades DOS extra. Por defecto el juego SOLO ve la carpeta de este exe (C:) y sus
// subcarpetas. DISCOS.txt (junto al exe, una linea ""D=C:\ruta"" por unidad) monta mas carpetas
// del host como D:..Z:, y la primera pantalla las anade con la tecla D. Las unidades las recoge
// el Int21hHandler en su PRIMER servicio, ya pasado el splash (Platform/DosFileSystem.cs).
var _discos = new PakuPort.Core.Platform.DiscosExtra(gameDir);
_discos.Cargar();
_discos.Aplicar();
var (platform, keyboard, mouse, timer) = PlatformFactory.CreateFull(gameDir, ""{Path.GetFileName(game.ExePath)}"");
// Ratón: escala X del contrato virtual-640 (Int33hHandler.XScale). MouseX se guarda SIEMPRE en
// píxeles de juego; la escala se aplica al exponer (callback/AH=03/05/06) y afecta por igual a
// la ruta de ratón REAL y a la de wiggle. Valor RESUELTO en generación por el mundo del export:
// {(game.MouseXScale != 0 ? $"game.json manda (mouseXScale={game.MouseXScale})" : (snapExpMem != null || bootMem != null) ? "snapshot/memimage congelado con la bandera 640-virtual OFF → píxeles crudos (1)" : "arranque fresco game.json/boot-real, fiel al driver DOS/DOSBox (virtual-640 → 2)")}.
mouse.XScale = {resolvedMouseXScale};
mem.Video = platform.Video;
{(game.DpmiHost ? $@"// game.json dpmiHost: PakuPort se presenta como host DPMI en INT 2Fh AX=1687h. Para juegos
// con EXTENSOR EMBEBIDO, que arrancan en modo real y preguntan por un host antes de intentar el
// cambio de modo por su cuenta. VA ANTES DE AttachMemory A PROPOSITO: es alli donde nace el host
// DPMI y se registra su INT 31h. PAKU_NO_DPMI=1 lo apaga para un A/B.
if (Environment.GetEnvironmentVariable(""PAKU_NO_DPMI"") != ""1"")
    PakuPort.Core.Platform.BiosInterrupts.DpmiHostDisponible = true;" : "")}
platform.AttachMemory(mem);
{(game.ApiExtensor ? @"// game.json apiExtensor: el DOS de 32 bits sirve la API PROPIETARIA del extensor embebido en
// INT 21h AH=25/35 (el numero de interrupcion va en CL, no en AL). Va DESPUES de AttachMemory:
// es alli donde nace FlatServices.
if (platform.Dpmi != null) platform.Dpmi.ApiExtensor = true;" : "")}
{(realDosMap ? @"platform.Int21!.AdoptBootState(bootDos.Mcb, pspSeg); // el arena DOS fiel construido en el boot" : "")}
{(snapEntryKey >= 0 ? @"if (snapBytes != null) DosPlatform.RestoreGameVectors(mem, snapBytes); // hooks del juego (timer del driver de sonido) tras el stubbeo" : "")}
// Disparo de ISRs instaladas por el juego: la Platform (PakuPort.Core) no puede referenciar
// el código generado, así que el host le pasa FarDispatch como callback.
platform.IsrDispatch = interpOnly
    ? ((c, mm, seg, off) => PakuPort.Core.Interp.Run(c, mm, seg, off, null, null))
    : ((c, mm, seg, off) => {em.Namespace}.{em.ClassName}.FarDispatch(c, mm, seg, off));
{(game.VideoMode != 0 ? $"platform.Video.VideoMode = 0x{game.VideoMode:X2};{(game.Format == "com" ? "" : "\nplatform.Video.LoadDefaultVgaPalette();")}"
  + (game.PaletteRemap != null ? $"\nplatform.Video.ApplyPaletteRemap(new byte[] {{ {string.Join(", ", game.PaletteRemap)} }});" : "")
: "// modo de vídeo: el de arranque de la plataforma (texto 3, calibrado contra el oráculo); videoMode en game.json lo fija")}
{(snapEntryKey >= 0 ? $@"
if (snapBytes != null)
{{
    // Mundo snapshot: la memoria alta congelada (>0x90000) contiene datos VIVOS del
    // oráculo (música, buffers) — el arena DOS no debe pisarla (techo histórico 0x9000).
    PakuPort.Core.Platform.Int21hHandler.ArenaTopSegment = 0x9000;
    // set-mode real por la plataforma (deja los registros gráficos coherentes)
    var _mcpu = new Cpu {{ AX = 0x0013, Platform = platform }};
    platform.Int(0x10, _mcpu, mem);
    if (platform.Video.IsMode13h)
        Array.Copy(snapBytes, 0xA0000, platform.Video.Planes[0], 0, 64000);
    if (snapPal != null) Array.Copy(snapPal, platform.Video.Dac, 768);
    foreach (var (dseg, dname) in snapDrivers) DriverRegistry.RegisterDriverSegment(dseg, dname);
    // restaurar la tabla de handles del kernel del oráculo (mismos números)
    foreach (var (hn, hnm, hpos) in snapHandles)
        platform.Int21?.OpenAtHandle(hn, hnm, hpos);
    if (snapMouseCb.HasValue && snapMouseCb.Value.mask != 0)
        mouse.RestoreCallback(snapMouseCb.Value.seg, snapMouseCb.Value.off, snapMouseCb.Value.mask);
    // drivers EXEC lifteados en la imagen compuesta (keyBase horneado al exportar)
{string.Join("\n", expDriverRegions.Select(dr => $"    DriverRegistry.RegisterDriverSegment(0x{dr.Seg:X4}, \"{dr.Name}\", 0x{dr.KeyBase:X5});"))}
}}" : "")}
{(bootMem != null && expDriverRegions.Count > 0 ? $@"
// boot-real: drivers EXEC lifteados como pseudo-overlays. El juego los cargará por
// INT21 4B03 en runtime; el registro por NOMBRE resuelve el keyBase sea cual sea el
// segmento que asigne el allocator (RegisterDriverSegment hace el lookup al cargar).
{string.Join("\n", expDriverRegions.Select(dr => $"DriverRegistry.DriverKeyBases[\"{dr.Name}\"] = 0x{dr.KeyBase:X5};"))}" : "")}

// Render fps + compensación del presupuesto por frame. BudgetPerFrame
// está calibrado a 30 fps; subir el render a 60 fps acorta cada frame, así que un juego SIN
// governor reduce a la mitad su presupuesto por frame para mantener el throughput (budget × fps)
// constante — 500k×30 = 250k×60. Un juego CON governor (cyclesPerMs>0) conserva el presupuesto
// entero: el governor fija su ritmo y el presupuesto solo debe quedar POR ENCIMA del objetivo
// del governor para no caparlo.
const int RenderFps = 60;
const int BudgetBaseFps = 30;
int perFrameBudget = {game.CyclesPerMs} > 0
    ? {game.BudgetPerFrame}
    : {game.BudgetPerFrame} * BudgetBaseFps / RenderFps;

{(game.GamePortAbsent ? $@"// game.json gamePortAbsent: máquina sin tarjeta de juegos (0x201 = 0xFF, como el DOSBox
// del usuario sin joystick físico). PAKU_NO_GAMEPORT=0 fuerza el puerto presente para A/B.
if (Environment.GetEnvironmentVariable(""PAKU_NO_GAMEPORT"") != ""0"")
    PakuPort.Core.Platform.GamePort.Absent = true;
" : "")}var cpu = new Cpu
{{
    CS = {(bootMem != null ? $"0x{bootRealCs:X4}" : game.Format == "com" ? comCsExpr : "mz.AbsCS")},
    DS = {(bootMem != null ? "pspSeg" : game.Format == "com" ? "loadSeg" : (game.DsBase > 0 ? $"(ushort)(loadSeg + {(int)(game.DsBase - 0x10000) / 16})" : "pspSeg"))},
    ES = {(bootMem != null ? "pspSeg" : game.Format == "com" ? "loadSeg" : (game.DsBase > 0 ? $"(ushort)(loadSeg + {(int)(game.DsBase - 0x10000) / 16})" : "pspSeg"))},
    SS = {(bootMem != null ? $"0x{bootRealSs:X4}" : game.Format == "com" ? "loadSeg" : "mz.AbsSS")},
    SP = {(bootMem != null ? $"0x{bootRealSp:X4}" : "mz.InitSP")},
    Platform = platform,
    Budget = perFrameBudget,
    PresupuestoPorFrame = perFrameBudget,
    // PAKU_CYCLES: override en runtime del governor (0 = velocidad libre). El valor horneado
    // viene de game.json (cyclesPerMs = instrucciones/ms, la unidad DOSBox cycles=N).
    CyclesPerMs = int.TryParse(Environment.GetEnvironmentVariable(""PAKU_CYCLES""), out var _cycOv) ? _cycOv : {game.CyclesPerMs},
    LoadBase = loadSeg * 16,
    ImageBias = {((snapEntryKey >= 0 || bootMem != null) ? $"0x{expImageBias:X}" : "0")},
}};
// Sirve IRQs hardware pendientes (timer/teclado) en el límite de frame, sobre este hilo (seguro).
cpu.OnFrameBoundary = c => platform.ServicePendingInterrupts(c, mem);
// Temporizador del PIT gobernado por tiempo real en el HILO DEL JUEGO (modelo DOSBox): poll
// barato cada 4096 Steps que entrega IRQ0 a la frecuencia programada (no ~30/s en el frame).
cpu.OnPeriodicPoll = c => platform.MachineTimer.Poll(platform, c, mem);
cpu.GapHits = new HashSet<int>();
cpu.FarDispatchMisses = new HashSet<int>();
cpu.FarRetUnwinds = new HashSet<long>();   // TAREA B: destinos de retf descartados por el unwind
// Intérprete de respaldo: al caer en un hueco del código lifteado se ejecutan
// los bytes ORIGINALES de la imagen en memoria. PAKU_INTERP=0 lo desactiva (comportamiento
// Fase 0: registrar el hueco y volver mudo).
Interp.Enabled = Environment.GetEnvironmentVariable(""PAKU_INTERP"") != ""0"";
Interp.OverlayWindowSegDelta = 0x{(game.VroommStub?.Seg ?? 0):X4};
Interp.OverlayWindowIpBase = 0x{game.OverlayLoadBase:X4};
// FarRet co-routine redirection (Frente 1): ON in the runtime host (this is where the co-routine's retf
// to a hand-pushed continuation actually executes). It stays OFF in the judge and the export-time probe,
// where a redirect would run the interpreter and perturb function discovery. The intra-module fail-safe
// (FarRetCs == CS) lives in Cpu.ShouldFarRetRedirect.
Cpu.FarRetRedirectEnabled = true;
{(game.Overlays.Count > 0
? $"Interp.OverlayStarts = new Dictionary<int, int> {{ {string.Join(", ", game.Overlays.Entries.Select(e => $"{{ 0x{e.Ovno:X2}, 0x{e.Start:X5} }}"))} }};"
: "")}
{(snapEntryKey >= 0 ? @"
if (snapBytes != null)
{
    cpu.AX = snapRegs[0]; cpu.BX = snapRegs[1]; cpu.CX = snapRegs[2]; cpu.DX = snapRegs[3];
    cpu.SI = snapRegs[4]; cpu.DI = snapRegs[5]; cpu.BP = snapRegs[6]; cpu.SP = snapRegs[7];
    cpu.CS = snapRegs[8]; cpu.DS = snapRegs[9]; cpu.ES = snapRegs[10]; cpu.SS = snapRegs[11];
    cpu.SetFlagsWord(snapRegs[12]);
}" : "")}
{(game.PageDescriptors != null && game.PageDescriptors.Count > 0
? $"if (!interpOnly && {(snapEntryKey >= 0 ? "snapBytes == null" : "true")}) DriverRegistry.InitPageBuffers(mem, cpu.DS, new (ushort, int)[] {{ {string.Join(", ", game.PageDescriptors.Select(kv => $"(0x{kv.Value:X4}, {kv.Key})"))} }});"
: "")}

Console.WriteLine(""Port de {game.Title} generado por PakuPort"");
Console.WriteLine(""  {allFuncs.Count} funciones traducidas"");

// --- Ventana Raylib ---
const int scale = 2; // escala lógica del splash; el tamaño real se ajusta al monitor.
int w = platform.Video.Width;
int h = platform.Video.Height;
// Headless runs (see CorteDeterminista.SinVentana) must not steal the user's focus:
// GLFW grabs it even when launched minimized, so hide the window entirely.
if (PakuPort.Core.CorteDeterminista.SinVentana)
    Raylib.SetConfigFlags(ConfigFlags.HiddenWindow);
// Arrancar siempre con una ventana que quepa. Algunos juegos empiezan directamente en texto
// 720x400: el antiguo 3x con aspecto 4:3 pedía 2160x1620, más alto que un monitor 1440p, y
// GLFW podía dejar la ventana inaccesible. Tras crearla ya podemos consultar el monitor y
// agrandarla al máximo seguro.
bool _initialAspect = Environment.GetEnvironmentVariable(""PAKU_NO_ASPECT"") != ""1"";
float _initialPixelAspect = _initialAspect ? ((float)w / h) * (3f / 4f) : 1f;
Raylib.InitWindow(Math.Min(960, w * 2), Math.Min(720, (int)MathF.Round(h * _initialPixelAspect * 2)),
    ""{game.Title} — PakuPort{dedicationTitle}"");
int _monitor = Raylib.GetCurrentMonitor();
int _monitorW = Raylib.GetMonitorWidth(_monitor);
int _monitorH = Raylib.GetMonitorHeight(_monitor);
float _fitScale = Math.Min(3f, Math.Min((_monitorW - 80f) / w,
                                        (_monitorH - 120f) / (h * _initialPixelAspect)));
_fitScale = Math.Max(1f, _fitScale);
Raylib.SetWindowSize((int)MathF.Round(w * _fitScale),
                     (int)MathF.Round(h * _initialPixelAspect * _fitScale));
// Pantalla completa (ventana sin bordes) DE SERIE: el juego se escala y centra solo — viewScale
// se recalcula cada frame contra el tamaño real de la ventana —, así que no hay nada que
// recolocar al arrancar. F11 vuelve a ventana; PAKU_WINDOWED=1 arranca en ventana.
// El tamaño calculado arriba sigue siendo el que queda al salir del borderless.
// Excepción headless (CorteDeterminista.SinVentana): la ventana está oculta a propósito y cambiarle el
// estado hace que GLFW la muestre y robe el foco al usuario en mitad de un smoke.
if (Environment.GetEnvironmentVariable(""PAKU_WINDOWED"") != ""1"" &&
    !PakuPort.Core.CorteDeterminista.SinVentana)
    Raylib.ToggleBorderlessWindowed();
// ESC es una tecla DEL JUEGO (cierra menús/diálogos en muchos DOS); sin esto Raylib la usa
// como exit key y la ventana se cierra en mitad de la partida. Salir = F12 o la X.
Raylib.SetExitKey(KeyboardKey.Null);
Raylib.SetTargetFPS(RenderFps);   // 60 fps: el ratón se muestrea 1×/frame (límite GLFW) → mitad de latencia y cursor el doble de fino
platform.Video.HostDrivenRetrace = true;

// --- Backend de audio del host (mezclable, thread-safe) ---
// El ring HostAudio lo escribe el hilo del juego (dentro de un OUT lifteado, vía SoundBlaster
// u otras fuentes futuras) y lo drena este hilo (UpdateAudioStream). Nace para TODO juego —
// hoy solo lo alimenta el SB si game.json declara `sb`; speaker/OPL se enchufan sin rediseño.
// Failsafe: si el dispositivo de audio no arranca (headless/sin tarjeta) o PAKU_NOAUDIO=1, el
// juego corre igual (sin sonido) — try+log y PAKU_NOAUDIO como escotilla.
AudioStream _sbStream = default;
bool _sbStreamReady = false;
short[] _audioBuf = new short[2048];
HostAudio? _hostAudio = null;
bool _noAudio = Environment.GetEnvironmentVariable(""PAKU_NOAUDIO"") == ""1"";
if (!_noAudio)
{{
    try
    {{
        Raylib.InitAudioDevice();
        if (Raylib.IsAudioDeviceReady())
        {{
            _hostAudio = new HostAudio(22050, 22050);   // anillo de ~1 s a 22050 Hz
            Raylib.SetAudioStreamBufferSizeDefault(_audioBuf.Length);
            _sbStream = Raylib.LoadAudioStream(22050, 16, 1);
            Raylib.PlayAudioStream(_sbStream);
            _sbStreamReady = true;
        }}
    }}
    catch (Exception _aex) {{ Console.Error.WriteLine(""[audio] backend deshabilitado: "" + _aex.Message); }}
}}
// PAKU_WAV_DUMP=<ruta>: vuelca el PCM drenado (mezcla SB+OPL) a un WAV mono 16-bit 22050 para
// validacion objetiva (FFT/RMS vs oraculo). Cabecera WAV minima, sin dependencias externas.
string? _wavPath = Environment.GetEnvironmentVariable(""PAKU_WAV_DUMP"");
System.IO.FileStream? _wavFs = null;
byte[]? _wavBuf = null;
long _wavData = 0;
double _wavT0 = 0;
if (!string.IsNullOrEmpty(_wavPath))
{{
    try
    {{
        var _wd = System.IO.Path.GetDirectoryName(_wavPath);
        if (!string.IsNullOrEmpty(_wd)) System.IO.Directory.CreateDirectory(_wd);
        _wavFs = new System.IO.FileStream(_wavPath, System.IO.FileMode.Create);
        _wavFs.Write(new byte[44], 0, 44); // cabecera placeholder (se parchea al cerrar)
    }}
    catch (Exception _we) {{ Console.Error.WriteLine(""[wav] no pude abrir "" + _wavPath + "": "" + _we.Message); _wavFs = null; }}
}}
// Para volcar WAV en modo totalmente headless (sin dispositivo de audio del host), crea el ring
// igualmente: la sintesis OPL escribe alli y el bucle de render lo drena al WAV a ritmo de frame.
if (_wavFs != null && _hostAudio == null) _hostAudio = new HostAudio(22050, 22050);
{(game.HasSb ? $@"// SoundBlaster digital (DSP + DMA single-cycle 8-bit): game.json declara `sb`.
platform.InstallSoundBlaster(0x{game.SbBase:X4}, {game.SbIrq}, {game.SbDma}, _hostAudio);" : "// game.json sin bloque `sb`: el SB no se instancia (los puertos SB/DMA quedan en reposo).")}
// Sintesis FM AdLib/OPL2 (OplChip): el chip SOLO se crea al enchufar el audio del host (init
// perezoso). Sin _hostAudio -> OPL mudo (comportamiento previo). Junto a InstallSoundBlaster.
if (_hostAudio != null) platform.Opl.AttachAudio(_hostAudio);
// PC Speaker: el unico sonido de montones de juegos de los 80 y la opcion IBM de los 90.
if (_hostAudio != null) platform.Speaker.AttachAudio(_hostAudio);
var img = new Image {{ Width = w, Height = h, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 }};
var tex = Raylib.LoadTextureFromImage(img);
float viewScale = scale; // escala de dibujo actual (se recalcula por frame: ventana/fullscreen/modo)
float viewOx = 0, viewOy = 0; // letterbox: la imagen va centrada; el mapeo de ratón lo descuenta
// Corrección de aspecto 4:3 (como `aspect=true` de DOSBox): los modos DOS tienen píxeles no
// cuadrados y sin esto la imagen sale achatada respecto al original en un CRT.
bool aspectCorrection = Environment.GetEnvironmentVariable(""PAKU_NO_ASPECT"") != ""1"";
// Escala ENTERA: la imagen se amplia un numero exacto de veces y se centra. Sin esto el
// factor sale fraccionario (320x240 a 1080p son x4,5) y con filtro de vecino mas proximo
// unas filas se duplican y otras no: se ve como si media pantalla estuviera a media
// resolucion, y el patron BAILA cuando el juego mueve la imagen. Ctrl+F12 conmuta.
bool escalaEntera = Environment.GetEnvironmentVariable(""PAKU_ESCALA_ENTERA"") == ""1"";
float pixelAspect = 1f; // alto efectivo = h * pixelAspect (lo usa también el mapeo de ratón)
// Captura de ratón estilo DOSBox: click dentro captura (cursor confinado a la imagen, el del
// JUEGO es el único visible, 1:1 con DOS), Ctrl+F10 libera, F11 = pantalla completa sin bordes.
// PAKU_NO_MOUSE_CAPTURE=1 la desactiva; una corrida sin ventana nunca captura.
bool mouseCaptured = false;
bool noMouseCapture = Environment.GetEnvironmentVariable(""PAKU_NO_MOUSE_CAPTURE"") == ""1""
    || PakuPort.Core.CorteDeterminista.SinVentana;
// Movimiento crudo del ratón con la captura activa: referencia del frame anterior y acumulador
// de las fracciones de píxel de juego. PAKU_MOUSE_ABS=1 vuelve al modelo previo (mickeys
// derivados de la posición ya recortada), por si algún juego prefiriera el anterior.
System.Numerics.Vector2 _prevRawMouse = default;
float _rawAccX = 0, _rawAccY = 0;
bool _rawMotion = Environment.GetEnvironmentVariable(""PAKU_MOUSE_ABS"") != ""1"";
// Velocidad de CPU en caliente (Ctrl+F1 / Ctrl+F2 / Ctrl+F3), el equivalente del Ctrl+F11/F12 de
// DOSBox. Muchos juegos de los 80 no tienen NINGUNA fuente de tiempo -- ni tick del BIOS, ni
// retrace, ni PIT -- y corren a la velocidad que les dé la CPU: el único mando de su ritmo es
// éste, y sólo el usuario jugando puede decir cuál es el bueno. Sin ajuste en caliente cada
// tanteo exigía re-hornear game.json y republicar, así que se tanteaban dos o tres valores a
// ciegas. La unidad es instrucciones/ms, medida idéntica a la `cycles` de DOSBox con core=normal
// (verificado con tools\speed_probe: mismo binario, 1,0 % de diferencia).
// Los tres escalones de arriba (30000-60000) salen de un juego de prueba que corre libre a unas
// 44.000 instrucciones/ms en esta maquina, asi que con el tope anterior (20000) no habia forma
// de hornear un poco mas lento que libre -- el maximo disponible era menos de la mitad, y el
// juego pasaba de ir sobrado a tardar el triple en llegar al titulo (7.697 pixeles contra
// 58.903 en el mismo smoke de 15 s).
int[] cyclesSteps = {{ 100, 150, 200, 250, 315, 400, 500, 650, 800, 1000, 1250, 1600,
                      2000, 2500, 3000, 4000, 5000, 6500, 8000, 12000, 20000,
                      30000, 40000, 60000 }};
int cyclesIdx = 0;
int _cycRef = cpu.CyclesPerMs > 0 ? cpu.CyclesPerMs : 3000;   // con governor libre, partir de 3000
for (int _i = 1; _i < cyclesSteps.Length; _i++)
    if (Math.Abs(cyclesSteps[_i] - _cycRef) < Math.Abs(cyclesSteps[cyclesIdx] - _cycRef))
        cyclesIdx = _i;
int cyclesFactory = cpu.CyclesPerMs;   // el de game.json, para poder volver con Ctrl+F3
double speedOsdUntil = 0;              // el aviso en pantalla se apaga solo
bool finished = false;
bool gameStarted = false;
bool splashDone = false;
string? error = null;
bool exitHandled = false;         // terminación visible (spec §5c): overlay + logs/exit.log una sola vez
string exitReason = """";           // motivo de salida — lo fija el hilo de lógica al morir
ushort exitCs = 0;                // CS en el instante de la terminación
double timerAccum = 0;

// Verificación automatizada (CI/diagnóstico, genérico):
//   PAKU_NOSPLASH=1     salta el splash (la lógica arranca ya)
//   PAKU_AUTOEXIT_MS=N  cierra tras N ms reportando estado + screenshot.png
if (Environment.GetEnvironmentVariable(""PAKU_NOSPLASH"") == ""1"") splashDone = true;
long autoExitMs = long.TryParse(Environment.GetEnvironmentVariable(""PAKU_AUTOEXIT_MS""), out var _ae) ? _ae : 0;
// Headless (autoexit) nunca recibe teclas: sin esto el splash lo bloquea para siempre.
if (autoExitMs > 0) splashDone = true;
var runClock = System.Diagnostics.Stopwatch.StartNew();
long _nextHb = 5000; // [HB] pulso de diagnóstico cada 5 s (interp/ticks/retrace/fps) a stderr
// PAKU_AUTOKEYS: secuencia de teclas inyectada al arrancar (p.ej. elegir el modo de vídeo
// en el menú de setup). '\r'=Enter, '~'=pausa ~1 s. Se alimenta una tecla cada ~10 frames por ambas rutas
// (INT 16h + hardware INT 9). Útil para verificación y para pasar menús de arranque.
var autoKeys = new Queue<char>((Environment.GetEnvironmentVariable(""PAKU_AUTOKEYS"") ?? """").Replace(""\\r"", ""\r"").ToCharArray());
int autoKeyTick = 0;
byte autoKeyBreak = 0;   // make pendiente de break code (PAKU_AUTOKEYS)
string? wiggleEnv = Environment.GetEnvironmentVariable(""PAKU_MOUSE_WIGGLE"");
bool mouseWiggle = wiggleEnv == ""1"" || wiggleEnv == ""2"";
bool wiggleClicks = wiggleEnv == ""2"";
int wiggleTick = 0;
// PAKU_CLICKS: clicks scriptados deterministas ""x,y,frame[,hold];..."" (coordenadas de JUEGO).
// En el frame indicado el cursor salta a (x,y) y pulsa botón izq `hold` frames (por defecto 6;
// hold=0 = mantener pulsado hasta el cierre, útil para dejar un menú desplegado en el
// screenshot). Mientras hay un click activo tiene prioridad sobre el wiggle y el ratón real.
var scriptClicks = new Queue<int[]>((Environment.GetEnvironmentVariable(""PAKU_CLICKS"") ?? """")
    .Split(';', StringSplitOptions.RemoveEmptyEntries)
    .Select(s => s.Split(',').Select(t => int.Parse(t.Trim())).ToArray()));
int[]? clickActive = null;   // click en curso: [x, y, frameFin] (frameFin<0 = mantener)
int frameCount = 0;
// PAKU_LIVE_VRAM=1 presenta siempre la VRAM viva en vez del front buffer capturado por reloj
// (diagnóstico / A/B del parpadeo). Por defecto se presenta el front buffer (modelo hardware).
bool liveVram = Environment.GetEnvironmentVariable(""PAKU_LIVE_VRAM"") == ""1"";
int joyX = 128, joyY = 128; // posición integrada del joystick emulado (0x201)
var keysDown = new Dictionary<int, byte>(); // rKey -> scancode (make), para emitir el break code al soltar
// Typematic (auto-repeat): rKey -> instante (Raylib.GetTime) de la próxima repetición.
var keyRepeatAt = new Dictionary<int, double>();
// Defaults del teclado PC/AT: 500 ms de retardo inicial y 10,9 repeticiones/s — el estado
// tras el arranque del 8042 (comando F3h con byte 2Bh), que es lo que un juego DOS espera.
const double TypematicDelay = 0.500, TypematicPeriod = 1.0 / 10.9;
// PAKU_NO_TYPEMATIC=1: escotilla por si algún juego se atraganta con las repeticiones.
bool typematic = Environment.GetEnvironmentVariable(""PAKU_NO_TYPEMATIC"") != ""1"";
var _hdTex = new Dictionary<string, Texture2D>(); // PASO 4: texturas HD del remake.json, carga perezosa en el hilo de render
var _escTex = new Texture2D();          // frame del juego ampliado con Scale3x (PAKU_SCALE3X)
int _fotoFrames = 0;
double _capturaCadaS = double.TryParse(Environment.GetEnvironmentVariable(""PAKU_SCREENSHOT_CADA""),
    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
    out var _ccs) ? _ccs : 0;
double _capturaProxima = 0; int _capturaN = 0;
// PAKU_XBRZ=<2..6>: escalado xBRZ del frame del juego (sustituye a Scale3x si está puesto).
int _xbrzFactor = int.TryParse(Environment.GetEnvironmentVariable(""PAKU_XBRZ""), out var _xf)
                  && _xf >= 2 && _xf <= 6 ? _xf : 0;
byte[]? _xbrzOut = null;
long _xbrzGen = 0;
// PASO 4: por instancia HD viva, cómo era el lienzo sombra cuando la textura se puso encima. Es
// la referencia contra la que se decide qué píxeles ha cambiado el juego DESPUÉS (ver el render).
var _hdBase = new Dictionary<PakuPort.Core.RemakeMap.Instance, byte[]>();
var _hdSeen = new HashSet<PakuPort.Core.RemakeMap.Instance>();
var _hdDead = new List<PakuPort.Core.RemakeMap.Instance>();
// PAKU_REMAKE_NO_AGUJEREAR=1: la textura HD tapa TODO, como antes del arreglo que destapo una
// prueba manual. Es el CONTROL NEGATIVO del juez de pantalla presentada (judge_presentado.py): sin una
// forma de reproducir el fallo a voluntad, un juez que sale verde no ha demostrado que sepa
// ponerse rojo. No se usa para jugar: congela la imagen.
bool _hdSinAgujerear = Environment.GetEnvironmentVariable(""PAKU_REMAKE_NO_AGUJEREAR"") == ""1"";
bool numLock = true; // arranque con NumLock ON (estado BIOS/DOSBox por defecto); la tecla NumLock lo conmuta

// --- Cinta de input (PASO 3 remakes): record/replay exigen governor calibrado ---
if (PakuPort.Core.InputTape.ClockLocked && cpu.CyclesPerMs <= 0)
{{
    Console.Error.WriteLine(""[TAPE] PAKU_TAPE_RECORD/REPLAY exigen cyclesPerMs>0 (game.json o PAKU_CYCLES): abortado"");
    return;
}}

// --- Splash screen ---
while (!Raylib.WindowShouldClose() && !splashDone)
{{
    // D y Supr gestionan las unidades extra y NO arrancan el juego; cualquier otra tecla si.
    int _sk;
    while ((_sk = Raylib.GetKeyPressed()) != 0)
    {{
        if (_sk == (int)KeyboardKey.D)
        {{
            string? _sruta = PakuPort.Core.Platform.SelectorDeCarpeta.Elegir(""Elige una carpeta del PC para que el juego la vea como una unidad DOS"");
            if (_sruta != null) _discos.Anadir(_sruta);
        }}
        else if (_sk == (int)KeyboardKey.Delete) _discos.QuitarUltima();
        else splashDone = true;
    }}
    if (Raylib.IsMouseButtonPressed(MouseButton.Left)) splashDone = true;
    Raylib.BeginDrawing();
    Raylib.ClearBackground(Color.Black);
{dedicationSplash}
    Raylib.DrawText(""Portado por PakuPort"", Raylib.GetScreenWidth() / 2 - 140, Raylib.GetScreenHeight() / 2 - 70, 24, Color.White);
    Raylib.DrawText(""{game.Title}"", Raylib.GetScreenWidth() / 2 - 100, Raylib.GetScreenHeight() / 2 - 30, 20, Color.Green);
    Raylib.DrawText(""Pulsa cualquier tecla"", Raylib.GetScreenWidth() / 2 - 130, Raylib.GetScreenHeight() / 2 + 10, 16, Color.Gray);
    // Chuleta de teclas del HOST (las del juego son las suyas de DOS)
    Raylib.DrawText(""F11 = pantalla completa"", Raylib.GetScreenWidth() / 2 - 130, Raylib.GetScreenHeight() / 2 + 44, 14, Color.DarkGray);
    Raylib.DrawText(""Click = capturar raton   Ctrl+F10 = soltarlo"", Raylib.GetScreenWidth() / 2 - 130, Raylib.GetScreenHeight() / 2 + 62, 14, Color.DarkGray);
    Raylib.DrawText(""F12 = salir (ESC es del juego)"", Raylib.GetScreenWidth() / 2 - 130, Raylib.GetScreenHeight() / 2 + 80, 14, Color.DarkGray);
    Raylib.DrawText(""Ctrl+F1 / Ctrl+F2 = mas lento / mas rapido"", Raylib.GetScreenWidth() / 2 - 130, Raylib.GetScreenHeight() / 2 + 98, 14, Color.DarkGray);
    int _dy = Raylib.GetScreenHeight() / 2 + 132;
    Raylib.DrawText(""Unidades:"", Raylib.GetScreenWidth() / 2 - 130, _dy, 14, Color.Gold);
    foreach (string _dl in _discos.LineasParaPantalla())
    {{
        _dy += 18;
        Raylib.DrawText(_dl, Raylib.GetScreenWidth() / 2 - 118, _dy, 14, Color.LightGray);
    }}
    Raylib.DrawText(""D = anadir una carpeta como unidad   Supr = quitar la mas reciente"", Raylib.GetScreenWidth() / 2 - 130, _dy + 26, 14, Color.DarkGray);
{(game.SplashKeys.Length > 0 ? $@"    // Teclas del JUEGO (game.json `splashKeys`, sacadas del manual)
    Raylib.DrawText(""TECLAS DEL JUEGO"", 40, 30, 18, Color.Gold);
{string.Join("\n", game.SplashKeys.Select((k, i) => $@"    Raylib.DrawText(""{k.Replace("\"", "'")}"", 40, {58 + i * 22}, 16, Color.RayWhite);"))}" : "")}
    Raylib.EndDrawing();
}}

while (!Raylib.WindowShouldClose())
{{
    // F12 = salir del host (ESC pertenece al juego; ver SetExitKey arriba)
    if (Raylib.IsKeyPressed(KeyboardKey.F12)) break;
    // Replay de cinta: aqui solo se SUPRIME el input vivo (teclado/raton/autokeys/wiggle/clicks).
    // Los eventos grabados los reinyecta el HILO DEL JUEGO en MachineTimer.Poll
    // (InputTape.Reproduce): este bucle corre a fotogramas de reloj de pared, y hacerlo aqui
    // metia cada evento en una instruccion distinta en cada corrida.
    if (PakuPort.Core.InputTape.Replaying) goto _liveInputDone;
    // --- Input: teclado ---
    int rKey;
    while ((rKey = Raylib.GetKeyPressed()) != 0)
    {{
        // NumLock conmuta el estado que decide el ASCII del numpad (fórmula BIOS de abajo) y
        // ADEMÁS viaja al juego como cualquier otra tecla: el BIOS real también le pasa su
        // scancode al INT 9 para que actualice el bit de la BDA.
        if ((KeyboardKey)rKey == KeyboardKey.NumLock)
        {{
            numLock = !numLock;
            platform.Keyboard.EnqueueHostScancode(0x45);
            keysDown[rKey] = 0x45;
            continue;
        }}
        // Teclas del HOST (se tragan, no llegan al juego): F11 fullscreen; Ctrl+F10 suelta el ratón.
        if ((KeyboardKey)rKey == KeyboardKey.F11)
        {{
            if (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl))
                aspectCorrection = !aspectCorrection;   // Ctrl+F11: 4:3 de CRT <-> píxeles cuadrados
            else
                Raylib.ToggleBorderlessWindowed();
            continue;
        }}
        if ((KeyboardKey)rKey == KeyboardKey.F12 &&
            (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl)))
        {{
            escalaEntera = !escalaEntera;
            Console.Error.WriteLine($""[VIDEO] escala entera: {{(escalaEntera ? ""SI"" : ""no"")}}"");
            continue;
        }}
        if ((KeyboardKey)rKey == KeyboardKey.F10 &&
            (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl)))
        {{
            if (mouseCaptured) {{ Raylib.EnableCursor(); mouseCaptured = false; mouse.RawMotionMode = false; }}
            continue;
        }}
        // Ctrl+F1 / Ctrl+F2 / Ctrl+F3: velocidad de CPU en caliente. Se eligen F1-F3 y no el
        // Ctrl+F11/F12 de DOSBox porque aquí Ctrl+F11 ya conmuta el aspecto 4:3 y F12 cierra.
        if (((KeyboardKey)rKey == KeyboardKey.F1 || (KeyboardKey)rKey == KeyboardKey.F2 ||
             (KeyboardKey)rKey == KeyboardKey.F3) &&
            (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl)))
        {{
            if ((KeyboardKey)rKey == KeyboardKey.F1 && cyclesIdx > 0) cyclesIdx--;
            else if ((KeyboardKey)rKey == KeyboardKey.F2 && cyclesIdx < cyclesSteps.Length - 1) cyclesIdx++;
            else if ((KeyboardKey)rKey == KeyboardKey.F3)
                for (int _i = 0; _i < cyclesSteps.Length; _i++)
                    if (Math.Abs(cyclesSteps[_i] - _cycRef) < Math.Abs(cyclesSteps[cyclesIdx] - _cycRef))
                        cyclesIdx = _i;
            cpu.CyclesPerMs = cyclesSteps[cyclesIdx];
            speedOsdUntil = Raylib.GetTime() + 3.0;
            Console.Error.WriteLine($""[VELOCIDAD] cyclesPerMs={{cpu.CyclesPerMs}} (fabrica={{cyclesFactory}})"");
            continue;
        }}
        var (sc, ascii) = MapKey((KeyboardKey)rKey,
            Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift),
            numLock);
        if (sc != 0)
        {{
            // Los modificadores sólo van por la ruta hardware: el BIOS los convierte en
            // flags de la BDA, no en entradas del buffer de INT 16h.
            if (!IsModifierScancode(sc))
                keyboard.EnqueueKey(sc, ascii);        // ruta INT 16h (buffer bajo lock en Int16hHandler: ya serializado)
            platform.Keyboard.EnqueueHostScancode(sc); // ruta hardware: encola crudo; Poll lo mete en el 8042 + IRQ1 (hilo del juego)
            keysDown[rKey] = sc;
            keyRepeatAt[rKey] = Raylib.GetTime() + TypematicDelay;
        }}
    }}

    // --- Input: teclado, typematic (auto-repeat del hardware) ---
    // Raylib.GetKeyPressed() solo entrega PULSACIONES NUEVAS, así que mantener una tecla no
    // producía nada más tras el primer make. El teclado real repite el make mientras la tecla
    // siga pulsada, y un juego que mueve el cursor leyendo INT 16h (con las
    // flechas, medido en un juego de prueba) depende de ello: sin repetición da UN paso y se queda quieto por mucho que
    // mantengas la tecla. El scancode/ASCII se recalcula en cada repetición para que soltar
    // shift o NumLock a media pulsación se note, como en el hardware.
    if (typematic && keysDown.Count > 0)
    {{
        double _tnow = Raylib.GetTime();
        foreach (var (k, _) in keysDown)
        {{
            if (!Raylib.IsKeyDown((KeyboardKey)k)) continue;
            if (!keyRepeatAt.TryGetValue(k, out double _due) || _tnow < _due) continue;
            var (rsc, rascii) = MapKey((KeyboardKey)k,
                Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift),
                numLock);
            if (rsc != 0)
            {{
                if (!IsModifierScancode(rsc))
                    keyboard.EnqueueKey(rsc, rascii);
                platform.Keyboard.EnqueueHostScancode(rsc);
            }}
            keyRepeatAt[k] = _tnow + TypematicPeriod;
        }}
    }}

    // --- Input: teclado, break code (tecla soltada) ---
    // El hardware real 8042 manda scancode|0x80 al soltar; algunos juegos DOS leen el
    // puerto 0x60/INT 9 crudo esperando ver esta transicion (p.ej. para no repetir accion
    // mientras la tecla siga pulsada). Sin esto, un bucle de esa clase espera para siempre.
    if (keysDown.Count > 0)
    {{
        List<int>? released = null;
        foreach (var (k, sc) in keysDown)
        {{
            if (Raylib.IsKeyUp((KeyboardKey)k))
            {{
                platform.Keyboard.EnqueueHostScancode((byte)(sc | 0x80));
                (released ??= new List<int>()).Add(k);
            }}
        }}
        if (released != null) foreach (var k in released) {{ keysDown.Remove(k); keyRepeatAt.Remove(k); }}
    }}

    // --- Input: teclas automáticas (PAKU_AUTOKEYS) ---
    // '\r'=Enter, ' '=Espacio, '<'/'>'/'^'/'v' = flechas izq/dcha/arriba/abajo
    // 'W'/'E'/'N'/'S' = numpad 4/6/8/2 con dígito (NumLock ON: acción, no navegación)
    autoKeyTick++;
    // Fase 0..9 SIEMPRE positiva: cada '~' resta 40 al contador, así que con unas pocas pausas
    // autoKeyTick se vuelve negativo y en C# el % conserva el signo del dividendo → la fase 5
    // (la que emite el break code) no volvía a darse jamás y las teclas quedaban PULSADAS para
    // siempre. Los makes seguían saliendo (fase 0 sí casa con los negativos), así que el fallo
    // era invisible salvo en juegos que exigen la transición make->break.
    int autoKeyPhase = ((autoKeyTick % 10) + 10) % 10;
    if (autoKeys.Count > 0 && autoKeyPhase == 0)
    {{
        char c = autoKeys.Dequeue();
        if (c == '~') {{ autoKeyTick -= 40; goto _autoKeysDone; }} // '~' = pausa ~30 frames (sortea flushes de type-ahead en menús)
        // Modificadores MANTENIDOS: '%'/'&' = Shift abajo/arriba, '*'/'_' = Ctrl, '='/'|' = Alt.
        // Son pares explícitos (y no un prefijo) porque lo que hay que poder expresar es
        // justo eso: el modificador SIGUE pulsado mientras se teclea lo de en medio
        // (`%a&` = Shift+A). Pasan por MapKey, la misma función que una pulsación real, para
        // que el arnés mida el camino del usuario y no un atajo suyo.
        {{
            KeyboardKey _mk = c switch
            {{
                '%' or '&' => KeyboardKey.LeftShift,
                '*' or '_' => KeyboardKey.LeftControl,
                '=' or '|' => KeyboardKey.LeftAlt,
                _          => KeyboardKey.Null,
            }};
            if (_mk != KeyboardKey.Null)
            {{
                var (_msc, _) = MapKey(_mk, false, numLock);
                if (_msc != 0)
                    platform.Keyboard.EnqueueHostScancode(
                        c is '&' or '_' or '|' ? (byte)(_msc | 0x80) : _msc);
                goto _autoKeysDone;   // sin autoKeyBreak: el modificador se suelta con su pareja
            }}
        }}
        // Scancodes set 1 de letras y dígitos por fila del teclado. Antes de esta tabla caían
        // TODOS en el default 0x39 (espacio): un juego que lee el 0x60 crudo en vez del
        // ASCII de INT 16h (dos juegos de prueba) no recibía la tecla. El shift no se modela: una
        // mayúscula manda el scancode sin shift y su propio ASCII, que es lo que leen los
        // consumidores de INT 16h. 'v' y 'W'/'E'/'N'/'S' los toman las flechas y el numpad.
        static byte QwertyScancode(char ch)
        {{
            const string Row1 = ""1234567890"", Row2 = ""qwertyuiop"", Row3 = ""asdfghjkl"", Row4 = ""zxcvbnm"";
            char lc = char.ToLowerInvariant(ch);
            int i;
            if ((i = Row1.IndexOf(lc)) >= 0) return (byte)(0x02 + i);
            if ((i = Row2.IndexOf(lc)) >= 0) return (byte)(0x10 + i);
            if ((i = Row3.IndexOf(lc)) >= 0) return (byte)(0x1E + i);
            if ((i = Row4.IndexOf(lc)) >= 0) return (byte)(0x2C + i);
            return 0x39;
        }}
        (byte sc, byte ascii) = c switch
        {{
            '\r' => ((byte)0x1C, (byte)0x0D),
            '<'  => ((byte)0x4B, (byte)0x00),
            '>'  => ((byte)0x4D, (byte)0x00),
            '^'  => ((byte)0x48, (byte)0x00),
            'v'  => ((byte)0x50, (byte)0x00),
            'W'  => ((byte)0x4B, (byte)'4'),
            'E'  => ((byte)0x4D, (byte)'6'),
            'N'  => ((byte)0x48, (byte)'8'),
            'S'  => ((byte)0x50, (byte)'2'),
            // '+'/'-' = PgDn/PgUp: los briefings paginados de algun juego no se pasan con Enter
            '+'  => ((byte)0x51, (byte)0x00),
            '-'  => ((byte)0x49, (byte)0x00),
            '\x1b' => ((byte)0x01, (byte)0x1B), // ESC: abre los menús en juego

            // '{{'..')' = F1..F6 (vistas/atajos de juegos)
            '{{'  => ((byte)0x3B, (byte)0x00),
            '}}'  => ((byte)0x3C, (byte)0x00),
            '['  => ((byte)0x3D, (byte)0x00),
            ']'  => ((byte)0x3E, (byte)0x00),
            '('  => ((byte)0x3F, (byte)0x00),
            ')'  => ((byte)0x40, (byte)0x00),
            // '!'..'$' = F7..F10 (atajos de juegos)
            '!'  => ((byte)0x41, (byte)0x00),
            '@'  => ((byte)0x42, (byte)0x00),
            '#'  => ((byte)0x43, (byte)0x00),
            '$'  => ((byte)0x44, (byte)0x00),
            _    => (QwertyScancode(c), (byte)c),
        }};
        keyboard.EnqueueKey(sc, ascii);
        platform.Keyboard.EnqueueHostScancode(sc);
        autoKeyBreak = sc;   // break code pendiente (el 8042 real lo manda al soltar)
    }}
    else if (autoKeyBreak != 0 && autoKeyPhase == 5)
    {{
        // PAKU_AUTOKEYS con break code: sin esto, un juego que espera la transición
        // make->break en el puerto 0x60 no reacciona nunca a las teclas sintéticas.
        platform.Keyboard.EnqueueHostScancode((byte)(autoKeyBreak | 0x80));
        autoKeyBreak = 0;
    }}
    _autoKeysDone: ;

    // --- Input: ratón ---
    // SetPosition recibe SIEMPRE píxeles de juego; el contrato virtual-640 (shr cx,1) lo aplica
    // el driver vía mouse.XScale (fijado arriba al valor resuelto por mundo). No escalar aquí.
    var mp = Raylib.GetMousePosition();
    // Captura estilo DOSBox: el primer click DENTRO de la imagen captura (DisableCursor =
    // confinado + solo el cursor del juego); Ctrl+F10 libera (arriba). Al perder el foco
    // (Alt-Tab) se libera sola para no secuestrar el escritorio.
    if (!noMouseCapture)
    {{
        if (!mouseCaptured && Raylib.IsWindowFocused() && Raylib.IsMouseButtonPressed(MouseButton.Left)
            && mp.X >= viewOx && mp.X < viewOx + w * viewScale
            && mp.Y >= viewOy && mp.Y < viewOy + h * pixelAspect * viewScale)
        {{
            Raylib.DisableCursor();
            mouseCaptured = true;
            // Referencia limpia: el primer frame tras capturar no debe entregar como movimiento
            // el salto que hay entre el puntero del escritorio y el del juego.
            _prevRawMouse = mp; _rawAccX = _rawAccY = 0;
            mouse.RawMotionMode = _rawMotion;
        }}
        else if (mouseCaptured && !Raylib.IsWindowFocused())
        {{
            Raylib.EnableCursor();
            mouseCaptured = false;
            mouse.RawMotionMode = false;
        }}
    }}
    if (mouseCaptured)
    {{
        // Movimiento CRUDO del ratón (antes de recortar contra los bordes de la imagen), en
        // píxeles de juego. De aquí salen los mickeys de AH=0B — el driver real los cuenta del
        // hardware, no del cursor recortado (mouse.RawMotionMode). Sin esto, todo lo que el
        // usuario mueva estando ya pegado a un borde se pierde, y un juego que integra mickeys
        // en su propio espacio no vuelve a alcanzar el extremo contrario.
        // Las fracciones se acumulan (mismo truco que el mickey_accum de DOSBox): con la ventana
        // a 3x, un píxel de ratón es 1/3 de píxel de juego y truncar cada frame se comería el
        // movimiento lento.
        if (mouse.RawMotionMode)
        {{
            _rawAccX += (mp.X - _prevRawMouse.X) / viewScale;
            _rawAccY += (mp.Y - _prevRawMouse.Y) / (viewScale * pixelAspect);
            int _rdx = (int)_rawAccX, _rdy = (int)_rawAccY;
            if (_rdx != 0 || _rdy != 0)
            {{
                _rawAccX -= _rdx; _rawAccY -= _rdy;
                mouse.AddRawMotion(_rdx, _rdy);
            }}
        }}
        // Con el cursor deshabilitado la posición virtual de GLFW es ilimitada: reclampar a la
        // imagen para que el borde no se vuelva pegajoso (sin esto, salirse 500 px por la
        // derecha exige desandarlos antes de que el cursor del juego se mueva).
        float _cx = Math.Clamp(mp.X, viewOx, viewOx + w * viewScale - 1);
        float _cy = Math.Clamp(mp.Y, viewOy, viewOy + h * pixelAspect * viewScale - 1);
        if (_cx != mp.X || _cy != mp.Y)
        {{
            Raylib.SetMousePosition((int)_cx, (int)_cy);
            mp = new System.Numerics.Vector2(_cx, _cy);
        }}
        // El reposicionamiento anterior NO es movimiento del usuario: la referencia del delta
        // crudo es la posición ya recortada, o el salto se contaría como mickeys.
        _prevRawMouse = mp;
    }}
    ushort btns = 0;
    if (Raylib.IsMouseButtonDown(MouseButton.Left)) btns |= 1;
    if (Raylib.IsMouseButtonDown(MouseButton.Right)) btns |= 2;
    frameCount++;
    if (clickActive == null && scriptClicks.Count > 0 && frameCount >= scriptClicks.Peek()[2])
    {{
        int[] _c = scriptClicks.Dequeue();
        int _hold = _c.Length > 3 ? _c[3] : 6;
        // fases: posarse 4 frames con botón SUELTO (si el wiggle dejó el botón pulsado, el
        // juego vería un arrastre, no un click), luego press desde pressStart hasta pressEnd
        clickActive = new[] {{ _c[0], _c[1], frameCount + 4, _hold == 0 ? -1 : frameCount + 4 + _hold }};
    }}
    if (clickActive != null)
    {{
        mouse.SetPosition((ushort)clickActive[0], (ushort)clickActive[1]);
        bool _down = frameCount >= clickActive[2] && (clickActive[3] < 0 || frameCount < clickActive[3]);
        mouse.SetButtons((ushort)(_down ? 1 : 0));
        // tras el press, 4 frames de release visibles en la misma posición antes de soltar el control
        if (clickActive[3] >= 0 && frameCount >= clickActive[3] + 4) clickActive = null;
    }}
    else if (mouseWiggle)
    {{
        // PAKU_MOUSE_WIGGLE=1: movimiento sintético en coordenadas de juego (prueba
        // headless del camino de eventos INT 33h sin ratón físico); =2: además clicks.
        wiggleTick++;
        mouse.SetPosition((ushort)(160 + 100 * MathF.Sin(wiggleTick * 0.05f)),
                          (ushort)(100 + 60 * MathF.Cos(wiggleTick * 0.05f)));
        mouse.SetButtons((ushort)(wiggleClicks ? (wiggleTick / 40) % 2 : 0));
    }}
    else
    {{
        mouse.SetPosition((ushort)Math.Clamp((mp.X - viewOx) / viewScale, 0, w - 1),
                          (ushort)Math.Clamp((mp.Y - viewOy) / (viewScale * pixelAspect), 0, h - 1));
        mouse.SetButtons(btns);
    }}
    _liveInputDone: ;

    // --- Input: joystick emulado (puerto 0x201) — flechas integran posición, Ctrl/Alt = botones.
    //     Integrador (no auto-centrado): los juegos de stick absoluto (la torreta persigue el
    //     ángulo del stick) necesitan que al soltar la tecla la posición SE QUEDE donde está. ---
    if (Raylib.IsKeyDown(KeyboardKey.Left))  joyX = Math.Max(0,   joyX - 8);
    if (Raylib.IsKeyDown(KeyboardKey.Right)) joyX = Math.Min(255, joyX + 8);
    if (Raylib.IsKeyDown(KeyboardKey.Up))    joyY = Math.Max(0,   joyY - 8);
    if (Raylib.IsKeyDown(KeyboardKey.Down))  joyY = Math.Min(255, joyY + 8);
    // Con una cinta el joystick lo mueve la cinta (evento `J`), no el teclado del anfitrion.
    if (!PakuPort.Core.InputTape.Replaying)
    {{
        platform.GamePort.X = (byte)joyX;
        platform.GamePort.Y = (byte)joyY;
        byte jb = 0;
        if (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.Space)) jb |= 1;
        if (Raylib.IsKeyDown(KeyboardKey.LeftAlt)) jb |= 2;
        platform.GamePort.Buttons = jb;
    }}

    // --- Ejecutar lógica en thread separado ---
    if (!gameStarted)
    {{
        gameStarted = true;
        cpu.FrameGate = new System.Threading.ManualResetEventSlim(false);
        new Thread(() =>
        {{
            try
            {{
                // Banco de pruebas por funcion: PAKU_CORPUS graba
                // casos de entrada/salida por funcion desde el interprete. Apagado no cuesta nada.
                {{
                    var _avisoCorpus = PakuPort.Core.CorpusRecorder.ArrancaDesdeEntorno();
                    var _avisoCapas = PakuPort.Core.PerfilCapas.ArrancaDesdeEntorno();
                    var _avisoCob = PakuPort.Core.Cobertura.ArrancaDesdeEntorno();
                    if (_avisoCapas != null) Console.Error.WriteLine(_avisoCapas);
                    if (_avisoCob != null) Console.Error.WriteLine(_avisoCob);
                    if (_avisoCorpus != null) Console.Error.WriteLine(_avisoCorpus);
                    // Las funciones ya traducidas se registran solas (Remake/RemakeRegistro.cs,
                    // un ModuleInitializer): esto solo lo cuenta.
                    var _avisoPortadas = PakuPort.Core.FuncionesPortadas.Aviso();
                    if (_avisoPortadas != null) Console.Error.WriteLine(_avisoPortadas);
                }}
{(game.Format == "com" ? "                cpu.AX = loadSeg; // COM bootstrap: mov ax,ds before retf" : "")}
{interpOnlyEntry}
{(snapEntryKey >= 0 && snapEachKeyExp >= 0 ? $@"                // bucle de sesión (transcrito del supervisor real, ver game.json snapshotLoop)
                while (snapBytes != null && mem.RdU16(cpu.DS, 0x{snapUntilOffExp:X4}) == 0)
                {{
                    cpu.SP -= 2; // el ret de la Fn hace SP += 2
                    {em.Namespace}.{em.ClassName}.Fn_{snapEachKeyExp:X5}(cpu, mem);
                }}" : "")}
                // (gate DEEP): si el unwind FarRet llega al FONDO del hilo, transporta
                // el destino REAL del retf (cadena driver->driver o continuación empujada a
                // mano, no pareada con ningún call-site). Morir aquí pierde el juego con una
                // ""salida limpia"" prematura; el hardware simplemente CONTINÚA en ese destino.
                // Se reanuda en el interp; un destino basura acaba en error VISIBLE (mejor
                // que muerte muda). Guard anti-bucle: mismo destino+SP dos veces seguidas.
                {{
                    ushort _rbCs = 0, _rbIp = 0, _rbSp = 0;
                    while (cpu.FarRet)
                    {{
                        // El PRIMER destino descartado del episodio (latch FarRetBottom*) es la
                        // verdad del hardware; los FarRetCs/Ip posteriores son artefactos del unwind.
                        ushort _dCs = cpu.FarRetBottomValid ? cpu.FarRetBottomCs : cpu.FarRetCs;
                        ushort _dIp = cpu.FarRetBottomValid ? cpu.FarRetBottomIp : cpu.FarRetIp;
                        if (_dCs == _rbCs && _dIp == _rbIp && cpu.SP == _rbSp) break; // anti-bucle
                        _rbCs = _dCs; _rbIp = _dIp; _rbSp = cpu.SP;
                        cpu.FarRet = false;   // consume el flag y cierra el episodio (limpia el latch)
                        if (PakuPort.Core.Cpu.GapTrace)
                            Console.Error.WriteLine(""[FARRET-BOTTOM] reanudando en "" + _rbCs.ToString(""X4"") + "":"" + _rbIp.ToString(""X4""));
                        // WholeProgram (precedente handoff VM-INT): en el fondo no hay
                        // continuación inline — los ret/retf del resto del programa siguen
                        // EN el interp; los call far/near despachan a lifteado (velocidad).
                        bool _wp = PakuPort.Core.Interp.WholeProgram;
                        PakuPort.Core.Interp.WholeProgram = true;
                        try
                        {{
                            if (PakuPort.Core.Interp.Run(cpu, mem, _rbCs, _rbIp,
                                    {em.Namespace}.{em.ClassName}.CallKey, {em.Namespace}.{em.ClassName}.FarDispatch))
                                cpu.FarRet = true;   // retf terminal: encadenar la siguiente continuación
                            else
                                break;               // fin de programa de verdad
                        }}
                        finally {{ PakuPort.Core.Interp.WholeProgram = _wp; }}
                    }}
                }}
                exitReason = ""El juego volvio a DOS (salida limpia)"";
            }}
            catch (PakuPort.Core.Platform.ProgramExitException) {{ exitReason = ""El juego volvio a DOS (INT 21h/4Ch)""; }}
            catch (NotSupportedException ex)
            {{
                error = ex.Message;
                exitReason = ""Error: opcode no soportado @ CS:"" + cpu.CS.ToString(""X4"") + ""  "" + ex.Message;
                // La pila C# ES la cadena de llamadas del juego (Fn_xxxxx) — con ella un
                // opcode-no-soportado se diagnostica sin arqueología.
                if (Environment.GetEnvironmentVariable(""PAKU_TRACE_EX"") == ""1"")
                    Console.Error.WriteLine(ex.ToString());
            }}
            catch (Exception ex)
            {{
                error = ex.GetType().Name + ex.Message;
                exitReason = ""Error: "" + ex.GetType().Name + "" @ CS:"" + cpu.CS.ToString(""X4"") + ""  "" + ex.Message;
                if (Environment.GetEnvironmentVariable(""PAKU_TRACE_EX"") == ""1"")
                    Console.Error.WriteLine(ex.ToString());
            }}
            PakuPort.Core.CorpusRecorder.Termina();
        PakuPort.Core.FuncionesPortadas.Vuelca();
            exitCs = cpu.CS;
            finished = true;
        }}) {{ IsBackground = true }}.Start();
    }}
    if (!PakuPort.Core.Platform.MachineTimer.SinFrenoAhora) cpu.Budget = perFrameBudget;
    cpu.FrameGate?.Set();

    // --- Retrace. El timer de máquina (PIT canal 0 → IRQ0) YA NO se avanza aquí: vive en el
    //     hilo del juego por tiempo real (cpu.OnPeriodicPoll → MachineTimer.Poll), modelo
    //     DOSBox. La fase de retrace (0x3DA) la gobierna el reloj de pared en
    //     VgaPortHandler.StatusRead; no se toca aquí. ---

    // --- Render ---
    // Presentación del front buffer (modelo hardware, VideoState): el hilo del juego captura UN
    // frame de VRAM por refresco del VGA (70 Hz, MachineTimer.CaptureFrontBuffer) y aquí
    // presentamos SIEMPRE ese frame. Un único capturador y un único presentador → sin carrera
    // (nada de latch de retrace vs poll de cursor pisándose, ni caída a VRAM viva). Antes de la
    // primera captura se renderiza la VRAM viva una vez como arranque. PAKU_LIVE_VRAM=1 fuerza
    // VRAM viva siempre (diagnóstico / A/B del parpadeo).
    // Cambio de modo de vídeo (p.ej. texto 720x400 tras arrancar en 320x200): recrear la
    // textura al tamaño nuevo y ajustar la escala. El hilo del juego puede cambiar de modo
    // a MITAD de frame, así que la verdad es el tamaño del framebuffer devuelto — solo se
    // sube a la textura si coincide (un frame perdido en la transición es invisible).
    byte[] fb;
    int vw, vh;
    if (!liveVram && platform.Video.TryGetFrontBuffer(out var _front, out var _fw, out var _fh)
        && _front.Length == _fw * _fh * 4)
    {{
        fb = _front; vw = _fw; vh = _fh;
    }}
    else
    {{
        fb = platform.Video.RenderFramebuffer();
        vw = platform.Video.Width; vh = platform.Video.Height;
    }}
    if (fb.Length == vw * vh * 4 && (vw != w || vh != h))
    {{
        // Un cambio de tamano de presentacion RECREA la textura y reescala todo. Si ocurre a
        // menudo —un juego cuyo CRTC baila entre dos geometrias— la imagen SALTA en pantalla, y
        // sin decirlo es indistinguible de un problema de render.
        Console.Error.WriteLine($""[VIDEO] presentacion {{w}}x{{h}} -> {{vw}}x{{vh}}"");
        w = vw; h = vh;
        Raylib.UnloadTexture(tex);
        img = new Image {{ Width = w, Height = h, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 }};
        tex = Raylib.LoadTextureFromImage(img);
    }}
    // Escala y letterbox por frame: cubre cambio de modo de vídeo, F11 y redimensionado.
    // Corrección de aspecto (DOSBox `aspect=true`): un CRT de la época mostraba TODOS estos
    // modos a 4:3 con píxeles NO cuadrados, así que 320x200 y 640x200 hay que estirarlos en
    // vertical (los círculos salen ovalados si no). Ctrl+F11 conmuta; PAKU_NO_ASPECT=1 la apaga.
    // A 4:3 CRT shows EVERY mode at 4:3: displayed ratio w/(h*pixelAspect) == 4/3, so
    // pixelAspect = 0.75*(w/h): 1.2 for 320x200 (DOSBox aspect=true), 0.6 for 320x400
    // double-scan, 1.35 for 720x400 text. The old reciprocal (4/3)/(w/h) squashed
    // 320x200 to 1.92:1 (mild, went unnoticed) and blew 320x400 up to 0.48:1 (measured on a test game).
    pixelAspect = aspectCorrection ? ((float)w / h) * (3f / 4f) : 1f;
    viewScale = Math.Min((float)Raylib.GetScreenWidth() / w,
                         (float)Raylib.GetScreenHeight() / (h * pixelAspect));
    if (escalaEntera && viewScale >= 1f) viewScale = (float)Math.Floor(viewScale);
    viewOx = (Raylib.GetScreenWidth() - w * viewScale) / 2;
    viewOy = (Raylib.GetScreenHeight() - h * pixelAspect * viewScale) / 2;
    if (fb.Length == w * h * 4)
        unsafe
        {{
            fixed (byte* ptr = fb)
                Raylib.UpdateTexture(tex, ptr);
        }}

    // PAKU_SCALE3X=1: el frame del juego, ampliado x3 respetando los bordes (Scale3x) antes de
    // estirarlo a la ventana. Ampliar repitiendo píxeles da bloques y ampliar interpolando da una
    // foto borrosa; esto prolonga los bordes diagonales y deja los colores planos como estaban,
    // que es lo que necesitan un panel de instrumentos, una tipografía de mapa de bits o un sprite.
    // Trabaja sobre ÍNDICES de paleta y no sobre RGB: ver `EscaladoPixelArt`.
    var texPres = tex;
    int escFuente = 1;
    // PAKU_XBRZ=<2..6>: el frame del juego ampliado con xBRZ (Zenju, vía DOSBox-X) antes de
    // estirarlo — bordes diagonales redondeados y tipografía suave, el look «HD» que Scale3x no
    // da (una prueba manual lo descartó: en los textos se veía peor que el original). Trabaja
    // sobre el RGBA presentado (xBRZ está DISEÑADO para color con distancia YCbCr, al revés que
    // Scale3x). Cuesta ~15 ms el reescalado, así que va CACHEADO por generación de frame: el
    // juego que produce pocos frames nuevos por segundo sólo reescala esos.
    // Hash MUESTREADO del frame presentado: reescalar 70 veces/s el mismo frame tiraría 15 ms por
    // captura. No vale FrameGeneration: sube con cada captura a 70 Hz aunque el juego no haya
    // pintado nada nuevo.
    long _presGen = 0;
    if ((_xbrzFactor >= 2) && fb.Length == w * h * 4)
    {{
        _presGen = 1469598103934665603L;
        for (int _hi = 0; _hi < fb.Length; _hi += 149)
            _presGen = (_presGen ^ fb[_hi]) * 1099511628211L;
    }}
    if (_xbrzFactor >= 2 && fb.Length == w * h * 4)
    {{
        long _gen = _presGen;
        if (_gen != _xbrzGen || _xbrzOut == null || _xbrzOut.Length != fb.Length * _xbrzFactor * _xbrzFactor)
        {{
            _xbrzOut = PakuPort.Core.EscaladoXbrz.EscalaRgba(fb, w, h, _xbrzFactor);
            _xbrzGen = _gen;
        }}
        int _sw = w * _xbrzFactor, _sh = h * _xbrzFactor;
        if (_escTex.Width != _sw || _escTex.Height != _sh)
        {{
            if (_escTex.Width != 0) Raylib.UnloadTexture(_escTex);
            _escTex = Raylib.LoadTextureFromImage(new Image {{
                Width = _sw, Height = _sh, Mipmaps = 1,
                Format = PixelFormat.UncompressedR8G8B8A8 }});
            Raylib.SetTextureFilter(_escTex, TextureFilter.Bilinear);
        }}
        unsafe {{ fixed (byte* _p = _xbrzOut) Raylib.UpdateTexture(_escTex, _p); }}
        texPres = _escTex;
        escFuente = _xbrzFactor;
    }}
    else if (PakuPort.Core.EscaladoPixelArt.Activo
        && platform.Video.TryGetIndices(out var _sidx, out var _siw, out var _sih)
        && _siw == w && _sih == h)
    {{
        var _s3 = PakuPort.Core.EscaladoPixelArt.Amplia(_sidx, _siw, _sih,
                                                        platform.Video.PaletaEfectiva());
        int _sw = _siw * PakuPort.Core.EscaladoPixelArt.Factor;
        int _sh = _sih * PakuPort.Core.EscaladoPixelArt.Factor;
        if (_escTex.Width != _sw || _escTex.Height != _sh)
        {{
            if (_escTex.Width != 0) Raylib.UnloadTexture(_escTex);
            _escTex = Raylib.LoadTextureFromImage(new Image {{
                Width = _sw, Height = _sh, Mipmaps = 1,
                Format = PixelFormat.UncompressedR8G8B8A8 }});
            Raylib.SetTextureFilter(_escTex, TextureFilter.Bilinear);
        }}
        unsafe {{ fixed (byte* _p = _s3) Raylib.UpdateTexture(_escTex, _p); }}
        texPres = _escTex;
        escFuente = PakuPort.Core.EscaladoPixelArt.Factor;
    }}

    Raylib.BeginDrawing();
    Raylib.ClearBackground(Color.Black);
    // DrawTexturePro (no DrawTextureEx): la corrección de aspecto necesita escala distinta en X
    // y en Y, y DrawTextureEx sólo admite un factor uniforme.
    Raylib.DrawTexturePro(texPres,
        new Rectangle(0, 0, w * escFuente, h * escFuente),
        new Rectangle(viewOx, viewOy, w * viewScale, h * pixelAspect * viewScale),
        new System.Numerics.Vector2(0, 0), 0, Color.White);
    // PASO 4 remakes: instancias HD del frame ENCIMA del lienzo sombra (que queda de fallback).
    //
    // Lo que el juego dibuja DESPUÉS encima del asset reemplazado —cursor, resaltado del menú,
    // disparos, texto— tiene que seguir viéndose. Si no, una textura a pantalla completa tapa el
    // juego ENTERO y el usuario mira una fotografía: una prueba manual se leyó como
    // ""ni el teclado ni el ratón funcionan"" cuando el juego respondía a todo por debajo, y como
    // ""las armas se oyen disparar pero no se ven"". Una instancia a pantalla completa además no
    // muere nunca por diseño (un dibujo one-shot que nadie sobrescribe vive para siempre), así que
    // el congelado era permanente.
    //
    // Regla: la primera vez que se ve una instancia se guarda el lienzo sombra de su rect (la
    // BASE: el aspecto original de justo lo que la textura sustituye) y en cada frame se agujerea
    // la textura HD allí donde el lienzo ACTUAL difiere de esa base. Comparar contra la base en
    // vez de acumular marcas es lo que evita estelas: cuando el juego restaura el fondo el píxel
    // vuelve a coincidir y el agujero se cierra solo, sin necesidad de que nadie lo caduque.
    if (PakuPort.Core.RemakeMap.Active && fb.Length == w * h * 4)
    {{
        _hdSeen.Clear();
        foreach (var _ri in PakuPort.Core.RemakeMap.TakeFrame())
        {{
            if (!_hdTex.TryGetValue(_ri.Tex, out var _t))
            {{ _t = Raylib.LoadTexture(_ri.Tex); _hdTex[_ri.Tex] = _t; }}
            Raylib.DrawTexturePro(_t, new Rectangle(0, 0, _t.Width, _t.Height),
                new Rectangle(viewOx + _ri.X * viewScale, viewOy + _ri.Y * pixelAspect * viewScale,
                              _ri.W * viewScale, _ri.H * pixelAspect * viewScale),
                new System.Numerics.Vector2(0, 0), 0, Color.White);
            _hdSeen.Add(_ri);
            if (_hdSinAgujerear) continue;
            int _x0 = Math.Max(0, _ri.X), _y0 = Math.Max(0, _ri.Y);
            int _x1 = Math.Min(w, _ri.X + _ri.W), _y1 = Math.Min(h, _ri.Y + _ri.H);
            if (_x1 <= _x0 || _y1 <= _y0) continue;
            int _bw = _x1 - _x0, _bh = _y1 - _y0;
            if (!_hdBase.TryGetValue(_ri, out var _base) || _base.Length != _bw * _bh * 4)
            {{
                _base = new byte[_bw * _bh * 4];
                for (int _y = 0; _y < _bh; _y++)
                    Array.Copy(fb, ((_y0 + _y) * w + _x0) * 4, _base, _y * _bw * 4, _bw * 4);
                _hdBase[_ri] = _base;
                continue;
            }}
            // Tramos por fila de lo que el juego ha cambiado desde la base, repintando el lienzo
            // sombra justo ahí. Un cursor son ~15 tramos de una fila: el coste es despreciable
            // frente a comparar el rect, que para una pantalla completa son 64.000 píxeles.
            for (int _y = 0; _y < _bh; _y++)
            {{
                int _run = -1;
                for (int _x = 0; _x <= _bw; _x++)
                {{
                    bool _dif = false;
                    if (_x < _bw)
                    {{
                        int _o = ((_y0 + _y) * w + _x0 + _x) * 4, _b = (_y * _bw + _x) * 4;
                        _dif = fb[_o] != _base[_b] || fb[_o + 1] != _base[_b + 1] ||
                               fb[_o + 2] != _base[_b + 2];
                    }}
                    if (_dif && _run < 0) _run = _x;
                    else if (!_dif && _run >= 0)
                    {{
                        Raylib.DrawTexturePro(tex,
                            new Rectangle(_x0 + _run, _y0 + _y, _x - _run, 1),
                            new Rectangle(viewOx + (_x0 + _run) * viewScale,
                                          viewOy + (_y0 + _y) * pixelAspect * viewScale,
                                          (_x - _run) * viewScale, pixelAspect * viewScale),
                            new System.Numerics.Vector2(0, 0), 0, Color.White);
                        _run = -1;
                    }}
                }}
            }}
        }}
        // La base de una instancia que ya no vive no puede sobrevivirla: si el mismo asset vuelve
        // a salir en esa posición, su base tiene que ser el fondo de ENTONCES, no el de hace rato.
        if (_hdBase.Count > _hdSeen.Count)
        {{
            _hdDead.Clear();
            foreach (var _k in _hdBase.Keys) if (!_hdSeen.Contains(_k)) _hdDead.Add(_k);
            foreach (var _k in _hdDead) _hdBase.Remove(_k);
        }}
    }}
    if (error != null && !finished)
        Raylib.DrawText(error, 10, Raylib.GetScreenHeight() - 30, 16, Color.Red);
    if (finished)
    {{
        // Terminación VISIBLE (spec §5c): el hilo de lógica murió (salida limpia, excepción o
        // error del interp). NO dejar la ventana congelada muda: overlay con el motivo hasta que
        // el usuario cierre (ESC o el botón de la ventana; el bucle sigue vivo mientras tanto).
        int _oy = Raylib.GetScreenHeight() - 64;
        Raylib.DrawRectangle(0, _oy, Raylib.GetScreenWidth(), 64, new Color((byte)0, (byte)0, (byte)0, (byte)210));
        string _ov = exitReason.Length > 0 ? exitReason : ""El juego terminó"";
        if (_ov.Length > 74) _ov = _ov.Substring(0, 74) + ""..."";
        Raylib.DrawText(_ov, 12, _oy + 8, 18, exitReason.StartsWith(""Error"") ? Color.Red : Color.Yellow);
        Raylib.DrawText(""Pulsa F12 o cierra la ventana"", 12, _oy + 40, 14, Color.Gray);
    }}
    if (Raylib.GetTime() < speedOsdUntil)
    {{
        // Aviso de velocidad: se apaga solo a los 3 s para no estorbar jugando. La referencia
        // que se anota es el IBM PC de 4,77 MHz (~315), la máquina de los juegos de la época.
        string _sp = $""velocidad {{cpu.CyclesPerMs}}  (IBM PC 4,77 MHz = 315)"";
        Raylib.DrawRectangle(8, 8, 12 + Raylib.MeasureText(_sp, 20), 32,
                             new Color((byte)0, (byte)0, (byte)0, (byte)190));
        Raylib.DrawText(_sp, 14, 14, 20, Color.Yellow);
    }}
    Raylib.EndDrawing();

    // PAKU_SCREENSHOT_CADA=<s>: captura de la pantalla COMPUESTA (juego + capas HD del remake)
    // cada s segundos, a logs/pantalla-<n>.png. Es la única forma headless de VER lo que el
    // jugador vería: PAKU_FB_PNG vuelca el frame nativo del juego, sin la composición.
    if (_capturaCadaS > 0 && Raylib.GetTime() >= _capturaProxima)
    {{
        _capturaProxima = Raylib.GetTime() + _capturaCadaS;
        Directory.CreateDirectory(""logs"");
        Raylib.TakeScreenshot($""logs/pantalla-{{_capturaN++:D3}}.png"");
    }}

    // Bombeo del stream de audio: por cada sub-buffer ya reproducido, drenar el ring (silencio
    // si no hay bastante) y subirlo. El ritmo del ring lo marca el IRQ del SB, acompasado a
    // tiempo real, así que no se desborda ni se acelera. Sin fuentes, sube silencio (inocuo).
    if (_hostAudio != null)
    {{
        if (_sbStreamReady)
        {{
            while (Raylib.IsAudioStreamProcessed(_sbStream))
            {{
                int _got = _hostAudio.Read(_audioBuf, _audioBuf.Length);
                for (int _i = _got; _i < _audioBuf.Length; _i++) _audioBuf[_i] = 0;
                Raylib.UpdateAudioStream(_sbStream, new ReadOnlySpan<short>(_audioBuf), _audioBuf.Length);
                if (_wavFs != null)
                {{
                    if (_wavBuf == null) _wavBuf = new byte[_audioBuf.Length * 2];
                    System.Buffer.BlockCopy(_audioBuf, 0, _wavBuf, 0, _audioBuf.Length * 2);
                    _wavFs.Write(_wavBuf, 0, _audioBuf.Length * 2);
                    _wavData += _audioBuf.Length * 2;
                }}
            }}
        }}
        else if (_wavFs != null)
        {{
            // headless sin stream de Raylib: drenar A TIEMPO REAL, no a ritmo de frame. A ritmo
            // de frame (~100 fps × 2048 muestras) se drenaba ~10× más deprisa de lo que el
            // hardware habría reproducido, el ring salía casi siempre vacío y el WAV era un
            // acordeón de silencios que no representaba lo que suena — inservible como juez.
            double _tNow = Raylib.GetTime();
            if (_wavT0 == 0) _wavT0 = _tNow;
            int _due = (int)((_tNow - _wavT0) * 22050) - (int)(_wavData / 2);
            while (_due > 0)
            {{
                int _chunk = Math.Min(_due, _audioBuf.Length);
                int _got = _hostAudio.Read(_audioBuf, _chunk);
                for (int _i = _got; _i < _chunk; _i++) _audioBuf[_i] = 0;
                if (_wavBuf == null) _wavBuf = new byte[_audioBuf.Length * 2];
                System.Buffer.BlockCopy(_audioBuf, 0, _wavBuf, 0, _chunk * 2);
                _wavFs.Write(_wavBuf, 0, _chunk * 2);
                _wavData += _chunk * 2;
                _due -= _chunk;
            }}
        }}
    }}

    // Terminación VISIBLE (spec §5c): al morir el hilo de lógica, SIEMPRE volcar logs/exit.log
    // (motivo, CS, timestamp) + screenshot con el overlay ya presentado. Una sola vez. Sin esto,
    // el usuario de una entrega no dejaba NI UN log al salir/petar (ventana congelada muda).
    if (finished && !exitHandled)
    {{
        exitHandled = true;
        try
        {{
            Directory.CreateDirectory(""logs"");
            File.WriteAllText(""logs/exit.log"",
                $""timestamp: {{DateTime.Now:yyyy-MM-dd HH:mm:ss}}\nmotivo: {{(exitReason.Length > 0 ? exitReason : ""terminado"")}}\nCS: {{exitCs:X4}}\ninterp_insns: {{PakuPort.Core.Interp.InsnCount}}\nticks: {{PakuPort.Core.Cpu.TickCount}}\n"");
            Raylib.TakeScreenshot(""logs/exit.png"");
            Console.Error.WriteLine($""[EXIT] {{(exitReason.Length > 0 ? exitReason : ""terminado"")}} CS={{exitCs:X4}} -> logs/exit.log"");
        }}
        catch (Exception _lex) {{ Console.Error.WriteLine(""[EXIT] no pude volcar logs/exit.log: "" + _lex.Message); }}
    }}

    if (runClock.ElapsedMilliseconds >= _nextHb)
    {{
        _nextHb += 5000;
        System.Console.Error.WriteLine($""[HB] t={{runClock.ElapsedMilliseconds / 1000.0:F1}}s interp={{PakuPort.Core.Interp.InsnCount:N0}} ticks={{PakuPort.Core.Cpu.TickCount:N0}} retraceReads={{platform.Video.RetraceCounter:N0}} fps={{Raylib.GetFPS()}} bnd={{PakuPort.Core.Platform.DosPlatform.DiagBoundaryCalls}} bndIfOff={{PakuPort.Core.Platform.DosPlatform.DiagBoundaryIfOff}} irq0={{PakuPort.Core.Platform.DosPlatform.DiagIrq0}} irq1={{PakuPort.Core.Platform.DosPlatform.DiagIrq1}} pitHz={{platform.Pit.Channel0FrequencyHz:F1}} cyc={{cpu.CyclesPerMs}} ivt8={{mem.RdU16(0, 8 * 4 + 2):X4}}:{{mem.RdU16(0, 8 * 4):X4}} modo=0x{{platform.Video.VideoMode:X2}} {{platform.Video.Width}}x{{platform.Video.Height}} pres={{w}}x{{h}} fb={{fb.Length}} srm={{platform.Video.ShiftRegisterMode}} seq4={{platform.Video.SeqRegs[4]:X2}} gfx={{(platform.Video.IsGraphicsFromRegs ? 1 : 0)}} insn32={{PakuPort.Core.Interp32.Ejecutadas:N0}} irq32={{platform.Dpmi?.IrqEntregadas ?? 0}} "" +
            $""vista={{w}}x{{h}} asp={{pixelAspect:F6}} escala={{viewScale:F6}} ox={{viewOx:F2}} oy={{viewOy:F2}} "" +
            $""pantalla={{Raylib.GetScreenWidth()}}x{{Raylib.GetScreenHeight()}}"");
    }}

    // PAKU_CORTE_FOTO: the guest froze at PAKU_AUTOEXIT_INSN; once a few frames of it have been
    // presented (HD layers included), save the composed screen and leave.
    if (PakuPort.Core.CorteDeterminista.Congelado && ++_fotoFrames >= 4)
    {{
        Raylib.TakeScreenshot(""screenshot.png"");
        Console.Error.WriteLine(""[CORTE] foto compuesta -> screenshot.png"");
        Console.Out.Flush(); Console.Error.Flush();
        Environment.Exit(0);
    }}
    if (autoExitMs > 0 && runClock.ElapsedMilliseconds >= autoExitMs)
    {{
        int nonBlack = 0;
        for (int i = 0; i + 3 < fb.Length; i += 4)
            if (fb[i] != 0 || fb[i + 1] != 0 || fb[i + 2] != 0) nonBlack++;
        Console.WriteLine($""[autoexit] logica={{(gameStarted ? ""arrancada"" : ""no"")}} terminada={{finished}} error={{error ?? ""ninguno""}} pixeles_no_negros={{nonBlack}}/{{w * h}}"");
        // El indice del banco de pruebas se escribe tambien aqui: el hilo del juego no termina
        // nunca en una captura headless, asi que sin esto el corpus quedaba grabado pero MUDO.
        PakuPort.Core.CorpusRecorder.Termina();
        PakuPort.Core.FuncionesPortadas.Vuelca();
        // PAKU_MEMDUMP=<fichero>: vuelca el MiB de memoria VIVA al terminar. Es la única
        // forma de leer el código que un juego genera o descomprime en runtime (el de la
        // imagen del EXE ya no es el que corre) y de ver dónde ha dejado sus vectores: así
        // se localizó el driver de teclado de un juego de prueba, colgado de INT 15h y no del INT 9.
        string? _memDump = Environment.GetEnvironmentVariable(""PAKU_MEMDUMP"");
        if (!string.IsNullOrEmpty(_memDump) && PakuPort.Core.Memory.Last != null)
            File.WriteAllBytes(_memDump, PakuPort.Core.Memory.Last.Raw);
        // PAKU_MEMDUMP_ALTO=<fichero>: la memoria PLANA (por encima de 1 MiB). En un juego de 32
        // bits todo lo que importa vive ahi —lo que descomprime al cargar un mapa, sus tablas—, y
        // PAKU_MEMDUMP solo ve el MiB de abajo: buscar ahi y no encontrar nada se lee como
        // «no esta» cuando lo que pasa es que no se ha mirado donde es.
        string? _memDumpAlto = Environment.GetEnvironmentVariable(""PAKU_MEMDUMP_ALTO"");
        if (!string.IsNullOrEmpty(_memDumpAlto) && PakuPort.Core.Memory.Last?.High is byte[] _alto)
        {{
            File.WriteAllBytes(_memDumpAlto, _alto);
            Console.WriteLine($""[MEMDUMP] {{_alto.Length / (1024 * 1024)}} MiB de memoria plana -> {{_memDumpAlto}}"");
        }}
        Raylib.TakeScreenshot(""screenshot.png"");
        // PAKU_FB_PNG=<fichero>: volcado del framebuffer NATIVO (WxH de juego, sin escala ni
        // aspecto) — la fuente pixel-exacta para jueces y catalogos (el screenshot de ventana
        // sale reescalado y no sirve para recortar sprites 1:1).
        string? _fbPng = Environment.GetEnvironmentVariable(""PAKU_FB_PNG"");
        if (!string.IsNullOrEmpty(_fbPng) && fb.Length == w * h * 4)
            unsafe
            {{
                fixed (byte* _fp = fb)
                {{
                    var _fi = new Image {{ Data = _fp, Width = w, Height = h, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 }};
                    Raylib.ExportImage(_fi, _fbPng);
                }}
            }}
        break;
    }}
}}

var gapLines = new List<string>();
if (cpu.GapHits != null) gapLines.AddRange(cpu.GapHits.OrderBy(k => k).Select(k => $""0x{{k:X5}}""));
if (cpu.FarDispatchMisses != null) gapLines.AddRange(cpu.FarDispatchMisses.OrderBy(k => k).Select(k => $""miss 0x{{k:X5}}""));
if (cpu.FarRetUnwinds != null) gapLines.AddRange(cpu.FarRetUnwinds.OrderBy(k => k).Select(k => $""farret-unwind cs={{(ushort)(k >> 32):X4}} destino_descartado={{(ushort)(k >> 16):X4}}:{{(ushort)k:X4}}""));
if (gapLines.Count > 0 || Interp.InsnCount > 0)
{{
    Directory.CreateDirectory(""logs"");
    File.WriteAllLines(""logs/gaps.log"", gapLines);
    Console.Error.WriteLine($""[GAP] {{gapLines.Count}} huecos/misses unicos volcados a logs/gaps.log; {{Interp.InsnCount:N0}} instrucciones interpretadas"");
}}

if (_wavFs != null)
{{
    try
    {{
        _wavFs.Seek(0, System.IO.SeekOrigin.Begin);
        var _bw = new System.IO.BinaryWriter(_wavFs);
        _bw.Write(new[] {{ 'R', 'I', 'F', 'F' }}); _bw.Write((uint)(36 + _wavData));
        _bw.Write(new[] {{ 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }});
        _bw.Write((uint)16); _bw.Write((ushort)1); _bw.Write((ushort)1);
        _bw.Write((uint)22050); _bw.Write((uint)44100); _bw.Write((ushort)2); _bw.Write((ushort)16);
        _bw.Write(new[] {{ 'd', 'a', 't', 'a' }}); _bw.Write((uint)_wavData);
        _bw.Flush(); _wavFs.Close();
        Console.Error.WriteLine(""[wav] "" + _wavData + "" bytes PCM -> "" + _wavPath);
    }}
    catch (Exception _we) {{ Console.Error.WriteLine(""[wav] cierre: "" + _we.Message); }}
}}
// Traza de los servicios del mundo de 32 bits (host DPMI + DOS plano). Se escribe SIEMPRE al
// cerrar, no solo cuando algo peta: un juego que corre pero NO AVANZA no dice por que, y esta
// es la unica lista de lo que ha pedido y de lo que se le ha negado.
if (platform.Dpmi != null && platform.Dpmi.Traza.Count > 0)
    try
    {{
        System.IO.Directory.CreateDirectory(""logs"");
        using var _sw = new System.IO.StreamWriter(""logs/servicios32.log"");
        foreach (var _l in platform.Dpmi.Traza) _sw.WriteLine(_l);
        if (platform.Dpmi.Rechazados.Count > 0)
        {{
            _sw.WriteLine();
            _sw.WriteLine(""--- RECHAZADOS ---"");
            foreach (var _r in platform.Dpmi.Rechazados) _sw.WriteLine(_r);
        }}
        Console.Error.WriteLine($""[GAP32] {{platform.Dpmi.Traza.Count}} servicios de 32 bits -> logs/servicios32.log"");
    }}
    catch {{ }}
Raylib.UnloadTexture(tex);
if (_sbStreamReady) Raylib.UnloadAudioStream(_sbStream);
if (!_noAudio && Raylib.IsAudioDeviceReady()) Raylib.CloseAudioDevice();
platform.Mpu.Cerrar();   // el sintetizador del sistema es un recurso del SO: se suelta
Raylib.CloseWindow();

static (byte scancode, byte ascii) MapKey(KeyboardKey k, bool shift, bool numLock)
{{
    // BIOS keypad rule (DOSBox-X bios_keyboard.cpp:809): (shift held) XOR (NumLock on)
    // picks the digit variant of scancodes 0x47-0x53; otherwise navigation (ascii=0,
    // 0xF0 for center 5). Games key different ACTIONS off the ascii byte: one test
    // game moves the cursor on 0x4D00 (Kp6+ascii 0) but does something else on 0x4D36
    // (Kp6+ascii '6') — NumLock must default ON like a real boot for that to work.
    bool kpDigit = shift ^ numLock;
    // Scancode set 1 de la A a la Z en orden alfabético (DOSBox-X keyboard.cpp).
    ReadOnlySpan<byte> QwertyScan = new byte[]
    {{
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
    }};
    return k switch
    {{
        KeyboardKey.Escape    => (0x01, 0x1B),
        KeyboardKey.Enter     => (0x1C, 0x0D),
        KeyboardKey.Space     => (0x39, 0x20),
        KeyboardKey.Backspace => (0x0E, 0x08),
        KeyboardKey.Tab       => (0x0F, 0x09),
        KeyboardKey.Up        => (0x48, 0x00),
        KeyboardKey.Down      => (0x50, 0x00),
        KeyboardKey.Left      => (0x4B, 0x00),
        KeyboardKey.Right     => (0x4D, 0x00),
        KeyboardKey.F1        => (0x3B, 0x00),
        KeyboardKey.F2        => (0x3C, 0x00),
        KeyboardKey.F3        => (0x3D, 0x00),
        KeyboardKey.F4        => (0x3E, 0x00),
        KeyboardKey.F5        => (0x3F, 0x00),
        KeyboardKey.F6        => (0x40, 0x00),
        KeyboardKey.F7        => (0x41, 0x00),
        KeyboardKey.F8        => (0x42, 0x00),
        KeyboardKey.F9        => (0x43, 0x00),
        KeyboardKey.F10       => (0x44, 0x00),
        // Scancodes set 1 QWERTY, portados literalmente de DOSBox-X keyboard.cpp:1647-1695.
        // NO son lineales en el alfabeto: la fórmula 0x1E+(k-A) que había aquí solo acertaba
        // con la 'a' y daba a 'j'/'k' los scancodes de ';'/'\'' — juegos que deciden por
        // scancode en vez de por ascii veían teclas ajenas.
        >= KeyboardKey.A and <= KeyboardKey.Z =>
            (QwertyScan[k - KeyboardKey.A],
             (byte)(shift ? 'A' + (k - KeyboardKey.A) : 'a' + (k - KeyboardKey.A))),
        >= KeyboardKey.Zero and <= KeyboardKey.Nine =>
            ((byte)(k == KeyboardKey.Zero ? 0x0B : 0x02 + (int)(k - KeyboardKey.One)),
             (byte)(shift ? "")!@#$%^&*(""[k - KeyboardKey.Zero] : '0' + (k - KeyboardKey.Zero))),
        // Bloque de navegación dedicado: el BIOS los entrega con el MISMO scancode que su
        // gemelo del numpad y ascii 0 (DOSBox-X keyboard.cpp:1766-1775, prefijo E0 en el
        // 8042). Faltaban por completo en esta tabla: se descartaban en el `sc != 0` del
        // bucle de input, así que el juego no llegaba a verlos nunca.
        KeyboardKey.Home      => (0x47, 0x00),
        KeyboardKey.End       => (0x4F, 0x00),
        KeyboardKey.PageUp    => (0x49, 0x00),
        KeyboardKey.PageDown  => (0x51, 0x00),
        KeyboardKey.Insert    => (0x52, 0x00),
        KeyboardKey.Delete    => (0x53, 0x00),
        KeyboardKey.Grave     => (0x29, (byte)(shift ? '~' : '`')),
        KeyboardKey.Apostrophe   => (0x28, (byte)(shift ? '""' : '\'')),
        KeyboardKey.Backslash    => (0x2B, (byte)(shift ? '|' : '\\')),
        KeyboardKey.KpMultiply   => (0x37, (byte)'*'),
        KeyboardKey.KpDivide     => (0x35, (byte)'/'),
        KeyboardKey.Kp7        => (0x47, (byte)(kpDigit ? '7' : 0x00)),
        KeyboardKey.Kp8        => (0x48, (byte)(kpDigit ? '8' : 0x00)),
        KeyboardKey.Kp9        => (0x49, (byte)(kpDigit ? '9' : 0x00)),
        KeyboardKey.Kp4        => (0x4B, (byte)(kpDigit ? '4' : 0x00)),
        KeyboardKey.Kp5        => (0x4C, (byte)(kpDigit ? '5' : 0xF0)),
        KeyboardKey.Kp6        => (0x4D, (byte)(kpDigit ? '6' : 0x00)),
        KeyboardKey.Kp1        => (0x4F, (byte)(kpDigit ? '1' : 0x00)),
        KeyboardKey.Kp2        => (0x50, (byte)(kpDigit ? '2' : 0x00)),
        KeyboardKey.Kp3        => (0x51, (byte)(kpDigit ? '3' : 0x00)),
        KeyboardKey.Kp0        => (0x52, (byte)(kpDigit ? '0' : 0x00)),
        KeyboardKey.KpDecimal  => (0x53, (byte)(kpDigit ? '.' : 0x00)),
        KeyboardKey.KpAdd      => (0x4E, (byte)'+'),
        KeyboardKey.KpSubtract => (0x4A, (byte)'-'),
        KeyboardKey.KpEnter    => (0x1C, 0x0D),
        KeyboardKey.Minus      => (0x0C, (byte)(shift ? '_' : '-')),
        KeyboardKey.Equal      => (0x0D, (byte)(shift ? '+' : '=')),
        KeyboardKey.LeftBracket  => (0x1A, (byte)(shift ? '{{' : '[')),
        KeyboardKey.RightBracket => (0x1B, (byte)(shift ? '}}' : ']')),
        KeyboardKey.Semicolon  => (0x27, (byte)(shift ? ':' : ';')),
        KeyboardKey.Comma      => (0x33, (byte)(shift ? '<' : ',')),
        KeyboardKey.Period     => (0x34, (byte)(shift ? '>' : '.')),
        KeyboardKey.Slash      => (0x35, (byte)(shift ? '?' : '/')),
        // Modificadores y bloqueos. Faltaban por completo: caían en el `_ => (0,0)` y el
        // `if (sc != 0)` del bucle de input los tiraba, así que NUNCA llegaba al juego el
        // make/break de Shift, Ctrl ni Alt. Dos consecuencias: un juego que lee scancodes
        // crudos del 0x60 no puede ver esas teclas (todo el que las use para algo), y el
        // INT 9 del BIOS jamás recibía 2A/1D/38, así que los flags de teclado de la BDA
        // (40:17) se quedaban a cero para siempre — INT 16h AH=02 mentía y el propio BIOS
        // no aplicaba Shift/Ctrl al meter la tecla en su buffer.
        // ASCII 0 en todos: el BIOS no genera carácter para ellas (la tabla scan_to_scanascii
        // de bios_keyboard.cpp las tiene a 0x0000), sólo mueven los flags.
        KeyboardKey.LeftShift    => (0x2A, 0x00),
        KeyboardKey.RightShift   => (0x36, 0x00),
        KeyboardKey.LeftControl  => (0x1D, 0x00),
        KeyboardKey.RightControl => (0x1D, 0x00),
        KeyboardKey.LeftAlt      => (0x38, 0x00),
        KeyboardKey.RightAlt     => (0x38, 0x00),
        KeyboardKey.CapsLock     => (0x3A, 0x00),
        KeyboardKey.NumLock      => (0x45, 0x00),
        KeyboardKey.ScrollLock   => (0x46, 0x00),
        _ => (0, 0),
    }};
}}

// Scancodes que el BIOS trata como estado (modificadores y bloqueos) y NUNCA entrega como
// entrada de INT 16h: van sólo por la ruta hardware. Meterlos en el buffer de INT 16h haría
// que un juego que espera tecla se ""comiera"" un Shift como si fuera una pulsación.
static bool IsModifierScancode(byte sc) =>
    sc is 0x2A or 0x36 or 0x1D or 0x38 or 0x3A or 0x45 or 0x46;
";
    File.WriteAllText(Path.Combine(outDir, "Program.cs"), programCs);

    // 2e. .csproj
    string csproj = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <!-- The host binary must NEVER share the DOS exe's name: gameDir resolution prefers
         BaseDirectory when it sees the game exe there, and a same-named host
         made every data open resolve into bin/ (a collision class that bit more than once). -->
    <AssemblyName>{game.Id}-host</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Raylib-cs"" Version=""8.0.0"" />
    <PackageReference Include=""Iced"" Version=""1.21.0"" />
  </ItemGroup>
</Project>
";
    File.WriteAllText(Path.Combine(outDir, $"{game.Id}.csproj"), csproj);
    // pakuport-host.json: what this host was generated from, for `remake doctor` / `capture`.
    // Capture needs a boot it can replay: the real startup, or a COM/MZ entry without a snapshot.
    ExportCapabilities.Write(outDir, game.Id, gamePath, game.ExePath, game.CyclesPerMs, "dos16",
        $"pakuport export --game \"{gamePath}\" --out \"{outDir}\"" + (bootReal ? " --boot-real" : "") +
        (seedsFile != null ? $" --seeds \"{seedsFile}\"" : "") +
        (!string.IsNullOrEmpty(probeKeys) ? $" --probe-keys \"{probeKeys}\"" : "") +
        (portadasDir != null ? $" --portadas \"{portadasDir}\"" : ""),
        supportsCapture: bootMem != null || snapEntryKey < 0);

    // 2f. README con instrucciones. ES LO PRIMERO QUE VE UN TERCERO, asi que dice lo que la
    // carpeta ES de verdad. La version anterior seguia diciendo "falta host de renderizado para
    // ver la salida grafica" mucho despues de que el host con ventana, sonido y raton fuera lo
    // normal: un README desfasado hace mas dano que uno corto, porque se lee como un hecho.
    string readme = $@"# {game.Title} — Port .NET generado por PakuPort

Este directorio es un proyecto .NET 8 que **ejecuta el juego**: abre ventana, suena y responde al
teclado y al raton. Generado por PakuPort el {DateTime.Now:yyyy-MM-dd}.

## Como ejecutar

`dotnet run`, o directamente `bin/Release/net8.0/{game.Id}-host.exe`. El ejecutable original del
juego (`{Path.GetFileName(game.ExePath)}`) y sus datos tienen que estar en esta carpeta.

## Que hay aqui

| | |
|---|---|
| `Program.cs` | el anfitrion: ventana, escalado, entrada, sonido y las palancas `PAKU_*` |
| `Core/` | el motor: CPU, memoria, interprete y la plataforma DOS (video, teclado, raton, disco, sonido) |
| `GameCode*.cs` | el codigo del juego traducido a C#, con el ensamblador original en un comentario por linea |
| `Remake/` | las funciones que alguien ha reescrito a mano; el juego las usa de verdad |
| `{Path.GetFileName(game.ExePath)}` y los datos | el juego original, sin tocar |

## Como corre el juego, que es lo que mas confunde

**El motor es el INTERPRETE**: `Core/Interp.cs` ejecuta las instrucciones del ejecutable original
tal cual. `GameCode*.cs` es una traduccion mecanica 1:1 que se genera y se compila, pero **no es
lo que se ejecuta por defecto**; esta ahi como material de lectura y como acelerador futuro.

Lo que SI se ejecuta traducido es `Remake/`: cada funcion de ahi sustituye a la del binario en su
direccion de entrada, y **todo lo demas lo sigue haciendo el original**. Asi se puede portar un
juego funcion a funcion sin romperlo por el camino. `PAKU_SIN_PORTADAS=1` las apaga todas sin
recompilar, que es como se comprueba que no cambian nada.

## Metricas de la generacion

- Funciones descubiertas y traducidas a `GameCode*.cs`: {allFuncs.Count}
- Tipos de opcode distintos: {em.OpcodesUsed.Count}
- Opcodes no soportados: {em.NotSupported.Count} (si se alcanzan, revientan en vez de mentir)
";
    File.WriteAllText(Path.Combine(outDir, "README.md"), readme);

    Console.WriteLine($"  Proyecto generado:");
    Console.WriteLine($"    {game.Id}.csproj");
    Console.WriteLine($"    Program.cs (host)");
    Console.WriteLine($"    GameCode.cs ({allFuncs.Count} funciones)");
    Console.WriteLine($"    Core/ (Cpu, Memory, Fpu, Platform)");
    Console.WriteLine($"    README.md");
    Console.WriteLine();
    Console.WriteLine($"  Para ejecutar:");
    Console.WriteLine($"    cd {outDir}");
    Console.WriteLine($"    copy {Path.GetFileName(game.ExePath)} .");
    Console.WriteLine($"    dotnet run");

    // EL REMAKE VIAJA CON EL JUEGO: las funciones ya traducidas del tablero se copian al proyecto
    // y se compilan con el, asi que dentro del juego son codigo C# normal. Sin --portadas no se
    // genera nada y el export es el de siempre.
    if (portadasDir != null) RemakeAlExport.Copia(portadasDir, outDir);

    return 0;
}

// =====================================================================
// DISCOVER: descubre TODAS las funciones (corpus + CodeMap sweep)
// =====================================================================

static int CmdDiscover(GameConfig game, string gamePath)
{
    Console.WriteLine($"== PakuPort discover: {game.Id} ==");
    var exeBytes = File.ReadAllBytes(game.ExePath);
    byte[] imgBytes;
    if (game.Format == "com")
    {
        imgBytes = exeBytes;
    }
    else
    {
        ushort ls = game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110;
        imgBytes = ExeImage.NormalizeMzImage(exeBytes, ls);
        PakuPort.Core.ExePackDecompressor.PatchResidentThunks(imgBytes);
    }
    var exe = new ExeImage(imgBytes);
    exe.LoadSegment = game.LoadSegment > 0 ? game.LoadSegment : (ushort)0;
    exe.CodeMap = game.CodeMap;

    // Recoger semillas del corpus
    var seeds = new List<int>();
    if (Directory.Exists(game.CorpusDir))
        foreach (var dir in Directory.GetDirectories(game.CorpusDir, "FUN_1000_*"))
        {
            try { seeds.Add(ExeImage.ResidentBase + FunctionVerifier.ParseOffset(Path.GetFileName(dir))); }
            catch { }
        }

    // Entry point del MZ
    var mem = new Memory();
    ushort loadSeg = game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110;
    var mz = MzLoader.Load(exeBytes, mem, loadSeg);
    seeds.Add(ExeImage.ResidentBase + mz.InitCS * 16 + mz.InitIP);

    // Semillas de ENTRADA de la traza dinámica (destinos de CallsFromTo, seeds.spice86.json):
    // entradas de función resueltas en runtime — incluidos destinos de indirectos que el
    // análisis estático no alcanza. Ya son file offsets (= clave del decoder). Genérico:
    // cualquier juego con un dump de flujo produce su seeds.spice86.json.
    int spiceCount = 0;
    if (game.SpiceSeeds != null) { seeds.AddRange(game.SpiceSeeds); spiceCount = game.SpiceSeeds.Count; }

    Console.WriteLine($"  Semillas explicitas: {seeds.Count} (corpus+entry {seeds.Count - spiceCount}, dinámicas {spiceCount})");
    Console.WriteLine($"  CodeMap: {game.CodeMap?.Count ?? 0} offsets");

    var all = Emitter.DiscoverAll(exe, game.Overlays, seeds, comMode: game.Format == "com", vroommStubTable: game.VroommStubTable);
    Console.WriteLine($"  Funciones descubiertas: {all.Count}");

    // Lista de funciones para el grabador autónomo (explore_exec --record-src):
    // ghidra offsets de las entradas descubiertas en el RESIDENTE + el mapa de
    // funciones con params INLINE (algunos runtimes: N bytes tras el CALL, la
    // función ajusta la dirección de retorno — el driver de llamadas lo emula).
    string funcsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gamePath))!, "functions.json");
    var ghidraOffs = all.Keys.Where(OverlayDirectory.IsResident)
                             .Select(k => k - ExeImage.ResidentBase)
                             .Where(g => g >= 0).OrderBy(g => g).ToList();
    var inline = Emitter.DetectInlineParamFunctions(all.Values.ToList());
    string inlineJson = string.Join(", ", inline
        .Where(kv => OverlayDirectory.IsResident(kv.Key))
        .OrderBy(kv => kv.Key)
        .Select(kv => $"\"0x{kv.Key - ExeImage.ResidentBase:X}\": {kv.Value}"));
    File.WriteAllText(funcsPath, "{ \"functions\": [" +
        string.Join(", ", ghidraOffs.Select(g => $"\"0x{g:X}\"")) + "],\n  \"inline\": { " +
        inlineJson + " } }");
    Console.WriteLine($"  functions.json -> {funcsPath} ({ghidraOffs.Count} entradas, {inline.Count} con inline params)");

    // Estadísticas de cobertura
    int totalCodeMapBytes = game.CodeMap?.Count ?? 0;
    int coveredBytes = 0;
    if (game.CodeMap != null)
    {
        var covered = new HashSet<int>();
        foreach (var fn in all.Values)
            foreach (var insn in fn.Instructions)
                covered.Add(fn.Base + insn.Offset);
        coveredBytes = game.CodeMap.Count(off => covered.Contains(off));
    }
    Console.WriteLine($"  Cobertura CodeMap: {coveredBytes}/{totalCodeMapBytes} offsets ({(totalCodeMapBytes > 0 ? 100.0 * coveredBytes / totalCodeMapBytes : 0):F1}%)");

    // Emitir para verificar compilación
    var funcList = all.Values.ToList();
    int rootKey = seeds.FirstOrDefault(s => all.ContainsKey(s));
    if (rootKey == 0 && all.Count > 0) rootKey = all.Keys.First();
    var em = Emitter.EmitProgram(funcList, rootKey, vroommStub: game.VroommStub, overlayDir: game.Overlays, vroommStubTable: game.VroommStubTable, overlayLoadBase: game.OverlayLoadBase, exe: exe, thunkRedirects: game.ThunkRedirects);
    Console.WriteLine($"  Opcodes: {em.OpcodesUsed.Count} tipos, {em.NotSupported.Count} no soportados");
    if (em.NotSupported.Count > 0)
        Console.WriteLine("  No soportados: " + string.Join("; ", em.NotSupported.Distinct().Take(20)));

    Console.WriteLine($"  Codigo generado: {em.Source.Length:N0} caracteres");

    // Distribución por zona
    int gameCount = 0, crtCount = 0, ovCount = 0;
    foreach (var fn in all.Values)
    {
        if (fn.Key >= 0x15A00) ovCount++;
        else if (fn.Key >= 0x10200) crtCount++;
        else gameCount++;
    }
    Console.WriteLine($"  Juego (<0x10200): {gameCount}, C-runtime: {crtCount}, Overlay: {ovCount}");

    return 0;
}

static List<int> CollectSeeds(GameConfig game, byte[] exeBytes)
{
    var seeds = new List<int>();

    // Entry override (el punto de entrada real del juego, si lo sabemos)
    if (!string.IsNullOrEmpty(game.EntryOverride))
    {
        string eo = game.EntryOverride;
        if (eo.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) eo = eo[2..];
        seeds.Add(ExeImage.ResidentBase + Convert.ToInt32(eo, 16));
    }

    // Corpus
    if (Directory.Exists(game.CorpusDir))
        foreach (var dir in Directory.GetDirectories(game.CorpusDir, "FUN_1000_*"))
        {
            try { seeds.Add(ExeImage.ResidentBase + FunctionVerifier.ParseOffset(Path.GetFileName(dir))); }
            catch { }
        }

    // Extra seeds manuales (offsets hex en game.json)
    foreach (var s in game.ExtraSeeds)
    {
        string sv = s;
        if (sv.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) sv = sv[2..];
        seeds.Add(ExeImage.ResidentBase + Convert.ToInt32(sv, 16));
    }

    // Semillas de entrada del dump de Spice86 (destinos de CallsFromTo). Ya son file offsets
    // (= clave del decoder), NO se les suma ResidentBase.
    if (game.SpiceSeeds != null)
        seeds.AddRange(game.SpiceSeeds);

    // Entry point
    if (game.Format == "com")
    {
        seeds.Add(ExeImage.ResidentBase + 0x100); // COM entry = seg:0100
    }
    else
    {
        var mzPeek = MzLoader.Load(exeBytes, new Memory(), game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110);
        seeds.Add(ExeImage.ResidentBase + mzPeek.InitCS * 16 + mzPeek.InitIP);
    }

    return seeds;
}

static void CopyIfExists(string src, string dst)
{
    if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
}

/// <summary>Escribe el C# emitido en 1..N ficheros (partial class) para no ahogar al
/// compilador con un único .cs de decenas de MB. Divide por miembros de clase (los
/// métodos empiezan con "        public static " a 8 espacios de indentación). Cada
/// fichero reenvuelve su porción con el mismo header/namespace/partial-class.</summary>
static int WritePartitionedGameCode(string outDir, string source, int maxBytesPerFile = 3_000_000)
{
    // Un solo fichero si es pequeño.
    if (source.Length <= maxBytesPerFile)
    {
        File.WriteAllText(Path.Combine(outDir, "GameCode.cs"), source);
        return 1;
    }
    const string marker = "\n        public static ";
    int firstMember = source.IndexOf(marker, StringComparison.Ordinal);
    int classClose = source.LastIndexOf("\n    }\n}", StringComparison.Ordinal);
    if (firstMember < 0 || classClose < 0 || classClose <= firstMember)
    {
        File.WriteAllText(Path.Combine(outDir, "GameCode.cs"), source);
        return 1;
    }
    // Header hasta la apertura de la clase (incluye using/namespace/partial-class {).
    string header = source[..firstMember] + "\n";
    // El footer empieza con \n: los chunks terminan sin salto de línea (justo antes
    // del siguiente marcador), así la llave de cierre no se pega a la última línea.
    const string footer = "\n    }\n}\n";
    string membersBlob = source[firstMember..classClose];   // empieza con "\n        public static ..."
    // Trocear por miembros conservando el delimitador.
    var members = new List<string>();
    int idx = 0;
    while (idx < membersBlob.Length)
    {
        int next = membersBlob.IndexOf(marker, idx + 1, StringComparison.Ordinal);
        if (next < 0) next = membersBlob.Length;
        members.Add(membersBlob[idx..next]);
        idx = next;
    }
    int part = 0, written = 0;
    var sb = new System.Text.StringBuilder();
    void Flush()
    {
        if (sb.Length == 0) return;
        File.WriteAllText(Path.Combine(outDir, $"GameCode.{part}.cs"), header + sb + footer);
        sb.Clear(); part++; written++;
    }
    foreach (var mem in members)
    {
        if (sb.Length > 0 && sb.Length + mem.Length > maxBytesPerFile) Flush();
        sb.Append(mem);
    }
    Flush();
    return written;
}

// =====================================================================
// INIT: crea game.json y CodeMap estático para un juego nuevo
// =====================================================================

// =====================================================================
// PORT: la cadena completa juego→port .NET en un solo comando, sin pasos
// manuales: init → dump-image → export (boot-real si hay memimage) → copia
// de datos del juego → dotnet build → smoke headless con métrica objetiva.
// =====================================================================

/// <summary>¿Es un ejecutable de MODO PROTEGIDO (LE/LX + extensor tipo DOS/4GW)? Se mira la
/// cabecera, que es lo único que no miente: un LE se envuelve en un MZ (el stub que dice
/// "this program requires a DOS extender"), así que por fuera parece de 16 bits y sólo el
/// puntero `e_lfanew` a la firma real lo distingue.</summary>
static bool EsEjecutableLe(string exePath)
{
    try
    {
        using var f = File.OpenRead(exePath);
        var cab = new byte[0x40];
        if (f.Read(cab, 0, 0x40) < 0x40) return false;
        if (cab[0] != 'M' || cab[1] != 'Z') return false;
        uint off = BitConverter.ToUInt32(cab, 0x3C);
        if (off == 0 || off + 2 > f.Length) return false;
        f.Seek(off, SeekOrigin.Begin);
        int a = f.ReadByte(), b = f.ReadByte();
        return a == 'L' && (b == 'E' || b == 'X');
    }
    catch { return false; }
}

/// <summary>DOS extender used by a 16-bit MZ stub, when it is one PakuPort cannot run yet.
/// Returns null for plain real-mode binaries and for LE/LX ones (those go through Interp32,
/// which is supported — see EsEjecutableLe).
///
/// Presence of a single string proves nothing: Rational's own stub names DOS/4G and Phar Lap
/// alongside DOS/16M so the three can coexist, so all three test binaries match
/// several markers. What discriminates is WHICH marker and how early: measured on the five
/// binaries at hand, the two DOS/16M games carry "DOS/16M" at 0x1018 (inside the stub) while the
/// Phar Lap binary carries PHARLAP at 0xBF94 and its "DOS/16M" only at 0x13557 (inside data).</summary>
static string? DetectarExtensorNoSoportado(string exePath)
{
    try
    {
        if (EsEjecutableLe(exePath)) return null;
        var bytes = File.ReadAllBytes(exePath);
        string txt = System.Text.Encoding.ASCII.GetString(bytes);

        // Dónde acaba la imagen MZ: lo que venga DESPUÉS es la carga en modo protegido, y su
        // firma dice de qué formato es. Un MZ sin nada detrás no es un ejecutable protegido:
        // es un LANZADOR que ejecuta otro fichero (en un juego de prueba, el EXE imprime el banner
        // de DOS/4GW y arranca un .OVL, que es el juego de verdad).
        int lastPage = bytes.Length > 5 ? bytes[2] | (bytes[3] << 8) : 0;
        int pages = bytes.Length > 5 ? bytes[4] | (bytes[5] << 8) : 0;
        int finImagen = lastPage != 0 ? (pages - 1) * 512 + lastPage : pages * 512;
        bool hayCarga = finImagen > 0 && finImagen < bytes.Length - 4;

        // Phar Lap: el ejecutable protegido va en formato P2/P3 justo detrás de la imagen MZ.
        // Medido en 3D Studio R4: imagen hasta 0x2DED4 y ahí la firma "P3".
        if (hayCarga && bytes[finImagen] == (byte)'P' &&
            (bytes[finImagen + 1] == (byte)'2' || bytes[finImagen + 1] == (byte)'3'))
            return $"Phar Lap 386|DOS-Extender, ejecutable en formato " +
                   $"P{(char)bytes[finImagen + 1]} a partir de 0x{finImagen:X}";
        if (txt.Contains("386|DOS-Extender") || txt.Contains("PHARLAP"))
            return "Phar Lap 386|DOS-Extender";

        // La presencia de una cadena no prueba nada: el stub de Rational nombra DOS/4G y Phar
        // Lap junto a DOS/16M para que los tres puedan convivir, así que los tres binarios de
        // prueba casan con varias marcas. Lo que discrimina es cuál y A QUÉ ALTURA: en los dos
        // juegos DOS/16M "DOS/16M" está en 0x1018, dentro del stub; en el de Phar Lap, sólo en 0x13557,
        // dentro de datos.
        int d16 = txt.IndexOf("DOS/16M", StringComparison.Ordinal);
        if (d16 >= 0 && d16 < 0x8000)
            return "DOS/16M (Rational Systems, modo protegido de 16 bits) — SOPORTADO desde " +
                   "2026-08-16, pero es reciente: un juego de prueba arranca entero y otro " +
                   "se queda en el LINEXE_LOADER del runtime";

        if (txt.Contains("DOS/4G"))
            return hayCarga
                ? "DOS/4GW (la carga protegida va detrás de la imagen MZ)"
                : "un lanzador de DOS/4GW: este MZ no lleva carga, ejecuta OTRO fichero " +
                  "(ese otro fichero es el juego — portar ése)";
        return null;
    }
    catch { return null; }
}

static int CmdPort(string[] rest)
{
    if (rest.Length < 1 || !File.Exists(rest[0]))
    {
        Console.WriteLine("uso: pakuport port <RUTA\\JUEGO.EXE|.COM> [--id <nombre>] [--out <dir>] [--smoke-ms <n>] [--keys <autokeys>] [--no-smoke] [--no-package]");
        return 1;
    }
    string exePath = Path.GetFullPath(rest[0]);
    // The game id defaults to the EXE file name, which is NOT unique across games: two unrelated
    // games in the test set ship an EXE with the same name, and a generic GAME.EXE is used by
    // any number of others. --id gives the second one its own games/<id>/ instead of inheriting the first
    // one's calibration; the guard below refuses to guess when it is missing.
    string gameName = (GetOpt(rest, "--id") ?? Path.GetFileNameWithoutExtension(exePath)).ToLowerInvariant();
    string outDir = GetOpt(rest, "--out") ?? Path.Combine("export", gameName);
    int smokeMs = int.Parse(GetOpt(rest, "--smoke-ms") ?? "20000");
    string? keys = GetOpt(rest, "--keys");
    bool noSmoke = HasFlag(rest, "--no-smoke");
    var resumen = new List<string>();

    Console.WriteLine($"== PakuPort port: {Path.GetFileName(exePath)} → {outDir} ==");

    // Say it up front. Without this the port completes, the export runs, and the user just sees
    // a black window — which reads as "PakuPort is broken" instead of "this binary needs an
    // extender PakuPort does not implement". The port still runs: it gets as far as the stub.
    string? extensor = DetectarExtensorNoSoportado(exePath);
    if (extensor != null)
    {
        bool soportado = extensor.StartsWith("DOS/16M", StringComparison.Ordinal);
        Console.WriteLine($"  AVISO: este binario usa {extensor}.");
        if (!soportado)
        {
            Console.WriteLine("  PakuPort soporta modo protegido de 32 bits por LE/LX");
            Console.WriteLine("  y de 16 bits por DOS/16M, NO este. El port llegará hasta el extensor y ahí se quedará.");
        }
        resumen.Add(soportado ? $"extensor: {extensor}" : $"extensor NO soportado: {extensor}");
    }

    // 1. init: game.json + codemap.json derivados del binario. Un game.json EXISTENTE se
    // respeta (puede llevar calibración a mano: entryOverride, budgetPerFrame, extraSeeds);
    // --reinit fuerza la regeneración.
    string gameJsonPath = Path.Combine("games", gameName, "game.json");
    if (File.Exists(gameJsonPath) && !HasFlag(rest, "--reinit"))
    {
        // Reusing a game.json belonging to a DIFFERENT binary is the worst outcome here: the
        // port silently inherits someone else's hand calibration (cyclesPerMs, entryOverride,
        // gamePortAbsent...) and the resulting misbehaviour looks like an emulation bug. Only
        // the exePath can tell the two apart, so compare it and stop rather than guess.
        string previoExe = GameConfig.Load(gameJsonPath).ExePath;
        if (!string.IsNullOrEmpty(previoExe) &&
            !Path.GetFullPath(previoExe).Equals(exePath, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"games\\{gameName}\\game.json ya existe y es de OTRO binario:");
            Console.Error.WriteLine($"  el registrado: {previoExe}");
            Console.Error.WriteLine($"  el que portas: {exePath}");
            Console.Error.WriteLine("Dos juegos distintos con el mismo nombre de fichero. Elige:");
            Console.Error.WriteLine($"  pakuport port \"{exePath}\" --id <otro-nombre>   (recomendado)");
            Console.Error.WriteLine($"  pakuport port \"{exePath}\" --reinit             (PISA la config existente)");
            return 1;
        }
        resumen.Add("init: game.json existente respetado (--reinit para regenerar)");
    }
    else if (EsEjecutableLe(exePath))
    {
        // MODO PROTEGIDO de 32 bits (LE/LX + extensor DOS). El `init` normal no vale: deriva
        // segmento de carga, corpus y codemap de un MZ de 16 bits, y aquí no hay nada de eso —
        // el juego no se traduce, se ejecuta entero en Interp32 sobre FlatServices. La config
        // que hace falta cabe en seis líneas y sale toda del propio binario.
        Directory.CreateDirectory(Path.Combine("games", gameName));
        string titulo = char.ToUpperInvariant(gameName[0]) + gameName[1..];
        File.WriteAllText(gameJsonPath, $@"{{
  ""id"": ""{gameName}"",
  ""title"": ""{titulo}"",
  ""format"": ""le"",
  ""exePath"": ""{exePath.Replace("\\", "/")}"",
  ""corpusDir"": ""games/{gameName}/corpus"",
  ""cyclesPerMs"": 20000,
  ""sb"": {{ ""base"": ""0x220"", ""irq"": 7, ""dma"": 1 }}
}}
");
        Console.WriteLine("  formato LE detectado en la cabecera: game.json de modo protegido generado");
        resumen.Add("init: OK (LE, modo protegido de 32 bits)");
    }
    else
    {
        int rcInit = CmdInit(new[] { "init", exePath, "--id", gameName });
        if (rcInit != 0) return PortFail(resumen, "init");
        resumen.Add("init: OK");
    }
    var game = GameConfig.Load(gameJsonPath);
    bool isCom = game.Format == "com";
    bool isLe = game.Format == "le";

    // 2. dump-image (solo MZ de 16 bits): imagen de memoria canónica que habilita boot-real.
    // Un LE no pasa por aquí: no hay lifter, ni corpus, ni imagen que congelar.
    bool bootReal = false;
    if (!isCom && !isLe)
    {
        string exDir = Path.Combine("games", gameName, "explorer");
        int rcDump = CmdDumpImage(game, gameJsonPath, exDir);
        bootReal = rcDump == 0 && File.Exists(Path.Combine(exDir, "memimage.bin"));
        resumen.Add(bootReal ? "dump-image: OK (boot-real)" : "dump-image: FALLO — export sin boot-real");
    }

    // 3. export del port .NET; las semillas de autocuración acumuladas del juego
    // (games/<id>/seeds-runtime.log, fase 3) se siembran solas si existen
    string seedsPath = Path.Combine("games", gameName, "seeds-runtime.log");
    string? seedsFile = File.Exists(seedsPath) ? seedsPath : null;
    // --keys conduce TAMBIÉN el probe de descubrimiento (no sólo el smoke): así el export
    // liftea el código que sólo se alcanza tras input de menú (misma cadena que el smoke).
    int rc = CmdExport(game, outDir, gameJsonPath, bootReal: bootReal, seedsFile: seedsFile, probeKeys: keys);
    if (rc != 0) return PortFail(resumen, "export");
    resumen.Add(seedsFile != null ? $"export: OK (+semillas {seedsFile})" : "export: OK");

    // 4. datos del juego junto al port (sin pisar lo generado por el export)
    int copied = CopyGameDataTree(Path.GetDirectoryName(exePath)!, outDir);
    resumen.Add($"datos: {copied} ficheros copiados");

    // 5. compilar el port.
    // Los 300 s de antes mataban el port de los juegos grandes cuando el build era FRIO, y por eso
    // fallaba de forma intermitente: al segundo intento, con los artefactos calientes, el mismo
    // juego compilaba de sobra. Un limite que depende de si la caché está templada no distingue un
    // export roto de uno lento, que es justo lo que un límite tiene que hacer. Lo que se quiere
    // atajar aquí es un build COLGADO, no uno tardón: 20 minutos.
    var (buildRc, buildOut) = RunCapture("dotnet", "build -c Release", outDir, 1_200_000);
    if (buildRc != 0)
    {
        Console.Error.WriteLine(string.Join('\n', buildOut.TakeLast(15)));
        return PortFail(resumen, "build");
    }
    resumen.Add("build: OK");

    // 6. smoke headless: el host reporta [autoexit] logica/error/pixeles y screenshot.png
    if (!noSmoke)
    {
        var env = new Dictionary<string, string> { ["PAKU_NOSPLASH"] = "1", ["PAKU_AUTOEXIT_MS"] = smokeMs.ToString() };
        if (keys != null) env["PAKU_AUTOKEYS"] = keys;
        var (smokeRc, smokeOut) = RunCapture("dotnet", "run -c Release --no-build", outDir, smokeMs + 120_000, env);
        string? metrics = smokeOut.LastOrDefault(l => l.Contains("[autoexit]"));
        resumen.Add(metrics != null ? $"smoke: {metrics.Trim()}" : $"smoke: SIN MÉTRICA (rc={smokeRc}); últimas líneas:");
        if (metrics == null)
            foreach (var l in smokeOut.TakeLast(8)) resumen.Add($"    {l}");
    }

    // 7. empaquetado doble-click en entrega\ (publish self-contained + datos + LEEME); --no-package lo salta
    if (!HasFlag(rest, "--no-package"))
    {
        int rcPkg = DoPackage(game, keys, 15000, noSmoke: false, resumen);
        if (rcPkg != 0) resumen.Add("package: FALLO (export intacto en export\\" + gameName + ")");
    }

    Console.WriteLine();
    Console.WriteLine($"== PORT RESUMEN: {gameName} ==");
    foreach (var l in resumen) Console.WriteLine($"  {l}");
    return 0;
}

static int PortFail(List<string> resumen, string etapa)
{
    resumen.Add($"{etapa}: FALLO");
    Console.WriteLine();
    Console.WriteLine("== PORT RESUMEN (fallo) ==");
    foreach (var l in resumen) Console.WriteLine($"  {l}");
    return 1;
}

// Copia recursiva de los datos del juego al export. No sobrescribe: lo que el
// export acaba de generar (host, GameCode, Core/) tiene prioridad.
static int CopyGameDataTree(string srcDir, string dstDir)
{
    int n = 0;
    foreach (string src in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
    {
        string rel = Path.GetRelativePath(srcDir, src);
        string dst = Path.Combine(dstDir, rel);
        if (File.Exists(dst)) continue;
        // Un fichero que no se pueda copiar NO puede tumbar el porteo entero. Paso
        // con `nul` — un nombre de DISPOSITIVO reservado de Windows que aparece en la carpeta
        // del juego en cuanto algo redirige a `>nul` desde un shell que no es cmd (Git Bash
        // crea el fichero literal). `Directory.GetFiles` lo lista, y al copiarlo revienta con
        // ArgumentException y se lleva por delante el `port` de un juego de prueba con 19 de
        // sus 40 ficheros ya copiados. Se avisa y se sigue: es basura, no datos del juego.
        try
        {
            string? dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dst);
            n++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [datos] omitido {rel}: {ex.GetType().Name} — {ex.Message}");
        }
    }
    return n;
}

static int CmdPackage(string[] rest)
{
    if (rest.Length < 1)
    {
        Console.WriteLine("uso: pakuport package <game.json|id> [--keys <autokeys>] [--smoke-ms <n>] [--no-smoke]");
        return 1;
    }
    string arg = rest[0];
    string gamePath = File.Exists(arg) ? Path.GetFullPath(arg) : Path.Combine("games", arg, "game.json");
    if (!File.Exists(gamePath))
    {
        Console.Error.WriteLine($"no encuentro game.json: {gamePath}");
        return 1;
    }
    var game = GameConfig.Load(gamePath);
    string? keys = GetOpt(rest, "--keys");
    int smokeMs = int.Parse(GetOpt(rest, "--smoke-ms") ?? "15000");
    bool noSmoke = HasFlag(rest, "--no-smoke");

    var resumen = new List<string>();
    int rc = DoPackage(game, keys, smokeMs, noSmoke, resumen);
    Console.WriteLine();
    Console.WriteLine($"== PACKAGE RESUMEN: {game.Id} ==");
    foreach (var l in resumen) Console.WriteLine($"  {l}");
    return rc;
}

// Empaqueta export\<id> (ya exportado y compilable) en el entregable doble-clic
// entrega\<carpeta>\: publish self-contained win-x64 + datos del juego + logs\ vacío + LEEME.txt,
// smoke del exe publicado y respaldo de la entrega previa como _preN.
static int DoPackage(GameConfig game, string? keys, int smokeMs, bool noSmoke, List<string> resumen)
{
    string exportDir = Path.GetFullPath(Path.Combine("export", game.Id));
    if (!Directory.Exists(exportDir) || Directory.GetFiles(exportDir, "*.csproj").Length == 0)
    {
        resumen.Add($"package: FALLO — falta export\\{game.Id} compilable (corre 'port' o 'export' antes)");
        return 1;
    }

    // Nombre del exe (AssemblyName) y de la carpeta de entrega (knobs opcionales de game.json).
    string packageName = string.IsNullOrEmpty(game.PackageName)
        ? "Jugar" + char.ToUpperInvariant(game.Id[0]) + game.Id[1..]
        : game.PackageName;
    string folderName = string.IsNullOrEmpty(game.PackageDir)
        ? (packageName.StartsWith("Jugar", StringComparison.Ordinal) ? packageName[5..] : packageName) + "-PakuPort"
        : game.PackageDir;
    string exeName = packageName + ".exe";

    // a0. publishFlow="solo-nucleo": el export NO se regenera (su Program.cs está validado a
    //     mano), pero sí tiene que recibir los cambios de núcleo, o se queda con una copia vieja
    //     de Core/ y la entrega deja de llevar los arreglos. Refrescar + compilar aquí es lo que
    //     convierte su "camino propio" documentado en un paso más de la republicación normal.
    if (game.PublishFlow == "solo-nucleo")
    {
        CopyCoreToExport(exportDir);
        var (bRc, bOut) = RunCapture("dotnet", "build -c Release", exportDir, 600_000);
        if (bRc != 0)
        {
            foreach (var l in bOut.TakeLast(15)) resumen.Add($"    {l}");
            resumen.Add("package: FALLO — build tras refrescar Core (publishFlow=solo-nucleo)");
            return 1;
        }
        resumen.Add("nucleo: Core/ refrescado desde src/ y export recompilado (sin regenerar)");
    }

    // a. publish self-contained a un staging DENTRO del export (los dirs publish* quedan
    //    excluidos de la copia de datos).
    string staging = Path.Combine(exportDir, "publish-pkg");
    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
    var (pubRc, pubOut) = RunCapture("dotnet",
        $"publish -c Release -r win-x64 --self-contained true -p:AssemblyName={packageName} -o \"{staging}\"",
        exportDir, 600_000);
    if (pubRc != 0 || !File.Exists(Path.Combine(staging, exeName)))
    {
        foreach (var l in pubOut.TakeLast(15)) resumen.Add($"    {l}");
        resumen.Add("package: FALLO — dotnet publish");
        return 1;
    }

    // b. datos del juego (ficheros de la raíz del export, sin artefactos de build/diagnóstico);
    //    el output de publish manda si hay colisión de nombre.
    int dataCopied = 0;
    foreach (string src in Directory.GetFiles(exportDir))
    {
        string name = Path.GetFileName(src);
        if (IsPackageArtifact(name)) continue;
        // Reserved DOS device names (nul, con, ...) exist as stray files when a bash
        // `> nul` redirect ran in the game dir; System.IO cannot copy them (\\.\nul).
        if (name.ToLowerInvariant() is "nul" or "con" or "prn" or "aux") continue;
        string dst = Path.Combine(staging, name);
        if (File.Exists(dst)) continue;
        File.Copy(src, dst);
        dataCopied++;
    }
    // b1. subdirectorios de DATOS del juego (un juego de prueba guarda casi todo en subcarpetas;
    // sin esto la entrega pedía "insert disk 1" al no encontrar las misiones). Se excluyen
    // los artefactos del port por nombre.
    foreach (string dir in Directory.GetDirectories(exportDir))
    {
        string dname = Path.GetFileName(dir);
        if (dname.ToLowerInvariant() is "bin" or "obj" or "logs" or "core" or "explorer"
            or "publish-pkg" or ".claude") continue;
        foreach (string src in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(exportDir, src);
            if (IsPackageArtifact(Path.GetFileName(src))) continue;
            string dst = Path.Combine(staging, rel);
            if (File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst);
            dataCopied++;
        }
    }
    // b2. red de seguridad: ficheros del directorio del juego ORIGINAL ausentes en el
    // export (exports históricos anteriores a CopyGameDataTree perdieron ficheros
    // de datos). Se completan aquí para que la entrega lleve el set
    // íntegro. El dir fuente puede ser un repo de trabajo: se excluyen artefactos de
    // desarrollo por extensión y cualquier fichero > 4 MB (los datos DOS son pequeños).
    string srcGameDir = Path.GetDirectoryName(Path.GetFullPath(game.ExePath)) ?? "";
    int dataHealed = 0;
    if (Directory.Exists(srcGameDir))
    {
        foreach (string src in Directory.GetFiles(srcGameDir))
        {
            string name = Path.GetFileName(src);
            if (name.ToLowerInvariant() is "nul" or "con" or "prn" or "aux") continue;
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is ".py" or ".md" or ".ppm" or ".conf" or ".json" or ".txt" or ".sln"
                or ".csproj" or ".cs" or ".log" or ".png" or ".gitignore") continue;
            if (name.StartsWith('.')) continue;
            if (new FileInfo(src).Length > 4 * 1024 * 1024) continue;
            string dst = Path.Combine(staging, name);
            if (File.Exists(dst)) continue;
            File.Copy(src, dst);
            dataHealed++;
        }
        if (dataHealed > 0) resumen.Add($"datos: {dataHealed} ficheros del juego original ausentes en el export, añadidos");
    }
    Directory.CreateDirectory(Path.Combine(staging, "logs")); // logs\ vacío

    // LEEME.txt: reusa el de la entrega previa si existe; si no, uno mínimo.
    string target = Path.Combine("entrega", folderName);
    string leemeSrc = Path.Combine(target, "LEEME.txt");
    if (File.Exists(leemeSrc)) File.Copy(leemeSrc, Path.Combine(staging, "LEEME.txt"), overwrite: true);
    else File.WriteAllText(Path.Combine(staging, "LEEME.txt"),
        $"{(string.IsNullOrEmpty(game.Title) ? game.Id : game.Title)}\r\n\r\n" +
        $"Doble clic en {exeName} para jugar.\r\n" +
        "Para salir: F12 o cerrar la ventana (ESC es una tecla del juego).\r\n" +
        "F11 = pantalla completa. Click dentro = capturar el raton (no se sale de la\r\n" +
        "ventana); Ctrl+F10 = soltarlo (tambien se suelta solo al cambiar de ventana).\r\n" +
        "Ctrl+F1 / Ctrl+F2 = mas lento / mas rapido (Ctrl+F3 vuelve al valor de fabrica).\r\n" +
        "  Muchos juegos de los 80 no llevan reloj y corren a la velocidad de la CPU:\r\n" +
        "  si va disparado o va sobrado de lento, ajustalo aqui mismo mientras juegas.\r\n" +
        "  El numero que sale en pantalla es el mismo que la opcion 'cycles' de DOSBox.\r\n" +
        "El juego solo ve esta carpeta y sus subcarpetas (es su unidad C:).\r\n" +
        "Para que vea otra carpeta del PC: pulsa D en la primera pantalla y eligela; sera\r\n" +
        "  la unidad D:. Supr quita la mas reciente. Queda apuntado en DISCOS.txt (D=C:\\ruta).\r\n" +
        "Los registros de la partida quedan en la carpeta logs\\.\r\n" +
        "Si algo falla, guarda el contenido de logs\\. para diagnosticarlo.\r\n");

    // c. smoke del exe publicado: exige [autoexit] con error=ninguno y pixeles>0.
    if (!noSmoke)
    {
        var env = new Dictionary<string, string> { ["PAKU_NOSPLASH"] = "1", ["PAKU_AUTOEXIT_MS"] = smokeMs.ToString() };
        if (keys != null) env["PAKU_AUTOKEYS"] = keys;
        var (smRc, smOut) = RunCapture(Path.Combine(staging, exeName), "", staging, smokeMs + 120_000, env);
        string? metric = smOut.LastOrDefault(l => l.Contains("[autoexit]"));
        bool ok = metric != null && metric.Contains("error=ninguno") && SmokePixels(metric) > 0;
        // la entrega sale con logs\ vacío y sin screenshot: borra los rastros del smoke.
        try { File.Delete(Path.Combine(staging, "screenshot.png")); } catch { }
        string smLogs = Path.Combine(staging, "logs");
        if (Directory.Exists(smLogs)) foreach (var f in Directory.GetFiles(smLogs)) { try { File.Delete(f); } catch { } }
        resumen.Add(metric != null ? $"smoke publicado: {metric.Trim()}" : $"smoke publicado: SIN MÉTRICA (rc={smRc})");
        if (!ok)
        {
            if (metric == null) foreach (var l in smOut.TakeLast(8)) resumen.Add($"    {l}");
            resumen.Add($"package: FALLO — smoke del exe publicado (staging conservado en {staging})");
            return 1;
        }
    }

    // d. swap con respaldo. Windows es case-insensitive con nombres de carpeta: localizo la
    //    entrega previa por OrdinalIgnoreCase y la renombro a _preN ANTES de mover el staging al
    //    nombre canónico, para que Move no anide dentro de la carpeta vieja.
    Directory.CreateDirectory("entrega");
    string? existing = Directory.GetDirectories("entrega")
        .FirstOrDefault(d => string.Equals(Path.GetFileName(d), folderName, StringComparison.OrdinalIgnoreCase));
    if (existing != null)
    {
        int n = 1;
        while (Directory.Exists($"{target}_pre{n}")) n++;
        if (MoveDirRetry(existing, $"{target}_pre{n}"))
        {
            resumen.Add($"entrega previa respaldada: {Path.GetFileName(existing)} -> {folderName}_pre{n}");
            MoveDirRetry(staging, target, obligatorio: true);
        }
        else
        {
            // La carpeta de la entrega no se deja renombrar, pero SÍ se puede escribir dentro:
            // publicar in situ (respaldo por copia + sustitución del contenido). Ver MoveDirRetry.
            CopyDirTree(existing, $"{target}_pre{n}");
            resumen.Add($"entrega previa respaldada POR COPIA: {Path.GetFileName(existing)} -> {folderName}_pre{n} (la carpeta está retenida por otro proceso)");
            ReplaceDirContents(staging, existing);
            try { Directory.Delete(staging, true); } catch { }
        }
    }
    else MoveDirRetry(staging, target, obligatorio: true);

    int nFiles = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length;
    resumen.Add($"package: OK -> entrega\\{folderName}\\{exeName} ({nFiles} ficheros, {dataCopied} de datos)");
    return 0;
}

// Renombrar un directorio en Windows falla con "Access is denied" mientras alguien tenga un
// handle sobre él, y eso pasa de forma TRANSITORIA justo después de escribir dentro: el
// antivirus (o el indexador) lo abre para escanearlo. Es exactamente el momento en el que
// publicamos, porque el smoke acaba de correr ahí. Sin reintento, el juego se queda sin
// republicar aunque no haya nada roto — pasó con un juego de prueba, y más de una vez.
// Reintentar cubre también el caso de tener la carpeta de la entrega abierta en el Explorador,
// que es justo lo que hace el usuario para lanzar su Jugar<Juego>.exe.
// Devuelve false si no lo consigue (salvo `obligatorio`, que relanza). Hay dos casos distintos:
// el TRANSITORIO se cura reintentando; el PERSISTENTE es que un proceso tenga esa carpeta como
// directorio de trabajo (o abierta en el Explorador, que es justo lo que hace el usuario para
// lanzar su Jugar<Juego>.exe). En el persistente se puede escribir DENTRO y renombrar sus
// subdirectorios, pero la carpeta en sí no se mueve nunca: medido con un juego de prueba, donde
// dejó el juego sin republicar aunque no había nada roto. Por eso el que llama tiene un plan B.
static bool MoveDirRetry(string origen, string destino, bool obligatorio = false)
{
    const int intentos = 15;              // ~6 s, de sobra para un escaneo del antivirus
    for (int i = 1; ; i++)
    {
        try { Directory.Move(origen, destino); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (i >= intentos) { if (obligatorio) throw; return false; }
            Thread.Sleep(400);
        }
    }
}

static void CopyDirTree(string origen, string destino)
{
    Directory.CreateDirectory(destino);
    foreach (var f in Directory.GetFiles(origen))
        File.Copy(f, Path.Combine(destino, Path.GetFileName(f)), true);
    foreach (var d in Directory.GetDirectories(origen))
        CopyDirTree(d, Path.Combine(destino, Path.GetFileName(d)));
}

// Deja `destino` con exactamente el contenido de `origen`, SIN renombrar `destino` (ver
// MoveDirRetry). Lo que no se pueda borrar se sobrescribe igualmente, así que la entrega queda
// publicada aunque el usuario tenga la carpeta abierta.
static void ReplaceDirContents(string origen, string destino)
{
    foreach (var f in Directory.GetFiles(destino)) { try { File.Delete(f); } catch { } }
    foreach (var d in Directory.GetDirectories(destino)) { try { Directory.Delete(d, true); } catch { } }
    CopyDirTree(origen, destino);
}

// Artefactos de build/diagnóstico excluidos de la copia de datos (misma regla que las entregas
// hechas a mano): fuentes, proyecto, README, capturas, wav y logs de diagnóstico.
static bool IsPackageArtifact(string name)
{
    string n = name.ToLowerInvariant();
    if (n.EndsWith(".cs") || n.EndsWith(".csproj") || n.EndsWith(".png") || n.EndsWith(".wav")) return true;
    if (n == "readme.md") return true;
    if (n.EndsWith("_err.txt")) return true;
    if (n.StartsWith("run") || n.StartsWith("gaps_run") || n.StartsWith("pantalla-") || n.StartsWith("opl_")) return true;
    return false;
}

static int SmokePixels(string autoexitLine)
{
    var m = Regex.Match(autoexitLine, @"pixeles_no_negros=(\d+)");
    return m.Success ? int.Parse(m.Groups[1].Value) : 0;
}

static (int rc, List<string> output) RunCapture(string cmd, string cmdArgs, string workDir, int timeoutMs, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(cmd, cmdArgs)
    {
        WorkingDirectory = workDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
    var lines = new List<string>();
    using var p = new Process { StartInfo = psi };
    p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
    p.Start();
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    if (!p.WaitForExit(timeoutMs))
    {
        try { p.Kill(entireProcessTree: true); } catch { }
        lines.Add($"[port] TIMEOUT tras {timeoutMs} ms — proceso matado");
        return (-1, lines);
    }
    p.WaitForExit(); // drena los streams async
    return (p.ExitCode, lines);
}

static int CmdInit(string[] args)
{
    string? exePath = args.Length > 1 ? args[1] : null;
    if (exePath == null || !File.Exists(exePath))
    {
        Console.Error.WriteLine("uso: pakuport init <GAME.EXE> [--id <nombre>]");
        Console.Error.WriteLine("  Crea game.json y codemap.json (analisis estatico) para un juego DOS.");
        return 1;
    }

    exePath = Path.GetFullPath(exePath);
    byte[] exeBytes = File.ReadAllBytes(exePath);
    bool isCom = !(exeBytes.Length >= 28 && exeBytes[0] == 'M' && exeBytes[1] == 'Z');
    string ext = Path.GetExtension(exePath).ToLowerInvariant();
    if (ext == ".com") isCom = true;

    // --id: el mismo de `port`. El nombre del EXE NO identifica a un juego (dos juegos
    // distintos pueden traer el mismo nombre de EXE), y sin propagarlo aqui `init` escribia
    // games\<nombre-del-exe>\game.json mientras `port` buscaba games\<id>\game.json y moria con
    // un DirectoryNotFoundException a medio porteo. Solo se notaba cuando el id NO coincidia con
    // el nombre del fichero Y el game.json no existia de antes.
    string gameName = (GetOpt(args, "--id") ?? Path.GetFileNameWithoutExtension(exePath)).ToLowerInvariant();
    string gameDir = Path.Combine("games", gameName);
    Directory.CreateDirectory(gameDir);

    Console.WriteLine($"== PakuPort init: {Path.GetFileName(exePath)} ({(isCom ? "COM" : "MZ EXE")}) ==");

    // 1. Cargar binario
    var mem = new Memory();
    // dosMap=real: derive the DOS-faithful load segment by simulating the real-mode boot
    // map (same MCB chain + first-fit EXEC semantics the host uses at dosMap:"real").
    // The lifted code is relocated against loadSegment, so it must match what the host
    // derives from BootRealDos: COM -> loadSegment = PSP (image at PSP:0100);
    // MZ EXE -> loadSegment = PSP + 0x10 (module loaded just past the PSP paragraph).
    var bootDosMap = PakuPort.Core.Platform.Int21hHandler.BootRealDos(new Memory(), Path.GetFileName(exePath), exeBytes);
    ushort loadSeg = isCom ? bootDosMap.Psp : (ushort)(bootDosMap.Psp + 0x10);
    Console.WriteLine($"  dosMap=real: PSP simulado 0x{bootDosMap.Psp:X4} -> loadSegment 0x{loadSeg:X4}");
    MzLoader mz;
    byte[] imageBytes; // bytes para ExeImage (con padding para alinear a ResidentBase)
    if (isCom)
    {
        mz = MzLoader.LoadCom(exeBytes, mem, loadSeg);
        Console.WriteLine($"  COM: {exeBytes.Length} bytes, cargado en {loadSeg:X4}:0100");
        // Para COM: prependo 0x300 bytes (0x200 ResidentBase + 0x100 PSP) para que
        // key = ResidentBase + seg_offset funcione. Entry en seg:0100 → key = 0x300.
        imageBytes = new byte[ExeImage.ResidentBase + 0x100 + exeBytes.Length];
        Array.Copy(exeBytes, 0, imageBytes, ExeImage.ResidentBase + 0x100, exeBytes.Length);
    }
    else
    {
        mz = MzLoader.Load(exeBytes, mem, loadSeg);
        Console.WriteLine($"  MZ header: image={mz.ImageSize} bytes, {mz.RelocCount} relocaciones");
        imageBytes = ExeImage.NormalizeMzImage(exeBytes, loadSeg);
        PakuPort.Core.ExePackDecompressor.PatchResidentThunks(imageBytes);
    }
    Console.WriteLine($"  Entry CS:IP = {mz.InitCS:X4}:{mz.InitIP:X4}");
    Console.WriteLine($"  SS:SP = {mz.InitSS:X4}:{mz.InitSP:X4}");

    // 2. Heuristica para DS: buscar MOV AX,seg / MOV DS,AX en el entry point (solo MZ)
    uint dsBase = 0;
    int entryFile = isCom ? 0 : mz.HeaderSize + mz.InitCS * 16 + mz.InitIP;
    if (!isCom && entryFile + 10 < exeBytes.Length)
    {
        for (int i = entryFile; i < Math.Min(entryFile + 200, exeBytes.Length - 5); i++)
        {
            if (exeBytes[i] == 0xB8 && exeBytes[i + 3] == 0x8E && exeBytes[i + 4] == 0xD8)
            {
                ushort dsSeg = (ushort)(exeBytes[i + 1] | (exeBytes[i + 2] << 8));
                dsBase = (uint)(dsSeg * 16);
                Console.WriteLine($"  DS heuristico: 0x{dsSeg:X4} (base 0x{dsBase:X5})");
                break;
            }
        }
    }

    // 3. Generar CodeMap estatico
    var codeMap = new HashSet<int>();
    var exe = new ExeImage(imageBytes);
    exe.LoadSegment = isCom ? (ushort)0 : loadSeg;
    int imageEnd = isCom
        ? ExeImage.ResidentBase + 0x100 + exeBytes.Length
        : Math.Min(mz.ImageSize + ExeImage.ResidentBase, imageBytes.Length);

    if (isCom)
    {
        // COM: marcar todo como codigo (no tenemos forma de distinguir sin emulacion)
        for (int off = ExeImage.ResidentBase + 0x100; off < imageEnd; off++)
            codeMap.Add(off);
    }
    else
    {
        // 3a. Buscar prologos: push bp; mov bp,sp (55 8B EC) y sub sp,N
        for (int off = ExeImage.ResidentBase; off < imageEnd - 3; off++)
        {
            if (imageBytes[off] == 0x55 && imageBytes[off + 1] == 0x8B && imageBytes[off + 2] == 0xEC)
                codeMap.Add(off);
        }

        // 3b. Buscar targets de call near (E8 rel16)
        for (int off = ExeImage.ResidentBase; off < imageEnd - 3; off++)
        {
            if (imageBytes[off] == 0xE8)
            {
                short rel = (short)(imageBytes[off + 1] | (imageBytes[off + 2] << 8));
                int target = off + 3 + rel;
                if (target >= ExeImage.ResidentBase && target < imageEnd)
                    codeMap.Add(target);
            }
        }
    }

    // 3c. Añadir entry point
    int entryKey = ExeImage.ResidentBase + mz.InitCS * 16 + mz.InitIP;
    if (entryKey >= ExeImage.ResidentBase && entryKey < imageEnd)
        codeMap.Add(entryKey);

    // 3d. COM bootstrap: detectar patron "mov ax,cs; add ax,IMM; push ax; xor ax,ax; push ax; ...; retf"
    // que calcula un segmento destino y salta ahi via retf (far jump disfrazado)
    string? comBootstrapTarget = null;
    if (isCom && entryKey + 12 < imageEnd)
    {
        int ep = entryKey;
        // 8C C8 = mov ax,cs; 05 xx xx = add ax,imm16; 50 = push ax; 31 C0 = xor ax,ax; 50 = push ax
        if (imageBytes[ep] == 0x8C && imageBytes[ep+1] == 0xC8 &&
            imageBytes[ep+2] == 0x05)
        {
            ushort segDelta = (ushort)(imageBytes[ep+3] | (imageBytes[ep+4] << 8));
            int realEntry = segDelta * 16;
            int realKey = ExeImage.ResidentBase + realEntry;
            if (realKey >= ExeImage.ResidentBase && realKey < imageEnd)
            {
                comBootstrapTarget = $"0x{realKey - ExeImage.ResidentBase:X4}";
                Console.WriteLine($"  COM bootstrap: retf a CS+0x{segDelta:X4} = offset 0x{realEntry:X4}");
                codeMap.Add(realKey);
            }
        }
    }

    Console.WriteLine($"  CodeMap estatico: {codeMap.Count} offsets (prologos + call targets)");
    Console.WriteLine($"  (para mejor cobertura, usa DOSBox trace: pakuport trace-dosbox)");

    // 4. Detectar overlays VROOMM: buscar INT 3F (CD 3F) seguido de ovno + offset
    exe.CodeMap = codeMap;
    var overlays = new OverlayDirectory();
    var overlayStubs = new Dictionary<int, int>(); // ovno -> file offset
    string? vroommStubStr = null;
    string? entryOverride = null;

    // 4a. Buscar INT 3F (CD 3F ovno off16) en zona residente (solo MZ)
    var int3fTargets = new Dictionary<int, HashSet<int>>(); // ovno -> set of offsets
    for (int off = ExeImage.ResidentBase; off < imageEnd - 5; off++)
    {
        if (imageBytes[off] == 0xCD && imageBytes[off + 1] == 0x3F)
        {
            int ovno = imageBytes[off + 2];
            int ovOff = imageBytes[off + 3] | (imageBytes[off + 4] << 8);
            if (!int3fTargets.ContainsKey(ovno)) int3fTargets[ovno] = new HashSet<int>();
            int3fTargets[ovno].Add(ovOff);
        }
    }

    if (int3fTargets.Count > 0)
    {
        Console.WriteLine($"  VROOMM detectado: {int3fTargets.Count} overlays referenciados via INT 3F");
        // Buscar Borland overlay header en el EXE: signature "FB" seguida de tabla de overlays
        // Heuristica: buscar offsets de codigo de overlays tras el codigo residente
        // Los overlays empiezan tras el residente; cada uno tiene un header de 6 bytes (FB xx xx size)
        // Buscamos bloques de codigo despues de residentCodeEnd con prologos

        // Determinar residentCodeEnd: ultimo offset del codemap antes del primer gap grande
        var sortedCode = codeMap.OrderBy(x => x).ToList();
        int resCodeEnd = imageEnd;
        for (int i = 1; i < sortedCode.Count; i++)
        {
            if (sortedCode[i] - sortedCode[i - 1] > 0x800 && sortedCode[i - 1] > ExeImage.ResidentBase + 0x1000)
            {
                resCodeEnd = sortedCode[i - 1] + 0x10;
                break;
            }
        }

        // Buscar bloques de overlay: buscar prologos (55 8B EC) en zonas >resCodeEnd
        // y agrupar por proximidad (gap > 0x200 = nuevo overlay)
        var ovPrologues = new List<int>();
        for (int off = resCodeEnd; off < imageBytes.Length - 3; off++)
        {
            if (imageBytes[off] == 0x55 && imageBytes[off + 1] == 0x8B && imageBytes[off + 2] == 0xEC)
                ovPrologues.Add(off);
        }

        if (ovPrologues.Count > 0)
        {
            // Agrupar prologos por bloques (gap > 0x400 = nuevo overlay)
            var blocks = new List<(int start, int end)>();
            int blockStart = ovPrologues[0];
            int blockEnd = ovPrologues[0];
            for (int i = 1; i < ovPrologues.Count; i++)
            {
                if (ovPrologues[i] - blockEnd > 0x400)
                {
                    blocks.Add((blockStart, blockEnd));
                    blockStart = ovPrologues[i];
                }
                blockEnd = ovPrologues[i];
            }
            blocks.Add((blockStart, blockEnd));

            // Asignar bloques a overlay numbers (orden)
            var ovNos = int3fTargets.Keys.OrderBy(x => x).ToList();
            for (int i = 0; i < Math.Min(blocks.Count, ovNos.Count); i++)
            {
                // Alinear a pagina de 0x200 (sector)
                int alignedStart = (blocks[i].start / 0x200) * 0x200;
                overlays.Add(ovNos[i], alignedStart);
            }

            if (overlays.Count > 0)
                Console.WriteLine($"  Overlays mapeados: {overlays.Count} (heuristico por prologos)");
        }
    }

    // 4b. Detectar VROOMM entry stub: call far seg:off mas frecuente en overlays
    if (overlays.Count > 0)
    {
        var farCallFreq = new Dictionary<(ushort seg, ushort off), int>();
        for (int off = ExeImage.ResidentBase; off < imageBytes.Length - 5; off++)
        {
            if (imageBytes[off] == 0x9A) // call far ptr
            {
                ushort fOff = (ushort)(imageBytes[off + 1] | (imageBytes[off + 2] << 8));
                ushort fSeg = (ushort)(imageBytes[off + 3] | (imageBytes[off + 4] << 8));
                if (fSeg != 0)
                {
                    var k = (fSeg, fOff);
                    farCallFreq[k] = farCallFreq.GetValueOrDefault(k) + 1;
                }
            }
        }
        if (farCallFreq.Count > 0)
        {
            var top = farCallFreq.OrderByDescending(kv => kv.Value).First();
            if (top.Value > 10) // mas de 10 calls al mismo destino far = probablemente VROOMM stub
            {
                vroommStubStr = $"0x{top.Key.seg:X4}:0x{top.Key.off:X4}";
                Console.WriteLine($"  VROOMM entry stub: {vroommStubStr} ({top.Value} refs)");
            }
        }
    }

    // 4c. Detectar entry override: si el entry point tiene un VROOMM bootstrap,
    // buscar el _main real (call near al inicio que salta al codigo del juego)
    if (int3fTargets.Count > 0)
    {
        // El entry point de un juego VROOMM suele tener: setup VROOMM, luego call near al _main.
        // Buscar el primer call near (E8) tras el entry que apunte fuera del bootstrap
        int ep = ExeImage.ResidentBase + mz.InitCS * 16 + mz.InitIP;
        for (int off = ep; off < Math.Min(ep + 500, imageEnd - 3); off++)
        {
            if (imageBytes[off] == 0xE8)
            {
                short rel = (short)(imageBytes[off + 1] | (imageBytes[off + 2] << 8));
                int target = off + 3 + rel;
                if (target > ep + 100 && target < imageEnd - 3 &&
                    imageBytes[target] == 0x55 && imageBytes[target + 1] == 0x8B && imageBytes[target + 2] == 0xEC)
                {
                    int ghidraOff = target - ExeImage.ResidentBase;
                    entryOverride = $"0x{ghidraOff:X4}";
                    Console.WriteLine($"  Entry override (main detectado): {entryOverride}");
                    break;
                }
            }
        }
    }

    // 5. Descubrir funciones con DiscoverAll
    if (comBootstrapTarget != null && entryOverride == null)
        entryOverride = comBootstrapTarget;
    var seeds = new List<int>();
    if (entryOverride != null)
    {
        string eo = entryOverride;
        if (eo.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) eo = eo[2..];
        seeds.Add(ExeImage.ResidentBase + Convert.ToInt32(eo, 16));
    }
    seeds.Add(ExeImage.ResidentBase + mz.InitCS * 16 + mz.InitIP);
    var funcs = Emitter.DiscoverAll(exe, overlays, seeds, comMode: isCom);
    Console.WriteLine($"  Funciones descubiertas: {funcs.Count}");

    // 6. Escribir game.json
    var jsonParts = new List<string>
    {
        $"  \"id\": \"{gameName}\"",
        $"  \"title\": \"{Path.GetFileNameWithoutExtension(exePath)}\"",
        $"  \"exePath\": \"{exePath.Replace("\\", "/")}\"",
        $"  \"corpusDir\": \"\"",
        $"  \"format\": \"{(isCom ? "com" : "exe")}\"",
        // New game.json are born on the interpreter engine (interpreter-first doctrine; the first
        // ports got this by hand, `port` never did): without it the double-click build boots the LIFTED
        // world, whose entry can be an empty stub (one test game: 0 insns, instant "clean exit").
        $"  \"interpDefault\": true",
        // New game.json are born with the faithful DOS memory map; loadSegment above is the
        // simulated PSP (COM) / PSP+0x10 (EXE) so the host's dosMap=real world matches.
        $"  \"dosMap\": \"real\"",
        $"  \"loadSegment\": \"0x{loadSeg:X4}\"",
        $"  \"dsBase\": \"0x{dsBase:X5}\"",
        // New game.json are born with the default SoundBlaster hardware (the DOSBox oracle's
        // default: base 0x220, IRQ 7, DMA 1). Enables digital SB audio (DSP + DMA single-cycle
        // 8-bit) for games that detect/use it; games without SB simply never touch these ports.
        // Existing game.json are respected (CmdPort skips init if game.json exists).
        "  \"sb\": { \"base\": \"0x220\", \"irq\": 7, \"dma\": 1 }"
    };
    if (entryOverride != null)
        jsonParts.Add($"  \"entryOverride\": \"{entryOverride}\"");
    if (vroommStubStr != null)
        jsonParts.Add($"  \"vroommEntryStub\": \"{vroommStubStr}\"");
    if (overlays.Count > 0)
    {
        int resEnd = codeMap.Where(x => x < (overlays.Count > 0 ? overlays.BaseOf(overlays.OverlayNumbers().First()) : imageEnd))
                            .DefaultIfEmpty(imageEnd).Max() + 0x10;
        jsonParts.Add($"  \"residentCodeEnd\": \"0x{resEnd:X5}\"");
        var ovJson = new System.Text.StringBuilder("  \"overlayCodeStart\": {\n");
        bool first = true;
        foreach (int ovno in overlays.OverlayNumbers().OrderBy(x => x))
        {
            if (!first) ovJson.Append(",\n");
            ovJson.Append($"    \"0x{ovno:X02}\": \"0x{overlays.CodeStartOf(ovno):X5}\"");
            first = false;
        }
        ovJson.Append("\n  }");
        jsonParts.Add(ovJson.ToString());
    }
    string gameJson = "{\n" + string.Join(",\n", jsonParts) + "\n}\n";
    string gameJsonPath = Path.Combine(gameDir, "game.json");
    File.WriteAllText(gameJsonPath, gameJson);
    Console.WriteLine($"  -> {gameJsonPath}");

    // 6. Escribir codemap.json
    string cmPath = Path.Combine(gameDir, "codemap.json");
    using (var fs = File.Create(cmPath))
    using (var w = new System.Text.Json.Utf8JsonWriter(fs, new System.Text.Json.JsonWriterOptions { Indented = false }))
    {
        w.WriteStartObject();
        w.WriteStartArray("code");
        foreach (int off in codeMap.OrderBy(x => x))
            w.WriteNumberValue(off);
        w.WriteEndArray();
        w.WriteEndObject();
    }
    Console.WriteLine($"  -> {cmPath}");

    Console.WriteLine();
    Console.WriteLine("Siguiente paso:");
    Console.WriteLine($"  pakuport survey --game {gameJsonPath}");
    Console.WriteLine($"  pakuport export --game {gameJsonPath}");

    return 0;
}

static int CmdTrace(GameConfig game, string func, int caseIdx = 0)
{
    var (writes, ips) = FunctionVerifier.TraceCase(game.ExePath, game.CorpusDir, func, game.Overlays, game.CodeMap, caseIdx,
        loadBase: game.LoadSegment * 16, loadSegment: game.LoadSegment);
    // formato parseable: sección IPS (camino) y sección WRITES (escrituras DGROUP), orden de ejecución.
    Console.WriteLine("#IPS");
    foreach (var ip in ips) Console.WriteLine($"{ip:X4}");
    Console.WriteLine("#WRITES");
    foreach (var (off, v) in writes) Console.WriteLine($"{off:X4} {v:X2}");
    return 0;
}

static int CmdRender(GameConfig game, string func, string caseIdxStr)
{
    int caseIdx = int.Parse(caseIdxStr);
    int off = FunctionVerifier.ParseOffset(func);
    int rootKey = OverlayDirectory.ResidentBase + off;
    var exe = ExeImage.Load(game.ExePath, game.LoadSegment);
    // No CodeMap for render — we need full code coverage, not just exercised paths
    exe.CodeMap = null;

    List<DecodedFunction> funcs;
    EmitResult em;
    funcs = Emitter.DiscoverCallTree(exe, rootKey, game.Overlays, followFar: false);
    em = Emitter.EmitProgram(funcs, rootKey, softStubs: true);

    var del = FunctionVerifier.Compile(em.Source, em.Namespace, em.ClassName, em.MethodName);
    string corpusFuncName = func.StartsWith("FUN_") ? func : $"FUN_1000_{func}";
    var cases = FunctionVerifier.LoadCorpus(game.CorpusDir, corpusFuncName);
    if (cases.Count == 0) { Console.Error.WriteLine("sin casos en corpus"); return 2; }
    if (caseIdx >= cases.Count) { Console.Error.WriteLine($"caso {caseIdx} fuera de rango (hay {cases.Count})"); return 2; }

    var cas = cases[caseIdx];
    var (platform, keyboard, mouse, timer) = PlatformFactory.CreateFull(Path.GetDirectoryName(game.ExePath)!);

    var mem = new Memory { Video = platform.Video };
    int dsBase = (int)cas.DsBase;
    Array.Copy(cas.EntryMem, 0, mem.Raw, dsBase, cas.EntryMem.Length);

    var cpu = Cpu.FromState(cas.EntryCpu);
    cpu.Platform = platform;
    cpu.LoadBase = game.LoadSegment * 16;

    if (game.VideoMode != 0)
    {
        platform.Video.VideoMode = game.VideoMode;
        platform.Video.LoadDefaultVgaPalette();
        if (game.PaletteRemap != null)
            platform.Video.ApplyPaletteRemap(game.PaletteRemap);
    }

    Console.WriteLine($"== render {func} caso {caseIdx} ==");
    Console.WriteLine($"  video mode antes: 0x{platform.Video.VideoMode:X2}");

    // Force redraw flags for functions that skip when "nothing changed"
    mem.WrU16(cpu.DS, 0x8CCE, 1);
    long budgetBefore = cpu.Budget;
    try { del(cpu, mem); }
    catch (Exception ex) { Console.Error.WriteLine($"  EXCEPCIÓN: {ex.GetType().Name}: {ex.Message}"); }
    Console.WriteLine($"  Steps ejecutados: {budgetBefore - cpu.Budget}");

    Console.WriteLine($"  video mode después: 0x{platform.Video.VideoMode:X2} ({platform.Video.Width}x{platform.Video.Height})");

    // Volcar framebuffer a BMP (formato simple, sin dependencias externas)
    string gameDir = Path.GetDirectoryName(game.ExePath) ?? ".";
    string outDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameDir)!) ?? ".", "output");
    Directory.CreateDirectory(outDir);
    string bmpPath = Path.Combine(outDir, $"render_{func}_{caseIdx}.bmp");

    byte[] rgba = platform.Video.RenderFramebuffer();
    WriteBmp(bmpPath, rgba, platform.Video.Width, platform.Video.Height);
    Console.WriteLine($"  framebuffer → {bmpPath}");

    // Comprobar si hay píxeles no negros
    int nonBlack = 0;
    for (int i = 0; i < rgba.Length; i += 4)
        if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0) nonBlack++;
    Console.WriteLine($"  píxeles no negros: {nonBlack}/{platform.Video.Width * platform.Video.Height}");

    return 0;
}

static void WriteBmp(string path, byte[] rgba, int w, int h)
{
    int rowBytes = w * 3;
    int padding = (4 - rowBytes % 4) % 4;
    int rowStride = rowBytes + padding;
    int pixelDataSize = rowStride * h;
    int fileSize = 54 + pixelDataSize;

    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    // BMP header
    bw.Write((byte)'B'); bw.Write((byte)'M');
    bw.Write(fileSize);
    bw.Write(0); // reserved
    bw.Write(54); // pixel data offset
    // DIB header (BITMAPINFOHEADER)
    bw.Write(40); // header size
    bw.Write(w);
    bw.Write(h); // positive = bottom-up
    bw.Write((short)1); // planes
    bw.Write((short)24); // bpp
    bw.Write(0); // compression
    bw.Write(pixelDataSize);
    bw.Write(2835); bw.Write(2835); // ppm
    bw.Write(0); bw.Write(0); // colors
    // Pixel data (bottom-up, BGR)
    var padBytes = new byte[padding];
    for (int y = h - 1; y >= 0; y--)
    {
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            // Guard: the framebuffer may be smaller than w*h*4 if the video mode
            // changed after the buffer was captured; emit black for out-of-range.
            if (i + 2 < rgba.Length)
            {
                bw.Write(rgba[i + 2]); // B
                bw.Write(rgba[i + 1]); // G
                bw.Write(rgba[i + 0]); // R
            }
            else { bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); }
        }
        if (padding > 0) bw.Write(padBytes);
    }
}

static int CmdRun(GameConfig game, string? exeOverride, string? entryOverride, string? corpusFunc = null,
                  bool bootReal = false, string? gamePath = null, bool snapshot = false)
{
    string exePath = exeOverride ?? game.ExePath;
    byte[] exeBytes = File.ReadAllBytes(exePath);
    string gameDir = Path.GetDirectoryName(exePath) ?? ".";

    // snapshot: arrancar DENTRO del juego desde un estado capturado por el
    // explorador (tools/explore_exec.py --gameplay --snap-at): memoria 1 MiB +
    // registros en la ENTRADA de una función. Salta el startup entero.
    byte[]? snapMem = null;
    System.Text.Json.JsonElement snapRoot = default;
    int runImageBias = 0;
    if (snapshot)
    {
        string exDirS = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gamePath!))!, "explorer");
        string sPath = Path.Combine(exDirS, "gameplay_snapshot.bin");
        string rPath = Path.Combine(exDirS, "gameplay_regs.json");
        if (!File.Exists(sPath) || !File.Exists(rPath))
        {
            Console.Error.WriteLine($"  snapshot: faltan {sPath} / gameplay_regs.json — corre el explorador con --gameplay --snap-at antes.");
            return 1;
        }
        snapMem = File.ReadAllBytes(sPath);
        using var sDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rPath));
        snapRoot = sDoc.RootElement.Clone();
        string ijS = Path.Combine(exDirS, "image.json");
        if (File.Exists(ijS))
        {
            using var ijDocS = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ijS));
            if (ijDocS.RootElement.TryGetProperty("memImage", out var miS) &&
                miS.TryGetProperty("xOfGhidra0", out var xogS))
                runImageBias = HexToInt(xogS.GetString()!);
        }
        Console.WriteLine($"  snapshot: {snapMem.Length:N0} bytes, func {snapRoot.GetProperty("func").GetProperty("name").GetString()} " +
                          $"(ghidra {snapRoot.GetProperty("func").GetProperty("ghidra").GetString()}, hit {snapRoot.GetProperty("func").GetProperty("hit").GetInt32()})");
    }

    // boot-real: arrancar del startup REAL sobre la imagen de memoria descomprimida
    // (memimage.bin de `dump-image`), como el explorador — en vez del `main` C, que
    // asume el startup ya corrido y descarrila a las ~6 instrucciones. Genérico.
    byte[]? bootMemImage = null;
    int bootEntryGhidra = -1, bootSs = 0, bootSp = 0, bootCs = 0;
    if (bootReal)
    {
        string exDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gamePath!))!, "explorer");
        string memPath = Path.Combine(exDir, "memimage.bin");
        string imgJson = Path.Combine(exDir, "image.json");
        if (!File.Exists(memPath) || !File.Exists(imgJson))
        {
            Console.Error.WriteLine($"  boot-real: falta {memPath} o image.json — corre `dump-image --game {gamePath}` antes.");
            return 1;
        }
        bootMemImage = File.ReadAllBytes(memPath);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(imgJson));
        var r = doc.RootElement;
        int ls = HexToInt(r.GetProperty("loadSegment").GetString()!);
        var re = r.GetProperty("realEntry"); var rs = r.GetProperty("realStack");
        int rcs = HexToInt(re.GetProperty("cs").GetString()!), rip = HexToInt(re.GetProperty("ip").GetString()!);
        bootSs = HexToInt(rs.GetProperty("ss").GetString()!); bootSp = HexToInt(rs.GetProperty("sp").GetString()!);
        bootCs = rcs;
        if (r.TryGetProperty("memImage", out var miB) && miB.TryGetProperty("xOfGhidra0", out var xogB))
            runImageBias = HexToInt(xogB.GetString()!);
        // ghidra = linear(realEntry) - loadBase - xOfGhidra0 (la memimage coloca
        // ghidra-0 en +xOfGhidra0; sin restarlo el entry decodifica 0x129 arriba:
        // caía en mitad del loader VROOMM = el "bucle de auto-carga" observado).
        bootEntryGhidra = rcs * 16 + rip - ls * 16 - runImageBias;
        Console.WriteLine($"  boot-real: memimage {bootMemImage.Length} bytes, entry ghidra 0x{bootEntryGhidra:X4}, SS:SP {bootSs:X4}:{bootSp:X4}");
    }

    // 1. Cargar ejecutable en memoria
    bool isCom = game.Format == "com";
    var mem = new Memory();
    ushort loadSeg = game.LoadSegment > 0 ? game.LoadSegment : (ushort)0x0110;
    ushort pspSeg = (ushort)(loadSeg - 0x10);
    MzLoader? mz = null;
    if (!isCom)
    {
        mz = MzLoader.Load(exeBytes, mem, loadSeg);
        MzLoader.SetupPsp(mem, pspSeg, (ushort)(pspSeg - 0x10));
        MzLoader.SetupEnvironment(mem, (ushort)(pspSeg - 0x10), exePath);
    }
    else
    {
        int lin = loadSeg * 16;
        Array.Copy(exeBytes, 0, mem.Raw, lin, Math.Min(exeBytes.Length, 0xFFF0));
        MzLoader.SetupPsp(mem, pspSeg, (ushort)(pspSeg - 0x10));
        MzLoader.SetupEnvironment(mem, (ushort)(pspSeg - 0x10), exePath);
    }

    // boot-real: sustituir la imagen por la de MEMORIA descomprimida (X-space) en loadSeg,
    // igual que el explorador. El PSP/env ya montados sirven; el startup real hará el resto.
    if (bootMemImage != null)
    {
        int lin = loadSeg * 16;
        Array.Copy(bootMemImage, 0, mem.Raw, lin, Math.Min(bootMemImage.Length, mem.Raw.Length - lin));
    }

    Console.WriteLine($"== PakuPort run: {Path.GetFileName(exePath)} ==");
    if (!isCom)
    {
        Console.WriteLine($"  MZ header: image={mz!.ImageSize} bytes, {mz.RelocCount} relocaciones");
        Console.WriteLine($"  Load segment: 0x{loadSeg:X4}, entry CS:IP = {mz.AbsCS:X4}:{mz.AbsIP:X4}");
        Console.WriteLine($"  SS:SP = {mz.AbsSS:X4}:{mz.InitSP:X4}");
    }
    else
    {
        Console.WriteLine($"  COM format, load segment: 0x{loadSeg:X4}, entry 0x0100");
    }

    // 2. Preparar Platform
    var (platform, keyboard, mouse, timer) = PlatformFactory.CreateFull(gameDir, exePath);
    mem.Video = platform.Video;

    if (game.VideoMode != 0)
    {
        platform.Video.VideoMode = game.VideoMode;
        platform.Video.LoadDefaultVgaPalette();
        if (game.PaletteRemap != null)
            platform.Video.ApplyPaletteRemap(game.PaletteRemap);
    }

    // 3. Determinar el entry point: boot-real > CLI --entry > game.json entryOverride > MZ header
    string? effectiveEntry = entryOverride ?? game.EntryOverride;
    int entryGhidra;
    if (bootEntryGhidra >= 0)
    {
        entryGhidra = bootEntryGhidra;
        Console.WriteLine($"  boot-real entry ghidra: 0x{entryGhidra:X4}");
    }
    else if (snapMem != null && entryOverride == null)
    {
        // el snapshot manda sobre el entryOverride del game.json (ese es para el
        // arranque por main-C); --entry explícito de CLI sí lo puede pisar.
        entryGhidra = HexToInt(snapRoot.GetProperty("func").GetProperty("ghidra").GetString()!);
        Console.WriteLine($"  snapshot entry ghidra: 0x{entryGhidra:X4}");
    }
    else if (effectiveEntry != null)
    {
        string ee = effectiveEntry;
        if (ee.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) ee = ee[2..];
        entryGhidra = FunctionVerifier.ParseOffset("FUN_1000_" + ee);
        Console.WriteLine($"  Entry override: 0x{entryGhidra:X4}");
    }
    else
    {
        entryGhidra = isCom ? 0x0100 : mz!.InitCS * 16 + mz.InitIP;
    }
    int rootKey = ExeImage.ResidentBase + entryGhidra;
    Console.WriteLine($"  Entry ghidra offset: 0x{entryGhidra:X4} (file key: 0x{rootKey:X5})");

    // 4. Descubrir TODAS las funciones
    byte[] imageBytes2;
    if (isCom)
    {
        imageBytes2 = exeBytes;
    }
    else
    {
        imageBytes2 = ExeImage.NormalizeMzImage(exeBytes, loadSeg);
    }
    // Mundo memimage/snapshot: el código RESIDENTE se decodifica de ESA memoria
    // (key = memoff - xOfGhidra0 + 0x200). La imagen normalizada difiere de la
    // memimage en la región de la tabla de thunks (g < ~0x1080, corrimientos de
    // varios bytes por la descompresión EXEPACK): liftar de image.bin ahí produce
    // funciones sutilmente distintas a las que ejecutó el oráculo. Los overlays
    // (keys >= residente) siguen viniendo del fichero, como en el juez.
    if (!isCom && (snapMem != null || bootMemImage != null))
    {
        byte[] world = snapMem ?? bootMemImage!;
        int srcOff = (snapMem != null ? loadSeg * 16 : 0) + runImageBias;
        int residentLen = Math.Min(OverlayDirectory.ResidentThreshold, imageBytes2.Length) - ExeImage.ResidentBase;
        residentLen = Math.Min(residentLen, world.Length - srcOff);
        if (residentLen > 0)
        {
            Array.Copy(world, srcOff, imageBytes2, ExeImage.ResidentBase, residentLen);
            Console.WriteLine($"  imagen híbrida: residente ({residentLen:N0} bytes) del mundo {(snapMem != null ? "snapshot" : "memimage")}, overlays del fichero");
        }
    }
    // Drivers EXEC del snapshot (graficos, de sonido...): LIFTARLOS como
    // pseudo-overlays de la imagen compuesta, con las relocaciones MZ aplicadas
    // para el segmento donde el snapshot los tiene cargados. El port ejecuta el
    // código REAL del driver traducido (dibujo/sonido byte-exactos), en vez de
    // una reimplementación. FarDispatch resuelve seg->keyBase en runtime.
    var driverRegions = new List<(ushort Seg, string Name, int KeyBase, int Len)>();
    var driverSeeds = new List<int>();
    if (snapMem != null)
        imageBytes2 = BuildDriverComposite(imageBytes2, game, snapRoot, gameDir, driverRegions, driverSeeds,
            snapMem, game.LoadSegment, runImageBias);

    var exe = new ExeImage(imageBytes2);
    exe.LoadSegment = game.LoadSegment > 0 ? game.LoadSegment : (ushort)0;
    exe.CodeMap = game.CodeMap;
    if (snapMem != null || bootMemImage != null) exe.ImageBias = runImageBias;

    var seeds = CollectSeeds(game, exeBytes);
    if (!seeds.Contains(rootKey)) seeds.Insert(0, rootKey);
    foreach (int dsd in driverSeeds) if (!seeds.Contains(dsd)) seeds.Add(dsd);

    // snapshot-loop (bucle de sesión transcrito del supervisor real): la Fn que se
    // llama por iteración también tiene que estar descubierta y emitida.
    int snapEachKey = -1, snapUntilOff = -1;
    if (snapMem != null && game.SnapshotLoopEach != null)
    {
        snapEachKey = ExeImage.ResidentBase + HexToInt(game.SnapshotLoopEach);
        if (!seeds.Contains(snapEachKey)) seeds.Add(snapEachKey);
        if (game.SnapshotLoopUntil != null) snapUntilOff = HexToInt(game.SnapshotLoopUntil);
    }

    Console.WriteLine($"  Semillas: {seeds.Count} (entry + corpus)");
    if (game.VroommStubTable != null)
        Console.WriteLine($"  VroommStubTable: {game.VroommStubTable.Count} entries, OverlayLoadBase: 0x{game.OverlayLoadBase:X4}");
    if (game.ThunkRedirects != null)
        Console.WriteLine($"  ThunkRedirects: {game.ThunkRedirects.Count} entries");

    // Discovery iterativo: discover → emit → compile → run-breve → recoger misses → re-discover
    Dictionary<int, DecodedFunction> allFuncs;
    EmitResult em;
    Action<Cpu, Memory> entryDelegate;
    int imageEnd = Math.Min(exe.Length, ExeImage.ResidentBase + 0x30000);
    for (int iteration = 0; ; iteration++)
    {
        allFuncs = Emitter.DiscoverAll(exe, game.Overlays, seeds, comMode: game.Format == "com", vroommStubTable: game.VroommStubTable);
        Console.WriteLine($"  Funciones descubiertas: {allFuncs.Count}" + (iteration > 0 ? $" (iteración {iteration})" : ""));

        var funcList = allFuncs.Values.ToList();
        var entryFunc = funcList.FirstOrDefault(f => f.Key == rootKey);
        if (entryFunc != null) { funcList.Remove(entryFunc); funcList.Insert(0, entryFunc); }

        try
        {
            em = Emitter.EmitProgram(funcList, rootKey, trace: Environment.GetEnvironmentVariable("PAKU_EMIT_TRACE") == "1", vroommStub: game.VroommStub, softStubs: true, overlayDir: game.Overlays, vroommStubTable: game.VroommStubTable, overlayLoadBase: game.OverlayLoadBase, exe: exe, thunkRedirects: game.ThunkRedirects, imageBias: runImageBias);
        }
        catch (Exception ex) { Console.Error.WriteLine($"  ERROR emit: {ex.Message}"); return 2; }

        if (Environment.GetEnvironmentVariable("PAKU_DUMP_SRC") is string dumpP && dumpP.Length > 0)
            File.WriteAllText(dumpP, em.Source);

        try
        {
            entryDelegate = FunctionVerifier.Compile(em.Source, em.Namespace, em.ClassName, em.MethodName);
        }
        catch (Exception ex) { Console.Error.WriteLine($"  ERROR compilación: {ex.Message}"); return 2; }

        if (iteration >= 3) break;

        // Ejecución de prueba rápida para recoger MISSes. Con snapshot, el probe corre
        // EL MUNDO REAL (estado inyectado + bucle de sesión): los far targets dependen
        // de datos del juego (tablas de despacho de overlays) y solo aparecen ahí.
        // boot-real: el probe debe correr EXACTAMENTE como la ejecución real (CS/SS/SP del
        // realEntry, DS=PSP e ImageBias). Sin el bias, los MISSes del probe se registran
        // 0x129 bytes ARRIBA y el bucle siembra claves de bytes corridos (la función real
        // nunca se emite y el MISS persiste en la ejecución real).
        var probeCpu = new Cpu
        {
            CS = bootReal ? (ushort)bootCs : (isCom ? loadSeg : mz!.AbsCS),
            SS = bootReal ? (ushort)bootSs : (isCom ? loadSeg : mz!.AbsSS),
            SP = bootReal ? (ushort)bootSp : (isCom ? (ushort)0xFFFE : mz!.InitSP),
            Platform = platform,
            LoadBase = isCom ? 0 : loadSeg * 16,
            ImageBias = runImageBias,
            DS = bootReal ? pspSeg : (isCom ? loadSeg : (game.DsBase > 0 ? (ushort)(loadSeg + ((int)game.DsBase - 0x10000) / 16) : pspSeg)),
            ES = bootReal ? pspSeg : (isCom ? loadSeg : (game.DsBase > 0 ? (ushort)(loadSeg + ((int)game.DsBase - 0x10000) / 16) : pspSeg)),
        };
        probeCpu.FarDispatchMisses = new HashSet<int>();
        probeCpu.Budget = 5_000_000;
        if (snapMem != null)
        {
            Array.Copy(snapMem, 0, mem.Raw, 0, 0xA0000);
            platform.AttachMemory(mem);
            PakuPort.Core.Platform.DosPlatform.RestoreGameVectors(mem, snapMem);
            var pr = snapRoot.GetProperty("regs");
            ushort PR(string k) => (ushort)HexToInt(pr.GetProperty(k).GetString()!);
            probeCpu.AX = PR("ax"); probeCpu.BX = PR("bx"); probeCpu.CX = PR("cx"); probeCpu.DX = PR("dx");
            probeCpu.SI = PR("si"); probeCpu.DI = PR("di"); probeCpu.BP = PR("bp"); probeCpu.SP = PR("sp");
            probeCpu.CS = PR("cs"); probeCpu.DS = PR("ds"); probeCpu.ES = PR("es"); probeCpu.SS = PR("ss");
            probeCpu.ImageBias = runImageBias;
            foreach (var e in snapRoot.GetProperty("execOvls").EnumerateArray())
            {
                ushort pseg = (ushort)e.GetProperty("seg").GetInt32();
                string pnm = e.GetProperty("name").GetString()!;
                var prg = driverRegions.FirstOrDefault(dr => dr.Seg == pseg && dr.Name.Equals(pnm, StringComparison.OrdinalIgnoreCase));
                PakuPort.Core.Platform.DriverRegistry.RegisterDriverSegment(pseg, pnm, prg.Name != null ? prg.KeyBase : -1);
            }
            try
            {
                entryDelegate(probeCpu, mem);
                if (snapEachKey >= 0 && FunctionVerifier.GetCompiledMethod($"Fn_{snapEachKey:X5}") is { } probeEach)
                {
                    ushort pDs = probeCpu.DS;
                    while (probeCpu.Budget > 0 &&
                           (snapUntilOff < 0 || mem.RdU16(pDs, (ushort)snapUntilOff) == 0))
                    {
                        probeCpu.SP -= 2;
                        probeEach(probeCpu, mem);
                    }
                }
            }
            catch { }
        }
        else
        {
            try { entryDelegate(probeCpu, mem); } catch { }
        }

        var misses = probeCpu.FarDispatchMisses;
        int newSeeds = 0;
        foreach (int fk in misses)
        {
            if (fk < ExeImage.ResidentBase || fk >= imageEnd) continue;
            if (fk < 0) continue;
            if (allFuncs.ContainsKey(fk)) continue;
            if (!seeds.Contains(fk)) { seeds.Add(fk); newSeeds++; Console.WriteLine($"    miss->seed 0x{fk:X5}"); }
        }
        if (newSeeds == 0) break;
        Console.WriteLine($"  {newSeeds} funciones descubiertas via MISSes, re-compilando...");

        // Recargar solo la imagen del EXE (código), preservar datos del probe.
        // boot-real: restaurar la memimage (MzLoader pisaría la imagen descomprimida
        // con el MZ EXEPACK-comprimido y la ejecución real correría sobre datos basura).
        if (bootMemImage != null)
            Array.Copy(bootMemImage, 0, mem.Raw, loadSeg * 16, Math.Min(bootMemImage.Length, mem.Raw.Length - loadSeg * 16));
        else if (isCom)
            Array.Copy(exeBytes, 0, mem.Raw, loadSeg * 16, Math.Min(exeBytes.Length, 0xFFF0));
        else
            MzLoader.Load(exeBytes, mem, loadSeg);
    }

    Console.WriteLine($"  Opcodes: {em.OpcodesUsed.Count} tipos, {em.NotSupported.Count} no soportados");
    if (em.NotSupported.Count > 0)
        Console.WriteLine("  No soportados (throw en runtime si se alcanzan): " +
            string.Join("; ", em.NotSupported.Distinct().Take(10)));
    Console.WriteLine("  Compilación OK");

    // boot-real: el probe ejecutó el juego DE VERDAD y mutó la memoria (DGROUP, PSP,
    // env — el crt0 escribe sobre ellos). Restaurar el mundo inicial completo para la
    // ejecución real; sin esto el env sale corrupto y el startup aborta.
    if (bootMemImage != null)
    {
        Array.Copy(bootMemImage, 0, mem.Raw, loadSeg * 16, Math.Min(bootMemImage.Length, mem.Raw.Length - loadSeg * 16));
        MzLoader.SetupPsp(mem, pspSeg, (ushort)(pspSeg - 0x10));
        MzLoader.SetupEnvironment(mem, (ushort)(pspSeg - 0x10), exePath);
    }

    // 6. Preparar CPU con registros del MZ header + game config
    // dsBase in game.json is the linear address of DGROUP when loaded at a low segment (~0x0100).
    // For runtime with loadSeg, we need to relocate: DS = loadSeg + (dsBase - lowLoadBase) / 16
    // where lowLoadBase = dsBase - (dsBase_offset_from_image_start) = computed from the corpus.
    // Simpler: DGROUP offset from image start = dsBase - 0x10000 (Ghidra base = seg 0x1000 = 0x10000).
    // Runtime DS = loadSeg + dgroupOffset / 16.
    ushort dsSeg;
    if (game.DsBase > 0)
    {
        int dgroupOffsetFromImage = (int)game.DsBase - 0x10000;
        dsSeg = (ushort)(loadSeg + dgroupOffsetFromImage / 16);
    }
    else
    {
        dsSeg = pspSeg;
    }
    var cpu = new Cpu
    {
        CS = bootReal ? (ushort)bootCs : (isCom ? loadSeg : mz!.AbsCS),
        DS = bootReal ? pspSeg : (isCom ? loadSeg : dsSeg),
        ES = bootReal ? pspSeg : (isCom ? loadSeg : dsSeg),
        SS = bootReal ? (ushort)bootSs : (isCom ? loadSeg : mz!.AbsSS),
        SP = bootReal ? (ushort)bootSp : (isCom ? (ushort)0xFFFE : mz!.InitSP),
        Platform = platform,
        LoadBase = isCom ? 0 : loadSeg * 16,
        ImageBias = runImageBias,
    };
    // boot-real necesita la BDA/IVT montadas (timer, vectores) como en el host.
    if (bootReal) platform.AttachMemory(mem);

    if (snapMem != null)
    {
        // Memoria convencional entera del snapshot (PSP, env, DGROUP, drivers,
        // ventana de overlays). AttachMemory después: re-stubbea los vectores de
        // los INT que maneja la plataforma C# (los trampolines 9F00 del explorador
        // no significan nada aquí) y monta la BDA canónica.
        Array.Copy(snapMem, 0, mem.Raw, 0, 0xA0000);
        platform.AttachMemory(mem);
        PakuPort.Core.Platform.DosPlatform.RestoreGameVectors(mem, snapMem);

        // Set-mode REAL por la plataforma (INT 10 AH=00): asignar VideoMode a
        // pelo deja los registros del Graphics Controller en texto y
        // RenderFramebuffer renderizaría la página de texto vacía.
        cpu.AX = 0x0013; platform.Int(0x10, cpu, mem);

        // VRAM (modo 13h lineal) + paleta DAC capturada (INT 10 AH=10 / OUTs).
        if (platform.Video.IsMode13h)
            Array.Copy(snapMem, 0xA0000, platform.Video.Planes[0], 0, 64000);
        string palHex = snapRoot.GetProperty("palette").GetString() ?? "";
        if (palHex.Length == 1536 && palHex.Any(ch => ch != '0'))
            for (int i = 0; i < 768; i++)
                platform.Video.Dac[i] = Convert.ToByte(palHex.Substring(i * 2, 2), 16);

        // Drivers cargados por EXEC en el snapshot: el juego los llama por far
        // pointers con su segmento real; FarDispatch los enruta al código lifteado
        // del driver (keyBase). Sin keyBase, la far-call cae al miss path.
        foreach (var e in snapRoot.GetProperty("execOvls").EnumerateArray())
        {
            ushort dseg = (ushort)e.GetProperty("seg").GetInt32();
            string dnm = e.GetProperty("name").GetString()!;
            var reg = driverRegions.FirstOrDefault(dr => dr.Seg == dseg &&
                dr.Name.Equals(dnm, StringComparison.OrdinalIgnoreCase));
            PakuPort.Core.Platform.DriverRegistry.RegisterDriverSegment(dseg, dnm,
                reg.Name != null ? reg.KeyBase : -1);
        }

        // Registros exactos de la entrada de la función snapshoteada.
        var sr = snapRoot.GetProperty("regs");
        ushort R(string k) => (ushort)HexToInt(sr.GetProperty(k).GetString()!);
        cpu.AX = R("ax"); cpu.BX = R("bx"); cpu.CX = R("cx"); cpu.DX = R("dx");
        cpu.SI = R("si"); cpu.DI = R("di"); cpu.BP = R("bp"); cpu.SP = R("sp");
        cpu.CS = R("cs"); cpu.DS = R("ds"); cpu.ES = R("es"); cpu.SS = R("ss");
        cpu.SetFlagsWord(R("flags"));
        Console.WriteLine($"  snapshot regs: CS={cpu.CS:X4} SS:SP={cpu.SS:X4}:{cpu.SP:X4} DS={cpu.DS:X4} ES={cpu.ES:X4}");

        // Handles del kernel DOS abiertos en el oráculo al snapshotear: el juego guarda
        // los NÚMEROS en su memoria (FILE._file), así que hay que reabrir los mismos
        // ficheros con los mismos números antes de reanudar.
        if (snapRoot.TryGetProperty("handles", out var snapHandles) && platform.Int21 != null)
            foreach (var hp in snapHandles.EnumerateObject())
            {
                int hNum = int.Parse(hp.Name);
                string hName = hp.Value.GetProperty("name").GetString() ?? "";
                long hPos = hp.Value.GetProperty("pos").GetInt64();
                bool ok = platform.Int21.OpenAtHandle(hNum, hName, hPos);
                Console.WriteLine($"  handle {hNum}: '{hName}' pos={hPos} -> {(ok ? "OK" : "NO ENCONTRADO")}");
            }
    }

    // 6b. Inyectar DGROUP del corpus (solo si se pide explícitamente con --corpus)
    if (corpusFunc != null)
    {
        string cfn = corpusFunc.StartsWith("FUN_") ? corpusFunc : $"FUN_1000_{corpusFunc}";
        var corpusCases = FunctionVerifier.LoadCorpus(game.CorpusDir, cfn);
        if (corpusCases.Count > 0)
        {
            var cas = corpusCases[0];
            int dgroupDst = dsSeg * 16;
            int len = Math.Min(cas.EntryMem.Length, mem.Raw.Length - dgroupDst);
            if (len > 0)
            {
                Array.Copy(cas.EntryMem, 0, mem.Raw, dgroupDst, len);
                Console.WriteLine($"  DGROUP inyectado de {cfn} case 0 ({len} bytes en 0x{dgroupDst:X})");
            }
        }
    }

    // 7. Ejecutar
    cpu.Budget = 10_000_000;
    cpu.FarDispatchMisses = new HashSet<int>();
    if (bootReal)
    {
        int pspLin = pspSeg * 16;
        ushort envSeg = (ushort)(mem.Raw[pspLin + 0x2C] | (mem.Raw[pspLin + 0x2D] << 8));
        string envFirst = "";
        for (int k = 0; k < 24 && mem.Raw[envSeg * 16 + k] != 0; k++) envFirst += (char)mem.Raw[envSeg * 16 + k];
        Console.WriteLine($"  [boot-real] PSP=0x{pspSeg:X4} env_seg=0x{envSeg:X4} primera_cadena_env='{envFirst}'");
    }
    Console.WriteLine($"  DS=0x{dsSeg:X4} (DGROUP at 0x{dsSeg * 16:X5})");
    Console.WriteLine("  Ejecutando...");
    if (Environment.GetEnvironmentVariable("PAKU_EMIT_TRACE") == "1")
        mem.IpLog = new List<int>();
    // Banco de pruebas por función: sólo graba desde el intérprete,
    // así que esta captura pide PAKU_INTERP_ONLY=1 — igual que la telemetría de procedencia.
    string? avisoCorpus = PakuPort.Core.CorpusRecorder.ArrancaDesdeEntorno();
    string? avisoCapas = PakuPort.Core.PerfilCapas.ArrancaDesdeEntorno();
    string? avisoCob = PakuPort.Core.Cobertura.ArrancaDesdeEntorno();
    if (avisoCapas != null) Console.Error.WriteLine(avisoCapas);
    if (avisoCob != null) Console.Error.WriteLine(avisoCob);
    if (avisoCorpus != null) Console.WriteLine("  " + avisoCorpus);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        entryDelegate(cpu, mem);
        if (snapEachKey >= 0)
        {
            var eachFn = FunctionVerifier.GetCompiledMethod($"Fn_{snapEachKey:X5}");
            ushort snapDgroup = cpu.DS;
            if (eachFn != null)
            {
                Console.WriteLine($"  snapshot-loop: Fn_{snapEachKey:X5} hasta [DS:0x{snapUntilOff:X4}]!=0");
                while (cpu.Budget > 0 &&
                       (snapUntilOff < 0 || mem.RdU16(snapDgroup, (ushort)snapUntilOff) == 0))
                {
                    cpu.SP -= 2; // el ret de la Fn hace SP += 2 (llamada directa sin call real)
                    eachFn(cpu, mem);
                }
            }
        }
    }
    catch (PakuPort.Core.Platform.ProgramExitException pex)
    {
        Console.WriteLine($"  Programa terminó con INT 21h/4Ch (exit code {pex.ExitCode})");
        if (Environment.GetEnvironmentVariable("PAKU_EXIT_STACK") == "1")
        {
            // DEBUG: dump the C# Fn_ chain that reached exit + MSC errno global
            ushort dg = 0x264B;
            int errno = mem.RdU16(dg, 0x599b);
            Console.WriteLine($"  [exit-dbg] errno[0x599b]={errno} DS={cpu.DS:X4}");
            var st = pex.StackTrace ?? "";
            foreach (var line in st.Split('\n').Where(l => l.Contains("Fn_")).Take(40))
                Console.WriteLine($"    {line.Trim()}");
            if (PakuPort.Core.Platform.Int21hHandler.FileTrace)
            {
                Console.WriteLine("  [file-trace] últimas operaciones de fichero:");
                foreach (var op in PakuPort.Core.Platform.Int21hHandler.FileOps)
                    Console.WriteLine($"    {op}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  EXCEPCIÓN: {ex.GetType().Name}: {ex.Message}");
        if (Environment.GetEnvironmentVariable("PAKU_EXIT_STACK") == "1")
            foreach (var line in (ex.StackTrace ?? "").Split('\n').Where(l => l.Contains("Fn_")).Take(30))
                Console.Error.WriteLine($"    {line.Trim()}");
        else
            Console.Error.WriteLine($"  Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()}");
    }
    sw.Stop();
    PakuPort.Core.CorpusRecorder.Termina();
    Console.WriteLine($"  Terminado en {sw.ElapsedMilliseconds} ms (budget restante: {cpu.Budget})");
    if (PakuPort.Core.Platform.Int21hHandler.FileTrace)
    {
        Console.WriteLine("  [file-trace] últimas operaciones de fichero:");
        foreach (var op in PakuPort.Core.Platform.Int21hHandler.FileOps)
            Console.WriteLine($"    {op}");
    }
    if (mem.IpLog != null && Environment.GetEnvironmentVariable("PAKU_IPLOG_DUMP") is string ipDump)
    {
        using var wtr = new StreamWriter(ipDump);
        foreach (int e in mem.IpLog) wtr.WriteLine($"{e & 0xFFFF:X4}");
        Console.WriteLine($"  IpLog volcado ({mem.IpLog.Count}) a {ipDump}");
    }
    if (mem.IpLog != null)
    {
        // Entradas = ip | (SP<<16). Buscar la PRIMERA caída de SP bajo 0xFC00 (el
        // oráculo nunca baja de 0xFF2E) y volcar la ventana alrededor.
        int idx = -1;
        for (int i = 0; i < mem.IpLog.Count; i++)
        {
            int sp = (int)((uint)mem.IpLog[i] >> 16);
            if (sp != 0 && sp < 0xFC00) { idx = i; break; }
        }
        if (idx >= 0)
        {
            Console.WriteLine($"  IpLog: primera caída SP<FC00 en entrada {idx}/{mem.IpLog.Count}:");
            for (int i = Math.Max(0, idx - 50); i < Math.Min(mem.IpLog.Count, idx + 8); i++)
                Console.WriteLine($"    ip={mem.IpLog[i] & 0xFFFF:X4} sp={(uint)mem.IpLog[i] >> 16:X4}{(i == idx ? "  <<<" : "")}");
        }
        Console.WriteLine($"  IpLog cola ({mem.IpLog.Count}): " +
            string.Join(" ", mem.IpLog.TakeLast(90).Select(ip => $"{ip & 0xFFFF:X4}/{(uint)ip >> 16:X4}")));
        // Entrada al camino de error del runtime C (pila de emergencia SP<0x200):
        int eIdx = -1;
        for (int i = 0; i < mem.IpLog.Count; i++)
        {
            int sp = (int)((uint)mem.IpLog[i] >> 16);
            int ip = mem.IpLog[i] & 0xFFFF;
            if (sp > 0 && sp < 0x200 && ip >= 0xE000) { eIdx = i; break; }
        }
        if (eIdx >= 0)
        {
            Console.WriteLine($"  IpLog: entrada al camino de error en {eIdx}:");
            for (int i = Math.Max(0, eIdx - 70); i < Math.Min(mem.IpLog.Count, eIdx + 6); i++)
                Console.WriteLine($"    ip={mem.IpLog[i] & 0xFFFF:X4} sp={(uint)mem.IpLog[i] >> 16:X4}{(i == eIdx ? "  <<<" : "")}");
        }
    }
    if (cpu.FarDispatchMisses != null && cpu.FarDispatchMisses.Count > 0)
        Console.WriteLine($"  FarDispatch MISSes: {cpu.FarDispatchMisses.Count} únicos ({string.Join(", ", cpu.FarDispatchMisses.OrderBy(fk => fk).Take(12).Select(fk => $"0x{fk:X}"))}{(cpu.FarDispatchMisses.Count > 12 ? "..." : "")})");

    // 8. Volcar framebuffer
    string outDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameDir)!) ?? ".", "output");
    Directory.CreateDirectory(outDir);
    string bmpPath = Path.Combine(outDir, $"run_{Path.GetFileNameWithoutExtension(exePath)}.bmp");
    byte[] rgba = platform.Video.RenderFramebuffer();
    WriteBmp(bmpPath, rgba, platform.Video.Width, platform.Video.Height);

    int nonBlack = 0;
    for (int i = 0; i < rgba.Length; i += 4)
        if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0) nonBlack++;

    Console.WriteLine($"  Video mode: 0x{platform.Video.VideoMode:X2} ({platform.Video.Width}x{platform.Video.Height})");
    Console.WriteLine($"  Píxeles no negros: {nonBlack}/{platform.Video.Width * platform.Video.Height}");
    Console.WriteLine($"  Framebuffer → {bmpPath}");

    return 0;
}

// Imagen compuesta para el arranque por snapshot: añade los drivers EXEC (con las
// relocaciones MZ del segmento real del snapshot) como pseudo-overlays y devuelve
// la imagen ampliada. Rellena driverRegions (seg->keyBase) y driverSeeds
// (entradas de driver observadas en la sesión del explorador).
static byte[] BuildDriverComposite(byte[] imageBytes, GameConfig game, System.Text.Json.JsonElement snapRoot,
    string gameDir, List<(ushort Seg, string Name, int KeyBase, int Len)> driverRegions, List<int> driverSeeds,
    byte[]? snapMem = null, int loadSeg = 0, int imageBias = 0)
{
    Dictionary<string, (ushort Seg, int Reloc)>? byName = null;
    if (snapRoot.TryGetProperty("execOvls", out var snapOvls))
    {
        byName = new Dictionary<string, (ushort Seg, int Reloc)>();
        foreach (var e in snapOvls.EnumerateArray())
            byName[e.GetProperty("name").GetString()!.ToLowerInvariant()] =
                ((ushort)e.GetProperty("seg").GetInt32(), e.GetProperty("reloc").GetInt32());
    }
    imageBytes = BuildDriverCompositeFromList(imageBytes, game, byName, gameDir, driverRegions, driverSeeds,
        snapMem, loadSeg, imageBias);
    if (snapRoot.TryGetProperty("driverCalls", out var dcs0))
        foreach (var pcall in dcs0.EnumerateObject())
        {
            int plus = pcall.Name.LastIndexOf("+0x", StringComparison.OrdinalIgnoreCase);
            if (plus < 0) continue;
            string cn = pcall.Name[..plus].ToLowerInvariant();
            int coff = Convert.ToInt32(pcall.Name[(plus + 3)..], 16);
            var reg = driverRegions.FirstOrDefault(dr => dr.Name == cn);
            if (reg.Name != null && coff < reg.Len) driverSeeds.Add(reg.KeyBase + coff);
        }
    return imageBytes;
}

// Núcleo reutilizable: boot-real le pasa la lista de EXEC observados en el probe
// (Int21hHandler.ExecLoads) en vez del json del snapshot.
static byte[] BuildDriverCompositeFromList(byte[] imageBytes, GameConfig game,
    Dictionary<string, (ushort Seg, int Reloc)>? lastByName, string gameDir,
    List<(ushort Seg, string Name, int KeyBase, int Len)> driverRegions, List<int> driverSeeds,
    byte[]? snapMem = null, int loadSeg = 0, int imageBias = 0)
{
    // Zona boot [loadSeg*16, +imageBias) del mundo snapshot: código del bootstrap
    // VROOMM/EXEPACK que vive ANTES del inicio de la imagen (no existe en el fichero).
    // Se añade como pseudo-overlay en BootKeyBase y se siembran sus prólogos, para que
    // los `lcall loadSeg:off<bias` (p.ej. el file_exists de la auto-carga en 1000:0002)
    // se ejecuten como código lifteado en vez de hacer MISS (SP+=4 y llamada perdida).
    if (snapMem != null && imageBias > 0 && loadSeg > 0)
    {
        int bootLin = loadSeg * 16;
        var boot = new byte[imageBias];
        Array.Copy(snapMem, bootLin, boot, 0, imageBias);
        int bootBase = OverlayDirectory.BootKeyBase;
        if (imageBytes.Length < bootBase + imageBias)
        {
            var big = new byte[bootBase + imageBias];
            Array.Copy(imageBytes, big, imageBytes.Length);
            imageBytes = big;
        }
        Array.Copy(boot, 0, imageBytes, bootBase, imageBias);
        game.Overlays.Add(0x2000, bootBase, imageBias);
        int nSeeds = 0;
        for (int i = 0; i + 2 < imageBias; i++)
            if (boot[i] == 0x55 && boot[i + 1] == 0x8B && boot[i + 2] == 0xEC)
            { driverSeeds.Add(bootBase + i); nSeeds++; }
        Console.WriteLine($"  zona boot lifteada: {imageBias} bytes @key 0x{bootBase:X5}, {nSeeds} entradas");
    }
    if (lastByName == null || lastByName.Count == 0) return imageBytes;
    int nextBase = 0x40000;
    var grown = new List<(int At, byte[] Body)>();
    foreach (var (dnm, (dseg, drel)) in lastByName)
    {
        string? dpath = Directory.EnumerateFiles(gameDir)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(dnm, StringComparison.OrdinalIgnoreCase));
        if (dpath == null) { Console.WriteLine($"  driver {dnm}: no encontrado en {gameDir}"); continue; }
        var dd = File.ReadAllBytes(dpath);
        if (dd.Length < 28 || dd[0] != 'M' || dd[1] != 'Z') continue;
        int dHdr = (dd[8] | (dd[9] << 8)) * 16;
        int dNrel = dd[6] | (dd[7] << 8);
        int dRt = dd[24] | (dd[25] << 8);
        var body = new byte[dd.Length - dHdr];
        Array.Copy(dd, dHdr, body, 0, body.Length);
        for (int ri = 0; ri < dNrel; ri++)
        {
            int ro = dRt + ri * 4;
            int fo = dd[ro] | (dd[ro + 1] << 8);
            int fs = dd[ro + 2] | (dd[ro + 3] << 8);
            int fa = fs * 16 + fo;
            if (fa + 1 < body.Length)
            {
                int w = (body[fa] | (body[fa + 1] << 8)) + drel;
                body[fa] = (byte)w; body[fa + 1] = (byte)(w >> 8);
            }
        }
        grown.Add((nextBase, body));
        game.Overlays.Add(0x1000 + driverRegions.Count, nextBase, body.Length);
        driverRegions.Add((dseg, dnm, nextBase, body.Length));
        nextBase += (body.Length + 0xFF) & ~0xFF;
    }
    if (grown.Count > 0)
    {
        var bigger = new byte[Math.Max(imageBytes.Length, nextBase)];
        Array.Copy(imageBytes, bigger, imageBytes.Length);
        foreach (var (at, body) in grown) Array.Copy(body, 0, bigger, at, body.Length);
        imageBytes = bigger;
        Console.WriteLine($"  drivers lifteados: {string.Join(", ", driverRegions.Select(dr => $"{dr.Name}@{dr.Seg:X4}->key 0x{dr.KeyBase:X5} ({dr.Len} bytes)"))}");
    }
    // Semillas genéricas por driver: la tabla de EXPORTS de los drivers gráficos de
    // una familia de drivers (firma "?GRAPHIC.EXE" en +0x10 de la imagen: count en +0x30, tabla de
    // word-offsets en +0x32 — el residente escribe estas entradas en su tabla de despacho
    // 010C..02EE) y los prólogos push bp;mov bp,sp como red para el resto.
    foreach (var dr in driverRegions)
    {
        if (dr.KeyBase < 0x40000 || dr.KeyBase + 0x40 > imageBytes.Length) continue;
        bool isGfx = dr.Len > 0x40 &&
            System.Text.Encoding.ASCII.GetString(imageBytes, dr.KeyBase + 0x10, 16).Contains("GRAPHIC");
        if (isGfx)
        {
            int cnt = imageBytes[dr.KeyBase + 0x30] | (imageBytes[dr.KeyBase + 0x31] << 8);
            for (int k = 0; k < cnt && k < 128; k++)
            {
                int off = imageBytes[dr.KeyBase + 0x32 + k * 2] | (imageBytes[dr.KeyBase + 0x33 + k * 2] << 8);
                if (off > 0 && off < dr.Len) driverSeeds.Add(dr.KeyBase + off);
            }
        }
        for (int i = 0; i + 2 < dr.Len; i++)
            if (imageBytes[dr.KeyBase + i] == 0x55 && imageBytes[dr.KeyBase + i + 1] == 0x8B && imageBytes[dr.KeyBase + i + 2] == 0xEC)
                driverSeeds.Add(dr.KeyBase + i);
    }
    return imageBytes;
}

static int CmdVerify(GameConfig game, string func)
{
    var rep = FunctionVerifier.Verify(game.ExePath, game.CorpusDir, func, game.Overlays, game.CodeMap, loadBase: game.LoadSegment * 16, loadSegment: game.LoadSegment);

    Console.WriteLine($"== PakuPort verify {func} (0x{rep.GhidraOffset:x4}) ==");
    if (rep.Emit is not null)
    {
        Console.WriteLine($"opcodes usados : {string.Join(", ", rep.Emit.OpcodesUsed)}");
        Console.WriteLine($"no soportados  : {(rep.Emit.NotSupported.Count == 0 ? "ninguno" : string.Join("; ", rep.Emit.NotSupported))}");
    }
    if (rep.Error is not null)
    {
        Console.WriteLine($"ERROR: {rep.Error}");
        return 2;
    }
    if (rep.AllPassed)
    {
        Console.WriteLine($"[32mVERDE {rep.Passed}/{rep.Total} casos[0m (byte-exacto, idéntico al original)");
        return 0;
    }

    Console.WriteLine($"[31mROJO {rep.Passed}/{rep.Total} casos[0m");
    var first = rep.Failures.FirstOrDefault();
    if (first is not null)
    {
        Console.WriteLine("primera divergencia:");
        foreach (var dr in first.MemoryDivergences.Take(4))
            Console.WriteLine("    " + dr);
    }
    return 3;
}

// Gate 4 (Fase 1): replay del corpus A TRAVÉS del intérprete (Interp.Run),
// interpretación pura de los bytes originales — sin CallKey, sin código lifteado. Cada caso
// monta la imagen canónica (la MISMA que decodifica el juez) + el DGROUP del caso, arranca
// en el CS:IP grabado por el oráculo y compara con el MISMO juez (DiffEngine.ReplayCase).
// Un verde aquí = la semántica del intérprete coincide byte-exacta con el oráculo.
// --registros: juzgar tambien los REGISTROS de salida (BancoCiego.ComparaSalida) en vez de solo
// la memoria. NO es el modo por defecto a proposito: el gate 4 de tools/gates.py espera los
// numeros exactos del juez heredado sobre el game.explorer.json del juego de referencia, y esa red
// protege al interprete. Con --registros el enmascarado de ruido queda apagado (implica --strict).
static int CmdInterpVerify(GameConfig game, string gamePath, string? corpusOverride = null, bool strict = false, bool conRegistros = false)
{
    var mundoJ = BancoCiego.MundoDeJuego(game, gamePath, corpusOverride);
    if (mundoJ == null) return 2;
    var (mundo, corpusDir, queMundo, snapshot) = mundoJ.Value;
    int loadBase = mundo.LoadBase, bias = mundo.Bias;
    byte[] codeBytes = mundo.CodeBytes; int codeAt = mundo.CodeAt;

    if (conRegistros) strict = true;   // comparar registros y enmascarar ruido a la vez no tiene sentido
    Console.WriteLine($"== interp-verify: corpus por el INTÉRPRETE (loadBase 0x{loadBase:X5}, " +
        $"código {queMundo} {codeBytes.Length:N0} bytes, bias 0x{bias:X}, " +
        $"juez={(conRegistros ? "memoria+REGISTROS" : "solo memoria")}) ==");
    int green = 0, red = 0, err = 0, skip = 0, considered = 0;
    var greens = new List<string>(); var redsL = new List<string>(); var errsL = new List<string>();
    foreach (var d in Directory.GetDirectories(corpusDir).OrderBy(x => x, StringComparer.Ordinal))
    {
        string funcName = Path.GetFileName(d);
        var cases = FunctionVerifier.LoadCorpus(corpusDir, funcName);
        if (cases.Count == 0) continue;
        considered++;
        var e0 = cases[0].EntryCpu;
        int ghidra = FunctionVerifier.ParseOffset(funcName);
        bool isDrv = funcName.StartsWith("DRV_", StringComparison.Ordinal);
        if (isDrv && snapshot == null) { skip++; continue; }
        if (!isDrv && e0.Cs * 16 + e0.Ip != loadBase + bias + ghidra)
        {
            // coordenadas del caso fuera del mundo del código disponible
            if (skip < 8)
                Console.WriteLine($"  skip {funcName}: cs:ip={e0.Cs:X4}:{e0.Ip:X4} lin=0x{e0.Cs * 16 + e0.Ip:X5} vs esperado=0x{loadBase + bias + ghidra:X5}");
            skip++;
            continue;
        }

        // El montaje del caso (mundo + DGROUP + contexto + efectos) vive en BancoCiego: es la
        // misma operación que necesita el banco ciego, y tenerla dos veces es la clase de
        // duplicado que ya rompió los exports una vez.
        PakuPort.Judge.Diff.PortLogicFunction MakePort(PakuPort.Judge.Corpus.CorpusCase cc) =>
            (byte[] dgroup, in PakuPort.Judge.Oracle.CpuState entry) =>
                BancoCiego.MontaPuerto(mundo, cc.EffectsJson, isDrv,
                                       BancoCiego.ConElInterprete(mundo, cc))(dgroup, in entry);

        int passed = 0; string? firstErr = null; string? firstDiff = null;
        var failures = new List<PakuPort.Judge.Diff.DiffResult>();
        foreach (var cc in cases)
        {
            try
            {
                if (conRegistros)
                {
                    var (dg, cpuFin) = BancoCiego.EjecutaCaso(mundo, cc,
                        BancoCiego.ConElInterprete(mundo, cc), isDrv);
                    string? mal = BancoCiego.ComparaSalida(cc, dg, cpuFin);
                    if (mal == null) passed++;
                    else { firstDiff ??= mal; failures.Add(new PakuPort.Judge.Diff.DiffResult { Passed = false }); }
                    continue;
                }
                var r = PakuPort.Judge.Diff.DiffEngine.ReplayCase(cc, MakePort(cc));
                if (r.Passed) passed++;
                else failures.Add(r);
            }
            catch (Exception ex)
            {
                firstErr ??= $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        // Noise masking iterativo, idéntico al del juez lifteado (FunctionVerifier):
        // divergencias pequeñas y comunes a TODOS los casos fallidos = jitter de C-runtime.
        // --strict lo apaga: con el enmascarado puesto, un solo caso fallido admite hasta 64
        // bytes de divergencia, y eso se traga un control negativo de un byte — medido el
        // 2026-08-24, corrompiendo la salida de un caso y viendo que seguia VERDE.
        var allMask = new List<(int start, int len)>();
        for (int round = 0; !strict && round < 5 && passed < cases.Count && failures.Count > 0 && firstErr == null; round++)
        {
            var noise = FunctionVerifier.DetectRuntimeNoise(failures);
            int maxNoise = failures.Count <= 2 ? 64 : 16;
            if (noise.Count == 0 || noise.Sum(n => n.len) > maxNoise) break;
            allMask.AddRange(noise);
            int p2 = 0; var f2 = new List<PakuPort.Judge.Diff.DiffResult>();
            foreach (var cc in cases)
            {
                var working = (byte[])cc.EntryMem.Clone();
                try { MakePort(cc)(working, cc.EntryCpu); }
                catch (Exception ex) { firstErr ??= $"{ex.GetType().Name}: {ex.Message}"; break; }
                int spM = (int)(cc.EntryCpu.Esp & 0xFFFF);
                int loM = Math.Max(0, spM - PakuPort.Judge.Diff.DiffEngine.StackMaskBelow);
                int hiM = Math.Min(cc.ExitMem.Length, spM + PakuPort.Judge.Diff.DiffEngine.StackMaskAbove);
                for (int i = loM; i < hiM; i++) working[i] = cc.ExitMem[i];
                foreach (var (sM, lM) in allMask)
                {
                    int endM = Math.Min(sM + lM, working.Length);
                    for (int i = sM; i < endM; i++) working[i] = cc.ExitMem[i];
                }
                var ranges = PakuPort.Judge.Diff.MemoryDiff.Compare(working, cc.ExitMem);
                if (ranges.Count == 0) p2++;
                else f2.Add(new PakuPort.Judge.Diff.DiffResult { Passed = false, MemoryDivergences = ranges });
            }
            if (p2 <= passed) break;
            passed = p2; failures = f2;
        }

        if (passed == cases.Count) { green++; greens.Add(funcName.Replace("FUN_1000_", "")); }
        else if (firstErr != null) { err++; errsL.Add($"{funcName.Replace("FUN_1000_", "")}[{firstErr}]"); }
        else { red++; redsL.Add($"{funcName.Replace("FUN_1000_", "")}({passed}/{cases.Count}{(firstDiff != null ? " " + firstDiff : "")})"); }
    }
    Console.WriteLine($"  VERDE={green}  ROJO={red}  ERROR={err}  SKIP={skip}  insns interpretadas={Interp.InsnCount:N0}");
    Console.WriteLine("  VERDES: " + string.Join(" ", greens));
    if (redsL.Count > 0) Console.WriteLine("  ROJAS : " + string.Join(" ", redsL));
    if (errsL.Count > 0) Console.WriteLine("  ERROR : " + string.Join(" | ", errsL.Take(20)));
    // A gate that skips every case is a no-op, not a pass: it means the corpus coordinates do
    // not belong to the code world of this game.json (wrong config), so say it out loud.
    if (considered > 0 && skip == considered)
    {
        Console.WriteLine($"[31mCONFIG INCORRECTA: los {skip} casos de {corpusDir}[0m");
        Console.WriteLine($"[31m  no viven en el mundo de este game.json (loadSegment 0x{game.LoadSegment:X4}).[0m");
        Console.WriteLine("[31m  El gate canonico usa el corpus del EXPLORADOR:[0m");
        Console.WriteLine("[31m  pakuport interp-verify --game games/<id>/game.explorer.json[0m");
    }
    return green >= 20 ? 0 : 3;
}

// Barrido del corpus: la lista de trabajo determinista de Fase 1 (plan §5). Dice qué funciones
// están verdes, cuáles rojas, y qué opcodes faltan (histograma por impacto) — sin inventar nada.
static int CmdSurvey(GameConfig game)
{
    var es = CorpusSurvey.Run(game.ExePath, game.CorpusDir, game.Overlays, game.CodeMap, game.LoadSegment * 16, game.LoadSegment);
    int green = es.Count(e => e.Status == SurveyStatus.Green);
    int red = es.Count(e => e.Status == SurveyStatus.Red);
    int unsup = es.Count(e => e.Status == SurveyStatus.Unsupported);
    int derr = es.Count(e => e.Status == SurveyStatus.DecodeError);
    int withCorpus = es.Count(e => e.Status != SurveyStatus.NoCorpus);

    int greenStub = es.Count(e => e.Status == SurveyStatus.Green && e.Stubbed.Count > 0);
    Console.WriteLine($"== survey corpus ({es.Count} funciones, {withCorpus} con casos) ==");
    Console.WriteLine($"  VERDE={green} (de ellos {greenStub} con INT/in/out stubbeado, juez-verificado)  ROJO={red}  NO-SOPORTADA={unsup}  ERROR-DECODE={derr}");

    // Categoriza el conjunto de bloqueadores de CADA función no soportada, para saber qué fase la
    // desbloquearía. Una función solo es alcanzable si TODOS sus bloqueadores son de la misma fase.
    static string Cat(string ns)
    {
        if (ns.Contains("int 0x3F")) return "overlay(int3F)";
        if (ns.Contains("int 0x")) return "dos/bios-int";
        if (ns.Contains("call far/indirecto")) return "farcall/indcall";
        if (ns.Contains("jmp indirecto")) return "jmp-indirecto";
        if (ns.Contains("in (") || ns.Contains("In)")) return "port-io";
        return "otro";
    }
    var perFuncCats = new Dictionary<string, int>(StringComparer.Ordinal);
    var soloOverlayFarcall = new List<string>();
    var otroReasons = new HashSet<string>(StringComparer.Ordinal);
    foreach (var e in es.Where(e => e.Status == SurveyStatus.Unsupported))
    {
        foreach (var ns in e.NotSupported) if (Cat(ns) == "otro") otroReasons.Add(ns);
        var cats = e.NotSupported.Select(Cat).ToHashSet();
        string key = string.Join("+", cats.OrderBy(x => x));
        perFuncCats[key] = perFuncCats.GetValueOrDefault(key) + 1;
        if (cats.All(c => c is "overlay(int3F)" or "farcall/indcall" or "jmp-indirecto"))
            soloOverlayFarcall.Add(e.FuncName.Replace("FUN_1000_", ""));
    }
    if (perFuncCats.Count > 0)
    {
        Console.WriteLine("  funciones no soportadas por combinación de bloqueadores:");
        foreach (var kv in perFuncCats.OrderByDescending(k => k.Value))
            Console.WriteLine($"      {kv.Value,3}x  [{kv.Key}]");
        Console.WriteLine($"  → alcanzables SOLO con Fase 5 (overlay/far/jmp-ind, sin plataforma): {soloOverlayFarcall.Count}");
        if (soloOverlayFarcall.Count > 0)
            Console.WriteLine("    " + string.Join(" ", soloOverlayFarcall));
        if (otroReasons.Count > 0)
            Console.WriteLine("  razones 'otro': " + string.Join(" | ", otroReasons.Take(10)));
    }

    Console.WriteLine("  VERDES: " + string.Join(" ", es.Where(e => e.Status == SurveyStatus.Green).Select(e => e.FuncName.Replace("FUN_1000_", ""))));
    if (red > 0)
        Console.WriteLine("  ROJAS : " + string.Join(" ", es.Where(e => e.Status == SurveyStatus.Red).Select(e => $"{e.FuncName.Replace("FUN_1000_", "")}({e.Passed}/{e.Total})")));
    if (derr > 0)
        Console.WriteLine("  ERROR : " + string.Join(" ", es.Where(e => e.Status == SurveyStatus.DecodeError).Select(e => e.FuncName.Replace("FUN_1000_", ""))));

    return 0;
}

// Sondeo de las ROJAS: clasifica CADA byte divergente de cada caso que falla en
//   - "oráculo": el port dejó el byte en su valor de ENTRADA y el oráculo lo cambió → efecto de
//     runtime que el juez offline no modela (INT/IO/BIOS) o artefacto de secuenciación. NO es un
//     valor mal calculado por el port.
//   - "port-mal": el port escribió un valor DISTINTO al de entrada Y distinto al del oráculo →
//     divergencia real de cálculo (posible bug de lifting o de enmascarado del juez).
// Reusa el MISMO juez (DiffEngine.ReplayCase: enmascarado de pila + ratón/timer) y la MISMA
// elección de modo (stub/follow) que el survey. Solo lee; no cambia veredictos.
static int CmdDiagRed(GameConfig game, string? outPath)
{
    outPath ??= Path.Combine("logs", "diag_rojas.json");
    int loadBase = game.LoadSegment * 16;

    Console.WriteLine("== diag-red: clasificando divergencias de las ROJAS ==");
    var es = CorpusSurvey.Run(game.ExePath, game.CorpusDir, game.Overlays, game.CodeMap, loadBase, game.LoadSegment);
    var reds = es.Where(e => e.Status == SurveyStatus.Red).ToList();
    Console.WriteLine($"  survey: {reds.Count} rojas de {es.Count} funciones");

    // Carga de la imagen idéntica a FunctionVerifier.Verify.
    var exeBytes = File.ReadAllBytes(game.ExePath);
    ExePackDecompressor.PatchResidentThunks(exeBytes);
    var exe = new ExeImage(exeBytes);
    if (game.LoadSegment != 0) exe.ApplyMzRelocations(game.LoadSegment);
    exe.CodeMap = game.CodeMap;

    var report = new List<object>();
    int nOracle = 0, nWrong = 0;
    foreach (var red in reds)
    {
        int rootKey = OverlayDirectory.ResidentBase + red.Offset;
        var cases = FunctionVerifier.LoadCorpus(game.CorpusDir, red.FuncName);

        // Elección de modo idéntica al survey: gana el que más casos pasa (empate → stub).
        (bool ok, int passed, EmitResult? em, Action<Cpu, Memory>? del) Eval(bool followFar)
        {
            try
            {
                var funcs = Emitter.DiscoverCallTree(exe, rootKey, game.Overlays, followFar);
                var em = Emitter.EmitProgram(funcs, rootKey);
                if (!em.FullySupported) return (false, -1, em, null);
                var del = FunctionVerifier.Compile(em.Source, em.Namespace, em.ClassName, em.MethodName);
                var (allPassed, p, _, failures) = FunctionVerifier.ReplayAllFx(cases, del, loadBase);
                if (!allPassed && failures.Count > 0)
                    (_, p, _) = FunctionVerifier.TryMaskNoise(del, loadBase, cases, p, failures);
                return (true, p, em, del);
            }
            catch { return (false, -1, null, null); }
        }

        var stub = Eval(false);
        var follow = Eval(true);
        var chosen = (follow.ok && follow.passed > stub.passed) ? follow : (stub.ok ? stub : follow);
        if (!chosen.ok || chosen.del is null || chosen.em is null)
        {
            Console.WriteLine($"  {red.FuncName.Replace("FUN_1000_", "")}: no evaluable (unsupported/compile)");
            continue;
        }
        string mode = (chosen.Equals(follow)) ? "follow" : "stub";

        // Tres buckets por byte divergente (p=port, x=exit oráculo, e=entrada; siempre p!=x):
        //   oracle  : p==e            -> el port dejó el byte; el oráculo lo cambió (INT/IO/BIOS).
        //   phantom : p!=e && x==e    -> el oráculo NO lo tocó; el port escribió scratch de más
        //                                (flujo divergente por I/O stubbeado). No es cálculo malo.
        //   threeway: p!=e && x!=e    -> los tres difieren: recálculo real a otro valor.
        int failCases = 0; long div = 0, oracle = 0, phantom = 0, threeway = 0;
        var examples = new List<object>();
        for (int i = 0; i < cases.Count; i++)
        {
            var dr = PakuPort.Judge.Diff.DiffEngine.ReplayCase(cases[i],
                FunctionVerifier.MakePort(chosen.del, loadBase, cases[i]));
            if (dr.Passed) continue;
            failCases++;
            foreach (var rg in dr.MemoryDivergences)
            {
                int rgThree = 0, k3 = -1;
                for (int k = 0; k < rg.Length; k++)
                {
                    byte pv = rg.Before[k], xv = rg.After[k];
                    if (pv == xv) continue;
                    div++;
                    byte en = cases[i].EntryMem[rg.Start + k];
                    if (pv == en) oracle++;
                    else if (xv == en) phantom++;
                    else { threeway++; rgThree++; if (k3 < 0) k3 = k; }
                }
                if (rgThree > 0 && examples.Count < 16)
                {
                    int off = rg.Start + k3;
                    examples.Add(new
                    {
                        @case = i, at = $"0x{off:X4}", len = rg.Length, threewayBytes = rgThree,
                        entry = $"0x{cases[i].EntryMem[off]:X2}", port = $"0x{rg.Before[k3]:X2}", exit = $"0x{rg.After[k3]:X2}",
                    });
                }
            }
        }

        // ORACULO: sin escrituras de más ni recálculos. PHANTOM: escribe scratch pero 0 recálculos
        // (raíz I/O). THREEWAY: al menos un byte recalculado a otro valor (candidato a bug/artefacto).
        string klass = threeway > 0 ? "THREEWAY" : (phantom > 0 ? "PHANTOM" : "ORACULO");
        bool hasIo = chosen.em.Stubbed.Any(s => s.Contains("int 0x") || s == "in" || s.Contains("out"));
        if (klass == "ORACULO") nOracle++; else nWrong++;
        var name = red.FuncName.Replace("FUN_1000_", "");
        Console.WriteLine($"  {name,-5} [{mode,-6}] {red.Passed}/{red.Total}  div={div,-5} oraculo={oracle,-5} phantom={phantom,-4} threeway={threeway,-4} => {klass,-8} io={(hasIo ? "si" : "NO")}"
            + (chosen.em.Stubbed.Count > 0 ? $"  ({string.Join(",", chosen.em.Stubbed.Distinct().Take(6))})" : ""));

        report.Add(new
        {
            func = name, mode, passed = red.Passed, total = red.Total, failCases,
            divBytes = div, oracleBytes = oracle, phantomBytes = phantom, threewayBytes = threeway,
            klass, io = hasIo, stubbed = chosen.em.Stubbed.Distinct().ToList(), examples,
        });
    }

    int cOrac = report.Count(r => (string)r.GetType().GetProperty("klass")!.GetValue(r)! == "ORACULO");
    int cPhan = report.Count(r => (string)r.GetType().GetProperty("klass")!.GetValue(r)! == "PHANTOM");
    int cThree = report.Count(r => (string)r.GetType().GetProperty("klass")!.GetValue(r)! == "THREEWAY");
    int cThreeNoIo = report.Count(r => (string)r.GetType().GetProperty("klass")!.GetValue(r)! == "THREEWAY"
        && !(bool)r.GetType().GetProperty("io")!.GetValue(r)!);
    Console.WriteLine($"\n  RESUMEN: ORACULO={cOrac}  PHANTOM={cPhan}  THREEWAY={cThree} (de ellas sin I/O={cThreeNoIo})");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(
        new { cOrac, cPhan, cThree, cThreeNoIo, funcs = report },
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"  JSON => {Path.GetFullPath(outPath)}");
    return 0;
}

// Anti-verde-falso (docs/04 §4, plan Fase 0): exige VERDE limpio y luego ROJO al inyectar un bug
// deliberado (invertir la primera resta del C# emitido). Si el "bug" siguiera VERDE, el juez no
// estaría mordiendo y el verde sería falso → fallo.
static int CmdSanity(GameConfig game, string func)
{
    Console.WriteLine($"== PakuPort sanity {func} ==");

    int lb = game.LoadSegment * 16;
    var clean = FunctionVerifier.Verify(game.ExePath, game.CorpusDir, func, game.Overlays, game.CodeMap, loadBase: lb, loadSegment: game.LoadSegment);
    if (clean.Error is not null) { Console.WriteLine($"ERROR (limpio): {clean.Error}"); return 2; }
    Console.WriteLine($"1) limpio  : {(clean.AllPassed ? "[32mVERDE" : "[31mROJO")} {clean.Passed}/{clean.Total}[0m");

    const string from = "c.Sub16(";
    const string to = "c.Add16(";
    if (clean.Emit is null || !clean.Emit.Source.Contains(from))
    {
        Console.WriteLine("AVISO: la función no contiene una resta inyectable; sanity no aplicable a esta diana.");
        return clean.AllPassed ? 0 : 3;
    }

    var buggy = FunctionVerifier.Verify(game.ExePath, game.CorpusDir, func, game.Overlays, game.CodeMap, s => s.Replace(from, to), loadBase: lb, loadSegment: game.LoadSegment);
    Console.WriteLine($"2) con bug : {(buggy.AllPassed ? "[31mVERDE (¡falso!)" : "[32mROJO (juez muerde)")} {buggy.Passed}/{buggy.Total}[0m");
    if (!buggy.AllPassed && buggy.Failures.Count > 0)
        Console.WriteLine("   divergencia: " + buggy.Failures[0].MemoryDivergences.FirstOrDefault());

    bool ok = clean.AllPassed && !buggy.AllPassed;
    Console.WriteLine(ok ? "[32mSANITY OK: verde limpio y rojo con bug (verde no falso)[0m"
                         : "[31mSANITY FALLA[0m");
    return ok ? 0 : 3;
}

static int CmdEmit(GameConfig game, string func)
{
    int off = FunctionVerifier.ParseOffset(func);
    int rootKey = OverlayDirectory.ResidentBase + off;
    var exe = ExeImage.Load(game.ExePath, game.LoadSegment);
    exe.CodeMap = game.CodeMap;
    var funcs = Emitter.DiscoverCallTree(exe, rootKey, game.Overlays);
    var em = Emitter.EmitProgram(funcs, rootKey);
    Console.WriteLine(em.Source);
    Console.WriteLine($"// funciones en el árbol: {em.Functions.Count}");
    Console.WriteLine($"// opcodes: {string.Join(", ", em.OpcodesUsed)}");
    Console.WriteLine($"// no soportados: {(em.NotSupported.Count == 0 ? "ninguno" : string.Join("; ", em.NotSupported))}");
    return em.FullySupported ? 0 : 2;
}

static int CmdDecode(GameConfig game, string func)
{
    int off = FunctionVerifier.ParseOffset(func);
    int rootKey = OverlayDirectory.ResidentBase + off;
    var exe = ExeImage.Load(game.ExePath, game.LoadSegment);
    exe.CodeMap = game.CodeMap;
    var fn = Decoder16.DecodeFunction(exe.GetBytesForKey(rootKey), rootKey, game.Overlays.BaseOf(rootKey), game.Overlays, exe);
    foreach (var d in fn.Instructions)
        Console.WriteLine($"{d.Offset:X4}: {Convert.ToHexString(d.Bytes).ToLowerInvariant(),-16} {d.Text}");
    return 0;
}

// Contraste iced vs capstone (oráculo de decode independiente, plan Paso 4). Compara las ternas
// (offset, longitud, mnemónico). Requiere python + capstone en el entorno.
static int CmdCrosscheck(GameConfig game, string func)
{
    int off = FunctionVerifier.ParseOffset(func);
    int rootKey = OverlayDirectory.ResidentBase + off;
    var exe = ExeImage.Load(game.ExePath, game.LoadSegment);
    var fn = Decoder16.DecodeFunction(exe.GetBytesForKey(rootKey), rootKey, game.Overlays.BaseOf(rootKey), game.Overlays);

    string py = $@"
from capstone import *
import json,sys
data=open(r'{game.ExePath}','rb').read()
md=Cs(CS_ARCH_X86,CS_MODE_16)
base=0x200+{off}
out=[]
for ins in md.disasm(data[base:base+512],{off}):
    out.append([ins.address,ins.size,ins.mnemonic])
    if ins.mnemonic in ('ret','retf'): break
print(json.dumps(out))
";
    var psi = new ProcessStartInfo("python", "-c \"" + py.Replace("\"", "\\\"").Replace("\n", "; ") + "\"")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    // El script multilinea se pasa mejor por stdin; usamos archivo temporal.
    string tmp = Path.GetTempFileName() + ".py";
    File.WriteAllText(tmp, py);
    psi = new ProcessStartInfo("python", $"\"{tmp}\"")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

    using var p = Process.Start(psi)!;
    string sout = p.StandardOutput.ReadToEnd();
    string serr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    File.Delete(tmp);
    if (p.ExitCode != 0)
    {
        Console.Error.WriteLine("capstone falló: " + serr);
        return 2;
    }

    using var doc = System.Text.Json.JsonDocument.Parse(sout);
    var cap = doc.RootElement.EnumerateArray()
        .Select(e => ((int)e[0].GetInt32(), e[1].GetInt32(), e[2].GetString()!)).ToList();

    bool ok = cap.Count == fn.Instructions.Count;
    int n = Math.Min(cap.Count, fn.Instructions.Count);
    for (int i = 0; i < n; i++)
    {
        var ic = fn.Instructions[i];
        var (ca, cl, cm) = cap[i];
        bool match = ca == ic.Offset && cl == ic.Length && cm == ic.Mnemonic;
        if (!match) ok = false;
        Console.WriteLine($"{(match ? "OK " : "XX ")} iced {ic.Offset:X4}/{ic.Length}/{ic.Mnemonic,-6}  capstone {ca:X4}/{cl}/{cm}");
    }
    Console.WriteLine(ok ? "[32miced == capstone (decode fiel en 16-bit)[0m"
                         : "[31mDISCREPANCIA iced vs capstone[0m");
    return ok ? 0 : 3;
}

static string? GetOpt(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
}

static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

// ═══════════════════════════════════════════════════════════════════════════
// SPICE86 — record / build / play / doctor / credits
// ═══════════════════════════════════════════════════════════════════════════

// Spice86 checked out next to this repo; paths are relative to the repo root (the CLI's cwd).
const string Spice86ExePath  = @"..\Spice86\src\Spice86\bin\Release\net10.0\Spice86.exe";
const string Spice86Csproj   = @"..\Spice86\src\Spice86\Spice86.csproj";

static int CmdRecord(string[] a)
{
    if (a.Length == 0)
    {
        Console.Error.WriteLine("uso: pakuport record <exe> [--game-dir <dir>] [--output <dir>] [--max-minutes <n>]");
        return 1;
    }
    string exe     = a[0];
    string gameDir = GetOpt(a, "--game-dir") ?? Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".";
    string output  = GetOpt(a, "--output")   ?? Path.Combine("dumps", Path.GetFileNameWithoutExtension(exe));
    string? maxMin = GetOpt(a, "--max-minutes");

    if (!File.Exists(exe))          { Console.Error.WriteLine($"PakuPort: no encuentro {exe}"); return 1; }
    if (!File.Exists(Spice86ExePath)) { Console.Error.WriteLine($"PakuPort: Spice86.exe no encontrado en {Spice86ExePath}"); return 1; }

    Directory.CreateDirectory(output);
    Console.WriteLine($"PakuPort record: {Path.GetFileName(exe)}");
    Console.WriteLine($"  game-dir : {gameDir}");
    Console.WriteLine($"  output   : {output}");
    Console.WriteLine("  Jugando con Spice86... cierra la ventana limpiamente para volcar.");

    var sb = new System.Text.StringBuilder();
    sb.Append($"-e \"{Path.GetFullPath(exe)}\"");
    sb.Append($" -c \"{Path.GetFullPath(gameDir)}\"");
    sb.Append($" -r \"{Path.GetFullPath(output)}\"");
    sb.Append(" --SbType None --OplMode None");

    var psi = new ProcessStartInfo { FileName = Spice86ExePath, Arguments = sb.ToString(), UseShellExecute = false };
    using var proc = Process.Start(psi) ?? throw new InvalidOperationException("no se pudo arrancar Spice86");

    if (maxMin != null && int.TryParse(maxMin, out int mins))
    {
        if (!proc.WaitForExit(TimeSpan.FromMinutes(mins)))
        { proc.Kill(); Console.WriteLine("  Timeout alcanzado, Spice86 terminado."); }
    }
    else proc.WaitForExit();

    string? gp = FindGeneratedProject(output);
    if (gp != null)
        Console.WriteLine($"  OK — GeneratedProject: {gp}\n  Usa 'pakuport build {output}' para construir el .exe.");
    else
        Console.Error.WriteLine("  AVISO: no se encontró GeneratedProject/. ¿Cerraste Spice86 limpiamente?");

    return 0;
}

static int CmdBuild(string[] a)
{
    if (a.Length == 0)
    {
        Console.Error.WriteLine("uso: pakuport build <dumps-dir> [--output <path.exe>] [--game-name <name>] [--spice86 <csproj>]");
        return 1;
    }
    string dumpsDir     = a[0];
    string spiceCsproj  = GetOpt(a, "--spice86") ?? Spice86Csproj;
    string? gp          = FindGeneratedProject(dumpsDir);

    if (gp == null)
    {
        Console.Error.WriteLine($"PakuPort: no encontré GeneratedProject/ en '{dumpsDir}'.");
        Console.Error.WriteLine("  Comprueba que cerraste Spice86 limpiamente.");
        return 1;
    }
    if (!File.Exists(spiceCsproj))
    {
        Console.Error.WriteLine($"PakuPort: Spice86.csproj no encontrado en {spiceCsproj}");
        Console.Error.WriteLine("  Usa --spice86 <ruta> para especificarlo.");
        return 1;
    }

    string gameName = GetOpt(a, "--game-name") ?? DetectGameName(dumpsDir);
    string outExe   = GetOpt(a, "--output")    ?? Path.Combine(dumpsDir, $"{gameName}.exe");

    Console.WriteLine($"PakuPort build: {gameName}");
    Console.WriteLine($"  GeneratedProject : {gp}");
    Console.WriteLine($"  Spice86.csproj   : {spiceCsproj}");

    // 1. Copiar GeneratedProject a directorio de trabajo temporal
    string workDir = Path.Combine(Path.GetTempPath(), $"pakuport-{gameName}-{Path.GetRandomFileName()}");
    CopyDirSkipBinObj(gp, workDir);
    Console.WriteLine($"  WorkDir          : {workDir}");

    // 2. Renombrar y parchear el .csproj
    string? oldCsproj = Directory.GetFiles(workDir, "*.csproj").FirstOrDefault();
    if (oldCsproj == null) { Console.Error.WriteLine("  ERROR: no hay .csproj en GeneratedProject"); return 2; }

    string newCsprojPath = Path.Combine(workDir, $"{gameName}.csproj");
    if (!string.Equals(oldCsproj, newCsprojPath, StringComparison.OrdinalIgnoreCase))
        File.Move(oldCsproj, newCsprojPath);

    string csprojContent = File.ReadAllText(newCsprojPath);
    // Reemplazar referencia relativa a Spice86 → ruta absoluta
    csprojContent = Regex.Replace(
        csprojContent,
        @"<ProjectReference\s+Include=""[^""]*Spice86[^""]*\.csproj""\s*/>",
        $@"<ProjectReference Include=""{spiceCsproj.Replace("\\", "\\\\")}"" />",
        RegexOptions.IgnoreCase);
    csprojContent = Regex.Replace(csprojContent, @"<AssemblyName>[^<]+</AssemblyName>",   $"<AssemblyName>{gameName}</AssemblyName>");
    csprojContent = Regex.Replace(csprojContent, @"<RootNamespace>[^<]+</RootNamespace>", "<RootNamespace>PakuPort.Generated</RootNamespace>");
    File.WriteAllText(newCsprojPath, csprojContent);

    // 3. Limpiar branding de Spice86 en los .cs
    foreach (string f in Directory.GetFiles(workDir, "*.cs", SearchOption.AllDirectories))
    {
        string src = File.ReadAllText(f);
        string s2  = Regex.Replace(src,  @"^//[^\n]*[Ss]pice86[^\n]*\n", "", RegexOptions.Multiline);
        string s3  = Regex.Replace(s2,   @"\bSpice86\.Generated\b", "PakuPort.Generated");
        // Corregir la string de OverrideSupplier: tras s3 el assembly name es "PakuPort.Generated"
        // pero el assembly real se llama gameName (renombrado en el .csproj). Arreglarlo.
        string s4  = Regex.Replace(s3,
            @"""(PakuPort\.Generated\.[^,]+),\s*PakuPort\.Generated""",
            $"\"$1, {gameName}\"");
        if (s4 != src) File.WriteAllText(f, s4);
    }
    Console.WriteLine("  Branding de Spice86 eliminado");

    // 4. dotnet publish
    string publishDir = Path.Combine(workDir, "publish");
    Console.WriteLine("  Compilando con dotnet publish...");
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"publish \"{newCsprojPath}\" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o \"{publishDir}\" --nologo -v q",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
    };
    using var proc = Process.Start(psi) ?? throw new InvalidOperationException("no se pudo arrancar dotnet");
    string stdout = proc.StandardOutput.ReadToEnd();
    string stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();

    if (proc.ExitCode != 0)
    {
        Console.Error.WriteLine("  ERROR en dotnet publish:");
        if (!string.IsNullOrWhiteSpace(stdout)) Console.Error.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
        Console.Error.WriteLine($"  WorkDir conservado para diagnóstico: {workDir}");
        return 3;
    }

    // 5. Copiar el .exe al destino final
    string builtExe = Path.Combine(publishDir, $"{gameName}.exe");
    if (!File.Exists(builtExe))
        builtExe = Directory.GetFiles(publishDir, "*.exe")
            .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("createdump", StringComparison.OrdinalIgnoreCase))
            ?? builtExe;

    if (!File.Exists(builtExe))
    {
        Console.Error.WriteLine($"  ERROR: no encontré {gameName}.exe en {publishDir}");
        return 4;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outExe)) ?? ".");
    File.Copy(builtExe, outExe, overwrite: true);
    try { Directory.Delete(workDir, recursive: true); } catch { }

    long size = new FileInfo(outExe).Length;
    Console.WriteLine($"  OK → {outExe} ({size / 1024 / 1024} MB)");
    return 0;
}

static int CmdPlay(string[] a)
{
    if (a.Length == 0) { Console.Error.WriteLine("uso: pakuport play <exe> [--game-dir <dir>] [--output-dir <dir>]"); return 1; }

    string exe      = a[0];
    string gameDir  = GetOpt(a, "--game-dir")    ?? Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".";
    string outDir   = GetOpt(a, "--output-dir")  ?? Path.Combine("dumps", Path.GetFileNameWithoutExtension(exe));
    string gameName = Path.GetFileNameWithoutExtension(exe);

    Console.WriteLine($"PakuPort play: {gameName}");
    Console.WriteLine("  Paso 1/3: record...");
    int rc = CmdRecord([exe, "--game-dir", gameDir, "--output", outDir]);
    if (rc != 0) return rc;

    Console.WriteLine("  Paso 2/3: build...");
    string outExe = Path.Combine(outDir, $"{gameName}.exe");
    rc = CmdBuild([outDir, "--output", outExe]);
    if (rc != 0) return rc;

    Console.WriteLine($"  Paso 3/3: ejecutando {outExe}...");
    Process.Start(new ProcessStartInfo { FileName = outExe, UseShellExecute = true });
    return 0;
}

static int CmdDoctor(string[] a)
{
    string dir = a.Length > 0 ? a[0] : ".";
    Console.WriteLine($"PakuPort doctor: {dir}");
    bool ok = true;

    string? gp = FindGeneratedProject(dir);
    Check("GeneratedProject/", gp != null, gp ?? "no encontrado"); ok &= gp != null;

    if (gp != null)
    {
        string? csproj = Directory.GetFiles(gp, "*.csproj").FirstOrDefault();
        Check("  .csproj", csproj != null, csproj != null ? Path.GetFileName(csproj) : "no encontrado");
        if (csproj != null)
        {
            bool hasRef = File.ReadAllText(csproj).Contains("Spice86", StringComparison.OrdinalIgnoreCase);
            Check("  referencia Spice86", hasRef, hasRef ? "presente" : "FALTA"); ok &= hasRef;
        }
        int csCount = Directory.GetFiles(gp, "*.cs", SearchOption.AllDirectories).Length;
        Check($"  archivos .cs", csCount > 0, $"{csCount} archivos");
    }

    bool hasDump = File.Exists(Path.Combine(dir, "spice86dumpCfgGeneratedOverrides.cs")) ||
                   Directory.GetDirectories(dir).Any(d => File.Exists(Path.Combine(d, "spice86dumpCfgGeneratedOverrides.cs")));
    Check("spice86dumpCfgGeneratedOverrides.cs", hasDump, hasDump ? "presente" : "no encontrado");
    Check("Spice86.exe (record)", File.Exists(Spice86ExePath),  Spice86ExePath);
    Check("Spice86.csproj (build)", File.Exists(Spice86Csproj), Spice86Csproj);

    Console.WriteLine();
    Console.WriteLine(ok ? "  Estado: LISTO para 'pakuport build'" : "  Estado: PROBLEMAS detectados");
    return ok ? 0 : 1;

    static void Check(string label, bool pass, string detail)
        => Console.WriteLine($"  {(pass ? "[OK]" : "[!!]")} {label}: {detail}");
}

static int CmdCredits()
{
    Console.WriteLine("""
        PakuPort — porteador autónomo de juegos DOS a .NET
        Copyright (c) 2026 PakuPort contributors

        Este producto incluye Spice86 (https://github.com/OpenRakis/Spice86)
        licenciado bajo Apache License, Version 2.0.
        Véase LICENSES/THIRD-PARTY.txt para la atribución completa.
        """);
    return 0;
}

static void PrintPlanDHelp()
{
    Console.WriteLine("""
        PakuPort — porteador autónomo de juegos DOS a .NET

        Comando producto:
          port <RUTA\JUEGO.EXE|.COM> [--out <dir>] [--smoke-ms <n>] [--keys <autokeys>] [--no-smoke] [--no-package]
              Cadena completa en uno: init → dump-image → export (boot-real) →
              copia de datos → dotnet build → smoke headless → package (entrega\).
              Correr desde la raíz del repo (escribe games/<juego>/ y export/<juego>/).
              --keys conduce el smoke Y el probe de descubrimiento con las mismas autokeys
              ('~'=pausa) para liftear el código sólo alcanzable tras input de menú.
              --no-package salta el empaquetado doble-clic final.

          package <game.json|id> [--keys <autokeys>] [--smoke-ms <n>] [--no-smoke]
              Empaqueta export\<id> ya exportado en entrega\<carpeta>\: publish
              self-contained win-x64 + datos + logs\ vacío + LEEME.txt, con smoke del
              exe publicado y respaldo de la entrega previa como _preN.

        Comandos Spice86:
          record <exe> [--game-dir <dir>] [--output <dir>] [--max-minutes <n>]
              Graba una partida con Spice86. Cierra la ventana limpiamente para volcar.

          build <dumps-dir> [--output <path.exe>] [--game-name <name>] [--spice86 <csproj>]
              Construye un .exe standalone desde los dumps de Spice86.

          play <exe> [--game-dir <dir>] [--output-dir <dir>]
              record + build + ejecuta (atajo rápido).

          doctor <dumps-dir>
              Diagnostica si los dumps están listos para build.

          credits
              Muestra atribución legal (Spice86).

        Comandos legacy (requieren --game <game.json>):
          verify/emit/decode/crosscheck/sanity/render/run/survey/export/discover/init
        """);
}

static string? FindGeneratedProject(string dumpsDir)
{
    if (!Directory.Exists(dumpsDir)) return null;
    // caso 1: dumps-dir/GeneratedProject/
    string direct = Path.Combine(dumpsDir, "GeneratedProject");
    if (Directory.Exists(direct) && Directory.GetFiles(direct, "*.csproj").Length > 0) return direct;
    // caso 2: dumps-dir/<sha>/GeneratedProject/ (estructura auto de Spice86)
    foreach (string sub in Directory.GetDirectories(dumpsDir))
    {
        string gp = Path.Combine(sub, "GeneratedProject");
        if (Directory.Exists(gp) && Directory.GetFiles(gp, "*.csproj").Length > 0) return gp;
    }
    return null;
}

static string DetectGameName(string dumpsDir)
{
    foreach (string f in Directory.GetFiles(dumpsDir, "spice86dumpCfgGeneratedOverrides.cs")
        .Concat(Directory.GetDirectories(dumpsDir).SelectMany(d =>
            Directory.GetFiles(d, "spice86dumpCfgGeneratedOverrides.cs"))))
    {
        string first = File.ReadLines(f).FirstOrDefault() ?? "";
        var m = Regex.Match(first, @"//.*?(\w+)\.exe", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
    }
    return Path.GetFileName(Path.GetFullPath(dumpsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToLowerInvariant();
}

static void CopyDirSkipBinObj(string src, string dst)
{
    Directory.CreateDirectory(dst);
    foreach (string f in Directory.GetFiles(src))
        File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
    foreach (string d in Directory.GetDirectories(src))
    {
        string name = Path.GetFileName(d);
        if (name is "bin" or "obj") continue;
        CopyDirSkipBinObj(d, Path.Combine(dst, name));
    }
}

// ═══════════════════════════════════════════════════════════════════════════

// ── TAREA A: tipos del unpack dinámico ──
sealed class DynUnpackResult
{
    public bool Cut;
    public ushort RealCs, RealIp, RealSs, RealSp;
    public byte[]? MemImage;
    public int XOfGhidra0;
    public string Reason = "";
    public string Detail = "";
}

// Señal interna: el flujo saltó far hacia abajo (al programa descomprimido). Propaga fuera
// de Interp.Run (que solo captura DivideByZeroException) hasta TryDynamicUnpack.
sealed class UnpackCutSignal : Exception
{
    public readonly ushort Cs, Ip;
    public UnpackCutSignal(ushort cs, ushort ip) { Cs = cs; Ip = ip; }
}

// Señal interna: síntoma de "programa real" antes del corte => el EXE no estaba empaquetado.
sealed class UnpackAbortSignal : Exception
{
    public UnpackAbortSignal(string msg) : base(msg) { }
}
