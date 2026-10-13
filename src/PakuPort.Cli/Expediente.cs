using Iced.Intel;
using PakuPort.Judge.Corpus;

/// <summary>
/// EL EXPEDIENTE DE UNA FUNCION.
///
/// <para>Lo que PakuPort puede escribir y un desensamblador no: <b>hechos medidos</b>. Aqui se
/// junta lo que se lee del cuerpo (a que llama, que argumentos toca, que direcciones fijas usa)
/// con lo que se OBSERVA en las invocaciones reales del corpus (que registros cambian, cuales se
/// conservan, que valores tomo cada cosa). Cada apartado de la ficha dice de donde sale, porque
/// un dato observado la IA puede usarlo sin verificarlo y uno leido del codigo no.</para>
///
/// <para><b>Regla:</b> un dato que un solo caso del corpus contradiga no se publica. Una firma
/// falsa es peor que ninguna — la IA se la cree.</para>
/// </summary>
static class Expediente
{
    public sealed class Llamada
    {
        public required string Tipo;      // near | far | int | in | out | indirecta
        public ushort Off, Seg;
        public byte N;
        public string Puerto = "";
        public ushort En;                 // direccion de la instruccion
        /// <summary>La instruccion siguiente, o sea la direccion de retorno que el `call`
        /// escribe en la pila. Hace falta para que la traduccion la reproduzca: la pila es
        /// memoria y el juez la compara.</summary>
        public ushort Sig;
    }

    public sealed class ArgPila
    {
        public int Off;                   // desplazamiento sobre BP
        public int Ancho;                 // 1, 2 o 4 bytes
        public bool PunteroFar;           // se carga con lds/les
        public List<string> Valores = new();
    }

    public sealed class Fija
    {
        public ushort Dir;
        public string Seg = "ds";
        public int Ancho;
        public bool Escrita;
        public List<string> Valores = new();
    }

    public sealed class Ficha
    {
        public List<Llamada> Llamadas = new();
        public List<ArgPila> Args = new();
        public List<Fija> Fijas = new();
        public List<string> Preservados = new();
        public List<string> Cambian = new();
        public int RetN = -1;             // bytes de argumentos que quita la funcion (`ret n`)
        public bool RetLejano;
        public int Casos;
    }

    static readonly string[] RegsNombre = ["AX", "BX", "CX", "DX", "SI", "DI", "BP", "DS", "ES"];

    static ushort Reg(in PakuPort.Judge.Oracle.CpuState s, int i) => i switch
    {
        0 => s.Ax, 1 => s.Bx, 2 => s.Cx, 3 => s.Dx,
        4 => (ushort)s.Esi, 5 => (ushort)s.Edi, 6 => (ushort)s.Ebp, 7 => s.Ds, _ => s.Es,
    };

    public static Ficha Observa(SortedDictionary<ushort, Instruction> cuerpo, List<CorpusCase> casos)
    {
        var f = new Ficha { Casos = casos.Count };

        foreach (var (at, insn) in cuerpo)
        {
            switch (insn.Mnemonic)
            {
                case Mnemonic.Call when insn.Op0Kind == OpKind.NearBranch16:
                    f.Llamadas.Add(new Llamada { Tipo = "near", Off = (ushort)insn.NearBranchTarget, En = at, Sig = (ushort)(at + insn.Length) });
                    break;
                case Mnemonic.Call when insn.Op0Kind is OpKind.FarBranch16 or OpKind.FarBranch32:
                    f.Llamadas.Add(new Llamada { Tipo = "far", Seg = insn.FarBranchSelector, Off = (ushort)insn.FarBranch16, En = at, Sig = (ushort)(at + insn.Length) });
                    break;
                case Mnemonic.Call:
                    f.Llamadas.Add(new Llamada { Tipo = "indirecta", En = at, Sig = (ushort)(at + insn.Length) });
                    break;
                case Mnemonic.Int:
                    f.Llamadas.Add(new Llamada { Tipo = "int", N = insn.Immediate8, En = at });
                    break;
                case Mnemonic.In:
                case Mnemonic.Out:
                {
                    bool esIn = insn.Mnemonic == Mnemonic.In;
                    int iPuerto = esIn ? 1 : 0;
                    string p = insn.GetOpKind(iPuerto) == OpKind.Immediate8 ? $"0x{insn.Immediate8:X2}" : "c.DX";
                    f.Llamadas.Add(new Llamada { Tipo = esIn ? "in" : "out", Puerto = p, En = at });
                    break;
                }
                case Mnemonic.Ret:
                    f.RetN = insn.OpCount >= 1 ? insn.Immediate16 : 0;
                    break;
                case Mnemonic.Retf:
                    f.RetN = insn.OpCount >= 1 ? insn.Immediate16 : 0;
                    f.RetLejano = true;
                    break;
            }

            // Operandos de memoria: argumentos sobre BP y direcciones fijas.
            for (int op = 0; op < insn.OpCount; op++)
            {
                if (insn.GetOpKind(op) != OpKind.Memory) continue;
                int ancho = insn.MemorySize.GetSize();
                bool far = insn.Mnemonic is Mnemonic.Lds or Mnemonic.Les;
                if (far) ancho = 4;
                int disp = (int)insn.MemoryDisplacement32;
                if (insn.MemoryBase == Register.BP && insn.MemoryIndex == Register.None
                    && disp >= 4 && disp < 0x100)
                {
                    var a = f.Args.FirstOrDefault(x => x.Off == disp);
                    if (a == null) { a = new ArgPila { Off = disp, Ancho = ancho }; f.Args.Add(a); }
                    a.Ancho = Math.Max(a.Ancho, ancho);
                    a.PunteroFar |= far;
                }
                else if (insn.MemoryBase == Register.None && insn.MemoryIndex == Register.None
                         && disp is > 0 and < 0x10000 && ancho is 1 or 2 or 4)
                {
                    ushort dir = (ushort)disp;
                    string seg = insn.SegmentPrefix == Register.None ? "ds" : insn.SegmentPrefix.ToString().ToLowerInvariant();
                    var x = f.Fijas.FirstOrDefault(y => y.Dir == dir && y.Seg == seg);
                    if (x == null) { x = new Fija { Dir = dir, Seg = seg, Ancho = ancho }; f.Fijas.Add(x); }
                    x.Escrita |= op == 0 && insn.Mnemonic is not (Mnemonic.Cmp or Mnemonic.Test or Mnemonic.Push);
                }
            }
        }
        f.Args.Sort((a, b) => a.Off - b.Off);
        f.Fijas.Sort((a, b) => a.Dir - b.Dir);

        if (casos.Count == 0) return f;

        // --- lo OBSERVADO: que registros conserva y cuales cambia ---
        for (int i = 0; i < RegsNombre.Length; i++)
        {
            bool igualSiempre = casos.All(c => Reg(c.EntryCpu, i) == Reg(c.ExitCpu, i));
            if (igualSiempre) f.Preservados.Add(RegsNombre[i]);
            else f.Cambian.Add(RegsNombre[i]);
        }

        // --- valores reales de argumentos y direcciones fijas ---
        // Solo se publican si se pueden LEER del caso: el DGROUP grabado cubre [DsBase, +len), y
        // la pila solo cae dentro si SS y DS comparten base (modelo small, que es lo normal).
        foreach (var c in casos)
        {
            bool pilaLegible = c.EntryCpu.SsBase == c.EntryCpu.DsBase;
            foreach (var a in f.Args)
            {
                if (!pilaLegible) { a.Valores.Add("?"); continue; }
                int off = (int)((c.EntryCpu.Ebp & 0xFFFF) + a.Off);
                a.Valores.Add(Lee(c.EntryMem, off, a.Ancho, a.PunteroFar));
            }
            foreach (var x in f.Fijas)
            {
                uint baseSeg = x.Seg == "ss" ? c.EntryCpu.SsBase : x.Seg == "es" ? c.EntryCpu.EsBase : c.EntryCpu.DsBase;
                if (baseSeg != c.EntryCpu.DsBase) { x.Valores.Add("?"); continue; }
                string ent = Lee(c.EntryMem, x.Dir, x.Ancho, false);
                string sal = Lee(c.ExitMem, x.Dir, x.Ancho, false);
                x.Valores.Add(ent == sal ? ent : ent + "->" + sal);
            }
        }
        return f;
    }

    static string Lee(byte[] mem, int off, int ancho, bool far)
    {
        if (off < 0 || off + ancho > mem.Length) return "?";
        if (far && ancho == 4) return $"{(mem[off + 3] << 8) | mem[off + 2]:X4}:{(mem[off + 1] << 8) | mem[off]:X4}";
        return ancho switch
        {
            1 => $"{mem[off]:X2}",
            2 => $"{(mem[off + 1] << 8) | mem[off]:X4}",
            4 => $"{(mem[off + 3] << 8) | mem[off + 2]:X4}{(mem[off + 1] << 8) | mem[off]:X4}",
            _ => "?",
        };
    }

    /// <summary>La ficha en markdown, para el encargo. Cada apartado dice si es observado (y sobre
    /// cuantos casos) o leido del cuerpo.</summary>
    public static string ATexto(Ficha f)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Lo que se sabe de esta funcion");
        sb.AppendLine();
        sb.AppendLine($"Lo que dice \"observado\" sale de **{f.Casos} invocaciones reales** grabadas mientras");
        sb.AppendLine("el juego corria, y ningun caso lo contradice.");
        sb.AppendLine();

        sb.AppendLine("### Convencion de llamada (observado)");
        sb.AppendLine();
        sb.AppendLine("- Registros que **conserva** (salen igual que entraron en todos los casos): " +
                      (f.Preservados.Count > 0 ? "`" + string.Join("` `", f.Preservados) + "`" : "ninguno"));
        sb.AppendLine("- Registros que **cambia**: " +
                      (f.Cambian.Count > 0 ? "`" + string.Join("` `", f.Cambian) + "`" : "ninguno"));
        if (f.RetN >= 0)
            sb.AppendLine($"- Sale con `{(f.RetLejano ? "retf" : "ret")}{(f.RetN > 0 ? " " + f.RetN : "")}`" +
                          (f.RetN > 0 ? $": la funcion quita **{f.RetN} bytes** de argumentos de la pila."
                                      : ": los argumentos los limpia quien llama."));
        sb.AppendLine();

        if (f.Args.Count > 0)
        {
            sb.AppendLine("### Argumentos en la pila");
            sb.AppendLine();
            sb.AppendLine("Leidos del cuerpo (`[bp+N]`), con el valor que llevaban en cada caso:");
            sb.AppendLine();
            sb.AppendLine("| donde | ancho | valores observados |");
            sb.AppendLine("|---|---|---|");
            foreach (var a in f.Args)
                sb.AppendLine($"| `[bp+{a.Off:X}h]` | {(a.PunteroFar ? "4 (**puntero far**: se carga con lds/les)" : a.Ancho.ToString())} | " +
                              string.Join(", ", a.Valores.Distinct().Take(6)) + " |");
            sb.AppendLine();
        }

        if (f.Fijas.Count > 0)
        {
            sb.AppendLine("### Direcciones fijas que toca");
            sb.AppendLine();
            sb.AppendLine("Variables globales del juego. `A->B` = la funcion la cambio en ese caso.");
            sb.AppendLine("El SEGMENTO va siempre escrito: `ds:` es el que el 8086 usa por defecto, y no");
            sb.AppendLine("omitirlo evita tener que adivinar si la fila sin prefijo iba por DS o por SS.");
            sb.AppendLine();
            sb.AppendLine("| direccion | ancho | escrita | valores observados |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var x in f.Fijas)
                sb.AppendLine($"| `{x.Seg}:[{x.Dir:X4}h]` | {x.Ancho} | {(x.Escrita ? "si" : "no")} | " +
                              string.Join(", ", x.Valores.Distinct().Take(6)) + " |");
            sb.AppendLine();
        }

        if (f.Llamadas.Count > 0)
        {
            sb.AppendLine("### A que llama, y como se llama eso desde C#");
            sb.AppendLine();
            foreach (var l in f.Llamadas.DistinctBy(l => (l.Tipo, l.Off, l.Seg, l.N, l.Puerto)))
                sb.AppendLine(l.Tipo switch
                {
                    "near" => $"- `{l.En:X4}: call 0{l.Off:X4}h` -> **`o.Llama(0x{l.Off:X4})`**",
                    "far" => $"- `{l.En:X4}: call far {l.Seg:X4}:{l.Off:X4}` -> **`o.LlamaLejos(0x{l.Seg:X4}, 0x{l.Off:X4})`**",
                    "int" => $"- `{l.En:X4}: int {l.N:X2}h` -> **`o.Interrupcion(0x{l.N:X2})`**",
                    "in" => $"- `{l.En:X4}: in {l.Puerto}` -> **`o.LeePuerto8({l.Puerto})`** (o `LeePuerto16`)",
                    "out" => $"- `{l.En:X4}: out {l.Puerto}` -> **`o.EscribePuerto8({l.Puerto}, valor)`**",
                    _ => $"- `{l.En:X4}: call` **indirecta** (el destino esta en un registro o en memoria): " +
                         "calculalo igual que el original y usa `o.Llama(destino)` / `o.LlamaLejos(seg, off)`",
                });
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ==========================================================================================
    //  LA FICHA DE LA CALLEE
    // ==========================================================================================

    /// <summary>Indice del corpus por direccion LINEAL de entrada: dice que funcion grabada vive
    /// en cada sitio. Se lee la cabecera del primer caso de cada carpeta (92 bytes), no el caso
    /// entero — el corpus de un juego de prueba pesa 82 MB y aqui solo hace falta el CS:IP.
    ///
    /// <para>Formato PFC1: "PFC1" | ghidraOffset | callDepth | dsBase | entryCpu[76], y dentro de
    /// <c>CpuState</c> el EIP esta en el byte 32 y el CS en el 40.</para></summary>
    public static Dictionary<int, string> IndicePorDireccion(string corpusDir)
    {
        var idx = new Dictionary<int, string>();
        foreach (var d in Directory.GetDirectories(corpusDir))
        {
            var pfc = Directory.GetFiles(d, "*.pfc").OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
            if (pfc == null) continue;
            var cab = new byte[16 + 76];
            using (var fs = File.OpenRead(pfc))
                if (fs.Read(cab, 0, cab.Length) < cab.Length) continue;
            if (BitConverter.ToUInt32(cab, 0) != 0x31434650) continue;   // "PFC1"
            uint eip = BitConverter.ToUInt32(cab, 16 + 32);
            ushort cs = BitConverter.ToUInt16(cab, 16 + 40);
            idx[(cs << 4) + (int)(eip & 0xFFFF)] = Path.GetFileName(d);
        }
        return idx;
    }

    /// <summary>Lo que se sabe de las funciones a las que ESTA funcion llama. Es la carencia que
    /// dejaron dos de las tres rojas del dia 3: se sabia que conservaba y que cambiaba la funcion
    /// juzgada, y de sus callees nada — asi que una traduccion correcta divergia en un registro
    /// que le habia dejado otro.
    ///
    /// <para>Cada callee se mide con SU PROPIO corpus, con el mismo criterio: conserva lo que sale
    /// igual que entro en todas sus invocaciones grabadas. Si no hay ninguna, se dice — un hueco
    /// declarado vale mas que un dato inventado.</para></summary>
    public static string FichasDeCallees(Ficha f, ushort cs, string corpusDir,
                                          Dictionary<int, string> indice, byte[] mundo)
    {
        var destinos = f.Llamadas
            .Where(l => l.Tipo is "near" or "far")
            .Select(l => (l.Tipo, seg: l.Tipo == "far" ? l.Seg : cs, l.Off))
            .Distinct().ToList();
        if (destinos.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("### Lo que se sabe de las funciones a las que llamas");
        sb.AppendLine();
        sb.AppendLine("No tienes que traducirlas (para eso esta `o.Llama`), pero SI importa que dejan:");
        sb.AppendLine("un registro que devuelve la funcion llamada es un registro que tu no debes tocar");
        sb.AppendLine("despues.");
        sb.AppendLine();
        foreach (var (tipo, seg, off) in destinos)
        {
            string comoSeLlama = tipo == "far" ? $"o.LlamaLejos(0x{seg:X4}, 0x{off:X4})" : $"o.Llama(0x{off:X4})";
            if (!indice.TryGetValue((seg << 4) + off, out var nombre))
            {
                sb.AppendLine($"- **`{comoSeLlama}`**: no hay invocaciones grabadas de esa funcion, " +
                              "asi que **no se sabe** que registros conserva. Trata todo como destruido.");
                continue;
            }
            var casos = PakuPort.Core.Verify.FunctionVerifier.LoadCorpus(corpusDir, nombre);
            if (casos.Count == 0) continue;
            var fc = Observa(BancoCiego.DesensamblaInsn(mundo, seg, off, 200), casos);
            sb.AppendLine($"- **`{comoSeLlama}`** ({nombre}, {casos.Count} invocaciones grabadas):");
            sb.AppendLine($"  conserva `{string.Join("` `", fc.Preservados)}`" +
                          (fc.Cambian.Count > 0 ? $", cambia `{string.Join("` `", fc.Cambian)}`" : ", no cambia ninguno") +
                          (fc.RetN > 0 ? $", y quita **{fc.RetN} bytes** de argumentos de la pila." : "."));
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Las invocaciones reales, tal cual: el material que mas rapido cierra una duda.</summary>
    public static string Ejemplos(List<CorpusCase> casos, int cuantos = 3)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Invocaciones reales (grabadas del juego corriendo)");
        sb.AppendLine();
        sb.AppendLine("Si tu version reproduce estas, va bien. Salen del mismo corpus con el que se te va");
        sb.AppendLine("a juzgar (hay mas casos, y se exigen TODOS).");
        sb.AppendLine();
        foreach (var c in casos.Take(cuantos))
        {
            sb.AppendLine($"- entra: {Regs(c.EntryCpu)}");
            sb.AppendLine($"  sale : {Regs(c.ExitCpu)}");
            var tramos = PakuPort.Judge.Diff.MemoryDiff.Compare(c.EntryMem, c.ExitMem);
            if (tramos.Count == 0) sb.AppendLine("  no escribio nada en memoria.");
            else
            {
                sb.AppendLine($"  escribio {tramos.Count} tramo(s):");
                foreach (var t in tramos.Take(4)) sb.AppendLine($"    {t}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("> Los tramos de memoria incluyen la PILA (la funcion empuja y saca): es normal, y");
        sb.AppendLine("> forma parte de lo que se compara.");
        sb.AppendLine();
        return sb.ToString();
    }

    static string Regs(in PakuPort.Judge.Oracle.CpuState s) =>
        $"AX={s.Ax:X4} BX={s.Bx:X4} CX={s.Cx:X4} DX={s.Dx:X4} SI={(ushort)s.Esi:X4} DI={(ushort)s.Edi:X4} " +
        $"BP={(ushort)s.Ebp:X4} SP={(ushort)s.Esp:X4} DS={s.Ds:X4} ES={s.Es:X4}";
}
