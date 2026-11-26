using System.Globalization;
using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Sn;

/// <summary>
/// Turns musical state into SN76489 writes, one 60 Hz (or tick) frame at a time: each tone voice
/// becomes one PSG tone, its frequency a period and its volume an attenuation; noise becomes noise.
/// Only voices that sound at some point are written, so a converted sound effect owns only those
/// channels and not ones its source merely sets up or silences. What the PSG cannot play is
/// approximated or dropped, each loss reported once.
/// </summary>
internal static class SnWriter
{
    private const double PsgClock = VgmWriter.SnClockHz;
    private const int MaxPeriod = 0x3FF;
    private const int Silent = 15;
    private const int TriangleVolume = 12;

    // The PSG noise generator steps at clock/512, /1024 or /2048 (noise rates 0-2).
    private static readonly double[] NoiseRates = [PsgClock / 512, PsgClock / 1024, PsgClock / 2048];

    public static ChipWriteResult Write(MusicalTimeline source, ConversionOptions options)
    {
        var timeline = source.ToGrid();
        var frames = timeline.Frames;
        var tones = frames.Select(frame => new[] { Tone(frame.Tone1), Tone(frame.Tone2), Tone(frame.Tone3) }).ToList();

        // A channel the source only set up or silenced is not claimed.
        var sounding = Enumerable.Range(0, 3).Where(channel => tones.Any(frame => frame[channel].Audible)).ToList();
        var noiseSounds = frames.Any(frame => NoiseAudible(frame.Noise));
        var transposed = false;
        var lastPeriod = new int?[3];
        var lastAttenuation = new int?[4];
        int? noiseControl = null;

        var writer = new VgmWriter(SoundChip.Sn76489);
        for (var index = 0; index < frames.Count; index++)
        {
            writer.WaitUntil(frames[index].Sample);
            if (timeline.LoopFrame == index)
            {
                writer.MarkLoop();
            }

            foreach (var channel in sounding)
            {
                var tone = tones[index][channel];
                if (tone.Audible)
                {
                    var period = Period(tone.Frequency, options.TransposeOutOfRange, ref transposed);
                    if (lastPeriod[channel] != period)
                    {
                        writer.Write(0, 0x80 | (channel << 5) | (period & 0x0F));
                        writer.Write(0, (period >> 4) & 0x3F);
                        lastPeriod[channel] = period;
                    }
                }

                WriteAttenuation(writer, lastAttenuation, channel, tone.Audible ? tone.Volume : 0);
            }

            if (noiseSounds)
            {
                // A silent noise keeps its control, so it is not rewritten for nothing. A restart
                // rewrites it, which restarts the PSG generator too.
                var noise = frames[index].Noise;
                var audible = NoiseAudible(noise);
                var control = audible ? NoiseControl(noise) : noiseControl ?? 0;
                if (control != noiseControl || (noise.Retriggered && audible))
                {
                    writer.Write(0, 0xE0 | control);
                    noiseControl = control;
                }

                WriteAttenuation(writer, lastAttenuation, 3, audible ? noise.Volume : 0);
            }
        }

        var vgm = writer.Finish(timeline.TotalSamples, timeline.Log.Gd3, $"Converted from {timeline.Source.DisplayName()} by SoulConverter");
        var result = new ChipWriteResult
        {
            Vgm = vgm,
            Silent = sounding.Count == 0 && !noiseSounds,
            RegisterWrites = writer.RegisterWrites,
        };
        result.Output["ticks"] = frames.Count;
        result.Output["loop_tick"] = timeline.LoopFrame;
        AddLosses(result, timeline, transposed);
        return result;
    }

    /// <summary>A linear volume 0-15 as a PSG attenuation in 2 dB steps; 0 is silent (15).</summary>
    public static int Attenuation(int volume)
    {
        if (volume <= 0)
        {
            return Silent;
        }

        var decibels = 20 * Math.Log10(15.0 / Math.Min(volume, 15));
        return Math.Clamp((int)Math.Round(decibels / 2), 0, Silent - 1);
    }

    private static void WriteAttenuation(VgmWriter writer, int?[] last, int channel, int volume)
    {
        var attenuation = Attenuation(volume);
        if (last[channel] != attenuation)
        {
            writer.Write(0, 0x90 | (channel << 5) | attenuation);
            last[channel] = attenuation;
        }
    }

    // The 2A03 triangle has no volume; it plays at 12, where it balances the pulses.
    private static (double Frequency, int Volume, bool Audible) Tone(ToneVoice voice)
    {
        var volume = voice.Timbre == Timbre.Triangle ? TriangleVolume : voice.Volume;
        var audible = voice.Playing && volume > 0 && voice.FrequencyHz > 0;
        return (voice.FrequencyHz, volume, audible);
    }

    private static bool NoiseAudible(NoiseVoice noise) => noise.Playing && noise.Volume > 0 && noise.ClockRateHz > 0;

    // The PSG period of a frequency: clock / (32 * period). Notes too low for its 10 bits rise by
    // octaves, or stay on the lowest note when transposing is off.
    private static int Period(double frequency, bool allowTranspose, ref bool transposed)
    {
        var period = Math.Round(PsgClock / (32 * frequency));
        if (!allowTranspose)
        {
            return (int)Math.Clamp(period, 1, MaxPeriod);
        }

        while (period > MaxPeriod)
        {
            period = Math.Floor((period + 1) / 2);
            transposed = true;
        }

        return (int)Math.Max(1, period);
    }

    // The nearest of the three noise rates on a log scale, with the periodic or white feedback.
    private static int NoiseControl(NoiseVoice noise)
    {
        var rate = 0;
        for (var candidate = 1; candidate < NoiseRates.Length; candidate++)
        {
            if (Math.Abs(Math.Log(noise.ClockRateHz / NoiseRates[candidate])) < Math.Abs(Math.Log(noise.ClockRateHz / NoiseRates[rate])))
            {
                rate = candidate;
            }
        }

        return (noise.Short ? 0x00 : 0x04) | rate;
    }

    private static void AddLosses(ChipWriteResult result, MusicalTimeline timeline, bool transposed)
    {
        var frames = timeline.Frames;
        var nes = timeline.Source == SoundChip.Nes2A03;
        if (frames.Any(frame => OffDuty(frame.Tone1) || OffDuty(frame.Tone2)))
        {
            result.Warnings.Add(new ConversionWarning(
                WarningCodes.Duty,
                $"{(nes ? "pulse" : "square")} duty cycles other than 50% play as square waves on the SN76489"));
        }

        if (frames.Any(frame => Tone(frame.Tone3).Audible))
        {
            result.Warnings.Add(new ConversionWarning(
                WarningCodes.Timbre,
                nes ? "the triangle wave plays as a square wave on the SN76489" : "the wave channel plays as a square tone on the SN76489"));
        }

        if (frames.Any(frame => NoiseAudible(frame.Noise)))
        {
            result.Warnings.Add(new ConversionWarning(WarningCodes.NoiseMapping, "noise pitch is limited to the SN76489's three noise rates"));
            if (frames.Any(frame => NoiseAudible(frame.Noise) && frame.Noise.Short))
            {
                result.Warnings.Add(new ConversionWarning(
                    WarningCodes.PeriodicNoise,
                    $"{(nes ? "short-mode" : "7-bit")} noise plays as periodic noise on the SN76489"));
            }
        }

        if (frames.Any(frame => frame.Dmc is { Started: true, Sample.Length: > 1 } || frame.Dmc.DirectLevel != 0))
        {
            result.Warnings.Add(new ConversionWarning(WarningCodes.DpcmDropped, "DPCM samples are dropped: the SN76489 has no sample channel"));
        }

        if (frames.Any(frame => frame.StereoSplit))
        {
            result.Warnings.Add(new ConversionWarning(WarningCodes.Stereo, "stereo panning plays as mono on the SN76489"));
        }

        if (transposed)
        {
            result.Warnings.Add(new ConversionWarning(
                WarningCodes.Transposed,
                string.Create(CultureInfo.InvariantCulture, $"notes below {PsgClock / (32 * MaxPeriod):0} Hz are raised by octaves on the SN76489")));
        }

        static bool OffDuty(ToneVoice voice) => Tone(voice).Audible && voice.Timbre == Timbre.Pulse && voice.Duty != 2;
    }
}
