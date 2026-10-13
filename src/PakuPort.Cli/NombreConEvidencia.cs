using Iced.Intel;

/// <summary>
/// EL NOMBRE DE UNA FUNCION, CON SU PRUEBA AL LADO — y ninguno cuando no hay prueba.
///
/// <para><b>Por que existe.</b> El tablero del remake entrega funciones que se llaman
/// <c>FUN_1000_15c78</c>, y una IA que lee doscientas de esas no tiene por donde empezar. Pero
/// ponerle un nombre adivinado es PEOR que no ponerle ninguno: un nombre se lee como un hecho, y
/// la IA lo usa sin verificarlo. Por eso aqui un nombre solo sale si hay una evidencia concreta que
/// se pueda citar, la evidencia viaja escrita al lado, y cuando no la hay se dice en voz alta.</para>
///
/// <para><b>De donde sale la evidencia.</b> De lo que la funcion HACE, no de lo que parece: el
/// servicio de DOS o de la BIOS que invoca (con su subfuncion), los puertos de E/S que toca, y la
/// capa que ya midio el dia 4 (`PAKU_CAPAS`). Nada de heuristicas sobre el tamano o la forma.</para>
///
/// <para><b>La subfuncion se lee del cuerpo, no se adivina.</b> Un <c>int 21h</c> no dice nada por
/// si solo; lo que lo dice es el <c>mov ah, 3Dh</c> de justo antes. Se busca hacia atras en linea
/// recta, y <b>se descarta si al tramo mirado salta alguien</b>: si se puede llegar ahi por otro
/// camino, ese <c>mov</c> no es necesariamente el que corrio. Eso deja fuera casos legitimos, y es
/// lo correcto — el precio de un nombre inventado es mas alto que el de un hueco.</para>
/// </summary>
static class NombreConEvidencia
{
    public sealed class Propuesta
    {
        /// <summary>null = no hay evidencia suficiente. No se rellena "por si acaso".</summary>
        public string? Nombre;
        /// <summary>alta | media. Sin nombre no hay confianza que dar.</summary>
        public string Confianza = "";
        public List<string> Evidencia = new();
    }

    /// <summary>Servicios con subfuncion en AH: (int, ah) -> verbo del nombre y texto humano.</summary>
    static readonly Dictionary<(byte n, byte ah), (string verbo, string que)> Servicios = new()
    {
        // --- INT 21h, DOS ---
        [(0x21, 0x01)] = ("lee_teclado", "leer un caracter del teclado con eco"),
        [(0x21, 0x02)] = ("escribe_texto", "escribir un caracter en pantalla"),
        [(0x21, 0x06)] = ("escribe_texto", "E/S directa por consola"),
        [(0x21, 0x07)] = ("lee_teclado", "leer un caracter sin eco"),
        [(0x21, 0x08)] = ("lee_teclado", "leer un caracter sin eco"),
        [(0x21, 0x09)] = ("escribe_texto", "escribir una cadena en pantalla"),
        [(0x21, 0x0B)] = ("mira_teclado", "preguntar si hay tecla pendiente"),
        [(0x21, 0x0C)] = ("mira_teclado", "vaciar el buffer y leer"),
        [(0x21, 0x1A)] = ("busca_ficheros", "fijar la DTA"),
        [(0x21, 0x25)] = ("instala_manejador", "instalar un vector de interrupcion"),
        [(0x21, 0x2A)] = ("lee_la_fecha", "pedir la fecha del sistema"),
        [(0x21, 0x2C)] = ("lee_la_hora", "pedir la hora del sistema"),
        [(0x21, 0x30)] = ("pregunta_la_version", "pedir la version de DOS"),
        [(0x21, 0x35)] = ("lee_manejador", "leer un vector de interrupcion"),
        [(0x21, 0x39)] = ("crea_directorio", "crear un directorio"),
        [(0x21, 0x3B)] = ("cambia_directorio", "cambiar de directorio"),
        [(0x21, 0x3C)] = ("crea_fichero", "crear un fichero"),
        [(0x21, 0x3D)] = ("abre_fichero", "abrir un fichero"),
        [(0x21, 0x3E)] = ("cierra_fichero", "cerrar un fichero"),
        [(0x21, 0x3F)] = ("lee_fichero", "leer de un fichero"),
        [(0x21, 0x40)] = ("escribe_fichero", "escribir en un fichero"),
        [(0x21, 0x41)] = ("borra_fichero", "borrar un fichero"),
        [(0x21, 0x42)] = ("mueve_puntero", "mover el puntero de un fichero"),
        [(0x21, 0x43)] = ("atributos_de_fichero", "leer o poner atributos"),
        [(0x21, 0x47)] = ("pregunta_directorio", "pedir el directorio actual"),
        [(0x21, 0x48)] = ("reserva_memoria", "reservar memoria a DOS"),
        [(0x21, 0x49)] = ("libera_memoria", "liberar memoria de DOS"),
        [(0x21, 0x4A)] = ("ajusta_memoria", "redimensionar un bloque de memoria"),
        [(0x21, 0x4B)] = ("lanza_programa", "cargar y ejecutar otro programa"),
        [(0x21, 0x4C)] = ("termina", "terminar el programa"),
        [(0x21, 0x4E)] = ("busca_ficheros", "buscar el primer fichero que casa"),
        [(0x21, 0x4F)] = ("busca_ficheros", "buscar el siguiente fichero"),
        [(0x21, 0x56)] = ("renombra_fichero", "renombrar un fichero"),
        // --- INT 10h, video de la BIOS ---
        [(0x10, 0x00)] = ("pone_modo_video", "fijar el modo de video"),
        [(0x10, 0x02)] = ("mueve_cursor", "mover el cursor"),
        [(0x10, 0x06)] = ("desplaza_pantalla", "hacer scroll de una ventana"),
        [(0x10, 0x09)] = ("escribe_en_pantalla", "escribir caracteres con atributo"),
        [(0x10, 0x0A)] = ("escribe_en_pantalla", "escribir caracteres"),
        [(0x10, 0x0C)] = ("pinta_punto", "escribir un pixel"),
        [(0x10, 0x0D)] = ("lee_punto", "leer un pixel"),
        [(0x10, 0x0E)] = ("escribe_en_pantalla", "escribir en modo teletipo"),
        [(0x10, 0x0F)] = ("lee_modo_video", "preguntar el modo de video actual"),
        [(0x10, 0x10)] = ("cambia_la_paleta", "tocar la paleta o el DAC"),
        [(0x10, 0x11)] = ("carga_la_fuente", "cargar el generador de caracteres"),
        [(0x10, 0x13)] = ("escribe_en_pantalla", "escribir una cadena"),
        // --- INT 16h, teclado de la BIOS ---
        [(0x16, 0x00)] = ("espera_tecla", "esperar a que haya tecla"),
        [(0x16, 0x01)] = ("mira_teclado", "preguntar si hay tecla"),
        [(0x16, 0x02)] = ("lee_modificadores", "leer el estado de Shift/Ctrl/Alt"),
        [(0x16, 0x10)] = ("espera_tecla", "esperar tecla (teclado extendido)"),
        [(0x16, 0x11)] = ("mira_teclado", "preguntar si hay tecla (extendido)"),
        // --- INT 33h, raton ---
        [(0x33, 0x00)] = ("inicia_raton", "reiniciar el raton"),
        [(0x33, 0x01)] = ("muestra_cursor", "mostrar el cursor del raton"),
        [(0x33, 0x02)] = ("oculta_cursor", "ocultar el cursor del raton"),
        [(0x33, 0x03)] = ("lee_raton", "leer posicion y botones"),
        [(0x33, 0x04)] = ("mueve_raton", "colocar el cursor"),
        [(0x33, 0x07)] = ("limita_raton", "fijar el rango horizontal"),
        [(0x33, 0x08)] = ("limita_raton", "fijar el rango vertical"),
        [(0x33, 0x0B)] = ("lee_raton", "leer el movimiento relativo"),
        [(0x33, 0x0C)] = ("instala_callback_raton", "registrar un callback de eventos"),
        // --- INT 1Ah, reloj ---
        [(0x1A, 0x00)] = ("lee_el_reloj", "leer el contador de tics"),
        [(0x1A, 0x02)] = ("lee_la_hora", "leer la hora del reloj de tiempo real"),
    };

    /// <summary>Rangos de puertos con dueno conocido. Es evidencia mas debil que una subfuncion:
    /// dice el subsistema, no el verbo.</summary>
    static readonly (int lo, int hi, string que)[] Puertos =
    {
        (0x0020, 0x0021, "el controlador de interrupciones"),
        (0x0040, 0x0043, "el temporizador"),
        (0x0060, 0x0064, "el teclado y el altavoz"),
        (0x0201, 0x0201, "el puerto de juegos"),
        (0x0220, 0x022F, "la Sound Blaster"),
        (0x0330, 0x0331, "el MPU-401 (MIDI)"),
        (0x0388, 0x0389, "el sintetizador OPL"),
        (0x03B4, 0x03BF, "el video (MDA/Hercules)"),
        (0x03C0, 0x03CF, "los registros del VGA"),
        (0x03D0, 0x03DF, "el CRTC y el retrace"),
    };

    /// <summary>Verbos que la capa medida respalda: si la capa dice Render y el verbo pinta, las
    /// dos pruebas apuntan al mismo sitio y el nombre sube a confianza alta.</summary>
    static bool CapaRespalda(string? capa, string verbo) => capa switch
    {
        "Render" => verbo.StartsWith("pinta") || verbo.StartsWith("escribe_en_pantalla")
                    || verbo.StartsWith("pone_modo") || verbo.Contains("paleta")
                    || verbo.Contains("fuente") || verbo.Contains("cursor") || verbo.Contains("pantalla"),
        "Audio" => verbo.Contains("sonido") || verbo.Contains("musica"),
        "ES" => verbo.Contains("fichero") || verbo.Contains("teclado") || verbo.Contains("raton")
                || verbo.Contains("tecla") || verbo.Contains("memoria") || verbo.Contains("directorio"),
        _ => false,
    };

    public static Propuesta Propone(SortedDictionary<ushort, Instruction> cuerpo,
                                    Expediente.Ficha ficha, string? capa, byte[] dgroup)
    {
        var p = new Propuesta();

        // Todo destino de salto DENTRO de la funcion: si el tramo que miro hacia atras contiene
        // uno, no puedo afirmar que ese `mov ah` sea el que corrio.
        var destinos = new HashSet<ushort>();
        foreach (var (_, insn) in cuerpo)
            if (insn.Op0Kind == OpKind.NearBranch16 && insn.Mnemonic != Mnemonic.Call)
                destinos.Add((ushort)insn.NearBranchTarget);

        var orden = cuerpo.Keys.ToList();
        var verbos = new List<string>();

        for (int i = 0; i < orden.Count; i++)
        {
            var insn = cuerpo[orden[i]];
            if (insn.Mnemonic != Mnemonic.Int) continue;
            byte n = insn.Immediate8;
            var (ah, seguro) = AhAntesDe(cuerpo, orden, i, destinos);
            if (ah == null)
            {
                // Un driver del propio juego no tiene tabla, pero saberlo ya orienta.
                if (n >= 0x60 && n != 0x67)
                    p.Evidencia.Add($"int {n:X2}h en 0x{orden[i]:X4}: un driver del propio juego");
                continue;
            }
            if (!seguro)
            {
                p.Evidencia.Add($"int {n:X2}h en 0x{orden[i]:X4}: la subfuncion no es segura " +
                                $"(al `mov ah` de antes se puede llegar por un salto)");
                continue;
            }
            if (Servicios.TryGetValue((n, ah.Value), out var s))
            {
                if (!verbos.Contains(s.verbo)) verbos.Add(s.verbo);
                // Y si el servicio recibe un nombre en DS:DX, se LEE de la memoria del caso.
                // Es lo que convierte "abre un fichero" en "abre CHARGEN.PL", que es lo que
                // de verdad orienta a quien traduce.
                string? cad = n == 0x21 && PideCadenaEnDx(ah.Value)
                    ? CadenaDeDx(cuerpo, orden, i, destinos, dgroup) : null;
                p.Evidencia.Add($"int {n:X2}h AH={ah.Value:X2}h en 0x{orden[i]:X4}: {s.que}"
                                + (cad != null ? $" — {cad}" : ""));
            }
            else
            {
                p.Evidencia.Add($"int {n:X2}h AH={ah.Value:X2}h en 0x{orden[i]:X4}: sin nombre en la tabla");
            }
        }

        // Los puertos: mas debiles, y solo se anotan como prueba de apoyo.
        foreach (var l in ficha.Llamadas)
        {
            if (l.Tipo is not ("in" or "out")) continue;
            if (!l.Puerto.StartsWith("0x", StringComparison.Ordinal)) continue;
            int puerto = Convert.ToInt32(l.Puerto[2..], 16);
            foreach (var (lo, hi, que) in Puertos)
                if (puerto >= lo && puerto <= hi)
                {
                    string ev = $"{l.Tipo} sobre {l.Puerto}: {que}";
                    if (!p.Evidencia.Contains(ev)) p.Evidencia.Add(ev);
                    break;
                }
        }

        // LAS CADENAS QUE TOCA. Para las funciones de logica —que en un juego de prueba son dos de
        // cada tres— no hay servicio del sistema al que agarrarse, y sin embargo el texto que
        // referencian dice de que van. Se lee del DGROUP que el corpus grabo al ENTRAR, asi que
        // es dato medido: si ahi hay texto, ese texto estaba de verdad en la memoria del juego.
        var vistas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fija in ficha.Fijas)
        {
            if (vistas.Count >= 6) break;
            if (Texto(dgroup, fija.Dir) is not string t || !vistas.Add(t)) continue;
            p.Evidencia.Add($"la global {fija.Seg}:[{fija.Dir:X4}h] es el texto {t}");
        }
        foreach (var (at, insn) in cuerpo)
        {
            if (vistas.Count >= 6) break;
            if (insn.Mnemonic != Mnemonic.Mov || insn.Op0Kind != OpKind.Register) continue;
            if (insn.Op0Register is not (Register.SI or Register.DI or Register.BX or Register.DX)) continue;
            if (insn.Op1Kind is not (OpKind.Immediate16 or OpKind.Immediate8to16)) continue;
            if (Texto(dgroup, insn.Immediate16) is not string t || !vistas.Add(t)) continue;
            p.Evidencia.Add($"en 0x{at:X4} carga la direccion del texto {t}");
        }

        if (verbos.Count == 0)
        {
            // Sin verbo no hay nombre. Se dice lo que se sabe y se para.
            p.Evidencia.Add(capa != null
                ? $"la capa medida dice {capa}, pero ninguna prueba dice QUE hace: sin nombre"
                : "no hay ninguna prueba de que hace: sin nombre");
            return p;
        }

        p.Nombre = verbos.Count == 1 ? verbos[0] : verbos[0] + "_y_" + verbos[1];
        bool respaldada = capa != null && verbos.Any(v => CapaRespalda(capa, v));
        p.Confianza = respaldada || verbos.Count >= 2 ? "alta" : "media";
        if (respaldada) p.Evidencia.Add($"la capa medida ({capa}) dice lo mismo que el servicio");
        return p;
    }

    /// <summary>El AH con que se llama a esa INT, leido del <c>mov</c> mas cercano hacia atras.
    /// <paramref name="destinos"/> son las direcciones a las que salta alguien: si el tramo mirado
    /// contiene una, el valor no es seguro y se declara en vez de usarse.</summary>
    static (byte? ah, bool seguro) AhAntesDe(SortedDictionary<ushort, Instruction> cuerpo,
                                             List<ushort> orden, int iInt, HashSet<ushort> destinos)
    {
        const int Ventana = 12;
        for (int j = iInt - 1; j >= 0 && iInt - j <= Ventana; j--)
        {
            var insn = cuerpo[orden[j]];
            // Otra llamada o un retorno por medio: lo de mas atras ya no es de esta INT.
            if (insn.Mnemonic is Mnemonic.Int or Mnemonic.Call or Mnemonic.Ret or Mnemonic.Retf) break;
            bool esAh = insn.Mnemonic == Mnemonic.Mov && insn.Op0Kind == OpKind.Register
                        && insn.Op0Register == Register.AH && insn.Op1Kind == OpKind.Immediate8;
            bool esAx = insn.Mnemonic == Mnemonic.Mov && insn.Op0Kind == OpKind.Register
                        && insn.Op0Register == Register.AX
                        && insn.Op1Kind is OpKind.Immediate16 or OpKind.Immediate8to16;
            if (!esAh && !esAx) continue;
            // Entre el `mov` y la INT no puede entrar nadie por un salto.
            bool seguro = true;
            for (int k = j; k <= iInt; k++) if (destinos.Contains(orden[k])) seguro = false;
            byte ah = esAh ? insn.Immediate8 : (byte)(insn.Immediate16 >> 8);
            return (ah, seguro);
        }
        return (null, false);
    }

    /// <summary>Servicios de DOS cuyo argumento es un nombre de fichero en DS:DX.</summary>
    static bool PideCadenaEnDx(byte ah) => ah is 0x39 or 0x3A or 0x3B or 0x3C or 0x3D
                                                or 0x41 or 0x43 or 0x4B or 0x4E or 0x56;

    /// <summary>El texto al que apunta el <c>mov dx, imm</c> de antes de la INT, leido del
    /// DGROUP que el corpus grabo al entrar en la funcion. Devuelve null en cuanto algo no
    /// cuadra: sin DX inmediato, fuera de rango, o bytes que no son texto.</summary>
    static string? CadenaDeDx(SortedDictionary<ushort, Instruction> cuerpo, List<ushort> orden,
                              int iInt, HashSet<ushort> destinos, byte[] dgroup)
    {
        for (int j = iInt - 1; j >= 0 && iInt - j <= 12; j--)
        {
            var insn = cuerpo[orden[j]];
            if (insn.Mnemonic is Mnemonic.Int or Mnemonic.Call) break;
            if (insn.Mnemonic != Mnemonic.Mov || insn.Op0Kind != OpKind.Register
                || insn.Op0Register != Register.DX
                || insn.Op1Kind is not (OpKind.Immediate16 or OpKind.Immediate8to16)) continue;
            for (int k = j; k <= iInt; k++) if (destinos.Contains(orden[k])) return null;
            return Texto(dgroup, insn.Immediate16);
        }
        return null;
    }

    /// <summary>Texto imprimible terminado en 0 o en '$', y que EMPIEZA donde se dice.
    ///
    /// <para>Lo de "empieza donde se dice" es la mitad del asunto, y se aprendio midiendo: sin
    /// exigir que el byte de antes cierre otra cadena, un inmediato cualquiera que cayera dentro
    /// del area de textos daba cadenas CORTADAS y creibles — "ny key." por "any key.",
    /// "ocate too many bytes" por "allocate too many bytes". Eso es justo lo que esta clase no
    /// puede permitirse: una evidencia falsa con pinta de medida.</para>
    ///
    /// <para>Tambien pide cuatro caracteres y al menos una letra: con menos, cualquier basura
    /// pasa por cadena.</para></summary>
    static string? Texto(byte[] mem, int off)
    {
        if (off <= 0 || off >= mem.Length) return null;
        byte antes = mem[off - 1];
        if (antes != 0 && antes != (byte)'$') return null;
        var sb = new System.Text.StringBuilder();
        bool letra = false;
        for (int i = off; i < mem.Length && sb.Length < 64; i++)
        {
            byte b = mem[i];
            if (b == 0 || b == (byte)'$')
                return sb.Length >= 4 && letra ? $"\"{sb}\"" : null;
            if (b < 0x20 || b > 0x7E) return null;
            letra |= (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');
            sb.Append((char)b);
        }
        return null;
    }
}
