using PakuPort.Core.Platform;

// Standalone synthesis probe: drive OplChip (dbopl port) with a real FM note and
// confirm GenerateBlock2 emits non-zero PCM. Proves the synth->PCM link end to end,
// independent of the game driving the OPL ports.
var chip = new OplChip(22050);

// OPL2 channel 0 uses operator register offsets 0x00 (modulator) and 0x03 (carrier).
// Simple audible FM patch, additive-ish, fast attack, held.
void W(uint r, byte v) => chip.WriteReg(r, v);
W(0x01, 0x20);        // waveform select enable
// modulator (op 0x00)
W(0x20, 0x21);        // mult=1, sustain held (bit5)
W(0x40, 0x10);        // TL a bit attenuated
W(0x60, 0xF0);        // attack fast, decay 0
W(0x80, 0x0F);        // sustain max, release
W(0xE0, 0x00);        // sine
// carrier (op 0x03)
W(0x23, 0x21);        // carrier mult=1, sustain held
W(0x43, 0x00);        // carrier full volume
W(0x63, 0xF0);
W(0x83, 0x0F);
W(0xE3, 0x00);
W(0xC0, 0x01);        // feedback=0, connection=1 (AM/additive) -> carrier audible
// note: A0 low freq, B0 = key-on | block=4 | freq high bits
W(0xA0, 0x98);        // fnum low
W(0xB0, 0x31);        // KON(0x20) | block4(0x10) | fnumHigh(0x01)

int n = 22050; // 1 second
int[] buf = new int[n];
chip.GenerateBlock2((uint)n, buf);

long sumSq = 0; int peak = 0; int nonzero = 0;
for (int i = 0; i < n; i++)
{
    int s = buf[i];
    int a = Math.Abs(s);
    if (a > peak) peak = a;
    if (a > 0) nonzero++;
    sumSq += (long)s * s;
}
double rms = Math.Sqrt(sumSq / (double)n);
Console.WriteLine($"OplChip synth probe: samples={n} peak={peak} nonzero={nonzero} rms={rms:F1}");
Console.WriteLine(peak > 0 ? "RESULT: SYNTH OK (produces audio)" : "RESULT: SILENT (synth broken)");
