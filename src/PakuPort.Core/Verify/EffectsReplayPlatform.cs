using System.Text.Json;
using PakuPort.Core.Platform;

namespace PakuPort.Core.Verify;

/// <summary>
/// Plataforma de REPLAY de efectos grabados: consume en orden el EffectsJson
/// del caso .pfc grabado por el explorador Unicorn (tools/explore_exec.py --record). Cada
/// INT/in del código emitido aplica el efecto observado en el oráculo (registros post-stub y
/// bytes escritos en memoria) en vez de ser no-op. Así el "límite del oráculo" (27/28 rojas,
/// EffectsJson vacío) pasa a ser verificable con la misma maquinaria autónoma, sin DOS vivo.
///
/// Esquema (v1, lo escribe el grabador; hex sin 0x):
///   { "v":1,
///     "pre":[["46C","64000000"], ...],                      lineal → bytes ANTES de ejecutar
///     "fx":[ {"t":"i","n":33,"r":[ax,bx,cx,dx,si,di,es,ds],"cf":0,"zf":1,"w":[["1E6F0","AABB"]]},
///            {"t":"p","v":9,"c":30000},                       "i"=INT n, "p"=in (valor devuelto)
///            {"t":"q","n":8,"d":1734,"w":[["1E6F0","01"]]} ] } "q"=IRQ (vector, instante, efecto)
/// El campo "c" (opcional, por defecto 1) son REPETICIONES INMEDIATAS del mismo efecto: un bucle
/// que espera el retrace leyendo 0x3DA treinta mil veces deja UNA entrada, no treinta mil.
///
/// Orden ESTRICTO: un efecto solo se consume si el siguiente de la cola coincide en tipo (y nº
/// de INT). Un mismatch cuenta como desync y se degrada a no-op — el diff byte-exacto del juez
/// decidirá; aquí no se inventa nada.
///
/// Las IRQ ("q") son la excepción y por eso van en su PROPIA lista: nadie las pide, las entrega
/// el reloj del caso (<see cref="PasoIrq"/>). Su campo "d" son las instrucciones desde el
/// arranque de la llamada, sin contar las gastadas dentro de una ISR.
/// </summary>
public sealed class EffectsReplayPlatform : IPlatform, IFarCallSink, Interp.IRelojIrq
{
    private sealed class Fx
    {
        public char T;                     // 'i' = INT, 'p' = port in, 'f' = far-call driver
        public byte N;                     // nº de INT
        public ushort Off;                 // 'f': offset del operando `call far` (match)
        public ushort[]? R;                // post-regs: ax bx cx dx si di es ds
        public bool Cf, Zf;
        public List<(int lin, byte[] d)> W = new();
        public ushort V;                   // valor devuelto por `in`
        public int C = 1;                  // repeticiones inmediatas (campo "c"; ausente = 1)
        public int Usadas;                 // cuántas se han consumido ya de este efecto
    }

    private readonly List<(int lin, byte[] d)> _pre = new();
    private readonly List<Fx> _fx = new();
    private int _pos;

    /// <summary>Una IRQ grabada: el instante (instrucciones desde el arranque del caso, sin
    /// contar las gastadas dentro de otra ISR) y lo que la ISR dejo escrito.</summary>
    private sealed record Irq(long D, byte Vector, List<(int lin, byte[] d)> W, List<(int reg, ushort val)> Rw);
    private readonly List<Irq> _irqs = new();
    private int _irqPos;

    /// <summary>IRQ grabadas en el caso y cuantas se han entregado ya. Lo lee el diagnostico: una
    /// funcion que acaba con IRQ sin entregar es una que no llego a esperarlas.</summary>
    public int IrqsTotal => _irqs.Count;
    public int IrqsEntregadas => _irqPos;

    public int Desyncs { get; private set; }
    public int Consumed => _pos;
    public int Total => _fx.Count;

    /// <summary>QUÉ efecto se pidió que la cola grabada no tenía, y DÓNDE. Sin esto un desync es
    /// un número: con esto es una instrucción concreta del juego y un hueco concreto del
    /// grabador. Lo lee <c>pakuport corpus-diag</c>.</summary>
    public List<string> DetalleDesyncs { get; } = new();

    void Desync(string pedido)
    {
        Desyncs++;
        if (DetalleDesyncs.Count < 16)
        {
            uint pc = Interp.LastPc;
            string tocaba = _pos >= _fx.Count ? "la cola grabada esta AGOTADA"
                : _fx[_pos].T == 'i' ? $"tocaba INT {_fx[_pos].N:X2}h"
                : _fx[_pos].T == 'p' ? "tocaba un `in`"
                : $"tocaba far-call {_fx[_pos].Off:X4}";
            DetalleDesyncs.Add($"{pedido} @ {pc >> 16:X4}:{pc & 0xFFFF:X4}: {tocaba} ({_pos}/{_fx.Count})");
        }
    }

    public EffectsReplayPlatform(string effectsJson)
    {
        using var doc = JsonDocument.Parse(effectsJson);
        var r = doc.RootElement;
        if (r.TryGetProperty("pre", out var pre))
            foreach (var e in pre.EnumerateArray())
                _pre.Add((Convert.ToInt32(e[0].GetString()!, 16), Convert.FromHexString(e[1].GetString()!)));
        if (r.TryGetProperty("fx", out var fxs))
            foreach (var e in fxs.EnumerateArray())
            {
                var f = new Fx { T = e.GetProperty("t").GetString()![0] };
                if (f.T == 'q')
                {
                    // Las IRQ NO van a la cola secuencial: no las pide nadie, las entrega el
                    // reloj. Meterlas en `_fx` pondria un efecto que ningun INT ni ningun `in`
                    // puede consumir justo delante del siguiente, y el caso saldria DesyncEfectos.
                    var w = new List<(int lin, byte[] d)>();
                    if (e.TryGetProperty("w", out var wq))
                        foreach (var we in wq.EnumerateArray())
                            w.Add((Convert.ToInt32(we[0].GetString()!, 16), Convert.FromHexString(we[1].GetString()!)));
                    var rw = new List<(int reg, ushort val)>();
                    if (e.TryGetProperty("rw", out var rwq))
                        foreach (var re in rwq.EnumerateArray())
                            rw.Add((re[0].GetInt32(), (ushort)re[1].GetInt32()));
                    _irqs.Add(new Irq(e.GetProperty("d").GetInt64(),
                                      (byte)e.GetProperty("n").GetInt32(), w, rw));
                    continue;
                }
                if (f.T == 'i' || f.T == 'f')
                {
                    if (f.T == 'i') f.N = (byte)e.GetProperty("n").GetInt32();
                    else f.Off = (ushort)e.GetProperty("o").GetInt32();
                    f.R = e.GetProperty("r").EnumerateArray().Select(x => (ushort)x.GetInt32()).ToArray();
                    f.Cf = e.GetProperty("cf").GetInt32() != 0;
                    f.Zf = e.GetProperty("zf").GetInt32() != 0;
                    if (e.TryGetProperty("w", out var w))
                        foreach (var we in w.EnumerateArray())
                            f.W.Add((Convert.ToInt32(we[0].GetString()!, 16), Convert.FromHexString(we[1].GetString()!)));
                }
                else
                {
                    f.V = (ushort)e.GetProperty("v").GetInt32();
                }
                if (e.TryGetProperty("c", out var cEl)) f.C = Math.Max(1, cEl.GetInt32());
                if (f.T == 'i') _intsConEfecto.Add(f.N);
                _fx.Add(f);
            }
    }

    /// <summary>Estado inicial fuera del DGROUP leído por la llamada grabada (BDA/PSP/env).</summary>
    public void ApplyPre(Memory m)
    {
        foreach (var (lin, d) in _pre)
            if (lin >= 0 && lin + d.Length <= Memory.Size)
                Array.Copy(d, 0, m.Raw, lin, d.Length);
    }

    /// <summary>El replay atiende un INT si el caso trae ALGÚN efecto grabado de ese número. Es
    /// lo que dice el propio corpus sobre quién lo atendía: si aquella INT hubiera vectorizado a
    /// código del juego, no habría dejado efecto ninguno —el grabador la descarta a propósito,
    /// ver <c>CorpusRecorder.ConfirmaInt</c>— y aquí toca volver a vectorizar y ejecutar ese
    /// código.
    ///
    /// <para>La pregunta es por NÚMERO de INT y no por la cabeza de la cola, y eso importa: con
    /// la cabeza, un desync convertía la INT en una vectorización y el ISR del juego se ponía a
    /// correr en mitad de un caso. Medido el 2026-08-25 en el corpus del explorador de un juego de
    /// Tycoon, eso tumbaba tres funciones que estaban verdes (`40a4`, `53b5`, `568f`), cuyas INT
    /// SÍ están todas grabadas.</para></summary>
    public bool AtiendeInt(byte intNo) => _intsConEfecto.Contains(intNo);

    private readonly HashSet<byte> _intsConEfecto = new();

    public void Int(byte intNo, Cpu cpu, Memory mem)
    {
        if (_pos >= _fx.Count || _fx[_pos].T != 'i' || _fx[_pos].N != intNo) { Desync($"INT {intNo:X2}h"); return; }
        var f = _fx[_pos];
        if (++f.Usadas >= f.C) _pos++;
        foreach (var (lin, d) in f.W)
            if (lin >= 0 && lin + d.Length <= Memory.Size)
                Array.Copy(d, 0, mem.Raw, lin, d.Length);
        var r = f.R!;
        cpu.AX = r[0]; cpu.BX = r[1]; cpu.CX = r[2]; cpu.DX = r[3];
        cpu.SI = r[4]; cpu.DI = r[5]; cpu.ES = r[6]; cpu.DS = r[7];
        cpu.CF = f.Cf; cpu.ZF = f.Zf;
    }

    /// <summary>Efecto "f": far-call a driver. Sólo consume si el siguiente efecto es "f" y su
    /// offset coincide con el del operando (no cuenta como desync si no: un `call far` a código
    /// residente lifteado legítimamente no lleva efecto). Aplica el write-set neto del driver,
    /// los registros post-llamada, y simula el `retf` (SP += 4, los 4 bytes que el emisor empujó).</summary>
    public bool TryFarCall(ushort seg, ushort off, Cpu cpu, Memory mem)
    {
        if (_pos >= _fx.Count || _fx[_pos].T != 'f' || _fx[_pos].Off != off) return false;
        var f = _fx[_pos];
        if (++f.Usadas >= f.C) _pos++;
        foreach (var (lin, d) in f.W)
            if (lin >= 0 && lin + d.Length <= Memory.Size)
                Array.Copy(d, 0, mem.Raw, lin, d.Length);
        var r = f.R!;
        cpu.AX = r[0]; cpu.BX = r[1]; cpu.CX = r[2]; cpu.DX = r[3];
        cpu.SI = r[4]; cpu.DI = r[5]; cpu.ES = r[6]; cpu.DS = r[7];
        cpu.CF = f.Cf; cpu.ZF = f.Zf;
        cpu.SP += 4;
        return true;
    }

    public byte PortIn8(ushort port) => (byte)NextIn(port);
    public ushort PortIn16(ushort port) => NextIn(port);
    public void PortOut8(ushort port, byte val) { }
    public void PortOut16(ushort port, ushort val) { }

    private ushort NextIn(ushort port)
    {
        if (_pos >= _fx.Count || _fx[_pos].T != 'p') { Desync($"in 0x{port:X3}"); return 0; }
        var f = _fx[_pos];
        if (++f.Usadas >= f.C) _pos++;
        return f.V;
    }

    /// <summary>EL RELOJ DE LAS IRQ. Lo llama el bucle del interprete una vez por instruccion con
    /// las que lleva ejecutadas el caso; cuando se alcanza el instante grabado, se aplica el
    /// efecto neto de aquella interrupcion de hardware.
    ///
    /// <para>Sin esto, una funcion que espera una IRQ no retorna JAMAS: son 14 de las 19 que el
    /// binario original de un juego de prueba no reproducia. La ISR no se ejecuta —igual que no se
    /// ejecuta el manejador de una INT— porque lo que el caso grabo es lo que aquella ISR dejo
    /// escrito, y eso es exactamente lo que el bucle de espera mira.</para>
    ///
    /// <para>El coste apagado es un `null` estatico en la cabecera del bucle; encendido, una
    /// comparacion de enteros por instruccion.</para></summary>
    public void PasoIrq(long insnsDelCaso, Memory m)
    {
        while (_irqPos < _irqs.Count && _irqs[_irqPos].D <= insnsDelCaso)
        {
            var q = _irqs[_irqPos++];
            foreach (var (lin, d) in q.W)
                if (lin >= 0 && lin + d.Length <= Memory.Size)
                    Array.Copy(d, 0, m.Raw, lin, d.Length);
            // Los REGISTROS que la ISR devolvio cambiados. Solo esos: aplicarlos todos
            // aplastaria el contador de vueltas del propio bucle de espera.
            var c = CpuActual;
            if (c != null)
                foreach (var (reg, val) in q.Rw)
                    switch (reg)
                    {
                        case 0: c.AX = val; break;
                        case 1: c.BX = val; break;
                        case 2: c.CX = val; break;
                        case 3: c.DX = val; break;
                        case 4: c.SI = val; break;
                        case 5: c.DI = val; break;
                        case 6: c.BP = val; break;
                        case 7: c.DS = val; break;
                        case 8: c.ES = val; break;
                    }
        }
    }

    /// <summary>La CPU del caso en curso. La pone quien enciende el reloj: el efecto de una IRQ
    /// toca registros y <see cref="PasoIrq"/> solo recibe la memoria.</summary>
    public Cpu? CpuActual { get; set; }
}
