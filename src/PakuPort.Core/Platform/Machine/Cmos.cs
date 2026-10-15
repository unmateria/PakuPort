namespace PakuPort.Core.Platform;

/// <summary>
/// CMOS/RTC (Motorola MC146818). Puerto 0x70 = índice (+ NMI mask en bit 7), 0x71 = dato.
/// Registros 0x00-0x09: fecha/hora real en BCD. Registros 0x0A/0x0B: status.
/// Adaptado de dosbox-x src/hardware/cmos.cpp.
/// </summary>
public sealed class Cmos
{
    private byte _index = 0;
    private readonly byte[] _regs = new byte[128]; // 128 registros CMOS

    /// <summary>Instructions per ms, for the time under a tape (<see cref="GuestClock"/>). The
    /// port has no CPU at hand; MachineTimer.Poll and FlatServices.ServicioIrq keep it current.</summary>
    public int CyclesPerMs;

    public Cmos() { Init(); }

    private void Init()
    {
        // Status A: 0x26 = oscilador activo, 32.768 KHz, tasa de interrupción ~976 μs
        _regs[0x0A] = 0x26;
        // Status B: modo 24h, no alarma, no interrupciones periódicas, formato BCD
        _regs[0x0B] = 0x02;
        // Status C/D: sin flags
        _regs[0x0C] = 0x00;
        _regs[0x0D] = 0x80; // bit 7 = batería OK
    }

    public byte In(ushort port)
    {
        if (port == 0x70) return _index; // devuelve índice actual (algunos programas leen)
        if (port != 0x71) return 0xFF;

        var now = GetDateTime();
        return _index switch
        {
            0x00 => ToBcd(now.Second),
            0x02 => ToBcd(now.Minute),
            0x04 => ToBcd(now.Hour),
            0x06 => ToBcd((int)now.DayOfWeek + 1), // 1=domingo
            0x07 => ToBcd(now.Day),
            0x08 => ToBcd(now.Month),
            0x09 => ToBcd(now.Year % 100),
            0x0A => _regs[0x0A],
            0x0B => _regs[0x0B],
            0x0C => (byte)(_regs[0x0C] | 0x00),  // limpiar flags de interrupción al leer
            0x0D => _regs[0x0D],
            0x32 => ToBcd(now.Year / 100),         // siglo
            _    => _regs[_index & 0x7F],
        };
    }

    public void Out(ushort port, byte v)
    {
        if (port == 0x70)
        {
            _index = (byte)(v & 0x7F); // bit 7 = NMI mask, ignorado aquí
            return;
        }
        if (port != 0x71) return;

        // Almacenar en el array; las lecturas de hora los ignoran y usan DateTime.Now
        _regs[_index & 0x7F] = v;
    }

    /// <summary>
    /// La fecha/hora del RTC: la del sistema, o la del invitado bajo una cinta. Expuesto para que
    /// Int1AhHandler lea el RTC sin duplicar la logica.
    /// </summary>
    public DateTime GetDateTime() => GuestClock.Now(CyclesPerMs);

    private static byte ToBcd(int v) => (byte)(((v / 10) << 4) | (v % 10));
}
