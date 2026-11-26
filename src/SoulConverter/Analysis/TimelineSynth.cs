using SoulConverter.Dmg;
using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Analysis;

/// <summary>
/// Renders sampled musical state for comparison. Not an emulator: it drives phase-continuous
/// oscillators from the per-tick state, through the 2A03's non-linear mixer for a 2A03 log
/// (including its DPCM samples, so the reference shows what a target without them is missing) and
/// through a plain sum for the SN76489.
/// </summary>
internal static class TimelineSynth
{
    public static readonly string[] VoiceNames = ["pulse1", "pulse2", "triangle", "noise"];

    private static readonly double[] NoiseLong = Oscillators.LfsrSequence(bits: 15, tap: 1);
    private static readonly double[] NoiseShort = Oscillators.LfsrSequence(bits: 15, tap: 6);
    private static readonly double[] SquareTable = [.. Enumerable.Repeat(1.0, 16), .. Enumerable.Repeat(0.0, 16)];

    public static RenderedAudio Render(MusicalTimeline timeline)
    {
        var frames = timeline.Frames;
        var total = Math.Max(timeline.TotalSamples, frames.Count != 0 ? frames[^1].Sample : 0);
        var tones = new[] { new double[total], new double[total], new double[total] };
        var noise = new double[total];
        var phases = new double[4];
        var block = new double[1];
        var nes = timeline.Source == SoundChip.Nes2A03;

        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            var start = frame.Sample;
            var end = index + 1 < frames.Count ? frames[index + 1].Sample : total;
            var count = Math.Max(0, Math.Min(end, total) - start);
            if (count == 0)
            {
                continue;
            }

            if (block.Length < count)
            {
                block = new double[count];
            }

            var span = block.AsSpan(0, count);
            for (var channel = 0; channel < 3; channel++)
            {
                var tone = frame.Tone(channel);
                double scale;
                if (tone.Timbre == Timbre.Triangle)
                {
                    phases[channel] = Oscillators.WaveBlock(span, phases[channel], tone.FrequencyHz, Oscillators.TriangleSequence);
                    scale = 1.0;
                }
                else if (tone.Timbre == Timbre.Pulse)
                {
                    phases[channel] = Oscillators.SquareBlock(span, phases[channel], tone.FrequencyHz, tone.Duty);
                    scale = tone.Volume;
                }
                else
                {
                    phases[channel] = Oscillators.WaveBlock(span, phases[channel], tone.FrequencyHz, SquareTable);
                    scale = tone.Volume;
                }

                if (tone.Playing)
                {
                    var target = tones[channel].AsSpan(start, count);
                    for (var offset = 0; offset < count; offset++)
                    {
                        target[offset] = span[offset] * scale;
                    }
                }
            }

            phases[3] = Oscillators.NoiseBlock(span, phases[3], frame.Noise.ClockRateHz, frame.Noise.Short ? NoiseShort : NoiseLong);
            if (frame.Noise.Playing)
            {
                var target = noise.AsSpan(start, count);
                for (var offset = 0; offset < count; offset++)
                {
                    target[offset] = span[offset] * frame.Noise.Volume;
                }
            }
        }

        var samples = new double[total];
        if (nes)
        {
            var dmc = RenderDpcm(timeline, total);
            for (var index = 0; index < total; index++)
            {
                // The 2A03 non-linear mixer, as documented on nesdev.
                var pulseSum = tones[0][index] + tones[1][index];
                var pulseOut = pulseSum > 0 ? 95.88 / ((8128.0 / Math.Max(pulseSum, 1e-9)) + 100.0) : 0.0;
                var tnd = (tones[2][index] / 8227.0) + (noise[index] / 12241.0) + (dmc[index] / 22638.0);
                var tndOut = tnd > 0 ? 159.79 / ((1.0 / Math.Max(tnd, 1e-12)) + 100.0) : 0.0;
                samples[index] = pulseOut + tndOut;
            }
        }
        else
        {
            for (var index = 0; index < total; index++)
            {
                samples[index] = (tones[0][index] + tones[1][index] + tones[2][index] + noise[index]) / 60.0;
            }
        }

        var frequencies = new Dictionary<string, double[]>
        {
            ["pulse1"] = [.. frames.Select(frame => frame.Tone1.Playing ? frame.Tone1.FrequencyHz : 0.0)],
            ["pulse2"] = [.. frames.Select(frame => frame.Tone2.Playing ? frame.Tone2.FrequencyHz : 0.0)],
            ["triangle"] = [.. frames.Select(frame => frame.Tone3.Playing ? frame.Tone3.FrequencyHz : 0.0)],
            ["noise"] = [.. frames.Select(frame => frame.Noise.Playing ? frame.Noise.ClockRateHz : 0.0)],
        };
        return new RenderedAudio(samples, frequencies);
    }

    // Replays each triggered sample at its own rate, resampled to 44100 Hz. The DMC was
    // monophonic, so a sample is cut where the next one starts instead of mixed with it.
    private static double[] RenderDpcm(MusicalTimeline timeline, int total)
    {
        var output = new double[total];
        var rom = timeline.Log.DpcmRom;
        if (rom.Count == 0)
        {
            return output;
        }

        var starts = timeline.Frames
            .Where(frame => frame.Dmc is { Started: true, Sample: not null })
            .Select(frame => (frame.Sample, Ref: frame.Dmc.Sample!))
            .ToList();
        var cache = new Dictionary<SampleRef, double[]>();
        for (var position = 0; position < starts.Count; position++)
        {
            var (start, sample) = starts[position];
            if (!cache.TryGetValue(sample, out var block))
            {
                var waveform = Dpcm.Decode(rom, sample).Waveform;
                var length = Math.Max(1, (int)Math.Round(waveform.Length * (double)Oscillators.SampleRate / sample.RateHz));
                var positions = Dsp.Linspace(0, waveform.Length - 1, length);
                block = new double[length];
                for (var index = 0; index < length; index++)
                {
                    // The DMC output is a 7-bit level; the mixer expects that scale.
                    block[index] = (Dsp.Interpolate(waveform, positions[index]) * 64.0) + 64.0;
                }

                cache[sample] = block;
            }

            var limit = position + 1 < starts.Count ? starts[position + 1].Sample : total;
            var count = Math.Max(0, Math.Min(Math.Min(start + block.Length, limit), total) - start);
            block.AsSpan(0, count).CopyTo(output.AsSpan(start, count));
        }

        return output;
    }
}
