using System.Text.Json;
using System.Text.Json.Serialization;
using PakuPort.Core;
using Xunit;

namespace PakuPort.Tests;

/// <summary>
/// NIVEL 1: unit test de opcodes liftados contra una referencia INDEPENDIENTE
/// (Unicorn). El "esperado" lo dicta Unicorn (tools/gen_opcode_vectors.py), nunca un humano. Caza
/// bugs de flags/wrap ANTES de tocar funciones reales — el riesgo nº1 de divergencia. Si rompo una
/// regla de flag en el Cpu, estos tests se ponen ROJOS (el juez muerde también a este nivel).
/// </summary>
public class OpcodeFlagTests
{
    public sealed class Vector
    {
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
        public int Ax { get; set; }
        public int Bx { get; set; }
        public int Cx { get; set; }
        public int Dx { get; set; }
        [JsonPropertyName("cf_in")] public int CfIn { get; set; }
        [JsonPropertyName("af_in")] public int AfIn { get; set; }
        [JsonPropertyName("out_ax")] public int OutAx { get; set; }
        [JsonPropertyName("out_dx")] public int OutDx { get; set; }
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
        string path = TestSupport.VectorsPath();
        var json = File.ReadAllText(path);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var vecs = JsonSerializer.Deserialize<List<Vector>>(json, opts)!;
        int i = 0;
        foreach (var v in vecs) yield return new object[] { i++, v };
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void LiftedOpcodeMatchesUnicorn(int index, Vector v)
    {
        var cpu = TestSupport.RunSnippet(Convert.FromHexString(v.Code), (ushort)v.Ax, (ushort)v.Bx, (ushort)v.Cx, v.CfIn != 0, (ushort)v.Dx, v.AfIn != 0);

        string tag = $"[{index} {v.Name} code={v.Code} ax={v.Ax:X4} bx={v.Bx:X4} dx={v.Dx:X4} cf_in={v.CfIn}]";

        var got = new Dictionary<string, int>
        {
            ["ax"] = cpu.AX, ["dx"] = cpu.DX,
            ["cf"] = cpu.CF ? 1 : 0, ["pf"] = cpu.PF ? 1 : 0, ["af"] = cpu.AF ? 1 : 0,
            ["zf"] = cpu.ZF ? 1 : 0, ["sf"] = cpu.SF ? 1 : 0, ["of"] = cpu.OF ? 1 : 0,
        };
        var exp = new Dictionary<string, int>
        {
            ["ax"] = v.OutAx, ["dx"] = v.OutDx,
            ["cf"] = v.Cf, ["pf"] = v.Pf, ["af"] = v.Af,
            ["zf"] = v.Zf, ["sf"] = v.Sf, ["of"] = v.Of,
        };
        foreach (var key in v.Check)
            Assert.True(exp[key] == got[key],
                $"{tag} {key.ToUpper()} esperado 0x{exp[key]:X} obtenido 0x{got[key]:X}");
    }
}
