using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Analysis;

/// <summary>
/// Renders a Game Boy DMG log by replaying its bytes. It consumes the written file on purpose, not
/// the intermediate model, so a comparison measures the bytes that ship.
/// </summary>
internal static class DmgSynth
{
    private const double EnvelopePeriodSamples = Oscillators.SampleRate / 64.0;

    private static readonly double[] NoiseLong = Oscillators.LfsrSequence(bits: 15, tap: 1);
    private static readonly double[] NoiseShort = Oscillators.LfsrSequence(bits: 7, tap: 1);

    public static RenderedAudio Render(VgmLog log, double tickHz)
    {
        var events = log.Writes;
        var totalSamples = log.TotalSamples;
        var apu = new Apu();
        var output = new double[totalSamples];
        var position = 0;
        var eventIndex = 0;
        var nextEnvelope = EnvelopePeriodSamples;

        while (position < totalSamples)
        {
            while (eventIndex < events.Count && events[eventIndex].Sample <= position)
            {
                apu.Write(events[eventIndex].Register, events[eventIndex].Value);
                eventIndex++;
            }

            var boundary = totalSamples;
            if (eventIndex < events.Count)
            {
                boundary = Math.Min(boundary, events[eventIndex].Sample);
            }

            boundary = Math.Min(boundary, (int)Math.Round(nextEnvelope));
            boundary = Math.Max(boundary, position + 1);
            boundary = Math.Min(boundary, totalSamples);

            apu.RenderBlock(output.AsSpan(position, boundary - position));
            position = boundary;

            if (position >= (int)Math.Round(nextEnvelope))
            {
                apu.TickEnvelopes();
                nextEnvelope += EnvelopePeriodSamples;
            }
        }

        return new RenderedAudio(output, Frequencies(log, tickHz));
    }

    // The written stream's per-channel pitch, sampled on the conversion tick grid.
    private static Dictionary<string, double[]> Frequencies(VgmLog log, double tickHz)
    {
        var events = log.Writes;
        var apu = new Apu();
        var samplesPerTick = Oscillators.SampleRate / tickHz;
        var tickCount = Math.Max(1, (int)Math.Round(log.TotalSamples / samplesPerTick));
        var series = TimelineSynth.VoiceNames.ToDictionary(name => name, _ => new double[tickCount]);
        var eventIndex = 0;
        for (var tick = 0; tick < tickCount; tick++)
        {
            var sample = (int)Math.Round(tick * samplesPerTick);
            while (eventIndex < events.Count && events[eventIndex].Sample <= sample)
            {
                apu.Write(events[eventIndex].Register, events[eventIndex].Value);
                eventIndex++;
            }

            series["pulse1"][tick] = apu.Enabled[0] ? apu.SquareFrequency(0x03, 0x04) : 0.0;
            series["pulse2"][tick] = apu.Enabled[1] ? apu.SquareFrequency(0x08, 0x09) : 0.0;

            // CH3 rests at output level 0 instead of being disabled, so a running channel turned
            // all the way down is not sounding a note.
            series["triangle"][tick] = apu.Enabled[2] && (apu.Registers[0x0C] & 0x60) != 0 ? apu.WaveFrequency() : 0.0;
            series["noise"][tick] = apu.Enabled[3] ? apu.NoiseRate().Rate : 0.0;
        }

        return series;
    }

    private sealed class Envelope
    {
        private int direction;
        private int period;
        private int timer;

        public int Volume;

        public void Trigger(int register)
        {
            Volume = register >> 4;
            direction = (register >> 3) & 1;
            period = register & 0x07;
            timer = period;
        }

        public void Tick()
        {
            if (period == 0 || --timer > 0)
            {
                return;
            }

            timer = period;
            if (direction != 0 && Volume < 15)
            {
                Volume++;
            }
            else if (direction == 0 && Volume > 0)
            {
                Volume--;
            }
        }
    }

    /// <summary>Just enough DMG APU to render the channels a converted log uses.</summary>
    private sealed class Apu
    {
        private readonly double[] phases = new double[4];
        private readonly Envelope[] envelopes = [new(), new(), new(), new()];
        private readonly double[] table = new double[32];
        private double[] block = new double[1];
        private double noisePosition;

        public int[] Registers { get; } = new int[0x30];

        public bool[] Enabled { get; } = new bool[4];

        public void Write(int register, int value)
        {
            if (register >= Registers.Length)
            {
                return;
            }

            Registers[register] = value & 0xFF;
            if (register == 0x16 && (value & 0x80) == 0)
            {
                Array.Clear(Enabled);
                return;
            }

            if (register == 0x02 && (value & 0xF8) == 0)
            {
                Enabled[0] = false;
            }

            if (register == 0x07 && (value & 0xF8) == 0)
            {
                Enabled[1] = false;
            }

            if (register == 0x0A && (value & 0x80) == 0)
            {
                Enabled[2] = false;
            }

            if (register == 0x11 && (value & 0xF8) == 0)
            {
                Enabled[3] = false;
            }

            if (register == 0x04 && (value & 0x80) != 0)
            {
                Trigger(0, 0x02);
            }
            else if (register == 0x09 && (value & 0x80) != 0)
            {
                Trigger(1, 0x07);
            }
            else if (register == 0x0E && (value & 0x80) != 0)
            {
                if ((Registers[0x0A] & 0x80) != 0)
                {
                    Enabled[2] = true;
                    phases[2] = 0.0;
                }
            }
            else if (register == 0x13 && (value & 0x80) != 0)
            {
                Trigger(3, 0x11);
            }
        }

        public void TickEnvelopes()
        {
            envelopes[0].Tick();
            envelopes[1].Tick();
            envelopes[3].Tick();
        }

        public double SquareFrequency(int low, int high) => 131072.0 / (2048 - (Registers[low] | ((Registers[high] & 0x07) << 8)));

        public double WaveFrequency() => 65536.0 / (2048 - (Registers[0x0D] | ((Registers[0x0E] & 0x07) << 8)));

        public (double Rate, bool Short) NoiseRate()
        {
            var register = Registers[0x12];
            var shift = register >> 4;
            var divisorCode = register & 0x07;
            var divisor = divisorCode == 0 ? 0.5 : divisorCode;
            return (shift > 13 ? 0.0 : 262144.0 / (divisor * Math.Pow(2, shift)), (register & 0x08) != 0);
        }

        public void RenderBlock(Span<double> total)
        {
            if (block.Length < total.Length)
            {
                block = new double[total.Length];
            }

            var span = block.AsSpan(0, total.Length);
            total.Clear();

            phases[0] = Oscillators.SquareBlock(span, phases[0], SquareFrequency(0x03, 0x04), (Registers[0x01] >> 6) & 0x03);
            Mix(total, span, Enabled[0], envelopes[0].Volume);

            phases[1] = Oscillators.SquareBlock(span, phases[1], SquareFrequency(0x08, 0x09), (Registers[0x06] >> 6) & 0x03);
            Mix(total, span, Enabled[1], envelopes[1].Volume);

            // Output levels 0-3: mute, 100%, 50%, 25%.
            var level = (Registers[0x0C] >> 5) & 0x03;
            for (var index = 0; index < 16; index++)
            {
                var value = Registers[0x20 + index];
                table[index * 2] = level == 0 ? 0.0 : (value >> 4) / Math.Pow(2, level - 1);
                table[(index * 2) + 1] = level == 0 ? 0.0 : (value & 0x0F) / Math.Pow(2, level - 1);
            }

            phases[2] = Oscillators.WaveBlock(span, phases[2], WaveFrequency(), table);
            Mix(total, span, Enabled[2], 1.0);

            var (rate, isShort) = NoiseRate();
            noisePosition = Oscillators.NoiseBlock(span, noisePosition, rate, isShort ? NoiseShort : NoiseLong);
            Mix(total, span, Enabled[3], envelopes[3].Volume);

            for (var index = 0; index < total.Length; index++)
            {
                total[index] /= 60.0;
            }
        }

        private static void Mix(Span<double> total, ReadOnlySpan<double> source, bool enabled, double scale)
        {
            if (!enabled)
            {
                return;
            }

            for (var index = 0; index < total.Length; index++)
            {
                total[index] += source[index] * scale;
            }
        }

        private void Trigger(int channel, int envelopeRegister)
        {
            var register = Registers[envelopeRegister];
            if ((register & 0xF8) == 0)
            {
                Enabled[channel] = false;
                return;
            }

            Enabled[channel] = true;
            phases[channel] = 0.0;
            envelopes[channel].Trigger(register);
            if (channel == 3)
            {
                noisePosition = 0.0;
            }
        }
    }
}
