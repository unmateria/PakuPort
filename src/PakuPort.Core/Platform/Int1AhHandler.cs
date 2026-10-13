namespace PakuPort.Core.Platform;

/// <summary>
/// INT 1Ah — BIOS timer y RTC services.
///
/// AH=00: Get tick count  → CX:DX = ticks desde medianoche, AL = midnight flag.
/// AH=01: Set tick count  ← CX:DX.
/// AH=02: Read RTC time   → CH = hora BCD, CL = minutos BCD, DH = segundos BCD, CF=0.
/// AH=03: Set RTC time    ← CH/CL/DH BCD (stub — no persiste al CMOS real).
/// AH=04: Read RTC date   → CH = siglo BCD, CL = año BCD, DH = mes BCD, DL = día BCD, CF=0.
/// AH=05: Set RTC date    ← CH/CL/DH/DL BCD (stub).
/// AH=06: Set alarm       ← CH/CL/DH BCD (stub — CF=0 siempre).
/// AH=07: Reset alarm     (stub — no-op).
///
/// El contador de ticks (AH=00/01) usa p.Bda.TimerTicks (respaldado en 0x40:0x6C de Memory)
/// cuando DosPlatform expone Bda; si no, cae al campo privado _ticks para compatibilidad.
///
/// Adaptado de dosbox-x src/ints/bios.cpp — INT1A_Handler (case 0x00..0x07).
/// </summary>
public sealed class Int1AhHandler
{
    // Campo de respaldo para AH=00/01 cuando la BDA todavía no está disponible.
    // Una vez que DosPlatform expone Bda, TimerTicks se lee/escribe desde allí.
    private uint _ticks;

    // Referencia opcional a la plataforma (necesaria para Bda y Cmos).
    // Se asigna en Register().
    private DosPlatform? _platform;

    public void Register(DosPlatform p)
    {
        _platform = p;
        p.RegisterInt(0x1A, Handle);
    }

    /// <summary>
    /// Incrementa el contador de ticks. Llamado por el host o por INT 08h.
    /// Actualiza tanto el campo local como la BDA si está disponible.
    /// </summary>
    public void Tick()
    {
        _ticks++;
        if (_platform is not null)
            _platform.Bda.TimerTicks = _ticks;
    }

    private void Handle(Cpu cpu, Memory mem)
    {
        switch (cpu.AH)
        {
            // ── AH=00: Get tick count ──────────────────────────────────────────────────────
            // dosbox-x bios.cpp: case 0x00 — lee mem[0x40:0x6C] y el midnight flag.
            case 0x00:
            {
                uint t = _platform is not null ? _platform.Bda.TimerTicks : _ticks;
                cpu.CX = (ushort)(t >> 16);
                cpu.DX = (ushort)(t & 0xFFFF);
                if (_platform is not null)
                {
                    cpu.AL = _platform.Bda.MidnightFlag;
                    _platform.Bda.MidnightFlag = 0; // limpiar tras leer
                }
                else
                {
                    cpu.AL = 0;
                }
                break;
            }

            // ── AH=01: Set tick count ──────────────────────────────────────────────────────
            // dosbox-x bios.cpp: case 0x01 — escribe el contador.
            case 0x01:
            {
                uint t = (uint)(cpu.CX << 16) | cpu.DX;
                _ticks = t;
                if (_platform is not null)
                    _platform.Bda.TimerTicks = t;
                break;
            }

            // ── AH=02: Read RTC time ───────────────────────────────────────────────────────
            // dosbox-x bios.cpp: case 0x02 — lee hora/min/seg del CMOS en BCD.
            // CH = horas BCD, CL = minutos BCD, DH = segundos BCD, DL = bit0=DST.
            // CF=0 si el reloj está corriendo; CF=1 si la hora no es válida.
            case 0x02:
            {
                var now = _platform is not null
                    ? _platform.Cmos.GetDateTime()
                    : System.DateTime.Now;
                cpu.CH = ToBcd((byte)now.Hour);
                cpu.CL = ToBcd((byte)now.Minute);
                cpu.DH = ToBcd((byte)now.Second);
                cpu.DL = 0; // DST flag (0 = no DST)
                cpu.CF = false;
                break;
            }

            // ── AH=03: Set RTC time ────────────────────────────────────────────────────────
            // dosbox-x bios.cpp: case 0x03 — escribe hora/min/seg al CMOS.
            // STUB: aceptamos los valores pero no los aplicamos al reloj del sistema.
            // CH = horas BCD, CL = minutos BCD, DH = segundos BCD.
            case 0x03:
            {
                // TODO: propagar a p.Cmos.SetTime(FromBcd(cpu.CH), FromBcd(cpu.CL), FromBcd(cpu.DH))
                //       cuando Cmos exponga SetTime().
                cpu.CF = false;
                break;
            }

            // ── AH=04: Read RTC date ───────────────────────────────────────────────────────
            // dosbox-x bios.cpp: case 0x04 — lee fecha del CMOS en BCD.
            // CH = siglo BCD (19/20), CL = año BCD (00-99), DH = mes BCD, DL = día BCD.
            case 0x04:
            {
                var now = _platform is not null
                    ? _platform.Cmos.GetDateTime()
                    : System.DateTime.Now;
                int year    = now.Year;
                int century = year / 100;
                int yearIn  = year % 100;
                cpu.CH = ToBcd((byte)century);
                cpu.CL = ToBcd((byte)yearIn);
                cpu.DH = ToBcd((byte)now.Month);
                cpu.DL = ToBcd((byte)now.Day);
                cpu.CF = false;
                break;
            }

            // ── AH=05: Set RTC date ────────────────────────────────────────────────────────
            // dosbox-x bios.cpp: case 0x05 — escribe fecha al CMOS.
            // STUB: no persiste. TODO: p.Cmos.SetDate(...)
            case 0x05:
            {
                cpu.CF = false;
                break;
            }

            // ── AH=06: Set alarm ───────────────────────────────────────────────────────────
            // dosbox-x bios.cpp: case 0x06 — programa alarma RTC (IRQ8/INT 70h).
            // STUB: CF=0 = éxito ficticio. TODO: implementar con MachineTimer.
            case 0x06:
            {
                cpu.CF = false;
                break;
            }

            // ── AH=07: Reset alarm ─────────────────────────────────────────────────────────
            // dosbox-x bios.cpp: case 0x07 — desactiva la alarma del CMOS.
            // STUB: no-op.
            case 0x07:
            {
                cpu.CF = false;
                break;
            }
        }
    }

    // ── Utilidades BCD ────────────────────────────────────────────────────────────────────

    /// <summary>Convierte un byte binario (0-99) a formato BCD empaquetado.</summary>
    private static byte ToBcd(byte v) => (byte)(((v / 10) << 4) | (v % 10));

    /// <summary>Convierte un byte BCD empaquetado a binario.</summary>
    private static byte FromBcd(byte v) => (byte)(((v >> 4) * 10) + (v & 0x0F));
}
