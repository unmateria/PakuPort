using System.Text.Json;
using System.Text.Json.Serialization;
using PakuPort.Core;
using Xunit;

namespace PakuPort.Tests;

/// <summary>
/// Red de las instrucciones de 32 bits: el INTÉRPRETE ejecutando instrucciones de
/// 32 bits contra una referencia INDEPENDIENTE (Unicorn, tools/gen_opcode_vectors32.py).
///
/// Por qué el intérprete y no el lifter (que es lo que mide <see cref="OpcodeFlagTests"/>): el
/// intérprete ES el motor del producto, así que es donde el ancho de 32 bits tiene
/// que ser exacto. El lifter se queda como estaba a propósito — las redes de regresión lo cubren y no se toca.
///
/// Cada vector monta el MISMO banco de pruebas que Unicorn (código en 0:2000, scratch en 0:0100,
/// pila en 0:1000), ejecuta hasta el <c>hlt</c> final y compara los OCHO registros de 32 bits, las
/// dos ventanas de memoria y los flags que el SDM define para ese opcode. Los flags indefinidos no
/// se comparan: el vector trae su propia lista.
/// </summary>
public class Interp32Tests
{
    const ushort Code = 0x2000, Scratch = 0x0100, StackTop = 0x1000;
    const int ScratchLen = 0x40;

    public sealed class Vector
    {
        public string Name { get; set; } = "";
        public string Src { get; set; } = "";
        public string Dis { get; set; } = "";
        public string Code { get; set; } = "";
        public int N { get; set; }
        public Dictionary<string, uint> In { get; set; } = new();
        [JsonPropertyName("flags_in")] public uint FlagsIn { get; set; }
        public Dictionary<string, uint> Out { get; set; } = new();
        [JsonPropertyName("mem_out")] public Dictionary<string, string> MemOut { get; set; } = new();
        public Dictionary<string, uint> Masks { get; set; } = new();
        public int Cf { get; set; }
        public int Pf { get; set; }
        public int Af { get; set; }
        public int Zf { get; set; }
        public int Sf { get; set; }
        public int Of { get; set; }
        public string[] Check { get; set; } = Array.Empty<string>();
    }

    public static IEnumerable<object[]> Vectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "vectors", "opcodes32.json");
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var vecs = JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(path), opts)!;
        int i = 0;
        foreach (var v in vecs) yield return new object[] { i++, v };
    }

    /// <summary>Mismo patrón que el generador: bytes crecientes con el bit alto puesto en la
    /// segunda mitad, para que un acceso del ancho equivocado se note.</summary>
    static byte[] ScratchData()
    {
        var d = new byte[ScratchLen];
        for (int i = 0; i < ScratchLen; i++) d[i] = (byte)((i * 37 + 11) ^ (i >= 32 ? 0x80 : 0));
        return d;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void InterpMatchesUnicorn(int index, Vector v)
    {
        var m = new Memory();
        var scratch = ScratchData();
        Array.Copy(scratch, 0, m.Raw, Scratch, ScratchLen);
        var code = Convert.FromHexString(v.Code);
        Array.Copy(code, 0, m.Raw, Code, code.Length);
        m.Raw[Code + code.Length] = 0xF4;   // hlt: cierra el Run sin tocar estado

        var c = new Cpu
        {
            EAX = v.In["eax"], EBX = v.In["ebx"], ECX = v.In["ecx"], EDX = v.In["edx"],
            ESI = v.In["esi"], EDI = v.In["edi"], EBP = v.In["ebp"], ESP = v.In["esp"],
            CS = 0, DS = 0, ES = 0, SS = 0,
        };
        c.SetFlagsWord((ushort)v.FlagsIn);

        string tag = $"[{index} {v.Name} \"{v.Dis}\" code={v.Code} " +
                     $"eax={v.In["eax"]:X8} ebx={v.In["ebx"]:X8} ecx={v.In["ecx"]:X8} " +
                     $"edx={v.In["edx"]:X8} flags={v.FlagsIn:X4}]";

        Interp.Run(c, m, 0, Code, null, null);

        var got = new Dictionary<string, uint>
        {
            ["eax"] = c.EAX, ["ebx"] = c.EBX, ["ecx"] = c.ECX, ["edx"] = c.EDX,
            ["esi"] = c.ESI, ["edi"] = c.EDI, ["ebp"] = c.EBP, ["esp"] = c.ESP,
        };
        foreach (var (reg, want) in v.Out)
        {
            uint mask = v.Masks.TryGetValue(reg, out var mk) ? mk : 0xFFFFFFFF;
            Assert.True((want & mask) == (got[reg] & mask),
                $"{tag} {reg.ToUpper()} esperado {want & mask:X8} obtenido {got[reg] & mask:X8}");
        }

        foreach (var (addrHex, wantHex) in v.MemOut)
        {
            int addr = Convert.ToInt32(addrHex, 16);
            var want = Convert.FromHexString(wantHex);
            var gotMem = new byte[want.Length];
            Array.Copy(m.Raw, addr, gotMem, 0, want.Length);
            if (!want.AsSpan().SequenceEqual(gotMem))
            {
                int d = 0;
                while (d < want.Length && want[d] == gotMem[d]) d++;
                Assert.Fail($"{tag} memoria {addr:X4}+{d:X2} esperado {want[d]:X2} obtenido {gotMem[d]:X2} " +
                            $"(esperado {Convert.ToHexString(want)} obtenido {Convert.ToHexString(gotMem)})");
            }
        }

        var flags = new Dictionary<string, int>
        {
            ["cf"] = c.CF ? 1 : 0, ["pf"] = c.PF ? 1 : 0, ["af"] = c.AF ? 1 : 0,
            ["zf"] = c.ZF ? 1 : 0, ["sf"] = c.SF ? 1 : 0, ["of"] = c.OF ? 1 : 0,
        };
        var expFlags = new Dictionary<string, int>
        {
            ["cf"] = v.Cf, ["pf"] = v.Pf, ["af"] = v.Af,
            ["zf"] = v.Zf, ["sf"] = v.Sf, ["of"] = v.Of,
        };
        foreach (var key in v.Check)
            Assert.True(expFlags[key] == flags[key],
                $"{tag} {key.ToUpper()} esperado {expFlags[key]} obtenido {flags[key]}");
    }
}
