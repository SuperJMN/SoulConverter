using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Dmg;

/// <summary>
/// The Game Boy DMG APU replayed into musical state at the log's own timing: two square channels
/// (the first with a sweep), the wave channel and the noise channel, with the 512 Hz frame
/// sequencer clocking length counters (256 Hz), the sweep (128 Hz) and the envelopes (64 Hz).
/// NR50's louder side scales every volume, NR51 silences a channel routed nowhere, and turning
/// NR52 off clears the APU. A frame is produced for every group of writes and for every sequencer
/// step that changes a voice, so nothing is quantised to a grid here.
/// </summary>
internal sealed class DmgApu
{
    // 44100 / 512: the frame sequencer's step, in VGM samples. A log does not say where the
    // sequencer was when it started, so the steps are placed with the envelope clock on whole
    // multiples of 1/64 s.
    private const double StepSamples = VgmLog.SampleRate / 512.0;

    private Square[] squares = [new(hasSweep: true), new(hasSweep: false)];
    private Wave wave = new();
    private NoiseChannel noise = new();
    private readonly bool[] addressed = new bool[4];
    private readonly bool[] retriggered = new bool[4];
    private bool powered = true;
    private int masterVolume = 0x77;
    private int panning = 0xFF;

    public static MusicalTimeline Replay(VgmLog log, double tickHz)
    {
        var apu = new DmgApu();
        var frames = new List<MusicalFrame>();
        var powerWrites = new List<bool>();
        var writes = log.Writes;
        long nextStep = 1;

        void Emit(int sample)
        {
            var frame = apu.Snapshot(frames.Count, sample, powerWrites.Count == 0 ? [] : [.. powerWrites]);
            powerWrites.Clear();
            Array.Clear(apu.retriggered);
            if (frames.Count == 0 || frame.PowerWrites.Count != 0 || !SameState(frames[^1], frame))
            {
                frames.Add(frame);
            }
        }

        void RunStepsBefore(int limit)
        {
            while (Math.Round(nextStep * StepSamples) < limit)
            {
                if (apu.ClockSequencer(nextStep))
                {
                    Emit((int)Math.Round(nextStep * StepSamples));
                }

                nextStep++;
            }
        }

        var index = 0;
        while (index < writes.Count)
        {
            var sample = writes[index].Sample;
            RunStepsBefore(sample);
            for (; index < writes.Count && writes[index].Sample == sample; index++)
            {
                apu.Write(writes[index].Register, writes[index].Value, powerWrites);
            }

            Emit(sample);
        }

        RunStepsBefore(log.TotalSamples);
        return new MusicalTimeline
        {
            Log = log,
            TickHz = tickHz,
            Frames = frames,
            OnGrid = false,
            TotalSamples = log.TotalSamples,
        };
    }

    private static bool SameState(MusicalFrame previous, MusicalFrame next) =>
        previous.Tone1 == next.Tone1 with { Retriggered = previous.Tone1.Retriggered }
        && previous.Tone2 == next.Tone2 with { Retriggered = previous.Tone2.Retriggered }
        && previous.Tone3 == next.Tone3 with { Retriggered = previous.Tone3.Retriggered }
        && previous.Noise == next.Noise with { Retriggered = previous.Noise.Retriggered }
        && previous.StereoSplit == next.StereoSplit
        && !next.Tone1.Retriggered && !next.Tone2.Retriggered && !next.Tone3.Retriggered && !next.Noise.Retriggered;

    private void Write(int register, int value, List<bool> powerWrites)
    {
        if (register == GameBoy.NR52)
        {
            var on = (value & 0x80) != 0;
            powerWrites.Add(on);
            if (powered && !on)
            {
                // Power off clears every APU register except wave RAM.
                squares = [new(hasSweep: true), new(hasSweep: false)];
                wave = new Wave();
                noise = new NoiseChannel();
                masterVolume = 0;
                panning = 0;
                Array.Clear(addressed);
                Array.Clear(retriggered);
            }

            powered = on;
            return;
        }

        if (!powered || register >= GameBoy.WaveRamBase)
        {
            return;
        }

        switch (register)
        {
            case >= GameBoy.NR10 and <= GameBoy.NR14:
                addressed[0] = true;
                retriggered[0] |= squares[0].Write(register - GameBoy.NR10, value);
                break;
            case >= GameBoy.NR21 and <= GameBoy.NR24:
                addressed[1] = true;
                retriggered[1] |= squares[1].Write(register - GameBoy.NR21 + 1, value);
                break;
            case >= GameBoy.NR30 and <= GameBoy.NR34:
                addressed[2] = true;
                retriggered[2] |= wave.Write(register - GameBoy.NR30, value);
                break;
            case >= GameBoy.NR41 and <= GameBoy.NR44:
                addressed[3] = true;
                retriggered[3] |= noise.Write(register - GameBoy.NR41 + 1, value);
                break;
            case GameBoy.NR50:
                masterVolume = value;
                Array.Fill(addressed, true);
                break;
            case GameBoy.NR51:
                panning = value;
                Array.Fill(addressed, true);
                break;
        }
    }

    // Clocks sequencer step `step` (1, 2, 3...); true when a voice may have changed.
    private bool ClockSequencer(long step)
    {
        var changed = false;
        var phase = (int)(step % 8);
        if (phase % 2 == 1)
        {
            changed |= squares[0].Length.Clock() | squares[1].Length.Clock() | wave.Length.Clock() | noise.Length.Clock();
        }

        if (phase is 3 or 7)
        {
            changed |= squares[0].ClockSweep();
        }

        if (phase == 0)
        {
            changed |= squares[0].Envelope.Clock() | squares[1].Envelope.Clock() | noise.Envelope.Clock();
        }

        return changed;
    }

    private MusicalFrame Snapshot(int index, int sample, IReadOnlyList<bool> powerWrites)
    {
        var tone1 = SquareVoice(0);
        var tone2 = SquareVoice(1);
        var waveVolume = Scaled(wave.Volume);
        var tone3 = new ToneVoice(
            Playing: wave.Sounding && Routed(2) && waveVolume > 0,
            FrequencyHz: wave.Started ? wave.FrequencyHz : 0.0,
            Volume: waveVolume,
            Duty: 2,
            Timbre: Timbre.Wave,
            Retriggered: retriggered[2],
            Addressed: addressed[2]);
        var noiseVolume = Scaled(noise.Envelope.Volume);
        var noiseVoice = new NoiseVoice(
            Playing: noise.Enabled && noise.Envelope.DacOn && Routed(3) && noiseVolume > 0,
            ClockRateHz: GameBoy.NoiseFrequency(noise.Shift, noise.Divisor),
            Volume: noiseVolume,
            Short: noise.ShortWidth,
            Retriggered: retriggered[3],
            RateIndex: (noise.Shift << 4) | noise.Divisor,
            Addressed: addressed[3],
            Started: noise.Started);

        bool[] sounding = [tone1.Playing, tone2.Playing, tone3.Playing, noiseVoice.Playing];
        var split = false;
        for (var channel = 0; channel < 4; channel++)
        {
            split |= sounding[channel] && ((panning >> channel) & 1) != ((panning >> (channel + 4)) & 1);
        }

        return new MusicalFrame
        {
            Index = index,
            Sample = sample,
            Tone1 = tone1,
            Tone2 = tone2,
            Tone3 = tone3,
            Noise = noiseVoice,
            PowerWrites = powerWrites,
            StereoSplit = split,
        };
    }

    private ToneVoice SquareVoice(int channel)
    {
        var square = squares[channel];
        var volume = Scaled(square.Envelope.Volume);
        return new ToneVoice(
            Playing: square.Enabled && square.Envelope.DacOn && Routed(channel) && volume > 0,
            FrequencyHz: square.Started ? square.FrequencyHz : 0.0,
            Volume: volume,
            Duty: square.Duty,
            Timbre: Timbre.Pulse,
            Retriggered: retriggered[channel],
            Addressed: addressed[channel]);
    }

    // A channel routed to neither output is silent; NR50's louder side scales the rest.
    private bool Routed(int channel) => (panning & (0x11 << channel)) != 0;

    private int Scaled(int volume)
    {
        var level = Math.Max((masterVolume >> 4) & 7, masterVolume & 7) + 1;
        return (int)Math.Round(volume * level / 8.0);
    }

    private sealed class VolumeEnvelope
    {
        private int timer;
        private int initial;
        private bool increase;
        private int period;

        public int Volume { get; private set; }

        /// <summary>The DAC is on while the initial volume or the direction is set.</summary>
        public bool DacOn => initial != 0 || increase;

        public void Trigger()
        {
            Volume = initial;
            timer = period;
        }

        public bool Clock()
        {
            if (period == 0 || --timer > 0)
            {
                return false;
            }

            timer = period;
            var before = Volume;
            Volume = Math.Clamp(Volume + (increase ? 1 : -1), 0, 15);
            return before != Volume;
        }

        public void Write(int value)
        {
            initial = value >> 4;
            increase = (value & 0x08) != 0;
            period = value & 7;
        }
    }

    // A length counter that, when enabled, turns its channel off as it reaches zero.
    private sealed class LengthCounter(int maximum, Action expire)
    {
        private int remaining;

        public bool Enabled { get; set; }

        public void Load(int value) => remaining = maximum - value;

        public void Trigger()
        {
            if (remaining == 0)
            {
                remaining = maximum;
            }
        }

        public bool Clock()
        {
            if (Enabled && remaining > 0 && --remaining == 0)
            {
                expire();
                return true;
            }

            return false;
        }
    }

    private sealed class Square
    {
        private readonly bool hasSweep;
        private int period;
        private int sweepPeriod;
        private bool sweepNegate;
        private int sweepShift;
        private int sweepTimer;
        private int shadow;
        private bool sweepEnabled;

        public Square(bool hasSweep)
        {
            this.hasSweep = hasSweep;
            Length = new LengthCounter(64, () => Enabled = false);
        }

        public bool Enabled { get; private set; }

        /// <summary>The channel has been triggered since power on.</summary>
        public bool Started { get; private set; }

        public int Duty { get; private set; }

        public LengthCounter Length { get; }

        public VolumeEnvelope Envelope { get; } = new();

        public double FrequencyHz => GameBoy.SquareFreqNumerator / (2048 - period);

        // Registers from NRx0 (the sweep, on the first square only) to NRx4; true on a trigger.
        public bool Write(int register, int value)
        {
            switch (register)
            {
                case 0 when hasSweep:
                    sweepPeriod = (value >> 4) & 7;
                    sweepNegate = (value & 0x08) != 0;
                    sweepShift = value & 7;
                    break;
                case 1:
                    Duty = value >> 6;
                    Length.Load(value & 0x3F);
                    break;
                case 2:
                    Envelope.Write(value);
                    Enabled &= Envelope.DacOn;
                    break;
                case 3:
                    period = (period & 0x700) | value;
                    break;
                case 4:
                    period = (period & 0x0FF) | ((value & 7) << 8);
                    Length.Enabled = (value & 0x40) != 0;
                    if ((value & 0x80) != 0)
                    {
                        Trigger();
                        return true;
                    }

                    break;
            }

            return false;
        }

        public bool ClockSweep()
        {
            if (!hasSweep || --sweepTimer > 0)
            {
                return false;
            }

            sweepTimer = sweepPeriod == 0 ? 8 : sweepPeriod;
            if (!sweepEnabled || sweepPeriod == 0)
            {
                return false;
            }

            var next = NextSweep();
            if (next > 2047)
            {
                Enabled = false;
            }
            else if (sweepShift != 0)
            {
                shadow = next;
                period = next;
                if (NextSweep() > 2047)
                {
                    Enabled = false;
                }
            }

            return true;
        }

        private int NextSweep() => sweepNegate ? shadow - (shadow >> sweepShift) : shadow + (shadow >> sweepShift);

        private void Trigger()
        {
            Started = true;
            Enabled = Envelope.DacOn;
            Length.Trigger();
            Envelope.Trigger();
            if (hasSweep)
            {
                shadow = period;
                sweepTimer = sweepPeriod == 0 ? 8 : sweepPeriod;
                sweepEnabled = sweepPeriod != 0 || sweepShift != 0;
                if (sweepShift != 0 && NextSweep() > 2047)
                {
                    Enabled = false;
                }
            }
        }
    }

    private sealed class Wave
    {
        private bool dacOn;
        private int level;
        private int period;
        private bool enabled;

        public Wave() => Length = new LengthCounter(256, () => enabled = false);

        public bool Started { get; private set; }

        public LengthCounter Length { get; }

        public bool Sounding => enabled && dacOn && level != 0;

        // Output levels 0-3: mute, 100%, 50%, 25%.
        public int Volume => level == 0 ? 0 : 15 >> (level - 1);

        public double FrequencyHz => GameBoy.WaveFreqNumerator / (2048 - period);

        public bool Write(int register, int value)
        {
            switch (register)
            {
                case 0:
                    dacOn = (value & 0x80) != 0;
                    enabled &= dacOn;
                    break;
                case 1:
                    Length.Load(value);
                    break;
                case 2:
                    level = (value >> 5) & 3;
                    break;
                case 3:
                    period = (period & 0x700) | value;
                    break;
                case 4:
                    period = (period & 0x0FF) | ((value & 7) << 8);
                    Length.Enabled = (value & 0x40) != 0;
                    if ((value & 0x80) != 0)
                    {
                        Started = true;
                        enabled = dacOn;
                        Length.Trigger();
                        return true;
                    }

                    break;
            }

            return false;
        }
    }

    private sealed class NoiseChannel
    {
        public NoiseChannel() => Length = new LengthCounter(64, () => Enabled = false);

        public bool Enabled { get; private set; }

        public bool Started { get; private set; }

        public int Shift { get; private set; }

        public bool ShortWidth { get; private set; }

        public int Divisor { get; private set; }

        public LengthCounter Length { get; }

        public VolumeEnvelope Envelope { get; } = new();

        public bool Write(int register, int value)
        {
            switch (register)
            {
                case 1:
                    Length.Load(value & 0x3F);
                    break;
                case 2:
                    Envelope.Write(value);
                    Enabled &= Envelope.DacOn;
                    break;
                case 3:
                    Shift = value >> 4;
                    ShortWidth = (value & 0x08) != 0;
                    Divisor = value & 7;
                    break;
                case 4:
                    Length.Enabled = (value & 0x40) != 0;
                    if ((value & 0x80) != 0)
                    {
                        Started = true;
                        Enabled = Envelope.DacOn;
                        Length.Trigger();
                        Envelope.Trigger();
                        return true;
                    }

                    break;
            }

            return false;
        }
    }
}
