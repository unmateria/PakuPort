using System.Text.Json;
using PakuPort.Core;
using PakuPort.Core.Verify;
using PakuPort.Judge.Corpus;
using PakuPort.Judge.Oracle;

/// <summary>
/// `pakuport oracle-run` — LE PREGUNTA AL JUEGO ORIGINAL algo que nadie ha grabado antes.
///
/// Hasta ahora todo lo que ejecuta una función del original (`verify`, `trace`, `render`,
/// `interp-verify`) va contra casos YA GRABADOS: se puede repetir una pregunta vieja, no hacer una
/// nueva. Y hacer preguntas nuevas es la operación fundamental de un porteo: le pides al original
/// N respuestas, escribes tu versión, comparas. Verde o rojo, sin ojo y sin adivinar.
///
/// El motor ya estaba: el intérprete ejecuta los BYTES ORIGINALES con el DOS, los ficheros, el
/// vídeo y los overlays montados, y está medido byte-exacto contra el corpus (164/174 en
/// `interp-verify`). Lo único que faltaba era que la ENTRADA viniera de un fichero en vez de un
/// caso grabado. Esto es eso, y nada más — el mismo camino de ejecución, otra procedencia de la
/// entrada.
///
/// Por qué aquí y no en el oráculo de Unicorn que ya existe en el juez de casos (PakuPort.Judge): aquél es CPU y memoria
/// pelada. En cuanto la función abre un fichero, toca la pantalla o vive en un overlay, se queda
/// corto. Éste no.
///
/// Salida: JSON por stdout (o a `--out`), y opcionalmente un `.pfc` con `--pfc` que el juez de
/// casos (PakuPort.Judge) consume SIN TOCAR NADA — el bucle se cierra solo.
/// </summary>
static class OracleRun
{
    public static int Ejecutar(GameConfig game, string func, string gamePath,
                               string? entradaPath, string? baseIdx, string? outPath, string? pfcPath)
    {
        if (string.IsNullOrEmpty(func))
        {
            Console.Error.WriteLine("oracle-run: falta --func FUN_1000_xxxx");
            return 1;
        }

        int loadBase = game.LoadSegment * 16;
        int ghidra = FunctionVerifier.ParseOffset(func);

        // ---- el mundo de código: el MISMO que usa interp-verify ----
        // memimage.bin es el mundo en que el explorador grabó el corpus (con las relocs de EXEPACK
        // ya corridas); si no está, la imagen canónica del EXE.
        string exDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gamePath))!, "explorer");
        string miPath = Path.Combine(exDir, "memimage.bin");
        int bias = 0;
        byte[] codeBytes;
        int codeAt;
        if (File.Exists(miPath))
        {
            codeBytes = File.ReadAllBytes(miPath);
            codeAt = loadBase;
            string ijPath = Path.Combine(exDir, "image.json");
            if (File.Exists(ijPath))
            {
                using var ij = JsonDocument.Parse(File.ReadAllText(ijPath));
                if (ij.RootElement.TryGetProperty("memImage", out var mi) &&
                    mi.TryGetProperty("xOfGhidra0", out var xog))
                    bias = Convert.ToInt32(xog.GetString()!.Replace("0x", ""), 16);
            }
        }
        else
        {
            var exeBytes = File.ReadAllBytes(game.ExePath);
            ExePackDecompressor.PatchResidentThunks(exeBytes);
            var exe = new ExeImage(exeBytes);
            if (game.LoadSegment != 0) exe.ApplyMzRelocations(game.LoadSegment);
            codeBytes = exe.Bytes;
            codeAt = loadBase;
        }

        // Rango lineal del código residente, para poder avisar de un parche que lo pise.
        // `residentCodeEnd` va en el game.json y GameConfig no lo expone, así que se lee crudo
        // (igual que hace `dump-image`). Sin él, no se avisa: mejor callar que inventar el rango.
        int codeIni = loadBase + bias, codeEnd = 0;
        try
        {
            using var gd = JsonDocument.Parse(File.ReadAllText(gamePath));
            if (gd.RootElement.TryGetProperty("residentCodeEnd", out var rce))
                codeEnd = codeIni + Convert.ToInt32(rce.GetString()!.Replace("0x", ""), 16);
        }
        catch { }

        // ---- estado de partida ----
        // Con `--base N` se arranca del caso N del corpus: así el DGROUP es un estado REAL del
        // juego y no ceros, que es lo que hace que la respuesta signifique algo. Sin él se parte
        // de memoria limpia y hay que dar los registros a mano.
        CorpusCase? baseCase = null;
        if (!string.IsNullOrEmpty(game.CorpusDir))
        {
            var casos = FunctionVerifier.LoadCorpus(game.CorpusDir, func);
            if (casos.Count > 0)
            {
                int idx = int.TryParse(baseIdx, out var bi) ? bi : 0;
                if (idx < 0 || idx >= casos.Count)
                {
                    Console.Error.WriteLine($"oracle-run: --base {idx} fuera de rango (hay {casos.Count} casos)");
                    return 1;
                }
                baseCase = casos[idx];
            }
        }

        // GATE de config, copiado de `interp-verify`: si las coordenadas del caso no caen donde
        // este game.json dice que vive la función, es que se ha pasado la configuración
        // equivocada. Sin esto se ejecutaban 20 M de instrucciones sobre memoria vacía y se
        // devolvía un "difiere en 4 bytes" con toda la cara de respuesta buena. Un comando que
        // contesta basura sin avisar es peor que uno que no existe.
        if (baseCase != null && !func.StartsWith("DRV_", StringComparison.Ordinal))
        {
            var e0 = baseCase.EntryCpu;
            int esperado = loadBase + bias + ghidra;
            int real = e0.Cs * 16 + e0.Ip;
            if (real != esperado)
            {
                Console.Error.WriteLine(
                    $"oracle-run: el caso no cuadra con esta config — el corpus sitúa {func} en " +
                    $"0x{real:X5} (cs:ip={e0.Cs:X4}:{e0.Ip:X4}) y este game.json la espera en 0x{esperado:X5}." +
                    Environment.NewLine +
                    $"  Casi seguro es el game.json equivocado: prueba con la variante del explorador " +
                    $"(game.explorer.json) o con la canónica, según de dónde salga el corpus.");
                return 1;
            }
        }

        var mem = new Memory();
        Array.Copy(codeBytes, 0, mem.Raw, codeAt, Math.Min(codeBytes.Length, Memory.Size - codeAt));

        CpuState entrada;
        uint dsBase;
        int dgroupLen;
        if (baseCase != null)
        {
            entrada = baseCase.EntryCpu;
            // El ancla del DGROUP es la del ESTADO, no la del contenedor: es lo que usa
            // `interp-verify`, que es el camino medido byte-exacto.
            dsBase = baseCase.EntryCpu.DsBase != 0 ? baseCase.EntryCpu.DsBase : baseCase.DsBase;
            dgroupLen = baseCase.EntryMem.Length;
            Array.Copy(baseCase.EntryMem, 0, mem.Raw, (int)dsBase, dgroupLen);
        }
        else
        {
            entrada = new CpuState();
            entrada.Cs = (ushort)((loadBase + bias + ghidra) >> 4);
            entrada.Eip = (uint)((loadBase + bias + ghidra) & 0xF);
            dsBase = (uint)loadBase;
            dgroupLen = 0x10000;
        }

        // ---- la ENTRADA del usuario, encima del estado de partida ----
        var avisos = new List<string>();
        var preLlamadas = new List<(string nombre, int dir, List<(string, ushort)> regs)>();
        bool dsDado = false;
        if (!string.IsNullOrEmpty(entradaPath))
        {
            if (!File.Exists(entradaPath))
            {
                Console.Error.WriteLine($"oracle-run: no existe {entradaPath}");
                return 1;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(entradaPath));
            var raiz = doc.RootElement;
            if (raiz.TryGetProperty("registros", out var regs))
                foreach (var r in regs.EnumerateObject())
                {
                    ushort v = LeerU16(r.Value);
                    switch (r.Name.ToLowerInvariant())
                    {
                        case "ax": entrada.Ax = v; break;
                        case "bx": entrada.Bx = v; break;
                        case "cx": entrada.Cx = v; break;
                        case "dx": entrada.Dx = v; break;
                        case "si": entrada.Esi = v; break;
                        case "di": entrada.Edi = v; break;
                        case "bp": entrada.Ebp = v; break;
                        case "sp": entrada.Esp = v; break;
                        case "ds": entrada.Ds = v; dsDado = true; break;
                        case "es": entrada.Es = v; break;
                        case "ss": entrada.Ss = v; break;
                        case "flags": entrada.Eflags = v; break;
                        default: avisos.Add($"registro desconocido, ignorado: {r.Name}"); break;
                    }
                }
            // El re-anclaje va AQUÍ, ANTES de los parches: si se recalcula después, `devolver` lee
            // del sitio nuevo y `memoria` ha escrito en el viejo, así que se lee bien de un sitio
            // donde nunca se escribió. Con ds=0x1E6E eso desviaba cada parche 0xE6E0 bytes, de
            // lleno en el código del juego, y los síntomas (opcode inválido, o veinte millones de
            // instrucciones devolviendo el entorno de DOS) no apuntan a la causa ni de lejos.
            // Y sólo si el `ds` lo ha dado la entrada: el del caso base ya viene anclado por su
            // propio estado grabado, que no siempre es `ds*16`.
            if (dsDado)
            {
                dsBase = (uint)(entrada.Ds * 16);
                dgroupLen = Math.Min(0x10000, Memory.Size - (int)dsBase);
            }

            // Parches de memoria: {"en": "ds:0x1234"|"0x12345", "bytes": "aabbcc"}. Se aplican
            // DESPUÉS del DGROUP de partida, así que ganan ellos.
            if (raiz.TryGetProperty("memoria", out var parches))
                foreach (var p in parches.EnumerateArray())
                {
                    int at = ResolverDireccion(p.GetProperty("en").GetString()!, entrada, dsBase);
                    // Los bytes pueden venir en hex o DE UN FICHERO. Sin lo segundo, meter un .PIC
                    // de 30 KB exigiría 60.000 caracteres de hex escritos a mano, que es tanto como
                    // no poder hacerlo.
                    byte[] bytes = p.TryGetProperty("fichero", out var fEl)
                        ? File.ReadAllBytes(fEl.GetString()!)
                        : Convert.FromHexString(p.GetProperty("bytes").GetString()!.Replace(" ", ""));
                    if (at < 0 || at + bytes.Length > Memory.Size)
                    { avisos.Add($"parche fuera de la memoria, ignorado: {p.GetProperty("en").GetString()}"); continue; }
                    // En modo real un buffer mal colocado cae encima del código sin hacer ruido, y
                    // lo que se ve luego es un opcode imposible muy lejos de la causa. Avisar es
                    // gratis y mata ese diagnóstico de raíz.
                    // El rango se corta en el DGROUP: `residentCodeEnd` cubre la imagen residente
                    // entera (código Y datos) y en algún juego el DGROUP arranca dentro, así que sin
                    // cortarlo el aviso saltaría en el sitio donde hay que escribir — un aviso con
                    // falsos positivos enseña a ignorar los avisos, que es justo lo contrario.
                    int codeTope = Math.Min(codeEnd, (int)dsBase);
                    if (codeTope > codeIni && at < codeTope && at + bytes.Length > codeIni)
                        avisos.Add($"OJO: el parche {p.GetProperty("en").GetString()} (lineal 0x{at:X5}, "
                                 + $"{bytes.Length} bytes) cae DENTRO del código del programa "
                                 + $"(0x{codeIni:X5}..0x{codeTope:X5}) — lo estás machacando");
                    Array.Copy(bytes, 0, mem.Raw, at, bytes.Length);
                }

            // Funciones a ejecutar ANTES de la principal, en orden y sobre la misma memoria.
            // Sin esto no se puede preguntar por nada que tenga inicialización: el decodificador
            // de imágenes de un juego de prueba, por ejemplo, necesita que otra función siembre su diccionario
            // primero, y una función que sólo sabe ejecutarse suelta no puede contestar a eso.
            if (raiz.TryGetProperty("llamar_antes", out var pre))
                foreach (var p in pre.EnumerateArray())
                {
                    string nom = p.ValueKind == JsonValueKind.String
                        ? p.GetString()! : p.GetProperty("funcion").GetString()!;
                    var regs2 = new List<(string, ushort)>();
                    if (p.ValueKind != JsonValueKind.String && p.TryGetProperty("registros", out var r2))
                        foreach (var x in r2.EnumerateObject()) regs2.Add((x.Name.ToLowerInvariant(), LeerU16(x.Value)));
                    preLlamadas.Add((nom, loadBase + bias + FunctionVerifier.ParseOffset(nom), regs2));
                }
        }

        // ---- ejecutar ----
        var dgroupAntes = new byte[dgroupLen];
        Array.Copy(mem.Raw, (int)dsBase, dgroupAntes, 0, dgroupLen);

        var cpu = Cpu.FromState(entrada);
        cpu.LoadBase = loadBase;
        cpu.ImageBias = bias;
        // Los EFECTOS grabados del caso (paleta, VRAM, puertos, INT). Sin ellos una función que
        // pide algo al exterior recibe silencio y su respuesta no es la del original — es lo que
        // hacía que la autocomprobación saliera roja. Sólo aplican mientras la entrada sea la
        // grabada: con una entrada NUEVA, los efectos viejos ya no corresponden y se dice.
        bool efectosGrabados = baseCase != null && baseCase.EffectsJson.Length > 0
                               && string.IsNullOrEmpty(entradaPath);
        if (efectosGrabados)
        {
            var plat = new PakuPort.Core.Verify.EffectsReplayPlatform(baseCase!.EffectsJson);
            plat.ApplyPre(mem);
            cpu.Platform = plat;
        }
        else if (baseCase != null && baseCase.EffectsJson.Length > 0)
            avisos.Add("la entrada cambia respecto al caso base: los efectos grabados NO se aplican, "
                       + "asi que lo que la funcion pida al exterior no tendra respuesta");
        mem.IpLog = new List<int>();    // el camino recorrido: es la cobertura, y sale gratis

        string? error = null;
        Interp.FarDispatchFn miniFar = (c2, m2, seg, off) =>
            c2.Platform is PakuPort.Core.Platform.IFarCallSink s && s.TryFarCall(seg, off, c2, m2);

        // Las de `llamar_antes`, en orden y sobre la MISMA memoria y la misma CPU: lo que deja
        // una lo ve la siguiente, que es justo lo que hace el juego. Si una revienta se para
        // aquí — seguir ejecutando la principal sobre un estado a medio construir daría una
        // respuesta con toda la pinta de buena y sin nada detrás.
        foreach (var (nombre, dir, regs2) in preLlamadas)
        {
            foreach (var (n, v) in regs2) AplicaReg(cpu, n, v);
            // La pila se restaura tras cada previa. Entran sin dirección de retorno empujada, así
            // que su `ret` desapila lo que haya debajo y deja SP movido: quien monte a mano un
            // marco de argumentos para la función principal se lo encuentra desplazado y lee los
            // argumentos corridos. Una llamada previa es un efecto sobre la MEMORIA, no sobre la
            // pila de quien pregunta.
            ushort ss0 = cpu.SS, sp0 = cpu.SP;
            try { Interp.Run(cpu, mem, (ushort)(dir >> 4), (ushort)(dir & 0xF), null, miniFar); }
            catch (Exception ex)
            {
                error = $"en la llamada previa {nombre}: {ex.GetType().Name}: {ex.Message}";
                break;
            }
            cpu.SS = ss0; cpu.SP = sp0;
        }

        // El camino recorrido es el de la función PREGUNTADA: el de las previas ya se ha andado
        // y mezclarlos daría una cobertura que no es de nadie.
        mem.IpLog = new List<int>();
        if (error == null)
            try { Interp.Run(cpu, mem, entrada.Cs, entrada.Ip, null, miniFar); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

        var salida = AEstado(cpu, entrada);
        var dgroupDespues = new byte[dgroupLen];
        Array.Copy(mem.Raw, (int)dsBase, dgroupDespues, 0, dgroupLen);

        // ---- respuesta ----
        var escrituras = new List<object>();
        int tramosTotales = 0, bytesTotales = 0;
        for (int i = 0; i < dgroupLen; )
        {
            if (dgroupAntes[i] == dgroupDespues[i]) { i++; continue; }
            int ini = i;
            while (i < dgroupLen && dgroupAntes[i] != dgroupDespues[i]) i++;
            tramosTotales++;
            bytesTotales += i - ini;
            if (escrituras.Count < 256)
            {
                int corte = Math.Min(i - ini, 64);
                escrituras.Add(new
                {
                    en = $"ds:0x{ini:X4}",
                    lineal = $"0x{dsBase + ini:X5}",
                    antes = Convert.ToHexString(dgroupAntes, ini, corte),
                    ahora = Convert.ToHexString(dgroupDespues, ini, corte),
                    bytes = i - ini,
                    // Truncar está bien; truncar EN SILENCIO no. Si el hex no trae los `bytes` que
                    // declara, se dice en el propio tramo y se remite al volcado íntegro.
                    truncado = i - ini > corte ? $"sí: {corte} de {i - ini} bytes; el tramo entero va en el caso que escribe --pfc" : null,
                });
            }
        }

        // El camino: direcciones ÚNICAS visitadas, en orden. Con esto una respuesta del oráculo
        // dice además POR DÓNDE pasó — que es la mitad de la cobertura de ramas.
        var camino = new List<string>();
        var vistos = new HashSet<int>();
        foreach (int e in mem.IpLog!)
        {
            int ip = e & 0xFFFF;
            if (vistos.Add(ip) && camino.Count < 4096) camino.Add($"0x{ip:X4}");
        }

        // DEUDA CONOCIDA, medida y dicha: este contraste compara el DGROUP ENTERO byte a byte y
        // varios registros, y es MÁS ESTRICTO que el juez oficial (`interp-verify`, que usa el
        // DiffEngine del juez de casos (PakuPort.Judge) y sabe qué zonas son volátiles). Sobre el
        // corpus de un juego de prueba: 28 verdes de 40 aquí, contra 164 de 174 del juez. La diferencia es de criterio,
        // no del motor — son el mismo intérprete y el mismo mundo. Cambiar esto por el DiffEngine
        // haría que un VERDE aquí signifique exactamente lo mismo que allí.
        //
        // AUTOCOMPROBACIÓN, y es lo que hace que esto valga de algo: si se partió de un caso
        // grabado y NO se cambió nada de la entrada, la respuesta tiene que ser la que el original
        // dio en su día. Si no coincide, el oráculo está mintiendo y hay que saberlo AQUÍ, no
        // cuando alguien haya escrito medio port contra él.
        string? contraste = null;
        if (baseCase != null && string.IsNullOrEmpty(entradaPath))
        {
            bool memOk = dgroupDespues.AsSpan().SequenceEqual(baseCase.ExitMem);
            var e = baseCase.ExitCpu;
            bool cpuOk = salida.Ax == e.Ax && salida.Bx == e.Bx && salida.Cx == e.Cx && salida.Dx == e.Dx
                      && (ushort)salida.Esi == (ushort)e.Esi && (ushort)salida.Edi == (ushort)e.Edi
                      && salida.Ds == e.Ds && salida.Es == e.Es;
            if (memOk && cpuOk) contraste = "VERDE: identico al caso grabado del original";
            else
            {
                // Un ROJO sin detalle no sirve: se dice QUE registros y CUANTOS bytes.
                var d = new List<string>();
                if (salida.Ax != e.Ax) d.Add($"ax {salida.Ax:X4}!={e.Ax:X4}");
                if (salida.Bx != e.Bx) d.Add($"bx {salida.Bx:X4}!={e.Bx:X4}");
                if (salida.Cx != e.Cx) d.Add($"cx {salida.Cx:X4}!={e.Cx:X4}");
                if (salida.Dx != e.Dx) d.Add($"dx {salida.Dx:X4}!={e.Dx:X4}");
                if ((ushort)salida.Esi != (ushort)e.Esi) d.Add($"si {(ushort)salida.Esi:X4}!={(ushort)e.Esi:X4}");
                if ((ushort)salida.Edi != (ushort)e.Edi) d.Add($"di {(ushort)salida.Edi:X4}!={(ushort)e.Edi:X4}");
                if (salida.Ds != e.Ds) d.Add($"ds {salida.Ds:X4}!={e.Ds:X4}");
                if (salida.Es != e.Es) d.Add($"es {salida.Es:X4}!={e.Es:X4}");
                int bytesDif = 0, primero = -1;
                int n2 = Math.Min(dgroupDespues.Length, baseCase.ExitMem.Length);
                for (int i2 = 0; i2 < n2; i2++)
                    if (dgroupDespues[i2] != baseCase.ExitMem[i2]) { bytesDif++; if (primero < 0) primero = i2; }
                contraste = $"ROJO: difiere del caso grabado — cpu: {(d.Count == 0 ? "ok" : string.Join(" ", d))}"
                          + $"; memoria: {bytesDif} bytes distintos"
                          + (primero >= 0 ? $", el primero en ds:0x{primero:X4}" : "")
                          + (dgroupDespues.Length != baseCase.ExitMem.Length
                             ? $" (LONGITUDES DISTINTAS: {dgroupDespues.Length} vs {baseCase.ExitMem.Length})" : "");
            }
        }

        // Trozos que el llamante pide de vuelta ENTEROS y en binario. El JSON vale para mirar; para
        // comparar un buffer de 64.000 bytes contra tu decodificador hace falta el binario, y
        // trocearlo en hex dentro del JSON no es una respuesta, es un rompecabezas.
        var devueltos = new List<object>();
        if (!string.IsNullOrEmpty(entradaPath))
        {
            using var doc2 = JsonDocument.Parse(File.ReadAllText(entradaPath));
            if (doc2.RootElement.TryGetProperty("devolver", out var pedidos))
                foreach (var d in pedidos.EnumerateArray())
                {
                    int at = ResolverDireccion(d.GetProperty("en").GetString()!, salida, dsBase);
                    int n3 = d.GetProperty("bytes").GetInt32();
                    if (at < 0 || n3 <= 0 || at + n3 > Memory.Size)
                    { avisos.Add($"'devolver' fuera de la memoria, ignorado: {d.GetProperty("en").GetString()}"); continue; }
                    var buf = new byte[n3];
                    Array.Copy(mem.Raw, at, buf, 0, n3);
                    string? aFich = d.TryGetProperty("a", out var aEl) ? aEl.GetString() : null;
                    if (!string.IsNullOrEmpty(aFich)) File.WriteAllBytes(aFich, buf);
                    devueltos.Add(new
                    {
                        en = d.GetProperty("en").GetString(),
                        lineal = $"0x{at:X5}",
                        bytes = n3,
                        fichero = aFich,
                        // sin fichero, hasta 256 bytes en hex; más que eso pide un fichero y se dice
                        hex = aFich == null ? Convert.ToHexString(buf, 0, Math.Min(n3, 256)) : null,
                        truncado = aFich == null && n3 > 256 ? $"sí: 256 de {n3}, usa \"a\": \"fichero.bin\"" : null,
                    });
                }
        }

        var resp = new
        {
            func,
            contraste,
            ghidra_offset = $"0x{ghidra:X}",
            ok = error == null,
            error,
            avisos,
            base_corpus = baseCase != null ? (object)(baseIdx ?? "0") : "ninguno (memoria limpia)",
            // El ancla con la que se han resuelto TODOS los "ds:...", de entrada y de salida.
            // Iba implícita, y una dirección implícita que no cuadra es media tarde de nadie.
            ds_base = $"0x{dsBase:X5}",
            codigo = codeEnd > codeIni ? $"0x{codeIni:X5}..0x{codeEnd:X5}" : null,
            entrada = Registros(entrada),
            salida = Registros(salida),
            instrucciones = mem.IpLog!.Count,
            tramos_escritos = tramosTotales,
            bytes_escritos = bytesTotales,
            tramos_omitidos = Math.Max(0, tramosTotales - escrituras.Count),
            direcciones_distintas = vistos.Count,
            camino,
            memoria_escrita = escrituras,
            devuelto = devueltos,
        };
        string json = JsonSerializer.Serialize(resp, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrEmpty(outPath)) { File.WriteAllText(outPath, json); Console.WriteLine($"oracle-run -> {outPath}"); }
        else Console.WriteLine(json);

        // Un `.pfc` con la pregunta y su respuesta: el juez de casos (PakuPort.Judge) lo consume tal cual, así
        // que una respuesta del oráculo se convierte en un caso de prueba permanente sin traducir
        // nada por el camino.
        if (!string.IsNullOrEmpty(pfcPath))
        {
            var caso = new CorpusCase
            {
                GhidraOffset = ghidra,
                CallDepth = 0,
                DsBase = dsBase,
                EntryCpu = entrada,
                ExitCpu = salida,
                EntryMem = dgroupAntes,
                ExitMem = dgroupDespues,
                EffectsJson = "",
            };
            File.WriteAllBytes(pfcPath, caso.Serialize());
            Console.WriteLine($"caso grabado -> {pfcPath}");
        }
        return error == null ? 0 : 2;
    }

    /// <summary>El estado de salida del intérprete como CpuState. El núcleo tiene el camino de
    /// ida (`Cpu.FromState`) pero no el de vuelta, y hace falta para poder grabar el `.pfc`. Las
    /// bases de segmento se arrastran de la entrada: en modo real no cambian.</summary>
    static void AplicaReg(Cpu c, string nombre, ushort v)
    {
        switch (nombre)
        {
            case "ax": c.AX = v; break; case "bx": c.BX = v; break;
            case "cx": c.CX = v; break; case "dx": c.DX = v; break;
            case "si": c.SI = v; break; case "di": c.DI = v; break;
            case "bp": c.BP = v; break; case "sp": c.SP = v; break;
            case "ds": c.DS = v; break; case "es": c.ES = v; break;
            case "ss": c.SS = v; break; case "flags": c.SetFlagsWord(v); break;
        }
    }

    static CpuState AEstado(Cpu c, in CpuState entrada)
    {
        var s = entrada;
        s.Eax = c.AX; s.Ebx = c.BX; s.Ecx = c.CX; s.Edx = c.DX;
        s.Esi = c.SI; s.Edi = c.DI; s.Ebp = c.BP; s.Esp = c.SP;
        s.Cs = c.CS; s.Ds = c.DS; s.Es = c.ES; s.Ss = c.SS;
        s.Eflags = c.FlagsWord();
        return s;
    }

    static object Registros(CpuState s) => new
    {
        ax = $"0x{s.Ax:X4}", bx = $"0x{s.Bx:X4}", cx = $"0x{s.Cx:X4}", dx = $"0x{s.Dx:X4}",
        si = $"0x{(ushort)s.Esi:X4}", di = $"0x{(ushort)s.Edi:X4}", bp = $"0x{(ushort)s.Ebp:X4}", sp = $"0x{(ushort)s.Esp:X4}",
        ds = $"0x{s.Ds:X4}", es = $"0x{s.Es:X4}", ss = $"0x{s.Ss:X4}",
        cs = $"0x{s.Cs:X4}", ip = $"0x{s.Ip:X4}", flags = $"0x{(ushort)s.Eflags:X4}",
    };

    static ushort LeerU16(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? (ushort)Convert.ToInt32(e.GetString()!.Replace("0x", ""), 16)
        : (ushort)e.GetInt32();

    /// <summary>"ds:0x1234" (relativo al DGROUP), "es:0x10" o "0x12345" (lineal absoluta).</summary>
    static int ResolverDireccion(string s, CpuState cpu, uint dsBase)
    {
        s = s.Trim();
        int c = s.IndexOf(':');
        if (c < 0) return Convert.ToInt32(s.Replace("0x", ""), 16);
        string seg = s[..c].ToLowerInvariant();
        int off = Convert.ToInt32(s[(c + 1)..].Replace("0x", ""), 16);
        return seg switch
        {
            "ds" => (int)dsBase + off,
            "es" => cpu.Es * 16 + off,
            "ss" => cpu.Ss * 16 + off,
            "cs" => cpu.Cs * 16 + off,
            _ => Convert.ToInt32(seg.Replace("0x", ""), 16) * 16 + off,
        };
    }
}
