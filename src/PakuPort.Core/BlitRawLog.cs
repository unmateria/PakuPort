using PakuPort.Core.Platform;

namespace PakuPort.Core;

/// <summary>
/// Graba las escrituras a VRAM en logs/blitraw.bin, para que el catálogo offline
/// (tools/blit_catalog) pueda reconstruir los píxeles de lo que el juego dibuja.
///
/// PAKU_LOG_BLIT=2 (second-port doctrine, remake-kit step 1a):
/// records the loose VRAM writes — mov/xor/and-style stores that do NOT come from movs/stos
/// (those are already logged as rectangles in logs/blit.log) — in a compact binary format, so
/// hand-rolled blits (as in one test game) can be rebuilt.
///
/// File format (little-endian):
///   header  "PBR1" (4 bytes)
///   'M' u8 mode                      — video mode change (also emitted before first write)
///   'W' u32 lin, u8 val, u32 at, u32 dt — 14 B. NON-planar modes (CGA 4/5/6, mode 13h):
///        the CPU byte IS the pixel data, `lin` is the CPU linear address.
///   'P' u32 addr, u8 p0, u8 p1, u8 p2, u8 p3, u32 at, u32 dt — 17 B. EGA/VGA planar modes
///        0Dh-12h: the CPU byte is NOT the pixel data (it crosses map mask, write mode,
///        set/reset, data rotate, logic op, bit mask and the 4 latches), so what gets
///        recorded is the content of the 4 planes AS THEY ENDED UP after the write, and
///        `addr` is the resolved plane address (odd/even shift already applied), not the
///        CPU linear address. Last-write-wins offline = the final pixels, with no need to
///        replay the EGA write pipeline (which would also require logging the latches).
/// `at` = cs:ip of the interpreted instruction, `dt` = tick delta since the previous record.
///
/// PAKU_LOG_BLIT=3 añade DOS cosas y resuelve el pero de las otras dos: hasta el nivel 2, un
/// juego que dibuja con `movs`/`stos` en modo no planar deja su rectángulo en `blit.log` —con su
/// IDENTIDAD, que es lo único que el motor sabe reemplazar en ejecución— pero **sin sus píxeles**,
/// mientras que las escrituras sueltas dejan píxeles **sin identidad**. Medido en un juego de prueba:
/// 1167 encargos y CERO con imagen de referencia. O sea, quien tiene que dibujar el arte no puede
/// ver lo que le están pidiendo.
///   'A'                              — abre grupo: empiezan las escrituras de una string-op
///   'B' u32 at, u8 lc, clave[lc], u8 lp, prov[lp]  — cierra el grupo y le pone su identidad
/// La `clave` es LITERALMENTE la que usa `tools/blit_catalog` para indexar el rectángulo (el FNV
/// del origen, o el valor del relleno en un `stos`), así que el cruce píxeles↔identidad es por
/// CONSTRUCCIÓN y no por parecido: nada de casar por la dirección de la rutina, que una misma
/// rutina de blit dibuja sprites distintos.
///
/// Off (any level &lt; 2) = zero cost: Memory checks a single static readonly bool.
/// </summary>
public static class BlitRawLog
{
    static readonly int _nivel =
        int.TryParse(Environment.GetEnvironmentVariable("PAKU_LOG_BLIT"), out var lvl) ? lvl : 0;

    public static readonly bool Enabled = _nivel >= 2;

    /// <summary>Nivel 3: TODA escritura a VRAM se graba, venga de donde venga. Los niveles 1 y 2
    /// no cambian ni un byte, así que los jueces y catálogos ya congelados siguen valiendo.</summary>
    public static readonly bool Todo = _nivel >= 3;

    static Stream? _out;
    static byte[] _buf = new byte[1 << 16];
    static int _n;
    static long _lastTick;
    static int _lastMode = -1;
    public static bool GroupOpen { get; private set; }

    /// <summary>EGA/VGA planar graphics modes — same range as Int10hHandler.IsEga.</summary>
    static bool IsPlanar(byte mode) => mode is >= 0x0D and <= 0x12;

    public static void Log(int lin, int vramOff, byte val, VideoState v, bool stringOp)
    {
        byte mode = v.VideoMode;
        bool planar = IsPlanar(mode);
        // movs/stos bytes are already covered by the blit.log rectangle — except in planar
        // modes, where that rectangle hashes SOURCE bytes that are not the pixels. En el nivel 3
        // tampoco se saltan: ese rectángulo lleva la identidad pero no los píxeles, y los píxeles
        // son justo lo que hay que enseñarle a quien dibuja el arte.
        if (stringOp && !planar && !Todo) return;
        _out ??= Open();
        if (mode != _lastMode)
        {
            EnsureRoom(2);
            _buf[_n++] = (byte)'M';
            _buf[_n++] = mode;
            _lastMode = mode;
        }
        long t = Cpu.TickCount;
        uint dt = (uint)Math.Min(t - _lastTick, uint.MaxValue);
        _lastTick = t;
        if (planar)
        {
            int addr = v.OddEvenMode ? vramOff >> 1 : vramOff;
            if ((uint)addr >= VideoState.PlaneSize) return;
            EnsureRoom(17);
            _buf[_n++] = (byte)'P';
            WrU32((uint)addr);
            _buf[_n++] = v.Planes[0][addr];
            _buf[_n++] = v.Planes[1][addr];
            _buf[_n++] = v.Planes[2][addr];
            _buf[_n++] = v.Planes[3][addr];
            WrU32(Interp.CurrentPc);
            WrU32(dt);
            return;
        }
        EnsureRoom(14);
        _buf[_n++] = (byte)'W';
        WrU32((uint)lin);
        _buf[_n++] = val;
        WrU32(Interp.CurrentPc);
        WrU32(dt);
    }

    /// <summary>Nivel 3: abre el grupo de escrituras de una string-op. Lo que caiga entre esta
    /// marca y su 'B' pertenece a ese blit; lo que quede fuera de una pareja A..B es anónimo, que
    /// es la verdad (nadie calculó su identidad) y no una laguna que haya que rellenar.</summary>
    public static void AbreGrupo()
    {
        if (!Todo) return;
        // A group left open by a routine that never closed it is closed with an empty key,
        // so its pixels are not glued to the identity of the next group.
        if (GroupOpen) CierraGrupo(Interp.CurrentPc, "", null);
        _out ??= Open();
        EnsureRoom(1);
        _buf[_n++] = (byte)'A';
        GroupOpen = true;
    }

    /// <summary>Nivel 3: cierra el grupo y le pega su identidad, la MISMA con la que el catálogo
    /// indexa el rectángulo de `blit.log`.</summary>
    public static void CierraGrupo(uint at, string clave, string? prov)
    {
        if (!Todo || !GroupOpen) return;
        GroupOpen = false;
        _out ??= Open();
        byte[] c = Texto(clave);
        byte[] p = Texto(prov ?? "");
        EnsureRoom(1 + 4 + 1 + c.Length + 1 + p.Length);
        _buf[_n++] = (byte)'B';
        WrU32(at);
        _buf[_n++] = (byte)c.Length;
        Array.Copy(c, 0, _buf, _n, c.Length); _n += c.Length;
        _buf[_n++] = (byte)p.Length;
        Array.Copy(p, 0, _buf, _n, p.Length); _n += p.Length;
    }

    /// <summary>Latin-1 y recortado a 255: los nombres 8.3 y los hashes hex caben de sobra, y un
    /// campo de longitud fija hace el fichero legible sin adivinar.</summary>
    static byte[] Texto(string s)
    {
        if (s.Length > 255) s = s[..255];
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = s[i] < 256 ? (byte)s[i] : (byte)'?';
        return b;
    }

    static void WrU32(uint v)
    {
        _buf[_n++] = (byte)v; _buf[_n++] = (byte)(v >> 8);
        _buf[_n++] = (byte)(v >> 16); _buf[_n++] = (byte)(v >> 24);
    }

    static void EnsureRoom(int bytes)
    {
        if (_n + bytes > _buf.Length) Flush();
    }

    static void Flush()
    {
        if (_out != null && _n > 0) { _out.Write(_buf, 0, _n); _n = 0; }
    }

    static Stream Open()
    {
        Directory.CreateDirectory("logs");
        var s = new FileStream(Path.Combine("logs", "blitraw.bin"),
                               FileMode.Create, FileAccess.Write, FileShare.Read);
        s.Write("PBR1"u8);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        { try { Flush(); _out?.Flush(); } catch { } };
        return s;
    }
}
