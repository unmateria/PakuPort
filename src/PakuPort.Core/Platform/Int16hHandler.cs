namespace PakuPort.Core.Platform;

/// <summary>
/// INT 16h — servicios de teclado BIOS.
///
/// AH=00/10: Read key (blocking) — espera hasta que haya tecla y la devuelve en AX.
/// AH=01/11: Peek key (non-blocking) — ZF=0 si hay tecla (AX=scancode+ascii), ZF=1 si no.
/// AH=02/12: Get shift flags — AL=0 (stub; TODO: leer flags de la BDA 0x40:0x17).
/// AH=05:    Push key to buffer — AL=0 si éxito, AL=1 si buffer lleno.
///
/// TODO de consistencia con BDA:
///   Actualmente este handler mantiene un buffer privado (_keyBuffer) independiente
///   del buffer circular de la BDA (BiosDataArea, 0x40:0x1A-0x3D). Cuando la BDA esté
///   disponible en DosPlatform, los métodos Read/Peek deberían usar p.Bda.PopKey/PeekKey
///   y AH=05 debería usar p.Bda.PushKey, para que tanto el código del juego que llama a
///   INT 16h COMO el código que lee la BDA directamente (mov ax, [0x40:0x1A]) vean el
///   mismo buffer. El buffer privado actual funciona correctamente para juegos que solo
///   usan INT 16h.
///
/// Adaptado de dosbox-x src/ints/bios_keyboard.cpp — INT16_Handler.
/// </summary>
public sealed class Int16hHandler
{
    private readonly object _lock = new();
    private readonly Queue<ushort> _keyBuffer = new();
    private readonly System.Threading.ManualResetEventSlim _keyReady = new(false);

    private DosPlatform? _platform;

    /// <summary>
    /// Headless discovery mode. When true, the blocking read (AH=00/10) never waits:
    /// with an empty buffer it returns "no key" (AX=0) instead of blocking. Set ONLY by
    /// the export discovery probe, which runs on a finite instruction budget and must
    /// terminate — no host thread feeds keys there, so a real blocking read would deadlock
    /// with flat CPU. The real host leaves this false and keeps the legitimate blocking read.
    /// </summary>
    public bool NonBlockingRead { get; set; }

    public void Register(DosPlatform p)
    {
        _platform = p;
        p.Int16 = this;
        p.RegisterInt(0x16, Handle);
    }

    /// <summary>Non-blocking peek of the next key (scancode&lt;&lt;8|ascii). False if empty.
    /// Used by INT 21h console input so it reads the SAME source as the window.</summary>
    public bool TryPeek(out ushort key)
    {
        lock (_lock)
        {
            if (_keyBuffer.Count > 0) { key = _keyBuffer.Peek(); return true; }
            key = 0; return false;
        }
    }

    /// <summary>Non-blocking pop of the next key. False if empty.</summary>
    public bool TryPop(out ushort key)
    {
        lock (_lock)
        {
            if (_keyBuffer.Count > 0)
            {
                key = _keyBuffer.Dequeue();
                if (_keyBuffer.Count == 0) _keyReady.Reset();
                return true;
            }
            key = 0; return false;
        }
    }

    /// <summary>
    /// Inserta una tecla en el buffer. Llamado por el host de render al detectar pulsación.
    /// El parámetro es (scancode &lt;&lt; 8) | ascii — mismo formato que la BDA.
    /// </summary>
    public void EnqueueKey(byte scancode, byte ascii)
    {
        InputTape.RecKey(scancode, ascii);
        lock (_lock)
        {
            _keyBuffer.Enqueue((ushort)((scancode << 8) | ascii));
            _keyReady.Set();
        }
    }

    /// <summary>PAKU_INT16_TRACE=1: cada servicio de teclado del BIOS con lo que devuelve. Es la
    /// forma de saber si un juego NO lee por aqui (muchos instalan su propia ISR del 09h).</summary>
    private static readonly bool _int16Trace = Environment.GetEnvironmentVariable("PAKU_INT16_TRACE") == "1";
    private static readonly bool _parkLegacy = Environment.GetEnvironmentVariable("PAKU_INT16_PARK") == "1";

    /// <summary>Blocking read for the DOS console services (INT 21h AH=01/07/08), which in real
    /// DOS wait for a key exactly like INT 16h AH=00. Same wait loop as AH=00: short parks with
    /// the machine polled in between, so the PIT, the mouse and the CRT keep running.</summary>
    public ushort ReadBlocking(Cpu cpu, Memory mem)
    {
        while (true)
        {
            if (TryPop(out ushort key)) return key;
            if (NonBlockingRead) return 0;
            _keyReady.Wait(2);
            if (_platform != null) _platform.MachineTimer.Poll(_platform, cpu, mem);
        }
    }

    private void Handle(Cpu cpu, Memory mem)
    {
        if (_int16Trace) Console.Error.WriteLine($"[INT16] AH={cpu.AH:X2} entry");
        switch (cpu.AH)
        {
            // ── AH=00/10: Read key (blocking) ─────────────────────────────────────────────
            // dosbox-x bios_keyboard.cpp: case 0x00/0x10 — bloquea hasta que haya tecla,
            // la extrae del buffer y la devuelve en AX (AH=scancode, AL=ascii).
            case 0x00:
            case 0x10:
                while (true)
                {
                    lock (_lock)
                    {
                        if (_keyBuffer.Count > 0)
                        {
                            cpu.AX = _keyBuffer.Dequeue();
                            if (_keyBuffer.Count == 0) _keyReady.Reset();
                            if (_int16Trace) Console.Error.WriteLine($"[INT16] AH=00/10 -> AX={cpu.AX:X4}");
                            return;
                        }
                        // Headless discovery probe: never block on a key so the
                        // budget-limited run terminates instead of deadlocking (flat CPU).
                        if (NonBlockingRead) { cpu.AX = 0; return; }
                        _keyReady.Reset();
                    }
                    // Real hardware keeps running while the BIOS spins in the INT16 wait: the
                    // PIT keeps ticking (IRQ0/game clock), the mouse ISR keeps firing (games
                    // service mouse input inside modal key waits through the INT33 user
                    // callback — one game's facility selector), and the CRT keeps scanning VRAM out
                    // (the finished menu gets presented). Parking the thread froze the whole
                    // machine: clicks were starved (the user's input.log shows 11 samples at
                    // one frozen TickCount) and the menu stayed unpresented when the pre-park
                    // capture fell inside the 10 ms throttle. Park in short slices and poll
                    // the machine between them. PAKU_INT16_PARK=1 reverts (A/B).
                    if (_parkLegacy)
                    {
                        _platform?.Video.CaptureFrontBuffer();
                        _keyReady.Wait();
                    }
                    else
                    {
                        _keyReady.Wait(2);
                        if (_platform != null) _platform.MachineTimer.Poll(_platform, cpu, mem);
                    }
                }

            // ── AH=01/11: Check for key (non-blocking) ────────────────────────────────────
            // dosbox-x bios_keyboard.cpp: case 0x01/0x11 — ZF=0 si hay tecla (peek sin consumir).
            case 0x01:
            case 0x11:
                lock (_lock)
                {
                    if (_keyBuffer.Count > 0)
                    {
                        cpu.AX = _keyBuffer.Peek();
                        cpu.ZF = false;
                        if (_int16Trace) Console.Error.WriteLine($"[INT16] AH=01/11 -> AX={cpu.AX:X4} ZF=0");
                    }
                    else
                    {
                        cpu.ZF = true;
                        if (_int16Trace) Console.Error.WriteLine("[INT16] AH=01/11 -> ZF=1 (vacio)");
                    }
                }
                break;

            // ── AH=02: Get shift flags ───────────────────────────────────────────────────
            // dosbox-x bios_keyboard.cpp: case 0x02 — reads BDA 0x40:0x17 (BIOS_KEYBOARD_FLAGS1)
            case 0x02:
                cpu.AL = _platform?.Bda?.KeyboardFlags1 ?? 0;
                break;

            // ── AH=03: Set typematic rate — bios_keyboard.cpp ────────────────────────────
            case 0x03:
                break;

            // ── AH=12: Get extended shift flags ──────────────────────────────────────────
            // dosbox-x bios_keyboard.cpp: case 0x12 — AL=flags1, AH=flags2+flags3 bits
            case 0x12:
                cpu.AL = _platform?.Bda?.KeyboardFlags1 ?? 0;
                cpu.AH = _platform?.Bda?.KeyboardFlags2 ?? 0;
                break;

            // ── AH=05: Push key to keyboard buffer ────────────────────────────────────────
            // dosbox-x bios_keyboard.cpp: case 0x05 — inserta scancode (CH) + ascii (CL) en el buffer.
            // AL=0: éxito, AL=1: buffer lleno.
            case 0x05:
            {
                ushort entry = (ushort)((cpu.CH << 8) | cpu.CL);
                lock (_lock)
                {
                    if (_keyBuffer.Count < 15) // buffer circular de 16 entradas (15 utilizables)
                    {
                        _keyBuffer.Enqueue(entry);
                        _keyReady.Set();
                        cpu.AL = 0; // éxito
                    }
                    else
                    {
                        cpu.AL = 1; // buffer lleno
                    }
                }
                break;
            }
        }
    }
}
