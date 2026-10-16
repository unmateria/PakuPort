namespace PakuPort.Core;

/// <summary>
/// Remake-kit step 3: deterministic input record/replay.
/// (Env vars are PAKU_TAPE_*, not PAKU_INPUT_*: PAKU_INPUT_REPLAY already belongs to the
/// older mouse-only replay in Int33hHandler and must keep working.)
/// PAKU_TAPE_RECORD=&lt;file&gt;: every input primitive that enters the emulation (INT16h
/// EnqueueKey, 8042 EnqueueHostScancode, INT33h SetPosition/SetButtons) is logged with its
/// Cpu.TickCount stamp — the sinks record, so synthetic sources (autokeys, wiggle, script
/// clicks) are captured for free. PAKU_TAPE_REPLAY=&lt;file&gt;: the host template suppresses
/// live input and re-injects the tape at the same ticks.
/// Both modes lock MachineTimer to the virtual clock (TickCount/CyclesPerMs) so IRQ0/BDA/
/// VGA land on the same instruction-stream points every run; both REQUIRE a calibrated
/// governor (CyclesPerMs &gt; 0). Off (neither var set) = zero cost, nothing changes.
/// Tape line format: "&lt;tick&gt; &lt;E|H|M|T&gt; args" (E sc ascii · H scancode · M x y · T buttons).
/// </summary>
public static class InputTape
{
    public static readonly string? RecordPath = Environment.GetEnvironmentVariable("PAKU_TAPE_RECORD");
    public static readonly string? ReplayPath = Environment.GetEnvironmentVariable("PAKU_TAPE_REPLAY");
    public static readonly bool Replaying = !string.IsNullOrEmpty(ReplayPath);
    public static readonly bool Recording = !string.IsNullOrEmpty(RecordPath) && !Replaying;
    /// <summary>MachineTimer switches to the deterministic clock when a tape is active.</summary>
    public static readonly bool ClockLocked = Recording || Replaying;

    /// <summary>`PAKU_RELOJ_PARED=<que>` — devuelve al RELOJ DE PARED lo que la cinta pasó al del
    /// invitado, para poder medir A/B si algo se rompe con la cinta puesta y no sin ella.
    /// Valores, separados por comas: <c>retrace</c> (la fase de 0x3DA), <c>sb</c> (la IRQ del
    /// bloque de la Sound Blaster), <c>hora</c> (fecha y hora, ver GuestClock), <c>espera</c> (el
    /// tiempo que pasa mientras la BIOS espera una tecla, ver Int16hHandler.EsperaUnPoco), <c>todo</c>.
    /// Sin la variable no cambia nada.
    ///
    /// Existe porque un cambio que solo se activa con la cinta no se puede bisecar de otra forma:
    /// el sintoma aparece justo en la corrida que uno quiere medir.</summary>
    static readonly string RelojPared =
        (Environment.GetEnvironmentVariable("PAKU_RELOJ_PARED") ?? "").ToLowerInvariant();

    public static bool ParedPara(string que) =>
        RelojPared.Length > 0 && (RelojPared.Contains("todo") || RelojPared.Contains(que));

    /// <summary>El reloj del invitado manda para <paramref name="que"/>: hay cinta y nadie ha
    /// pedido volver al de pared para esa pieza.</summary>
    public static bool RelojDelInvitado(string que) => ClockLocked && !ParedPara(que);

    static StreamWriter? _w;
    static ushort _lx = 0xFFFF, _ly = 0xFFFF, _lb = 0xFFFF;

    static void Rec(string ev)
    {
        if (_w == null)
        {
            _w = new StreamWriter(RecordPath!) { AutoFlush = false };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _w.Flush(); } catch { } };
        }
        _w.WriteLine($"{Cpu.TickCount} {ev}");
    }

    public static void RecKey(byte sc, byte ascii) { if (Recording) Rec($"E {sc} {ascii}"); }
    public static void RecScan(byte sc) { if (Recording) Rec($"H {sc}"); }
    public static void RecMouse(ushort x, ushort y)
    { if (Recording && (x != _lx || y != _ly)) { _lx = x; _ly = y; Rec($"M {x} {y}"); } }
    public static void RecButtons(ushort b)
    { if (Recording && b != _lb) { _lb = b; Rec($"T {b}"); } }

    // ---- replay ----
    static (long t, string[] f)[]? _tape;
    static int _idx;

    /// <summary>Mete en la maquina los eventos de la cinta que ya han vencido. LO LLAMA EL HILO
    /// DEL JUEGO (<see cref="Platform.Machine.MachineTimer.Poll"/>), y ese es todo el asunto.
    ///
    /// <para>Esto lo hacia el bucle de render del anfitrion, que corre a 60 fotogramas de RELOJ DE
    /// PARED: el evento entraba en el primer fotograma que pillara, o sea en una instruccion
    /// distinta en cada corrida, y con el la posicion del raton, su callback y todo lo que el
    /// juego hiciera despues. Era la dependencia final del reloj del anfitrion que sobrevivia
    /// bajo una cinta, y mientras estuvo ahi ningun juez podia comparar dos corridas: eran dos
    /// partidas distintas. Medido en un juego de prueba: dos corridas identicas cortadas en la misma
    /// instruccion dejaban 11 bytes distintos en la memoria del invitado, y el rastro las separaba
    /// justo en el callback del raton.</para>
    ///
    /// <para>Aqui el reloj es el del invitado, asi que el evento entra en el mismo poll —y con el,
    /// en la misma instruccion— en cualquier corrida.</para></summary>
    public static void Reproduce(Platform.DosPlatform p)
    {
        while (TryNext(Cpu.TickCount, out var ev))
            switch (ev[1])
            {
                case "E": p.Int16?.EnqueueKey(byte.Parse(ev[2]), byte.Parse(ev[3])); break;
                case "H": p.Keyboard.EnqueueHostScancode(byte.Parse(ev[2])); break;
                case "M": p.Int33?.SetPosition(ushort.Parse(ev[2]), ushort.Parse(ev[3])); break;
                case "T": p.Int33?.SetButtons(ushort.Parse(ev[2])); break;
                // `J x y botones`: el joystick del puerto 0x201 (ejes 0-255, botones bit0/bit1).
                // Sin esto un juego que se juega con joystick no se puede guiar desde una cinta.
                case "J":
                    p.GamePort.X = byte.Parse(ev[2]);
                    p.GamePort.Y = byte.Parse(ev[3]);
                    p.GamePort.Buttons = byte.Parse(ev[4]);
                    break;
                // `P <lineal hex> <byte hex>`: FORZAR un byte de la memoria del invitado, como un
                // truco de los de la epoca. Sirve para llevar la partida a un estado al que el juego
                // llega por si solo pero que una cinta a ciegas no alcanza (un nivel avanzado, una
                // fase concreta): lo que se graba despues sigue siendo el codigo ORIGINAL
                // ejecutandose. Los casos del corpus que estuvieran abiertos se tiran: su memoria de
                // salida llevaria un cambio que no hizo nadie de dentro.
                case "P":
                    if (Memory.Last != null)
                    {
                        Memory.Last.Raw[Convert.ToInt32(ev[2], 16)] = Convert.ToByte(ev[3], 16);
                        CorpusRecorder.DescartaAbiertos();
                        GrabadorFotogramas.NotaTruco(Convert.ToInt32(ev[2], 16), Convert.ToByte(ev[3], 16));
                    }
                    break;
            }
    }

    /// <summary>Next tape event due at or before tick <paramref name="now"/> (template loop).</summary>
    public static bool TryNext(long now, out string[] fields)
    {
        if (_tape == null)
        {
            var list = new List<(long, string[])>();
            foreach (var line in File.ReadLines(ReplayPath!))
            {
                // A tape can be written by hand: blank lines and `#` comments are allowed.
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
                var f = line.Split(' ');
                if (f.Length >= 2) list.Add((long.Parse(f[0]), f));
            }
            _tape = list.ToArray();
        }
        if (_idx < _tape.Length && _tape[_idx].t <= now)
        {
            fields = _tape[_idx++].f;
            return true;
        }
        fields = System.Array.Empty<string>();
        return false;
    }
}
