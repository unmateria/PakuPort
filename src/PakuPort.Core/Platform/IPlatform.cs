namespace PakuPort.Core.Platform;

/// <summary>
/// Shim de plataforma DOS: despacha interrupciones (INT n) y puertos de E/S (in/out).
/// Cuando es null en Cpu, el código emitido se comporta como stub (sin efecto) — backward
/// compatible con los 78 verdes del juez byte-exacto.
/// </summary>
public interface IPlatform
{
    void Int(byte intNo, Cpu cpu, Memory mem);

    /// <summary>¿Atiende ESTA plataforma el INT n, o hay que vectorizarlo por la IVT del juego?
    ///
    /// <para>Es una pregunta del HARDWARE, no de la plataforma: un `int n` cuyo vector apunta a
    /// código que el juego instaló tiene que ejecutar ese código, sea cual sea la plataforma que
    /// haya debajo. Estaba atada a <c>DosPlatform</c> con un <c>is</c>, y eso dejaba al REPLAY del
    /// corpus sin vectorizar: medido el 2026-08-25 en un juego de prueba cuya VM de gráficos vive en
    /// <c>INT 91h</c>, el original no reproducía 25 de sus propias funciones grabadas porque el
    /// INT 91h se convertía en un no-op silencioso.</para></summary>
    bool AtiendeInt(byte intNo);
    byte PortIn8(ushort port);
    ushort PortIn16(ushort port);
    void PortOut8(ushort port, byte val);
    void PortOut16(ushort port, ushort val);
}

/// <summary>
/// Sink de llamadas far a driver (efecto "f"): en modo REPLAY (juez), una `call far` a un
/// driver gráfico se consume como el efecto grabado (write-set neto + registros post-llamada)
/// en vez de ejecutar el driver. Sólo lo implementa EffectsReplayPlatform; el runtime real
/// (DosPlatform) NO lo implementa, así que el port en producción no cambia de comportamiento.
/// El match es por OFFSET del operando (el segmento difiere: 0 pre-reloc en el port vs relocado
/// en el oráculo) y por orden estricto de la cola de efectos.
/// </summary>
public interface IFarCallSink
{
    bool TryFarCall(ushort seg, ushort off, Cpu cpu, Memory mem);
}
