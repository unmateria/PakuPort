using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

/// <summary>
/// LAS TRADUCCIONES VIAJAN AL JUEGO — `pakuport export --portadas &lt;tablero&gt;`.
///
/// <para>El tablero del remake se juzga con Roslyn en el CLI, pero la entrega es un
/// <c>Jugar&lt;Juego&gt;.exe</c> de doble clic que no lleva compilador. Asi que las
/// <c>respuesta.cs</c> se COPIAN al proyecto exportado y se compilan con el: dentro del juego, una
/// funcion traducida es codigo C# normal, no algo que se compile al arrancar.</para>
///
/// <para><b>Cada respuesta va en su propio namespace</b> (<c>PakuPort.Remake.FUN_1000_xxxx</c>).
/// Quien traduce llama a su clase como quiera, y lo natural es llamarla <c>Respuesta</c>: sin
/// envolver, la segunda funcion traducida rompe la compilacion del juego entero por un nombre
/// repetido.</para>
///
/// <para><b>El registro se engancha solo</b>, con un <c>ModuleInitializer</c>: corre antes del
/// <c>Main</c> del juego y no hay que tocar la plantilla del export. Sin remake no se genera el
/// fichero y no cambia nada.</para>
/// </summary>
static class RemakeAlExport
{
    /// <summary>Copia al export las traducciones del tablero y genera su registro. Devuelve
    /// cuantas viajaron, o -1 si el tablero no vale.</summary>
    public static int Copia(string tableroDir, string outDir)
    {
        string bancoJson = Path.Combine(tableroDir, "banco.json");
        if (!File.Exists(bancoJson))
        {
            Console.Error.WriteLine($"  portadas: falta {bancoJson} (genera el tablero con `pakuport expediente tablero`)");
            return -1;
        }

        string remakeDir = Path.Combine(outDir, "Remake");
        if (Directory.Exists(remakeDir)) Directory.Delete(remakeDir, recursive: true);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(bancoJson));
        var registros = new List<string>();
        var sinTraducir = 0;
        var noVale = new List<string>();

        // Los bytes de argumentos de cada `ret n`, medidos por el expediente: el gancho los
        // necesita para distinguir una salida normal de una por un atajo.
        var retN = new Dictionary<string, int>();
        string expJson = Path.Combine(tableroDir, "expediente.json");
        if (File.Exists(expJson))
        {
            using var ex = System.Text.Json.JsonDocument.Parse(File.ReadAllText(expJson));
            foreach (var e in ex.RootElement.GetProperty("funciones").EnumerateArray())
                if (e.TryGetProperty("ret", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Object
                    && r.TryGetProperty("bytes_de_argumentos", out var n))
                    retN[e.GetProperty("func").GetString()!] = n.GetInt32();
        }

        foreach (var f in doc.RootElement.GetProperty("funciones").EnumerateArray())
        {
            string func = f.GetProperty("func").GetString()!;
            string resp = Path.Combine(tableroDir, func, "respuesta.cs");
            if (!File.Exists(resp)) { sinTraducir++; continue; }

            string fuente = Envuelve(func, File.ReadAllText(resp));
            var punto = Localiza(fuente);
            if (punto == null) { noVale.Add(func + ": no encuentro un metodo estatico publico (Cpu, Memory[, Original]) que devuelva void o ushort"); continue; }
            var (donde, metodo, parametros, _, esVuelta) = punto.Value;
            // UNA VUELTA DE BUCLE devuelve por donde sigue el programa, porque no tiene direccion
            // de retorno en la pila: el gancho continua donde ella diga (ver
            // FuncionesPortadas.RegistraVuelta). Lo que no puede pasar es que el tablero diga que
            // es una vuelta y la traduccion no lo sepa, o al reves: el juego seguiria por lo que
            // hubiera en la pila, que es basura. El juez ya lo caza; aqui se vuelve a mirar porque
            // lo que se enchufa al juego no tiene por que haber pasado por el juez.
            bool tableroDiceVuelta = f.TryGetProperty("bucle", out var bu) && bu.GetBoolean();
            if (tableroDiceVuelta != esVuelta)
            {
                noVale.Add(func + (tableroDiceVuelta
                    ? ": es una VUELTA de bucle y la traduccion devuelve void (tiene que devolver ushort: por donde sigue)"
                    : ": es una funcion y la traduccion devuelve ushort (tiene que devolver void y salir con su `ret`)"));
                continue;
            }

            Directory.CreateDirectory(remakeDir);
            File.WriteAllText(Path.Combine(remakeDir, func + ".cs"), fuente);

            ushort cs = Convert.ToUInt16(f.GetProperty("cs").GetString()!, 16);
            ushort ip = Convert.ToUInt16(f.GetProperty("ip").GetString()!, 16);
            bool retf = f.TryGetProperty("retf", out var rf) && rf.GetBoolean();
            string llamada = parametros == 3
                ? $"(c, m) => {donde}.{metodo}(c, m, new PakuPort.Core.Verify.Original(c, m))"
                : $"{donde}.{metodo}";
            bool programa = f.TryGetProperty("programa", out var pr) && pr.GetBoolean();
            registros.Add(programa
                ? $"        PakuPort.Core.FuncionesPortadas.RegistraPrograma(0x{cs:X4}, 0x{ip:X4}, {llamada});"
                : esVuelta
                ? $"        PakuPort.Core.FuncionesPortadas.RegistraVuelta(0x{cs:X4}, 0x{ip:X4}, {llamada}, \"{func}\");"
                : $"        PakuPort.Core.FuncionesPortadas.Registra(0x{cs:X4}, 0x{ip:X4}, " +
                  $"{llamada}, \"{func}\", lejana: {(retf ? "true" : "false")}, retN: {retN.GetValueOrDefault(func)});");
        }

        foreach (var n in noVale) Console.Error.WriteLine("  portadas: " + n);
        if (registros.Count == 0)
        {
            Console.WriteLine($"  portadas: ninguna funcion traducida en {tableroDir} ({sinTraducir} sin respuesta.cs)");
            return 0;
        }

        var sb = new StringBuilder();
        sb.AppendLine("// GENERADO por `pakuport export --portadas`. No editar a mano.");
        sb.AppendLine("//");
        sb.AppendLine("// Las funciones de aqui abajo YA ESTAN TRADUCIDAS a C#: el interprete las ejecuta en");
        sb.AppendLine("// vez de interpretar el original, y todo lo demas del juego sigue siendo el binario.");
        sb.AppendLine("// El enganche corre antes del Main (ModuleInitializer), asi que no hay que tocar el");
        sb.AppendLine("// arranque del juego.");
        sb.AppendLine();
        sb.AppendLine("static class RemakeRegistro");
        sb.AppendLine("{");
        sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Registra()");
        sb.AppendLine("    {");
        foreach (var r in registros) sb.AppendLine(r);
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(remakeDir, "RemakeRegistro.cs"), sb.ToString());

        Console.WriteLine($"  portadas: {registros.Count} funciones traducidas viajan al juego " +
                          $"({sinTraducir} siguen sin traducir)" +
                          (noVale.Count > 0 ? $", {noVale.Count} descartadas" : ""));
        return registros.Count;
    }

    /// <summary>La respuesta tal cual si ya trae namespace, o envuelta en
    /// <c>PakuPort.Remake.&lt;func&gt;</c> si no. Quien traduce llama a su clase como quiera —lo
    /// natural es <c>Respuesta</c>— y sin envolver, la segunda funcion traducida romperia la
    /// compilacion por un nombre repetido. El juez usa este MISMO envoltorio: lo que compila alli
    /// compila en el juego, y una traduccion puede llamar a otra por ese nombre.</summary>
    internal static string Envuelve(string func, string fuente)
    {
        bool tieneNs = CSharpSyntaxTree.ParseText(fuente).GetRoot().DescendantNodes()
            .Any(n => n is BaseNamespaceDeclarationSyntax);
        return tieneNs ? fuente : $"namespace PakuPort.Remake.{func}\n{{\n{fuente}\n}}\n";
    }

    /// <summary>Donde esta el metodo que hay que llamar. Se lee del arbol de sintaxis y no por
    /// reflexion porque aqui no se compila nada: quien traduce elige el nombre de su clase y de su
    /// metodo, y el registro tiene que nombrarlos tal cual. Si hay varios candidatos (una vuelta
    /// que deja ayudantes publicos para que otra los llame), manda el que se llame `Ejecuta`.
    ///
    /// <para>Lo que devuelve decide que es: <c>void</c> es una FUNCION, que sale por su `ret`;
    /// <c>ushort</c> es una VUELTA de bucle, que devuelve la direccion por la que sigue el
    /// programa.</para></summary>
    internal static (string clase, string metodo, int parametros, bool tieneNamespace, bool esVuelta)? Localiza(string fuente)
    {
        var raiz = CSharpSyntaxTree.ParseText(fuente).GetRoot();
        bool tieneNs = raiz.DescendantNodes().Any(n => n is BaseNamespaceDeclarationSyntax);

        (string, string, int, bool, bool)? primero = null;
        foreach (var m in raiz.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!m.Modifiers.Any(SyntaxKind.StaticKeyword) || !m.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;
            var ps = m.ParameterList.Parameters;
            if (ps.Count is not (2 or 3)) continue;
            if (!Tipo(ps[0], "Cpu") || !Tipo(ps[1], "Memory")) continue;
            if (ps.Count == 3 && !Tipo(ps[2], "Original")) continue;
            string ret = m.ReturnType.ToString();
            bool esVuelta = ret is "ushort" or "System.UInt16" or "UInt16";
            if (!esVuelta && ret != "void") continue;
            if (m.Parent is not TypeDeclarationSyntax t) continue;

            // El nombre completo incluye el namespace propio del fichero, si lo trae, y las clases
            // que lo envuelvan: sin eso el registro no sabria nombrarlo.
            var partes = new List<string> { t.Identifier.Text };
            for (var p = t.Parent; p != null; p = p.Parent)
            {
                if (p is TypeDeclarationSyntax padre) partes.Insert(0, padre.Identifier.Text);
                else if (p is BaseNamespaceDeclarationSyntax nsD) partes.Insert(0, nsD.Name.ToString());
            }
            var este = (string.Join(".", partes), m.Identifier.Text, ps.Count, tieneNs, esVuelta);
            if (m.Identifier.Text == "Ejecuta") return este;
            primero ??= este;
        }
        return primero;
    }

    static bool Tipo(ParameterSyntax p, string nombre)
    {
        string t = p.Type?.ToString() ?? "";
        return t == nombre || t.EndsWith("." + nombre, StringComparison.Ordinal);
    }
}
