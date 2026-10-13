namespace PakuPort.Core.Platform;

/// <summary>
/// Controlador de teclado 8042 (i8042). Puerto 0x60 = dato/scancode, 0x64 = status/comando.
/// Mantiene una cola de scancodes; al leer 0x60 se consume el siguiente.
/// El caller debe invocar PicRaiseIrq1 (hook) cuando se inserta un scancode para señalar IRQ1.
/// Adaptado de dosbox-x src/hardware/keyboard.cpp (versión mínima de interfaz de bus).
/// </summary>
public sealed class KeyboardController
{
    private readonly Queue<byte> _queue = new(256);

    // Latch del puerto 0x60, como el hardware real y dosbox-x (keyboard.cpp p60data): el último
    // byte entregado PERSISTE tras leerlo hasta que el 8042 transfiere el siguiente. Sin esto,
    // cuando el INT 9 de la BIOS consume el break code, un juego que sondea 0x60 por su cuenta
    // (un juego de prueba en sus pantallas de selección) nunca ve la liberación → tecla pegada eterna.
    private byte _p60data;
    private bool _obf;   // output buffer full (bit 0 del status 0x64)

    // PAKU_LOG_KBD=1: traza del bus del 8042 (encolado, transferencia al latch con su IRQ1 y
    // lectura de 0x60 con el PC del lector). Es la única forma de ver por qué se atasca la cola.
    private static readonly bool _log =
        Environment.GetEnvironmentVariable("PAKU_LOG_KBD") == "1";
    private static void Log(string s) => Console.Error.WriteLine("[KBD] " + s);
    private static string Pc()
    {
        uint pc = Interp.LastPc;
        return $"{pc >> 16:X4}:{pc & 0xFFFF:X4}";
    }

    // Transfiere el siguiente byte de la cola al latch si está libre; una IRQ1 por byte
    // transferido (el 8042 real levanta la IRQ al poner OBF, no al encolar).
    private void Pump()
    {
        if (!_obf && _queue.Count > 0)
        {
            _p60data = _queue.Dequeue();
            _obf = true;
            if (_log) Log($"pump sc={_p60data:X2} -> IRQ1 (cola={_queue.Count})");
            PicRaiseIrq1?.Invoke();
        }
    }

    // Variante sin IRQ para las respuestas de comando del 8042 (self-test etc.), que el
    // firmware lee sondeando el status — igual que el comportamiento previo (no señalaban PIC).
    private void PumpNoIrq()
    {
        if (!_obf && _queue.Count > 0)
        {
            _p60data = _queue.Dequeue();
            _obf = true;
        }
    }

    // Estado del comando 8042 (0x64 write)
    private bool _expectingData = false;
    private byte _pendingCmd    = 0;

    // Byte de estado interno
    private byte _outputPort = 0xCF; // A20 activo, reset no, etc.

    /// <summary>
    /// Hook: asignado por el host para señalar IRQ1 cuando se inserta un scancode.
    /// Si es null, el scancode se encola sin señalar el PIC.
    /// </summary>
    public Action? PicRaiseIrq1;

    /// <summary>
    /// Lee el latch del puerto 0x60 (equivalente a IN AL, 0x60 en hardware): devuelve el último
    /// scancode entregado, que PERSISTE (no se consume). La lectura libera OBF y el 8042
    /// transfiere el siguiente byte pendiente (con su IRQ1), como dosbox-x.
    /// </summary>
    public byte ReadScancode()
    {
        byte v = _p60data;
        if (_log) Log($"read60 -> {v:X2} desde {Pc()} (cola={_queue.Count})");
        _obf = false;
        Pump();
        return v;
    }

    /// <summary>Inserta un scancode en la cola. La IRQ1 se señala al transferir el byte al
    /// latch (una por byte), no al encolar.
    /// Debe llamarse SOLO desde el hilo del juego (lo hace MachineTimer.Poll al drenar la cola
    /// del host): _queue es un Queue&lt;byte&gt; sin lock, leído por el puerto 0x60 / INT 9 en ese
    /// mismo hilo.</summary>
    public void PushScancode(byte sc)
    {
        if (_queue.Count < 256)
            _queue.Enqueue(sc);
        if (_log) Log($"push sc={sc:X2} (obf={(_obf ? 1 : 0)} cola={_queue.Count})");
        Pump();
    }

    // ── Cola de productor del host (hilo de render → hilo del juego) ─────────────────────────
    // El hilo de render ENCOLA scancodes crudos aquí (EnqueueHostScancode); el hilo del juego los
    // DRENA en MachineTimer.Poll (TryDequeueHostScancode → PushScancode + Pic.RaiseIrq(1)). Así
    // _queue (el buffer del 8042 leído por el puerto 0x60 / INT 9) lo muta UN solo hilo y la IRQ1
    // se late en el flujo de instrucciones, sin carrera sobre el Queue<byte>. Misma clase que la
    // entrada del ratón: antes el hilo de render llamaba PushScancode
    // (Enqueue) mientras el hilo del juego hacía ReadScancode (Dequeue) sobre el MISMO Queue<byte>.
    private readonly Queue<byte> _hostQueue = new(256);
    private readonly object _hostLock = new();

    /// <summary>Hilo de render: encola un scancode crudo del host. No toca _queue ni el PIC.</summary>
    public void EnqueueHostScancode(byte sc)
    {
        InputTape.RecScan(sc);
        lock (_hostLock) { if (_hostQueue.Count < 256) _hostQueue.Enqueue(sc); }
    }

    /// <summary>Hilo del juego (Poll): saca el siguiente scancode encolado por el host. False si
    /// la cola está vacía.</summary>
    public bool TryDequeueHostScancode(out byte sc)
    {
        lock (_hostLock)
        {
            if (_hostQueue.Count > 0) { sc = _hostQueue.Dequeue(); return true; }
            sc = 0; return false;
        }
    }

    /// <summary>Lee el puerto 0x60 (dato) o 0x64 (status).</summary>
    public byte In(ushort port)
    {
        if (port == 0x60)
        {
            return ReadScancode();
        }
        if (port == 0x64)
        {
            // Status register:
            // bit 0 = OBF (output buffer full — dato disponible para leer en 0x60)
            // bit 1 = IBF (input buffer full — listo para escribir)
            // bit 2 = System flag
            // bit 5 = OBF mouse (0)
            byte status = 0x04; // system flag siempre 1
            if (_obf) status |= 0x01; // OBF
            return status;
        }
        return 0xFF;
    }

    /// <summary>Escribe al puerto 0x60 (dato al 8042) o 0x64 (comando al 8042).</summary>
    public void Out(ushort port, byte v)
    {
        if (port == 0x64)
        {
            // Comandos estándar del 8042
            switch (v)
            {
                case 0xAA: // Self-test
                    _queue.Enqueue(0x55); PumpNoIrq(); // test pasado
                    break;
                case 0xAB: // Interface test
                    _queue.Enqueue(0x00); PumpNoIrq(); // OK
                    break;
                case 0xD0: // Read output port
                    _queue.Enqueue(_outputPort); PumpNoIrq();
                    break;
                case 0xD1: // Write output port
                    _expectingData = true;
                    _pendingCmd = 0xD1;
                    break;
                case 0xD4: // Write to mouse
                    _expectingData = true;
                    _pendingCmd = 0xD4;
                    break;
                case 0xAD: // Disable keyboard
                case 0xAE: // Enable keyboard
                    break;
                case 0x20: // Read command byte
                    _queue.Enqueue(0x45); PumpNoIrq(); // traducción habilitada, sin IRQ1 habilitada
                    break;
                case 0x60: // Write command byte
                    _expectingData = true;
                    _pendingCmd = 0x60;
                    break;
                case 0xFE: // Pulse reset
                    break; // ignorar
                default:
                    _expectingData = false;
                    break;
            }
            return;
        }

        if (port == 0x60)
        {
            if (_expectingData)
            {
                _expectingData = false;
                switch (_pendingCmd)
                {
                    case 0xD1: // dato para output port
                        _outputPort = v;
                        break;
                    case 0x60: // comando byte
                        break; // ignorar; mínimo viable
                    // 0xD4 = write to mouse: ignorar
                }
            }
            // else: datos de teclado enviados al 8042 — típicamente comandos al teclado
            // como 0xED (set LEDs), 0xF0 (scan code set), etc.; ignorar en modo mínimo
        }
    }
}
