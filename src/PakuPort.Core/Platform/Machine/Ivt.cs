namespace PakuPort.Core.Platform;

/// <summary>
/// Tabla de vectores de interrupción (IVT). Los 256 vectores residen en la memoria baja
/// 0x0000:0x0000–0x03FF (cada vector = 4 bytes: offset-lo, offset-hi, seg-lo, seg-hi).
/// Adaptado de dosbox-x src/ints/bios.cpp (BIOS_Setup*) — el layout de memoria es estándar
/// IBM PC BIOS.
/// </summary>
public sealed class Ivt
{
    // Vectores en m.Raw[n*4..n*4+3]: [0]=off-lo, [1]=off-hi, [2]=seg-lo, [3]=seg-hi

    // PAKU_LOG_IVT=1: trace every vector write (who hooks/unhooks what, and from where).
    private static readonly bool _log =
        Environment.GetEnvironmentVariable("PAKU_LOG_IVT") == "1";
    private static bool _nullDumped;

    /// <summary>Escribe el vector de interrupción n en la IVT (memoria baja).</summary>
    public void SetVector(Memory m, byte n, ushort seg, ushort off)
    {
        int base_ = n * 4;
        if (_log)
        {
            var (oldSeg, oldOff) = GetVector(m, n);
            uint pc = Interp.LastPc;
            Console.Error.WriteLine($"[IVT] set {n:X2} = {seg:X4}:{off:X4} (antes {oldSeg:X4}:{oldOff:X4}) desde {pc >> 16:X4}:{pc & 0xFFFF:X4}");
            // A hook installed as 0000:0000 over a live vector is always a bug smell (a driver
            // restoring an unpopulated save table): snapshot memory once so the caller can be
            // disassembled while its code segment is still resident.
            if (seg == 0 && off == 0 && (oldSeg != 0 || oldOff != 0) && !_nullDumped)
            {
                _nullDumped = true;
                try
                {
                    System.IO.Directory.CreateDirectory("logs");
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine("logs", "nullhook-mem.bin"), m.Raw);
                }
                catch { }
            }
        }
        m.Raw[base_ + 0] = (byte)(off & 0xFF);
        m.Raw[base_ + 1] = (byte)(off >> 8);
        m.Raw[base_ + 2] = (byte)(seg & 0xFF);
        m.Raw[base_ + 3] = (byte)(seg >> 8);
    }

    /// <summary>Lee el vector de interrupción n de la IVT.</summary>
    public (ushort seg, ushort off) GetVector(Memory m, byte n)
    {
        int base_ = n * 4;
        ushort off = (ushort)(m.Raw[base_ + 0] | (m.Raw[base_ + 1] << 8));
        ushort seg = (ushort)(m.Raw[base_ + 2] | (m.Raw[base_ + 3] << 8));
        return (seg, off);
    }
}
