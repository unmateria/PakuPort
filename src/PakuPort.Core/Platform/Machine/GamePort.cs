namespace PakuPort.Core.Platform;

/// <summary>
/// Game port analógico (puerto 0x201). Protocolo real del hardware:
///   OUT 0x201      → dispara los monoestables (one-shot) de los 4 ejes.
///   IN 0x201       → bits 0-3: 1 mientras el condensador RC del eje no llega al umbral
///                    (tiempo proporcional a la posición del stick); bits 4-7: botones,
///                    activos a nivel BAJO (1 = suelto).
/// Los juegos miden la posición contando lecturas en bucle hasta que el bit cae
/// (p.ej. un juego de prueba: in/test/loopne). Aquí el "tiempo" se modela en número de
/// lecturas, determinista e independiente del budget de CPU.
/// El host alimenta X/Y/Buttons cada frame (p.ej. flechas del teclado → ejes).
/// Adaptado del comportamiento de dosbox-x src/hardware/joystick.cpp (modelo simplificado).
/// </summary>
public sealed class GamePort
{
    /// <summary>Posición del eje en "cuentas" de descarga RC (0-255, centro ≈ 128).</summary>
    public byte X = 128, Y = 128;

    /// <summary>Botones pulsados: bit0 = botón 1, bit1 = botón 2 (1 = pulsado).
    /// En la lectura del puerto salen invertidos (activo-bajo) en los bits 4-5.</summary>
    public byte Buttons;

    // Contadores de descarga en vuelo (cargados por Trigger, drenados por Read)
    private int _cx, _cy;

    /// <summary>OUT 0x201 — dispara los one-shot de los ejes.</summary>
    public void Trigger() { _cx = X; _cy = Y; }

    /// <summary>PAKU_NO_GAMEPORT=1 — sin tarjeta de juegos: 0xFF en toda lectura, como
    /// joystick.cpp:79-92 de DOSBox-X (`ret=0xff` y los bits solo se limpian dentro de
    /// `if (stick[n].enabled)`, que con joytype=none es falso).
    /// POR QUE EXISTE: emular el puerto SIEMPRE presente cambia lo que decide el juego.
    /// Un juego de prueba hace `in al,0x201 / neg al / test al,0x30`: con nuestro 0xF0 sale
    /// neg=0x10 y test != 0 → arranca en modo JOYSTICK y su teclado queda muerto en partida;
    /// con el 0xFF de una máquina sin tarjeta sale neg=0x01, test = 0 → modo teclado. El
    /// default NO cambia (el host mapea las flechas a los ejes y así se validó jugando),
    /// pero un arnés headless con autokeys necesita la máquina sin joystick.
    /// Mutable: game.json "gamePortAbsent": true lo hornea por juego (otro juego suma el
    /// joystick fantasma al teclado y cruza las direcciones). Nota apuntada para el
    /// futuro: lo FIEL a DOSBox sería ausente POR DEFECTO con opt-in por juego, pero eso
    /// obliga a volver a probar a mano los juegos validados con puerto presente.</summary>
    public static bool Absent =
        Environment.GetEnvironmentVariable("PAKU_NO_GAMEPORT") == "1";

    /// <summary>IN 0x201 — estado de ejes (bits 0-1) y botones (bits 4-5, activo-bajo).</summary>
    public byte Read()
    {
        if (Absent) return 0xFF;
        byte v = 0xF0;                       // botones sueltos, ejes 3-4 (2º stick) ya caídos
        if (_cx > 0) { _cx--; v |= 0x01; }   // eje X aún descargando
        if (_cy > 0) { _cy--; v |= 0x02; }   // eje Y aún descargando
        v &= (byte)~((Buttons & 0x0F) << 4); // botones pulsados → bit a 0
        return v;
    }
}
