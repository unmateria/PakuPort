using System.Buffers.Binary;
using System.Text;
using PakuPort.Judge.Oracle;

namespace PakuPort.Judge.Corpus;

/// <summary>
/// Un caso grabado del ORIGINAL para una función: estado de entrada y de salida + efectos.
/// Modo offline (record-replay): se graba una vez jugando el original; el verificador re-pasa
/// este caso por la función C# del port y exige salida idéntica.
///
/// Contenedor binario autodescriptivo "PFC1":
///   "PFC1" | u32 ghidraOffset | u32 callDepth | u32 dsBase
///   entryCpu[76] | exitCpu[76]
///   u32 memLen | entryMem[memLen] | exitMem[memLen]
///   u32 effLen  | effectsJson[effLen]   (utf8; "" si no hay)
/// memLen = tamaño de la región capturada (p.ej. 0x10000 = DGROUP completo).
/// </summary>
public sealed class CorpusCase
{
    public const uint Magic = 0x31434650; // "PFC1" little-endian

    public int GhidraOffset;
    public int CallDepth;
    /// <summary>Base lineal del DS en el snapshot, para anclar memLen al DGROUP del port.</summary>
    public uint DsBase;
    public CpuState EntryCpu;
    public CpuState ExitCpu;
    public byte[] EntryMem = Array.Empty<byte>();
    public byte[] ExitMem = Array.Empty<byte>();
    /// <summary>Efectos ordenados (paleta/VRAM/OPL2/IO/INT) como JSON; vacío si no aplica.</summary>
    public string EffectsJson = "";

    public byte[] Serialize()
    {
        if (EntryMem.Length != ExitMem.Length)
            throw new InvalidOperationException("entryMem y exitMem deben tener la misma longitud");
        var eff = Encoding.UTF8.GetBytes(EffectsJson);
        int memLen = EntryMem.Length;
        int total = 4 + 4 + 4 + 4 + CpuState.Size * 2 + 4 + memLen * 2 + 4 + eff.Length;
        var d = new byte[total];
        int p = 0;
        void PU32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(p), v); p += 4; }
        void PBytes(byte[] b) { Array.Copy(b, 0, d, p, b.Length); p += b.Length; }
        PU32(Magic);
        PU32((uint)GhidraOffset);
        PU32((uint)CallDepth);
        PU32(DsBase);
        PBytes(EntryCpu.ToBytes());
        PBytes(ExitCpu.ToBytes());
        PU32((uint)memLen);
        PBytes(EntryMem);
        PBytes(ExitMem);
        PU32((uint)eff.Length);
        PBytes(eff);
        return d;
    }

    public static CorpusCase Deserialize(byte[] d)
    {
        int p = 0;
        uint GU32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }
        byte[] GBytes(int n) { var b = d[p..(p + n)]; p += n; return b; }
        if (GU32() != Magic) throw new InvalidDataException("magic de corpus inválido (esperado PFC1)");
        var c = new CorpusCase
        {
            GhidraOffset = (int)GU32(),
            CallDepth = (int)GU32(),
            DsBase = GU32(),
        };
        c.EntryCpu = CpuState.FromBytes(GBytes(CpuState.Size));
        c.ExitCpu = CpuState.FromBytes(GBytes(CpuState.Size));
        int memLen = (int)GU32();
        c.EntryMem = GBytes(memLen);
        c.ExitMem = GBytes(memLen);
        int effLen = (int)GU32();
        c.EffectsJson = Encoding.UTF8.GetString(GBytes(effLen));
        return c;
    }
}
