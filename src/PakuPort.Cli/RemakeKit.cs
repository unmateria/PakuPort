using System.Diagnostics;
using System.Text.Json;
using PakuPort.Core;

/// <summary>
/// `pakuport remake` — EL KIT DE REMAKES.
///
/// El remake es un contrato de datos que PakuPort redacta, valida y ejecuta. Quien hace el arte
/// no decide qué se dibuja, dónde va, de qué tamaño, en qué orden ni si persiste al apagarse:
/// todo eso lo MIDE el motor y lo escribe aquí. Lo único que aporta el arte son imágenes en las
/// rutas que este comando le indica por su nombre.
///
/// Por qué existe: las capacidades ya estaban —catálogo, tracks, escenas, procedencia, replay,
/// reemplazo con liveness— pero repartidas en ocho scripts sueltos y 85 palancas `PAKU_*` que
/// había que encadenar a mano acertando las rutas. Eso no es una herramienta, es un examen; y el
/// primer remake que se intentó sin ella (la intro de un juego de prueba) falló por adivinar de
/// forma plausible justo lo que el motor sabía medir. Esto es la puerta.
///
/// Cinco verbos y uno que dice cuál toca:
///     pakuport remake                  en qué paso estás y el comando literal siguiente
///     pakuport remake capture &lt;id&gt;     graba una partida con la telemetría correcta encendida
///     pakuport remake study   &lt;id&gt;     mide y produce dossier + referencias + pendiente + manifiesto
///     pakuport remake check   &lt;id&gt;     valida el arte entregado sin ejecutar el juego
///     pakuport remake judge   &lt;id&gt;     ejecuta la cinta con y sin remake y compara
///     pakuport remake play    &lt;id&gt;     juega con el remake puesto
///
/// Todo vive en `games/&lt;id&gt;/remake/`, con nombres evidentes y una sola fuente de verdad por
/// fichero. Nada de lo de aquí es específico de un juego.
/// </summary>
static class RemakeKit
{
    const string Uso = @"uso: pakuport remake [<id>]                      dice en qué paso estás
     pakuport remake capture <id> [--minutos N] [--teclas S] [--raton] [--export DIR]
     pakuport remake study   <id> [--export DIR]
     pakuport remake hoja    <id>                     el trabajo, en una página que se mira
     pakuport remake try     <id> <clave> <img.png>   una imagen: antes y después, en segundos
     pakuport remake check   <id>
     pakuport remake judge   <id> [--export DIR]
     pakuport remake play    <id> [--export DIR]";

    public static int Ejecutar(string[] rest)
    {
        string verbo = rest.Length > 0 && !rest[0].StartsWith('-') ? rest[0] : "";
        bool esVerbo = verbo is "capture" or "study" or "check" or "judge" or "play"
                             or "hoja" or "try";

        // `pakuport remake <id>` y `pakuport remake` (sin nada) son la misma cosa: estado.
        if (!esVerbo) return Estado(ResuelveId(verbo));

        string id = rest.Length > 1 && !rest[1].StartsWith('-') ? rest[1] : ResuelveId("");
        if (id is "") { Console.Error.WriteLine(Uso); return 1; }
        string[] flags = rest[1..];

        return verbo switch
        {
            "capture" => Capture(id, flags),
            "study" => Study(id, flags),
            "check" => Check(id),
            "judge" => Judge(id, flags),
            "play" => Play(id, flags),
            "hoja" => Py("tools/remake_kit/build_hoja.py", id, "--remake", DirRemake(id)),
            "try" => Try(id, rest),
            _ => 1,
        };
    }

    // ---------------------------------------------------------------- rutas y estado

    static string GamesDir => "games";
    static string DirJuego(string id) => Path.Combine(GamesDir, id);
    static string GameJson(string id) => Path.Combine(DirJuego(id), "game.json");
    static string DirRemake(string id) => Path.Combine(DirJuego(id), "remake");
    static string Cinta(string id) => Path.Combine(DirRemake(id), "cinta.tape");
    static string Pendiente(string id) => Path.Combine(DirRemake(id), "pendiente.json");
    static string Manifiesto(string id) => Path.Combine(DirRemake(id), "manifiesto.json");
    static string Runtime(string id) => Path.Combine(DirRemake(id), "remake.json");
    static string DirHd(string id) => Path.Combine(DirRemake(id), "hd");

    /// <summary>Sin id explícito, el único juego que ya tiene remake empezado; si hay varios o
    /// ninguno, se pide por su nombre en vez de elegir por el usuario.</summary>
    static string ResuelveId(string dado)
    {
        if (!string.IsNullOrEmpty(dado)) return dado;
        if (!Directory.Exists(GamesDir)) return "";
        var conRemake = Directory.GetDirectories(GamesDir)
            .Where(d => Directory.Exists(Path.Combine(d, "remake")))
            .Select(Path.GetFileName).ToList();
        if (conRemake.Count == 1) return conRemake[0]!;
        if (conRemake.Count > 1)
        {
            Console.WriteLine("Hay varios remakes empezados. Dime cuál:");
            foreach (var c in conRemake) Console.WriteLine($"  pakuport remake {c}");
            return "";
        }
        Console.WriteLine("Ningún juego tiene un remake empezado todavía.");
        Console.WriteLine("Siguiente:  pakuport remake capture <id>   (el id es la carpeta en games\\)");
        return "";
    }

    static int Estado(string id)
    {
        if (id is "") return 1;
        if (!File.Exists(GameJson(id)))
        {
            Console.WriteLine($"No conozco el juego «{id}»: no existe {GameJson(id)}.");
            Console.WriteLine($"Siguiente:  pakuport port <ruta\\{id.ToUpperInvariant()}.EXE>");
            return 1;
        }
        var juego = GameConfig.Load(GameJson(id));
        string titulo = string.IsNullOrEmpty(juego.Title) ? id : juego.Title;
        string exp = DirExport(id, null);

        Console.WriteLine($"== {titulo} ==");

        if (!Directory.Exists(exp))
        {
            Console.WriteLine($"  {titulo} no está porteado todavía: no existe {exp}.");
            Console.WriteLine($"Siguiente:  pakuport port <ruta al EXE de {titulo}>");
            return 0;
        }
        if (juego.CyclesPerMs <= 0)
        {
            Console.WriteLine($"  {titulo} no tiene la velocidad calibrada (cyclesPerMs=0), y sin");
            Console.WriteLine("  ella la cinta no se puede grabar ni reproducir igual dos veces.");
            Console.WriteLine($"Siguiente:  pon \"cyclesPerMs\" en {GameJson(id)} (juega y calíbralo con Ctrl+F1/F2)");
            return 0;
        }
        if (!File.Exists(Cinta(id)))
        {
            Console.WriteLine($"  {titulo} no tiene ninguna partida grabada.");
            Console.WriteLine($"Siguiente:  pakuport remake capture {id} --minutos 3");
            return 0;
        }
        if (!File.Exists(Pendiente(id)))
        {
            Console.WriteLine($"  Hay partida grabada ({LineasDe(Cinta(id))} eventos) pero nadie la ha estudiado.");
            Console.WriteLine($"Siguiente:  pakuport remake study {id}");
            return 0;
        }

        var pend = LeePendiente(id);
        int hechos = Directory.Exists(DirHd(id))
            ? Directory.GetFiles(DirHd(id), "*.png", SearchOption.AllDirectories).Length : 0;
        int encargos = pend.TryGetProperty("resumen", out var res)
                       && res.TryGetProperty("encargos", out var ne) ? ne.GetInt32() : 0;
        Console.WriteLine($"  {encargos} encargos en pendiente.json · {hechos} imágenes en remake\\hd\\");
        MalasNoticias(id);

        if (hechos == 0)
        {
            Console.WriteLine($"Siguiente:  pakuport remake hoja {id}    (y deja las imágenes en {DirHd(id)}\\)");
            return 0;
        }
        if (!File.Exists(Runtime(id)))
        {
            Console.WriteLine($"Siguiente:  pakuport remake check {id}");
            return 0;
        }
        Console.WriteLine($"Siguiente:  pakuport remake judge {id}     (y luego: pakuport remake play {id})");
        return 0;
    }

    /// <summary>Lo que el último juicio ya sabía y nadie leyó. El informe de un juego de prueba
    /// decía "cobertura 0,2 %" y "1 asset perdió su arte" ANTES de la prueba manual que tumbó el kit:
    /// estaba escrito, en un fichero que hay que abrir a propósito. Un estado que no da las malas
    /// noticias por delante manda al siguiente a descubrirlas jugando.</summary>
    static void MalasNoticias(string id)
    {
        string informe = Path.Combine(DirRemake(id), "informe.json");
        if (!File.Exists(informe)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(informe));
            var r = doc.RootElement;
            if (r.TryGetProperty("cobertura", out var c) && c.GetDouble() < 50)
                Console.WriteLine($"  ⚠ el último juicio dio {c.GetDouble():F1} % de cobertura: "
                                  + "casi todo se sigue viendo original.");
            if (r.TryGetProperty("regresion", out var g) && g.ValueKind == JsonValueKind.Array
                && g.GetArrayLength() > 0)
                Console.WriteLine($"  ⚠ {g.GetArrayLength()} asset(s) TENÍAN arte y lo han perdido "
                                  + "(mira informe.md).");
            long peso = 0;
            string cap = Path.Combine(DirRemake(id), "captura");
            if (Directory.Exists(cap))
                foreach (var f in Directory.GetFiles(cap, "*", SearchOption.AllDirectories))
                    peso += new FileInfo(f).Length;
            if (peso > 200L * 1024 * 1024)
                Console.WriteLine($"  ⚠ la captura ocupa {peso / (1024 * 1024)} MiB en {cap} "
                                  + "(se borra sola al volver a capturar; `python tools/limpiar.py`).");
        }
        catch { /* un informe ilegible no debe impedir decir el resto */ }
    }

    // ---------------------------------------------------------------- capture

    static int Capture(string id, string[] flags)
    {
        if (!File.Exists(GameJson(id))) { Console.Error.WriteLine($"no existe {GameJson(id)}"); return 1; }
        var juego = GameConfig.Load(GameJson(id));
        string exp = DirExport(id, Opt(flags, "--export"));
        if (!Directory.Exists(exp)) { Console.Error.WriteLine($"no existe el export {exp}: pásalo por `pakuport port` primero"); return 1; }

        // La cinta ancla el reloj al contador de instrucciones (InputTape.ClockLocked), y eso
        // exige gobernador calibrado. Sin él las dos corridas del juez divergen y la comparación
        // no significa nada, así que se para aquí y no tres pasos más abajo.
        if (juego.CyclesPerMs <= 0)
        {
            Console.Error.WriteLine($"{id} no tiene cyclesPerMs en game.json.");
            Console.Error.WriteLine("Sin velocidad calibrada la cinta no reproduce igual dos veces y el juez de");
            Console.Error.WriteLine("fidelidad no vale. Calíbrala jugando (Ctrl+F1/F2) y hornéala en game.json.");
            return 1;
        }

        Directory.CreateDirectory(DirRemake(id));
        Directory.CreateDirectory(DirHd(id));

        string? teclas = Opt(flags, "--teclas");
        int minutos = int.TryParse(Opt(flags, "--minutos"), out var mm) ? mm : 3;
        string cinta = Path.GetFullPath(Cinta(id));

        // Las palancas NO se dejan a la memoria de nadie: aquí se imponen, porque fallar en
        // cualquiera de las dos da un catálogo pobre SIN DECIR NADA.
        //   PAKU_LOG_BLIT=3  — con =1 no se escribe blitraw.bin y un juego que dibuja con
        //                      mov+bucle (un juego de prueba entero) sale vacío; con =2 el que dibuja
        //                      con movs/stos deja su identidad pero NO sus píxeles, y entonces
        //                      el encargo no lleva imagen que enseñar (medido en un juego: 1167
        //                      encargos, 0 con referencia). El 3 graba las dos cosas y las ata.
        //   PAKU_INTERP_ONLY — el mundo lifteado no da procedencia: todas las escrituras salen
        //                      con la misma rutina, y la identidad del asset se pierde.
        var env = new Dictionary<string, string>
        {
            ["PAKU_LOG_BLIT"] = "3",
            ["PAKU_INTERP_ONLY"] = "1",
            ["PAKU_LOG_AUDIO_EV"] = "1",
            ["PAKU_SAMPLE_PC_MS"] = "1",
            ["PAKU_TAPE_RECORD"] = cinta,
            ["PAKU_NOSPLASH"] = "1",
            // Sin esto el contador de fotogramas no avanza fuera del host con ventana, y el
            // ORDEN DE DIBUJO —lo que decide qué tapa a qué— habría que estimarlo.
            ["PAKU_PRESENT_CAPTURE"] = "1",
            //   PAKU_LOG_PALETTE — sin la paleta EFECTIVA no hay color: en 256 colores el asset
            //                      guarda ÍNDICES, y la referencia que se le entrega a quien
            //                      dibuja salía en gris (el índice usado como nivel de gris),
            //                      que no se parece a lo que el jugador ve. Y la paleta no se
            //                      deduce del fichero: un juego la reprograma antes de pintar
            //                      cada imagen, así que hay que preguntarle al juego cuál tenía
            //                      puesta mientras esa imagen estaba en pantalla.
            ["PAKU_LOG_PALETTE"] = "1",
            //   PAKU_PRESENT_DUMP* — un muestreo de la pantalla TAL COMO SE VE, con el número de
            //                        fotograma en el nombre. De aquí sale la referencia de cada
            //                        asset: recortarla de lo presentado es exacto, mientras que
            //                        reconstruirla desde índices obliga a acertar cuál de las
            //                        paletas del fundido tocaba (254 en un minuto de un juego) y no
            //                        hay regla que acierte siempre. Uno de cada 10 fotogramas
            //                        son ~7/s: suficiente para ver una escena asentada.
            ["PAKU_PRESENT_DUMP"] = Path.Combine(Path.GetFullPath(exp), "logs", "present", "f"),
            ["PAKU_PRESENT_DUMP_EVERY"] = "10",
            ["PAKU_PRESENT_DUMP_N"] = "600",
        };

        foreach (var viejo in new[] { "blit.log", "blitraw.bin", "pcsample.log", "audio-events.log" })
        {
            string p = Path.Combine(exp, "logs", viejo);
            if (File.Exists(p)) File.Delete(p);      // un log de ayer mezclado con el de hoy miente
        }
        string present = Path.Combine(exp, "logs", "present");
        if (Directory.Exists(present)) Directory.Delete(present, true);

        Console.WriteLine($"== grabando {id} ({minutos} min máx.) ==");

        // El export lleva su PROPIA copia de Core y se compila con ella. Sin refrescarla, la
        // captura mide un motor viejo y no lo dice: el fichero de blits sale con el formato de
        // antes y todo lo de aguas abajo parece funcionar. Es justo la clase de fallo silencioso
        // que este kit existe para que no vuelva a pasar, así que se refresca siempre.
        ExportCore.Copiar(exp);
        Console.WriteLine("  Core del export refrescado desde src/");

        var (rcB, outB) = Proc("dotnet", "build -c Release", exp, 1_200_000);
        if (rcB != 0) { Console.Error.WriteLine(string.Join('\n', outB.TakeLast(12))); return 1; }

        int limiteMs = minutos * 60_000;
        if (teclas != null)
        {
            // Corrida guionizada y desatendida: la ventana va oculta (PAKU_AUTOEXIT_MS) y el
            // juego se conduce con teclas, para poder repetir la captura en un gate.
            env["PAKU_AUTOKEYS"] = teclas;
            env["PAKU_AUTOEXIT_MS"] = limiteMs.ToString();
            Console.WriteLine($"  guionizada: teclas «{teclas}», ventana oculta");
            // --raton: mueve el ratón durante la grabación. Lo que entra queda EN LA CINTA (los
            // sumideros de input graban, así que las fuentes sintéticas se capturan gratis), y
            // eso es lo que luego permite al juez de pantalla medir sobre algo que se mueve sin
            // renunciar al determinismo: el replay reinyecta ese mismo movimiento en los mismos
            // ticks. Sin movimiento en la cinta, un menú se queda quieto y no hay nada que medir.
            if (HasFlag(flags, "--raton")) env["PAKU_MOUSE_WIGGLE"] = "1";
        }
        else
        {
            Console.WriteLine("  juega tú: la ventana se abre; cierra cuando termines o espera al tope.");
        }

        var (rc, salida) = Proc("dotnet", "run -c Release --no-build", exp, limiteMs + 120_000, env);
        if (rc == -1) Console.WriteLine($"  (tope de {minutos} min alcanzado, la partida se corta ahí)");

        if (!File.Exists(cinta))
        {
            Console.Error.WriteLine("no se escribió la cinta. Últimas líneas del juego:");
            foreach (var l in salida.TakeLast(12)) Console.Error.WriteLine("  " + l);
            return 1;
        }

        // La paleta sale por stderr mezclada con todo lo demás; aquí se aparta a su propio
        // fichero para que las referencias puedan pintarse con los colores de VERDAD.
        var paleta = salida.Where(l => l.Contains("[PALETA]")).ToList();
        if (paleta.Count > 0)
            File.WriteAllLines(Path.Combine(exp, "logs", "paleta.log"), paleta);
        Console.WriteLine($"  paleta: {paleta.Count} cambio(s) registrado(s)");

        // La telemetría se guarda CON el remake, no dentro del export. El host archiva
        // `blit.log`/`blitraw.bin` en `logs/partida-*` en CADA arranque, así que bastaba lanzar el
        // juego una vez entre `capture` y `study` para que el estudio se quedara sin datos y
        // dijera "no existe blit.log" — pasó dos veces seguidas antes de verlo. Aquí deja de
        // depender de que nadie toque el export.
        string cap = Path.Combine(DirRemake(id), "captura");
        if (Directory.Exists(cap)) Directory.Delete(cap, true);
        Directory.CreateDirectory(cap);
        foreach (var f in new[] { "blit.log", "blitraw.bin", "paleta.log", "audio-events.log",
                                  "pcsample.log", "input.log" })
        {
            string o = Path.Combine(exp, "logs", f);
            if (File.Exists(o)) File.Copy(o, Path.Combine(cap, f), true);
        }
        string presOrigen = Path.Combine(exp, "logs", "present");
        if (Directory.Exists(presOrigen))
        {
            Directory.CreateDirectory(Path.Combine(cap, "present"));
            foreach (var f in Directory.GetFiles(presOrigen))
                File.Copy(f, Path.Combine(cap, "present", Path.GetFileName(f)), true);
        }

        long blits = Tam(Path.Combine(cap, "blit.log"));
        long crudo = Tam(Path.Combine(cap, "blitraw.bin"));
        Console.WriteLine($"Grabados {LineasDe(cinta)} eventos de entrada · blit.log {blits / 1024} KiB · "
                          + $"blitraw.bin {crudo / 1024} KiB · cinta.tape");

        // Un juego que dibuja pero cuya telemetría sale vacía es el fallo más caro de todos: el
        // resto de la cadena funciona, produce un encargo vacío y parece que "no hay assets".
        // Se dice aquí, con el diagnóstico y no sólo con el síntoma.
        if (blits < 1024 && crudo < 1024)
        {
            int px = PixelesDeLaCorridaFinal(salida);
            Console.WriteLine();
            Console.WriteLine("AVISO IMPORTANTE: no se ha grabado casi ningún dibujo"
                              + (px > 0 ? $", y sin embargo el juego pintó {px} píxeles." : "."));
            Console.WriteLine("  La telemetría de blits sólo ve lo que se escribe en la VENTANA DE VRAM");
            Console.WriteLine("  (0xA0000-0xC0000). Si sale vacía con el juego pintando, es que este juego");
            Console.WriteLine("  COMPONE FUERA DE PANTALLA: su driver dibuja en buffers propios de RAM y");
            Console.WriteLine("  vuelca al final. Hay juegos que son exactamente ese caso (un driver que usa");
            Console.WriteLine("  páginas en el segmento 0x4000+, ver Platform/DriverRegistry.cs).");
            Console.WriteLine("  Para un juego así el kit NO puede identificar assets en ejecución todavía,");
            Console.WriteLine("  y decirlo es la respuesta correcta: seguir daría un encargo vacío que");
            Console.WriteLine("  parece 'este juego no tiene gráficos'.");
            return 1;
        }
        Console.WriteLine($"Siguiente:  pakuport remake study {id}");
        return 0;
    }

    // ---------------------------------------------------------------- study

    static int Study(string id, string[] flags)
    {
        string exp = DirExport(id, Opt(flags, "--export"));
        string rem = DirRemake(id);
        string cat = Path.Combine(rem, "catalog");
        // La telemetría de la captura, que `capture` deja a salvo junto al remake. Si no está
        // (captura hecha con una versión anterior del kit) se cae al export, para no romper el
        // trabajo de nadie — pero avisando, porque allí la puede archivar cualquier corrida.
        string logs = Path.Combine(DirRemake(id), "captura");
        if (!File.Exists(Path.Combine(logs, "blit.log")))
        {
            logs = Path.Combine(exp, "logs");
            Console.WriteLine("  (captura antigua: se lee del export, donde una corrida del juego "
                              + "la puede archivar; vuelve a pasar `capture` para dejarla a salvo)");
        }
        if (!File.Exists(Cinta(id)))
        {
            Console.Error.WriteLine($"no hay partida grabada: falta {Cinta(id)}");
            Console.Error.WriteLine($"Siguiente:  pakuport remake capture {id}");
            return 1;
        }
        Directory.CreateDirectory(cat);

        Console.WriteLine($"== estudiando {id} ==");

        // El orden es el del encadenado real: catálogo -> tracks -> jugador; escenas y sonidos
        // van por su cuenta; el dossier va el último porque consume a los demás.
        if (Py("tools/blit_catalog/build_catalog.py", "--logs", logs, "--out", cat) != 0) return 1;
        Py("tools/blit_catalog/build_tracks.py", cat);
        Py("tools/player_id/find_player.py", cat, "--tape", Cinta(id),
           "--cycles", GameConfig.Load(GameJson(id)).CyclesPerMs.ToString());

        string escenas = Path.Combine(rem, "escenas.json");
        string pcs = Path.Combine(logs, "pcsample.log");
        if (File.Exists(pcs)) Py("tools/scene_hist/build_scenes.py", pcs, "--json", escenas);
        else Console.WriteLine("  (sin pcsample.log: no hay segmentación en escenas)");

        string audio = Path.Combine(logs, "audio-events.log");
        if (File.Exists(audio)) Py("tools/audio_catalog/build_sounds.py", "--log", audio, "--out", DirJuego(id));

        Py("tools/dossier/build_dossier.py", id, "--out", rem, "--catalog", cat,
           "--blitraw", Path.Combine(logs, "blitraw.bin"), "--blit-log", Path.Combine(logs, "blit.log"));

        // Los píxeles de los blits reemplazables, atados a su identidad por las marcas del
        // nivel 3. Es lo que convierte el encargo en algo que se puede mirar.
        Py("tools/remake_kit/build_ref.py", "--raw", Path.Combine(logs, "blitraw.bin"),
           "--out", Path.Combine(rem, "ref"), "--json", Path.Combine(rem, "ref.json"),
           "--paleta", Path.Combine(logs, "paleta.log"),
           "--blit-log", Path.Combine(logs, "blit.log"),
           "--present", Path.Combine(logs, "present"));

        // Los DIBUJOS LÓGICOS: en un juego que compone fila a fila, es la única forma de tener
        // una figura entera de la que colgar arte. Sin esto un juego de prueba ofrecía 3 assets
        // reemplazables de 1165, y el resto son tiras de un píxel de alto.
        Py("tools/remake_kit/build_grupos.py", "--blit-log", Path.Combine(logs, "blit.log"),
           "--out", Path.Combine(rem, "grupos.json"),
           "--present", Path.Combine(logs, "present"),
           "--ref-dir", Path.Combine(rem, "ref"),
           "--cycles", GameConfig.Load(GameJson(id)).CyclesPerMs.ToString());

        // Orden de dibujo y persistencia del fondo: qué tapa a qué, y si el sprite se queda o
        // el juego repinta debajo. Son las dos preguntas que tumbaron el remake de una intro.
        Py("tools/remake_kit/build_capas.py", "--blit-log", Path.Combine(logs, "blit.log"),
           "--out", Path.Combine(rem, "capas.json"));

        // Glifos agrupados en líneas legibles y rutinas procedurales declaradas como tales.
        Py("tools/remake_kit/build_texto.py", "--blit-log", Path.Combine(logs, "blit.log"),
           "--raw", Path.Combine(logs, "blitraw.bin"), "--refs", Path.Combine(rem, "ref.json"),
           "--ref-dir", Path.Combine(rem, "ref"), "--out", Path.Combine(rem, "texto.json"));

        int rc = Py("tools/remake_kit/build_pendiente.py", id, "--catalog", cat, "--out", rem,
                    "--escenas", escenas, "--refs", Path.Combine(rem, "ref.json"),
                    "--capas", Path.Combine(rem, "capas.json"),
                    "--texto", Path.Combine(rem, "texto.json"),
                    "--grupos", Path.Combine(rem, "grupos.json"));
        if (rc != 0) return rc;

        Console.WriteLine($"Referencias en {Path.Combine(rem, "ref")}\\");
        Console.WriteLine($"Trabajo pendiente -> {Pendiente(id)}");
        Console.WriteLine($"Siguiente:  lee {Pendiente(id)} y deja las imágenes en {DirHd(id)}\\");
        return 0;
    }

    // ---------------------------------------------------------------- check / judge / play

    static int Check(string id)
    {
        if (!File.Exists(Manifiesto(id)))
        {
            Console.Error.WriteLine($"no hay contrato que validar: falta {Manifiesto(id)}");
            Console.Error.WriteLine($"Siguiente:  pakuport remake study {id}");
            return 1;
        }
        int rc = Py("tools/remake_kit/check_arte.py", id, "--remake", DirRemake(id));
        if (rc == 0) Console.WriteLine($"Siguiente:  pakuport remake judge {id}");
        return rc;
    }

    static int Judge(string id, string[] flags)
    {
        if (!File.Exists(Runtime(id)))
        {
            Console.Error.WriteLine($"no hay arte admitido que juzgar: falta {Runtime(id)}");
            Console.Error.WriteLine($"Siguiente:  pakuport remake check {id}");
            return 1;
        }
        return Py("tools/remake_kit/judge_fidelidad.py", id,
                  "--export", DirExport(id, Opt(flags, "--export")), "--remake", DirRemake(id));
    }

    /// <summary>El bucle corto: una imagen, y el antes y el después en segundos. `rest` es
    /// `try &lt;id&gt; &lt;clave&gt; &lt;imagen&gt;`.</summary>
    static int Try(string id, string[] rest)
    {
        var libres = rest.Skip(1).Where(x => !x.StartsWith('-')).ToArray();
        if (libres.Length < 3)
        {
            Console.Error.WriteLine("uso: pakuport remake try <id> <clave> <imagen.png> [--ms N]");
            Console.Error.WriteLine("     las claves salen de la hoja: pakuport remake hoja " + id);
            return 1;
        }
        var args = new List<string> { id, libres[1], libres[2], "--export", DirExport(id, null),
                                      "--remake", DirRemake(id) };
        string? ms = Opt(rest, "--ms");
        if (ms != null) { args.Add("--ms"); args.Add(ms); }
        return Py("tools/remake_kit/try_asset.py", args.ToArray());
    }

    static int Play(string id, string[] flags)
    {
        string exp = DirExport(id, Opt(flags, "--export"));
        if (!Directory.Exists(exp)) { Console.Error.WriteLine($"no existe el export {exp}"); return 1; }
        var env = new Dictionary<string, string> { ["PAKU_INTERP_ONLY"] = "1" };
        if (File.Exists(Runtime(id))) env["PAKU_REMAKE"] = Path.GetFullPath(Runtime(id));
        else Console.WriteLine("(sin arte admitido: el juego se ve original — que es el fallo seguro)");
        var (rc, _) = Proc("dotnet", "run -c Release", exp, int.MaxValue, env);
        return rc;
    }

    // ---------------------------------------------------------------- fontanería

    static string DirExport(string id, string? dado) => dado ?? Path.Combine("export", id);

    static bool HasFlag(string[] a, string nombre) => Array.IndexOf(a, nombre) >= 0;

    static string? Opt(string[] a, string nombre)
    {
        int i = Array.IndexOf(a, nombre);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    static long Tam(string p) => File.Exists(p) ? new FileInfo(p).Length : 0;

    /// <summary>Los píxeles no negros que reportó el host al cerrar. Sirve para distinguir "el
    /// juego no dibujó nada" de "dibujó y la telemetría no lo vio", que son problemas opuestos.</summary>
    static int PixelesDeLaCorridaFinal(List<string> salida)
    {
        string? l = salida.LastOrDefault(x => x.Contains("pixeles_no_negros="));
        if (l == null) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(l, @"pixeles_no_negros=(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
    static int LineasDe(string p) => File.Exists(p) ? File.ReadLines(p).Count() : 0;

    static JsonElement LeePendiente(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Pendiente(id)));
        return doc.RootElement.Clone();
    }

    /// <summary>Lanza una herramienta del kit. El análisis vive en Python como todo el tooling
    /// de medida de la casa; lo que aporta este comando es que las rutas ya están resueltas.</summary>
    static int Py(string script, params string[] args)
    {
        string linea = string.Join(' ', new[] { script }.Concat(args).Select(Cita));
        Console.WriteLine($"  python {linea}");
        var (rc, salida) = Proc("python", linea, ".", 900_000);
        foreach (var l in salida) Console.WriteLine("    " + l);
        if (rc != 0) Console.Error.WriteLine($"  FALLO ({rc}): python {script}");
        return rc;
    }

    static string Cita(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    static (int rc, List<string> salida) Proc(string cmd, string args, string dir, int topeMs,
                                              Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(cmd, args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        var lineas = new List<string>();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lineas) lineas.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lineas) lineas.Add(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(topeMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, lineas);
        }
        p.WaitForExit();
        return (p.ExitCode, lineas);
    }
}
