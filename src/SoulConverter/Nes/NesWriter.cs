using System.Globalization;
using System.Text.Json.Nodes;
using SoulConverter.Dmg;
using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Nes;

/// <summary>
/// Turns musical state into 2A03 register writes, each frame at its own sample position: tone 1
/// and 2 on the pulses, tone 3 on the triangle, noise on noise. Volumes are written as constant
/// volume, so an envelope the source ran arrives as the volume changes it produced; the triangle
/// has no volume and plays on or off. A voice the source never addressed is not written.
/// </summary>
internal static class NesWriter
{
    private const double CpuHz = VgmWriter.NesClockHz;
    private const double NoiseWarnCents = 200.0;

    public static ChipWriteResult Write(MusicalTimeline timeline, ConversionOptions options)
    {
        var writes = new List<RegisterWrite>();
        var stats = new Stats();
        var pulses = new[] { new Channel(), new Channel() };
        var triangle = new Channel();
        var noise = new Channel();
        var statusWritten = false;

        foreach (var frame in timeline.Frames)
        {
            var sample = frame.Sample;
            foreach (var on in frame.PowerWrites)
            {
                writes.Add(new RegisterWrite(sample, 0x15, on ? 0x0F : 0x00));
                statusWritten = true;
                if (!on)
                {
                    pulses[0] = new Channel();
                    pulses[1] = new Channel();
                    triangle = new Channel();
                    noise = new Channel();
                }
            }

            if (!statusWritten && (frame.Tone1.Addressed || frame.Tone2.Addressed || frame.Tone3.Addressed || frame.Noise.Addressed))
            {
                writes.Add(new RegisterWrite(sample, 0x15, 0x0F));
                statusWritten = true;
            }

            UpdatePulse(writes, sample, 0x00, pulses[0], frame.Tone1, stats);
            UpdatePulse(writes, sample, 0x04, pulses[1], frame.Tone2, stats);
            UpdateTriangle(writes, sample, triangle, frame.Tone3, stats);
            UpdateNoise(writes, sample, noise, frame.Noise, stats);
        }

        var writer = new VgmWriter(SoundChip.Nes2A03);
        foreach (var write in writes)
        {
            MarkLoop(writer, timeline.LoopSample, write.Sample);
            writer.WaitUntil(write.Sample);
            writer.Write(write.Register, write.Value);
        }

        var totalSamples = Math.Max(timeline.TotalSamples, writer.Sample);
        MarkLoop(writer, timeline.LoopSample, totalSamples);
        var vgm = writer.Finish(totalSamples, timeline.Log.Gd3, $"Converted from {timeline.Source.DisplayName()} by SoulConverter");

        var result = new ChipWriteResult
        {
            Vgm = vgm,
            Silent = !timeline.Frames.Any(frame => frame.AnyVoicePlaying),
            RegisterWrites = writes.Count,
        };
        result.Output["loop_sample"] = timeline.LoopSample;

        var log = timeline.Log;
        var noiseCents = new JsonObject();
        foreach (var (index, cents) in stats.NoiseErrorCents.OrderBy(entry => entry.Key))
        {
            noiseCents[index.ToString(CultureInfo.InvariantCulture)] = Math.Round(cents, 1);
        }

        var names = timeline.Source == SoundChip.GameBoyDmg
            ? ("DMG CH1", "DMG CH2", "DMG CH3", "DMG CH4")
            : ("tone 0", "tone 1", "tone 2", "noise");
        result.Sections["mapping"] = new JsonObject
        {
            ["pulse1"] = $"{names.Item1} -> NES pulse 1",
            ["pulse2"] = $"{names.Item2} -> NES pulse 2",
            ["wave"] = $"{names.Item3} -> NES triangle; its waveform and volume are approximated",
            ["noise"] = $"{names.Item4} -> NES noise",
            ["wave_ram_writes"] = log.Chip == SoundChip.GameBoyDmg ? log.Writes.Count(write => write.Register >= GameBoy.WaveRamBase) : 0,
            ["volume_updates"] = stats.VolumeUpdates,
            ["pulse_period_updates"] = stats.PulsePeriods,
            ["wave_period_updates"] = stats.WavePeriods,
            ["noise_period_updates"] = stats.NoisePeriods,
            ["noise_error_cents"] = noiseCents,
        };

        if (timeline.Frames.Any(frame => frame.Tone3.Playing))
        {
            result.Warnings.Add(timeline.Source == SoundChip.GameBoyDmg
                ? new ConversionWarning(WarningCodes.Timbre, "The Game Boy wave table has no NES equivalent; it plays with the fixed 2A03 triangle waveform, at one volume.")
                : new ConversionWarning(WarningCodes.Timbre, "The third tone plays on the 2A03 triangle, which has its own waveform and no volume control."));
        }

        if (timeline.Source == SoundChip.Sn76489 && timeline.Frames.Any(frame => frame.Noise is { Playing: true, Short: true }))
        {
            result.Warnings.Add(new ConversionWarning(WarningCodes.PeriodicNoise, "The PSG's periodic noise plays as the 2A03's short-mode noise, at a different pitch."));
        }

        if (timeline.Frames.Any(frame => frame.StereoSplit))
        {
            result.Warnings.Add(new ConversionWarning(WarningCodes.Stereo, "Stereo panning plays as mono on the 2A03."));
        }

        foreach (var (index, cents) in stats.NoiseErrorCents.OrderBy(entry => entry.Key))
        {
            if (Math.Abs(Math.Round(cents, 1)) > NoiseWarnCents)
            {
                result.Warnings.Add(new ConversionWarning(
                    WarningCodes.NoiseMapping,
                    string.Create(CultureInfo.InvariantCulture, $"NES noise period {index} is {Math.Round(cents, 1):+0;-0} cents off the source's noise rate; the 2A03 has no closer one.")));
            }
        }

        return result;
    }

    private static void MarkLoop(VgmWriter writer, int? loopSample, int next)
    {
        if (loopSample is { } loop && !writer.HasLoop && writer.Sample <= loop && loop <= next)
        {
            writer.WaitUntil(loop);
            writer.MarkLoop();
        }
    }

    private static void UpdatePulse(List<RegisterWrite> writes, int sample, int register, Channel channel, ToneVoice voice, Stats stats)
    {
        if (!voice.Addressed)
        {
            return;
        }

        var period = voice.FrequencyHz > 0 ? Period(voice.FrequencyHz, 16.0) : channel.Period ?? 0x7FF;
        var control = (voice.Duty << 6) | 0x30 | (voice.Playing ? Math.Clamp(voice.Volume, 0, 15) : 0);
        if (voice.Retriggered || (voice.Playing && !channel.Keyed))
        {
            writes.Add(new RegisterWrite(sample, register, control));

            // The sweep unit silences a pulse whose target period overflows, which with the adder
            // on means every note under about 109 Hz; negating keeps the target in range.
            writes.Add(new RegisterWrite(sample, register + 1, LegacyParity.Enabled ? 0x00 : 0x08));
            writes.Add(new RegisterWrite(sample, register + 2, period & 0xFF));
            writes.Add(new RegisterWrite(sample, register + 3, 0xF8 | ((period >> 8) & 0x07)));
            channel.Period = period;
            channel.Control = control;
            channel.Keyed = true;
            stats.PulsePeriods++;
            return;
        }

        if (channel.Control != control)
        {
            writes.Add(new RegisterWrite(sample, register, control));
            channel.Control = control;
            stats.VolumeUpdates++;
        }

        if (voice.FrequencyHz > 0 && channel.Period != period)
        {
            writes.Add(new RegisterWrite(sample, register + 2, period & 0xFF));
            writes.Add(new RegisterWrite(sample, register + 3, 0xF8 | ((period >> 8) & 0x07)));
            channel.Period = period;
            stats.PulsePeriods++;
        }
    }

    private static void UpdateTriangle(List<RegisterWrite> writes, int sample, Channel channel, ToneVoice voice, Stats stats)
    {
        if (!voice.Addressed)
        {
            return;
        }

        var period = voice.FrequencyHz > 0 ? Period(voice.FrequencyHz, 32.0) : channel.Period ?? 0x7FF;
        var control = voice.Playing ? 0xFF : 0x00;
        if (voice.Retriggered || (voice.Playing && !channel.Keyed))
        {
            writes.Add(new RegisterWrite(sample, 0x08, control));
            writes.Add(new RegisterWrite(sample, 0x0A, period & 0xFF));
            writes.Add(new RegisterWrite(sample, 0x0B, 0xF8 | ((period >> 8) & 0x07)));
            channel.Period = period;
            channel.Control = control;
            channel.Keyed = true;
            stats.WavePeriods++;
            return;
        }

        var periodChanged = voice.FrequencyHz > 0 && channel.Period != period;
        if (channel.Control != control)
        {
            writes.Add(new RegisterWrite(sample, 0x08, control));
            channel.Control = control;

            // Silencing the triangle through $4008 clears its linear counter and lets its length
            // counter run out, and neither comes back until $400B is written again.
            if (control != 0 && !periodChanged && !LegacyParity.Enabled)
            {
                writes.Add(new RegisterWrite(sample, 0x0B, 0xF8 | ((period >> 8) & 0x07)));
            }
        }

        if (periodChanged)
        {
            writes.Add(new RegisterWrite(sample, 0x0A, period & 0xFF));
            writes.Add(new RegisterWrite(sample, 0x0B, 0xF8 | ((period >> 8) & 0x07)));
            channel.Period = period;
            stats.WavePeriods++;
        }
    }

    private static void UpdateNoise(List<RegisterWrite> writes, int sample, Channel channel, NoiseVoice voice, Stats stats)
    {
        if (!voice.Addressed)
        {
            return;
        }

        var (index, cents) = NearestNoise(voice.ClockRateHz);
        var noise = index | (voice.Short ? 0x80 : 0);
        var control = 0x30 | (voice.Playing ? Math.Clamp(voice.Volume, 0, 15) : 0);
        if (voice.Retriggered || (voice.Playing && !channel.Keyed))
        {
            writes.Add(new RegisterWrite(sample, 0x0C, control));
            writes.Add(new RegisterWrite(sample, 0x0E, noise));
            writes.Add(new RegisterWrite(sample, 0x0F, 0xF8));
            channel.Period = noise;
            channel.Control = control;
            channel.Keyed = true;
            stats.NoisePeriods++;
            stats.NoiseErrorCents[index] = cents;
            return;
        }

        if (channel.Control != control)
        {
            writes.Add(new RegisterWrite(sample, 0x0C, control));
            channel.Control = control;
            stats.VolumeUpdates++;
        }

        if (voice.Started && channel.Period != noise)
        {
            writes.Add(new RegisterWrite(sample, 0x0E, noise));
            channel.Period = noise;
            stats.NoisePeriods++;
            stats.NoiseErrorCents[index] = cents;
        }
    }

    // The 2A03 timer value of a frequency: cpu / (divider * (period + 1)).
    private static int Period(double frequencyHz, double divider) =>
        (int)Math.Clamp(Math.Round((CpuHz / (divider * frequencyHz)) - 1), 0, 0x7FF);

    // The nearest of the sixteen noise periods on a log scale, and its error in cents.
    private static (int Index, double Cents) NearestNoise(double targetHz)
    {
        if (targetHz <= 0)
        {
            return (0, 0.0);
        }

        var best = 0;
        var bestError = double.PositiveInfinity;
        for (var index = 0; index < NesApu.NoisePeriodTable.Length; index++)
        {
            var error = Math.Abs(Math.Log2(CpuHz / NesApu.NoisePeriodTable[index] / targetHz));
            if (error < bestError)
            {
                best = index;
                bestError = error;
            }
        }

        return (best, 1200.0 * Math.Log2(CpuHz / NesApu.NoisePeriodTable[best] / targetHz));
    }

    private sealed class Channel
    {
        public int? Period;
        public int? Control;

        /// <summary>The channel's length counter has been loaded, so a volume is enough to make it sound.</summary>
        public bool Keyed;
    }

    private sealed class Stats
    {
        public int VolumeUpdates;
        public int PulsePeriods;
        public int WavePeriods;
        public int NoisePeriods;

        public Dictionary<int, double> NoiseErrorCents { get; } = [];
    }
}
