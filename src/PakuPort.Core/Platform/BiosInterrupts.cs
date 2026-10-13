namespace PakuPort.Core.Platform;

/// <summary>
/// Handlers BIOS/DOS por defecto para las interrupciones de bajo nivel que no tienen
/// fichero propio. Se registran todos vía BiosInterrupts.Register(DosPlatform).
///
/// Cada handler cita el fichero DOSBox-X del que se adaptó:
///   - INT 08/09/11/12/15/1Ch → src/ints/bios.cpp
///   - INT 09h scancodes      → src/ints/bios_keyboard.cpp
///   - INT 13h                → src/ints/bios_disk.cpp
///   - INT 20/23/24/28/2Fh   → src/dos/dos.cpp
///
/// NOTA: este fichero depende de la API de Platform/Machine/ que añade otro agente.
/// Concretamente asume que DosPlatform expone:
///   BiosDataArea Bda, Pic Pic, Pit Pit, Cmos Cmos, KeyboardController Keyboard, Ivt Ivt
///   void FireInterrupt(byte n, Cpu c, Memory m)
/// El código es sintácticamente correcto pero no compilará hasta que esa API exista.
/// </summary>
public static class BiosInterrupts
{
    /// <summary>PakuPort se presenta como host DPMI en <c>INT 2Fh AX=1687h</c>. Lo enciende el
    /// host exportado desde <c>game.json "dpmiHost": true</c>; por defecto NO, que es lo que ven
    /// all the real-mode games it was tested with.
    ///
    /// Es la puerta de la clase de juegos con EXTENSOR EMBEBIDO (a 3D DOS/4GW-class game and similar): su stub de
    /// modo real pregunta primero por un host DPMI y sólo si no lo hay se pone a manejar VCPI o a
    /// tocar CR0 y las tablas del 386 por su cuenta — cosa que aquí no tiene sentido, porque
    /// PakuPort no emula un 386 con MMU: lo SUSTITUYE, igual que sustituye a DOS/4GW.</summary>
    public static bool DpmiHostDisponible;

    /// <summary>Memoria extendida que declara el BIOS (INT 15h AH=88h) y respalda AH=87h. 8 MiB
    /// es lo que ve un AT bien equipado de la época y de sobra para los extensores de 16 bits,
    /// que direccionan como mucho 16 MiB. Se reserva PEREZOSAMENTE, al primer servicio que la
    /// pida, así que un juego que no la use no paga ni un byte.</summary>
    public const int MemoriaExtendidaBytes = 8 * 1024 * 1024;

    // ── tabla scancode → (scancode+ASCII) por estado de modificadores ────────────────────
    // Portada LITERAL de dosbox-x src/ints/bios_keyboard.cpp scan_to_scanascii[], columnas
    // normal / shift / control / alt (las dos de kana son del AX japonés y no se portan).
    // Índice = scancode IBM; 0x0000 = esa combinación no genera entrada de teclado.
    // Antes aquí sólo estaba la columna `normal` y los modificadores se improvisaban a mano
    // (mayúsculas por char.ToUpper y Ctrl por `letra & 0x1F`), así que Alt+tecla no existía,
    // Ctrl sobre lo que no fuera una letra daba el carácter sin tocar, y las combinaciones de
    // las teclas de función quedaban todas en su valor sin modificar.
    private static readonly ushort[,] ScanTbl =
    {
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 00 unused
        { 0x011b, 0x011b, 0x011b, 0x01f0 },     // 01 Esc
        { 0x0231, 0x0221, 0x0000, 0x7800 },     // 02 1!
        { 0x0332, 0x0340, 0x0300, 0x7900 },     // 03 2@
        { 0x0433, 0x0423, 0x0000, 0x7a00 },     // 04 3#
        { 0x0534, 0x0524, 0x0000, 0x7b00 },     // 05 4$
        { 0x0635, 0x0625, 0x0000, 0x7c00 },     // 06 5%
        { 0x0736, 0x075e, 0x071e, 0x7d00 },     // 07 6^
        { 0x0837, 0x0826, 0x0000, 0x7e00 },     // 08 7&
        { 0x0938, 0x092a, 0x0000, 0x7f00 },     // 09 8*
        { 0x0a39, 0x0a28, 0x0000, 0x8000 },     // 0a 9(
        { 0x0b30, 0x0b29, 0x0000, 0x8100 },     // 0b 0)
        { 0x0c2d, 0x0c5f, 0x0c1f, 0x8200 },     // 0c -_
        { 0x0d3d, 0x0d2b, 0x0000, 0x8300 },     // 0d =+
        { 0x0e08, 0x0e08, 0x0e7f, 0x0ef0 },     // 0e Backspace
        { 0x0f09, 0x0f00, 0x9400, 0x0000 },     // 0f Tab
        { 0x1071, 0x1051, 0x1011, 0x1000 },     // 10 Q
        { 0x1177, 0x1157, 0x1117, 0x1100 },     // 11 W
        { 0x1265, 0x1245, 0x1205, 0x1200 },     // 12 E
        { 0x1372, 0x1352, 0x1312, 0x1300 },     // 13 R
        { 0x1474, 0x1454, 0x1414, 0x1400 },     // 14 T
        { 0x1579, 0x1559, 0x1519, 0x1500 },     // 15 Y
        { 0x1675, 0x1655, 0x1615, 0x1600 },     // 16 U
        { 0x1769, 0x1749, 0x1709, 0x1700 },     // 17 I
        { 0x186f, 0x184f, 0x180f, 0x1800 },     // 18 O
        { 0x1970, 0x1950, 0x1910, 0x1900 },     // 19 P
        { 0x1a5b, 0x1a7b, 0x1a1b, 0x1af0 },     // 1a [{
        { 0x1b5d, 0x1b7d, 0x1b1d, 0x1bf0 },     // 1b ]}
        { 0x1c0d, 0x1c0d, 0x1c0a, 0x0000 },     // 1c Enter
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 1d L Ctrl
        { 0x1e61, 0x1e41, 0x1e01, 0x1e00 },     // 1e A
        { 0x1f73, 0x1f53, 0x1f13, 0x1f00 },     // 1f S
        { 0x2064, 0x2044, 0x2004, 0x2000 },     // 20 D
        { 0x2166, 0x2146, 0x2106, 0x2100 },     // 21 F
        { 0x2267, 0x2247, 0x2207, 0x2200 },     // 22 G
        { 0x2368, 0x2348, 0x2308, 0x2300 },     // 23 H
        { 0x246a, 0x244a, 0x240a, 0x2400 },     // 24 J
        { 0x256b, 0x254b, 0x250b, 0x2500 },     // 25 K
        { 0x266c, 0x264c, 0x260c, 0x2600 },     // 26 L
        { 0x273b, 0x273a, 0x0000, 0x27f0 },     // 27 ;:
        { 0x2827, 0x2822, 0x0000, 0x28f0 },     // 28 '"
        { 0x2960, 0x297e, 0x0000, 0x29f0 },     // 29 `~
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 2a L Shift
        { 0x2b5c, 0x2b7c, 0x2b1c, 0x2bf0 },     // 2b backslash
        { 0x2c7a, 0x2c5a, 0x2c1a, 0x2c00 },     // 2c Z
        { 0x2d78, 0x2d58, 0x2d18, 0x2d00 },     // 2d X
        { 0x2e63, 0x2e43, 0x2e03, 0x2e00 },     // 2e C
        { 0x2f76, 0x2f56, 0x2f16, 0x2f00 },     // 2f V
        { 0x3062, 0x3042, 0x3002, 0x3000 },     // 30 B
        { 0x316e, 0x314e, 0x310e, 0x3100 },     // 31 N
        { 0x326d, 0x324d, 0x320d, 0x3200 },     // 32 M
        { 0x332c, 0x333c, 0x0000, 0x33f0 },     // 33 ,<
        { 0x342e, 0x343e, 0x0000, 0x34f0 },     // 34 .>
        { 0x352f, 0x353f, 0x0000, 0x35f0 },     // 35 /?
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 36 R Shift
        { 0x372a, 0x372a, 0x9600, 0x37f0 },     // 37 Keypad *
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 38 L Alt
        { 0x3920, 0x3920, 0x3920, 0x3920 },     // 39 Space
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 3a Caps Lock
        { 0x3b00, 0x5400, 0x5e00, 0x6800 },     // 3b F1
        { 0x3c00, 0x5500, 0x5f00, 0x6900 },     // 3c F2
        { 0x3d00, 0x5600, 0x6000, 0x6a00 },     // 3d F3
        { 0x3e00, 0x5700, 0x6100, 0x6b00 },     // 3e F4
        { 0x3f00, 0x5800, 0x6200, 0x6c00 },     // 3f F5
        { 0x4000, 0x5900, 0x6300, 0x6d00 },     // 40 F6
        { 0x4100, 0x5a00, 0x6400, 0x6e00 },     // 41 F7
        { 0x4200, 0x5b00, 0x6500, 0x6f00 },     // 42 F8
        { 0x4300, 0x5c00, 0x6600, 0x7000 },     // 43 F9
        { 0x4400, 0x5d00, 0x6700, 0x7100 },     // 44 F10
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 45 Num Lock
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 46 Scroll Lock
        { 0x4700, 0x4737, 0x7700, 0x0007 },     // 47 Home / 7
        { 0x4800, 0x4838, 0x8d00, 0x0008 },     // 48 Up / 8
        { 0x4900, 0x4939, 0x8400, 0x0009 },     // 49 PgUp / 9
        { 0x4a2d, 0x4a2d, 0x8e00, 0x4af0 },     // 4a Keypad -
        { 0x4b00, 0x4b34, 0x7300, 0x0004 },     // 4b Left / 4
        { 0x4cf0, 0x4c35, 0x8f00, 0x0005 },     // 4c Center / 5
        { 0x4d00, 0x4d36, 0x7400, 0x0006 },     // 4d Right / 6
        { 0x4e2b, 0x4e2b, 0x9000, 0x4ef0 },     // 4e Keypad +
        { 0x4f00, 0x4f31, 0x7500, 0x0001 },     // 4f End / 1
        { 0x5000, 0x5032, 0x9100, 0x0002 },     // 50 Down / 2
        { 0x5100, 0x5133, 0x7600, 0x0003 },     // 51 PgDn / 3
        { 0x5200, 0x5230, 0x9200, 0x0000 },     // 52 Ins / 0
        { 0x5300, 0x532e, 0x9300, 0x0000 },     // 53 Del / .
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 54
        { 0x0000, 0x0000, 0x0000, 0x0000 },     // 55
        { 0x565c, 0x567c, 0x0000, 0x0000 },     // 56 102-key backslash
        { 0x8500, 0x8700, 0x8900, 0x8b00 },     // 57 F11
        { 0x8600, 0x8800, 0x8a00, 0x8c00 },     // 58 F12
    };

    /// <summary>
    /// Registra todos los handlers de bajo nivel en la plataforma.
    /// Llamar desde PlatformFactory.CreateFull() después de crear los handlers de nivel superior.
    /// </summary>
    public static void Register(DosPlatform p)
    {
        // Adaptado de dosbox-x src/ints/bios.cpp — función BIOS_SetupTimerIRQ
        p.RegisterInt(0x08, (cpu, mem) => HandleInt08(p, cpu, mem));

        // Adaptado de dosbox-x src/ints/bios_keyboard.cpp — función BIOS_SetupKeyboard
        p.RegisterInt(0x09, (cpu, mem) => HandleInt09(p, cpu, mem));

        // IRQ3-7 por defecto: solo EOI (bios.cpp — handlers de default stubs)
        p.RegisterInt(0x0B, (cpu, mem) => p.Pic.Eoi(3));
        p.RegisterInt(0x0C, (cpu, mem) => p.Pic.Eoi(4));
        p.RegisterInt(0x0D, (cpu, mem) => p.Pic.Eoi(5));
        p.RegisterInt(0x0E, (cpu, mem) => p.Pic.Eoi(6));
        p.RegisterInt(0x0F, (cpu, mem) => p.Pic.Eoi(7));

        // Adaptado de dosbox-x src/ints/bios.cpp — INT11_Handler
        p.RegisterInt(0x11, (cpu, mem) => { cpu.AX = p.Bda.EquipmentWord; });

        // Adaptado de dosbox-x src/ints/bios.cpp — INT12_Handler
        p.RegisterInt(0x12, (cpu, mem) => { cpu.AX = p.Bda.MemorySizeKB; });

        // Adaptado de dosbox-x src/ints/bios_disk.cpp — INT13_Handler
        p.RegisterInt(0x13, (cpu, mem) => HandleInt13(cpu, mem));

        // Adaptado de dosbox-x src/ints/bios.cpp — INT15_Handler
        p.RegisterInt(0x15, (cpu, mem) => HandleInt15(p, cpu, mem));

        // INT 1Ch — user timer tick: no-op por defecto.
        // El juego suele enganchar este vector. Si nadie lo engancha, simplemente retorna.
        // Adaptado de dosbox-x src/ints/bios.cpp — INT1C_Handler (empty stub)
        p.RegisterInt(0x1C, (cpu, mem) => { /* no-op — el juego lo engancha si quiere */ });

        // Adaptado de dosbox-x src/dos/dos.cpp — INT20_Handler
        p.RegisterInt(0x20, (cpu, mem) =>
        {
            // Terminate program (equivalente a INT 21h/00h)
            throw new ProgramExitException(0);
        });

        // Adaptado de dosbox-x src/dos/dos.cpp — INT23_Handler (Ctrl-C)
        p.RegisterInt(0x23, (cpu, mem) => { /* no-op: ignore Ctrl-C */ });

        // Adaptado de dosbox-x src/dos/dos.cpp — INT24_Handler (critical error)
        // AL=3 → "fail" (el manejador de error crítico por defecto devuelve fail)
        p.RegisterInt(0x24, (cpu, mem) => { cpu.AL = 3; });

        // Adaptado de dosbox-x src/dos/dos.cpp — INT28_Handler (DOS idle)
        p.RegisterInt(0x28, (cpu, mem) => { /* no-op: DOS idle hook */ });

        // INT 14h — Serial port BIOS — bios.cpp
        p.RegisterInt(0x14, (cpu, mem) =>
        {
            switch (cpu.AH)
            {
                case 0x00: // Initialize serial port
                    cpu.AX = 0x6000; // DSR+CTS set, no errors
                    break;
                case 0x01: // Write character
                    cpu.AH = 0x60; // DSR+CTS, no timeout
                    break;
                case 0x02: // Read character
                    cpu.AH = 0x80; // timeout (no data)
                    cpu.AL = 0x00;
                    break;
                case 0x03: // Get status
                    cpu.AX = 0x6000; // DSR+CTS
                    break;
            }
        });

        // INT 17h — Printer BIOS — bios.cpp
        p.RegisterInt(0x17, (cpu, mem) =>
        {
            switch (cpu.AH)
            {
                case 0x00: // Print character
                    cpu.AH = 0xD0; // selected + not busy + acknowledged
                    break;
                case 0x01: // Initialize printer port
                    cpu.AH = 0xD0;
                    break;
                case 0x02: // Get printer status
                    cpu.AH = 0xD0;
                    break;
            }
        });

        // Adaptado de dosbox-x src/dos/dos.cpp y win_misc — INT2F_Handler (multiplex)
        p.RegisterInt(0x2F, (cpu, mem) => HandleInt2F(cpu, mem));

        // INT 67h — EMS (Expanded Memory Specification)
        // Adaptado de dosbox-x src/ints/ems.cpp — EMS no instalado, devolver AH=84h.
        p.RegisterInt(0x67, (cpu, mem) =>
        {
            switch (cpu.AH)
            {
                case 0x40: // Get EMS status
                case 0x41: // Get page frame segment
                case 0x42: // Get total/free pages
                case 0x43: // Allocate handle+pages
                case 0x44: // Map page
                case 0x45: // Release handle
                case 0x46: // Get EMM version
                case 0x4B: // Get handle count
                case 0x4C: // Get pages for handle
                case 0x4D: // Get pages for all handles
                case 0x50: // Map multiple pages
                case 0x51: // Reallocate pages
                case 0x53: // Set/get handle name
                case 0x54: // Handle functions
                case 0x57: // Move/exchange memory
                case 0x58: // Mappable region info
                case 0x59: // Hardware info
                case 0x5A: // Allocate pages (OS)
                    cpu.AH = 0x84; // function not supported / EMM not installed
                    break;
                default:
                    cpu.AH = 0x84;
                    break;
            }
        });

        // INT 2Ah — DOS network / critical section — no-op
        p.RegisterInt(0x2A, (cpu, mem) => { });
    }

    // ── INT 08h — IRQ0 timer tick por defecto ─────────────────────────────────────────────
    // Adaptado de dosbox-x src/ints/bios.cpp función BIOS_IRQ0_Handler / INT8_Handler.
    // Incrementa el contador de ticks en la BDA (0x40:0x6C). Si desborda la cuenta de un día
    // completo (0x1800B0 ticks), resetea a 0 y pone el flag de medianoche.
    // Luego llama INT 1Ch (user timer tick) si está instalado.
    // Finalmente envía EOI al PIC maestro para permitir nuevas IRQ0.
    private static void HandleInt08(DosPlatform p, Cpu cpu, Memory mem)
    {
        // NO incrementar BDA.TimerTicks aquí: la fuente única de tiempo es
        // MachineTimer.Advance (hilo del host), que ya incrementa la BDA al levantar
        // cada IRQ0. Con el PIC entregando IRQ0 (IMR post-BIOS), incrementar también
        // aquí duplicaba el reloj (juego a x2). En el PC real lo hace la ISR BIOS,
        // pero en PakuPort el host es el dueño del tiempo simulado.

        // Llamar INT 1Ch (user timer tick hook). Por diseño FireInterrupt invoca el handler
        // que esté instalado en la IVT; si el juego no lo engancha, llega al no-op de arriba.
        // NOTA: para no reentrar recursivamente solo disparamos si hay un handler real del juego.
        // Por ahora invocamos el handler registrado directamente — FireInterrupt lo haría con
        // la IVT completa cuando esté disponible.
        p.FireInterrupt(0x1C, cpu, mem);

        // Cuenta atrás del motor de disquete (dosbox-x bios.cpp INT8_Handler, líneas 6651-6655):
        // en cada tick decrementa 0x40:0x40 (parando en cero en máquinas EGA/VGA) y limpia los
        // bits de unidad activa de 0x40:0x3F cuando llega a cero. No es cosmético: los juegos lo
        // usan como RETARDO DE UN TICK sin tocar el PIT — escriben N en 0x40:0x40 y giran hasta
        // que cambia (un juego, al saltar la intro: `mov es:[440],1` + `cmp/je` en bucle). Sin el
        // decremento ese bucle no sale nunca.
        byte motor = p.Bda.DiskMotorTimeout;
        if (motor != 0) p.Bda.DiskMotorTimeout = (byte)(motor - 1);
        if (motor == 1) p.Bda.DriveRunning = (byte)(p.Bda.DriveRunning & 0xF0);

        // EOI a IRQ0 (maestro)
        p.Pic.Eoi(0);
    }

    // ── INT 09h — IRQ1 teclado por defecto ────────────────────────────────────────────────
    // Adaptado de dosbox-x src/ints/bios_keyboard.cpp función BIOS_IRQ1_Handler.
    // Lee el scancode del 8042 (puerto 0x60 vía KeyboardController), traduce a scancode+ASCII
    // usando la tabla scan_to_scanascii y mete la entrada en el buffer circular de la BDA.
    private static void HandleInt09(DosPlatform p, Cpu cpu, Memory mem)
    {
        // Leer scancode del controlador de teclado (equivalente a: IN AL, 0x60)
        byte scancode = p.Keyboard.ReadScancode();

        // ── gancho INT 15h AH=4Fh (keyboard intercept del PC/AT) ─────────────────────────
        // El BIOS ofrece CADA scancode a INT 15h con AH=4Fh y CF=1 antes de traducirlo: el
        // driver instalado ahí puede devolver otro scancode en AL o descartarlo con CF=0.
        // Es el punto de enganche estándar para quien no quiere robar el INT 9 —
        // un juego de prueba instala ahí su teclado entero (vector medido en su memoria viva:
        // INT 15h -> 4375:00B4, con `cmp ah,4Fh` literal, mientras el INT 9 sigue en el stub
        // del BIOS), así que sin esta llamada el juego no recibe una sola tecla y no se ve en
        // ninguna traza: no lee el 0x60, no llama a INT 16h y no toca la BDA.
        // Portado de dosbox-x src/cpu/callback.cpp, case CB_IRQ1 (el cuerpo real del INT 9):
        //   push ax / in al,60h / mov ah,4Fh / stc / int 15h / jnc skip / <handler> / skip:
        // (el opcode del salto es 73h = JNC: con CF=0 el BIOS se salta su propio manejador).
        // Sólo AX y FLAGS se salvan aquí porque es lo único que preserva el BIOS real; el
        // driver conserva los demás registros por contrato.
        {
            ushort savedAx = cpu.AX;
            ushort savedFlags = cpu.FlagsWord();
            cpu.AH = 0x4F;
            cpu.AL = scancode;
            cpu.CF = true;                 // por defecto: procesar la tecla
            p.FireInterrupt(0x15, cpu, mem);
            bool procesar = cpu.CF;
            scancode = cpu.AL;             // el driver puede haberlo traducido
            cpu.AX = savedAx;
            cpu.SetFlagsWord(savedFlags);
            if (!procesar) { p.Pic.Eoi(1); return; }
        }

        // Modificadores: make (0x2A/0x36 shift, 0x1D ctrl, 0x38 alt) y break (|0x80).
        // Mantener los bits de la BDA KeyboardFlags1 para que INT16h AH=02/12 y el
        // código del juego que lee 0x40:0x17 vean el estado correcto.
        byte flags = p.Bda.KeyboardFlags1;
        switch (scancode)
        {
            case 0x2A: case 0x36: p.Bda.KeyboardFlags1 = (byte)(flags | 0x02); p.Pic.Eoi(1); return; // shift down (bit1=izq, tratamos ambos)
            case 0xAA: case 0xB6: p.Bda.KeyboardFlags1 = (byte)(flags & ~0x03); p.Pic.Eoi(1); return; // shift up
            case 0x1D: p.Bda.KeyboardFlags1 = (byte)(flags | 0x04); p.Pic.Eoi(1); return; // ctrl down
            case 0x9D: p.Bda.KeyboardFlags1 = (byte)(flags & ~0x04); p.Pic.Eoi(1); return; // ctrl up
            case 0x38: p.Bda.KeyboardFlags1 = (byte)(flags | 0x08); p.Pic.Eoi(1); return; // alt down
            case 0xB8: p.Bda.KeyboardFlags1 = (byte)(flags & ~0x08); p.Pic.Eoi(1); return; // alt up
            // Bloqueos: conmutan su bit de estado en el make y no generan carácter
            // (bios_keyboard.cpp: bit4 scroll, bit5 num, bit6 caps de 0x40:0x17).
            case 0x3A: p.Bda.KeyboardFlags1 = (byte)(flags ^ 0x40); p.Pic.Eoi(1); return; // caps lock
            case 0x45: p.Bda.KeyboardFlags1 = (byte)(flags ^ 0x20); p.Pic.Eoi(1); return; // num lock
            case 0x46: p.Bda.KeyboardFlags1 = (byte)(flags ^ 0x10); p.Pic.Eoi(1); return; // scroll lock
            case 0xBA: case 0xC5: case 0xC6: p.Pic.Eoi(1); return;                        // sus break codes
        }

        // Scancodes >= 0x80 son key-release; ignorarlos en el buffer (solo key-press)
        if (scancode < 0x80 && scancode < ScanTbl.GetLength(0))
        {
            bool shift  = (flags & 0x03) != 0;
            bool ctrl   = (flags & 0x04) != 0;
            bool alt    = (flags & 0x08) != 0;
            bool numLk  = (flags & 0x20) != 0;
            bool caps   = (flags & 0x40) != 0;
            // Bloque del teclado numérico: el dígito sale de la columna shift, y el BIOS lo
            // elige con shift XOR numlock (bios_keyboard.cpp:809, "both means off"). Alt sobre
            // el keypad no entrega tecla: el BIOS va acumulando el código ASCII que se teclea
            // con Alt+dígitos (BIOS_KEYBOARD_TOKEN), algo que aquí no se modela.
            bool keypad = scancode is >= 0x47 and <= 0x53 and not 0x4A and not 0x4E;
            ushort entry;
            if (alt)                                    entry = keypad ? (ushort)0 : ScanTbl[scancode, 3];
            else if (ctrl)                              entry = ScanTbl[scancode, 2];
            else if (keypad ? (shift ^ numLk) : shift)  entry = ScanTbl[scancode, 1];
            else                                        entry = ScanTbl[scancode, 0];
            // CapsLock invierte el shift, pero SOLO sobre letras (bios_keyboard.cpp:840-849).
            if (caps)
            {
                byte a = (byte)(entry & 0xFF);
                if (shift) { if (a is > 0x40 and < 0x5B) entry = ScanTbl[scancode, 0]; }
                else       { if (a is > 0x60 and < 0x7B) entry = ScanTbl[scancode, 1]; }
            }
            // La BDA almacena el par (ascii bajo, scancode alto) — así lo lee INT 16h.
            if (entry != 0x0000) p.Bda.PushKey(entry);
        }

        // EOI a IRQ1 (maestro)
        p.Pic.Eoi(1);
    }

    // ── INT 13h — Disco BIOS ───────────────────────────────────────────────────────────────
    // Adaptado de dosbox-x src/ints/bios_disk.cpp — BIOS_Disk_Handler / INT13_Handler.
    // Implementación mínima: los juegos que no acceden a disco directamente nunca llegan aquí.
    // Se garantiza que el emulador no se cuelgue. Las funciones críticas devuelven error/CF=1
    // con un código razonable para que los juegos que prueban la presencia de disco fallen
    // graciosamente en lugar de entrar en bucle.
    private static void HandleInt13(Cpu cpu, Memory mem)
    {
        switch (cpu.AH)
        {
            case 0x00: // Reset disk system — siempre OK
                // dosbox-x bios_disk.cpp: DISK_RESET — devuelve AH=0, CF=0
                cpu.AH = 0x00;
                cpu.CF = false;
                break;

            case 0x01: // Get last status
                // Stub: no hay error pendiente (AH=0)
                cpu.AH = 0x00;
                cpu.CF = false;
                break;

            case 0x02: // Read sectors
                // One test game's copy-protection: reads a sector 4 times and checks that
                // bytes 0,1,2 are 'A','B','C' and all reads are identical.
                // Write the expected signature into the buffer and return success.
            {
                int buf = Memory.Lin(cpu.ES, cpu.BX);
                int count = cpu.AL * 512;
                for (int i = 0; i < count && buf + i < mem.Raw.Length; i++)
                    mem.Raw[buf + i] = 0;
                if (count >= 3 && buf + 2 < mem.Raw.Length)
                {
                    mem.Raw[buf] = 0x41;     // 'A'
                    mem.Raw[buf + 1] = 0x42; // 'B'
                    mem.Raw[buf + 2] = 0x43; // 'C'
                }
                cpu.AH = 0x00;
                cpu.CF = false;
                break;
            }

            case 0x03: // Write sectors
                // STUB: sin imagen de disco → error
                // TODO: implementar escritura real cuando se monte imagen de disco.
                cpu.AH = 0x03; // write protected
                cpu.CF = true;
                break;

            case 0x08: // Get drive parameters
                // dosbox-x bios_disk.cpp: devuelve parámetros ficticios para drive virtual.
                // DL = número de drives duros (0 si no hay disco).
                // Stub seguro: sin drives de disco duro.
                cpu.AH = 0x00;
                cpu.CF = false;
                cpu.BX = 0x0000; // no disk type
                cpu.CX = 0x0000; // cylinders=0, sectors=0
                cpu.DH = 0x00;   // heads=0
                cpu.DL = 0x00;   // number of drives = 0
                break;

            case 0x15: // Get disk type
                // dosbox-x bios_disk.cpp: AH=0 → no drive, CF=0
                cpu.AH = 0x00;
                cpu.CF = false;
                break;

            default:
                // Función desconocida: AH=0x01 (invalid function), CF=1
                cpu.AH = 0x01;
                cpu.CF = true;
                break;
        }
    }

    // ── INT 15h — Misc BIOS ────────────────────────────────────────────────────────────────
    // Adaptado de dosbox-x src/ints/bios.cpp — INT15_Handler.
    private static void HandleInt15(DosPlatform p, Cpu cpu, Memory mem)
    {
        switch (cpu.AH)
        {
            case 0x24: // Enable/Disable A20 gate — bios.cpp
                switch (cpu.AL)
                {
                    case 0x00: // disable A20
                    case 0x01: // enable A20
                        cpu.AH = 0x00;
                        cpu.CF = false;
                        break;
                    case 0x02: // query A20 status
                        cpu.AL = 0x01; // A20 enabled
                        cpu.AH = 0x00;
                        cpu.CF = false;
                        break;
                    case 0x03: // get A20 support
                        cpu.BX = 0x03; // keyboard and port 92h
                        cpu.AH = 0x00;
                        cpu.CF = false;
                        break;
                    default:
                        cpu.AH = 0x86;
                        cpu.CF = true;
                        break;
                }
                break;

            case 0x4F: // Keyboard intercept (AT BIOS) — bios.cpp
                cpu.CF = true; // CF=1 = not intercepted, pass to BIOS
                break;

            case 0x83: // Set event wait interval — bios.cpp
                if (cpu.AL == 0x01)
                {
                    cpu.CF = false; // cancel — OK
                }
                else
                {
                    cpu.CF = false; // set — OK (we don't actually wait)
                }
                break;

            case 0x84: // Joystick support — bios.cpp
                if (cpu.DX == 0x0000)
                {
                    // Get button status: all buttons released
                    cpu.AL = 0xF0;
                    cpu.CF = false;
                }
                else if (cpu.DX == 0x0001)
                {
                    // Get joystick position: centered (128)
                    cpu.AX = 128; cpu.BX = 128;
                    cpu.CX = 0; cpu.DX = 0;
                    cpu.CF = false;
                }
                else
                    cpu.CF = true;
                break;

            case 0x86: // BIOS WAIT (microseconds en CX:DX) — bios.cpp
                cpu.AH = 0x00;
                cpu.CF = false;
                break;

            // Memoria extendida por BIOS. Antes AH=88h decía "no hay" y AH=87h contestaba OK sin
            // copiar nada, que es la peor combinación posible: quien pedía la copia se creía
            // servido y encontraba ceros. Un extensor de 16 bits (DOS/16M en varios juegos de
            // prueba) vive de estos dos servicios — mueve el programa arriba y ejecuta allí en
            // modo protegido —, y sin ellos aborta pidiendo que le declaren la memoria a mano
            // con SET DOS16M=.
            case 0x87: // Copy extended memory — bios.cpp
            {
                // ES:SI apunta a una mini-GDT de 6 descriptores; el 2º (offset 0x10) es el
                // origen y el 3º (0x18) el destino. CX = PALABRAS a copiar.
                uint gdt = (uint)Memory.Lin(cpu.ES, cpu.SI);
                uint Base(uint d) => mem.RdLin16(gdt + d + 2)
                                     | ((uint)mem.RdLin8(gdt + d + 4) << 16)
                                     | ((uint)mem.RdLin8(gdt + d + 7) << 24);
                uint org = Base(0x10), dst = Base(0x18);
                uint bytes = (uint)cpu.CX * 2;
                mem.EnableFlat(MemoriaExtendidaBytes);
                for (uint i = 0; i < bytes; i++) mem.WrLin8(dst + i, mem.RdLin8(org + i));
                cpu.AH = 0x00;
                cpu.CF = false;
                break;
            }

            case 0x88: // Get extended memory size in KB — bios.cpp
                mem.EnableFlat(MemoriaExtendidaBytes);
                cpu.AX = (ushort)(MemoriaExtendidaBytes / 1024);
                cpu.CF = false;
                break;

            case 0x91: // Interrupt complete — bios.cpp
                cpu.CF = false;
                break;

            case 0xC0: // Get system configuration — bios.cpp
            {
                // Write a minimal config table at F000:E6F5 (standard BIOS location)
                int tblAddr = Memory.Lin(0xF000, 0xE6F5);
                if (tblAddr + 15 < Memory.Size)
                {
                    mem.Raw[tblAddr + 0] = 0x08; mem.Raw[tblAddr + 1] = 0x00; // table length
                    mem.Raw[tblAddr + 2] = 0xFC; // model (AT)
                    mem.Raw[tblAddr + 3] = 0x01; // submodel
                    mem.Raw[tblAddr + 4] = 0x00; // BIOS revision
                    mem.Raw[tblAddr + 5] = 0x74; // feature byte 1 (ISA, RTC, keyboard intercept)
                    mem.Raw[tblAddr + 6] = 0x00; // feature byte 2
                    mem.Raw[tblAddr + 7] = 0x00; // feature byte 3
                    mem.Raw[tblAddr + 8] = 0x00; // feature byte 4
                    mem.Raw[tblAddr + 9] = 0x00; // feature byte 5
                }
                cpu.ES = 0xF000;
                cpu.BX = 0xE6F5;
                cpu.AH = 0x00;
                cpu.CF = false;
                break;
            }

            case 0xC1: // Get extended BIOS data area segment — bios.cpp
                cpu.AH = 0x86; // not supported
                cpu.CF = true;
                break;

            case 0xC2: // PS/2 pointing device — bios.cpp
                cpu.AH = 0x86;
                cpu.CF = true;
                break;

            default:
                cpu.AH = 0x86;
                cpu.CF = true;
                break;
        }
    }

    // ── INT 2Fh — Multiplex ────────────────────────────────────────────────────────────────
    // Adaptado de dosbox-x src/dos/dos.cpp y varios módulos de driver.
    // El objetivo es responder "no instalado" de forma segura a los sondeos más comunes
    // para que los juegos no entren en bucle esperando un driver que no está.
    private static void HandleInt2F(Cpu cpu, Memory mem)
    {
        switch (cpu.AX)
        {
            case 0x1600: // Windows enhanced mode check — AL=0 (no Windows), AH intacto
                // (verificado contra el oráculo: DOSBox deja AH=0x16; antes zerábamos AX)
                //
                // CON host DPMI declarado se contesta "modo mejorado 3.x", y no es un capricho:
                // un extensor de DOS elige camino con una lógica de TRES vías, y ésta es la
                // pregunta que la decide. Bajo un host que POSEE el modo protegido no puede
                // cambiarse de modo él —lo tiene prohibido— y usa DPMI; con EMM386 usa VCPI; y en
                // DOS pelado se lo monta con XMS y su propio `mov cr0,eax`. PakuPort es el primer
                // caso literalmente: hay un host (nosotros) que sirve DPMI y no hay ningún 386 con
                // MMU debajo que se pueda reprogramar. Decir "no hay nada" mandaba a un juego de prueba por
                // la tercera vía, a pedir XMS para habilitar la A20 y acabar en un cambio de modo
                // que aquí no existe. Va con `dpmiHost`, así que los juegos probados no lo ven.
                cpu.AL = DpmiHostDisponible ? (byte)0x03 : (byte)0x00;
                break;

            case 0x1605: // Windows enhanced mode boot broadcast — sin respuesta
            case 0x1606:
                break;

            case 0x4300: // XMS installation check
                // dosbox-x ems.cpp / himem: AL=0x80 → XMS instalado. Devolvemos AL=0 → no.
                cpu.AL = 0x00;
                break;

            case 0x4310: // XMS get entry point — solo si AX=4300 respondió instalado
                // STUB: CF=1 para indicar error
                cpu.CF = true;
                break;

            case 0x1687: // DPMI installation check (spec DPMI 0.9, la llamada canónica)
                // OPT-IN POR JUEGO (game.json "dpmiHost"), y no por prudencia abstracta: los 21
                // juegos probados reciben hoy "no hay host" y toman su camino de modo REAL,
                // que es sobre el que están validados. Un juego con extensor embebido que se
                // creyera que hay DPMI intentaría el cambio de modo y moriría donde antes iba.
                if (!DpmiHostDisponible) { cpu.AL = 0xFF; break; }
                // Contrato de la spec: AX=0 significa QUE SÍ hay host (al revés que casi todo lo
                // demás de INT 2Fh, donde 0 suele ser "no").
                cpu.AX = 0x0000;
                cpu.BX = 0x0001;   // bit 0: se admiten clientes de 32 bits
                cpu.CL = 0x03;     // tipo de CPU: 80386
                cpu.DH = 0x00;     // versión DPMI mayor
                cpu.DL = 90;       // menor (0.90)
                cpu.SI = 0x0000;   // párrafos de memoria privada que necesita el host: ninguno
                // Punto de entrada del CAMBIO DE MODO: el cliente hará un `call far` aquí.
                cpu.ES = Cpu.IvtStubSeg;
                cpu.DI = Cpu.DpmiSwitchOff;
                break;

            case 0x4B01: // DPMI installation check
            case 0x4B05:
                // AL=0 → no instalado
                cpu.AL = 0x00;
                break;

            case 0xB800: // VDISK / ramdisk check
            case 0xB809:
                // AL=0 → no instalado
                cpu.AL = 0x00;
                break;

            default:
                // Función desconocida: AL=0xFF → not installed / not handled
                // (convención estándar para INT 2Fh)
                // dosbox-x dos.cpp: LOG INT2F Unknown call
                cpu.AL = 0xFF;
                break;
        }
    }
}
