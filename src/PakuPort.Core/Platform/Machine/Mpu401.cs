using System.Runtime.InteropServices;

namespace PakuPort.Core.Platform;

/// <summary>
/// MPU-401 en MODO UART, que es el único que usaron los juegos de DOS: el "intelligent mode" del
/// MPU original —con sus pistas, su metrónomo y su temporizador propio— prácticamente no lo tocó
/// nadie, porque las tarjetas compatibles de la época (Sound Blaster incluida) sólo traían el UART.
///
/// **Por qué existe** (2026-08-24). Antes, los puertos 0x330/0x331 devolvían dos constantes de
/// reposo sin ningún estado detrás. La rutina canónica para detectar un MPU-401 es mandar el
/// comando 0x3F (entrar en UART) y esperar el 0xFE de reconocimiento; como no llegaba nunca,
/// TODOS los juegos probados decidían que no hay tarjeta MIDI y se caían al OPL. Es decir: la
/// música General MIDI del catálogo del 92 al 97 —que es muchísima, y en muchos juegos es la
/// diferencia entre una banda sonora orquestal y cuatro pitidos FM— no se podía usar.
///
/// **A dónde va la música**: al sintetizador MIDI del sistema (winmm, `midiOutShortMsg`), que es
/// lo que hace DOSBox-X en Windows. No es una dependencia de terceros que pueda desaparecer:
/// forma parte de Windows desde hace décadas. Si no hay sintetizador, la salida se degrada a
/// silencio sin romper el juego, y se dice en el log.
///
/// Referencia: dosbox-x src/hardware/mpu401.cpp (MPU401_WriteCommand:640, estado del UART).
/// </summary>
public sealed class Mpu401
{
    /// <summary>Puerto de datos (base) y de estado/comando (base+1). 0x330 es la dirección de
    /// fábrica: la que anuncia la variable BLASTER que PakuPort le da al invitado (`P330`).</summary>
    public const ushort BasePort = 0x330;

    // Bits del registro de estado (base+1). Se leen NEGADOS, que es la parte que despista: un 0
    // significa "sí". DOSBox-X en reposo devuelve 0xBF = bit 7 puesto (no hay dato que leer) y
    // bit 6 a cero (listo para recibir). Este modelo SIEMPRE está listo para recibir —no hay
    // cola de salida que se llene, porque el sintetizador del sistema se traga los mensajes al
    // instante—, así que el bit 6 nunca se levanta.
    private const byte StatusInputEmpty = 0x80;   // bit 7: no hay nada que leer

    private readonly Queue<byte> _entrada = new();
    private bool _uart;
    /// <summary>PAKU_LOG_MIDI=1: la conversacion con el MPU-401 (entrada en modo UART, reset, y
    /// los comandos del intelligent mode que se reconocen sin modelar). Responde la pregunta de
    /// si un juego ha encontrado la tarjeta MIDI o se ha caido al OPL, que desde fuera suenan
    /// distinto pero no dicen por que.</summary>
    private readonly bool _log = Environment.GetEnvironmentVariable("PAKU_LOG_MIDI") == "1";

    /// <summary>Mensajes MIDI descartados por no estar modelados (SysEx). Aparecen en el informe
    /// de la partida como cualquier otro hueco: un juego que configura una MT-32 por SysEx y no
    /// suena como debe tiene que poder diagnosticarse sin adivinar.</summary>
    public static readonly List<string> NoModelado = new();

    private readonly SalidaMidi _salida = new();

    public void Reset()
    {
        _entrada.Clear();
        _uart = false;
        _salida.Silenciar();
    }

    public byte PortIn(ushort port)
    {
        if (port == BasePort)                       // datos
            return _entrada.Count > 0 ? _entrada.Dequeue() : (byte)0xFF;
        // estado: siempre listo para recibir; el bit de "hay dato" sólo si la cola tiene algo
        return (byte)(_entrada.Count > 0 ? 0x3F : StatusInputEmpty | 0x3F);
    }

    public void PortOut(ushort port, byte val)
    {
        if (port == BasePort) { EscribeDato(val); return; }
        Comando(val);
    }

    private void Comando(byte cmd)
    {
        switch (cmd)
        {
            case 0x3F:   // entrar en modo UART
                _uart = true;
                _entrada.Clear();
                _entrada.Enqueue(0xFE);   // ACK
                if (_log) Console.Error.WriteLine("[MIDI] MPU-401 en modo UART");
                break;
            case 0xFF:   // reset
                _uart = false;
                _entrada.Clear();
                _entrada.Enqueue(0xFE);
                _salida.Silenciar();
                if (_log) Console.Error.WriteLine("[MIDI] MPU-401 reset");
                break;
            default:
                // El resto de comandos son del intelligent mode. Se reconocen (un MPU real
                // contesta 0xFE a casi todo) pero no hacen nada: un juego que de verdad
                // dependiera de ellos se vería en que la música no avanza, y entonces esto se
                // convierte en la pista, no en un misterio.
                _entrada.Enqueue(0xFE);
                if (_log) Console.Error.WriteLine($"[MIDI] comando 0x{cmd:X2} reconocido sin modelar");
                break;
        }
    }

    // ---- ensamblado de mensajes MIDI ----
    //
    // Un flujo MIDI no viene troceado: llega byte a byte y hay que saber dónde acaba cada
    // mensaje. El byte de ESTADO (0x80-0xEF) dice cuántos datos lleva —dos casi todos, uno el
    // cambio de programa y la presión de canal— y además existe el RUNNING STATUS: si llega un
    // byte de datos sin estado delante, se reutiliza el último estado. Los secuenciadores de la
    // época lo usan a saco para ahorrar ancho de banda, así que sin esto la mitad de las notas
    // se perderían.
    private byte _estado;
    private readonly byte[] _datos = new byte[2];
    private int _nDatos, _faltan;
    private bool _enSysEx;

    private static int DatosDe(byte estado) => (estado & 0xF0) switch
    {
        0xC0 or 0xD0 => 1,          // cambio de programa, presión de canal
        0xF0 => estado switch { 0xF1 or 0xF3 => 1, 0xF2 => 2, _ => 0 },
        _ => 2,
    };

    private void EscribeDato(byte b)
    {
        if (!_uart) return;   // fuera del UART, los datos no son mensajes MIDI

        if (_enSysEx)
        {
            if (b == 0xF7) { _enSysEx = false; }
            return;   // ya contabilizado al abrirlo
        }

        if (b >= 0xF8) { _salida.Envia(b, 0, 0); return; }   // tiempo real: van solas

        if (b >= 0x80)
        {
            if (b == 0xF0)
            {
                _enSysEx = true;
                NoModelado.Add("MIDI SysEx (configuración de sintetizador)");
                return;
            }
            _estado = b; _nDatos = 0; _faltan = DatosDe(b);
            if (_faltan == 0) { _salida.Envia(_estado, 0, 0); _estado = 0; }
            return;
        }

        if (_estado == 0) return;   // dato suelto sin estado previo: nada que hacer con él
        _datos[_nDatos++] = b;
        if (_nDatos < _faltan) return;
        _salida.Envia(_estado, _datos[0], _faltan > 1 ? _datos[1] : (byte)0);
        _nDatos = 0;   // el estado SE CONSERVA: eso es el running status
    }

    public void Cerrar() => _salida.Cerrar();

    /// <summary>El sintetizador del sistema. Aislado aquí para que todo lo específico de Windows
    /// viva en un sitio y el MPU-401 de arriba sea puro protocolo.</summary>
    private sealed class SalidaMidi
    {
        [DllImport("winmm.dll")] private static extern int midiOutOpen(
            out IntPtr h, uint dispositivo, IntPtr callback, IntPtr instancia, uint flags);
        [DllImport("winmm.dll")] private static extern int midiOutShortMsg(IntPtr h, uint mensaje);
        [DllImport("winmm.dll")] private static extern int midiOutReset(IntPtr h);
        [DllImport("winmm.dll")] private static extern int midiOutClose(IntPtr h);

        private const uint MidiMapper = 0xFFFFFFFF;

        private IntPtr _h;
        private bool _intentado, _hay;

        /// <summary>PAKU_NOAUDIO=1 silencia también el MIDI. Sin esto, un diagnóstico headless
        /// con el usuario jugando al lado le mete una orquesta por encima de su partida.</summary>
        private static readonly bool SinAudio = Environment.GetEnvironmentVariable("PAKU_NOAUDIO") == "1";

        private void Abre()
        {
            if (_intentado) return;
            _intentado = true;
            if (SinAudio) return;
            try
            {
                _hay = midiOutOpen(out _h, MidiMapper, IntPtr.Zero, IntPtr.Zero, 0) == 0;
                if (!_hay)
                    Console.Error.WriteLine("[MIDI] no hay sintetizador del sistema: la música MIDI irá muda");
            }
            catch (Exception e)
            {
                // Sin winmm (otra plataforma) el juego sigue: se queda sin música, no sin juego.
                _hay = false;
                Console.Error.WriteLine("[MIDI] sin salida MIDI del sistema: " + e.Message);
            }
        }

        public void Envia(byte estado, byte d1, byte d2)
        {
            Abre();
            if (!_hay) return;
            try { midiOutShortMsg(_h, (uint)(estado | (d1 << 8) | (d2 << 16))); }
            catch { _hay = false; }
        }

        public void Silenciar()
        {
            if (!_hay) return;
            try { midiOutReset(_h); } catch { }
        }

        public void Cerrar()
        {
            if (!_hay) return;
            try { midiOutReset(_h); midiOutClose(_h); } catch { }
            _hay = false; _h = IntPtr.Zero;
        }
    }
}
