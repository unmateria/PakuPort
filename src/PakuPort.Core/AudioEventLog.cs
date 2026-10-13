namespace PakuPort.Core;

/// <summary>
/// Remake-kit step 5: sound-event catalog
/// with fingerprints, so the offline tooling can name the sounds a game makes and a future
/// remake manifest can map each fingerprint to an HD sample.
///
/// Two sources, one log (logs/audio-events.log), enabled by PAKU_LOG_AUDIO_EV=1:
///   spk — one event per gate-on -> gate-off sequence of PIT channel 2, carrying the list of
///         (frequency Hz, duration ms) segments that actually sounded. A frequency change
///         while the gate stays open opens a new segment (that is how DOS jingles are played).
///   opl — one event per burst of OPL register writes separated by silences (&gt;150 ms with no
///         key-on active): the register program that produced the sound.
///
/// The fingerprint is FNV-1a over the QUANTIZED sequence (Hz rounded to integer, ms rounded to
/// 4 ms buckets for spk; the raw reg/val byte stream for opl) so the same sound effect played
/// twice hashes the same despite the jitter of the real-time clock.
///
/// The "who" is the caller's cs:ip (Interp.CurrentPc), free telemetry that ties each sound to
/// the routine that triggered it — the same provenance idea as blit.log.
///
/// Off (default) = one static bool check on the audio tick and on each OPL register write;
/// nothing else in the emulator changes. This is telemetry only: it never touches CPU/memory
/// state nor the generated samples.
///
/// Line format (one per event):
///   t=&lt;tick&gt; src=spk fnv=XXXXXXXX n=&lt;items&gt; pc=CCCC:IIII ms=&lt;total&gt; fnvhz=XXXXXXXX seq=hz:ms,...
///   t=&lt;tick&gt; src=opl fnv=XXXXXXXX n=&lt;items&gt; pc=CCCC:IIII ms=&lt;total&gt; regs=RR:VV,...
/// The seq/regs detail is capped at <see cref="DetailCap"/> items with a "+N" tail; the
/// fingerprint always covers the WHOLE sequence.
/// </summary>
public static class AudioEventLog
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("PAKU_LOG_AUDIO_EV") == "1";

    /// <summary>Silence that closes an OPL group, in milliseconds (only counted while no
    /// channel holds a key-on).</summary>
    const double OplSilenceMs = 150.0;
    /// <summary>Safety cap: a long music stream never stops writing registers, so a group is
    /// closed and continued after this many writes instead of growing without bound.</summary>
    const int OplMaxWrites = 4096;
    /// <summary>Duration quantization of the speaker segments (ms).</summary>
    const int MsBucket = 4;
    const int DetailCap = 48;

    // Shared clock: milliseconds of machine time, advanced ONLY from the audio tick
    // (Speaker.Generate, called by MachineTimer.DeliverDueTicks with the same deltaMs that
    // drives the OPL and the speaker samples). Using it — instead of a private Stopwatch —
    // keeps the event durations on the same clock as the audio the player hears, and follows
    // the virtual clock when a replay tape locks it (InputTape.ClockLocked).
    static double _nowMs;

    // ---- PC speaker ----
    static bool _spkOn;
    static int _spkHz;                       // frequency of the segment being accumulated
    static double _spkSegMs;                 // duration accumulated for that segment
    static uint _spkPc;                      // cs:ip that opened the sequence
    static uint _lastReload = 1;             // channel-2 reload seen on the last tick
    static readonly List<(int Hz, int Ms)> _spkSeq = new();

    // ---- OPL ----
    static readonly List<(byte Reg, byte Val)> _oplRegs = new();
    static double _oplStartMs, _oplLastMs;
    static uint _oplPc;
    static readonly bool[] _oplKeyOn = new bool[9];
    static int _oplNotes;

    static StreamWriter? _log;
    static int _lines;

    /// <summary>Audio tick from the machine timer (Speaker.Generate). Advances the shared
    /// clock, accumulates the current speaker segment, samples the gate/frequency (a frequency
    /// change with the gate open is only visible here) and closes an OPL group that has gone
    /// silent.</summary>
    public static void Tick(double deltaMs, bool gateOn, uint pitReload)
    {
        _nowMs += deltaMs;
        if (_spkOn) _spkSegMs += deltaMs;
        _lastReload = pitReload;
        SpeakerState(gateOn, pitReload);
        if (_oplRegs.Count > 0 && _oplNotes == 0 && _nowMs - _oplLastMs > OplSilenceMs)
            EmitOpl();
    }

    /// <summary>Gate/enable edge straight from the 0x61 write. Needed on TOP of the tick
    /// sampling: a program can close the gate and exit to DOS a handful of instructions later,
    /// with no tick in between, and that beep would never be seen (measured with SPK.COM).
    /// The frequency is the one sampled on the last tick; if the PIT was reprogrammed in the
    /// same sub-tick window the next tick corrects it and the stale segment, being under the
    /// quantization floor, is dropped.</summary>
    public static void SpeakerGate(bool gateOn) => SpeakerState(gateOn, _lastReload);

    static void SpeakerState(bool on, uint reload)
    {
        // Same audibility rule as Speaker.Generate: outside 20..20000 Hz nothing sounds, so it
        // does not open a segment either.
        double freq = reload == 0 ? 0 : 1193181.666 / reload;
        int hz = (int)Math.Round(freq);
        if (freq < 20 || freq > 20000) on = false;

        if (!_spkOn && !on) return;
        if (_spkOn && on && hz == _spkHz) return;

        if (_spkOn)
        {
            int ms = (int)Math.Round(_spkSegMs / MsBucket) * MsBucket;
            if (ms > 0) _spkSeq.Add((_spkHz, ms));
            _spkSegMs = 0;
        }

        if (on)
        {
            if (!_spkOn) { _spkPc = Interp.CurrentPc; _spkSeq.Clear(); }
            _spkOn = true;
            _spkHz = hz;
        }
        else
        {
            _spkOn = false;
            EmitSpeaker();
        }
    }

    static void EmitSpeaker()
    {
        // A sequence shorter than one quantization bucket is an inaudible click, not a sound.
        if (_spkSeq.Count == 0) return;
        uint h = 2166136261, hz1 = 2166136261;
        int total = 0;
        foreach (var (hz, ms) in _spkSeq)
        {
            h = Fnv(h, hz);
            h = Fnv(h, ms);
            hz1 = Fnv(hz1, hz);       // duration-independent shape
            total += ms;
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _spkSeq.Count && i < DetailCap; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(_spkSeq[i].Hz).Append(':').Append(_spkSeq[i].Ms);
        }
        if (_spkSeq.Count > DetailCap) sb.Append("+").Append(_spkSeq.Count - DetailCap);
        // fnvhz = fingerprint of the FREQUENCIES alone. The durations of a real beep wobble by
        // a few ms between runs (they are timed off the BIOS tick), so fnv alone would give a
        // different identity to every appearance of the same effect; the catalog groups by this
        // one and keeps the exact fnv values as variants.
        Write("spk", h, _spkSeq.Count, _spkPc, total, "seq", sb.ToString(), hz1);
        _spkSeq.Clear();
    }

    /// <summary>One OPL register write (port 0x389 data). Opens a group on the first write of
    /// a silence and tracks key-on state (regs 0xB0..0xB8 bit 5) to know when the sound is
    /// still ringing.</summary>
    public static void OplWrite(byte reg, byte val)
    {
        if (_oplRegs.Count == 0) { _oplStartMs = _nowMs; _oplPc = Interp.CurrentPc; }
        _oplRegs.Add((reg, val));
        _oplLastMs = _nowMs;
        if (reg >= 0xB0 && reg <= 0xB8)
        {
            bool key = (val & 0x20) != 0;
            int ch = reg - 0xB0;
            if (key != _oplKeyOn[ch]) { _oplKeyOn[ch] = key; _oplNotes += key ? 1 : -1; }
        }
        if (_oplRegs.Count >= OplMaxWrites) EmitOpl();
    }

    static void EmitOpl()
    {
        if (_oplRegs.Count == 0) return;
        uint h = 2166136261;
        foreach (var (reg, val) in _oplRegs)
        {
            h = (h ^ reg) * 16777619;
            h = (h ^ val) * 16777619;
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _oplRegs.Count && i < DetailCap; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(_oplRegs[i].Reg.ToString("X2")).Append(':').Append(_oplRegs[i].Val.ToString("X2"));
        }
        if (_oplRegs.Count > DetailCap) sb.Append("+").Append(_oplRegs.Count - DetailCap);
        Write("opl", h, _oplRegs.Count, _oplPc, (int)Math.Round(_oplLastMs - _oplStartMs),
              "regs", sb.ToString());
        _oplRegs.Clear();
    }

    static uint Fnv(uint h, int v)
    {
        for (int i = 0; i < 4; i++) h = (h ^ (byte)(v >> (i * 8))) * 16777619;
        return h;
    }

    static void Write(string src, uint fnv, int n, uint pc, int ms, string detailKey, string detail,
                      uint? shape = null)
    {
        _log ??= Open();
        _log.WriteLine($"t={Cpu.TickCount} src={src} fnv={fnv:X8} n={n} " +
                       $"pc={pc >> 16:X4}:{pc & 0xFFFF:X4} ms={ms} " +
                       (shape is uint s ? $"fnvhz={s:X8} " : "") + $"{detailKey}={detail}");
        if (++_lines % 64 == 0) _log.Flush();
    }

    // Registered when the class is first touched — NOT inside Open() — so a sound still ringing
    // when the game exits is written even if it is the FIRST event of the run.
    static AudioEventLog()
    {
        if (!Enabled) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (_spkOn)
                {
                    int last = (int)Math.Round(_spkSegMs / MsBucket) * MsBucket;
                    if (last > 0) _spkSeq.Add((_spkHz, last));
                    _spkOn = false;
                    EmitSpeaker();
                }
                EmitOpl();
                _log?.Flush();
            }
            catch { }
        };
    }

    static StreamWriter Open()
    {
        Directory.CreateDirectory("logs");
        return new StreamWriter(Path.Combine("logs", "audio-events.log")) { AutoFlush = false };
    }
}
