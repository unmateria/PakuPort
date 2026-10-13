using PakuPort.Core;

/// <summary>
/// LAS CAPAS DEL PORT — qué función es render, cuál es música, cuál es E/S y cuál es LÓGICA.
///
/// <para>Es la pregunta que ordena el expediente: de las miles de funciones que deja un porteo
/// automático, la IA que viene detrás sólo necesita reescribir la <b>lógica</b>. Lo que pinta se
/// va a redibujar, lo que suena se va a resintetizar y lo que lee ficheros se va a reescribir
/// contra el sistema de hoy.</para>
///
/// <para><b>El criterio es una MEDIDA, no un parecido de nombre</b>: una función es de render
/// porque escribió en la VRAM, de música porque tocó el 0x388, de E/S porque pasó por INT 21h.
/// Lo graba <see cref="PerfilCapas"/> durante una partida real (<c>PAKU_CAPAS=&lt;csv&gt;</c>) y
/// aquí se agrega por función y se clasifica.</para>
///
/// <code>
/// (en el export)  set PAKU_CAPAS=logs\expediente\capas-&lt;id&gt;.csv &amp; dotnet run -c Release
/// pakuport expediente capas --game games\&lt;id&gt;\game.json --corpus games\&lt;id&gt;\corpus \
///                           --capas logs\expediente\capas-&lt;id&gt;.csv [--out games\&lt;id&gt;\capas.json]
/// </code>
/// </summary>
static class Capas
{
    /// <summary>Las capas, en el orden en que mandan cuando una función hace de varias cosas: lo
    /// que toca hardware de vídeo o audio manda sobre lo demás porque es lo que hay que
    /// reemplazar en un remake; la LÓGICA es lo que queda cuando no toca nada de eso.</summary>
    public enum Capa { Render, Audio, ES, Entrada, Tiempo, Plataforma, Desconocida, Logica }

    /// <summary>De un evento medido a la capa que implica. Cada rama lleva su motivo porque el
    /// motivo es el que viaja al expediente: un nombre sin prueba es peor que no tener nombre.</summary>
    public static (Capa capa, string prueba)? Clasifica(string tipo, int detalle) => tipo switch
    {
        "vram" => (Capa.Render, "escribe en la VRAM"),
        "int" => detalle switch
        {
            0x10 => (Capa.Render, "int 10h (BIOS de video)"),
            0x21 => (Capa.ES, "int 21h (servicios de DOS)"),
            0x33 => (Capa.Entrada, "int 33h (raton)"),
            0x16 => (Capa.Entrada, "int 16h (teclado por BIOS)"),
            0x09 => (Capa.Entrada, "int 09h (IRQ de teclado)"),
            0x1A => (Capa.Tiempo, "int 1Ah (reloj)"),
            0x08 or 0x1C => (Capa.Tiempo, "int 08h/1Ch (tick del timer)"),
            0x2F or 0x67 or 0x31 => (Capa.Plataforma, $"int {detalle:X2}h (multiplex/EMS/DPMI)"),
            _ => (Capa.Desconocida, $"int {detalle:X2}h (no es de las conocidas)"),
        },
        "in" or "out" => detalle switch
        {
            >= 0x3B0 and <= 0x3DF => (Capa.Render, $"puerto {detalle:X3}h (registros VGA/CRTC)"),
            0x388 or 0x389 or 0x38A or 0x38B => (Capa.Audio, $"puerto {detalle:X3}h (OPL/AdLib)"),
            >= 0x220 and <= 0x22F => (Capa.Audio, $"puerto {detalle:X3}h (Sound Blaster)"),
            0x330 or 0x331 => (Capa.Audio, $"puerto {detalle:X3}h (MPU-401/MIDI)"),
            0x61 => (Capa.Audio, "puerto 61h (altavoz del PC)"),
            0x60 or 0x64 => (Capa.Entrada, $"puerto {detalle:X2}h (controlador de teclado)"),
            >= 0x40 and <= 0x43 => (Capa.Tiempo, $"puerto {detalle:X2}h (PIT)"),
            0x20 or 0x21 or 0xA0 or 0xA1 => (Capa.Plataforma, $"puerto {detalle:X2}h (PIC)"),
            >= 0x00 and <= 0x0F => (Capa.Plataforma, $"puerto {detalle:X2}h (DMA)"),
            >= 0xC0 and <= 0xDF => (Capa.Plataforma, $"puerto {detalle:X2}h (segundo DMA)"),
            _ => (Capa.Desconocida, $"puerto {detalle:X3}h (no es de los conocidos)"),
        },
        _ => null,
    };

    sealed class Fun
    {
        public required string Nombre;
        public required int Off;
        public readonly Dictionary<Capa, long> Veces = [];
        public readonly Dictionary<Capa, string> Pruebas = [];
        public long Total;
    }

    /// <summary>Uso del grupo `expediente`.</summary>
    public static int Uso()
    {
        Console.WriteLine(
            "pakuport expediente capas --game <game.json> --corpus <dir> --capas <csv> [--out <json>]\n" +
            "    Agrega el perfil de PAKU_CAPAS por funcion y dice cual es render, cual audio,\n" +
            "    cual E/S y cual LOGICA. El criterio es lo que la funcion HIZO, medido.\n" +
            "    El csv lo graba el propio juego: PAKU_CAPAS=<ruta> antes de correr el export.");
        return 2;
    }

    public static int Ejecutar(string[] a)
    {
        string? gamePath = Opt(a, "--game");
        string? corpusDir = Opt(a, "--corpus");
        string? capasCsv = Opt(a, "--capas");
        if (gamePath == null || corpusDir == null || capasCsv == null
            || !File.Exists(capasCsv) || !Directory.Exists(corpusDir))
        {
            Console.WriteLine(
                "pakuport expediente capas --game <game.json> --corpus <dir> --capas <csv> [--out <json>]\n" +
                "    Agrega el perfil de PAKU_CAPAS por funcion y dice cual es render, cual audio,\n" +
                "    cual E/S y cual LOGICA. El criterio es lo que la funcion HIZO, medido.");
            return 2;
        }

        var mj = BancoCiego.MundoDeJuego(GameConfig.Load(gamePath), gamePath, corpusDir);
        if (mj == null) return 2;
        var mundo = mj.Value.mundo;
        // offset de fichero = lineal - LoadBase - Bias. Es la misma cuenta que Interp.KeyOf, que
        // es la que da nombre a las carpetas del corpus (FUN_1000_<offset>).
        int desplaza = mundo.LoadBase + mundo.Bias;

        // El universo son las funciones que el juego EJECUTO, que es lo que el corpus vio.
        var funs = new List<Fun>();
        foreach (var d in Directory.GetDirectories(corpusDir))
        {
            string n = Path.GetFileName(d);
            if (!n.StartsWith("FUN_1000_", StringComparison.Ordinal)) continue;
            if (int.TryParse(n["FUN_1000_".Length..], System.Globalization.NumberStyles.HexNumber,
                             null, out int off))
                funs.Add(new Fun { Nombre = n, Off = off });
        }
        if (funs.Count == 0) { Console.Error.WriteLine($"{corpusDir} no tiene funciones"); return 2; }
        funs.Sort((x, y) => x.Off.CompareTo(y.Off));
        var inicios = funs.Select(f => f.Off).ToArray();

        // UNA LINEAL ES UNA INSTRUCCION, y un `int` no escribe en la VRAM por si mismo: si la
        // misma direccion tiene eventos de VRAM Y de INT, quien pinto fue EL SERVICIO por encargo
        // suyo. Sigue siendo render —el efecto es que la pantalla cambia, y en un remake hay que
        // tocarla— pero la PRUEBA tiene que decirlo, porque "escribe en la VRAM" es falso y la IA
        // se lo cree. Medido en un juego de prueba: 0814:0402 es `int 10h` con AX=1114h (cargar el generador de
        // caracteres 8x16) y produce 4096 escrituras = 256 caracteres x 16 bytes. Es ademas el
        // motivo de que la procedencia de blit del kit no la vea: el BIOS no escribe con
        // instrucciones del invitado.
        var linealesConInt = new HashSet<int>();
        foreach (string linea in File.ReadLines(capasCsv).Skip(1))
        {
            var p0 = linea.Split(',');
            if (p0.Length >= 4 && p0[0] == "int") linealesConInt.Add(Convert.ToInt32(p0[2], 16));
        }

        long eventosFuera = 0, eventosTotal = 0;
        foreach (string linea in File.ReadLines(capasCsv).Skip(1))
        {
            var p = linea.Split(',');
            if (p.Length < 4) continue;
            string tipo = p[0];
            int detalle = Convert.ToInt32(p[1], 16);
            int lineal = Convert.ToInt32(p[2], 16);
            long veces = long.Parse(p[3]);
            eventosTotal += veces;
            var cl = Clasifica(tipo, detalle);
            if (cl == null) continue;
            if (tipo == "vram" && linealesConInt.Contains(lineal))
                cl = (cl.Value.capa, "pinta por ella un servicio de INT (el BIOS, no sus instrucciones)");

            int off = lineal - desplaza;
            int i = Array.BinarySearch(inicios, off);
            if (i < 0) i = ~i - 1;
            // Antes de la primera funcion conocida, o dentro de codigo que el corpus no vio: se
            // CUENTA aparte en vez de atribuirse a la funcion anterior, que estaria inventando.
            if (i < 0) { eventosFuera += veces; continue; }
            var f = funs[i];
            f.Veces.TryGetValue(cl.Value.capa, out long n);
            f.Veces[cl.Value.capa] = n + veces;
            // Se acumulan hasta tres pruebas DISTINTAS por capa: con TryAdd, una funcion que
            // escribe la VRAM por su cuenta y ademas delega en el BIOS publicaba solo la primera
            // que apareciera en el CSV, que es un orden sin significado.
            if (!f.Pruebas.TryGetValue(cl.Value.capa, out string? ya)) f.Pruebas[cl.Value.capa] = cl.Value.prueba;
            else if (!ya.Contains(cl.Value.prueba) && ya.Split(" + ").Length < 3)
                f.Pruebas[cl.Value.capa] = ya + " + " + cl.Value.prueba;
            f.Total += veces;
        }

        // La capa de una funcion es la de MAYOR PRIORIDAD que haya tocado, no la mas frecuente:
        // una rutina que pinta mil veces y ademas abre un fichero sigue siendo de render, pero una
        // que solo escribe UN byte en la VRAM tambien lo es — porque en un remake hay que tocarla.
        Capa[] prioridad = [Capa.Render, Capa.Audio, Capa.ES, Capa.Entrada, Capa.Tiempo,
                            Capa.Plataforma, Capa.Desconocida];
        var reparto = new Dictionary<Capa, List<Fun>>();
        foreach (var f in funs)
        {
            Capa suya = Capa.Logica;
            foreach (var c in prioridad) if (f.Veces.ContainsKey(c)) { suya = c; break; }
            if (!reparto.TryGetValue(suya, out var l)) reparto[suya] = l = [];
            l.Add(f);
        }

        Console.WriteLine($"== CAPAS de {Path.GetFileNameWithoutExtension(gamePath)}: " +
                          $"{funs.Count} funciones ejecutadas, {eventosTotal:N0} eventos medidos ==");
        Console.WriteLine($"   (mundo: {mundo.Descripcion}, offset = lineal - 0x{desplaza:X})");
        Console.WriteLine();
        foreach (var c in (Capa[])Enum.GetValues(typeof(Capa)))
        {
            if (!reparto.TryGetValue(c, out var l)) continue;
            Console.WriteLine($"  {c,-12} {l.Count,4}  ({100.0 * l.Count / funs.Count:F1} %)   " +
                              string.Join(" ", l.Take(14).Select(f => f.Nombre.Replace("FUN_1000_", ""))) +
                              (l.Count > 14 ? " …" : ""));
        }
        long eventosClasificables = funs.Sum(f => f.Total) + eventosFuera;
        double cobertura = eventosClasificables == 0
            ? 0 : 100.0 * (eventosClasificables - eventosFuera) / eventosClasificables;
        // LA COBERTURA MANDA SOBRE EL REPARTO, y por eso se dice aqui y viaja al JSON: el universo
        // del clasificador son las funciones que el CORPUS vio arrancar, y un corpus corto deja
        // fuera actividad real. Medido: un juego de prueba atribuye el 100 % y su reparto se sostiene; otro,
        // en modo auto atribuye el 3,6 % — el mismo cuadro de porcentajes describiria alli una
        // muestra del 4 % de lo que el juego hizo. Un reparto sin su cobertura al lado se lee como
        // si fuera todo.
        Console.WriteLine($"\n  cobertura: {cobertura:F1} % de los eventos clasificables se atribuyen a una funcion");
        if (eventosFuera > 0)
            Console.WriteLine($"  {eventosFuera:N0} de {eventosClasificables:N0} caen en codigo que el corpus " +
                              "no vio arrancar (no se atribuyen a nadie: seria inventar).");

        string? outPath = Opt(a, "--out");
        if (outPath != null)
        {
            using var w = new StreamWriter(outPath);
            w.WriteLine("{");
            w.WriteLine($"  \"juego\": \"{Path.GetFileNameWithoutExtension(gamePath)}\",");
            w.WriteLine($"  \"corpus\": \"{corpusDir.Replace("\\", "/")}\",");
            w.WriteLine($"  \"criterio\": \"la capa es lo que la funcion HIZO, medido con PAKU_CAPAS: escribir en VRAM = render, tocar 0x388/SB = audio, int 21h = E/S. Lo que no toca nada de eso es logica.\",");
            // El desplazamiento y la cobertura viajan al JSON porque sin ellos el fichero no se
            // puede AUDITAR: para cruzarlo con una verdad terreno en cs:ip (las rutinas de dibujo
            // del dossier, por ejemplo) hace falta la misma cuenta lineal->offset que se uso aqui,
            // y para leer los porcentajes hace falta saber que parte de la actividad se atribuyo.
            w.WriteLine($"  \"desplazamiento\": \"0x{desplaza:X}\",  \"_desplazamiento\": \"offset = lineal - desplazamiento\",");
            w.WriteLine($"  \"cobertura_pct\": {cobertura:F1}, \"eventos_atribuidos\": {eventosClasificables - eventosFuera}, \"eventos_fuera\": {eventosFuera},");
            w.WriteLine("  \"funciones\": {");
            bool primera = true;
            foreach (var c in prioridad.Append(Capa.Logica))
            {
                if (!reparto.TryGetValue(c, out var l)) continue;
                foreach (var f in l.OrderBy(x => x.Off))
                {
                    if (!primera) w.WriteLine(",");
                    primera = false;
                    string pruebas = string.Join("; ", f.Pruebas.Select(kv => $"{kv.Key}: {kv.Value}"));
                    w.Write($"    \"{f.Nombre}\": {{\"capa\": \"{c}\", \"offset\": \"0x{f.Off:X}\", \"eventos\": {f.Total}, " +
                            $"\"prueba\": \"{pruebas}\"}}");
                }
            }
            w.WriteLine();
            w.WriteLine("  }");
            w.WriteLine("}");
            Console.WriteLine($"\n-> {outPath}");
        }
        return 0;
    }

    static string? Opt(string[] a, string n)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == n) return a[i + 1];
        return null;
    }
}
