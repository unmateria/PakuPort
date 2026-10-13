using System.Buffers.Binary;

namespace PakuPort.Judge.Oracle;

/// <summary>
/// Snapshot completo de CPU del oráculo (REG_READ 0x20 / REG_WRITE 0x21).
/// Layout verbatim of the DOSBox-X oracle state: "&lt;10I6H6I" = 76 bytes
///   10×u32: eax ebx ecx edx esi edi ebp esp eip eflags
///    6×u16: cs ds es fs gs ss (selectores)
///    6×u32: cs_base ds_base es_base fs_base gs_base ss_base
/// </summary>
public struct CpuState
{
    public const int Size = 76;

    public uint Eax, Ebx, Ecx, Edx, Esi, Edi, Ebp, Esp, Eip, Eflags;
    public ushort Cs, Ds, Es, Fs, Gs, Ss;
    public uint CsBase, DsBase, EsBase, FsBase, GsBase, SsBase;

    // Vistas de 16 bits (modo real DOS)
    public ushort Ax { readonly get => (ushort)Eax; set => Eax = (Eax & 0xFFFF0000) | value; }
    public ushort Bx { readonly get => (ushort)Ebx; set => Ebx = (Ebx & 0xFFFF0000) | value; }
    public ushort Cx { readonly get => (ushort)Ecx; set => Ecx = (Ecx & 0xFFFF0000) | value; }
    public ushort Dx { readonly get => (ushort)Edx; set => Edx = (Edx & 0xFFFF0000) | value; }
    public readonly ushort Ip => (ushort)Eip;

    public static CpuState FromBytes(ReadOnlySpan<byte> d)
    {
        if (d.Length < Size) throw new InvalidDataException($"bloque de registros corto: {d.Length} (necesita {Size})");
        return new CpuState
        {
            Eax = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(0)),
            Ebx = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4)),
            Ecx = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(8)),
            Edx = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(12)),
            Esi = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(16)),
            Edi = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(20)),
            Ebp = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(24)),
            Esp = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(28)),
            Eip = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(32)),
            Eflags = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(36)),
            Cs = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(40)),
            Ds = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(42)),
            Es = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(44)),
            Fs = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(46)),
            Gs = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(48)),
            Ss = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(50)),
            CsBase = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(52)),
            DsBase = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(56)),
            EsBase = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(60)),
            FsBase = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(64)),
            GsBase = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(68)),
            SsBase = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(72)),
        };
    }

    public readonly byte[] ToBytes()
    {
        var d = new byte[Size];
        void U(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        void H(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), v);
        U(0, Eax); U(4, Ebx); U(8, Ecx); U(12, Edx);
        U(16, Esi); U(20, Edi); U(24, Ebp); U(28, Esp);
        U(32, Eip); U(36, Eflags);
        H(40, Cs); H(42, Ds); H(44, Es); H(46, Fs); H(48, Gs); H(50, Ss);
        U(52, CsBase); U(56, DsBase); U(60, EsBase); U(64, FsBase); U(68, GsBase); U(72, SsBase);
        return d;
    }
}
