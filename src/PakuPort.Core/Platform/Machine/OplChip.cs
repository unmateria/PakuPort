namespace PakuPort.Core.Platform;

/// <summary>
/// Sintetizador OPL2 mono (Yamaha YM3812) — porte A MANO de dosbox-x src/hardware/dbopl.cpp
/// (DBOPL_WAVE = WAVE_TABLEMUL, opl3Mode=false). Convierte los 256 registros que el driver FM
/// escribe por 0x388/0x389 en PCM int32 mono, generado directamente a la rate del host
/// (Setup(rate) escala todo por OPLRATE/rate → cero reamuestreo). El alcance es OPL2 mono:
/// se descartan OPL3/estéreo/4-op/GenerateBlock3/save-load (de dbopl.cpp).
///
/// Traducción de idiomas C peligrosos (SPEC-OPL §3.7):
///  - Aritmética de punteros sobre structs contiguos → arrays planos + índices enteros.
///    dbopl navega Chip{Channel chan[18]} / Channel{Operator op[2]} con (char*)this+offset;
///    aquí OplChannel guarda su índice y una referencia al chip, y Op(i) cruza de canal por
///    índice: chip.chan[idx + (i>>1)].op[i&1]. Los REGOP/REGCHAN (macros de dbopl que suman
///    offsets de byte) se sustituyen por las tablas de ÍNDICE OpIndexTable/ChanIndexTable
///    (construidas igual que ChanOffsetTable/OpOffsetTable, dbopl.cpp:1451-1478, pero en
///    unidades de índice; -1 = registro sin operador/canal).
///  - Punteros a método / plantillas (SynthHandler, BlockTemplate&lt;mode&gt;) → enum SynthMode +
///    switch en runtime (el coste de rama es despreciable a 22050 Hz).
/// Toda la aritmética de fase/contadores usa envoltura deliberada (unchecked).
/// </summary>
public sealed class OplChip
{
    // ---- constantes (dbopl.cpp:54-99) ----
    private const int WAVE_BITS = 10;
    private const int WAVE_SH = 32 - WAVE_BITS;            // 22
    private const uint WAVE_MASK = (1u << WAVE_SH) - 1;
    private const int LFO_SH = WAVE_SH - 10;              // 12
    private const uint LFO_MAX = 256u << LFO_SH;          // 0x100000
    private const int ENV_BITS = 9;
    private const int ENV_MIN = 0;
    private const int ENV_EXTRA = ENV_BITS - 9;           // 0
    private const int ENV_MAX = 511 << ENV_EXTRA;         // 511
    private const int ENV_LIMIT = (12 * 256) >> (3 - ENV_EXTRA); // 384
    private const int RATE_SH = 24;
    private const uint RATE_MASK = (1u << RATE_SH) - 1;
    private const int MUL_SH = 16;
    private const int SHIFT_KSLBASE = 16;
    private const int SHIFT_KEYCODE = 24;
    private const int TREMOLO_TABLE = 52;
    private const double OPLRATE = 14318180.0 / 288.0;

    private static bool EnvSilent(int x) => x >= ENV_LIMIT;

    // envelope states (dbopl.h:77-83)
    private const int S_OFF = 0, S_RELEASE = 1, S_SUSTAIN = 2, S_DECAY = 3, S_ATTACK = 4;
    // reg20 masks (dbopl.h:70-75)
    private const int MASK_KSR = 0x10, MASK_SUSTAIN = 0x20, MASK_VIBRATO = 0x40, MASK_TREMOLO = 0x80;

    private enum SynthMode
    {
        sm2AM = 0, sm2FM = 1, sm3AM = 2, sm3FM = 3, sm4Start = 4,
        sm3FMFM = 5, sm3AMFM = 6, sm3FMAM = 7, sm3AMAM = 8, sm6Start = 9,
        sm2Percussion = 10, sm3Percussion = 11,
    }

    // ---- tablas fijas (dbopl.cpp:108-199), copiadas verbatim ----
    private static readonly byte[] KslCreateTable =
        { 64, 32, 24, 19, 16, 12, 11, 10, 8, 6, 5, 4, 3, 2, 1, 0 };
    // FreqCreateTable con la macro M(x)=x*2 ya aplicada (dbopl.cpp:116-120)
    private static readonly byte[] FreqCreateTable =
        { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30 };
    private static readonly byte[] AttackSamplesTable =
        { 69, 55, 46, 40, 35, 29, 23, 20, 19, 15, 11, 10, 9 };
    private static readonly byte[] EnvelopeIncreaseTable =
        { 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32 };
    private static readonly ushort[] WaveBaseTable =
        { 0x000, 0x200, 0x200, 0x800, 0xa00, 0xc00, 0x100, 0x400 };
    private static readonly ushort[] WaveMaskTable =
        { 1023, 1023, 511, 511, 1023, 1023, 512, 1023 };
    private static readonly ushort[] WaveStartTable =
        { 512, 0, 0, 0, 0, 512, 512, 256 };
    // dbopl.cpp:191-194 (int8_t): 1-0x00,0-0x00,1-0x00,30-0x00, 1-0x80,0-0x80,1-0x80,30-0x80
    private static readonly sbyte[] VibratoTable =
        { 1, 0, 1, 30, -127, -128, -127, -98 };
    private static readonly byte[] KslShiftTable = { 31, 1, 2, 0 };

    // ---- tablas generadas en InitTables (dbopl.cpp:1357) ----
    private static readonly ushort[] MulTable = new ushort[384];
    private static readonly short[] WaveTable = new short[8 * 512];
    private static readonly byte[] KslTable = new byte[8 * 16];
    private static readonly byte[] TremoloTable = new byte[TREMOLO_TABLE];
    // Mapa registro→canal/operador en unidades de ÍNDICE (no de byte; SPEC-OPL §3.7).
    // ChanIndexTable[i] = índice de canal interno (0..17) o -1. OpIndexTable[i] = índice plano
    // de operador (canal*2 + op, 0..35) o -1; se accede con chan[v>>1].op[v&1].
    private static readonly int[] ChanIndexTable = new int[32];
    private static readonly int[] OpIndexTable = new int[64];
    private static bool _tablesDone;
    private static readonly object _tablesLock = new();

    // ---- estado del chip (dbopl.h:194-249) ----
    private readonly OplChannel[] chan = new OplChannel[18];

    private uint lfoCounter, lfoAdd;
    private uint noiseCounter, noiseAdd, noiseValue;
    private readonly uint[] freqMul = new uint[16];
    private readonly uint[] linearRates = new uint[76];
    private readonly uint[] attackRates = new uint[76];

    private byte reg104, reg08, regBD;
    private byte vibratoIndex, tremoloIndex;
    private sbyte vibratoSign;
    private byte vibratoShift;
    private byte tremoloValue;
    private byte vibratoStrength, tremoloStrength;
    private byte waveFormMask;
    private int opl3Active;              // 0 o 0xff (int8_t -1 en dbopl); siempre 0 tras Setup en OPL2
    private const bool opl3Mode = false;

    public OplChip(uint rate)
    {
        InitTables();
        for (int i = 0; i < 18; i++) chan[i] = new OplChannel(this, i);
        Setup(rate);
    }

    // ================================================================= Operator
    // dbopl.cpp:287-636. waveBase (int16_t*) → waveBaseIdx (offset en WaveTable).
    private sealed class OplOperator
    {
        private readonly OplChip chip;
        public OplOperator(OplChip c) { chip = c; SetState(S_OFF); rateZero = 1 << S_OFF;
            sustainLevel = ENV_MAX; currentLevel = ENV_MAX; totalLevel = ENV_MAX; volume = ENV_MAX; }

        public int waveBaseIdx;
        public uint waveMask, waveStart, waveIndex, waveAdd, waveCurrent;
        public uint chanData, freqMul, vibrato;
        public int sustainLevel, totalLevel;
        public uint currentLevel;
        public int volume;
        public uint attackAdd, decayAdd, releaseAdd, rateIndex;
        public int rateZero;
        public byte keyOn;
        public byte reg20, reg40, reg60, reg80, regE0;
        public int state;
        public byte tremoloMask;
        public byte vibStrength, ksr;

        // dbopl.cpp:292-330
        private void UpdateAttack()
        {
            int rate = reg60 >> 4;
            if (rate != 0) { attackAdd = chip.attackRates[(rate << 2) + ksr]; rateZero &= ~(1 << S_ATTACK); }
            else { attackAdd = 0; rateZero |= (1 << S_ATTACK); }
        }
        private void UpdateDecay()
        {
            int rate = reg60 & 0xf;
            if (rate != 0) { decayAdd = chip.linearRates[(rate << 2) + ksr]; rateZero &= ~(1 << S_DECAY); }
            else { decayAdd = 0; rateZero |= (1 << S_DECAY); }
        }
        private void UpdateRelease()
        {
            int rate = reg80 & 0xf;
            if (rate != 0)
            {
                releaseAdd = chip.linearRates[(rate << 2) + ksr];
                rateZero &= ~(1 << S_RELEASE);
                if ((reg20 & MASK_SUSTAIN) == 0) rateZero &= ~(1 << S_SUSTAIN);
            }
            else
            {
                rateZero |= (1 << S_RELEASE);
                releaseAdd = 0;
                if ((reg20 & MASK_SUSTAIN) == 0) rateZero |= (1 << S_SUSTAIN);
            }
        }

        // dbopl.cpp:332-339
        public void UpdateAttenuation()
        {
            int kslBase = (int)((chanData >> SHIFT_KSLBASE) & 0xff);
            uint tl = reg40 & 0x3fu;
            int kslShift = KslShiftTable[reg40 >> 6];
            totalLevel = (int)(tl << (ENV_BITS - 7));
            totalLevel += (kslBase << ENV_EXTRA) >> kslShift;
        }

        // dbopl.cpp:341-362 (rama sin WAVE_PRECISION)
        public void UpdateFrequency()
        {
            unchecked
            {
                uint freq = chanData & ((1u << 10) - 1);
                int block = (int)((chanData >> 10) & 0xff);
                waveAdd = (freq << block) * freqMul;
                if ((reg20 & MASK_VIBRATO) != 0)
                {
                    vibStrength = (byte)(freq >> 7);
                    vibrato = ((uint)vibStrength << block) * freqMul;
                }
                else { vibStrength = 0; vibrato = 0; }
            }
        }

        // dbopl.cpp:364-377
        public void UpdateRates()
        {
            byte newKsr = (byte)((chanData >> SHIFT_KEYCODE) & 0xff);
            if ((reg20 & MASK_KSR) == 0) newKsr = (byte)(newKsr >> 2);
            if (ksr == newKsr) return;
            ksr = newKsr;
            UpdateAttack(); UpdateDecay(); UpdateRelease();
        }

        // dbopl.cpp:379-384
        private int RateForward(uint add)
        {
            unchecked
            {
                rateIndex += add;
                int ret = (int)(rateIndex >> RATE_SH);
                rateIndex &= RATE_MASK;
                return ret;
            }
        }

        // dbopl.cpp:386-436 (TemplateVolume<state> aplanado a switch)
        private int TemplateVolume(int yes)
        {
            int vol = volume;
            int change;
            switch (yes)
            {
                case S_OFF:
                    return ENV_MAX;
                case S_ATTACK:
                    change = RateForward(attackAdd);
                    if (change == 0) return vol;
                    vol += ((~vol) * change) >> 3;
                    if (vol < ENV_MIN) { volume = ENV_MIN; rateIndex = 0; SetState(S_DECAY); return ENV_MIN; }
                    break;
                case S_DECAY:
                    vol += RateForward(decayAdd);
                    if (vol >= sustainLevel)
                    {
                        if (vol >= ENV_MAX) { volume = ENV_MAX; SetState(S_OFF); return ENV_MAX; }
                        rateIndex = 0; SetState(S_SUSTAIN);
                    }
                    break;
                case S_SUSTAIN:
                    if ((reg20 & MASK_SUSTAIN) != 0) return vol;
                    // no sustaining → release regular (FALLTHROUGH en dbopl)
                    goto case S_RELEASE;
                case S_RELEASE:
                    vol += RateForward(releaseAdd);
                    if (vol >= ENV_MAX) { volume = ENV_MAX; SetState(S_OFF); return ENV_MAX; }
                    break;
            }
            volume = vol;
            return vol;
        }

        // dbopl.cpp:446-454
        public int ForwardVolume() => (int)currentLevel + TemplateVolume(state);
        public uint ForwardWave() { unchecked { waveIndex += waveCurrent; return waveIndex >> WAVE_SH; } }

        // dbopl.cpp:456-479
        public void Write20(byte val)
        {
            int change = reg20 ^ val;
            if (change == 0) return;
            reg20 = val;
            tremoloMask = (byte)(((sbyte)val >> 7) & ~((1 << ENV_EXTRA) - 1));
            if ((change & MASK_KSR) != 0) UpdateRates();
            if ((reg20 & MASK_SUSTAIN) != 0 || releaseAdd == 0) rateZero |= (1 << S_SUSTAIN);
            else rateZero &= ~(1 << S_SUSTAIN);
            if ((change & (0xf | MASK_VIBRATO)) != 0) { freqMul = chip.freqMul[val & 0xf]; UpdateFrequency(); }
        }
        // dbopl.cpp:481-486
        public void Write40(byte val) { if ((reg40 ^ val) == 0) return; reg40 = val; UpdateAttenuation(); }
        // dbopl.cpp:488-497
        public void Write60(byte val)
        {
            int change = reg60 ^ val; reg60 = val;
            if ((change & 0x0f) != 0) UpdateDecay();
            if ((change & 0xf0) != 0) UpdateAttack();
        }
        // dbopl.cpp:499-511
        public void Write80(byte val)
        {
            int change = reg80 ^ val;
            if (change == 0) return;
            reg80 = val;
            int sustain = val >> 4;
            sustain |= (sustain + 1) & 0x10;             // 0xf → 0x1f
            sustainLevel = sustain << (ENV_BITS - 5);
            if ((change & 0x0f) != 0) UpdateRelease();
        }
        // dbopl.cpp:513-526 (rama WAVE_TABLEMUL)
        public void WriteE0(byte val)
        {
            if ((regE0 ^ val) == 0) return;
            int waveForm = val & ((0x3 & chip.waveFormMask) | (0x7 & chip.opl3Active));
            regE0 = val;
            waveBaseIdx = WaveBaseTable[waveForm];
            waveStart = (uint)WaveStartTable[waveForm] << WAVE_SH;
            waveMask = WaveMaskTable[waveForm];
        }

        // dbopl.cpp:528-575
        private void SetState(int s) => state = s;
        public bool Silent()
        {
            if (!EnvSilent(totalLevel + volume)) return false;
            if ((rateZero & (1 << state)) == 0) return false;
            return true;
        }
        public void Prepare()
        {
            unchecked
            {
                currentLevel = (uint)(totalLevel + (chip.tremoloValue & tremoloMask));
                waveCurrent = waveAdd;
                if ((vibStrength >> chip.vibratoShift) != 0)
                {
                    int add = (int)(vibrato >> chip.vibratoShift);
                    int neg = chip.vibratoSign;                 // sign extend over shift (dbopl.cpp:547)
                    add = (add ^ neg) - neg;
                    waveCurrent += (uint)add;
                }
            }
        }
        public void KeyOn(byte mask)
        {
            if (keyOn == 0) { waveIndex = waveStart; rateIndex = 0; SetState(S_ATTACK); }
            keyOn |= mask;
        }
        public void KeyOff(byte mask)
        {
            keyOn &= (byte)~mask;
            if (keyOn == 0 && state != S_OFF) SetState(S_RELEASE);
        }

        // dbopl.cpp:577-605 (rama WAVE_TABLEMUL). int16 * uint16 >> 16, con signo.
        public int GetWave(uint index, int vol)
            => (WaveTable[waveBaseIdx + (int)(index & waveMask)] * (int)MulTable[vol >> ENV_EXTRA]) >> MUL_SH;

        public int GetSample(int modulation)
        {
            unchecked
            {
                int vol = ForwardVolume();
                if (EnvSilent(vol)) { waveIndex += waveCurrent; return 0; }
                uint index = ForwardWave();
                index += (uint)modulation;
                return GetWave(index, vol);
            }
        }
    }

    // ================================================================= Channel
    // dbopl.cpp:638-986
    private sealed class OplChannel
    {
        private readonly OplChip chip;
        public readonly int idx;
        public readonly OplOperator[] op;
        public SynthMode synthMode = SynthMode.sm2FM;
        public uint chanData;
        public readonly int[] old = new int[2];
        public int feedback = 31;
        public byte regB0, regC0, fourMask;
        public int maskLeft = -1, maskRight = -1;

        public OplChannel(OplChip c, int i)
        {
            chip = c; idx = i;
            op = new[] { new OplOperator(c), new OplOperator(c) };
        }

        // Op(index) cruza a canales siguientes (dbopl.h:160): (this+(i>>1))->op[i&1].
        private OplOperator Op(int index) => chip.chan[idx + (index >> 1)].op[index & 1];

        // dbopl.cpp:654-670
        public void SetChanData(uint data)
        {
            uint change = chanData ^ data;
            chanData = data;
            Op(0).chanData = data; Op(1).chanData = data;
            // frequency update siempre (lo dispara una escritura de frecuencia)
            Op(0).UpdateFrequency(); Op(1).UpdateFrequency();
            if ((change & (0xffu << SHIFT_KSLBASE)) != 0) { Op(0).UpdateAttenuation(); Op(1).UpdateAttenuation(); }
            if ((change & (0xffu << SHIFT_KEYCODE)) != 0) { Op(0).UpdateRates(); Op(1).UpdateRates(); }
        }

        // dbopl.cpp:672-688
        public void UpdateFrequency(byte fourOp)
        {
            uint data = chanData & 0xffff;
            uint kslBase = KslTable[data >> 6];
            uint keyCode = (data & 0x1c00) >> 9;
            if ((chip.reg08 & 0x40) != 0) keyCode |= (data & 0x100) >> 8;   // notesel == 1
            else keyCode |= (data & 0x200) >> 9;                            // notesel == 0
            data |= (keyCode << SHIFT_KEYCODE) | (kslBase << SHIFT_KSLBASE);
            SetChanData(data);
            if ((fourOp & 0x3f) != 0) chip.chan[idx + 1].SetChanData(data);
        }

        // dbopl.cpp:690-700
        public void WriteA0(byte val)
        {
            byte fourOp = (byte)(chip.reg104 & chip.opl3Active & fourMask);
            if (fourOp > 0x80) return;
            uint change = (chanData ^ val) & 0xff;
            if (change != 0) { chanData ^= change; UpdateFrequency(fourOp); }
        }
        // dbopl.cpp:702-731
        public void WriteB0(byte val)
        {
            byte fourOp = (byte)(chip.reg104 & chip.opl3Active & fourMask);
            if (fourOp > 0x80) return;
            uint change = (chanData ^ ((uint)val << 8)) & 0x1f00u;
            if (change != 0) { chanData ^= change; UpdateFrequency(fourOp); }
            if (((val ^ regB0) & 0x20) == 0) return;
            regB0 = val;
            if ((val & 0x20) != 0)
            {
                Op(0).KeyOn(0x1); Op(1).KeyOn(0x1);
                if ((fourOp & 0x3f) != 0) { chip.chan[idx + 1].Op(0).KeyOn(1); chip.chan[idx + 1].Op(1).KeyOn(1); }
            }
            else
            {
                Op(0).KeyOff(0x1); Op(1).KeyOff(0x1);
                if ((fourOp & 0x3f) != 0) { chip.chan[idx + 1].Op(0).KeyOff(1); chip.chan[idx + 1].Op(1).KeyOff(1); }
            }
        }
        // dbopl.cpp:733-747
        public void WriteC0(byte val)
        {
            int change = val ^ regC0;
            if (change == 0) return;
            regC0 = val;
            feedback = (regC0 >> 1) & 7;
            feedback = feedback != 0 ? 9 - feedback : 31;   // shift a índice de onda de 10 bits
            UpdateSynth();
        }

        // dbopl.cpp:749-802 (rama opl3Active nunca activa en OPL2 mono; se porta por fidelidad
        // — no altera la salida mono: maskLeft/Right no se usan y synthMode se resuelve a sm2*).
        public void UpdateSynth()
        {
            if (chip.opl3Active != 0)
            {
                if (((chip.reg104 & fourMask) & 0x3f) != 0)
                {
                    OplChannel chan0, chan1;
                    if ((fourMask & 0x80) == 0) { chan0 = this; chan1 = chip.chan[idx + 1]; }
                    else { chan0 = chip.chan[idx - 1]; chan1 = this; }
                    int synth = ((chan0.regC0 & 1) << 0) | ((chan1.regC0 & 1) << 1);
                    chan0.synthMode = synth switch
                    {
                        0 => SynthMode.sm3FMFM, 1 => SynthMode.sm3AMFM,
                        2 => SynthMode.sm3FMAM, _ => SynthMode.sm3AMAM,
                    };
                }
                else if ((fourMask & 0x40) != 0 && (chip.regBD & 0x20) != 0) { /* percusión: no tocar */ }
                else if ((regC0 & 1) != 0) synthMode = SynthMode.sm3AM;
                else synthMode = SynthMode.sm3FM;
                maskLeft = (regC0 & 0x10) != 0 ? -1 : 0;
                maskRight = (regC0 & 0x20) != 0 ? -1 : 0;
            }
            else
            {
                if ((fourMask & 0x40) != 0 && (chip.regBD & 0x20) != 0) { /* percusión: no tocar */ }
                else if ((regC0 & 1) != 0) synthMode = SynthMode.sm2AM;
                else synthMode = SynthMode.sm2FM;
            }
        }

        // dbopl.cpp:804-856 (GeneratePercussion<false>, mono)
        private void GeneratePercussion(int[] output, int outOff)
        {
            unchecked
            {
                int mod = (int)((uint)(old[0] + old[1]) >> feedback);
                old[0] = old[1];
                old[1] = Op(0).GetSample(mod);

                if ((regC0 & 1) != 0) mod = 0; else mod = old[0];
                int sample = Op(1).GetSample(mod);

                uint noiseBit = chip.ForwardNoise() & 0x1;
                uint c2 = Op(2).ForwardWave();
                uint c5 = Op(5).ForwardWave();
                uint phaseBit = (((c2 & 0x88) ^ ((c2 << 5) & 0x80)) | ((c5 ^ (c5 << 2)) & 0x20)) != 0 ? 0x02u : 0x00u;

                uint hhVol = (uint)Op(2).ForwardVolume();
                if (!EnvSilent((int)hhVol))
                {
                    uint hhIndex = (phaseBit << 8) | (uint)(0x34 << (int)(phaseBit ^ (noiseBit << 1)));
                    sample += Op(2).GetWave(hhIndex, (int)hhVol);
                }
                uint sdVol = (uint)Op(3).ForwardVolume();
                if (!EnvSilent((int)sdVol))
                {
                    uint sdIndex = (0x100u + (c2 & 0x100)) ^ (noiseBit << 8);
                    sample += Op(3).GetWave(sdIndex, (int)sdVol);
                }
                sample += Op(4).GetSample(0);
                uint tcVol = (uint)Op(5).ForwardVolume();
                if (!EnvSilent((int)tcVol))
                {
                    uint tcIndex = (1u + phaseBit) << 8;
                    sample += Op(5).GetWave(tcIndex, (int)tcVol);
                }
                sample <<= 1;
                output[outOff] += sample;   // mono
            }
        }

        // dbopl.cpp:858-986 (BlockTemplate<mode> aplanado; solo sm2AM/sm2FM/sm2Percussion en OPL2
        // mono). Devuelve el ÍNDICE del siguiente canal (this+1 / this+3 de dbopl).
        public int BlockTemplate(int[] output, int outOff, int samples)
        {
            SynthMode mode = synthMode;
            switch (mode)
            {
                case SynthMode.sm2AM:
                    if (Op(0).Silent() && Op(1).Silent()) { old[0] = old[1] = 0; return idx + 1; }
                    break;
                case SynthMode.sm2FM:
                    if (Op(1).Silent()) { old[0] = old[1] = 0; return idx + 1; }
                    break;
            }
            Op(0).Prepare(); Op(1).Prepare();
            if ((int)mode > (int)SynthMode.sm4Start) { Op(2).Prepare(); Op(3).Prepare(); }
            if ((int)mode > (int)SynthMode.sm6Start) { Op(4).Prepare(); Op(5).Prepare(); }

            unchecked
            {
                for (int i = 0; i < samples; i++)
                {
                    if (mode == SynthMode.sm2Percussion) { GeneratePercussion(output, outOff + i); continue; }

                    int mod = (int)((uint)(old[0] + old[1]) >> feedback);
                    old[0] = old[1];
                    old[1] = Op(0).GetSample(mod);
                    int out0 = old[0];
                    int sample = mode == SynthMode.sm2AM
                        ? out0 + Op(1).GetSample(0)
                        : Op(1).GetSample(out0);       // sm2FM
                    output[outOff + i] += sample;
                }
            }

            return mode == SynthMode.sm2Percussion ? idx + 3 : idx + 1;
        }
    }

    // ================================================================= Chip glue
    // dbopl.cpp:1000-1010
    private uint ForwardNoise()
    {
        unchecked
        {
            noiseCounter += noiseAdd;
            uint count = noiseCounter >> LFO_SH;
            noiseCounter &= (1u << LFO_SH) - 1;
            for (; count > 0; --count)
            {
                noiseValue ^= 0x800302u & (0u - (noiseValue & 1u));
                noiseValue >>= 1;
            }
            return noiseValue;
        }
    }

    // dbopl.cpp:1012-1036
    private uint ForwardLFO(uint samples)
    {
        unchecked
        {
            vibratoSign = (sbyte)(VibratoTable[vibratoIndex >> 2] >> 7);
            vibratoShift = (byte)((VibratoTable[vibratoIndex >> 2] & 7) + vibratoStrength);
            tremoloValue = (byte)(TremoloTable[tremoloIndex] >> tremoloStrength);

            uint todo = LFO_MAX - lfoCounter;
            uint count = (todo + lfoAdd - 1) / lfoAdd;
            if (count > samples)
            {
                count = samples;
                lfoCounter += count * lfoAdd;
            }
            else
            {
                lfoCounter += count * lfoAdd;
                lfoCounter &= LFO_MAX - 1;
                vibratoIndex = (byte)((vibratoIndex + 1) & 31);
                if (tremoloIndex + 1 < TREMOLO_TABLE) ++tremoloIndex;
                else tremoloIndex = 0;
            }
            return count;
        }
    }

    // dbopl.cpp:1039-1100
    private void WriteBD(byte val)
    {
        int change = regBD ^ val;
        if (change == 0) return;
        regBD = val;
        vibratoStrength = (byte)((val & 0x40) != 0 ? 0x00 : 0x01);
        tremoloStrength = (byte)((val & 0x80) != 0 ? 0x00 : 0x02);
        if ((val & 0x20) != 0)
        {
            if ((change & 0x20) != 0)
                chan[6].synthMode = opl3Active != 0 ? SynthMode.sm3Percussion : SynthMode.sm2Percussion;
            if ((val & 0x10) != 0) { chan[6].op[0].KeyOn(0x2); chan[6].op[1].KeyOn(0x2); }
            else { chan[6].op[0].KeyOff(0x2); chan[6].op[1].KeyOff(0x2); }
            if ((val & 0x1) != 0) chan[7].op[0].KeyOn(0x2); else chan[7].op[0].KeyOff(0x2);
            if ((val & 0x8) != 0) chan[7].op[1].KeyOn(0x2); else chan[7].op[1].KeyOff(0x2);
            if ((val & 0x4) != 0) chan[8].op[0].KeyOn(0x2); else chan[8].op[0].KeyOff(0x2);
            if ((val & 0x2) != 0) chan[8].op[1].KeyOn(0x2); else chan[8].op[1].KeyOff(0x2);
        }
        else if ((change & 0x20) != 0)
        {
            chan[6].UpdateSynth();
            chan[6].op[0].KeyOff(0x2); chan[6].op[1].KeyOff(0x2);
            chan[7].op[0].KeyOff(0x2); chan[7].op[1].KeyOff(0x2);
            chan[8].op[0].KeyOff(0x2); chan[8].op[1].KeyOff(0x2);
        }
    }

    // dbopl.cpp:1118-1122
    private void UpdateSynths() { for (int i = 0; i < 18; i++) chan[i].UpdateSynth(); }

    // dbopl.cpp:1103-1187. REGOP/REGCHAN → tablas de índice (SPEC-OPL §3.7).
    public void WriteReg(uint reg, byte val)
    {
        int index;
        switch ((reg & 0xf0) >> 4)
        {
            case 0x00 >> 4:
                if (reg == 0x01) waveFormMask = (byte)(((val & 0x20) != 0 || opl3Mode) ? 0x7 : 0x0);
                else if (reg == 0x104)
                {
                    if (((reg104 ^ val) & 0x3f) == 0) return;
                    reg104 = (byte)(0x80 | (val & 0x3f));
                    UpdateSynths();
                }
                else if (reg == 0x105)
                {
                    if (((opl3Active ^ val) & 1) == 0) return;
                    opl3Active = (val & 1) != 0 ? 0xff : 0;
                    UpdateSynths();
                }
                else if (reg == 0x08) reg08 = val;
                break;   // case 0x00 cae a 0x10 (break) en dbopl → equivalente
            case 0x10 >> 4:
                break;
            case 0x20 >> 4:
            case 0x30 >> 4:
                index = (int)(((reg >> 3) & 0x20) | (reg & 0x1f));
                { int o = OpIndexTable[index]; if (o >= 0) chan[o >> 1].op[o & 1].Write20(val); }
                break;
            case 0x40 >> 4:
            case 0x50 >> 4:
                index = (int)(((reg >> 3) & 0x20) | (reg & 0x1f));
                { int o = OpIndexTable[index]; if (o >= 0) chan[o >> 1].op[o & 1].Write40(val); }
                break;
            case 0x60 >> 4:
            case 0x70 >> 4:
                index = (int)(((reg >> 3) & 0x20) | (reg & 0x1f));
                { int o = OpIndexTable[index]; if (o >= 0) chan[o >> 1].op[o & 1].Write60(val); }
                break;
            case 0x80 >> 4:
            case 0x90 >> 4:
                index = (int)(((reg >> 3) & 0x20) | (reg & 0x1f));
                { int o = OpIndexTable[index]; if (o >= 0) chan[o >> 1].op[o & 1].Write80(val); }
                break;
            case 0xa0 >> 4:
                index = (int)(((reg >> 4) & 0x10) | (reg & 0xf));
                { int ch = ChanIndexTable[index]; if (ch >= 0) chan[ch].WriteA0(val); }
                break;
            case 0xb0 >> 4:
                if (reg == 0xbd) WriteBD(val);
                else
                {
                    index = (int)(((reg >> 4) & 0x10) | (reg & 0xf));
                    { int ch = ChanIndexTable[index]; if (ch >= 0) chan[ch].WriteB0(val); }
                }
                break;
            case 0xc0 >> 4:
                index = (int)(((reg >> 4) & 0x10) | (reg & 0xf));
                { int ch = ChanIndexTable[index]; if (ch >= 0) chan[ch].WriteC0(val); }
                break;   // case 0xc0 cae a 0xd0 (break) en dbopl → equivalente
            case 0xd0 >> 4:
                break;
            case 0xe0 >> 4:
            case 0xf0 >> 4:
                index = (int)(((reg >> 3) & 0x20) | (reg & 0x1f));
                { int o = OpIndexTable[index]; if (o >= 0) chan[o >> 1].op[o & 1].WriteE0(val); }
                break;
        }
    }

    // dbopl.cpp:1190-1201
    public uint WriteAddr(uint port, byte val)
    {
        switch (port & 3)
        {
            case 0: return val;
            case 2:
                if (opl3Active != 0 || val == 0x05u) return 0x100u | val;
                return val;
        }
        return 0u;
    }

    // dbopl.cpp:1203-1215 — salida int32 mono.
    public void GenerateBlock2(uint total, int[] output)
    {
        int outBase = 0;
        while (total > 0)
        {
            uint samples = ForwardLFO(total);
            for (int k = 0; k < samples; k++) output[outBase + k] = 0;   // memset
            int ch = 0;
            while (ch < 9) ch = chan[ch].BlockTemplate(output, outBase, (int)samples);
            total -= samples;
            outBase += (int)samples;
        }
    }

    // dbopl.cpp:1231-1354
    private void Setup(uint rate)
    {
        double scale = OPLRATE / rate;

        noiseAdd = (uint)(0.5 + scale * (1 << LFO_SH));
        noiseCounter = 0;
        noiseValue = 1;
        lfoAdd = (uint)(0.5 + scale * (1 << LFO_SH));
        lfoCounter = 0;
        vibratoIndex = 0;
        tremoloIndex = 0;

        uint freqScale = (uint)(0.5 + scale * (1 << (WAVE_SH - 1 - 10)));
        for (int i = 0; i < 16; i++) freqMul[i] = freqScale * FreqCreateTable[i];

        for (int i = 0; i < 76; i++)
        {
            EnvelopeSelect((byte)i, out byte idx, out byte shift);
            linearRates[i] = (uint)(scale * (EnvelopeIncreaseTable[idx] << (RATE_SH + ENV_EXTRA - shift - 3)));
        }
        for (int i = 0; i < 62; i++)
        {
            EnvelopeSelect((byte)i, out byte idx, out byte shift);
            int originalAmount = (int)(uint)((AttackSamplesTable[idx] << shift) / scale);
            int guessAdd = (int)(uint)(scale * (EnvelopeIncreaseTable[idx] << (RATE_SH - shift - 3)));
            int bestAdd = guessAdd;
            uint bestDiff = 1u << 30;
            for (uint passes = 0; passes < 16; passes++)
            {
                int volume = ENV_MAX;
                int samples = 0;
                uint count = 0;
                while (volume > 0 && samples < originalAmount * 2)
                {
                    unchecked { count += (uint)guessAdd; }
                    int chg = (int)(count >> RATE_SH);
                    count &= RATE_MASK;
                    if (chg != 0) volume += (~volume * chg) >> 3;
                    samples++;
                }
                int diff = originalAmount - samples;
                uint lDiff = (uint)Math.Abs(diff);
                if (lDiff < bestDiff) { bestDiff = lDiff; bestAdd = guessAdd; if (bestDiff == 0) break; }
                double correct = (originalAmount - diff) / (double)originalAmount;
                guessAdd = (int)(guessAdd * correct);
                if (diff < 0) guessAdd++;
            }
            attackRates[i] = (uint)bestAdd;
        }
        for (int i = 62; i < 76; i++) attackRates[i] = 8u << RATE_SH;

        // four-op flags por canal INTERNO (dbopl.cpp:1321-1338)
        chan[0].fourMask = 0x00 | (1 << 0);
        chan[1].fourMask = 0x80 | (1 << 0);
        chan[2].fourMask = 0x00 | (1 << 1);
        chan[3].fourMask = 0x80 | (1 << 1);
        chan[4].fourMask = 0x00 | (1 << 2);
        chan[5].fourMask = 0x80 | (1 << 2);
        chan[9].fourMask = 0x00 | (1 << 3);
        chan[10].fourMask = 0x80 | (1 << 3);
        chan[11].fourMask = 0x00 | (1 << 4);
        chan[12].fourMask = 0x80 | (1 << 4);
        chan[13].fourMask = 0x00 | (1 << 5);
        chan[14].fourMask = 0x80 | (1 << 5);
        chan[6].fourMask = 0x40; chan[7].fourMask = 0x40; chan[8].fourMask = 0x40;

        // barrido de reset: cada registro a 0xFF y luego 0x00 (dbopl.cpp:1340-1353)
        WriteReg(0x105, 0x1);
        for (uint i = 0; i < 512; i++) { if (i == 0x105) continue; WriteReg(i, 0xff); WriteReg(i, 0x0); }
        WriteReg(0x105, 0x0);
        for (uint i = 0; i < 255; i++) { WriteReg(i, 0xff); WriteReg(i, 0x0); }
    }

    // dbopl.cpp:202-213
    private static void EnvelopeSelect(byte val, out byte index, out byte shift)
    {
        if (val < 13 * 4) { shift = (byte)(12 - (val >> 2)); index = (byte)(val & 3); }
        else if (val < 15 * 4) { shift = 0; index = (byte)(val - 12 * 4); }
        else { shift = 0; index = 12; }
    }

    // dbopl.cpp:1357-1507 (rama WAVE_TABLEMUL)
    private static void InitTables()
    {
        if (_tablesDone) return;
        lock (_tablesLock)
        {
            if (_tablesDone) return;

            for (int i = 0; i < 384; i++)
            {
                int s = i * 8;
                double val = 0.5 + Math.Pow(2.0, -1.0 + (255 - s) * (1.0 / 256)) * (1 << MUL_SH);
                MulTable[i] = (ushort)val;
            }

            for (int i = 0; i < 512; i++)
            {
                WaveTable[0x0200 + i] = (short)(Math.Sin((i + 0.5) * (Math.PI / 512.0)) * 4084);
                WaveTable[0x0000 + i] = (short)-WaveTable[0x200 + i];
            }
            for (int i = 0; i < 256; i++)
            {
                WaveTable[0x700 + i] = (short)(0.5 + Math.Pow(2.0, -1.0 + (255 - i * 8) * (1.0 / 256)) * 4085);
                WaveTable[0x6ff - i] = (short)-WaveTable[0x700 + i];
            }
            for (int i = 0; i < 256; i++)
            {
                WaveTable[0x400 + i] = WaveTable[0];
                WaveTable[0x500 + i] = WaveTable[0];
                WaveTable[0x900 + i] = WaveTable[0];
                WaveTable[0xc00 + i] = WaveTable[0];
                WaveTable[0xd00 + i] = WaveTable[0];
                WaveTable[0x800 + i] = WaveTable[0x200 + i];
                WaveTable[0xa00 + i] = WaveTable[0x200 + i * 2];
                WaveTable[0xb00 + i] = WaveTable[0x000 + i * 2];
                WaveTable[0xe00 + i] = WaveTable[0x200 + i * 2];
                WaveTable[0xf00 + i] = WaveTable[0x200 + i * 2];
            }

            for (int oct = 0; oct < 8; oct++)
            {
                int baseVal = oct * 8;
                for (int i = 0; i < 16; i++)
                {
                    int val = baseVal - KslCreateTable[i];
                    if (val < 0) val = 0;
                    KslTable[oct * 16 + i] = (byte)(val * 4);
                }
            }
            for (int i = 0; i < TREMOLO_TABLE / 2; i++)
            {
                byte val = (byte)(i << ENV_EXTRA);
                TremoloTable[i] = val;
                TremoloTable[TREMOLO_TABLE - 1 - i] = val;
            }

            // Tablas de canal/operador → ÍNDICE (dbopl.cpp:1451-1478 sin sizeof).
            for (int i = 0; i < 32; i++)
            {
                int index = i & 0xf;
                if (index >= 9) { ChanIndexTable[i] = -1; continue; }
                if (index < 6) index = (index % 3) * 2 + (index / 3);
                if (i >= 16) index += 9;
                ChanIndexTable[i] = index;
            }
            for (int i = 0; i < 64; i++)
            {
                if (i % 8 >= 6 || (i / 8) % 4 == 3) { OpIndexTable[i] = -1; continue; }
                int chNum = (i / 8) * 3 + (i % 8) % 3;
                if (chNum >= 12) chNum += 16 - 12;
                int derived = ChanIndexTable[chNum];    // = transform(chNum) de ChanOffsetTable
                int opNum = (i % 8) / 3;
                OpIndexTable[i] = derived * 2 + opNum;
            }

            // auto-chequeo del invariante reg→(canal,operador) (dbopl.cpp:1479-1506 reactivado):
            // los 18 canales y 36 operadores internos deben ser ALCANZABLES por las tablas.
            var seenC = new bool[18];
            for (int c = 0; c < 32; c++) if (ChanIndexTable[c] >= 0) seenC[ChanIndexTable[c]] = true;
            for (int i = 0; i < 18; i++) if (!seenC[i]) throw new InvalidOperationException($"OPL: canal {i} no alcanzable en ChanIndexTable");
            var seenO = new bool[36];
            for (int c = 0; c < 64; c++) if (OpIndexTable[c] >= 0) seenO[OpIndexTable[c]] = true;
            for (int i = 0; i < 36; i++) if (!seenO[i]) throw new InvalidOperationException($"OPL: operador {i} no alcanzable en OpIndexTable");

            _tablesDone = true;
        }
    }
}
