namespace PakuPort.Core.Platform;

/// <summary>
/// 8259A Programmable Interrupt Controller (PIC). Maestro en puertos 0x20/0x21,
/// esclavo en 0xA0/0xA1. Modelo mínimo: IMR (interrupt mask register), ISR (in-service),
/// IRR (requests), EOI no-específico, base de vector configurable.
/// Adaptado de dosbox-x src/hardware/pic.cpp (versión simplificada sin prioridad rotatoria).
/// </summary>
public sealed class Pic
{
    // ---- estado maestro ----
    // IMR inicial = estado post-BIOS de un PC DOS, no el reset puro del 8259A (0xFF).
    // La BIOS deja timer (IRQ0), teclado (IRQ1) y cascada (IRQ2) desenmascarados y los
    // juegos DOS confían en ello sin reprogramar el PIC. Con 0xFF ninguna IRQ se
    // entregaba jamás y las ISRs de timer/teclado del juego no corrían nunca.
    private byte _masterImr = 0xB8;   // máscara: 1 = enmascarado (IRQ0/1/2/6 abiertas)
    private byte _masterIsr = 0x00;   // in-service
    private byte _masterIrr = 0x00;   // interrupt request register
    private byte _masterIcw = 0;      // fase de inicialización ICW
    private byte _masterBase = 0x08;  // base vector maestro (IRQ0→INT8)

    // ---- estado esclavo ----
    private byte _slaveImr  = 0xFF;
    private byte _slaveIsr  = 0x00;
    private byte _slaveIrr  = 0x00;
    private byte _slaveIcw  = 0;
    private byte _slaveBase = 0x70;  // base vector esclavo (IRQ8→INT0x70)

    // ---- flags de lectura (POLL command) ----
    private bool _masterReadIsr = false; // false = leer IRR, true = leer ISR
    private bool _slaveReadIsr  = false;

    // ---- constantes ----
    private const byte CascadeIrq = 2;  // IRQ2 en maestro conecta al esclavo

    // El PIC lo mutan dos hilos: el host (RaiseIrq(1) de teclado) y el juego (Poll/AckNext/
    // Eoi/Out/In). Lock reentrante (Monitor) en las mutaciones públicas: Out puede llamar a
    // Eoi en el mismo hilo. ~miles de llamadas/s, no por-Step: coste despreciable.
    private readonly object _lock = new();

    /// <summary>
    /// Señala la llegada de una interrupción hardware en la línea irq (0-15).
    /// El caller debe comprobar IsMasked y, si no está enmascarada, llamar FireInterrupt.
    /// </summary>
    public void RaiseIrq(int irq)
    {
        if (RastroDeterminismo.Activo) RastroDeterminismo.AnotaEvento($"irq{irq}");
        lock (_lock)
        {
            if (irq < 8)
            {
                _masterIrr |= (byte)(1 << irq);
            }
            else
            {
                int slaveIrq = irq - 8;
                _slaveIrr |= (byte)(1 << slaveIrq);
                // El esclavo está conectado a IRQ2 del maestro
                _masterIrr |= (byte)(1 << CascadeIrq);
            }
        }
    }

    /// <summary>Devuelve true si el IRQ está enmascarado en el IMR.</summary>
    /// <summary>Abre una linea del PIC. La usa el driver de raton sintetico con la IRQ12: el
    /// esclavo arranca con TODO enmascarado (_slaveImr = 0xFF) y un raton PS/2 de verdad abre su
    /// linea al instalarse. Sin esto la IRQ nunca sale y el evento se queda en la cola.</summary>
    public void Desenmascara(int irq)
    {
        lock (_lock)
        {
            if (irq < 8) _masterImr &= unchecked((byte)~(1 << irq));
            else
            {
                _slaveImr &= unchecked((byte)~(1 << (irq - 8)));
                _masterImr &= unchecked((byte)~(1 << CascadeIrq));   // y la cascada, o el esclavo no llega
            }
        }
    }

    public bool IsMasked(int irq)
    {
        if (irq < 8) return (_masterImr & (1 << irq)) != 0;
        return (_slaveImr & (1 << (irq - 8))) != 0;
    }

    /// <summary>
    /// End-Of-Interrupt no-específico. Limpia el bit más alto del ISR.
    /// Acepta irq=-1 para EOI genérico (el juego manda 0x20 al puerto 0x20).
    /// </summary>
    public void Eoi(int irq)
    {
      lock (_lock)
      {
        if (irq < 0 || irq < 8)
        {
            // EOI al maestro: limpiar el bit de mayor prioridad activo en ISR
            if (_masterIsr != 0)
            {
                int bit = BitScanForward(_masterIsr);
                _masterIsr &= (byte)(~(1 << bit) & 0xFF);
            }
        }
        else
        {
            int slaveIrq = irq - 8;
            if (_slaveIsr != 0)
            {
                int bit = BitScanForward(_slaveIsr);
                _slaveIsr &= (byte)(~(1 << bit) & 0xFF);
            }
            // también limpiar cascada en maestro si el esclavo quedó vacío
            if (_slaveIsr == 0 && _slaveIrr == 0)
                _masterIsr &= (byte)(~(1 << CascadeIrq) & 0xFF);
        }
      }
    }

    /// <summary>Calcula el vector de interrupción para un irq (0-15).</summary>
    public byte IrqToVector(int irq)
        => irq < 8 ? (byte)(_masterBase + irq) : (byte)(_slaveBase + (irq - 8));

    /// <summary>
    /// Saca la siguiente IRQ pendiente (en IRR), no enmascarada (IMR) y de mayor prioridad
    /// (IRQ0 = máxima). La marca in-service (ISR) y la quita de IRR. Devuelve false si no hay
    /// ninguna lista para servir. El que la sirve hará EOI (el ISR del juego, o el handler por
    /// defecto). Usado por DosPlatform.ServicePendingInterrupts en el hilo del juego.
    ///
    /// <b>REPORTADO, NO ARREGLADO:</b> falta la RESOLUCIÓN DE PRIORIDAD del 8259. Un PIC
    /// real no entrega ninguna IRQ de prioridad igual o menor mientras haya una en servicio, y
    /// aquí sí se entrega: basta con que el bit esté en IRR. Un ISR que hace `sti` pronto —lo
    /// normal, para no perder otras IRQ— puede recibir OTRA VEZ la misma y reentrarse; si conmuta
    /// a una pila privada (el ISR de timer de un juego DOS/4GW lo hace con `lss esp,[…]`), la segunda
    /// entrada pisa la pila de la primera.
    ///
    /// El fix es de tres líneas (calcular el bit de mayor prioridad en `_masterIsr` y no bajar de
    /// ahí) y se PROBÓ: **no resuelve** el fallo no determinista de ese juego que lo motivó. Se
    /// revirtió por eso — toca núcleo compartido con los 21 juegos, no está probado en ninguno de
    /// ellos y no compraba nada. Antes de meterlo hace falta red propia y prueba manual.
    /// </summary>
    public bool AckNext(out int irq)
    {
      lock (_lock)
      {
        for (int i = 0; i < 8; i++)
        {
            int bit = 1 << i;
            if (i == CascadeIrq) continue; // la cascada se resuelve vía esclavo abajo
            if ((_masterIrr & bit) != 0 && (_masterImr & bit) == 0)
            {
                _masterIrr &= (byte)(~bit & 0xFF);
                _masterIsr |= (byte)bit;
                irq = i; return true;
            }
        }
        // Esclavo (IRQ8-15) si la cascada (IRQ2) está pedida y no enmascarada
        if ((_masterImr & (1 << CascadeIrq)) == 0)
            for (int i = 0; i < 8; i++)
            {
                int bit = 1 << i;
                if ((_slaveIrr & bit) != 0 && (_slaveImr & bit) == 0)
                {
                    _slaveIrr &= (byte)(~bit & 0xFF);
                    _slaveIsr |= (byte)bit;
                    if (_slaveIrr == 0) _masterIrr &= (byte)(~(1 << CascadeIrq) & 0xFF);
                    irq = 8 + i; return true;
                }
            }
        irq = -1; return false;
      }
    }

    /// <summary>Lee el puerto del PIC (0x20, 0x21, 0xA0, 0xA1).</summary>
    public byte In(ushort port)
    {
      lock (_lock)
      {
        switch (port)
        {
            case 0x20:
                return _masterReadIsr ? _masterIsr : _masterIrr;
            case 0x21:
                return _masterImr;
            case 0xA0:
                return _slaveReadIsr ? _slaveIsr : _slaveIrr;
            case 0xA1:
                return _slaveImr;
        }
        return 0xFF;
      }
    }

    /// <summary>Escribe al puerto del PIC.</summary>
    public void Out(ushort port, byte v)
    {
      lock (_lock)
      {
        switch (port)
        {
            case 0x20: // OCW2/ICW1 maestro
                if ((v & 0x10) != 0)
                {
                    // ICW1 — inicio de secuencia de inicialización
                    _masterIcw = 1;
                    _masterImr = 0;
                    _masterIsr = 0;
                    _masterIrr = 0;
                }
                else if ((v & 0x18) == 0x08)
                {
                    // OCW3
                    _masterReadIsr = (v & 0x03) == 0x03;
                }
                else
                {
                    // OCW2 — EOI
                    if ((v & 0xE0) == 0x20) Eoi(-1);           // EOI no-específico
                    else if ((v & 0xE0) == 0x60) Eoi(v & 7);   // EOI específico
                }
                break;

            case 0x21: // ICW2/3/4 o OCW1 maestro
                if (_masterIcw == 1)      { _masterBase = (byte)(v & 0xF8); _masterIcw = 2; }
                else if (_masterIcw == 2) { _masterIcw = 3; }           // ICW3 (cascade map) — ignorar
                else if (_masterIcw == 3) { _masterIcw = 0; }           // ICW4 — ignorar
                else                      { _masterImr = v; }            // OCW1 = IMR
                break;

            case 0xA0: // OCW2/ICW1 esclavo
                if ((v & 0x10) != 0)
                {
                    _slaveIcw = 1;
                    _slaveImr = 0;
                    _slaveIsr = 0;
                    _slaveIrr = 0;
                }
                else if ((v & 0x18) == 0x08)
                {
                    _slaveReadIsr = (v & 0x03) == 0x03;
                }
                else
                {
                    if ((v & 0xE0) == 0x20) Eoi(8);            // EOI no-específico esclavo → irq8
                    else if ((v & 0xE0) == 0x60) Eoi(8 + (v & 7));
                }
                break;

            case 0xA1: // ICW2/3/4 o OCW1 esclavo
                if (_slaveIcw == 1)      { _slaveBase = (byte)(v & 0xF8); _slaveIcw = 2; }
                else if (_slaveIcw == 2) { _slaveIcw = 3; }
                else if (_slaveIcw == 3) { _slaveIcw = 0; }
                else                     { _slaveImr = v; }
                break;
        }
      }
    }

    private static int BitScanForward(byte v)
    {
        for (int i = 0; i < 8; i++)
            if ((v & (1 << i)) != 0) return i;
        return 0;
    }
}
