namespace PakuPort.Core;

/// <summary>
/// Remake-kit step 4:
/// shadow-framebuffer replacement. PAKU_REMAKE=&lt;remake.json&gt; loads a manifest
/// mapping source-FNV hashes (the identity blit.log already computes) to HD textures:
///   { "assets": { "AE8E8135": { "tex": "hd/x.png", "ax": 0, "ay": 0, "w": 16, "h": 16 } } }
/// The game keeps writing the shadow VRAM untouched (fallback is ALWAYS rendered);
/// when a movs blit whose source hash is in the manifest lands on VRAM, an instance
/// (texture, game x/y) is registered here and the host draws it on top at scale.
///
/// Frame semantics: an instance stays alive while the game
/// keeps redrawing it and dies when erased. Erasure = a substantial FRACTION of its
/// rect overwritten (per-instance dirty-pixel bitmap, threshold 25%): a sprite being
/// erased or save-under-restored hits 100%, while a cursor passing over a full-screen
/// scene touches &lt;1% and must not kill it. Once past the threshold the instance dies
/// unless the same blit shows up again within a grace window (the erase→redraw cycle
/// of one game frame). Writes closer than a burst window to the instance's last
/// refresh are part of the SAME logical draw (rows 2..N of a sprite whose manifest
/// key is row 1) and do not dirty it. A one-shot draw that nothing ever overwrites
/// (menus, title screens) lives forever.
///
/// Coverage judge: logs/remake-coverage.log gets replaced/total counts, live-instance
/// stats and the top unmatched hashes on exit. Off (no env var) = zero cost.
/// </summary>
public static class RemakeMap
{
    public sealed record Entry(string Tex, int Ax, int Ay, int W, int H);
    public sealed record Instance(string Tex, int X, int Y, int W, int H);

    sealed class State
    {
        public long Seen;
        public long Dirty = -1;          // tick the dirty-fraction threshold was crossed
        public byte[]? Mask;             // W*H dirty-pixel bitmap (allocated on first touch)
        public int DirtyPx;              // distinct dirty pixels (immune to re-touches)
    }

    public static readonly bool Active;
    static readonly Dictionary<uint, Entry> _map = new();
    /// <summary>Identidad por PROCEDENCIA ("FICHERO@0xOFF" → textura), la clave estable
    /// cuando el juego COMPONE el sprite antes de volcarlo: el hash de píxeles cambia con
    /// el fondo que haya debajo (medido en un juego de prueba) pero el origen del dato no. Se
    /// consulta ANTES que el hash. Clave del manifiesto: "file:SPRITES.DAT@0x1234".</summary>
    static readonly Dictionary<string, Entry> _mapByProv =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();
    static readonly Dictionary<Instance, State> _live = new();
    static readonly List<Instance> _scratch = new();

    // Guest-time windows in ms; converted to ticks with the governor's CyclesPerMs
    // (20000 fallback when running free). Grace tunable for manual-playtest diagnosis.
    static readonly int _graceMs =
        int.TryParse(Environment.GetEnvironmentVariable("PAKU_REMAKE_GRACE_MS"), out var g) ? g : 150;
    // Burst = the rows of ONE logical draw arriving right after the keyed row's Note
    // (a 24x16 sprite completes in <1 ms of guest time). Must stay well below the
    // fastest legitimate erase→redraw cadence or erasures would be swallowed.
    const int BurstMs = 2;
    static long _graceTicks, _burstTicks;

    // Conservative bounding box of live instances in game pixels (volatile reads let
    // TouchWrite bail out without taking the lock on the vast majority of VRAM writes).
    static volatile int _bbMinX = int.MaxValue, _bbMinY, _bbMaxX = -1, _bbMaxY;
    static volatile int _liveCount;

    static long _blitsTotal, _blitsReplaced, _instDied;
    static int _livePeak;
    static readonly Dictionary<uint, long> _unmatched = new();

    // ---- Dibujo lógico: varios blits de la MISMA rutina que componen una figura ----
    // Un juego puede componer un sprite UNA FILA POR BLIT, y entonces cada fila tiene su propia
    // identidad y no hay ninguna clave de la que colgar el sprite entero: en un juego de prueba son
    // 1162 de 1165 claves, o sea que reemplazar "por clave" deja fuera el 99,7 % del juego.
    // Aquí se reconstruye la figura mientras se dibuja: blits consecutivos de la misma rutina,
    // con la misma x y la misma anchura, en filas que avanzan de una en una y dentro de la misma
    // ráfaga, son UN dibujo. Su identidad es el encadenado de las identidades de sus filas EN
    // ORDEN, que es estable aunque el juego componga sobre fondos distintos.
    // Clave en el manifiesto: "grupo:XXXXXXXX".
    static readonly Dictionary<uint, Entry> _mapByGroup = new();
    static uint _grpAt, _grpHash;
    static int _grpX, _grpY0, _grpW, _grpN;
    static long _grpTick;
    static long _gruposVistos, _gruposReemplazados;
    static readonly Dictionary<uint, long> _gruposSinTextura = new();

    static RemakeMap()
    {
        string? path = Environment.GetEnvironmentVariable("PAKU_REMAKE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            foreach (var p in doc.RootElement.GetProperty("assets").EnumerateObject())
            {
                string tex = Path.Combine(baseDir, p.Value.GetProperty("tex").GetString()!);
                int ax = p.Value.TryGetProperty("ax", out var a) ? a.GetInt32() : 0;
                int ay = p.Value.TryGetProperty("ay", out var b) ? b.GetInt32() : 0;
                int tw = p.Value.TryGetProperty("w", out var wv) ? wv.GetInt32() : 16;
                int th = p.Value.TryGetProperty("h", out var hv) ? hv.GetInt32() : 16;
                var entry = new Entry(tex, ax, ay, tw, th);
                if (p.Name.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    _mapByProv[p.Name["file:".Length..]] = entry;
                else if (p.Name.StartsWith("grupo:", StringComparison.OrdinalIgnoreCase))
                    _mapByGroup[Convert.ToUInt32(p.Name["grupo:".Length..], 16)] = entry;
                else
                    _map[Convert.ToUInt32(p.Name, 16)] = entry;
            }
            Active = _map.Count > 0 || _mapByProv.Count > 0 || _mapByGroup.Count > 0;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteCoverage();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[REMAKE] manifiesto ilegible {path}: {e.Message}");
        }
    }

    /// <summary>Bytes per line of the EGA/VGA planar modes (= Width / 8), same table the
    /// catalog decoder uses (tools/blit_catalog/build_catalog.py EGA_MODES), itself derived
    /// from the mode table of Platform/Int10hHandler.cs. A game that reprograms the CRTC
    /// Offset register (r13) away from the mode default would need that value instead;
    /// none of the fleet does, and GameXY only gets (offset, mode).</summary>
    /// <summary>Píxeles que ocupa un byte de VRAM en ese modo. Misma tabla que
    /// tools/blit_catalog/build_catalog.py: planar y CGA empaquetan varios píxeles por byte,
    /// y 256 colores va uno a uno.</summary>
    public static int PixelsPerByte(int mode) =>
        mode is 4 or 5 ? 4 : (PlanarBytesPerLine(mode) != 0 || mode == 6) ? 8 : 1;

    static int PlanarBytesPerLine(int mode) => mode switch
    {
        0x0D => 40,
        0x0E or 0x0F or 0x10 or 0x11 or 0x12 => 80,
        _ => 0,
    };

    /// <summary>VRAM offset → game pixel, for the same modes the catalog decodes: CGA
    /// interleave (4/5/6), EGA/VGA planar (0Dh-12h, where the offset is a PLANE address:
    /// the map mask picks the plane, not the address) and linear 13h. Shared with the
    /// Interp blit hook. Text modes fall through to the 13h branch and are meaningless
    /// there — no blit-replaceable sprites exist in them.</summary>
    public static (int gx, int gy) GameXY(int off, int mode)
    {
        if (mode is 4 or 5 or 6)
        {
            int o = off & 0x3FFF;
            bool odd = (off & 0x2000) != 0;
            int row = (o & 0x1FFF) / 80;
            return (((o & 0x1FFF) % 80) * (mode == 6 ? 8 : 4), row * 2 + (odd ? 1 : 0));
        }
        int bpl = PlanarBytesPerLine(mode);
        if (bpl != 0) return ((off % bpl) * 8, off / bpl);
        return (off % 320, off / 320);
    }

    /// <summary>Interp blit hook (same funnel as LogBlit). x/y are game pixels.
    /// <paramref name="prov"/> es la procedencia del origen ("FICHERO@0xOFF") cuando
    /// BlitProvenance la conoce, o null. Se busca ANTES que el hash: es la identidad que
    /// sobrevive a que el juego componga el sprite con el fondo.</summary>
    public static void Note(uint fnv, string? prov, int x, int y, int cyclesPerMs)
        => Note(fnv, prov, x, y, 0, 0, cyclesPerMs);

    /// <param name="w">anchura del blit en píxeles de juego (0 = desconocida: no se agrupa)</param>
    /// <param name="at">rutina que lo dibuja (CS:IP), la que decide si dos blits son el mismo
    /// dibujo lógico</param>
    public static void Note(uint fnv, string? prov, int x, int y, int w, uint at, int cyclesPerMs)
    {
        AgrupaFila(fnv, x, y, w, at, cyclesPerMs);
        _blitsTotal++;
        Entry? e = null;
        if (prov != null && _mapByProv.TryGetValue(prov, out var ep)) e = ep;
        else if (_map.TryGetValue(fnv, out var eh)) e = eh;
        if (e != null)
        {
            _blitsReplaced++;
            if (_graceTicks == 0)
            {
                long perMs = cyclesPerMs > 0 ? cyclesPerMs : 20000;
                _graceTicks = _graceMs * perMs;
                _burstTicks = BurstMs * perMs;
            }
            long now = Cpu.TickCount;
            var ins = new Instance(e.Tex, x + e.Ax, y + e.Ay, e.W, e.H);
            lock (_lock)
            {
                if (_live.TryGetValue(ins, out var st))
                {
                    st.Seen = now; st.Dirty = -1; st.DirtyPx = 0;
                    if (st.Mask != null) Array.Clear(st.Mask);
                }
                else if (_live.Count < 4096)
                {
                    _live[ins] = new State { Seen = now };
                    if (_live.Count > _livePeak) _livePeak = _live.Count;
                }
                // Liveness del sprite MÓVIL. La muerte por fracción de área no cubre el caso
                // medido en un juego de prueba y reproducido por GHOST.COM: al mover un sprite el juego
                // repinta la posición abandonada sólo en parte (~6-8 %, muy por debajo del
                // umbral), así que quedaba una instancia fantasma por cada posición recorrida.
                // Regla: si el MISMO asset aparece en otra posición y la instancia vieja YA
                // ha sido tocada (alguien escribió encima, por poco que sea) y no se ha
                // redibujado desde entonces, se da por abandonada y muere tras la gracia.
                // El "ya ha sido tocada" es lo que salva los casos legítimos: una cenefa de N
                // sprites idénticos dibujados una sola vez no recibe NINGUNA escritura encima
                // (DirtyPx == 0) y sobrevive, igual que el dibujo one-shot de una pantalla de
                // título; y una escena a pantalla completa sobre la que pasa un cursor no
                // reaparece nunca en otra posición, así que la regla ni se dispara.
                foreach (var kv in _live)
                {
                    if (ReferenceEquals(kv.Key, ins) || kv.Key.Equals(ins)) continue;
                    if (!string.Equals(kv.Key.Tex, e.Tex, StringComparison.Ordinal)) continue;
                    var old = kv.Value;
                    if (old.Dirty >= 0 || old.DirtyPx == 0 || old.Seen >= now) continue;
                    old.Dirty = now;
                }
                PurgeAndRecomputeLocked(now);
            }
        }
        else
        {
            _unmatched.TryGetValue(fnv, out long n);
            _unmatched[fnv] = n + 1;
        }
    }

    /// <summary>Encadena la fila que se acaba de dibujar al dibujo lógico en curso, o cierra el
    /// anterior y empieza uno nuevo. La ráfaga se mide en tiempo de invitado con la misma ventana
    /// que el resto: filas de un mismo sprite llegan seguidas, y un hueco significa otra cosa.</summary>
    static void AgrupaFila(uint fnv, int x, int y, int w, uint at, int cyclesPerMs)
    {
        if (w <= 0) return;                       // sin anchura no se puede saber si encaja
        if (_graceTicks == 0)
        {
            long perMs = cyclesPerMs > 0 ? cyclesPerMs : 20000;
            _graceTicks = _graceMs * perMs;
            _burstTicks = BurstMs * perMs;
        }
        long now = Cpu.TickCount;
        bool sigue = _grpN > 0 && at == _grpAt && x == _grpX && w == _grpW
                     && y == _grpY0 + _grpN && now - _grpTick <= _burstTicks;
        if (!sigue)
        {
            CierraGrupo();
            _grpAt = at; _grpX = x; _grpY0 = y; _grpW = w; _grpN = 0;
            _grpHash = 2166136261;
        }
        // FNV-1a sobre la clave en hexadecimal ASCII, EN ORDEN. El mismo cálculo que hace
        // tools/remake_kit/build_grupos.py para que la clave del manifiesto y la que ve el
        // motor sean la misma sin que nadie las tenga que sincronizar a mano.
        foreach (char ch in fnv.ToString("X8"))
            _grpHash = (_grpHash ^ ch) * 16777619;
        _grpN++;
        _grpTick = now;
    }

    /// <summary>Cierra el dibujo lógico en curso y, si el manifiesto trae arte para él, registra
    /// la instancia con el rectángulo COMPLETO de la figura.</summary>
    static void CierraGrupo()
    {
        if (_grpN < 2) { _grpN = 0; return; }     // una fila suelta no es una figura
        _gruposVistos++;
        if (_mapByGroup.TryGetValue(_grpHash, out var e))
        {
            _gruposReemplazados++;
            var ins = new Instance(e.Tex, _grpX + e.Ax, _grpY0 + e.Ay,
                                   e.W > 0 ? e.W : _grpW, e.H > 0 ? e.H : _grpN);
            long now = Cpu.TickCount;
            lock (_lock)
            {
                if (_live.TryGetValue(ins, out var st))
                {
                    st.Seen = now; st.Dirty = -1; st.DirtyPx = 0;
                    if (st.Mask != null) Array.Clear(st.Mask);
                }
                else if (_live.Count < 4096)
                {
                    _live[ins] = new State { Seen = now };
                    if (_live.Count > _livePeak) _livePeak = _live.Count;
                }
                PurgeAndRecomputeLocked(now);
            }
        }
        else
        {
            _gruposSinTextura.TryGetValue(_grpHash, out long n);
            _gruposSinTextura[_grpHash] = n + 1;
        }
        _grpN = 0;
    }

    /// <summary>Memory.WrU8 VRAM hook: any write overlapping a live instance outside its
    /// own draw burst marks it dirty (erase detection). Cheap bail-out via bbox.</summary>
    public static void TouchWrite(int off, int mode)
    {
        // El dibujo lógico en curso se cierra en cuanto pasa su ráfaga. Se hace aquí —en el hilo
        // del juego, que es el único que toca este estado— porque si sólo se cerrara al llegar
        // el siguiente blit, la última figura de una escena no se registraría hasta que el juego
        // volviera a dibujar algo, y en una pantalla que se queda quieta eso es nunca.
        if (_grpN > 0 && Cpu.TickCount - _grpTick > _burstTicks) CierraGrupo();
        if (_liveCount == 0) return;
        var (gx, gy) = GameXY(off, mode);
        if (gx < _bbMinX || gx > _bbMaxX || gy < _bbMinY || gy > _bbMaxY) return;
        long now = Cpu.TickCount;
        lock (_lock)
            foreach (var kv in _live)
            {
                var i = kv.Key;
                var st = kv.Value;
                if (st.Dirty >= 0 || now - st.Seen <= _burstTicks ||
                    gx < i.X || gx >= i.X + i.W || gy < i.Y || gy >= i.Y + i.H) continue;
                st.Mask ??= new byte[(i.W * i.H + 7) / 8];
                int bit = (gy - i.Y) * i.W + (gx - i.X);
                if ((st.Mask[bit >> 3] & (1 << (bit & 7))) != 0) continue;
                st.Mask[bit >> 3] |= (byte)(1 << (bit & 7));
                if (++st.DirtyPx * 4 > i.W * i.H) st.Dirty = now;
            }
    }

    /// <summary>Host render thread: snapshot of the currently-alive instances
    /// (dirty-within-grace included so the erase→redraw cycle never flickers).
    /// Ordered by last redraw so the painter's algorithm matches the GAME's: it paints
    /// the background first and the sprites over it, so the oldest refresh goes first.
    /// Dictionary order would do here only by luck — it is unspecified after removals.</summary>
    public static List<Instance> TakeFrame()
    {
        lock (_lock)
        {
            PurgeAndRecomputeLocked(Cpu.TickCount);
            var list = new List<KeyValuePair<Instance, State>>(_live);
            list.Sort((a, b) => a.Value.Seen.CompareTo(b.Value.Seen));
            var outp = new List<Instance>(list.Count);
            foreach (var kv in list) outp.Add(kv.Key);
            return outp;
        }
    }

    static void PurgeAndRecomputeLocked(long now)
    {
        _scratch.Clear();
        foreach (var kv in _live)
            if (kv.Value.Dirty >= 0 && now - kv.Value.Dirty > _graceTicks)
                _scratch.Add(kv.Key);
        foreach (var dead in _scratch) { _live.Remove(dead); _instDied++; }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        foreach (var i in _live.Keys)
        {
            if (i.X < minX) minX = i.X;
            if (i.Y < minY) minY = i.Y;
            if (i.X + i.W - 1 > maxX) maxX = i.X + i.W - 1;
            if (i.Y + i.H - 1 > maxY) maxY = i.Y + i.H - 1;
        }
        _bbMinX = minX; _bbMinY = minY; _bbMaxX = maxX; _bbMaxY = maxY;
        _liveCount = _live.Count;
    }

    static void WriteCoverage()
    {
        try
        {
            Directory.CreateDirectory("logs");
            using var w = new StreamWriter(Path.Combine("logs", "remake-coverage.log"));
            w.WriteLine($"blits={_blitsTotal} reemplazados={_blitsReplaced} " +
                        $"cobertura={(_blitsTotal > 0 ? 100.0 * _blitsReplaced / _blitsTotal : 0):F1}%");
            w.WriteLine($"instancias_vivas={_liveCount} pico={_livePeak} muertas={_instDied}");
            w.WriteLine($"dibujos_logicos={_gruposVistos} reemplazados={_gruposReemplazados} " +
                        $"cobertura={(_gruposVistos > 0 ? 100.0 * _gruposReemplazados / _gruposVistos : 0):F1}%");
            lock (_lock)
                foreach (var kv in _live)
                    w.WriteLine($"viva tex={Path.GetFileName(kv.Key.Tex)} x={kv.Key.X} y={kv.Key.Y} " +
                                $"seen={kv.Value.Seen} dirtypx={kv.Value.DirtyPx} dirty={kv.Value.Dirty}");
            foreach (var kv in _unmatched.OrderByDescending(kv => kv.Value).Take(40))
                w.WriteLine($"sin-textura fnv={kv.Key:X8} n={kv.Value}");
            foreach (var kv in _gruposSinTextura.OrderByDescending(kv => kv.Value).Take(40))
                w.WriteLine($"sin-textura grupo:{kv.Key:X8} n={kv.Value}");
        }
        catch { }
    }
}
