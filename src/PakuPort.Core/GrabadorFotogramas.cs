namespace PakuPort.Core;

/// <summary>
/// `PAKU_FOTOGRAMAS=&lt;fichero.jsonl&gt;` + `PAKU_FOTOGRAMA_EN=&lt;ip hex&gt;[,...]` — the original
/// game, frame by frame, as ground truth for a hand-written (idiomatic) remake.
///
/// <para><b>Why it exists.</b> The per-case judge compares registers, stack and instruction count,
/// so the only C# it accepts is a line-by-line copy of the assembly. A readable remake has none of
/// those; what it shares with the original is the game STATE at each frame and what is on screen.
/// This records exactly that: every time execution reaches one of the given addresses (the top of
/// the game's frame loop), one JSON line with the data segment and the CGA screen as they are at
/// that instant, plus every hardware input the game read since the previous line (port reads and
/// INT 16h answers) and every port write (the sound). A remake fed with those inputs must reach
/// the same state, frame after frame.</para>
///
/// <para>Memory and screen are stored as changed runs against the previous line (the first line
/// is against all zeros), so a long session stays small. The events are kept from the start of
/// the program, so the first line carries everything that happened before it (a title, a
/// menu).</para>
///
/// <para><b>Cost when off:</b> one static bool read per instruction, like its siblings in Interp.</para>
/// </summary>
public static class GrabadorFotogramas
{
    static readonly string? Ruta = Environment.GetEnvironmentVariable("PAKU_FOTOGRAMAS");
    public static readonly bool Activo = !string.IsNullOrEmpty(Ruta);

    static readonly HashSet<ushort> Cabeceras = [..
        (Environment.GetEnvironmentVariable("PAKU_FOTOGRAMA_EN") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(t => Convert.ToUInt16(t.Trim(), 16))];

    const int TamDatos = 0x10000;
    const int TamCga = 0x4000;
    const int BaseCga = 0xB8000;

    static StreamWriter? _w;
    static long _n;
    static readonly byte[] _datos = new byte[TamDatos];
    static readonly byte[] _cga = new byte[TamCga];
    static readonly byte[] _datosAntes = new byte[TamDatos];
    static readonly byte[] _cgaAntes = new byte[TamCga];
    static readonly List<string> _entradas = [];

    public static void Paso(Cpu c, Memory m, ushort ip)
    {
        if (!Cabeceras.Contains(ip)) return;
        if (_w == null)
        {
            _w = new StreamWriter(Ruta!) { AutoFlush = false };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _w.Flush(); } catch { } };
        }

        int baseDatos = c.DS << 4;
        Array.Copy(m.Raw, baseDatos, _datos, 0, Math.Min(TamDatos, m.Raw.Length - baseDatos));
        LeeCga(m);

        _w.WriteLine($"{{\"n\":{_n},\"cs\":\"{c.CS:X4}\",\"ip\":\"{ip:X4}\",\"t\":{Cpu.TickCount},\"ds\":\"{c.DS:X4}\"," +
                     $"\"e\":[{string.Join(",", _entradas)}]," +
                     $"\"mem\":{Tramos(_datos, _datosAntes)},\"cga\":{Tramos(_cga, _cgaAntes)}}}");
        _n++;
        _entradas.Clear();
        Array.Copy(_datos, _datosAntes, TamDatos);
        Array.Copy(_cga, _cgaAntes, TamCga);
    }

    /// <summary>A port read: `["p",port,value]`.</summary>
    public static void NotaIn(ushort puerto, ushort valor)
    {
        if (Activo) _entradas.Add($"[\"p\",{puerto},{valor}]");
    }

    /// <summary>A port write: `["o",port,value]`. Not input, but it is how the game makes sound
    /// (the PC speaker divisor goes out through port 42h), so the judge can compare it.</summary>
    public static void NotaOut(ushort puerto, ushort valor)
    {
        if (Activo) _entradas.Add($"[\"o\",{puerto},{valor}]");
    }

    /// <summary>A byte forced by the tape (a `P` event, a cheat to reach a phase or a level):
    /// `["t",linear address,value]`. The frame it falls in did not come from the game alone, and
    /// the judge has to know.</summary>
    public static void NotaTruco(int lineal, byte valor)
    {
        if (Activo) _entradas.Add($"[\"t\",{lineal},{valor}]");
    }

    /// <summary>An INT 16h answer: `["k",AH on entry,AX on exit,ZF on exit]`. The keyboard is
    /// polled with AH=1 (is there a key? ZF) and read with AH=0, and both answers are input.</summary>
    public static void NotaInt(int n, byte ahEntrada, Cpu c)
    {
        if (Activo && n == 0x16)
            _entradas.Add($"[\"k\",{ahEntrada},{c.AX},{(c.ZF ? 1 : 0)}]");
    }

    /// <summary>The 16 KB CGA window at B800:0000 as the program sees it. Reading VRAM loads the
    /// VGA latches, so they are saved and restored: recording must not change the run.</summary>
    static void LeeCga(Memory m)
    {
        var v = m.Video;
        if (v == null) return;
        Span<byte> latch = stackalloc byte[4];
        v.Latch.CopyTo(latch);
        for (int i = 0; i < TamCga; i++)
        {
            int off = BaseCga + i - v.MemoryMapBase;
            _cga[i] = (uint)off < (uint)v.MemoryMapSize ? v.ReadVram(off) : (byte)0;
        }
        latch.CopyTo(v.Latch);
    }

    /// <summary>`[[offset,"hex"],...]` with the runs that changed; runs closer than 8 bytes merge.</summary>
    static string Tramos(byte[] ahora, byte[] antes)
    {
        var sb = new System.Text.StringBuilder("[");
        int i = 0;
        while (i < ahora.Length)
        {
            if (ahora[i] == antes[i]) { i++; continue; }
            int ini = i, fin = i + 1, igual = 0;
            for (int j = i + 1; j < ahora.Length && igual < 8; j++)
            {
                if (ahora[j] != antes[j]) { fin = j + 1; igual = 0; }
                else igual++;
            }
            if (sb.Length > 1) sb.Append(',');
            sb.Append('[').Append(ini).Append(",\"").Append(Convert.ToHexString(ahora, ini, fin - ini)).Append("\"]");
            i = fin;
        }
        return sb.Append(']').ToString();
    }
}
