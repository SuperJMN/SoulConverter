using System.Text.Json.Nodes;
using SoulConverter.Model;

namespace SoulConverter.Analysis;

/// <summary>
/// Compares the source's render with the conversion's. Every number is diagnostic: a poor score
/// never fails a conversion, it is reported so a person can decide whether the tune survived.
/// </summary>
internal static class Metrics
{
    private const double EnvelopeWindowHz = 60.0;
    private const double OnsetToleranceSeconds = 0.035;
    private const int OnsetHop = 512;
    private const int SpectrogramBins = 512;

    /// <summary>The metric set of one conversion, keyed by what the report calls each side.</summary>
    public static JsonObject Compare(RenderedAudio source, RenderedAudio target, string sourceKey, string targetKey)
    {
        var sourceRms = Rms(source.Samples);
        var targetRms = Rms(target.Samples);
        var channels = new JsonObject();
        foreach (var name in TimelineSynth.VoiceNames)
        {
            channels[name] = PitchStats(
                source.Frequencies.GetValueOrDefault(name) ?? [],
                target.Frequencies.GetValueOrDefault(name) ?? [],
                sourceKey,
                targetKey);
        }

        return new JsonObject
        {
            ["envelope_correlation"] = Correlation(Envelope(source.Samples), Envelope(target.Samples)),
            ["spectral_correlation"] = SpectralCorrelation(source.Samples, target.Samples),
            [$"{sourceKey}_rms"] = sourceRms,
            [$"{targetKey}_rms"] = targetRms,
            ["rms_ratio"] = sourceRms > 0 ? targetRms / sourceRms : 0.0,
            ["onsets"] = OnsetMatch(source.Samples, target.Samples),
            ["channels"] = channels,
        };
    }

    /// <summary>Magnitudes per frequency bin (rows) and time frame (columns) of <paramref name="samples"/>.</summary>
    public static double[][] Spectrogram(ReadOnlySpan<double> samples)
    {
        var half = SpectrogramBins / 2;
        if (samples.Length < SpectrogramBins * 2)
        {
            return [.. Enumerable.Range(0, half).Select(_ => new double[1])];
        }

        var frames = samples.Length / SpectrogramBins;
        var window = Dsp.Hanning(SpectrogramBins);
        var rows = Enumerable.Range(0, half).Select(_ => new double[frames]).ToArray();
        var block = new double[SpectrogramBins];
        for (var frame = 0; frame < frames; frame++)
        {
            var slice = samples.Slice(frame * SpectrogramBins, SpectrogramBins);
            for (var index = 0; index < SpectrogramBins; index++)
            {
                block[index] = slice[index] * window[index];
            }

            var magnitudes = Dsp.RealFftMagnitudes(block);
            for (var bin = 0; bin < half; bin++)
            {
                rows[bin][frame] = magnitudes[bin];
            }
        }

        return rows;
    }

    private static double Rms(double[] samples) =>
        samples.Length == 0 ? 0.0 : Math.Sqrt(samples.Sum(value => value * value) / samples.Length);

    // The RMS envelope of `samples` on a fixed window grid.
    private static double[] Envelope(double[] samples)
    {
        var window = Math.Max(1, (int)Math.Round(Oscillators.SampleRate / EnvelopeWindowHz));
        var blocks = samples.Length / window;
        var envelope = new double[blocks];
        for (var block = 0; block < blocks; block++)
        {
            var sum = 0.0;
            for (var index = block * window; index < (block + 1) * window; index++)
            {
                sum += samples[index] * samples[index];
            }

            envelope[block] = Math.Sqrt(sum / window);
        }

        return envelope;
    }

    private static double Correlation(double[] left, double[] right)
    {
        var size = Math.Min(left.Length, right.Length);
        if (size < 2)
        {
            return 0.0;
        }

        var leftMean = Dsp.Mean(left.AsSpan(0, size));
        var rightMean = Dsp.Mean(right.AsSpan(0, size));
        double leftPower = 0.0, rightPower = 0.0, product = 0.0;
        for (var index = 0; index < size; index++)
        {
            var a = left[index] - leftMean;
            var b = right[index] - rightMean;
            leftPower += a * a;
            rightPower += b * b;
            product += a * b;
        }

        var denominator = Math.Sqrt(leftPower * rightPower);
        return denominator == 0.0 ? 0.0 : product / denominator;
    }

    private static JsonObject PitchStats(double[] source, double[] target, string sourceKey, string targetKey)
    {
        var size = Math.Min(source.Length, target.Length);
        int sourceActive = 0, targetActive = 0, agree = 0;
        var cents = new List<double>();
        for (var index = 0; index < size; index++)
        {
            var sourceOn = source[index] > 0;
            var targetOn = target[index] > 0;
            sourceActive += sourceOn ? 1 : 0;
            targetActive += targetOn ? 1 : 0;
            agree += sourceOn == targetOn ? 1 : 0;
            if (sourceOn && targetOn)
            {
                cents.Add(1200.0 * Math.Log2(target[index] / source[index]));
            }
        }

        // Octave transposition is an intended, reported outcome, so pitch-class error is measured
        // separately from raw error.
        var absolute = cents.Select(Math.Abs).ToList();
        var residual = cents.Select(value => Math.Abs(value - (1200.0 * Math.Round(value / 1200.0)))).ToList();
        return new JsonObject
        {
            ["compared_ticks"] = cents.Count,
            [$"{sourceKey}_active_ticks"] = sourceActive,
            [$"{targetKey}_active_ticks"] = targetActive,
            ["activity_agreement"] = size == 0 ? 1.0 : agree / (double)size,
            ["mean_abs_cents"] = cents.Count == 0 ? 0.0 : absolute.Average(),
            ["max_abs_cents"] = cents.Count == 0 ? 0.0 : absolute.Max(),
            ["p95_abs_cents"] = cents.Count == 0 ? 0.0 : Percentile(absolute, 95),
            ["mean_abs_cents_mod_octave"] = cents.Count == 0 ? 0.0 : residual.Average(),
            ["max_abs_cents_mod_octave"] = cents.Count == 0 ? 0.0 : residual.Max(),
            ["octaves_up_ticks"] = cents.Count(value => Math.Round(value / 1200.0) > 0),
        };
    }

    private static double Percentile(List<double> values, double percent)
    {
        var sorted = values.Order().ToList();
        var rank = percent / 100.0 * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + ((rank - lower) * (sorted[upper] - sorted[lower]));
    }

    // Times where the spectrum jumps, i.e. where a listener hears a hit. Spectral flux instead of
    // an amplitude jump, because a drum landing on a sustained note barely changes the level but
    // changes the spectrum a lot.
    private static List<double> Onsets(double[] samples)
    {
        if (samples.Length < OnsetHop * 4)
        {
            return [];
        }

        var frames = samples.Length / OnsetHop;
        var window = Dsp.Hanning(OnsetHop);
        var block = new double[OnsetHop];
        double[]? previous = null;
        var strength = new double[frames - 1];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var index = 0; index < OnsetHop; index++)
            {
                block[index] = samples[(frame * OnsetHop) + index] * window[index];
            }

            var spectrum = Dsp.RealFftMagnitudes(block);
            if (previous is not null)
            {
                var flux = 0.0;
                for (var bin = 0; bin < spectrum.Length; bin++)
                {
                    flux += Math.Max(0.0, spectrum[bin] - previous[bin]);
                }

                strength[frame - 1] = flux;
            }

            previous = spectrum;
        }

        if (strength.Length == 0 || strength.Max() <= 0)
        {
            return [];
        }

        var mean = Dsp.Mean(strength);
        var deviation = Math.Sqrt(strength.Sum(value => (value - mean) * (value - mean)) / strength.Length);
        var threshold = mean + deviation;
        var peaks = new List<double>();
        for (var index = 1; index < strength.Length - 1; index++)
        {
            if (strength[index] > threshold && strength[index] >= strength[index - 1] && strength[index] > strength[index + 1])
            {
                peaks.Add(index * (double)OnsetHop / Oscillators.SampleRate);
            }
        }

        return peaks;
    }

    // How many of the source's audible hits survive, and how punctually.
    private static JsonObject OnsetMatch(double[] source, double[] target)
    {
        var reference = Onsets(source);
        var produced = Onsets(target);
        var matched = 0;
        if (reference.Count != 0)
        {
            var remaining = new List<double>(produced);
            foreach (var time in reference)
            {
                var index = remaining.FindIndex(candidate => Math.Abs(candidate - time) <= OnsetToleranceSeconds);
                if (index >= 0)
                {
                    matched++;
                    remaining.RemoveAt(index);
                }
            }
        }

        return new JsonObject
        {
            ["reference"] = reference.Count,
            ["produced"] = produced.Count,
            ["matched"] = matched,
            ["recall"] = reference.Count == 0 ? 0.0 : Math.Round(matched / (double)reference.Count, 3),
        };
    }

    // The correlation of the two long-term average spectra, in dB.
    private static double SpectralCorrelation(double[] source, double[] target)
    {
        var size = Math.Min(source.Length, target.Length);
        if (size < 1024)
        {
            return 0.0;
        }

        return Correlation(AverageDb(source.AsSpan(0, size)), AverageDb(target.AsSpan(0, size)));

        static double[] AverageDb(ReadOnlySpan<double> samples) =>
            [.. Spectrogram(samples).Select(row => 20.0 * Math.Log10(Dsp.Mean(row) + 1e-9))];
    }
}
