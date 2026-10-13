using PakuPort.Core;
using PakuPort.Core.Verify;
using PakuPort.Judge.Corpus;
using PakuPort.Judge.Oracle;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// EL BANCO CIEGO — la vara con la que se mide si el expediente sirve de algo
/// del expediente.
///
/// <para>Un conjunto de funciones CONGELADO. A cada una se le entrega a una IA sin contexto del
/// proyecto el material disponible, se le pide reescribirla en C# idiomático, y su respuesta se
/// compila y se ejecuta contra el corpus de esa función. Sale un número: verde / rojo / no lo
/// intentó. Se mide con el material de hoy y se repite al final de la semana con el expediente;
/// la diferencia entre los dos números ES el valor del trabajo, y no admite opinión.</para>
///
/// <para><b>El montaje de un caso vive aquí y lo usa también <c>interp-verify</c></b>: son la
/// misma operación (poner el mundo, el DGROUP, el contexto y los efectos de un caso en una
/// <see cref="Memory"/> limpia) y tenerla dos veces es la clase de duplicado que ya rompió los
/// exports una vez.</para>
/// </summary>
static class BancoCiego
{
    /// <summary>El mundo de código y las coordenadas con las que se reproduce un corpus.</summary>
    public sealed class Mundo
    {
        public required byte[] CodeBytes;
        public required int CodeAt;
        public required int LoadBase;
        public required int Bias;
        public byte[]? Snapshot;
        public required string Descripcion;
        /// <summary>Dónde vive el corpus: de ahí salen las ventanas de código por caso
        /// (<c>codigo/&lt;seg&gt;-&lt;huella&gt;.bin</c>), que son lo que hace reproducible un
        /// juego con overlays. Sin ella se monta sólo el mundo, como siempre.</summary>
        public string? CorpusDir;
    }

    static readonly Dictionary<string, byte[]?> _codigoCache = [];

    /// <summary>La ventana de código que el caso trae consigo, o null si no trae (corpus viejo),
    /// si el código cambió a media llamada (<c>codvolatil</c>) o si el fichero no está.</summary>
    static (byte[] bytes, int en)? VentanaDeCodigo(Mundo mundo, string effectsJson)
    {
        if (mundo.CorpusDir == null || effectsJson.Length == 0) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(effectsJson);
            if (!doc.RootElement.TryGetProperty("cod", out var cE)) return null;
            if (!doc.RootElement.TryGetProperty("codseg", out var sE)) return null;
            string nombre = cE.GetString()!;
            if (!_codigoCache.TryGetValue(nombre, out var bytes))
            {
                string ruta = Path.Combine(mundo.CorpusDir, "codigo", nombre);
                bytes = File.Exists(ruta) ? File.ReadAllBytes(ruta) : null;
                _codigoCache[nombre] = bytes;
            }
            return bytes == null ? null : (bytes, sE.GetInt32() << 4);
        }
        catch { return null; }
    }

    /// <summary>Monta un caso en memoria limpia y ejecuta <paramref name="correr"/> con la CPU ya
    /// puesta en la entrada de la función. Es el contrato del juez: entra el DGROUP del caso, sale
    /// el DGROUP resultante para que <c>DiffEngine</c> lo compare byte a byte.
    ///
    /// <para>Orden de montaje, y cada paso está ahí por una medida:
    /// <list type="number">
    /// <item>el <b>código</b> del mundo (sin pisar la ventana del DGROUP);</item>
    /// <item>el <b>DGROUP</b> del caso, que es la verdad de su estado de entrada;</item>
    /// <item>el <b>contexto</b> (`pre`): lo que la función leyó fuera del DGROUP. Sin esto, un
    ///   caso sólo se reproduce si la foto del mundo es del mismo instante — medido: 75 % de
    ///   verdes con el mundo de hace 8 s contra 47 % con el de hace 25 s;</item>
    /// <item>los <b>efectos</b> (INT y `in`), que es lo que hace que un `int 1Ah` esperando el
    ///   reloj no gire para siempre.</item>
    /// </list></para></summary>
    public static PakuPort.Judge.Diff.PortLogicFunction MontaPuerto(
        Mundo mundo, string effectsJson, bool esDriver, Action<Cpu, Memory> correr)
        => (byte[] dgroup, in CpuState entry) =>
        {
            var mem = new Memory();
            int dsB = (int)entry.DsBase;
            int dgLo = dsB, dgHi = dsB + dgroup.Length;

            void CopyCode(byte[] src, int at, int len)
            {
                int lo = at, hi = Math.Min(at + len, Memory.Size);
                int pre = Math.Min(hi, dgLo);
                if (pre > lo) Array.Copy(src, 0, mem.Raw, lo, pre - lo);
                if (hi > dgHi && dgHi >= lo) Array.Copy(src, dgHi - lo, mem.Raw, dgHi, hi - dgHi);
                else if (dgHi < lo) Array.Copy(src, 0, mem.Raw, lo, hi - lo);
            }
            if (esDriver && mundo.Snapshot != null) CopyCode(mundo.Snapshot, 0, 0xA0000);
            else CopyCode(mundo.CodeBytes, mundo.CodeAt, mundo.CodeBytes.Length);
            // La ventana de código DEL CASO va encima del mundo: el mundo.bin es la foto del
            // final de la partida y en un juego con overlays ese segmento aloja ya otra cosa.
            if (VentanaDeCodigo(mundo, effectsJson) is (byte[] codB, int codEn))
                CopyCode(codB, codEn, codB.Length);

            Array.Copy(dgroup, 0, mem.Raw, dsB, dgroup.Length);

            var cpu = Cpu.FromState(entry);
            cpu.LoadBase = mundo.LoadBase;
            cpu.ImageBias = mundo.Bias;
            if (effectsJson.Length > 0)
            {
                var plat = new EffectsReplayPlatform(effectsJson);
                plat.ApplyPre(mem);          // el contexto del caso, encima del mundo
                cpu.Platform = plat;
            }
            correr(cpu, mem);
            Array.Copy(mem.Raw, dsB, dgroup, 0, dgroup.Length);
        };


    /// <summary>Monta el MUNDO de código de un juego a partir de su <c>game.json</c>, en orden de
    /// fidelidad: el <c>mundo.bin</c> que el grabador de corpus deja junto a los casos > el
    /// <c>memimage.bin</c> del explorador > la imagen canónica del EXE.
    ///
    /// <para>Vive aquí y no en <c>Program.cs</c> porque lo necesitan los TRES jueces
    /// (<c>interp-verify</c>, el banco ciego y <c>corpus-diag</c>), y tener el montaje tres veces
    /// es exactamente la clase de duplicado que ya rompió los exports una vez.</para></summary>
    public static (Mundo mundo, string corpusDir, string queMundo, byte[]? snapshot)? MundoDeJuego(
        GameConfig game, string gamePath, string? corpusOverride)
    {
        int loadBase = game.LoadSegment * 16;
        string corpusDir = !string.IsNullOrEmpty(corpusOverride) ? corpusOverride : game.CorpusDir;
        if (string.IsNullOrEmpty(corpusDir) || !Directory.Exists(corpusDir))
        { Console.Error.WriteLine($"no hay corpus que juzgar: {corpusDir}"); return null; }

        string exDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gamePath))!, "explorer");
        byte[]? memimage = File.Exists(Path.Combine(exDir, "memimage.bin"))
            ? File.ReadAllBytes(Path.Combine(exDir, "memimage.bin")) : null;
        byte[]? snapshot = File.Exists(Path.Combine(exDir, "gameplay_snapshot.bin"))
            ? File.ReadAllBytes(Path.Combine(exDir, "gameplay_snapshot.bin")) : null;
        int bias = 0;
        string ijPath = Path.Combine(exDir, "image.json");
        if (memimage != null && File.Exists(ijPath))
        {
            using var ijDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ijPath));
            if (ijDoc.RootElement.TryGetProperty("memImage", out var miE) &&
                miE.TryGetProperty("xOfGhidra0", out var xogE))
                bias = Convert.ToInt32(xogE.GetString()!.Replace("0x", ""), 16);
        }
        byte[] codeBytes;
        int codeAt;
        string mundoPath = Path.Combine(corpusDir, "mundo.bin");
        if (File.Exists(mundoPath)) { codeBytes = File.ReadAllBytes(mundoPath); codeAt = 0; }
        else if (memimage != null) { codeBytes = memimage; codeAt = loadBase; }
        else if (game.Format == "com")
        {
            // COM: imagen plana, sin cabecera ni relocs, a loadSeg:0100 como la carga el DOS.
            codeBytes = File.ReadAllBytes(game.ExePath);
            codeAt = loadBase + 0x100;
        }
        else
        {
            var exeBytes = File.ReadAllBytes(game.ExePath);
            ExePackDecompressor.PatchResidentThunks(exeBytes);
            var exe = new ExeImage(exeBytes);
            if (game.LoadSegment != 0) exe.ApplyMzRelocations(game.LoadSegment);
            int codeLen = Math.Min(exe.Length, OverlayDirectory.ResidentThreshold) - ExeImage.ResidentBase;
            codeBytes = new byte[codeLen];
            Array.Copy(exe.Bytes, ExeImage.ResidentBase, codeBytes, 0, codeLen);
            codeAt = loadBase;
        }
        string queMundo = File.Exists(mundoPath) ? "mundo.bin" : memimage != null ? "memimage" : "canónico";
        var mundo = new Mundo
        {
            CodeBytes = codeBytes, CodeAt = codeAt, LoadBase = loadBase, Bias = bias,
            Snapshot = snapshot, Descripcion = queMundo, CorpusDir = corpusDir,
        };
        return (mundo, corpusDir, queMundo, snapshot);
    }

    /// <summary>Lo que quedó sin ejecutar al reproducir un caso. Un número que hay que poder
    /// mirar: un caso que se saltó llamadas no está reproducido del todo, aunque salga verde.</summary>
    public sealed class Rastro { public int FarSaltados; }

    /// <summary>La memoria cuyo CÓDIGO el caso garantiza: su DGROUP (foto del instante) y la
    /// ventana de código que trae consigo. Fuera de ahí, lo que hay es el <c>mundo.bin</c> o el
    /// <c>memimage</c> —una foto de OTRO instante— y en un juego con overlays eso es otro
    /// programa.</summary>
    static List<(int ini, int fin)> Garantizado(Mundo mundo, CorpusCase caso)
    {
        var r = new List<(int, int)>
        {
            ((int)caso.EntryCpu.DsBase, (int)caso.EntryCpu.DsBase + caso.EntryMem.Length),
        };
        if (VentanaDeCodigo(mundo, caso.EffectsJson) is (byte[] b, int en)) r.Add((en, en + b.Length));
        return r;
    }

    /// <summary>Correr un caso con el INTÉRPRETE: la referencia. Un far-call usa el efecto "f"
    /// grabado si lo hay; si no, se EJECUTA como haría el hardware — pero sólo si el destino cae
    /// donde el caso garantiza el código.
    ///
    /// <para><b>Por qué esa condición y no ejecutar siempre.</b> Ejecutar siempre es lo fiel, y en
    /// el corpus de un juego de prueba —que se lleva su ventana de código— arregla funciones enteras:
    /// `b5a4` hacía `call far 1615:0000` y volvía sin ejecutar el thunk que hay ahí. Pero sobre un
    /// corpus cuyo mundo es una foto de otro instante, el destino tiene el overlay EQUIVOCADO y lo
    /// que se ejecuta es basura: medido en el corpus del explorador de otro juego de prueba (con
    /// overlays VROOMM), `53b5` saltaba a `1000:0160` (la tabla de thunks de VROOMM) y acababa ejecutando la
    /// IVT. Saltarse la llamada tampoco es correcto —el callee no corre— pero al menos no
    /// destroza el caso, y por eso se CUENTA en el rastro en vez de callarse.</para></summary>
    public static Action<Cpu, Memory> ConElInterprete(CpuState entry,
        List<(int ini, int fin)>? garantizado = null, Rastro? rastro = null) => (cpu, mem) =>
    {
        Interp.FarDispatchFn miniFar = (c2, m2, seg, off) =>
        {
            if (c2.Platform is PakuPort.Core.Platform.IFarCallSink s && s.TryFarCall(seg, off, c2, m2))
                return true;
            int lin = (seg << 4) + off;
            if (garantizado != null)
                foreach (var (ini, fin) in garantizado)
                    if (lin >= ini && lin < fin) return false;   // hay código de verdad: que lo interprete
            if (rastro != null) rastro.FarSaltados++;
            return true;   // fuera del mundo garantizado: se simula el retorno (y queda contado)
        };
        Interp.Run(cpu, mem, entry.Cs, entry.Ip, null, miniFar);
    };

    /// <summary>El <c>correr</c> de un caso, con su mundo garantizado ya resuelto. Es el que deben
    /// usar los jueces: construir el suyo a mano es cómo se pierde esa condición.</summary>
    public static Action<Cpu, Memory> ConElInterprete(Mundo mundo, CorpusCase caso, Rastro? rastro = null)
        => ConElInterprete(caso.EntryCpu, Garantizado(mundo, caso), rastro);

    /// <summary>Desensambla el CUERPO de una función desde el mundo de código: bloques básicos
    /// siguiendo los saltos, parando en <c>ret</c>/<c>retf</c>/<c>iret</c> o en un salto fuera del
    /// alcance. No sigue las llamadas — de eso se ocupa quien lea el desensamblado.
    ///
    /// <para>Se hace aquí y no con <c>Decoder16</c> porque aquél trabaja sobre la imagen del EXE, y
    /// el código que de verdad se ejecutó está en el <c>mundo.bin</c> del corpus: los overlays
    /// cargados, y lo que el juego descomprimió o movió en runtime.</para></summary>
    public static List<string> Desensambla(byte[] mundo, ushort cs, ushort ip, int maxInsn = 400)
    {
        var cuerpo = DesensamblaCuerpo(mundo, cs, ip, maxInsn);
        var porOffset = new SortedDictionary<ushort, string>();
        var fmt0 = new Iced.Intel.NasmFormatter();
        foreach (var kv in cuerpo.Insns)
        {
            // Una tabla de saltos vive DENTRO del cuerpo y NO es codigo: decodificarla produce
            // instrucciones inventadas (en un juego de prueba salian `pushf` / `ror si,0C1h` / `(bad)`), que es
            // justo el material con el que una IA se pone a traducir basura con aplomo.
            if (cuerpo.Tablas.Any(x => kv.Key >= x.En && kv.Key < x.En + x.Entradas.Length * 2)) continue;
            var salida = new Iced.Intel.StringOutput();
            var insn = kv.Value;
            fmt0.Format(in insn, salida);
            porOffset[kv.Key] = $"{kv.Key:X4}: {salida.ToStringAndReset()}";
        }
        // La tabla se anuncia SIEMPRE, este o no decodificada como instruccion: si el desensamblado
        // se paro en el `jmp`, esos bytes no aparecen por ningun otro sitio.
        foreach (var t in cuerpo.Tablas)
            porOffset[t.En] = $"{t.En:X4}: <TABLA DE SALTOS del `jmp` de arriba, {t.Entradas.Length} " +
                              "entradas de 2 bytes (la cota sale del `cmp`+`jbe` del propio codigo): " +
                              string.Join(" ", t.Entradas.Select(e => $"{e:X4}")) + ">";
        var lineas = porOffset.Values.ToList();
        foreach (var a in cuerpo.Avisos) lineas.Add("; " + a);
        return lineas;
    }

    /// <summary>Una tabla de saltos localizada en el cuerpo: donde empieza y a donde va cada
    /// entrada. La COTA no se adivina — sale del <c>cmp reg,N</c> + <c>jbe</c>/<c>jb</c> con el que
    /// el propio codigo valida el indice antes de saltar.</summary>
    public sealed class TablaSaltos
    {
        public ushort En;
        public ushort[] Entradas = [];
    }

    public sealed class Cuerpo
    {
        public SortedDictionary<ushort, Iced.Intel.Instruction> Insns = new();
        public List<TablaSaltos> Tablas = new();
        public List<string> Avisos = new();
    }

    public static SortedDictionary<ushort, Iced.Intel.Instruction> DesensamblaInsn(
        byte[] mundo, ushort cs, ushort ip, int maxInsn = 400)
        => DesensamblaCuerpo(mundo, cs, ip, maxInsn).Insns;

    /// <summary>Lo mismo, pero devolviendo las INSTRUCCIONES: es lo que necesita el expediente
    /// para leer del cuerpo lo que llama, lo que lee de la pila y las direcciones fijas que
    /// toca.</summary>
    public static Cuerpo DesensamblaCuerpo(byte[] mundo, ushort cs, ushort ip, int maxInsn = 400)
    {
        var cuerpo = new Cuerpo();
        var vistos = new HashSet<ushort>();
        var cola = new Queue<ushort>();
        cola.Enqueue(ip);
        var orden = cuerpo.Insns;
        int baseLin = cs << 4;

        while (cola.Count > 0 && vistos.Count < maxInsn)
        {
            ushort at = cola.Dequeue();
            while (vistos.Count < maxInsn)
            {
                if (!vistos.Add(at)) break;
                int lin = baseLin + at;
                if (lin < 0 || lin + 16 > mundo.Length) break;
                var lector = new Iced.Intel.ByteArrayCodeReader(mundo, lin, Math.Min(16, mundo.Length - lin));
                var dec = Iced.Intel.Decoder.Create(16, lector, at, Iced.Intel.DecoderOptions.None);
                var insn = dec.Decode();
                orden[at] = insn;
                var m = insn.Mnemonic;
                // SALTO POR TABLA. Sin esto, el cuerpo sale con UNA de sus N ramas y nadie avisa
                // (medido en un juego de prueba: la funcion 174de tiene seis y solo salia la observada).
                if (m == Iced.Intel.Mnemonic.Jmp && insn.Op0Kind == Iced.Intel.OpKind.Memory
                    && insn.MemoryBase != Iced.Intel.Register.None)
                {
                    var tab = LeeTablaSaltos(mundo, cs, insn, orden, cuerpo.Avisos);
                    if (tab != null)
                    {
                        cuerpo.Tablas.Add(tab);
                        foreach (var d in tab.Entradas) if (!vistos.Contains(d)) cola.Enqueue(d);
                    }
                    break;   // incondicional: no hay caida
                }
                if (m is Iced.Intel.Mnemonic.Ret or Iced.Intel.Mnemonic.Retf or Iced.Intel.Mnemonic.Iret
                      or Iced.Intel.Mnemonic.Iretd or Iced.Intel.Mnemonic.Hlt) break;
                // Un salto condicional abre rama: se encola el destino y se sigue por la caída.
                if (insn.Op0Kind is Iced.Intel.OpKind.NearBranch16 && m != Iced.Intel.Mnemonic.Call)
                {
                    ushort tgt = (ushort)insn.NearBranchTarget;
                    if (!vistos.Contains(tgt)) cola.Enqueue(tgt);
                    if (m == Iced.Intel.Mnemonic.Jmp) break;   // incondicional: no hay caída
                }
                at = (ushort)insn.NextIP;
            }
        }
        return cuerpo;
    }

    /// <summary>Lee la tabla de un <c>jmp word [cs:base+reg]</c>. Devuelve null y deja un AVISO si
    /// no hay cota medible: se prefiere decir "aqui hay ramas que no estan" a inventarse cuantas.
    ///
    /// <para>La cota sale del propio codigo: un <c>cmp reg,N</c> con su <c>jbe</c>/<c>jb</c> es
    /// como el programa valida el indice antes de saltar, y un <c>shl reg,1</c> en medio dice que
    /// cada entrada ocupa dos bytes. Es dato del binario, no heuristica.</para></summary>
    static TablaSaltos? LeeTablaSaltos(byte[] mundo, ushort cs, Iced.Intel.Instruction jmp,
                                       SortedDictionary<ushort, Iced.Intel.Instruction> yaVistas,
                                       List<string> avisos)
    {
        int n = -1;
        bool escala2 = false;
        // Las instrucciones ANTERIORES por direccion, no por camino de decodificacion: el `cmp`
        // que acota el indice suele estar en el bloque de antes, y ese bloque acaba en el salto
        // condicional que trajo aqui — o sea que ya no esta en el camino actual (medido en un juego de prueba:
        // el `cmp ax,5` de 174de vive en C17F y el bloque del `jmp` empieza en C187).
        var recientes = yaVistas.Where(kv => kv.Key < (ushort)jmp.IP)
                                .OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        for (int i = recientes.Count - 1, mirados = 0; i >= 0 && mirados < 12; i--, mirados++)
        {
            var x = recientes[i];
            if (x.Mnemonic == Iced.Intel.Mnemonic.Shl && x.Op1Kind == Iced.Intel.OpKind.Immediate8
                && x.Immediate8 == 1) escala2 = true;
            if (x.Mnemonic == Iced.Intel.Mnemonic.Cmp &&
                x.Op1Kind is Iced.Intel.OpKind.Immediate8 or Iced.Intel.OpKind.Immediate16
                          or Iced.Intel.OpKind.Immediate8to16)
            {
                int lim = (int)x.GetImmediate(1);
                // El salto que sigue al cmp dice si el limite entra: `jbe N` admite 0..N.
                var sig = i + 1 < recientes.Count ? recientes[i + 1] : default;
                if (sig.Mnemonic is Iced.Intel.Mnemonic.Jbe or Iced.Intel.Mnemonic.Ja) n = lim + 1;
                else if (sig.Mnemonic is Iced.Intel.Mnemonic.Jb or Iced.Intel.Mnemonic.Jae) n = lim;
                if (n > 0) break;
            }
        }
        if (n <= 0 || n > 256 || !escala2)
        {
            avisos.Add($"{(ushort)jmp.IP:X4}: SALTO INDIRECTO sin cota medible en el codigo. " +
                       "HAY RAMAS QUE NO ESTAN EN ESTE LISTADO.");
            return null;
        }
        ushort baseTab = (ushort)jmp.MemoryDisplacement32;
        int lin = (cs << 4) + baseTab;
        if (lin < 0 || lin + n * 2 > mundo.Length)
        {
            avisos.Add($"{(ushort)jmp.IP:X4}: SALTO INDIRECTO: la tabla cae fuera del mundo cargado.");
            return null;
        }
        var e = new ushort[n];
        for (int k = 0; k < n; k++) e[k] = (ushort)(mundo[lin + k * 2] | (mundo[lin + k * 2 + 1] << 8));
        return new TablaSaltos { En = baseTab, Entradas = e };
    }


    // ==========================================================================================
    //  EL JUICIO DE UN CASO, CON REGISTROS
    // ==========================================================================================

    /// <summary>Ejecuta un caso y devuelve el DGROUP resultante Y la CPU final.
    ///
    /// <para><b>Por qué no basta con <c>DiffEngine.ReplayCase</c>:</b> aquel juez compara SOLO
    /// memoria. Medido el 2026-08-25 con un control negativo del banco: a la funcion `2c68`
    /// (`mov dx,[0CCh]` / `add ax,[0CAh]` / `ret`, que no escribe un solo byte) se le dio como
    /// respuesta un cuerpo VACIO y salio <b>VERDE 4/4</b>. Toda funcion que calcule en registros
    /// —que son muchisimas— pasaba sin hacer nada. Ademas, aquel juez neutraliza ocho direcciones
    /// HORNEADAS de un único juego de prueba (0x5912, 0x9576, 0x96DC…) en cualquier juego, que en otro
    /// binario son datos como los demas.</para></summary>
    public static (byte[] dgroup, Cpu cpu) EjecutaCaso(Mundo mundo, CorpusCase caso, Action<Cpu, Memory> correr,
        bool esDriver = false, Action<EffectsReplayPlatform>? conPlataforma = null)
    {
        var dgroup = (byte[])caso.EntryMem.Clone();
        var entry = caso.EntryCpu;
        var mem = new Memory();
        int dsB = (int)entry.DsBase;
        int dgLo = dsB, dgHi = dsB + dgroup.Length;

        void CopyCode(byte[] src, int at, int len)
        {
            int lo = at, hi = Math.Min(at + len, Memory.Size);
            int pre = Math.Min(hi, dgLo);
            if (pre > lo) Array.Copy(src, 0, mem.Raw, lo, pre - lo);
            if (hi > dgHi && dgHi >= lo) Array.Copy(src, dgHi - lo, mem.Raw, dgHi, hi - dgHi);
            else if (dgHi < lo) Array.Copy(src, 0, mem.Raw, lo, hi - lo);
        }
        if (esDriver && mundo.Snapshot != null) CopyCode(mundo.Snapshot, 0, 0xA0000);
        else CopyCode(mundo.CodeBytes, mundo.CodeAt, mundo.CodeBytes.Length);
        // Ídem: la ventana de código del caso manda sobre el mundo (ver MontaPuerto).
        if (VentanaDeCodigo(mundo, caso.EffectsJson) is (byte[] codB, int codEn))
            CopyCode(codB, codEn, codB.Length);
        Array.Copy(dgroup, 0, mem.Raw, dsB, dgroup.Length);

        var cpu = Cpu.FromState(entry);
        cpu.LoadBase = mundo.LoadBase;
        cpu.ImageBias = mundo.Bias;
        EffectsReplayPlatform? plat = null;
        if (caso.EffectsJson.Length > 0)
        {
            plat = new EffectsReplayPlatform(caso.EffectsJson);
            plat.ApplyPre(mem);
            cpu.Platform = plat;
        }
        // El reloj de las IRQ grabadas. Solo se enciende si el caso trae alguna, y se apaga
        // pase lo que pase: es un estatico que lee el bucle del interprete.
        if (plat != null && plat.IrqsTotal > 0)
        { plat.CpuActual = cpu; Interp.ReplayIrq = plat; Interp.ReplayIrqBase = Interp.InsnCount; }
        // CASO DE VUELTA DE BUCLE: no termina en un `ret`, sino al volver a la cabecera con la
        // pila de entrada. Sin armar la parada, el replay seguiria dando vueltas hasta agotar el
        // presupuesto y el juez compararia el estado de la vuelta equivocada.
        int cuerpoDelBucle = CuerpoDelBucle(caso);
        bool esVuelta = cuerpoDelBucle > 0;
        if (esVuelta) Interp.ArmaParadaVuelta(entry.Cs, (ushort)entry.Eip, (ushort)entry.Esp, cuerpoDelBucle);
        try { correr(cpu, mem); }
        finally { Interp.ReplayIrq = null; if (esVuelta) Interp.DesarmaParadaVuelta(); }
        if (plat != null) conPlataforma?.Invoke(plat);
        Array.Copy(mem.Raw, dsB, dgroup, 0, dgroup.Length);
        return (dgroup, cpu);
    }

    /// <summary>Compara la salida de un caso: memoria Y registros.
    ///
    /// <para>La banda de pila alrededor del SP de entrada se neutraliza (scratch legitimo del
    /// compilador). Las BANDERAS no se comparan: una traduccion correcta puede dejarlas distintas
    /// si el original no las vuelve a mirar, y exigirlas convertiria en rojas traducciones buenas.
    /// Los registros de segmento SS y CS tampoco: el marco los fija el llamador.</para></summary>
    public static string? ComparaSalida(CorpusCase caso, byte[] dgroup, Cpu cpu)
    {
        int sp = (int)(caso.EntryCpu.Esp & 0xFFFF);
        // La banda de scratch, con la cota MEDIDA si el caso la trae (`spmin`): el SP más bajo al
        // que llegó la llamada grabada. Todo lo que hay entre él y el SP de entrada lo pisó el
        // marco de alguien —de la propia función, de las suyas anidadas o de una interrupción de
        // hardware— y nadie puede leerlo después sin escribirlo antes. Sin la medida se cae a la
        // constante de 256 bytes de siempre, que en un juego de prueba se quedaba 60 bytes corta.
        int lo = Math.Max(0, sp - PakuPort.Judge.Diff.DiffEngine.StackMaskBelow);
        int? medido = SpMinDelCaso(caso);
        if (medido is int sm) lo = Math.Min(sp, sm);
        int hi = Math.Min(caso.ExitMem.Length, sp + PakuPort.Judge.Diff.DiffEngine.StackMaskAbove);
        var trabajo = (byte[])dgroup.Clone();
        for (int i = lo; i < hi; i++) trabajo[i] = caso.ExitMem[i];

        var rangos = PakuPort.Judge.Diff.MemoryDiff.Compare(trabajo, caso.ExitMem);
        if (rangos.Count > 0) return $"memoria: {rangos.Count} tramo(s), el primero {rangos[0]}";

        var e = caso.ExitCpu;
        (string n, ushort esperado, ushort salio)[] regs =
        [
            ("AX", e.Ax, cpu.AX), ("BX", e.Bx, cpu.BX), ("CX", e.Cx, cpu.CX), ("DX", e.Dx, cpu.DX),
            ("SI", (ushort)e.Esi, cpu.SI), ("DI", (ushort)e.Edi, cpu.DI), ("BP", (ushort)e.Ebp, cpu.BP),
            ("SP", (ushort)e.Esp, cpu.SP), ("DS", e.Ds, cpu.DS), ("ES", e.Es, cpu.ES),
        ];
        foreach (var (n, esperado, salio) in regs)
            if (esperado != salio) return $"{n}: esperado {esperado:X4}, salio {salio:X4}";
        return null;
    }

    /// <summary>El `spmin` que el grabador midió para este caso, si lo trae.</summary>
    /// <summary>Los bytes del cuerpo del bucle si el caso se grabo como una VUELTA (campo `bucle`
    /// del esquema de efectos), o 0 si es una llamada normal. Lo pone <c>CorpusRecorder</c> cuando
    /// la unidad entro por la cabecera de un bucle en vez de por un `call`, y el replay lo necesita
    /// para saber que cuenta como SALIR del bucle.</summary>
    public static int CuerpoDelBucle(CorpusCase caso)
    {
        // EL CONTROL NEGATIVO de la parada, y por eso es una palanca y no un experimento suelto:
        // con `PAKU_SIN_PARADA_VUELTA=1` los casos de vuelta se reproducen SIN parar en la
        // cabecera. Tienen que salir ROJOS — el replay sigue dando vueltas y compara el estado de
        // otra iteración. Un mecanismo que nadie ha visto fallar no prueba nada.
        if (Environment.GetEnvironmentVariable("PAKU_SIN_PARADA_VUELTA") == "1") return 0;
        if (caso.EffectsJson.Length == 0) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(caso.EffectsJson);
            return doc.RootElement.TryGetProperty("bucle", out var e) ? e.GetInt32() : 0;
        }
        catch { return 0; }
    }

    static int? SpMinDelCaso(CorpusCase caso)
    {
        if (caso.EffectsJson.Length == 0) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(caso.EffectsJson);
            return doc.RootElement.TryGetProperty("spmin", out var e) ? e.GetInt32() : null;
        }
        catch { return null; }
    }

    // ==========================================================================================
    //  LOS DOS VERBOS
    // ==========================================================================================

    public static int Ejecutar(string[] args)
    {
        if (args.Length == 0) { Uso(); return 1; }
        return args[0] switch
        {
            "prepara" => Prepara(args[1..]),
            "encargos" => Encargos(args[1..]),
            "juzga" => Juzga(args[1..]),
            _ => Uso(),
        };
    }

    static int Uso()
    {
        Console.WriteLine(
            "pakuport banco prepara --game <game.json> --corpus <dir> --out <dir> [--n 20] [--verdes <log>]\n" +
            "    Elige las funciones del banco con el criterio congelado y escribe un ENCARGO por\n" +
            "    cada una. El material del encargo es el de HOY: el desensamblado, y nada mas.\n\n" +
            "pakuport banco encargos --banco <dir>\n" +
            "    Reescribe los ENCARGO.md de un banco ya congelado con el material de HOY, sin\n" +
            "    tocar la seleccion de funciones. Es como se mide el valor del expediente.\n\n" +
            "pakuport banco juzga --banco <dir>\n" +
            "    Compila cada <func>/respuesta.cs y la ejecuta contra el corpus de esa funcion.\n" +
            "    Sin respuesta.cs, la funcion cuenta como \"no lo intento\".");
        return 1;
    }

    static string? Opt(string[] a, string nombre)
    {
        int i = Array.IndexOf(a, nombre);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    /// <summary>CRITERIO CONGELADO de seleccion, publicado aqui y en el banco.json que sale:
    /// <list type="number">
    /// <item>al menos <b>3 casos</b> en el corpus (con menos, acertar por casualidad es facil);</item>
    /// <item><b>verde</b> en <c>interp-verify --strict</c>: si el propio corpus no reproduce la
    ///   funcion, no se puede juzgar a nadie con ella;</item>
    /// <item>cuerpo de <b>5 a 200 instrucciones</b>: por debajo es un stub, por encima no es una
    ///   funcion que nadie reescriba de una sentada;</item>
    /// <item>reparto <b>uniforme por offset</b> entre las que quedan, para no coger todas del
    ///   mismo rincon del binario.</item>
    /// </list>
    /// Se guardan ademas <b>tres triviales</b> (las mas cortas) como control positivo: si la linea
    /// base falla esas, lo roto es el arnes, no la IA.</summary>
    static int Prepara(string[] a)
    {
        string? gamePath = Opt(a, "--game"), corpusDir = Opt(a, "--corpus"), outDir = Opt(a, "--out");
        if (gamePath == null || corpusDir == null || outDir == null) return Uso();
        int n = int.TryParse(Opt(a, "--n"), out var nn) ? nn : 20;

        var game = GameConfig.Load(gamePath);
        string mundoPath = Path.Combine(corpusDir, "mundo.bin");
        if (!File.Exists(mundoPath)) { Console.Error.WriteLine($"falta {mundoPath}"); return 2; }
        var mundoBytes = File.ReadAllBytes(mundoPath);

        // Los veredictos: la salida de `interp-verify --strict` sobre este mismo corpus.
        var verdes = new HashSet<string>();
        string? logVerdes = Opt(a, "--verdes");
        if (logVerdes != null && File.Exists(logVerdes))
            foreach (var ln in File.ReadAllLines(logVerdes))
                if (ln.TrimStart().StartsWith("VERDES:"))
                    foreach (var f in ln[(ln.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        verdes.Add("FUN_1000_" + f);

        var candidatas = new List<(string func, int casos, int insn, ushort cs, ushort ip, List<string> asm)>();
        foreach (var d in Directory.GetDirectories(corpusDir).OrderBy(x => x, StringComparer.Ordinal))
        {
            string func = Path.GetFileName(d);
            var casos = FunctionVerifier.LoadCorpus(corpusDir, func);
            if (casos.Count < 3) continue;
            if (verdes.Count > 0 && !verdes.Contains(func)) continue;
            var e0 = casos[0].EntryCpu;
            var asm = Desensambla(mundoBytes, e0.Cs, e0.Ip);
            if (asm.Count < 5 || asm.Count > 200) continue;
            candidatas.Add((func, casos.Count, asm.Count, e0.Cs, e0.Ip, asm));
        }
        if (candidatas.Count == 0) { Console.Error.WriteLine("ninguna funcion pasa el criterio"); return 2; }

        // Reparto uniforme por offset + los tres cuerpos mas cortos como control positivo.
        var elegidas = new List<(string func, int casos, int insn, ushort cs, ushort ip, List<string> asm)>();
        int paso = Math.Max(1, candidatas.Count / Math.Max(1, n));
        for (int i = 0; i < candidatas.Count && elegidas.Count < n; i += paso) elegidas.Add(candidatas[i]);
        var triviales = candidatas.OrderBy(c => c.insn).Take(3).ToList();
        foreach (var t in triviales) if (!elegidas.Any(e => e.func == t.func)) elegidas.Add(t);

        Directory.CreateDirectory(outDir);
        var indiceCorpus = Expediente.IndicePorDireccion(corpusDir);
        foreach (var e in elegidas)
        {
            string dir = Path.Combine(outDir, e.func);
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "desensamblado.asm"), e.asm);
            var casosE = FunctionVerifier.LoadCorpus(corpusDir, e.func);
            var fichaE = Expediente.Observa(DesensamblaInsn(mundoBytes, e.cs, e.ip), casosE);
            File.WriteAllText(Path.Combine(dir, "ENCARGO.md"),
                Encargo(e.func, e.cs, e.ip, e.asm, fichaE, casosE, corpusDir, indiceCorpus, mundoBytes));
        }

        var banco = new
        {
            juego = game.Id,
            corpus = corpusDir,
            criterio = new
            {
                // OJO: "verde" aqui sale del log de `interp-verify` que se pase en --verdes. Si ese
                // log se saco con el juez de SOLO MEMORIA, entraran funciones que con registros no
                // reproduce ni el original (paso con tres funciones de un juego de prueba). Usa el log de
                // `interp-verify --registros`. `banco juzga` las detecta igual y las declara
                // INJUZGABLE, pero es mejor no meterlas.
                juez_del_log_de_verdes = "usar interp-verify --registros",
                casos_minimos = 3,
                solo_verdes_en_estricto = verdes.Count > 0,
                instrucciones = "5..200",
                reparto = "uniforme por offset",
                triviales_como_control_positivo = triviales.Select(t => t.func).ToArray(),
            },
            candidatas = candidatas.Count,
            funciones = elegidas.Select(e => new { func = e.func, casos = e.casos, instrucciones = e.insn,
                                                  cs = $"0x{e.cs:X4}", ip = $"0x{e.ip:X4}" }).ToArray(),
        };
        File.WriteAllText(Path.Combine(outDir, "banco.json"),
            System.Text.Json.JsonSerializer.Serialize(banco, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"banco: {elegidas.Count} funciones de {candidatas.Count} candidatas -> {outDir}");
        return 0;
    }

    /// <summary>El encargo que ve quien tiene que reescribir la funcion. En la LINEA BASE lleva
    /// solo lo que PakuPort sabe dar hoy: el desensamblado. Lo que se le anada a partir del dia 3
    /// (firma, tipos, capa, nombre con evidencia, casos de ejemplo) va aqui, y la diferencia en el
    /// numero final es exactamente lo que valen esos anadidos.</summary>
    static string Encargo(string func, ushort cs, ushort ip, List<string> asm,
                          Expediente.Ficha? ficha = null, List<CorpusCase>? casos = null,
                          string? corpusDir = null, Dictionary<int, string>? indice = null,
                          byte[]? mundo = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Encargo: reescribe `{func}` en C#");
        sb.AppendLine();
        sb.AppendLine("Tienes una funcion de un juego de MS-DOS de 16 bits, tal como se ejecuta.");
        sb.AppendLine("Reescribela en C# de forma que haga EXACTAMENTE lo mismo: mismos efectos");
        sb.AppendLine("sobre los registros y sobre la memoria.");
        sb.AppendLine();
        sb.Append(ComoEscribir());
        if (ficha != null) sb.Append(Expediente.ATexto(ficha));
        if (ficha != null && corpusDir != null && indice != null && mundo != null)
            sb.Append(Expediente.FichasDeCallees(ficha, cs, corpusDir, indice, mundo));
        if (casos != null && casos.Count > 0) sb.Append(Expediente.Ejemplos(casos));
        sb.AppendLine("## El codigo original");
        sb.AppendLine();
        sb.AppendLine($"Entra en `{cs:X4}:{ip:X4}`. Desensamblado ({asm.Count} instrucciones):");
        sb.AppendLine();
        sb.AppendLine("```asm");
        foreach (var l in asm) sb.AppendLine(l);
        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>COMO SE ESCRIBE UNA TRADUCCION: el contrato de entrega, la API de <c>Cpu</c> y
    /// <c>Memory</c>, que hacer con la pila y como llamar con <c>Original</c> a lo que todavia no
    /// esta portado. No depende de que funcion sea, asi que lo usan igual el encargo del banco y el
    /// tablero del remake (<c>expediente tablero</c>), que lo escribe una sola vez por juego en vez
    /// de repetirlo en cada una de las 231 cabeceras.</summary>
    public static string ComoEscribir()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Que tienes que entregar");
        sb.AppendLine();
        sb.AppendLine("Un fichero C# completo y compilable, con un metodo estatico publico asi:");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine("using PakuPort.Core;");
        sb.AppendLine("using PakuPort.Core.Verify;");
        sb.AppendLine();
        sb.AppendLine("public static class Respuesta");
        sb.AppendLine("{");
        sb.AppendLine("    public static void Ejecuta(Cpu c, Memory m, Original o)");
        sb.AppendLine("    {");
        sb.AppendLine("        // tu codigo");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("(el `using PakuPort.Core.Verify;` es por `Original`. Si tu funcion no llama a nada,");
        sb.AppendLine("puedes dejar el tercer parametro fuera y entregar `Ejecuta(Cpu c, Memory m)`.)");
        sb.AppendLine();
        sb.AppendLine("`Cpu` tiene los registros como campos publicos de lectura y escritura: `c.AX`, `c.BX`,");
        sb.AppendLine("`c.CX`, `c.DX`, `c.SI`, `c.DI`, `c.BP`, `c.SP`, `c.DS`, `c.ES`, `c.SS`, `c.CS`, y las");
        sb.AppendLine("mitades `c.AL c.AH c.BL c.BH c.CL c.CH c.DL c.DH` (byte, tambien escribibles). Las");
        sb.AppendLine("banderas son `bool`: `c.CF c.ZF c.SF c.OF c.DF c.PF c.AF c.IF`, y `c.FlagsWord()`");
        sb.AppendLine("devuelve el registro FLAGS entero como `ushort` (lo que empuja un `pushf`);");
        sb.AppendLine("`c.SetFlagsWord(v)` hace lo contrario, que es lo que necesita un `popf`.");
        sb.AppendLine();
        sb.AppendLine("Trae ademas las operaciones del 8086 con sus banderas ya calculadas. **Firmas exactas**");
        sb.AppendLine("(todas son metodos de instancia de `Cpu`, y las que devuelven valor NO escriben en");
        sb.AppendLine("ningun registro: el resultado lo asignas tu):");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine("ushort Add16(ushort a, ushort b, int carryIn = 0);   byte Add8(byte a, byte b, int carryIn = 0);");
        sb.AppendLine("ushort Sub16(ushort a, ushort b, int borrowIn = 0);  byte Sub8(byte a, byte b, int borrowIn = 0);");
        sb.AppendLine("ushort Adc16(ushort a, ushort b);                    byte Adc8(byte a, byte b);");
        sb.AppendLine("ushort Sbb16(ushort a, ushort b);                    byte Sbb8(byte a, byte b);");
        sb.AppendLine("void   Cmp16(ushort a, ushort b);                    void Cmp8(byte a, byte b);      // solo banderas");
        sb.AppendLine("void   Test16(ushort a, ushort b);                   void Test8(byte a, byte b);     // solo banderas");
        sb.AppendLine("ushort And16/Or16/Xor16(ushort a, ushort b);         byte And8/Or8/Xor8(byte a, byte b);");
        sb.AppendLine("ushort Inc16/Dec16/Neg16(ushort a);                  byte Inc8/Dec8/Neg8(byte a);");
        sb.AppendLine("ushort Shl16/Shr16/Sar16/Rol16/Ror16/Rcl16/Rcr16(ushort v, int count);   // y las 8 en byte");
        sb.AppendLine("void   Mul16(ushort src);  void Imul16(ushort src);  // resultado en DX:AX, como el 8086");
        sb.AppendLine("void   Div16(ushort src);  void Idiv16(ushort src);  // DX:AX / src -> AX, resto en DX");
        sb.AppendLine("void   Mul8(byte src);     void Div8(byte src);      // en AX");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("`Memory` se lee y escribe por segmento y desplazamiento: `m.RdU8(seg, off)`,");
        sb.AppendLine("`m.RdU16(seg, off)`, `m.WrU8(seg, off, v)`, `m.WrU16(seg, off, v)`.");
        sb.AppendLine();
        sb.AppendLine("La funcion entra con la direccion de retorno ya en la pila: al terminar, deja `SP`");
        sb.AppendLine("como lo dejaria el `ret` original (`c.SP += 2` para un `ret` normal, `c.SP += 2 + n`");
        sb.AppendLine("para un `ret n`). Si el original empuja y saca registros, hazlo tambien: la pila");
        sb.AppendLine("es memoria y se compara.");
        sb.AppendLine();
        sb.AppendLine("## Si tu ficha dice VUELTA DE BUCLE, esto cambia");
        sb.AppendLine();
        sb.AppendLine("Algunas carpetas de este tablero no son funciones: son **una vuelta de un bucle**. Lo");
        sb.AppendLine("dice su ficha en la primera linea. Existen porque en un juego escrito en ensamblador");
        sb.AppendLine("la mayor parte del trabajo no ocurre dentro de ninguna llamada que retorne — el bucle");
        sb.AppendLine("principal se llama una vez y no vuelve nunca, y lo que corre desde el punto de entrada");
        sb.AppendLine("no lo llama nadie. Sin esta unidad, ese codigo no tendria ni un solo caso de prueba.");
        sb.AppendLine();
        sb.AppendLine("Lo que cambia para ti:");
        sb.AppendLine();
        sb.AppendLine("- **Devuelves `ushort`, no `void`: la direccion por la que SIGUE el programa.** La");
        sb.AppendLine("  cabecera si toca otra vuelta, o el sitio por el que se sale del bucle. El juez la");
        sb.AppendLine("  compara con la grabada, y dentro del juego el interprete continua exactamente ahi.");
        sb.AppendLine("- **No hay direccion de retorno y no hay `ret`.** Entra y sale con el MISMO `SP`, y asi");
        sb.AppendLine("  tienes que dejarlo: nada de `c.SP += 2` al final.");
        sb.AppendLine("- **Salir del bucle es salir del cuerpo CON LA PILA A SU NIVEL**, mirado en cada");
        sb.AppendLine("  instruccion. Si la vuelta empujo algo y salta fuera del cuerpo, todavia no ha salido:");
        sb.AppendLine("  sigue hasta que desapile; y si desapila y la instruccion siguiente cae fuera, sale");
        sb.AppendLine("  JUSTO ahi, antes de ejecutarla (medido en un juego de prueba: `3cdd` sale en 1075h, no en");
        sb.AppendLine("  el 11CFh al que salta 1075h).");
        sb.AppendLine("- **Traduces UNA vuelta, no el bucle entero.** Terminas donde el original volveria a la");
        sb.AppendLine("  cabecera, y devuelves la cabecera. El salto de vuelta no lo das tu.");
        sb.AppendLine("- **Y los caminos que SALEN del bucle SI son tuyos**: cuando el original abandona el");
        sb.AppendLine("  bucle a media vuelta, eso es un caso mas de esta carpeta y el juez lo mide igual.");
        sb.AppendLine("  La ficha te dice cuantos de tus casos salen; si son cero, esta partida no cubrio la");
        sb.AppendLine("  condicion de salida y ahi no tienes red.");
        sb.AppendLine("- **Dentro del cuerpo pueden vivir otras carpetas de este tablero** (la ficha las lista");
        sb.AppendLine("  por nombre). No uses `o.Llama` para ellas —una vuelta no es una llamada—: el juez");
        sb.AppendLine("  compila todas las respuestas juntas, cada una en `PakuPort.Remake.<carpeta>`, asi que");
        sb.AppendLine("  la llamas en C# y la das hasta que salga:");
        sb.AppendLine("  `ushort sig; do sig = PakuPort.Remake.FUN_1000_xxxx.Respuesta.Ejecuta(c, m, o); while (sig == 0xCABE);`");
        sb.AppendLine();
        sb.AppendLine("## Como llamar a lo que todavia no esta traducido");
        sb.AppendLine();
        sb.AppendLine("No hace falta que traduzcas tambien lo que esta funcion llama. `Original o` te deja");
        sb.AppendLine("pedirselo al juego de verdad, y lo que se ejecuta ES el binario original:");
        sb.AppendLine();
        sb.AppendLine("- `o.Llama(0x1234, 0x5678)` — un `call 1234h` del mismo segmento de codigo.");
        sb.AppendLine("  El segundo numero es la direccion a la que volveria ese `call`, o sea la");
        sb.AppendLine("  instruccion siguiente. **Ponlo siempre**: el `call` la ESCRIBE en la pila, la pila");
        sb.AppendLine("  es memoria y el juez la compara. La ficha de cada funcion ya te la da hecha.");
        sb.AppendLine("- `o.LlamaLejos(seg, off)` — un `call far`.");
        sb.AppendLine("- `o.Interrupcion(0x21)` — un `int 21h` (o cualquier otra).");
        sb.AppendLine("- `o.LeePuerto8(p)`, `o.LeePuerto16(p)`, `o.EscribePuerto8(p, v)`, `o.EscribePuerto16(p, v)`.");
        sb.AppendLine();
        sb.AppendLine("Cada una deja los registros, **las BANDERAS** y la memoria como los dejaria el");
        sb.AppendLine("original: `o.Llama` ejecuta el codigo del juego sobre esta misma CPU, asi que un");
        sb.AppendLine("`jae`/`jb`/`je` justo detras de la llamada lee el CF/ZF que dejo el callee, igual que");
        sb.AppendLine("en el binario. **El orden importa**: pide las INT y los `in` en el mismo orden y la");
        sb.AppendLine("misma cantidad que el codigo de abajo, o el resultado no coincidira.");
        sb.AppendLine();
        sb.AppendLine("## El ritmo: cobra las instrucciones que habria gastado el original");
        sb.AppendLine();
        sb.AppendLine("El reloj del juego ES el contador de instrucciones del invitado: de el salen el tic de");
        sb.AppendLine("la BIOS, las IRQ del temporizador, las fronteras de frame y la velocidad del juego");
        sb.AppendLine("entero. Tu C# no gasta instrucciones del invitado, asi que las **cobra**:");
        sb.AppendLine();
        sb.AppendLine("- `o.Instrucciones(n)` — el original habria ejecutado `n` instrucciones aqui.");
        sb.AppendLine("- `o.Repeticiones(k)` — ademas, para un `rep` que proceso `k` elementos (el interprete");
        sb.AppendLine("  cobra un tic por elemento menos el ultimo, la regla de DOSBox-X).");
        sb.AppendLine();
        sb.AppendLine("**La regla que lo hace exacto**: antes de cada instruccion que alguien de fuera puede");
        sb.AppendLine("ver — `int`, `in`, `out`, `call` — cobra todo lo pendiente hasta ELLA INCLUIDA; al final");
        sb.AppendLine("de cada bloque (antes de un salto o de salir), cobra lo que quede. Una llamada a otra");
        sb.AppendLine("traduccion o a `o.Llama` cobra lo suyo: tu solo cobras tu `call`. Un `ret` es una");
        sb.AppendLine("instruccion mas.");
        sb.AppendLine();
        sb.AppendLine("El juez compara tu cuenta con la del original caso a caso y lo dice (`ritmo EXACTO`, o");
        sb.AppendLine("`ritmo 3/8` con el primer desfase). Con `--exige-ritmo` solo cuenta como VERDE lo que");
        sb.AppendLine("ademas va a compas. **Sin ritmo, una vuelta que espera un tic del reloj se queda");
        sb.AppendLine("esperando para siempre dentro del juego**, y cualquier juego va a otra velocidad.");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Reescribe los ENCARGO.md de un banco YA CONGELADO, con el material de hoy y sin
    /// tocar la seleccion de funciones. Es como se mide el valor del expediente: mismas funciones,
    /// mismo juez, distinto material.</summary>
    static int Encargos(string[] a)
    {
        string? bancoDir = Opt(a, "--banco");
        if (bancoDir == null) return Uso();
        string bancoJson = Path.Combine(bancoDir, "banco.json");
        if (!File.Exists(bancoJson)) { Console.Error.WriteLine($"falta {bancoJson}"); return 2; }
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(bancoJson));
        string corpusDir = doc.RootElement.GetProperty("corpus").GetString()!;
        var mundoBytes = File.ReadAllBytes(Path.Combine(corpusDir, "mundo.bin"));
        var indice = Expediente.IndicePorDireccion(corpusDir);

        int n = 0;
        foreach (var fEl in doc.RootElement.GetProperty("funciones").EnumerateArray())
        {
            string func = fEl.GetProperty("func").GetString()!;
            ushort cs = (ushort)Convert.ToInt32(fEl.GetProperty("cs").GetString()!, 16);
            ushort ip = (ushort)Convert.ToInt32(fEl.GetProperty("ip").GetString()!, 16);
            var casos = FunctionVerifier.LoadCorpus(corpusDir, func);
            var ficha = Expediente.Observa(DesensamblaInsn(mundoBytes, cs, ip), casos);
            string dir = Path.Combine(bancoDir, func);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ENCARGO.md"),
                Encargo(func, cs, ip, Desensambla(mundoBytes, cs, ip), ficha, casos,
                        corpusDir, indice, mundoBytes));
            n++;
        }
        Console.WriteLine($"encargos regenerados: {n} -> {bancoDir}");
        return 0;
    }

    static int Juzga(string[] a)
    {
        string? bancoDir = Opt(a, "--banco");
        if (bancoDir == null) return Uso();
        // --exige-ritmo: una traduccion solo es VERDE si ademas cobra las mismas instrucciones
        // que el original en cada caso. Es lo que hace falta para enchufarla al juego sin mover su
        // reloj; sin la opcion, el ritmo se informa pero no decide.
        bool exigeRitmo = a.Contains("--exige-ritmo");
        string bancoJson = Path.Combine(bancoDir, "banco.json");
        if (!File.Exists(bancoJson)) { Console.Error.WriteLine($"falta {bancoJson}"); return 2; }
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(bancoJson));
        string corpusDir = doc.RootElement.GetProperty("corpus").GetString()!;
        var mundo = new Mundo
        {
            CodeBytes = File.ReadAllBytes(Path.Combine(corpusDir, "mundo.bin")),
            CodeAt = 0, LoadBase = 0, Bias = 0, Descripcion = "mundo.bin", CorpusDir = corpusDir,
        };

        var funcs = doc.RootElement.GetProperty("funciones").EnumerateArray()
            .Select(f => f.GetProperty("func").GetString()!).ToList();
        var traducidas = CompilaTablero(bancoDir, funcs, out var errores);

        int verde = 0, rojo = 0, sinRespuesta = 0, noCompila = 0, injuzgable = 0, ritmoExacto = 0, sinCasos = 0;
        var detalle = new List<string>();
        foreach (var func in funcs)
        {
            if (errores.TryGetValue(func, out var err))
            { noCompila++; detalle.Add($"  {func}: NO COMPILA - {err[..Math.Min(160, err.Length)]}"); continue; }
            if (!traducidas.TryGetValue(func, out var tr)) { sinRespuesta++; detalle.Add($"  {func}: sin respuesta"); continue; }

            var casos = FunctionVerifier.LoadCorpus(corpusDir, func);
            if (casos.Count == 0)
            {
                // Compila (eso ya esta dicho arriba) pero no hay contra que medirla.
                sinCasos++;
                detalle.Add($"  {func}: SIN CASOS - compila; se juzga partida contra partida (tools/control_portadas.py)");
                continue;
            }

            // ¿ES JUZGABLE? Antes de exigirle nada a nadie, se pasa el BINARIO ORIGINAL por el
            // mismo aro. Si el propio original no reproduce sus casos grabados, esa funcion no
            // mide a quien la traduce: mide un agujero del corpus, y contarla como roja es
            // atribuirle a la IA un fallo del arnes.
            //
            // No es hipotetico: tres de las 23 del banco de un juego de prueba salian rojas con
            // divergencias identicas tanda tras tanda. Delegando la funcion ENTERA al original
            // (banco/controles/delega-todo-1793e.cs) salia la MISMA divergencia — o sea que no
            // habia nada que traducir bien. Son las mismas tres que `interp-verify --registros`
            // da por rojas sobre ese corpus.
            //
            // De paso se mide EL RITMO del original: cuantas instrucciones y cuantos tics de reloj
            // gasta cada caso. Es contra lo que se compara el de la traduccion.
            string? porQueNo = null;
            var ritmoOriginal = new List<(long insn, long tics)>();
            foreach (var cc in casos)
            {
                try
                {
                    Interp.IpParada = -1;
                    long i0 = Interp.InsnCount, t0 = Cpu.TickCount;
                    var (dgO, cpuO) = EjecutaCaso(mundo, cc, ConElInterprete(mundo, cc));
                    ritmoOriginal.Add((Interp.InsnCount - i0, Cpu.TickCount - t0));
                    porQueNo ??= ComparaSalida(cc, dgO, cpuO);
                    if (CuerpoDelBucle(cc) > 0) porQueNo ??= ComparaSigue(cc, Interp.IpParada);
                }
                catch (Exception ex) { porQueNo ??= ex.GetType().Name + ": " + ex.Message; }
                if (porQueNo != null) break;
            }
            if (porQueNo != null)
            {
                injuzgable++;
                detalle.Add($"  {func}: INJUZGABLE - el ORIGINAL tampoco reproduce sus casos ({porQueNo})");
                continue;
            }

            int pasan = 0, aCompas = 0; string? fallo = null, desfase = null;
            for (int k = 0; k < casos.Count; k++)
            {
                var cc = casos[k];
                bool esVuelta = CuerpoDelBucle(cc) > 0;
                try
                {
                    int sigue = -1;
                    long i0 = Interp.InsnCount, t0 = Cpu.TickCount;
                    var (dg, cpu) = EjecutaCaso(mundo, cc, (c, m) => sigue = tr.Corre(c, m));
                    long di = Interp.InsnCount - i0, dt = Cpu.TickCount - t0;
                    string? mal = ComparaSalida(cc, dg, cpu);
                    // UNA VUELTA TIENE QUE DECIR POR DONDE SIGUE. El gancho del juego continua
                    // en la direccion que devuelva, asi que una vuelta que deja bien la memoria y
                    // los registros pero devuelve otra cosa descarrila el juego igual.
                    if (mal == null && esVuelta)
                        mal = tr.EsVuelta
                            ? ComparaSigue(cc, sigue)
                            : "es una VUELTA de bucle: la traduccion tiene que devolver `ushort`, la direccion por la que sigue el programa";
                    if (mal == null && !esVuelta && tr.EsVuelta)
                        mal = "es una FUNCION, no una vuelta: tiene que devolver `void` y salir con su `ret`";
                    if (mal == null) pasan++; else fallo ??= $"caso {k}: {mal}";
                    var (oi, ot) = ritmoOriginal[k];
                    if (di == oi && dt == ot) aCompas++;
                    else desfase ??= $"caso {k}: {di} instrucciones y {dt} tics, el original {oi} y {ot}";
                }
                catch (Exception ex) { fallo ??= $"caso {k}: {ex.GetType().Name}: {ex.Message}"; }
            }
            bool ritmoOk = aCompas == casos.Count;
            if (ritmoOk) ritmoExacto++;
            string ritmo = ritmoOk ? "ritmo EXACTO" : $"ritmo {aCompas}/{casos.Count} ({desfase})";
            bool esVerde = pasan == casos.Count && (!exigeRitmo || ritmoOk);
            if (esVerde) { verde++; detalle.Add($"  {func}: VERDE {pasan}/{casos.Count} | {ritmo}"); }
            else
            {
                rojo++;
                detalle.Add($"  {func}: rojo {pasan}/{casos.Count}{(fallo != null ? " (" + fallo + ")" : "")} | {ritmo}");
            }
        }
        foreach (var d in detalle) Console.WriteLine(d);
        int total = verde + rojo + sinRespuesta + noCompila + injuzgable + sinCasos;
        int juzgables = total - injuzgable - sinCasos;
        Console.WriteLine();
        Console.WriteLine($"== BANCO: VERDE={verde}  ROJO={rojo}  NO COMPILA={noCompila}  " +
                          $"SIN RESPUESTA={sinRespuesta}  INJUZGABLE={injuzgable}  " +
                          (sinCasos > 0 ? $"SIN CASOS={sinCasos}  " : "") + $"(de {total})" +
                          $"   ritmo exacto: {ritmoExacto}{(exigeRitmo ? " (exigido)" : "")}");
        if (injuzgable > 0)
            Console.WriteLine($"   sobre las {juzgables} JUZGABLES: {verde} verdes " +
                              $"({100.0 * verde / Math.Max(1, juzgables):F1} %). Las injuzgables no las falla " +
                              "quien traduce: no las reproduce ni el binario original.");
        return 0;
    }

    /// <summary>La vuelta tenia que seguir por donde siguio la grabada: la cabecera, o el sitio
    /// por el que salio del bucle.</summary>
    static string? ComparaSigue(CorpusCase caso, int sigue)
    {
        ushort esperado = (ushort)caso.ExitCpu.Eip;
        if (sigue < 0) return $"la vuelta no dijo por donde sigue (esperado {esperado:X4})";
        return sigue == esperado ? null : $"sigue por {sigue:X4}, el original por {esperado:X4}";
    }

    /// <summary>Una traduccion compilada: como se corre y si es una vuelta.</summary>
    public sealed class Traduccion
    {
        /// <summary>Devuelve la direccion por la que sigue si es una vuelta, o -1 si es una
        /// funcion (esas siguen por su `ret`).</summary>
        public required Func<Cpu, Memory, int> Corre;
        public required bool EsVuelta;
    }

    /// <summary>Compila TODAS las <c>respuesta.cs</c> del tablero en un solo ensamblado, cada una
    /// en su namespace (<c>PakuPort.Remake.&lt;func&gt;</c>) — el mismo envoltorio que les pone
    /// <c>export --portadas</c>, asi que lo que compila aqui compila en el juego.
    ///
    /// <para><b>Por que juntas.</b> Un bucle vive dentro de otro: el principal de un juego de prueba mide
    /// 860 bytes y dentro tiene otras diez entradas del tablero. Compiladas por separado, la vuelta
    /// de fuera tenia que REPETIR el codigo de las de dentro, y el codigo repetido es donde se
    /// cuela el error. Juntas, la de fuera llama a las de dentro y cada trozo esta escrito una vez
    /// y juzgado por sus propios casos.</para>
    ///
    /// <para>Si una no compila, se aparta con su error y se recompila el resto: una respuesta rota
    /// no puede tumbar el juicio de las demas (salvo las que la llamen, que caen con ella y lo
    /// dicen).</para></summary>
    public static Dictionary<string, Traduccion> CompilaTablero(string bancoDir, IEnumerable<string> funcs,
                                                               out Dictionary<string, string> errores)
    {
        errores = [];
        var fuentes = new Dictionary<string, string>();
        foreach (var f in funcs)
        {
            string ruta = Path.Combine(bancoDir, f, "respuesta.cs");
            if (File.Exists(ruta)) fuentes[f] = File.ReadAllText(ruta);
        }
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location))
            .Select(x => (MetadataReference)MetadataReference.CreateFromFile(x.Location)).ToList();

        while (fuentes.Count > 0)
        {
            var arboles = fuentes.Select(kv => CSharpSyntaxTree.ParseText(
                RemakeAlExport.Envuelve(kv.Key, kv.Value), path: kv.Key)).ToList();
            var comp = CSharpCompilation.Create("Tablero_" + Guid.NewGuid().ToString("N"), arboles, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using var ms = new MemoryStream();
            var res = comp.Emit(ms);
            if (!res.Success)
            {
                var malas = res.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                    .GroupBy(d => d.Location.SourceTree?.FilePath ?? "").ToList();
                bool quitado = false;
                foreach (var g in malas)
                {
                    if (!fuentes.ContainsKey(g.Key)) continue;
                    errores[g.Key] = string.Join(" | ", g.Take(3).Select(d => d.ToString()));
                    fuentes.Remove(g.Key);
                    quitado = true;
                }
                if (!quitado)
                {
                    foreach (var k in fuentes.Keys) errores[k] = "el tablero no compila: " +
                        string.Join(" | ", malas.SelectMany(x => x).Take(2).Select(d => d.ToString()));
                    return [];
                }
                continue;
            }
            var asm = System.Reflection.Assembly.Load(ms.ToArray());
            var salida = new Dictionary<string, Traduccion>();
            foreach (var (func, fuente) in fuentes)
            {
                var punto = RemakeAlExport.Localiza(RemakeAlExport.Envuelve(func, fuente));
                if (punto == null) { errores[func] = "no hay ningun metodo estatico publico (Cpu, Memory[, Original]) que devuelva void o ushort"; continue; }
                var p = punto.Value;
                var mi = asm.GetType(p.clase)?.GetMethod(p.metodo);
                if (mi == null) { errores[func] = $"no encuentro {p.clase}.{p.metodo} en lo compilado"; continue; }
                bool conOriginal = mi.GetParameters().Length == 3;
                bool esVuelta = p.esVuelta;
                salida[func] = new Traduccion
                {
                    EsVuelta = esVuelta,
                    Corre = (c, m) =>
                    {
                        object?[] args = conOriginal ? [c, m, new Original(c, m)] : [c, m];
                        try
                        {
                            object? r = mi.Invoke(null, args);
                            return esVuelta ? (ushort)r! : -1;
                        }
                        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
                        {
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                            throw;
                        }
                    },
                };
            }
            return salida;
        }
        return [];
    }
}
