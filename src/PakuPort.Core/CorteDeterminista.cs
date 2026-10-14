namespace PakuPort.Core;

/// <summary>
/// `PAKU_AUTOEXIT_INSN=&lt;n&gt;` — CORTAR LA PARTIDA POR INSTRUCCIONES, no por reloj de pared.
///
/// <para><b>Para que existe.</b> Comparar dos corridas del mismo juego es la unica forma de decir
/// en numeros que un cambio no ha roto nada, y con <c>PAKU_AUTOEXIT_MS</c> no se puede: es un
/// cronometro del ANFITRION, asi que dos corridas identicas terminan en puntos distintos del
/// programa. Medido en un juego de prueba: 104.719 y 104.357 blits, y los fotogramas presentados
/// divergian en el segundo. Cortando por <see cref="Cpu.TickCount"/> las dos corridas paran en la
/// MISMA instruccion del invitado.</para>
///
/// <para>Al cortar vuelca lo que se le pida y termina el proceso: el hilo del juego no acaba nunca
/// por si solo en una corrida sin ventana, asi que esperar a que retorne no es una opcion.</para>
///
/// <para><b>Coste apagado: cero en el camino caliente.</b> La comprobacion viaja dentro del poll
/// periodico de <see cref="Cpu.Step"/>, que ya existia y solo entra una vez cada 4096
/// instrucciones. Eso deja el corte con una resolucion de 4096 instrucciones, que sigue siendo
/// determinista: el contador avanza igual en las dos corridas.</para>
/// </summary>
public static class CorteDeterminista
{
    /// <summary>Instrucciones tras las que se corta, o 0 si no se corta.</summary>
    public static long Insn =
        long.TryParse(Environment.GetEnvironmentVariable("PAKU_AUTOEXIT_INSN"), out var n) ? n : 0;

    /// <summary>UNA CORRIDA SIN VENTANA: no se abre ventana visible, no se roba el foco y no se
    /// captura el raton. Es UNA sola definicion a proposito — estaba escrita cinco veces en la
    /// plantilla del host, siempre como "hay PAKU_AUTOEXIT_MS", y al anadir el corte por
    /// instrucciones esos cinco sitios se quedaron fuera: las corridas de medida empezaron a
    /// abrir ventana y a robarle el foco al usuario, que es justo lo que la regla de la casa
    /// prohibe. `PAKU_HEADLESS=1` la fuerza sin ninguna condicion de parada.</summary>
    public static bool SinVentana =>
        Insn > 0
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PAKU_AUTOEXIT_MS"))
        || Environment.GetEnvironmentVariable("PAKU_HEADLESS") == "1";

    static bool _cortando;

    /// <summary>`PAKU_CORTE_EN_INT=1`: pasado el limite no se corta en el poll, sino en la
    /// PRIMERA interrupcion software que ejecute el programa despues.
    ///
    /// <para><b>Para el A/B de un juego TRADUCIDO.</b> Una traduccion cobra sus instrucciones por
    /// bloques (ver <c>Original.Instrucciones</c>), asi que en mitad de un bloque su memoria no
    /// tiene por que coincidir con la del original en esa misma instruccion: una ya escribio lo
    /// que la otra aun no. Justo antes de un `int` las dos tienen que estar igual — la traduccion
    /// cobra hasta el `int` incluido y hace antes todo lo que el original habia hecho. Cortar ahi
    /// compara lo comparable, y el corte sigue siendo el mismo en cualquier corrida.</para></summary>
    public static readonly bool EnInt = Environment.GetEnvironmentVariable("PAKU_CORTE_EN_INT") == "1";
    static bool _pendiente;

    /// <summary>Se llama al ejecutar un `int` (interpretado o desde una traduccion).</summary>
    public static void EnInterrupcion(Cpu c)
    {
        if (_pendiente) Corta(c);
    }

    /// <summary>Se llama desde el poll periodico de <see cref="Cpu.Step"/>.</summary>
    public static void Comprueba(Cpu c)
    {
        if (Insn <= 0 || Cpu.TickCount < Insn || _cortando) return;
        if (EnInt) { _pendiente = true; return; }
        Corta(c);
    }

    static void Corta(Cpu c)
    {
        if (_cortando) return;
        _cortando = true;

        Console.Error.WriteLine($"[CORTE] {Cpu.TickCount} instrucciones: se para aqui, " +
                                "en el mismo punto en cualquier corrida");
        try
        {
            CorpusRecorder.Termina();
            FuncionesPortadas.Vuelca();
            string? dump = Environment.GetEnvironmentVariable("PAKU_MEMDUMP");
            if (!string.IsNullOrEmpty(dump) && Memory.Last != null)
            {
                File.WriteAllBytes(dump, Memory.Last.Raw);
                // Y LA PANTALLA, en `<dump>.vram`: los cuatro planos y el DAC. La VRAM NO vive en
                // el MiB del invitado —son buffers de VideoState—, asi que un A/B que solo mire
                // `Memory.Raw` es CIEGO para todo lo que el juego DIBUJA. Medido: una funcion de
                // render rota a proposito (el salto de fila cambiado) daba exactamente el mismo
                // volcado de memoria que la buena, y el juez la habria dado por equivalente.
                var v = Memory.Last.Video;
                if (v != null)
                {
                    var vram = new byte[v.Planes[0].Length * 4 + v.Dac.Length];
                    for (int i = 0; i < 4; i++)
                        Array.Copy(v.Planes[i], 0, vram, i * v.Planes[i].Length, v.Planes[i].Length);
                    Array.Copy(v.Dac, 0, vram, v.Planes[0].Length * 4, v.Dac.Length);
                    File.WriteAllBytes(dump + ".vram", vram);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Console.Out.Flush();
        Console.Error.Flush();
        if (Foto)
        {
            // The host's render thread presents a few more frames of the frozen guest (with the
            // remake's HD layers on top), saves screenshot.png and exits. The game thread stays
            // here: the picture is of exactly this instruction, in any run.
            Congelado = true;
            Thread.Sleep(Timeout.Infinite);
        }
        Environment.Exit(0);
    }

    /// <summary>`PAKU_CORTE_FOTO=1` with `PAKU_AUTOEXIT_INSN`: instead of exiting at the cut, freeze
    /// the guest and let the host save the COMPOSED screen (game + HD layers) as screenshot.png.
    /// `PAKU_FB_PNG` gives the guest's own frame; this is what the player would see, at a
    /// repeatable instant, which is what a before/after comparison of HD art needs.</summary>
    public static readonly bool Foto = Environment.GetEnvironmentVariable("PAKU_CORTE_FOTO") == "1";

    /// <summary>Set by the game thread when it has frozen at the cut (see <see cref="Foto"/>).</summary>
    public static volatile bool Congelado;
}
