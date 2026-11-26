using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Nes;

/// <summary>
/// A transcription-oriented 2A03 model. No other chip has the 2A03's envelope, length counter,
/// linear counter or sweep units, so those units are run here and the resulting musical state
/// (pitch, volume, duty, on/off) is sampled on a fixed tick grid. The DMC is modelled as far as
/// which sample plays and when; turning its waveform into something another chip can play is the
/// job of the DMG writer.
/// </summary>
internal sealed class NesApu
{
    public const double DefaultTickHz = 60.0;

    public static readonly int[] LengthTable =
    [
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    ];

    // NTSC noise timer periods, in CPU cycles, indexed by the low nibble of $400E.
    public static readonly int[] NoisePeriodTable = [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068];

    // Frame sequencer step boundaries in CPU cycles, and what each step clocks: quarter frames
    // clock envelopes and the linear counter, half frames clock lengths and sweeps.
    private static readonly (int Boundary, bool Quarter, bool Half)[] FourStep =
        [(7457, true, false), (14913, true, true), (22371, true, false), (29829, true, true)];

    private const int FourStepLength = 29830;

    private static readonly (int Boundary, bool Quarter, bool Half)[] FiveStep =
        [(7457, true, true), (14913, true, false), (22371, true, true), (29829, true, false), (37281, false, false)];

    private const int FiveStepLength = 37282;

    private readonly int cpuClockHz;
    private readonly Pulse pulse1 = new(isPulse1: true);
    private readonly Pulse pulse2 = new(isPulse1: false);
    private readonly Triangle triangle = new();
    private readonly Noise noise = new();
    private readonly Dmc dmc = new();
    private long cycle;
    private long sequenceCycle;
    private int step;
    private bool fiveStep;

    private NesApu(int cpuClockHz) => this.cpuClockHz = cpuClockHz;

    /// <summary>Runs <paramref name="log"/> through the APU model and samples it on a tick grid.</summary>
    public static MusicalTimeline Simulate(VgmLog log, double tickHz)
    {
        if (!(tickHz > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(tickHz), tickHz, "The tick rate must be positive.");
        }

        var apu = new NesApu(log.ClockHz);
        var cyclesPerSample = log.ClockHz / (double)VgmLog.SampleRate;
        var samplesPerTick = VgmLog.SampleRate / tickHz;
        var tickCount = Math.Max(1, (int)Math.Ceiling(log.TotalSamples / samplesPerTick));
        var ticks = new List<MusicalFrame>(tickCount);
        var writes = log.Writes;
        var writeIndex = 0;

        for (var index = 0; index < tickCount; index++)
        {
            var sample = (int)Math.Round(index * samplesPerTick);
            while (writeIndex < writes.Count && writes[writeIndex].Sample <= sample)
            {
                var write = writes[writeIndex++];
                apu.RunToCycle((long)Math.Round(write.Sample * cyclesPerSample));
                apu.Write(write.Register, write.Value);
            }

            apu.RunToCycle((long)Math.Round(sample * cyclesPerSample));
            ticks.Add(apu.Snapshot(index, sample));
        }

        return new MusicalTimeline
        {
            Log = log,
            TickHz = tickHz,
            Frames = ticks,
            OnGrid = true,
            LoopFrame = log.LoopSample is { } loop ? Math.Min(tickCount - 1, (int)Math.Round(loop / samplesPerTick)) : null,
            TotalSamples = log.TotalSamples,
        };
    }

    private void Write(int register, int value)
    {
        switch (register)
        {
            case <= 0x03:
                pulse1.Write(register, value);
                break;
            case <= 0x07:
                pulse2.Write(register - 0x04, value);
                break;
            case <= 0x0B:
                triangle.Write(register - 0x08, value);
                break;
            case <= 0x0F:
                noise.Write(register - 0x0C, value);
                break;
            case <= 0x13:
                dmc.Write(register - 0x10, value);
                break;
            case 0x15:
                pulse1.SetEnabled((value & 0x01) != 0);
                pulse2.SetEnabled((value & 0x02) != 0);
                triangle.SetEnabled((value & 0x04) != 0);
                noise.SetEnabled((value & 0x08) != 0);
                dmc.SetEnabled((value & 0x10) != 0, cycle);
                break;
            case 0x17:
                fiveStep = (value & 0x80) != 0;
                sequenceCycle = 0;
                step = 0;
                if (fiveStep)
                {
                    ClockQuarter();
                    ClockHalf();
                }

                break;
        }
    }

    private void RunToCycle(long target)
    {
        dmc.RunToCycle(target);
        while (cycle < target)
        {
            var steps = fiveStep ? FiveStep : FourStep;
            var total = fiveStep ? FiveStepLength : FourStepLength;
            if (step >= steps.Length)
            {
                step = 0;
                sequenceCycle -= total;
            }

            var (boundary, quarter, half) = steps[step];
            var remaining = boundary - sequenceCycle;
            var advance = Math.Min(remaining, target - cycle);
            cycle += advance;
            sequenceCycle += advance;
            if (advance < remaining)
            {
                return;
            }

            if (quarter)
            {
                ClockQuarter();
            }

            if (half)
            {
                ClockHalf();
            }

            step++;
        }
    }

    private void ClockQuarter()
    {
        pulse1.Envelope.Clock();
        pulse2.Envelope.Clock();
        noise.Envelope.Clock();
        triangle.ClockLinear();
    }

    private void ClockHalf()
    {
        pulse1.ClockLength();
        pulse1.ClockSweep();
        pulse2.ClockLength();
        pulse2.ClockSweep();
        triangle.ClockLength();
        noise.ClockLength();
    }

    private MusicalFrame Snapshot(int index, int sample)
    {
        var frame = new MusicalFrame
        {
            Index = index,
            Sample = sample,
            Tone1 = PulseState(pulse1),
            Tone2 = PulseState(pulse2),
            Tone3 = TriangleState(),
            Noise = NoiseState(),
            Dmc = dmc.State(cycle),
        };
        pulse1.Retriggered = false;
        pulse2.Retriggered = false;
        triangle.Retriggered = false;
        noise.Retriggered = false;
        dmc.Started = false;
        return frame;
    }

    private ToneVoice PulseState(Pulse pulse)
    {
        var volume = pulse.Envelope.Output;
        return new ToneVoice(
            Playing: pulse.Length > 0 && !pulse.SweepMuted && volume > 0,
            FrequencyHz: pulse.TimerPeriod >= 8 ? cpuClockHz / (16.0 * (pulse.TimerPeriod + 1)) : 0.0,
            Volume: volume,
            Duty: pulse.Duty,
            Timbre: Timbre.Pulse,
            Retriggered: pulse.Retriggered,
            ConstantVolume: pulse.Envelope.Constant,
            EnvelopePeriod: pulse.Envelope.Period,
            EnvelopeLoop: pulse.Envelope.Loop);
    }

    // The triangle has no volume control: it is on or it is off.
    private ToneVoice TriangleState() => new(
        Playing: triangle.Length > 0 && triangle.Linear > 0 && triangle.TimerPeriod >= 2,
        FrequencyHz: triangle.TimerPeriod >= 2 ? cpuClockHz / (32.0 * (triangle.TimerPeriod + 1)) : 0.0,
        Volume: 15,
        Duty: 2,
        Timbre: Timbre.Triangle,
        Retriggered: triangle.Retriggered);

    private NoiseVoice NoiseState()
    {
        var volume = noise.Envelope.Output;
        return new NoiseVoice(
            Playing: noise.Length > 0 && volume > 0,
            ClockRateHz: cpuClockHz / (double)NoisePeriodTable[noise.PeriodIndex],
            Volume: volume,
            Short: noise.ModeShort,
            Retriggered: noise.Retriggered,
            ConstantVolume: noise.Envelope.Constant,
            EnvelopePeriod: noise.Envelope.Period,
            EnvelopeLoop: noise.Envelope.Loop,
            RateIndex: noise.PeriodIndex);
    }

    /// <summary>The envelope generator shared by both pulses and the noise channel.</summary>
    private sealed class EnvelopeUnit
    {
        private int divider;
        private int decay;

        public bool Constant;
        public bool Loop;
        public int Period;
        public bool Start;

        public int Output => Constant ? Period : decay;

        public void Clock()
        {
            if (Start)
            {
                Start = false;
                decay = 15;
                divider = Period;
                return;
            }

            if (divider == 0)
            {
                divider = Period;
                if (decay > 0)
                {
                    decay--;
                }
                else if (Loop)
                {
                    decay = 15;
                }
            }
            else
            {
                divider--;
            }
        }
    }

    private sealed class Pulse(bool isPulse1)
    {
        private bool enabled;
        private bool sweepEnabled;
        private int sweepPeriod;
        private bool sweepNegate;
        private int sweepShift;
        private int sweepDivider;
        private bool sweepReload;

        public EnvelopeUnit Envelope { get; } = new();

        public int Duty;
        public int TimerPeriod;
        public int Length;
        public bool Retriggered;

        public bool SweepMuted => TimerPeriod < 8 || SweepTarget() > 0x07FF;

        public void Write(int index, int value)
        {
            switch (index)
            {
                case 0:
                    Duty = (value >> 6) & 0x03;
                    Envelope.Loop = (value & 0x20) != 0;
                    Envelope.Constant = (value & 0x10) != 0;
                    Envelope.Period = value & 0x0F;
                    break;
                case 1:
                    sweepEnabled = (value & 0x80) != 0;
                    sweepPeriod = (value >> 4) & 0x07;
                    sweepNegate = (value & 0x08) != 0;
                    sweepShift = value & 0x07;
                    sweepReload = true;
                    break;
                case 2:
                    TimerPeriod = (TimerPeriod & 0x0700) | value;
                    break;
                default:
                    TimerPeriod = (TimerPeriod & 0x00FF) | ((value & 0x07) << 8);
                    if (enabled)
                    {
                        Length = LengthTable[(value >> 3) & 0x1F];
                    }

                    Envelope.Start = true;
                    Retriggered = true;
                    break;
            }
        }

        public void SetEnabled(bool on)
        {
            enabled = on;
            if (!on)
            {
                Length = 0;
            }
        }

        public void ClockLength()
        {
            // The envelope loop flag doubles as the length counter halt.
            if (Length > 0 && !Envelope.Loop)
            {
                Length--;
            }
        }

        public void ClockSweep()
        {
            if (sweepDivider == 0 && sweepEnabled && sweepShift > 0 && !SweepMuted)
            {
                TimerPeriod = Math.Max(0, SweepTarget());
            }

            if (sweepDivider == 0 || sweepReload)
            {
                sweepDivider = sweepPeriod;
                sweepReload = false;
            }
            else
            {
                sweepDivider--;
            }
        }

        // Pulse 1 negates using one's complement, pulse 2 using two's.
        private int SweepTarget()
        {
            var change = TimerPeriod >> sweepShift;
            return sweepNegate ? TimerPeriod - change - (isPulse1 ? 1 : 0) : TimerPeriod + change;
        }
    }

    private sealed class Triangle
    {
        private bool enabled;
        private int linearReloadValue;
        private bool linearReload;
        private bool control;

        public int TimerPeriod;
        public int Length;
        public int Linear;
        public bool Retriggered;

        public void Write(int index, int value)
        {
            switch (index)
            {
                case 0:
                    control = (value & 0x80) != 0;
                    linearReloadValue = value & 0x7F;
                    break;
                case 2:
                    TimerPeriod = (TimerPeriod & 0x0700) | value;
                    break;
                case 3:
                    TimerPeriod = (TimerPeriod & 0x00FF) | ((value & 0x07) << 8);
                    if (enabled)
                    {
                        Length = LengthTable[(value >> 3) & 0x1F];
                    }

                    linearReload = true;
                    Retriggered = true;
                    break;
            }
        }

        public void SetEnabled(bool on)
        {
            enabled = on;
            if (!on)
            {
                Length = 0;
            }
        }

        public void ClockLength()
        {
            if (Length > 0 && !control)
            {
                Length--;
            }
        }

        public void ClockLinear()
        {
            if (linearReload)
            {
                Linear = linearReloadValue;
            }
            else if (Linear > 0)
            {
                Linear--;
            }

            if (!control)
            {
                linearReload = false;
            }
        }
    }

    private sealed class Noise
    {
        private bool enabled;

        public EnvelopeUnit Envelope { get; } = new();

        public int PeriodIndex;
        public bool ModeShort;
        public int Length;
        public bool Retriggered;

        public void Write(int index, int value)
        {
            switch (index)
            {
                case 0:
                    Envelope.Loop = (value & 0x20) != 0;
                    Envelope.Constant = (value & 0x10) != 0;
                    Envelope.Period = value & 0x0F;
                    break;
                case 2:
                    ModeShort = (value & 0x80) != 0;
                    PeriodIndex = value & 0x0F;
                    break;
                case 3:
                    if (enabled)
                    {
                        Length = LengthTable[(value >> 3) & 0x1F];
                    }

                    Envelope.Start = true;
                    Retriggered = true;
                    break;
            }
        }

        public void SetEnabled(bool on)
        {
            enabled = on;
            if (!on)
            {
                Length = 0;
            }
        }

        public void ClockLength()
        {
            if (Length > 0 && !Envelope.Loop)
            {
                Length--;
            }
        }
    }

    /// <summary>
    /// The sample channel, tracked as note-on and note-off instead of as audio: the sample's
    /// identity and a cycle-accurate end time are all another chip can act on.
    /// </summary>
    private sealed class Dmc
    {
        private bool enabled;
        private int rateIndex;
        private bool looping;
        private int directLevel;
        private int addressByte;
        private int lengthByte;
        private bool playing;
        private long startCycle;
        private long endCycle;
        private SampleRef? current;
        private SampleRef? lastStarted;

        public bool Started;

        public void Write(int index, int value)
        {
            switch (index)
            {
                case 0:
                    looping = (value & 0x40) != 0;
                    rateIndex = value & 0x0F;
                    break;
                case 1:
                    directLevel = value & 0x7F;
                    break;
                case 2:
                    addressByte = value;
                    break;
                case 3:
                    lengthByte = value;
                    break;
            }
        }

        // $4015 bit 4: clearing it silences the channel, setting it restarts. The hardware only
        // restarts a sample when the bit goes from clear to set or when the running sample has
        // already finished; writing the bit again mid-sample does not retrigger.
        public void SetEnabled(bool on, long atCycle)
        {
            enabled = on;
            if (!on)
            {
                playing = false;
                current = null;
                return;
            }

            if (!playing)
            {
                Restart(atCycle);
            }
        }

        public void RunToCycle(long target)
        {
            while (playing && target >= endCycle)
            {
                if (looping && enabled)
                {
                    Restart(endCycle);
                    continue;
                }

                playing = false;
                current = null;
            }
        }

        public SampleVoice State(long atCycle)
        {
            var elapsed = playing && current is not null ? Math.Max(0, atCycle - startCycle) / SampleRef.NtscCpuHz : 0.0;

            // A one-byte sample starts and finishes inside the same tick. It still started, so the
            // trigger names what was triggered.
            return new SampleVoice(playing, current ?? (Started ? lastStarted : null), Started, elapsed, looping, directLevel);
        }

        private void Restart(long atCycle)
        {
            var length = (lengthByte * 16) + 1;
            var sample = new SampleRef(0xC000 + (addressByte * 64), length, rateIndex);
            current = sample;
            lastStarted = sample;
            playing = true;
            Started = true;
            startCycle = atCycle;
            endCycle = atCycle + ((long)length * 8 * SampleRef.RateTable[rateIndex]);
        }
    }
}
