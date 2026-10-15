namespace PakuPort.Core;

/// <summary>
/// The date and time the guest reads (INT 21h AH=2A/2C, INT 1Ah AH=02/04, the CMOS RTC, the
/// flat-world DOS services). Without a tape it is the host's clock, as always. Under a tape it is
/// a fixed epoch plus the guest's own elapsed time, so two runs of the same tape read the same
/// time: with the host's clock, a game that seeds anything from the time took a different path in
/// each run, and a recording could not be replayed.
///
/// <para>The epoch is 1994-06-01 12:00, the same one PAKU_HORA_FIJA uses in the flat world: before
/// 2000 so that no game walks a Y2K path it never walked on its own hardware, and at noon so that
/// a run does not cross midnight. DOSBox-X keeps the RTC the same way (cmos.cpp: host time at boot,
/// then emulated time); the boot instant is the only thing fixed here.</para>
///
/// <para>`PAKU_RELOJ_PARED=hora` gives the host's clock back under a tape, for an A/B.</para>
/// </summary>
public static class GuestClock
{
    public static readonly DateTime Epoch = new(1994, 6, 1, 12, 0, 0);

    static bool _avisado;

    public static DateTime Now(int cyclesPerMs)
    {
        if (!InputTape.RelojDelInvitado("hora")) return DateTime.Now;
        if (cyclesPerMs <= 0)
        {
            // Same fallback MachineTimer takes without a calibration, but said out loud.
            if (!_avisado)
            {
                _avisado = true;
                Console.Error.WriteLine("[RELOJ] cinta sin cyclesPerMs: la hora sale del reloj del anfitrion " +
                                        "y esta corrida no se puede repetir igual (pon cyclesPerMs en game.json)");
            }
            return DateTime.Now;
        }
        return Epoch.AddTicks(Cpu.TickCount * TimeSpan.TicksPerMillisecond / cyclesPerMs);
    }
}
