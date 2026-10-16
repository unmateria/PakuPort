using PakuPort.Core;
using PakuPort.Core.Verify;
using PakuPort.Judge.Corpus;
using System.Text;
using System.Text.Json;

/// <summary>
/// `pakuport expediente tablero` — EL TABLERO DEL REMAKE.
///
/// <para><b>Qué es.</b> Una carpeta por función del juego, con un esqueleto de C# cuya cabecera
/// lleva TODO lo que PakuPort midió de esa función mientras el juego corría: su capa con la prueba,
/// las escenas en las que vive, qué parte de su código se ha ejecutado de verdad, la convención de
/// llamada observada, las globales que toca, a qué llama (y cómo se llama eso desde C# sin haber
/// portado nada más), y la orden exacta para preguntarle al binario original. Quien la rellena
/// escribe <c>respuesta.cs</c> al lado, y el juez que ya existe la mide contra el juego real.</para>
///
/// <para><b>Por qué un tablero y no un montón de notas.</b> La salida es un banco compatible con
/// <c>pakuport banco juzga</c> byte por byte: el mismo <c>banco.json</c>, el mismo
/// <c>respuesta.cs</c>, el mismo juez. Así el avance del remake es un número que se saca con una
/// orden — cuántas funciones verdes de cuántas — en vez de una impresión.</para>
///
/// <para><b>Regla heredada del día 3, y es la que manda aquí.</b> Cada dato dice de dónde sale.
/// Lo "observado" viene de invocaciones reales y ningún caso lo contradice; lo leído del cuerpo se
/// marca como leído del cuerpo. Un dato inventado la IA se lo cree, y entonces el expediente
/// resta en vez de sumar.</para>
/// </summary>
static class Tablero
{
    public static int Uso()
    {
        Console.Error.WriteLine(
            "pakuport expediente tablero --game <game.json> --corpus <dir> [--out <dir>]\n" +
            "    Genera el tablero del remake: una carpeta por funcion con su esqueleto C# y\n" +
            "    todo lo medido en la cabecera. Por defecto sale en games/<id>/expediente.\n" +
            "    Opcional: --capas/--escenas/--frontera <json> (por defecto, los de games/<id>).\n" +
            "    El avance se mide con: pakuport banco juzga --banco <dir>");
        return 1;
    }

    static string? Opt(string[] a, string n)
    {
        int i = Array.IndexOf(a, n);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    /// <summary>Lo que se sabe de una función además de su cuerpo: sale de los tres ficheros que
    /// generaron el día 4 (capas, escenas, frontera). Cada campo puede faltar, y cuando falta se
    /// dice en la cabecera con la receta para capturarlo — un hueco declarado vale más que un
    /// dato inventado.</summary>
    sealed class Extras
    {
        public string? Capa, Prueba;
        public long Eventos;
        public List<(int escena, int muestras, double t0, double t1)> Escenas = new();
        public int BytesRango, BytesEjecutados;
        public bool RangoFiable;
        public bool TieneFrontera;
        /// <summary>La unidad grabada es una VUELTA de bucle y no una llamada: entra por la
        /// cabecera del bucle y sale al volver a ella con la misma pila. Sale del `indice.json`
        /// del corpus, que es quien lo sabe.</summary>
        public bool EsVuelta;
        /// <summary>Bytes del cuerpo del bucle, si es una vuelta. Es su tamano DE VERDAD, y no
        /// tiene nada que ver con el "rango" de la frontera, que va hasta la entrada siguiente:
        /// dentro del cuerpo de un bucle viven otras entradas del tablero.</summary>
        public int CuerpoDelBucle;
        /// <summary>De los casos de esta vuelta, cuantos terminan SALIENDO del bucle. Son los que
        /// miden la condicion de salida; si son cero, esa parte no tiene red y hay que decirlo.</summary>
        public int CasosQueSalen;
    }

    /// <summary>Lee del `indice.json` del corpus que es cada carpeta: una LLAMADA o una VUELTA de
    /// bucle. Sin esto el tablero le escribiria a una vuelta que "entra con la direccion de
    /// retorno ya en la pila" y que "sale con ret", que es falso de cabo a rabo.</summary>
    static int CargaUnidades(string corpusDir, Dictionary<string, Extras> d)
    {
        string p = Path.Combine(corpusDir, "indice.json");
        if (!File.Exists(p)) return 0;
        int n = 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (!doc.RootElement.TryGetProperty("funciones", out var arr)) return 0;
            foreach (var e in arr.EnumerateArray())
            {
                if (!e.TryGetProperty("func", out var fn)) continue;
                if (!e.TryGetProperty("unidad", out var u) || u.GetString() != "vuelta") continue;
                var x = Dame(d, fn.GetString()!);
                x.EsVuelta = true;
                x.CuerpoDelBucle = e.TryGetProperty("alcance_del_bucle", out var al) ? al.GetInt32() : 0;
                x.CasosQueSalen = e.TryGetProperty("casos_que_salen_del_bucle", out var sa) ? sa.GetInt32() : 0;
                n++;
            }
        }
        catch (JsonException) { }
        return n;
    }

    public static int Ejecutar(string[] a)
    {
        string? gamePath = Opt(a, "--game"), corpusDir = Opt(a, "--corpus");
        if (gamePath == null || corpusDir == null) return Uso();
        if (!Directory.Exists(corpusDir)) { Console.Error.WriteLine($"no existe el corpus {corpusDir}"); return 2; }

        var game = GameConfig.Load(gamePath);
        string gameDir = Path.GetDirectoryName(Path.GetFullPath(gamePath))!;
        string outDir = Opt(a, "--out") ?? Path.Combine(gameDir, "expediente");
        string capasPath = Opt(a, "--capas") ?? Path.Combine(gameDir, "capas.json");
        string escenasPath = Opt(a, "--escenas") ?? Path.Combine(gameDir, "escenas-funciones.json");
        string fronteraPath = Opt(a, "--frontera") ?? Path.Combine(gameDir, "frontera.json");

        string mundoPath = Path.Combine(corpusDir, "mundo.bin");
        if (!File.Exists(mundoPath)) { Console.Error.WriteLine($"falta {mundoPath}"); return 2; }
        var mundoBytes = File.ReadAllBytes(mundoPath);

        var extras = new Dictionary<string, Extras>(StringComparer.Ordinal);
        int conCapa = CargaCapas(capasPath, extras);
        int conEscena = CargaEscenas(escenasPath, extras);
        int conFrontera = CargaFrontera(fronteraPath, extras);
        int vueltas = CargaUnidades(corpusDir, extras);

        Directory.CreateDirectory(outDir);
        string gameRel = Rel(gamePath);
        var filas = new List<object>();
        var funcs = new List<(string func, int casos, int insn, ushort cs, ushort ip, bool retf, bool bucle)>();
        int sinCasos = 0, conNombre = 0, nombreAlta = 0;

        // PRIMERA PASADA: donde entra cada unidad. Hace falta ANTES de escribir ninguna ficha,
        // porque el cuerpo de una vuelta de bucle contiene otras entradas del tablero y eso hay
        // que decirlo en la suya. Se lee un solo caso por carpeta, no el corpus entero.
        var entradas = new List<(string func, ushort cs, ushort ip)>();
        foreach (var d in Directory.GetDirectories(corpusDir).OrderBy(x => x, StringComparer.Ordinal))
        {
            string fn = Path.GetFileName(d);
            if (!fn.StartsWith("FUN_", StringComparison.Ordinal)) continue;
            var uno = FunctionVerifier.LoadCorpus(corpusDir, fn, 1);
            if (uno.Count == 0) continue;
            entradas.Add((fn, uno[0].EntryCpu.Cs, (ushort)uno[0].EntryCpu.Eip));
        }

        foreach (var d in Directory.GetDirectories(corpusDir).OrderBy(x => x, StringComparer.Ordinal))
        {
            string func = Path.GetFileName(d);
            if (!func.StartsWith("FUN_", StringComparison.Ordinal)) continue;
            var casos = FunctionVerifier.LoadCorpus(corpusDir, func);
            if (casos.Count == 0) { sinCasos++; continue; }

            var e0 = casos[0].EntryCpu;
            var cuerpo = BancoCiego.DesensamblaInsn(mundoBytes, e0.Cs, e0.Ip);
            var ficha = Expediente.Observa(cuerpo, casos);
            var asm = BancoCiego.Desensambla(mundoBytes, e0.Cs, e0.Ip);
            extras.TryGetValue(func, out var ex);
            var nombre = NombreConEvidencia.Propone(cuerpo, ficha, ex?.Capa, casos[0].EntryMem);
            var sueltos = CambiosNoExplicados(casos, ficha);
            if (nombre.Nombre != null) { conNombre++; if (nombre.Confianza == "alta") nombreAlta++; }

            string dir = Path.Combine(outDir, func);
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "desensamblado.asm"), asm);
            File.WriteAllText(Path.Combine(dir, "PLANTILLA.cs"),
                Plantilla(func, e0.Cs, e0.Ip, ficha, ex, gameRel, Rel(corpusDir), asm.Count,
                          File.Exists(escenasPath), File.Exists(fronteraPath), nombre, sueltos,
                          DentroDelCuerpo(ex, e0.Ip, entradas)));

            funcs.Add((func, casos.Count, cuerpo.Count, e0.Cs, e0.Ip, ficha.RetLejano, ex?.EsVuelta == true));
            filas.Add(Fila(func, e0.Cs, e0.Ip, ficha, ex, casos.Count, cuerpo.Count, gameRel, Rel(corpusDir), nombre, sueltos));
        }

        if (funcs.Count == 0) { Console.Error.WriteLine("el corpus no tiene ninguna funcion con casos"); return 2; }

        // LAS UNIDADES SIN CASOS TAMBIEN SON DEL TABLERO. El grabador las vio (un `call` a ellas o
        // un salto hacia atras) pero ninguna invocacion cupo en un caso: el bucle principal que se
        // llama una vez y no vuelve, la partida entera, una funcion que siempre sale por un atajo.
        // Sin carpeta, nadie las podia traducir, y las que SI tienen casos y las llaman no tenian a
        // quien llamar en C#. Con carpeta se traducen, se compilan con las demas y viajan al
        // juego; lo que no hay es juicio por casos: se juzgan partida contra partida
        // (tools/control_portadas.py), y la ficha lo dice arriba del todo.
        var sinCasosAnadidas = new List<string>();
        string indicePath = Path.Combine(corpusDir, "indice.json");
        if (File.Exists(indicePath))
        {
            using var idx = JsonDocument.Parse(File.ReadAllText(indicePath));
            // offset del corpus -> lineal: se saca de una unidad CON casos, que trae su CS:IP
            var ancla = funcs[0];
            int delta = ancla.cs * 16 + ancla.ip - Convert.ToInt32(ancla.func[9..], 16);
            foreach (var u in idx.RootElement.GetProperty("funciones").EnumerateArray())
            {
                string func = u.GetProperty("func").GetString()!;
                if (u.GetProperty("casos").GetInt32() > 0 || funcs.Any(f => f.func == func)) continue;
                int lineal = Convert.ToInt32(func[9..], 16) + delta;
                var seg = funcs.Select(f => f.cs).FirstOrDefault(c => lineal >= c * 16 && lineal < c * 16 + 0x10000);
                if (seg == 0) continue;
                ushort ipU = (ushort)(lineal - seg * 16);
                // Un corpus de antes de las vueltas de bucle no trae "unidad": todo son llamadas.
                bool esVuelta = u.TryGetProperty("unidad", out var un) && un.GetString() == "vuelta";
                string porque = u.TryGetProperty("sin_casos_porque", out var sp) ? sp.GetString() ?? "" : "";
                AnadeSinCasos(outDir, func, seg, ipU, esVuelta, porque, mundoBytes);
                funcs.Add((func, 0, 0, seg, ipU, false, esVuelta));
                sinCasosAnadidas.Add(func);
            }
        }

        // Y EL PROGRAMA: la primera instruccion del juego. No la llama nadie ni es la cabecera de
        // ningun bucle, asi que el grabador no la ve; pero para portar un juego ENTERO hace falta
        // una traduccion que empiece ahi, o lo que corre desde el punto de entrada lo seguira
        // haciendo el interprete. Se registra como una vuelta que no vuelve nunca.
        var entrada = EntradaDelPrograma(game);
        if (entrada is (ushort pcs, ushort pip))
        {
            AnadeSinCasos(outDir, "PROGRAMA", pcs, pip, true,
                "es el punto de entrada del programa: empieza una vez y no vuelve nunca", mundoBytes);
            funcs.Add(("PROGRAMA", 0, 0, pcs, pip, false, true));
        }

        var opciones = new JsonSerializerOptions { WriteIndented = true };

        // El MISMO banco.json que consume `banco juzga`: el tablero es un banco, y el avance del
        // remake se mide con el juez que ya existe en vez de con uno nuevo.
        File.WriteAllText(Path.Combine(outDir, "banco.json"), JsonSerializer.Serialize(new
        {
            juego = game.Id,
            corpus = Rel(corpusDir),
            origen = "pakuport expediente tablero",
            criterio = new
            {
                seleccion = "TODAS las funciones del corpus con al menos un caso grabado",
                juez = "pakuport banco juzga --banco " + Rel(outDir),
                nota = "el juez pasa antes el binario ORIGINAL por el mismo aro: lo que ni el " +
                       "original reproduce sale INJUZGABLE y no cuenta como fallo de nadie",
            },
            // `retf` viaja aqui porque quien vuelve de una traduccion necesita saber si el marco
            // de retorno lleva CS: lo usa el registro que genera `export --portadas`.
            // `bucle` dice que la unidad es una VUELTA y no una llamada. Lo mira `export
            // --portadas`: el gancho de las funciones portadas continua en la direccion de retorno
            // que hay en la pila, y una vuelta no tiene ninguna — enchufar una descarrilaria el
            // juego en silencio.
            funciones = funcs.Select(f => new { func = f.func, casos = f.casos, instrucciones = f.insn,
                                               cs = $"0x{f.cs:X4}", ip = $"0x{f.ip:X4}", retf = f.retf,
                                               bucle = f.bucle, programa = f.func == "PROGRAMA" }).ToArray(),
        }, opciones));

        File.WriteAllText(Path.Combine(outDir, "expediente.json"), JsonSerializer.Serialize(new
        {
            juego = game.Id,
            generado = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            comando = $"pakuport expediente tablero --game {gameRel} --corpus {Rel(corpusDir)}",
            corpus = Rel(corpusDir),
            fuentes = new
            {
                capas = File.Exists(capasPath) ? Rel(capasPath) : null,
                escenas = File.Exists(escenasPath) ? Rel(escenasPath) : null,
                frontera = File.Exists(fronteraPath) ? Rel(fronteraPath) : null,
            },
            cobertura = new
            {
                funciones = funcs.Count,
                con_capa = conCapa,
                con_escena = conEscena,
                con_frontera = conFrontera,
                con_nombre = conNombre,
                con_nombre_de_confianza_alta = nombreAlta,
                sin_casos_descartadas = sinCasos,
                sin_casos_con_carpeta = sinCasosAnadidas,
            },
            funciones = filas,
        }, opciones));

        File.WriteAllText(Path.Combine(outDir, "COMO-ESCRIBIR.md"),
            "# Como se escribe una traduccion\n\n" +
            "Vale para cualquier funcion de este tablero: el contrato de entrega, la API de la CPU\n" +
            "y de la memoria, que hacer con la pila, y como llamar a lo que todavia no esta portado.\n\n" +
            BancoCiego.ComoEscribir());

        File.WriteAllText(Path.Combine(outDir, "LEEME.md"),
            Leeme(game.Id, gameRel, Rel(corpusDir), Rel(outDir), funcs.Count, conCapa, conEscena, conFrontera, conNombre, nombreAlta));

        int nVueltas = funcs.Count(f => f.bucle);
        Console.WriteLine($"tablero: {funcs.Count} unidades ({funcs.Count - nVueltas} llamadas, " +
                          $"{nVueltas} vueltas de bucle) -> {outDir}");
        // La cobertura de los nombres se PUBLICA aunque sea baja: la mayoria de las funciones
        // de un juego son logica pura y no tocan ningun servicio del sistema, asi que no hay de
        // donde sacarles un nombre sin inventarlo. Un hueco declarado vale mas que un relleno.
        Console.WriteLine($"  nombre con prueba: {conNombre} ({nombreAlta} de confianza alta)");
        Console.WriteLine($"  capa: {conCapa}  escena: {conEscena}  frontera: {conFrontera}" +
                          (sinCasos > 0 ? $"  (descartadas sin casos: {sinCasos})" : ""));
        Console.WriteLine($"  avance: pakuport banco juzga --banco {Rel(outDir)}");
        return 0;
    }

    static string Rel(string p)
    {
        string full = Path.GetFullPath(p), cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        return full.StartsWith(cwd, StringComparison.OrdinalIgnoreCase)
            ? full[(cwd.Length + 1)..].Replace('\\', '/') : full.Replace('\\', '/');
    }

    // ==========================================================================================
    //  LO QUE MIDIERON LOS TRES FICHEROS DEL DIA 4
    // ==========================================================================================

    static int CargaCapas(string path, Dictionary<string, Extras> dst)
    {
        if (!File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("funciones", out var fs)) return 0;
        int n = 0;
        foreach (var p in fs.EnumerateObject())
        {
            var e = Dame(dst, p.Name);
            e.Capa = p.Value.TryGetProperty("capa", out var c) ? c.GetString() : null;
            e.Prueba = p.Value.TryGetProperty("prueba", out var pr) ? pr.GetString() : null;
            e.Eventos = p.Value.TryGetProperty("eventos", out var ev) ? ev.GetInt64() : 0;
            n++;
        }
        return n;
    }

    static int CargaEscenas(string path, Dictionary<string, Extras> dst)
    {
        if (!File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("escenas", out var es)) return 0;
        var vistas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var esc in es.EnumerateArray())
        {
            int num = esc.GetProperty("escena").GetInt32();
            double t0 = esc.GetProperty("t_inicio_ms").GetDouble(), t1 = esc.GetProperty("t_fin_ms").GetDouble();
            if (!esc.TryGetProperty("funciones", out var fs)) continue;
            foreach (var f in fs.EnumerateArray())
            {
                string nombre = f.GetProperty("func").GetString()!;
                Dame(dst, nombre).Escenas.Add((num, f.GetProperty("muestras").GetInt32(), t0, t1));
                vistas.Add(nombre);
            }
        }
        return vistas.Count;
    }

    static int CargaFrontera(string path, Dictionary<string, Extras> dst)
    {
        if (!File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("funciones", out var fs)) return 0;
        int n = 0;
        foreach (var f in fs.EnumerateArray())
        {
            var e = Dame(dst, f.GetProperty("func").GetString()!);
            e.BytesRango = f.GetProperty("bytes_rango").GetInt32();
            e.BytesEjecutados = f.GetProperty("bytes_ejecutados").GetInt32();
            e.RangoFiable = f.TryGetProperty("rango_fiable", out var rf) && rf.GetBoolean();
            e.TieneFrontera = true;
            n++;
        }
        return n;
    }

    static readonly System.Globalization.CultureInfo Es =
        System.Globalization.CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Las otras entradas del tablero que caen DENTRO del cuerpo de este bucle. Sin
    /// esto, la ficha de una vuelta de 860 bytes no dice que la mitad de lo que hace ya tiene su
    /// propia carpeta al lado.</summary>
    static List<string> DentroDelCuerpo(Extras? ex, ushort ip, List<(string func, ushort cs, ushort ip)> todas)
    {
        var res = new List<string>();
        if (ex?.EsVuelta != true || ex.CuerpoDelBucle <= 0) return res;
        int fin = ip + ex.CuerpoDelBucle;
        foreach (var e in todas)
            if (e.ip > ip && e.ip <= fin) res.Add(e.func.Replace("FUN_1000_", ""));
        return res;
    }

    static Extras Dame(Dictionary<string, Extras> d, string k)
    {
        if (!d.TryGetValue(k, out var e)) d[k] = e = new Extras();
        return e;
    }

    // ==========================================================================================
    //  LA CABECERA: todo lo medido, cada cosa con su procedencia
    // ==========================================================================================

    /// <summary>Donde empieza el programa: un .COM en <c>PSP:0100</c>; un MZ donde diga su
    /// cabecera, reubicado sobre el segmento de carga.</summary>
    static (ushort cs, ushort ip)? EntradaDelPrograma(GameConfig game)
    {
        if (game.Format == "com") return (game.LoadSegment, 0x0100);
        if (!File.Exists(game.ExePath)) return null;
        var b = File.ReadAllBytes(game.ExePath);
        if (b.Length < 0x18 || b[0] != (byte)'M' || b[1] != (byte)'Z') return null;
        ushort ip = (ushort)(b[0x14] | (b[0x15] << 8));
        ushort cs = (ushort)(b[0x16] | (b[0x17] << 8));
        return ((ushort)(game.LoadSegment + 0x10 + cs), ip);
    }

    /// <summary>La carpeta de una unidad SIN casos: su desensamblado y una plantilla que dice, lo
    /// primero, que aqui no hay juicio por casos y por que.</summary>
    static void AnadeSinCasos(string outDir, string func, ushort cs, ushort ip, bool vuelta, string porque,
                              byte[] mundo)
    {
        string dir = Path.Combine(outDir, func);
        Directory.CreateDirectory(dir);
        var asm = BancoCiego.Desensambla(mundo, cs, ip);
        File.WriteAllLines(Path.Combine(dir, "desensamblado.asm"), asm);
        var sb = new StringBuilder();
        sb.AppendLine("// ==========================================================================");
        sb.AppendLine($"//  {func}  —  entra en {cs:X4}:{ip:X4}" + (vuelta ? "  (VUELTA DE BUCLE)" : ""));
        sb.AppendLine("// ==========================================================================");
        sb.AppendLine("//");
        sb.AppendLine("// ESTA UNIDAD NO TIENE CASOS: " + porque + ".");
        sb.AppendLine("//   El juez no la puede medir invocacion a invocacion. Se traduce igual —si no, lo");
        sb.AppendLine("//   que hace lo seguiria haciendo el interprete—, se compila con las demas y viaja al");
        sb.AppendLine("//   juego, y se juzga PARTIDA CONTRA PARTIDA: la misma cinta con y sin traducciones,");
        sb.AppendLine("//   comparando memoria y pantalla (tools/control_portadas.py).");
        sb.AppendLine("//");
        if (vuelta)
        {
            sb.AppendLine("// LO QUE DEVUELVE: `ushort`, la direccion por la que SIGUE el programa. Si no vuelve");
            sb.AppendLine("//   nunca, da igual: el gancho no llega a mirarlo.");
        }
        else
            sb.AppendLine("// Es una FUNCION: entra con la direccion de retorno en la pila y sale con su `ret`.");
        sb.AppendLine("//");
        sb.AppendLine("// EL RITMO: cobra con `o.Instrucciones(n)` lo que habria gastado el original, igual");
        sb.AppendLine("//   que las demas (COMO-ESCRIBIR.md). Aqui nadie lo mide caso a caso, pero el juego sin");
        sb.AppendLine("//   ritmo iria a otra velocidad y una espera al reloj no acabaria nunca.");
        sb.AppendLine("//");
        sb.AppendLine($"// EL CUERPO ORIGINAL: desensamblado.asm, aqui al lado ({asm.Count} lineas).");
        sb.AppendLine("//");
        sb.AppendLine("using PakuPort.Core;");
        sb.AppendLine("using PakuPort.Core.Verify;");
        sb.AppendLine();
        sb.AppendLine("public static class Respuesta");
        sb.AppendLine("{");
        sb.AppendLine(vuelta
            ? "    public static ushort Ejecuta(Cpu c, Memory m, Original o)"
            : "    public static void Ejecuta(Cpu c, Memory m, Original o)");
        sb.AppendLine("    {");
        sb.AppendLine("        throw new System.NotImplementedException();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "PLANTILLA.cs"), sb.ToString());
    }

    static string Plantilla(string func, ushort cs, ushort ip, Expediente.Ficha f, Extras? ex,
                            string gameRel, string corpusRel, int lineasAsm,
                            bool hayEscenas, bool hayFrontera, NombreConEvidencia.Propuesta nom,
                            List<string> cambiosSueltos, List<string> dentro)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s.Length > 0 ? "// " + s : "//");

        bool vuelta = ex?.EsVuelta == true;
        sb.AppendLine("// ==========================================================================");
        sb.AppendLine($"//  {func}  —  entra en {cs:X4}:{ip:X4}" + (vuelta ? "  (VUELTA DE BUCLE)" : ""));
        sb.AppendLine("// ==========================================================================");
        L();
        if (vuelta)
        {
            L("ESTO NO ES UNA FUNCION: ES UNA VUELTA DE BUCLE.");
            L($"  {cs:X4}:{ip:X4} es la cabecera de un bucle, no el destino de ningun `call`. La unidad");
            L("  que se graba y se juzga aqui es UNA ITERACION: entra por la cabecera y termina");
            L("  cuando el programa vuelve a ella —o SALE del bucle— con la MISMA pila. No hay");
            L("  direccion de retorno en la pila y no hay `ret` que dar.");
            if (ex!.CasosQueSalen > 0)
                L(ex.CasosQueSalen == 1
                    ? $"  De tus {f.Casos} casos, 1 sale del bucle en vez de volver: es el que mide"
                    : $"  De tus {f.Casos} casos, {ex.CasosQueSalen} salen del bucle en vez de volver: esos son los que");
            if (ex.CasosQueSalen > 0)
                L(ex.CasosQueSalen == 1 ? "  que aciertes la condicion de salida." : "  miden que aciertes la condicion de salida.");
            else
                L("  ⚠ Ninguno de tus casos SALE del bucle: esta partida no cubrio la condicion de salida,");
            if (ex.CasosQueSalen == 0)
                L("  asi que ahi no tienes red y hay que traducirla con cuidado.");
            if (ex!.CuerpoDelBucle > 0)
                L($"  El cuerpo del bucle son {ex.CuerpoDelBucle} bytes ({ip:X4}..{ip + ex.CuerpoDelBucle:X4}), y DENTRO de el");
            L("  pueden vivir otras entradas de este tablero: bucles anidados y funciones a las que");
            L("  llama. Lo que traduzcas aqui es la vuelta entera, con todo lo que haga por el camino.");
            if (dentro.Count > 0)
            {
                L("  Dentro de ese cuerpo hay " + dentro.Count + " entrada(s) mas de este tablero: " +
                  string.Join(" ", dentro));
                L("  Si las traduces tambien, esta vuelta puede llamarlas en vez de repetir su codigo:");
                L("  el juez compila juntas todas las respuestas del tablero, cada una en su namespace,");
                L($"  asi que desde aqui `PakuPort.Remake.FUN_1000_xxxx.Respuesta.Ejecuta(c, m, o)` corre");
                L("  la otra. Si es una vuelta, devuelve por donde sigue: dala hasta que salga, p. ej.");
                L("  `ushort sig; do sig = Interior.Ejecuta(c, m, o); while (sig == CABECERA);`.");
            }
            L();
            L("  Esta unidad existe porque en un juego escrito en ensamblador la mayor parte del");
            L("  trabajo NO ocurre dentro de una llamada que retorne: el bucle principal se llama una");
            L("  vez y no vuelve nunca, y lo que corre desde el punto de entrada no lo llama nadie.");
            L("  Sin la vuelta, ese codigo no tendria ni un solo caso de prueba. Cuanto cubre en este");
            L("  juego lo dice el cuadro de `pakuport expediente capas`.");
            L();
            L("  LO QUE DEVUELVE: `ushort`, la direccion por la que SIGUE el programa — la cabecera");
            L($"  (0x{ip:X4}) si toca otra vuelta, o el sitio por el que se sale del bucle. El juez la");
            L("  compara con la grabada, y dentro del juego el interprete continua exactamente ahi:");
            L("  si devuelves la cabecera, la vuelta siguiente tambien la hace tu C#.");
            L();
        }
        L("Lo que sigue lo MIDIO PakuPort viendo correr el juego. Lo que pone \"observado\" sale de");
        L($"{f.Casos} invocaciones reales grabadas y ningun caso lo contradice; lo que pone \"leido del");
        L("cuerpo\" sale del desensamblado y puede llevar codigo que esa partida no ejecuto.");
        L();

        // EL NOMBRE VA PRIMERO Y CON SU PRUEBA. Un nombre sin prueba es peor que ninguno: se
        // lee como un hecho y quien traduce lo usa sin verificarlo. Por eso, cuando no hay
        // evidencia, aqui no aparece un nombre inventado sino la frase que dice que no la hay.
        L("COMO SE LLAMARIA (propuesto por lo que HACE, no por lo que parece)");
        if (nom.Nombre != null)
        {
            L($"  {nom.Nombre}   [confianza {nom.Confianza}]");
            L("  La prueba, para que no haya que creerselo:");
        }
        else L("  SIN NOMBRE: no hay ninguna prueba de que hace. Es la respuesta honesta.");
        foreach (var ev in nom.Evidencia) L("    - " + ev);
        L();

        L("QUE HACE");
        if (ex?.Capa != null)
        {
            L($"  Capa: {ex.Capa.ToUpperInvariant()}" + (ex.Eventos > 0 ? $"  ({ex.Eventos.ToString("N0", Es)} eventos medidos)" : ""));
            if (!string.IsNullOrEmpty(ex.Prueba)) L($"  Prueba: {ex.Prueba}");
        }
        else L("  Capa: sin medir. Se saca corriendo el juego con PAKU_CAPAS=<csv> y luego");
        if (ex?.Capa == null) L("        `pakuport expediente capas`.");

        if (ex != null && ex.Escenas.Count > 0)
        {
            var top = ex.Escenas.OrderByDescending(e => e.muestras).Take(4).ToList();
            L("  Vive en " + (ex.Escenas.Count == 1 ? "la escena " : "las escenas ") +
              string.Join(", ", top.Select(e => $"{e.escena} ({e.t0 / 1000:0.0}-{e.t1 / 1000:0.0} s, {e.muestras} muestra{(e.muestras == 1 ? "" : "s")})")) +
              (ex.Escenas.Count > top.Count ? $" y {ex.Escenas.Count - top.Count} mas" : ""));
        }
        else if (hayEscenas) L("  Escena: no aparecio en el muestreo de esta partida.");
        else
        {
            L("  Escena: sin medir. Se saca de un pcsample.log del juego con");
            L("          `python tools/expediente_escenas.py`.");
        }

        if (ex?.TieneFrontera == true && ex.BytesRango > 0)
        {
            double pct = 100.0 * ex.BytesEjecutados / ex.BytesRango;
            L($"  Codigo ejecutado de verdad: {ex.BytesEjecutados} de {ex.BytesRango} bytes ({pct:0.0} %)" +
              (ex.RangoFiable ? "" : " [rango = COTA SUPERIOR: puede llevar datos o codigo de otra funcion]"));
            if (vuelta && ex.CuerpoDelBucle > ex.BytesRango)
                L($"  OJO: ese rango llega solo hasta la entrada siguiente del tablero. El CUERPO del" +
                  $" bucle son {ex.CuerpoDelBucle} bytes,");
            if (vuelta && ex.CuerpoDelBucle > ex.BytesRango)
                L("  y el resto esta repartido entre las entradas que viven dentro de el.");
            if (pct < 99.9)
                L("  Lo que falta hasta el 100 % NO se ha visto correr: traducelo con cuidado o pregunta al original.");
        }
        else if (!hayFrontera)
        {
            L("  Codigo ejecutado: sin medir. Se saca corriendo el juego con PAKU_COBERTURA=<csv>");
            L("  y luego `python tools/expediente_frontera.py`. Mientras no este, no se sabe que");
            L("  parte del cuerpo de abajo llego a ejecutarse nunca.");
        }
        L();

        L($"CONVENCION DE LLAMADA (observado en {f.Casos} invocaciones)");
        L("  Conserva (sale igual que entro en todos los casos): " +
          (f.Preservados.Count > 0 ? string.Join(" ", f.Preservados) : "ninguno"));
        L("  Cambia: " + (f.Cambian.Count > 0 ? string.Join(" ", f.Cambian) : "ninguno"));
        if (vuelta)
            L("  No sale con `ret`: termina al volver a la cabecera con la pila como entro.");
        else if (f.RetN >= 0)
            L($"  Sale con {(f.RetLejano ? "retf" : "ret")}{(f.RetN > 0 ? " " + f.RetN : "")}: " +
              (f.RetN > 0 ? $"quita {f.RetN} bytes de argumentos de la pila."
                          : "los argumentos los limpia quien llama."));
        L();

        if (f.Args.Count > 0)
        {
            L("ARGUMENTOS EN LA PILA (donde, leido del cuerpo; valores, observados)");
            foreach (var g in f.Args)
                L($"  [bp+{g.Off:X}h] {(g.PunteroFar ? "4 bytes (PUNTERO FAR: se carga con lds/les)" : g.Ancho + " bytes")}" +
                  (g.Valores.Count > 0 ? "  visto: " + string.Join(", ", g.Valores.Distinct().Take(6)) : ""));
            L();
        }

        if (f.Fijas.Count > 0)
        {
            L("GLOBALES QUE TOCA (el segmento va siempre escrito; `A->B` = la cambio en ese caso)");
            foreach (var x in f.Fijas.Take(24))
                L($"  {x.Seg}:[{x.Dir:X4}h] {x.Ancho} bytes {(x.Escrita ? "ESCRITA" : "solo lee")}" +
                  (x.Valores.Count > 0 ? "  visto: " + string.Join(", ", x.Valores.Distinct().Take(5)) : ""));
            if (f.Fijas.Count > 24) L($"  ... y {f.Fijas.Count - 24} mas (estan en expediente.json)");
            L();
        }

        var llamadas = f.Llamadas.DistinctBy(l => (l.Tipo, l.Off, l.Seg, l.N, l.Puerto)).ToList();
        if (llamadas.Count > 0)
        {
            if (cambiosSueltos.Count > 0)
        {
            L("LO QUE CAMBIA EN MEMORIA Y NO SALE DEL CUERPO (medido en los casos)");
            L("  El juez compara los 64 KB enteros, asi que esto TAMBIEN se exige. No lo escribe el"); 
            L("  cuerpo de arriba: lo escribe algo a lo que esta funcion llama, o una interrupcion.");
            foreach (var s in cambiosSueltos) L("    " + s);
            L();
        }

        L("A QUE LLAMA, Y COMO SE LLAMA ESO DESDE AQUI");
            L("  No hace falta haber portado nada de lo de abajo: lo que aun no este traducido lo");
            L("  sigue haciendo el binario original, interpretado, y eso ES el original byte a byte.");
            foreach (var l in llamadas.Take(24))
                L("  " + l.Tipo switch
                {
                    "near" => $"{l.En:X4}: call {l.Off:X4}h    ->  o.Llama(0x{l.Off:X4}, 0x{l.Sig:X4})",
                    "far" => $"{l.En:X4}: call far {l.Seg:X4}:{l.Off:X4}  ->  o.LlamaLejos(0x{l.Seg:X4}, 0x{l.Off:X4})",
                    "int" => $"{l.En:X4}: int {l.N:X2}h    ->  o.Interrupcion(0x{l.N:X2})",
                    "in" => $"{l.En:X4}: in {l.Puerto}    ->  o.LeePuerto8({l.Puerto})  (o LeePuerto16)",
                    "out" => $"{l.En:X4}: out {l.Puerto}    ->  o.EscribePuerto8({l.Puerto}, valor)",
                    _ => $"{l.En:X4}: call INDIRECTA (el destino esta en un registro o en memoria):" +
                         " calculalo igual que el original y usa o.Llama(destino)",
                });
            if (llamadas.Count > 24) L($"  ... y {llamadas.Count - 24} mas");
            L("  OJO: durante el juicio los efectos se devuelven EN ORDEN. Pedir uno de mas, o en");
            L("  otro orden, sale rojo — y es lo correcto: no seria equivalente al original.");
            L();
        }

        L("LA PILA, QUE ES DONDE MAS SE FALLA");
        if (vuelta)
        {
            L("  Una vuelta NO tiene direccion de retorno: entra y sale con el MISMO SP, y asi tiene");
            L("  que dejarlo tu traduccion. Si el cuerpo empuja algo, sacalo — y si llamas a una");
            L("  funcion con o.Llama, esa ya se encarga de su propio marco.");
        }
        else
        {
            L("  La funcion entra con la direccion de retorno YA en la pila. Al terminar deja SP como");
            L($"  lo dejaria el original: c.SP += {(f.RetN > 0 ? 2 + f.RetN : 2)}" +
              (f.RetN > 0 ? $" (ret {f.RetN}: 2 del retorno y {f.RetN} de argumentos)." : " (ret normal)."));
        }
        L("  Si el original empuja y saca registros, hazlo tambien: la pila es memoria y se compara.");
        L();

        L("EL RITMO: CUANTAS INSTRUCCIONES HABRIA GASTADO EL ORIGINAL");
        L("  El reloj del juego ES el contador de instrucciones del invitado: de el salen el tic de");
        L("  la BIOS, las IRQ y la velocidad. Tu C# no gasta instrucciones, asi que las COBRA:");
        L("  `o.Instrucciones(n)` antes de cada `int`/`in`/`out`/llamada (todo lo pendiente hasta");
        L("  ella incluida) y al final de cada bloque lo que quede; `o.Repeticiones(k)` ademas para");
        L("  un `rep` que proceso k elementos. El juez compara la cuenta caso a caso con la del");
        L("  original (\"ritmo EXACTO\"). Sin ritmo, una vuelta que espera un tic del reloj se");
        L("  queda esperando para siempre dentro del juego.");
        L();

        L("LA API, EL CONTRATO DE ENTREGA Y COMO LLAMAR A LO NO PORTADO");
        L("  COMO-ESCRIBIR.md, en la raiz del tablero. Es igual para todas las funciones.");
        L();

        L("EL CUERPO ORIGINAL");
        L($"  desensamblado.asm, aqui al lado ({lineasAsm} lineas).");
        L();

        L("SI DUDAS DE ALGO, PREGUNTALE AL ORIGINAL (te contesta el binario, no un modelo)");
        L($"  pakuport oracle-run --game {gameRel} --func {func} --base 0 --entrada <entrada.json>");
        L("  Le pones la entrada que quieras y ejecuta los BYTES ORIGINALES con su DOS, sus");
        L("  ficheros, su video y sus overlays montados. Con --pfc <fichero> la respuesta sale");
        L("  como un caso mas que el juez consume sin tocar nada.");
        L();

        L("COMO SE JUZGA LO QUE ESCRIBAS");
        L("  Guarda tu version como respuesta.cs EN ESTA MISMA CARPETA y lanza:");
        L("    pakuport banco juzga --banco <la carpeta del tablero>");
        L($"  Se compila y se ejecuta contra las {f.Casos} invocaciones reales de esta funcion: se");
        L("  comparan registros, banderas y memoria. Verde o rojo, sin ojo y sin opinion.");
        // EL LIMITE DEL JUEZ, DICHO DONDE SE LEE. Compara los 64 KB del DGROUP y los registros;
        // la VRAM esta FUERA de esos 64 KB. Medido en un juego de prueba con `FUN_1000_3a65`: una
        // traduccion que pinta cada byte UNA POSICION A LA DERECHA —el sprite sale corrido— sale
        // VERDE 8/8. Quien traduce una funcion de dibujo tiene que saberlo antes, no despues.
        if (ex?.Capa == "Render")
        {
            L("  ⚠ OJO, ESTA FUNCION DIBUJA: el juez compara el DGROUP y los registros, y la VRAM");
            L("    esta FUERA de esos 64 KB. Un VERDE aqui NO dice que pintes lo mismo — medido en");
            L("    un juego de prueba: pintar cada byte una posicion a la derecha sale VERDE 8/8. Comprueba");
            L("    el dibujo aparte (con el juego corriendo) o pregunta al original con oracle-run.");
        }
        L();

        sb.AppendLine("using PakuPort.Core;");
        sb.AppendLine("using PakuPort.Core.Verify;");
        sb.AppendLine();
        sb.AppendLine("public static class Respuesta");
        sb.AppendLine("{");
        sb.AppendLine(vuelta
            ? "    public static ushort Ejecuta(Cpu c, Memory m, Original o)"
            : "    public static void Ejecuta(Cpu c, Memory m, Original o)");
        sb.AppendLine("    {");
        sb.AppendLine(vuelta
            ? "        // TODO: aqui va UNA vuelta del bucle; devuelve por donde sigue el programa."
            : "        // TODO: aqui va la traduccion de la funcion.");
        sb.AppendLine("        // Los registros y la memoria de entrada ya estan puestos en `c` y `m`.");
        sb.AppendLine("        throw new System.NotImplementedException();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }


    /// <summary>LO QUE EL CASO VIO CAMBIAR Y NO SALE DEL CUERPO.
    ///
    /// <para>La tabla de globales de la ficha se saca del DESENSAMBLADO: son las direcciones fijas
    /// que la funcion nombra. Pero el juez compara los 64 KB enteros, asi que exige tambien lo que
    /// cambia por debajo — lo que escribe una funcion a la que esta llama, o una interrupcion que
    /// entro por medio. Quien traduce leyendo solo la tabla de globales no tiene forma de saber que
    /// eso hace falta, y el juez le sale rojo sin decirle donde mirar.</para>
    ///
    /// <para>Se usa el MISMO criterio que el juez (la banda de pila enmascarada con su `spmin`),
    /// para que lo que aparece aqui sea exactamente lo que va a exigirse y no un susto de mas.
    /// Medido en un juego de prueba con `FUN_1000_375c`: el unico tramo que sobra son 2 bytes en
    /// <c>ds:[FEE8]</c>, y sin esta seccion no habia por donde empezar a buscarlo.</para></summary>
    static List<string> CambiosNoExplicados(List<CorpusCase> casos, Expediente.Ficha f)
    {
        var salida = new List<string>();
        var conocidas = new HashSet<int>();
        foreach (var g in f.Fijas)
            for (int k = 0; k < Math.Max(1, g.Ancho); k++) conocidas.Add(g.Dir + k);

        var vistos = new HashSet<string>(StringComparer.Ordinal);
        for (int ci = 0; ci < casos.Count && salida.Count < 8; ci++)
        {
            var caso = casos[ci];
            if (caso.EntryMem.Length == 0 || caso.EntryMem.Length != caso.ExitMem.Length) continue;
            int sp = (int)(caso.EntryCpu.Esp & 0xFFFF);
            int lo = Math.Max(0, sp - PakuPort.Judge.Diff.DiffEngine.StackMaskBelow);
            int hi = Math.Min(caso.ExitMem.Length, sp + PakuPort.Judge.Diff.DiffEngine.StackMaskAbove);

            int ini = -1;
            for (int i = 0; i <= caso.EntryMem.Length; i++)
            {
                bool cambia = i < caso.EntryMem.Length
                              && caso.EntryMem[i] != caso.ExitMem[i]
                              && !(i >= lo && i < hi)
                              && !conocidas.Contains(i);
                if (cambia) { if (ini < 0) ini = i; continue; }
                if (ini < 0) continue;
                int len = i - ini;
                string clave = $"{ini:X4}/{len}";
                if (vistos.Add(clave) && salida.Count < 8)
                    salida.Add($"ds:[{ini:X4}h] {len} byte(s): " +
                               $"{Convert.ToHexString(caso.EntryMem, ini, len)} -> " +
                               $"{Convert.ToHexString(caso.ExitMem, ini, len)}  (caso {ci})");
                ini = -1;
            }
        }
        return salida;
    }

    static object Fila(string func, ushort cs, ushort ip, Expediente.Ficha f, Extras? ex,
                       int casos, int insn, string gameRel, string corpusRel,
                       NombreConEvidencia.Propuesta nom, List<string> sueltos) => new
    {
        func,
        // El nombre viaja SIEMPRE con su prueba y su confianza, y es null cuando no la hay:
        // quien lea este JSON tiene que poder distinguir un hecho medido de un hueco.
        nombre = nom.Nombre,
        confianza_del_nombre = nom.Nombre != null ? nom.Confianza : null,
        evidencia_del_nombre = nom.Evidencia.ToArray(),
        cambios_no_explicados_por_el_cuerpo = sueltos.ToArray(),
        entrada = $"{cs:X4}:{ip:X4}",
        casos,
        instrucciones = insn,
        capa = ex?.Capa,
        prueba_de_la_capa = ex?.Prueba,
        eventos = ex?.Eventos ?? 0,
        escenas = ex?.Escenas.OrderByDescending(e => e.muestras)
                     .Select(e => new { escena = e.escena, muestras = e.muestras, t_inicio_ms = e.t0, t_fin_ms = e.t1 })
                     .ToArray(),
        cobertura = ex?.TieneFrontera == true
            ? new { bytes_rango = ex.BytesRango, bytes_ejecutados = ex.BytesEjecutados, rango_fiable = ex.RangoFiable }
            : null,
        conserva = f.Preservados.ToArray(),
        cambia = f.Cambian.ToArray(),
        ret = f.RetN >= 0 ? new { lejano = f.RetLejano, bytes_de_argumentos = f.RetN } : null,
        argumentos = f.Args.Select(g => new
        {
            en = $"[bp+{g.Off:X}h]",
            ancho = g.Ancho,
            puntero_far = g.PunteroFar,
            valores_vistos = g.Valores.Distinct().Take(8).ToArray(),
        }).ToArray(),
        globales = f.Fijas.Select(x => new
        {
            direccion = $"{x.Seg}:[{x.Dir:X4}h]",
            ancho = x.Ancho,
            escrita = x.Escrita,
            valores_vistos = x.Valores.Distinct().Take(8).ToArray(),
        }).ToArray(),
        llamadas = f.Llamadas.DistinctBy(l => (l.Tipo, l.Off, l.Seg, l.N, l.Puerto)).Select(l => new
        {
            tipo = l.Tipo,
            en = $"{l.En:X4}",
            destino = l.Tipo switch
            {
                "near" => $"{l.Off:X4}",
                "far" => $"{l.Seg:X4}:{l.Off:X4}",
                "int" => $"int {l.N:X2}h",
                _ => l.Puerto,
            },
            desde_csharp = l.Tipo switch
            {
                "near" => $"o.Llama(0x{l.Off:X4}, 0x{l.Sig:X4})",
                "far" => $"o.LlamaLejos(0x{l.Seg:X4}, 0x{l.Off:X4})",
                "int" => $"o.Interrupcion(0x{l.N:X2})",
                "in" => $"o.LeePuerto8({l.Puerto})",
                "out" => $"o.EscribePuerto8({l.Puerto}, valor)",
                _ => "o.Llama(destino calculado)",
            },
        }).ToArray(),
        oraculo = $"pakuport oracle-run --game {gameRel} --func {func} --base 0 --entrada <entrada.json>",
        plantilla = $"{func}/PLANTILLA.cs",
        desensamblado = $"{func}/desensamblado.asm",
    };

    static string Leeme(string id, string gameRel, string corpusRel, string outRel,
                        int n, int conCapa, int conEscena, int conFrontera,
                        int conNombre, int nombreAlta) =>
$@"# El tablero del remake — {id}

Cada carpeta de aquí es **una función del juego**, y dentro está todo lo que PakuPort midió de
ella mientras el juego corría de verdad: qué hace, en qué pantalla vive, qué parte de su código
se ha ejecutado, cómo se la llama, qué variables toca y a qué llama. Reescribe la función en C#
y el juez la compara con el juego original, invocación a invocación. Lo que todavía no esté
traducido lo sigue haciendo el binario original, así que **se puede empezar por cualquier
función, sin portar antes lo que cuelga de ella**.

## Lo que hay

| | |
|---|---|
| funciones en el tablero | **{n}** |
| con capa medida (dibuja / suena / E-S / lógica) | {conCapa} |
| con escena | {conEscena} |
| con frontera de código ejecutado | {conFrontera} |
| **con nombre propuesto, y su prueba al lado** | **{conNombre}** ({nombreAlta} de confianza alta) |

El nombre sale de lo que la función HACE — el servicio de DOS o de la BIOS que llama con su
subfunción, los puertos que toca, el texto que referencia — y **viaja con la cita exacta**, para
que se pueda comprobar en el desensamblado de al lado. La mayoría de las funciones de un juego
son lógica pura y no llaman a nada del sistema: **ésas se quedan sin nombre a propósito**, porque
un nombre adivinado se lee como un hecho y quien traduce lo usaría sin verificarlo. Comprobar que
ninguna cita está inventada: `python tools/control_nombres.py --tablero {outRel}`.

- `<FUNCION>/PLANTILLA.cs` — el esqueleto, con todo lo medido en la cabecera.
- `<FUNCION>/desensamblado.asm` — el cuerpo original.
- `expediente.json` — lo mismo en datos, para consultar sin leerse las cabeceras.
- `banco.json` — la lista, en el formato que consume el juez.

## Las cuatro órdenes

Escribe tu versión como `respuesta.cs` dentro de la carpeta de la función.

```
pakuport banco juzga --banco {outRel}
```
Compila cada `respuesta.cs` y la ejecuta contra las invocaciones reales de su función. Compara
registros, banderas y memoria. Antes de exigirte nada pasa el **binario original** por el mismo
aro: si ni el original reproduce un caso, esa función sale `INJUZGABLE` y no cuenta como fallo.

```
pakuport oracle-run --game {gameRel} --func <FUNCION> --base 0 --entrada <entrada.json>
```
Le hace al juego original una pregunta que nadie ha grabado antes. Ejecuta los bytes originales
con su DOS, sus ficheros y su vídeo montados. Con `--pfc <fichero>` la respuesta sale como un
caso más para el juez.

```
pakuport interp-verify --game {gameRel} --corpus {corpusRel} --strict --registros
```
Comprueba que el propio corpus se reproduce. Si una función sale roja aquí, el material de esa
función no sirve para juzgar a nadie: es un hueco del grabador, no un fallo de la traducción.

```
pakuport expediente tablero --game {gameRel} --corpus {corpusRel}
```
Regenera este tablero. **No borra ningún `respuesta.cs`**: se puede volver a lanzar en cualquier
momento sin perder trabajo.

## De dónde sale cada cosa

Lo que en una cabecera pone **observado** viene de invocaciones reales grabadas, y ningún caso
lo contradice: se puede usar sin verificarlo. Lo que pone **leído del cuerpo** sale del
desensamblado y puede incluir código que esa partida no llegó a ejecutar. Cuando un dato falta,
la cabecera lo dice y da la receta para capturarlo. Un hueco declarado vale más que un dato
inventado, porque un dato falso te lo vas a creer.
";
}
