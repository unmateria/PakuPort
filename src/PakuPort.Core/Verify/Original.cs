using PakuPort.Core.Platform;

namespace PakuPort.Core.Verify;

/// <summary>
/// EL CONTRATO DE LLAMADA — lo que le falta a una traduccion para poder llamar a algo.
///
/// <para><b>Por que existe.</b> El banco ciego del dia 2 dejo la linea base en 10/23, y de las
/// 13 rojas <b>7 lo eran por la misma causa: la funcion llama a otra funcion del juego, a una
/// INT o a un puerto, y el encargo no daba ninguna forma de hacerlo</b>. Eran irreproducibles
/// por construccion, no por culpa de quien las tradujo. Con esto, una funcion se puede portar
/// sin haber portado antes todo lo que cuelga de ella: lo que todavia no esta traducido lo
/// sigue haciendo el binario original, interpretado.</para>
///
/// <para>Es la doctrina normal de un porteo incremental, y no cuesta fidelidad: la parte no
/// traducida ES el original, byte a byte.</para>
///
/// <para><b>Los efectos van EN ORDEN.</b> Durante el juicio de un caso, las INT y los `in` no se
/// ejecutan de verdad: se devuelven los grabados, en la secuencia en que ocurrieron. Pedirlos en
/// otro orden (o pedir uno de mas) descuadra la cola y el caso sale rojo — que es lo correcto:
/// una traduccion que llama a `int 21h` donde el original no lo hacia no es equivalente.</para>
/// </summary>
public sealed class Original
{
    private readonly Cpu _c;
    private readonly Memory _m;

    public Original(Cpu c, Memory m) { _c = c; _m = m; }

    /// <summary>Llamada CERCANA (`call off`) a otra funcion del juego, en el segmento de codigo
    /// actual. Deja los registros y la memoria como los deje la funcion original.
    ///
    /// <para><paramref name="retorno"/> es la direccion a la que volveria el `call` original, o sea
    /// la instruccion siguiente. <b>No es un detalle</b>: el `call` la ESCRIBE en la pila, la pila
    /// es memoria, y el juez compara los 64 KB. Cuando esa palabra cae por debajo de la banda de
    /// scratch enmascarada —porque lo llamado anida y baja la pila—, poner un valor inventado deja
    /// un byte distinto y la traduccion sale roja sin que nada este mal en ella. Medido en
    /// un juego de prueba con `FUN_1000_375c`: el juez veia `FFFF` donde el original tenia `0B7A`, que es
    /// exactamente la direccion de retorno del `call 1401h`.</para>
    ///
    /// <para>Se deja con valor por defecto para no romper las traducciones ya escritas, pero el
    /// tablero lo genera SIEMPRE: `o.Llama(0x1401, 0x0B7A)`.</para></summary>
    public void Llama(ushort off, ushort retorno = 0xFFFF)
    {
        ushort cs = _c.CS;
        Empuja(retorno);                // direccion de retorno: la consume el `ret` del callee
        HastaQueVuelva(cs, off);
        _c.CS = cs;
    }

    /// <summary>Llamada LEJANA (`call far seg:off`). Si el caso trae grabado el efecto de esa
    /// llamada (un driver que el juez no ejecuta), se aplica ese; si no, se interpreta.</summary>
    public void LlamaLejos(ushort seg, ushort off)
    {
        ushort cs = _c.CS;
        Empuja(cs);
        Empuja(0xFFFF);
        if (MiniFar(_c, _m, seg, off)) return;   // el efecto grabado ya deshizo el marco
        HastaQueVuelva(seg, off);
        _c.CS = cs;
    }

    /// <summary>Ejecuta lo llamado HASTA SU `ret`, y ni una instruccion mas.
    ///
    /// <para><b>Dentro del juego esto no pasaba, y se midio el 2026-09-23.</b> La partida corre
    /// con <c>Interp.WholeProgram</c> encendido, y en ese modo un `ret` que sube por encima del
    /// marco NO termina el <c>Run</c>: el interprete sigue en la direccion de retorno, como hace
    /// el hardware. Asi que un <c>o.Llama</c> hecho desde una traduccion no volvia nunca a ella:
    /// el juego entero seguia corriendo DENTRO de la llamada, el C# de despues no se ejecutaba, y
    /// cada vez que la partida pasaba otra vez por esa traduccion se anidaba un nivel mas en la
    /// pila de .NET. Era lo que parecia "una traduccion que se llama a si misma"
    /// (<c>FUN_1000_375c</c> de un juego de prueba, ocho veces en la pila y 336 rechazos), y lo que hacia
    /// que el A/B saliera limpio: el juego lo seguia haciendo el original. En el juez no se veia
    /// porque alli <c>WholeProgram</c> esta apagado.</para></summary>
    private void HastaQueVuelva(ushort seg, ushort off)
    {
        bool wp = Interp.WholeProgram, isr = Interp.IsrMode;
        Interp.WholeProgram = false;
        Interp.IsrMode = false;
        try { Interp.Run(_c, _m, seg, off, null, MiniFar); }
        finally { Interp.WholeProgram = wp; Interp.IsrMode = isr; }
    }

    /// <summary>EL RITMO: el original habria ejecutado <paramref name="n"/> instrucciones aqui.
    ///
    /// <para>Una traduccion en C# no gasta instrucciones del invitado, y el reloj del invitado ES
    /// el contador de instrucciones: de el salen las IRQ del temporizador, el paso del tiempo de
    /// la BIOS, las fronteras de frame y el ritmo del juego entero. Sin cobrar, un bucle traducido
    /// que espera un tic del reloj se quedaria esperando para siempre, y cualquier juego iria a
    /// otra velocidad.</para>
    ///
    /// <para>La regla que lo hace exacto: <b>antes de cada instruccion OBSERVABLE</b> (`int`,
    /// `in`, `out`, una llamada) se cobra todo lo pendiente hasta ella incluida, y al final del
    /// bloque lo que quede. Asi el reloj marca lo mismo que en el original en cada punto en que
    /// algo de fuera lo puede mirar. El juez compara la cuenta con la del original caso a caso
    /// (el "ritmo").</para></summary>
    public void Instrucciones(int n) => Interp.Cobra(_c, _m, n);

    /// <summary>Un `rep` que proceso <paramref name="elementos"/> elementos. Ademas de la
    /// instruccion (que se cobra con <see cref="Instrucciones"/> como cualquier otra), el
    /// interprete cobra un tic por cada elemento menos el ultimo — la regla de DOSBox-X, ver
    /// <c>Cpu.ChargeStringElement</c>. Con 0 elementos no cobra nada.</summary>
    public void Repeticiones(int elementos)
    {
        for (int i = 1; i < elementos; i++) _c.ChargeStringElement();
    }

    /// <summary>Una interrupcion software (`int n`). Durante el juicio devuelve el resultado
    /// GRABADO de esa misma llamada: registros de salida, banderas y lo que escribio en
    /// memoria.</summary>
    public void Interrupcion(byte n)
    {
        CorteDeterminista.EnInterrupcion(_c);
        _c.Platform?.Int(n, _c, _m);
    }

    public byte LeePuerto8(ushort puerto) => _c.Platform?.PortIn8(puerto) ?? 0;
    public ushort LeePuerto16(ushort puerto) => _c.Platform?.PortIn16(puerto) ?? 0;
    public void EscribePuerto8(ushort puerto, byte valor) => _c.Platform?.PortOut8(puerto, valor);
    public void EscribePuerto16(ushort puerto, ushort valor) => _c.Platform?.PortOut16(puerto, valor);

    private void Empuja(ushort v)
    {
        _c.SP -= 2;
        _m.WrU16(_c.SS, _c.SP, v);
    }

    private static bool MiniFar(Cpu c, Memory m, ushort seg, ushort off)
        => c.Platform is IFarCallSink s && s.TryFarCall(seg, off, c, m);
}
