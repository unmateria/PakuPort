using PakuPort.Core;
using PakuPort.Core.Verify;
using PakuPort.Judge.Corpus;

/// <summary>
/// POR QUÉ UN CASO DEL CORPUS NO SE REPRODUCE — el diagnóstico que separa "la traducción está
/// mal" de "el caso no mide nada".
///
/// <para>El banco de pruebas por función se apoya entero en que
/// el binario ORIGINAL reproduzca sus propios casos grabados. Medido en un juego de prueba, no lo
/// hacía en la mitad: 84 de 231 funciones de su corpus. Mientras eso siga así, ninguna de
/// esas funciones mide a quien la traduce — mide un agujero del corpus.</para>
///
/// <para>Este comando ejecuta cada caso con el intérprete (que ES el original) y lo CLASIFICA por
/// síntoma, en vez de dejar una lista de divergencias que hay que mirar a ojo una por una. La
/// clasificación no interpreta: cada clase es una condición medible sobre la ejecución
/// (excepción, dónde acabó el flujo, si la pila volvió a su sitio, si los efectos grabados se
/// consumieron en orden, y qué diverge).</para>
///
/// <code>
/// pakuport corpus-diag --corpus games/&lt;id&gt;/corpus            (todas: el cuadro por clase)
/// pakuport corpus-diag --corpus games/&lt;id&gt;/corpus --func 177d0 [--case 0]
/// pakuport corpus-diag --corpus games/&lt;id&gt;/corpus --func d57e --case 0 ///     --traza 400 --traza-desde 2403900 --traza-regs      (la ventana donde descarrila)
/// </code>
/// </summary>
static class CorpusDiag
{
    /// <summary>Una clase de fallo. El orden es el de diagnóstico: la primera que se cumple manda,
    /// porque un descarrilamiento produce divergencias de memoria a montones y contarlas como
    /// "memoria" escondería la causa.</summary>
    public enum Clase
    {
        Verde,
        Excepcion,          // el intérprete no pudo seguir (opcode, acceso imposible)
        SinFin,             // se comió el presupuesto de pasos: no retornó nunca
        PilaDesbordada,     // SP acabó MUY por debajo: la función no retornó, se comió el DGROUP
        NoRetorna,          // acabó, pero no en la dirección de retorno que el caso grabó
        DesyncEfectos,      // pidió un efecto que no tocaba: la cola grabada no encaja
        EfectosDeMas,       // retornó dejando efectos grabados sin consumir
        Registro,           // memoria idéntica, un registro distinto
        Memoria,            // diverge la memoria
    }

    public sealed class Resultado
    {
        public required string Func;
        public required int Caso;
        public Clase Clase;
        public string Detalle = "";
        public long Insns;
        public int Desyncs, Consumidos, TotalFx;
        public int IrqsEntregadas, IrqsTotal;
        public List<string> QueFalto = new();
        public BancoCiego.Rastro? Rastro;
        public ushort SpFin, SpEsperado;
        public string Hist = "";
        public List<string> Traza = new();
    }

    public static int Ejecutar(string[] a)
    {
        string? corpusDir = Opt(a, "--corpus");
        if (corpusDir == null || !Directory.Exists(corpusDir))
        {
            Console.WriteLine(
                "pakuport corpus-diag --corpus <dir> [--func <FUN_1000_xxxx|xxxx>] [--case <n>]\n" +
                "    Dice POR QUE el binario original no reproduce un caso grabado, clasificado\n" +
                "    por sintoma. Sin --func recorre el corpus entero y saca el cuadro por clase.");
            return 2;
        }
        // Con --game se monta el mundo EXACTAMENTE como lo monta `interp-verify` (mundo.bin >
        // memimage > imagen canónica), que es lo que permite diagnosticar cualquier juego
        // y no sólo los que dejan mundo.bin.
        BancoCiego.Mundo mundo;
        string? gamePath = Opt(a, "--game");
        if (gamePath != null)
        {
            var mj = BancoCiego.MundoDeJuego(GameConfig.Load(gamePath), gamePath, corpusDir);
            if (mj == null) return 2;
            mundo = mj.Value.mundo;
            Console.WriteLine($"   (mundo: {mundo.Descripcion}, loadBase 0x{mundo.LoadBase:X5}, bias 0x{mundo.Bias:X})");
        }
        else
        {
            string mundoPath = Path.Combine(corpusDir, "mundo.bin");
            if (!File.Exists(mundoPath))
            { Console.Error.WriteLine($"falta {mundoPath}: pásale --game <game.json> para montar el mundo del juego"); return 2; }
            mundo = new BancoCiego.Mundo
            {
                CodeBytes = File.ReadAllBytes(mundoPath),
                CodeAt = 0, LoadBase = 0, Bias = 0, Descripcion = "mundo.bin", CorpusDir = corpusDir,
            };
        }

        string? soloFunc = Opt(a, "--func");
        if (soloFunc != null && !soloFunc.StartsWith("FUN_", StringComparison.Ordinal))
            soloFunc = "FUN_1000_" + soloFunc.TrimStart('0', 'x', 'X').ToLowerInvariant();
        int? soloCaso = int.TryParse(Opt(a, "--case"), out int nc) ? nc : null;
        int traza = int.TryParse(Opt(a, "--traza"), out int nt) ? nt : 0;
        // LA VENTANA DE LA TRAZA. Un caso que descarrila a los 2,4 M de instrucciones no se
        // diagnostica desde la entrada: `--traza-desde` salta hasta donde interesa y
        // `--traza-regs` añade los registros, que es lo único que hace legible un recorrido de
        // punteros (una lista enlazada de heaps se lee por sus DS:BX, no por sus mnemónicos).
        long trazaDesde = long.TryParse(Opt(a, "--traza-desde"), out long ntd) && ntd > 0 ? ntd : 0;
        bool trazaRegs = Array.IndexOf(a, "--traza-regs") >= 0;
        // El presupuesto de pasos, que por defecto es el mismo del runtime. Bajarlo hace el
        // barrido MUCHO más rápido (un caso que no retorna cuesta el tope entero) a cambio de que
        // un caso legítimamente largo salga como SinFin: por eso se dice en la cabecera.
        long presupuesto = long.TryParse(Opt(a, "--presupuesto"), out long pb) && pb > 0 ? pb : 20_000_000;

        var todo = new List<Resultado>();
        foreach (var d in Directory.GetDirectories(corpusDir).OrderBy(x => x, StringComparer.Ordinal))
        {
            string func = Path.GetFileName(d);
            if (soloFunc != null && !func.Equals(soloFunc, StringComparison.OrdinalIgnoreCase)) continue;
            var casos = FunctionVerifier.LoadCorpus(corpusDir, func);
            for (int i = 0; i < casos.Count; i++)
            {
                if (soloCaso != null && i != soloCaso) continue;
                todo.Add(Diagnostica(mundo, func, i, casos[i], traza, presupuesto, trazaDesde, trazaRegs));
            }
        }
        if (todo.Count == 0) { Console.Error.WriteLine("no hay casos que diagnosticar"); return 2; }

        if (soloFunc != null) { Detalle(todo); return 0; }

        // El cuadro: por clase, y por función (una función es de la clase de su PRIMER caso malo,
        // que es el criterio con el que el juez la declara roja).
        Console.WriteLine($"== corpus-diag {corpusDir}: {todo.Count} casos de " +
                          $"{todo.Select(r => r.Func).Distinct().Count()} funciones " +
                          $"(presupuesto {presupuesto:N0} pasos) ==");
        Console.WriteLine("\n-- por CASO --");
        foreach (var g in todo.GroupBy(r => r.Clase).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-16} {g.Count(),5}");
        Console.WriteLine("\n-- por FUNCION (la del primer caso malo) --");
        var porFunc = todo.GroupBy(r => r.Func)
            .Select(g => g.FirstOrDefault(r => r.Clase != Clase.Verde) ?? g.First())
            .ToList();
        foreach (var g in porFunc.GroupBy(r => r.Clase).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {g.Key,-16} {g.Count(),5}   {string.Join(" ", g.Take(24).Select(r => r.Func.Replace("FUN_1000_", "")))}" +
                              (g.Count() > 24 ? " …" : ""));
        }
        Console.WriteLine("\n-- una linea por caso malo --");
        foreach (var r in todo.Where(r => r.Clase != Clase.Verde))
            Console.WriteLine($"  {r.Func.Replace("FUN_1000_", ""),-8} #{r.Caso} {r.Clase,-16} " +
                              $"insns={r.Insns,-9} fx={r.Consumidos}/{r.TotalFx} desync={r.Desyncs}" +
                              $"{(r.IrqsTotal > 0 ? $" irq={r.IrqsEntregadas}/{r.IrqsTotal}" : "")}" +
                              $"{(r.Rastro?.FarSaltados > 0 ? $" farSaltados={r.Rastro.FarSaltados}" : "")}  {r.Detalle}");
        return 0;
    }

    static void Detalle(List<Resultado> todo)
    {
        foreach (var r in todo)
        {
            Console.WriteLine($"== {r.Func} caso #{r.Caso}: {r.Clase} ==");
            Console.WriteLine($"   {r.Detalle}");
            Console.WriteLine($"   insns={r.Insns:N0}  efectos consumidos={r.Consumidos}/{r.TotalFx}  desyncs={r.Desyncs}" +
                              $"  IRQ entregadas={r.IrqsEntregadas}/{r.IrqsTotal}" +
                              $"  far-calls saltados (fuera del mundo garantizado)={r.Rastro?.FarSaltados ?? 0}");
            Console.WriteLine($"   SP: entrada->fin {r.SpEsperado:X4} -> {r.SpFin:X4}");
            foreach (var q in r.QueFalto) Console.WriteLine($"   desync: {q}");
            if (r.Hist.Length > 0) Console.WriteLine($"   caja negra: {r.Hist}");
            if (r.Traza.Count > 0)
            {
                Console.WriteLine($"   -- traza ({r.Traza.Count} instrucciones desde la entrada) --");
                foreach (var l in r.Traza) Console.WriteLine("      " + l);
            }
        }
    }

    /// <summary>Ejecuta UN caso con el intérprete y lo clasifica. Todo lo que se afirma aquí sale
    /// de la ejecución: no se adivina nada del cuerpo de la función.</summary>
    public static Resultado Diagnostica(BancoCiego.Mundo mundo, string func, int idx, CorpusCase caso,
                                        int traza = 0, long presupuesto = 20_000_000,
                                        long trazaDesde = 0, bool trazaRegs = false)
    {
        var r = new Resultado { Func = func, Caso = idx, SpEsperado = (ushort)caso.ExitCpu.Esp };
        long insn0 = Interp.InsnCount;
        EffectsReplayPlatform? plat = null;
        byte[]? dg = null; Cpu? cpu = null;
        // La traza se recoge con el gancho por instrucción que el intérprete ya tiene, y la
        // memoria es la del MONTAJE del caso: así lo que se desensambla es lo que se ejecutó de
        // verdad, incluido el código que el juego se hubiera reescrito.
        Memory? memCaso = null;
        var pasos = new List<uint>();
        var regs = new List<string>();
        Cpu? cpuViva = null;
        long vistos = 0;
        var anterior = Interp.OnStep;
        if (traza > 0) Interp.OnStep = (cs, ip) =>
        {
            if (vistos++ < trazaDesde || pasos.Count >= traza) return;
            pasos.Add((uint)((cs << 16) | ip));
            if (trazaRegs && cpuViva != null)
                regs.Add($"ax={cpuViva.AX:X4} bx={cpuViva.BX:X4} cx={cpuViva.CX:X4} dx={cpuViva.DX:X4} " +
                         $"si={cpuViva.SI:X4} di={cpuViva.DI:X4} bp={cpuViva.BP:X4} sp={cpuViva.SP:X4} " +
                         $"ds={cpuViva.DS:X4} es={cpuViva.ES:X4}");
        };
        try
        {
            var rastro = new BancoCiego.Rastro();
            r.Rastro = rastro;
            var correr = BancoCiego.ConElInterprete(mundo, caso, rastro);
            var (d, c) = BancoCiego.EjecutaCaso(mundo, caso,
                (cp, mm) => { memCaso = mm; cpuViva = cp; cp.Budget = presupuesto; correr(cp, mm); }, false, p => plat = p);
            dg = d; cpu = c;
        }
        catch (Exception ex)
        {
            r.Clase = Clase.Excepcion;
            r.Insns = Interp.InsnCount - insn0;
            string m = ex.Message;
            int corte = m.IndexOf(" hist=", StringComparison.Ordinal);
            r.Detalle = $"{ex.GetType().Name}: {(corte > 0 ? m[..corte] : m)}";
            r.Hist = corte > 0 ? m[(corte + 6)..] : "";
            Interp.OnStep = anterior;
            if (memCaso != null) r.Traza = Desensambla(memCaso, pasos, regs);
            return r;
        }
        finally { Interp.OnStep = anterior; }
        r.Insns = Interp.InsnCount - insn0;
        r.Desyncs = plat?.Desyncs ?? 0;
        if (plat != null) r.QueFalto = plat.DetalleDesyncs;
        r.Consumidos = plat?.Consumed ?? 0;
        r.TotalFx = plat?.Total ?? 0;
        r.IrqsEntregadas = plat?.IrqsEntregadas ?? 0;
        r.IrqsTotal = plat?.IrqsTotal ?? 0;
        r.SpFin = cpu!.SP;
        r.Hist = Interp.HistDumpPublico();
        if (memCaso != null) r.Traza = Desensambla(memCaso, pasos, regs);

        // El presupuesto de pasos agotado NO es una divergencia: es que la función no retornó
        // nunca. Decirlo como "memoria" escondería la causa detrás de sus propios destrozos.
        if (cpu.Budget < 0)
        {
            r.Clase = Clase.SinFin;
            // Un bucle CERRADO —la caja negra entera en un puñado de direcciones— no es lentitud:
            // es una espera de algo que en el replay no llega nunca. Las interrupciones de
            // hardware no son efectos grabados, así que una función que espera al timer o al
            // teclado no se puede reproducir con esta cola; decirlo por su nombre separa ese
            // límite conocido de un descarrilamiento de verdad.
            int distintas = r.Hist.Split('<').Distinct().Count();
            r.Detalle = $"se comió el presupuesto de {r.Insns:N0} pasos sin retornar; " +
                        (distintas <= 6
                            ? $"BUCLE CERRADO en {distintas} direccion(es) ({r.Hist.Split('<')[0]}): " +
                              (r.IrqsTotal == 0
                                  ? "espera algo que el replay no le puede dar (el caso NO trae IRQ grabadas)"
                                  : $"el caso trae {r.IrqsTotal} IRQ y se entregaron {r.IrqsEntregadas}")
                            : $"{distintas} direcciones distintas en la caja negra") +
                        (r.QueFalto.Count > 0 ? $" [{string.Join(" | ", r.QueFalto.Take(2))}]" : "");
            return r;
        }

        // La pila: si acabó MUY por debajo del SP de entrada, la función nunca retornó y lo que
        // hay en el DGROUP son sus propios marcos de pila. Es la causa del "todo el DGROUP
        // diverge desde el offset 0" y hay que decirla por su nombre, no como divergencia.
        int spEntrada = (int)(caso.EntryCpu.Esp & 0xFFFF);
        if (r.SpFin < spEntrada - 0x40)
        {
            r.Clase = Clase.PilaDesbordada;
            r.Detalle = $"SP entrada={spEntrada:X4} fin={r.SpFin:X4} ({spEntrada - r.SpFin} bytes empujados y no sacados)" +
                        (r.QueFalto.Count > 0 ? $" [{string.Join(" | ", r.QueFalto.Take(2))}]" : "");
            return r;
        }

        string? mal = BancoCiego.ComparaSalida(caso, dg!, cpu);
        if (mal == null) { r.Clase = Clase.Verde; return r; }

        // Un desync es una acusación fuerte y va antes que la divergencia que produce: significa
        // que el código pidió un efecto que la cola grabada no tenía en ese punto.
        if (r.Desyncs > 0)
        {
            r.Clase = Clase.DesyncEfectos;
            r.Detalle = $"{r.Desyncs} efecto(s) pedidos fuera de orden [{string.Join(" | ", r.QueFalto.Take(3))}]; {mal}";
            return r;
        }
        if (r.TotalFx > 0 && r.Consumidos < r.TotalFx)
        {
            r.Clase = Clase.EfectosDeMas;
            r.Detalle = $"quedaron {r.TotalFx - r.Consumidos} efecto(s) sin consumir; {mal}";
            return r;
        }
        r.Clase = mal.StartsWith("memoria", StringComparison.Ordinal) ? Clase.Memoria : Clase.Registro;
        r.Detalle = mal;
        return r;
    }

    /// <summary>Desensambla los CS:IP que la ejecución pisó, leyendo los bytes de la MEMORIA del
    /// caso. Marca con `>` la primera instrucción de cada tramo nuevo para que se vea el salto.</summary>
    static List<string> Desensambla(Memory m, List<uint> pasos, List<string>? regs = null)
    {
        var salida = new List<string>();
        var fmt = new Iced.Intel.NasmFormatter();
        uint siguiente = uint.MaxValue;
        for (int k = 0; k < pasos.Count; k++)
        {
            uint pc = pasos[k];
            string reg = regs != null && k < regs.Count ? "   | " + regs[k] : "";
            ushort cs = (ushort)(pc >> 16), ip = (ushort)pc;
            int lin = (cs << 4) + ip;
            if (lin < 0 || lin + 16 > m.Raw.Length) { salida.Add($"{cs:X4}:{ip:X4}  <fuera de memoria>{reg}"); continue; }
            var dec = Iced.Intel.Decoder.Create(16, new Iced.Intel.ByteArrayCodeReader(m.Raw, lin, 16), ip,
                                                Iced.Intel.DecoderOptions.None);
            var insn = dec.Decode();
            var so = new Iced.Intel.StringOutput();
            fmt.Format(in insn, so);
            salida.Add($"{(pc == siguiente ? " " : ">")}{cs:X4}:{ip:X4}  {so.ToStringAndReset(),-34}{reg}");
            siguiente = (uint)((cs << 16) | (ushort)insn.NextIP);
        }
        return salida;
    }

    static string? Opt(string[] a, string n)
    {
        int i = Array.IndexOf(a, n);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
