using System.Globalization;
using SoulConverter.Analysis;
using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Dmg;

/// <summary>A DPCM sample decoded to a normalised waveform.</summary>
internal sealed record DecodedSample(SampleRef Ref, float[] Waveform, double RateHz)
{
    public double DurationSeconds => Waveform.Length / RateHz;
}

/// <summary>
/// Physical measurements a rule can be written against. Everything is a duration in seconds, a
/// ratio in 0..1 or a frequency in Hz, so thresholds read as statements about sound.
/// </summary>
internal sealed record SampleFeatures(
    double Duration,
    double EffectiveDuration,
    double PeakPosition,
    double DecayRatio,
    double Low,
    double Mid,
    double High,
    double CentroidHz,
    double Flatness,
    double PitchHz,
    double PitchConfidence,
    double PitchSlope,
    double MonotonicRatio,
    double Amplitude)
{
    /// <summary>
    /// True when the sample dies away like a struck instrument: the single most discriminating
    /// property in practice. Delta modulation gives even a snare a strongly periodic look, so
    /// pitch confidence alone would call a drum a held note; a struck sound collapsing to
    /// near-silence inside a tenth of a second will not.
    /// </summary>
    public bool PercussiveDecay => EffectiveDuration <= 0.12 && DecayRatio < 0.20;
}

/// <summary>A decoded sample plus the verdict on what to play instead of it.</summary>
internal sealed record SampleAnalysis(
    SampleRef Ref,
    DecodedSample Decoded,
    SampleFeatures Features,
    DpcmRole Role,
    double Confidence,
    IReadOnlyList<string> Reasons,
    bool Overridden = false);

/// <summary>
/// Decodes DPCM samples and works out what they were meant to sound like. The Game Boy cannot
/// replay a sample, so the only way to keep the fifth voice is to recognise it: the delta-modulated
/// bytes are decoded back to a waveform, physical properties of that waveform are measured, and
/// named rules give it a percussive or tonal role. The classifier is rule-based on purpose: every
/// verdict is explained in the report and can be overridden by hand.
/// </summary>
internal static class Dpcm
{
    private const int DeltaStep = 2;
    private const int LevelMax = 127;
    private const int DefaultInitialLevel = 64;

    private const double BandLowHz = 250.0;
    private const double BandMidHz = 2000.0;
    private const double EnvelopeWindowSeconds = 0.003;
    private const double DecayThreshold = 0.25;
    private const double PitchMinHz = 40.0;
    private const double PitchMaxHz = 1500.0;

    // One delta step out of full scale is the quantisation floor of the format, so nothing quieter
    // than a hair above it carries musical intent: it is encoded silence or DAC dither.
    private const double SilenceFloor = 1.5 * DeltaStep / DefaultInitialLevel;

    // Measured drums reverse direction constantly and land near 0.55; only DC motion runs one way.
    private const double OneWayRatio = 0.95;

    // The DMC rate is the only knob that transposes a sample, so a driver that plays one address
    // at several rates is playing a scale on it. Drums get at most a normal and a low variant.
    private const int MelodicRateCount = 3;
    private const double MelodicPitchConfidence = 0.5;

    // Two semitones is about where a listener stops hearing "the same drum".
    private const double TunedDrumSemitones = 2.0;
    private const double TunedDrumPitchConfidence = 0.5;

    private static readonly DpcmRole[] PercussiveRoles = [DpcmRole.Kick, DpcmRole.Snare, DpcmRole.Hat, DpcmRole.Tom];

    // A negative weight is a veto: it argues against the role, and it still shows up in the report
    // so a wrong verdict can be traced to the rule that caused it.
    private static readonly (DpcmRole Role, string Name, double Weight, Func<SampleFeatures, bool> Test)[] Rules =
    [
        // A DAC-levelling blip or a driver start-up glitch attacks late and carries nothing the
        // DMG could reproduce: its energy sits below the range its period grid can reach.
        (DpcmRole.Artifact, "peak at the tail", 2.5, f => f.PeakPosition > 0.6),
        (DpcmRole.Artifact, "below the DMG's range", 2.0, f => f.Low > 0.9 && f.CentroidHz < 200.0),
        (DpcmRole.Artifact, "not a hit", -4.0, f => f.PercussiveDecay && f.PeakPosition < 0.4),

        // A kick is low-heavy, pitched under the bass range and gone quickly.
        (DpcmRole.Kick, "low-band dominant", 2.0, f => f.Low >= 0.30),
        (DpcmRole.Kick, "bass pitch", 1.5, f => f.PitchHz >= 30.0 && f.PitchHz <= 260.0 && f.PitchConfidence > 0.2),
        (DpcmRole.Kick, "short body", 1.0, f => f.EffectiveDuration <= 0.12),
        (DpcmRole.Kick, "downward sweep", 1.0, f => f.PitchSlope < -0.01),
        (DpcmRole.Kick, "not bright", 1.0, f => f.CentroidHz < 2500.0),
        (DpcmRole.Kick, "no audible pitch", -2.5, f => f.CentroidHz < 200.0 || f.PitchHz > 400.0),
        (DpcmRole.Kick, "attacks late", -3.0, f => f.PeakPosition > 0.5),

        // A snare is a mid-heavy broadband crack. Its centroid lands lower than a real snare's
        // because delta modulation rolls off the top end hard.
        (DpcmRole.Snare, "mid-band dominant", 2.5, f => f.Mid >= 0.45),
        (DpcmRole.Snare, "crack centroid", 1.5, f => f.CentroidHz >= 700.0 && f.CentroidHz <= 8000.0),
        (DpcmRole.Snare, "crack length", 1.5, f => f.EffectiveDuration >= 0.015 && f.EffectiveDuration <= 0.12),
        (DpcmRole.Snare, "little bass", 1.5, f => f.Low < 0.35),
        (DpcmRole.Snare, "dies away", 1.0, f => f.PercussiveDecay),
        (DpcmRole.Snare, "attacks late", -3.0, f => f.PeakPosition > 0.5),

        // A hat is almost all treble and over before anything else starts.
        (DpcmRole.Hat, "treble dominant", 2.0, f => f.High >= 0.45),
        (DpcmRole.Hat, "very short", 2.0, f => f.EffectiveDuration <= 0.045),
        (DpcmRole.Hat, "very bright", 1.5, f => f.CentroidHz >= 6000.0),
        (DpcmRole.Hat, "no bass", 1.0, f => f.Low < 0.20),
        (DpcmRole.Hat, "attacks late", -3.0, f => f.PeakPosition > 0.5),

        // Without this veto a low sample whose pitch fell outside the detector's range scores on
        // "very short" alone and wins the role by default. A cymbal made of bass is not a thing.
        (DpcmRole.Hat, "all bass", -4.0, f => f.Low >= 0.6),

        // A tom is a pitched drum: clearer pitch than a snare, higher than a kick.
        (DpcmRole.Tom, "drum pitch", 2.0, f => f.PitchHz >= 120.0 && f.PitchHz <= 500.0 && f.PitchConfidence > 0.35),
        (DpcmRole.Tom, "mid body", 1.5, f => f.Mid >= 0.30 && f.Low < 0.45),
        (DpcmRole.Tom, "decays", 1.0, f => f.EffectiveDuration >= 0.04 && f.EffectiveDuration <= 0.30),
        (DpcmRole.Tom, "downward sweep", 1.5, f => f.PitchSlope < -0.05),
        (DpcmRole.Tom, "attacks late", -3.0, f => f.PeakPosition > 0.5),

        // A tonal sample is a held note. It must actually be held: a struck sound that collapses
        // inside a tenth of a second is a drum however periodic it looks, which is the trap delta
        // modulation sets for the pitch detector.
        (DpcmRole.Tonal, "strongly periodic", 2.5, f => f.PitchConfidence >= 0.55),
        (DpcmRole.Tonal, "sustained", 2.0, f => f.EffectiveDuration >= 0.15),
        (DpcmRole.Tonal, "stable pitch", 1.5, f => Math.Abs(f.PitchSlope) < 0.08),
        (DpcmRole.Tonal, "musical pitch", 1.0, f => f.PitchHz >= 55.0 && f.PitchHz <= 1200.0),
        (DpcmRole.Tonal, "tonal spectrum", 1.0, f => f.Flatness < 0.15),
        (DpcmRole.Tonal, "struck, not held", -5.0, f => f.PercussiveDecay),
        (DpcmRole.Tonal, "no audible pitch", -3.0, f => f.CentroidHz < 200.0),

        // Anything long and unpitched is a sound effect, not an instrument.
        (DpcmRole.NoiseSfx, "long", 1.5, f => f.Duration >= 0.25),
        (DpcmRole.NoiseSfx, "unpitched", 1.5, f => f.PitchConfidence < 0.30),
        (DpcmRole.NoiseSfx, "flat spectrum", 1.0, f => f.Flatness >= 0.15),
        (DpcmRole.NoiseSfx, "sustained energy", 1.0, f => f.DecayRatio > 0.35),
        (DpcmRole.NoiseSfx, "struck, not sustained", -3.0, f => f.PercussiveDecay),
    ];

    /// <summary>
    /// Runs the 2A03 delta decoder over <paramref name="sample"/> and normalises the result. Each
    /// bit nudges a 7-bit level by ±2 with saturation, least significant bit first. The output is
    /// centred on the initial level so the DC offset the driver left in the DAC does not colour
    /// the spectrum.
    /// </summary>
    public static DecodedSample Decode(DpcmRom rom, SampleRef sample)
    {
        var level = DefaultInitialLevel;
        var waveform = new float[sample.Length * 8];
        var cursor = 0;
        for (var index = 0; index < sample.Length; index++)
        {
            var value = rom.Read(sample.Address + index);
            for (var bit = 0; bit < 8; bit++)
            {
                if ((value & (1 << bit)) != 0)
                {
                    if (level <= LevelMax - DeltaStep)
                    {
                        level += DeltaStep;
                    }
                }
                else if (level >= DeltaStep)
                {
                    level -= DeltaStep;
                }

                waveform[cursor++] = (level - DefaultInitialLevel) / (float)DefaultInitialLevel;
            }
        }

        return new DecodedSample(sample, waveform, sample.RateHz);
    }

    /// <summary>Measures the properties the classification rules are written against.</summary>
    public static SampleFeatures ExtractFeatures(DecodedSample decoded)
    {
        var waveform = decoded.Waveform;
        var rate = decoded.RateHz;
        var duration = decoded.DurationSeconds;
        var amplitude = waveform.Length == 0 ? 0.0 : waveform.Max(Math.Abs);
        if (waveform.Length < 8)
        {
            return new SampleFeatures(duration, duration, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, amplitude);
        }

        var envelope = Envelope(waveform, rate);
        var peakIndex = ArgMax(envelope);
        var peak = envelope[peakIndex] + 1e-9;
        var threshold = (float)(peak * DecayThreshold);
        var effective = duration;
        for (var index = peakIndex; index < envelope.Length; index++)
        {
            if (envelope[index] < threshold)
            {
                effective = index / rate;
                break;
            }
        }

        var decayRatio = (double)(envelope[^1] / (float)peak);
        var (low, mid, high, centroid, flatness) = BandEnergies(waveform, rate);

        var body = waveform.Length - peakIndex >= 64 ? waveform.AsSpan(peakIndex) : waveform.AsSpan();
        var (pitch, confidence) = AutocorrelationPitch(body, rate);

        // Two halves, the first taking the extra sample of an odd length.
        var firstHalf = (body.Length + 1) / 2;
        var (first, _) = AutocorrelationPitch(body[..firstHalf], rate);
        var (second, _) = AutocorrelationPitch(body[firstHalf..], rate);
        var slope = first > 0 ? (second - first) / first : 0.0;

        var trend = Trend(waveform, rate);
        int moving = 0, rising = 0;
        for (var index = 1; index < trend.Length; index++)
        {
            var delta = trend[index] - trend[index - 1];
            if (delta != 0)
            {
                moving++;
                rising += delta > 0 ? 1 : 0;
            }
        }

        var monotonic = Math.Max(rising, moving - rising) / (double)Math.Max(moving, 1);

        return new SampleFeatures(
            duration,
            effective,
            peakIndex / (double)Math.Max(waveform.Length - 1, 1),
            decayRatio,
            low,
            mid,
            high,
            centroid,
            flatness,
            pitch,
            confidence,
            slope,
            monotonic,
            amplitude);
    }

    /// <summary>
    /// Scores every role and returns the winner with the evidence behind it. Scoring instead of a
    /// decision tree because real samples sit between categories: a scored verdict degrades into a
    /// low confidence the report can flag, where a tree would silently pick a branch.
    /// </summary>
    public static (DpcmRole Role, double Confidence, IReadOnlyList<string> Reasons) Classify(SampleFeatures features)
    {
        // Silence is settled before the rules run, because it is an absence of evidence. A stretch
        // of digital silence encodes as alternating bits, so it decodes to a one-step buzz at half
        // the sample rate: weighed as evidence it looks exactly like a hi-hat.
        if (features.Amplitude <= SilenceFloor)
        {
            return (DpcmRole.Artifact, 1.0, ["no signal above the DAC's own step"]);
        }

        // A one-way ramp is the DAC being walked to a new resting level, not a note or a hit.
        if (features.MonotonicRatio >= OneWayRatio)
        {
            return (DpcmRole.Artifact, 1.0, ["one-way ramp, never turns around"]);
        }

        var scores = new Dictionary<DpcmRole, double>();
        var totals = new OrderedDictionary<DpcmRole, double>();
        var evidence = new Dictionary<DpcmRole, List<string>>();
        foreach (var rule in Rules)
        {
            totals[rule.Role] = totals.GetValueOrDefault(rule.Role) + Math.Max(rule.Weight, 0.0);
            if (rule.Test(features))
            {
                scores[rule.Role] = scores.GetValueOrDefault(rule.Role) + rule.Weight;
                if (!evidence.TryGetValue(rule.Role, out var names))
                {
                    evidence[rule.Role] = names = [];
                }

                names.Add(rule.Weight > 0 ? rule.Name : $"NOT {rule.Name}");
            }
        }

        var ratios = totals
            .Where(entry => entry.Value > 0)
            .Select(entry => (Role: entry.Key, Ratio: Math.Max(0.0, scores.GetValueOrDefault(entry.Key)) / entry.Value))
            .ToList();

        // An artifact verdict wins outright: playing a DAC blip as a drum is far worse than
        // dropping a quiet sample that might have been one.
        var artifact = ratios.FirstOrDefault(entry => entry.Role == DpcmRole.Artifact).Ratio;
        if (artifact >= 0.5)
        {
            return (DpcmRole.Artifact, artifact, Evidence(DpcmRole.Artifact));
        }

        // A stable sort, so equal scores keep the order the rules name the roles in.
        var ranked = ratios.Where(entry => entry.Role != DpcmRole.Artifact).OrderByDescending(entry => entry.Ratio).ToList();
        var (best, bestRatio) = ranked[0];
        var runnerUp = ranked.Count > 1 ? ranked[1].Ratio : 0.0;
        if (bestRatio <= 0.0)
        {
            return (DpcmRole.NoiseSfx, 0.0, ["no rule matched"]);
        }

        // Confidence is the margin over the nearest rival, not the raw score: a sample that fits
        // two roles equally well is genuinely ambiguous.
        var confidence = bestRatio * (1.0 - (0.5 * (runnerUp / bestRatio)));
        return (best, Math.Round(Math.Min(1.0, confidence), 3), Evidence(best));

        IReadOnlyList<string> Evidence(DpcmRole role) => evidence.TryGetValue(role, out var names) ? names : [];
    }

    /// <summary>Analyses a run of sample references, one entry per distinct sample, in order of first use.</summary>
    public static OrderedDictionary<SampleRef, SampleAnalysis> AnalyzeAll(
        DpcmRom rom,
        IEnumerable<SampleRef> refs,
        IReadOnlyDictionary<int, DpcmRole>? overrides)
    {
        var analyses = new OrderedDictionary<SampleRef, SampleAnalysis>();
        var plays = new Dictionary<SampleRef, int>();
        foreach (var sample in refs)
        {
            plays[sample] = plays.GetValueOrDefault(sample) + 1;
            if (!analyses.ContainsKey(sample))
            {
                analyses[sample] = Analyze(rom, sample, overrides is not null && overrides.TryGetValue(sample.Address, out var role) ? role : null);
            }
        }

        return ApplySongContext(analyses, plays);
    }

    /// <summary>
    /// A Game Boy wave table from one period of the sample itself, which keeps some of its timbre
    /// instead of replacing it with a generic waveform. Without a usable pitch it falls back to the
    /// loudest window.
    /// </summary>
    public static int[] WaveTable(SampleAnalysis analysis, int points = 32, int levels = 16)
    {
        var waveform = analysis.Decoded.Waveform;
        if (waveform.Length < points)
        {
            return [.. Enumerable.Repeat(levels / 2, points)];
        }

        var rate = analysis.Decoded.RateHz;
        var pitch = analysis.Features.PitchHz;
        var period = pitch > 0 && analysis.Features.PitchConfidence > 0.15
            ? (int)Math.Round(rate / pitch)
            : Math.Min(waveform.Length, (int)(rate / 220.0));
        period = Math.Max(points, Math.Min(period, waveform.Length));

        var start = ArgMax(Envelope(waveform, rate));
        if (start + period > waveform.Length)
        {
            start = Math.Max(0, waveform.Length - period);
        }

        var cycle = waveform.AsSpan(start, period);
        var resampled = new double[points];
        var positions = Dsp.Linspace(0, cycle.Length - 1, points);
        for (var index = 0; index < points; index++)
        {
            resampled[index] = Dsp.Interpolate(cycle, positions[index]);
        }

        var span = resampled.Max(Math.Abs) + 1e-9;
        return [.. resampled.Select(value => (int)Math.Clamp(Math.Round((((value / span) * 0.5) + 0.5) * (levels - 1)), 0, levels - 1))];
    }

    private static SampleAnalysis Analyze(DpcmRom rom, SampleRef sample, DpcmRole? roleOverride)
    {
        var decoded = Decode(rom, sample);
        var features = ExtractFeatures(decoded);
        var (role, confidence, reasons) = Classify(features);
        return roleOverride is { } forced
            ? new SampleAnalysis(sample, decoded, features, forced, 1.0, [$"forced by hand (auto verdict was {role.ToName()})"], Overridden: true)
            : new SampleAnalysis(sample, decoded, features, role, confidence, reasons);
    }

    // Re-reads percussion verdicts in the light of how the song uses the sample. In isolation a
    // short sampled bass note really does look like a kick: low, dark, struck. The evidence that
    // separates them is in the song: a kick is played at one or two pitches, a bass line at however
    // many the melody needs. The decision is taken per address, because the instrument is the
    // sample and the rate is only which note it is playing.
    private static OrderedDictionary<SampleRef, SampleAnalysis> ApplySongContext(
        OrderedDictionary<SampleRef, SampleAnalysis> analyses,
        Dictionary<SampleRef, int> plays)
    {
        var byAddress = new OrderedDictionary<int, List<SampleRef>>();
        foreach (var sample in analyses.Keys)
        {
            if (!byAddress.TryGetValue(sample.Address, out var list))
            {
                byAddress[sample.Address] = list = [];
            }

            list.Add(sample);
        }

        var result = new OrderedDictionary<SampleRef, SampleAnalysis>(analyses);
        foreach (var refs in byAddress.Values)
        {
            if (refs.Any(sample => analyses[sample].Overridden) || !refs.Any(sample => PercussiveRoles.Contains(analyses[sample].Role)))
            {
                continue;
            }

            // The median, not the best: one lucky reading is not evidence that a whole drum sample
            // is really an instrument.
            var confidences = refs.Select(sample => analyses[sample].Features.PitchConfidence).Order().ToList();
            var medianConfidence = confidences[confidences.Count / 2];

            if (refs.Count < MelodicRateCount)
            {
                RetuneDrums(result, analyses, refs, medianConfidence, plays);
                continue;
            }

            if (medianConfidence < MelodicPitchConfidence)
            {
                continue;
            }

            var anchor = refs.MaxBy(sample => analyses[sample].Features.PitchConfidence)!;
            var reason = $"the song plays this sample at {refs.Count} different rates: a melodic instrument, not a drum";
            foreach (var sample in refs)
            {
                var analysis = analyses[sample];
                if (PercussiveRoles.Contains(analysis.Role))
                {
                    result[sample] = new SampleAnalysis(
                        sample,
                        analysis.Decoded,
                        BorrowPitch(analysis, analyses[anchor]),
                        DpcmRole.Tonal,
                        analysis.Confidence,
                        [reason, $"isolated verdict was {analysis.Role.ToName()}"]);
                }
            }
        }

        return result;
    }

    // Hears a drum that was deliberately retuned as a different drum. A driver that plays the kick
    // sample a fourth down for three hits in a bar is playing a tom fill. The most-used tuning
    // keeps its verdict: that one is the beat. Only the departures are re-read.
    private static void RetuneDrums(
        OrderedDictionary<SampleRef, SampleAnalysis> result,
        OrderedDictionary<SampleRef, SampleAnalysis> analyses,
        List<SampleRef> refs,
        double medianConfidence,
        Dictionary<SampleRef, int> plays)
    {
        if (refs.Count < 2 || medianConfidence < TunedDrumPitchConfidence)
        {
            return;
        }

        var tuned = refs.Where(sample => analyses[sample].Role is DpcmRole.Kick or DpcmRole.Snare or DpcmRole.Tom).ToList();
        if (tuned.Count < 2)
        {
            return;
        }

        var home = tuned.MaxBy(sample => (plays.GetValueOrDefault(sample), analyses[sample].Features.PitchConfidence))!;
        var homePitch = analyses[home].Features.PitchHz;
        if (homePitch <= 0.0)
        {
            return;
        }

        foreach (var sample in tuned)
        {
            var analysis = analyses[sample];
            var pitch = analysis.Features.PitchHz;
            if (ReferenceEquals(sample, home) || sample == home || analysis.Role == DpcmRole.Tom || pitch <= 0.0)
            {
                continue;
            }

            var semitones = Math.Abs(12.0 * Math.Log2(pitch / homePitch));
            if (semitones < TunedDrumSemitones)
            {
                continue;
            }

            result[sample] = analysis with
            {
                Role = DpcmRole.Tom,
                Reasons =
                [
                    string.Create(CultureInfo.InvariantCulture, $"the song retunes this drum {semitones:0} semitones for these hits: a pitched drum, not another {analysis.Role.ToName()}"),
                    $"isolated verdict was {analysis.Role.ToName()}",
                ],
            };
        }
    }

    // Recovers a note's pitch from a sibling when the detector could not hear it. The DMC rate is a
    // clock divider, so the same bytes played faster come out proportionally higher, exactly.
    private static SampleFeatures BorrowPitch(SampleAnalysis analysis, SampleAnalysis anchor)
    {
        if (analysis.Features.PitchConfidence >= MelodicPitchConfidence
            || anchor.Features.PitchConfidence < MelodicPitchConfidence
            || anchor.Decoded.RateHz <= 0)
        {
            return analysis.Features;
        }

        return analysis.Features with
        {
            PitchHz = anchor.Features.PitchHz * (analysis.Decoded.RateHz / anchor.Decoded.RateHz),
            PitchConfidence = anchor.Features.PitchConfidence,
        };
    }

    private static float[] Envelope(float[] waveform, double rateHz)
    {
        var window = Math.Max(1, (int)(rateHz * EnvelopeWindowSeconds));
        var magnitude = new float[waveform.Length];
        for (var index = 0; index < waveform.Length; index++)
        {
            magnitude[index] = Math.Abs(waveform[index]);
        }

        return Dsp.MovingAverageSame(magnitude, window);
    }

    private static (double Low, double Mid, double High, double Centroid, double Flatness) BandEnergies(float[] waveform, double rateHz)
    {
        var size = 1 << (int)Math.Floor(Math.Log2(Math.Max(waveform.Length, 2)));
        if (size < 8)
        {
            return (0.0, 0.0, 0.0, 0.0, 0.0);
        }

        var mean = (float)(waveform.AsSpan(0, size).ToArray().Sum(value => (double)value) / size);
        var window = Dsp.Hanning(size);
        var block = new double[size];
        for (var index = 0; index < size; index++)
        {
            block[index] = (waveform[index] - mean) * window[index];
        }

        var magnitudes = Dsp.RealFftMagnitudes(block);
        var spacing = size * (1.0 / rateHz);
        double total = 1e-12, low = 0.0, mid = 0.0, high = 0.0, weighted = 0.0, logSum = 0.0, positiveSum = 0.0;
        for (var bin = 0; bin < magnitudes.Length; bin++)
        {
            var power = magnitudes[bin] * magnitudes[bin];
            var frequency = bin / spacing;
            total += power;
            weighted += power * frequency;
            if (frequency < BandLowHz)
            {
                low += power;
            }
            else if (frequency < BandMidHz)
            {
                mid += power;
            }
            else
            {
                high += power;
            }

            if (bin > 0)
            {
                logSum += Math.Log(power + 1e-12);
                positiveSum += power + 1e-12;
            }
        }

        var count = magnitudes.Length - 1;
        var flatness = Math.Exp(logSum / count) / (positiveSum / count);
        return (low / total, mid / total, high / total, weighted / total, flatness);
    }

    // Dominant pitch and how convincingly periodic the segment is. The peak is taken after the
    // autocorrelation first crosses zero, not as the global maximum: a bass tone sampled at 33 kHz
    // is so oversampled that it correlates strongly with itself at every short lag, and a plain
    // maximum would always answer with the shortest lag allowed. Confidence matters more than the
    // pitch: a snare always yields some peak, and only the confidence says to ignore it.
    private static (double PitchHz, double Confidence) AutocorrelationPitch(ReadOnlySpan<float> segment, double rateHz)
    {
        if (segment.Length < 64)
        {
            return (0.0, 0.0);
        }

        var sum = 0.0;
        foreach (var value in segment)
        {
            sum += value;
        }

        var mean = (float)(sum / segment.Length);
        var block = new float[segment.Length];
        for (var index = 0; index < block.Length; index++)
        {
            block[index] = segment[index] - mean;
        }

        var lo = Math.Max(1, (int)(rateHz / PitchMaxHz));
        var hi = Math.Min((int)(rateHz / PitchMinHz), block.Length - 1);
        var energy = Correlation(block, 0);
        if (energy <= 1e-9 || hi <= lo)
        {
            return (0.0, 0.0);
        }

        var correlation = new float[hi];
        correlation[0] = (float)energy;
        var firstNegative = -1;
        for (var lag = 1; lag < hi; lag++)
        {
            correlation[lag] = (float)Correlation(block, lag);
            if (firstNegative < 0 && correlation[lag] < 0)
            {
                firstNegative = lag;
            }
        }

        var searchStart = firstNegative >= 0 ? Math.Max(lo, firstNegative) : lo;
        if (searchStart >= hi)
        {
            searchStart = lo;
        }

        var best = searchStart;
        for (var lag = searchStart + 1; lag < hi; lag++)
        {
            if (correlation[lag] > correlation[best])
            {
                best = lag;
            }
        }

        var confidence = correlation[best] / (correlation[0] + 1e-12f);
        return (rateHz / best, Math.Clamp(confidence, 0.0, 1.0));
    }

    private static double Correlation(float[] block, int lag)
    {
        var sum = 0.0;
        for (var index = lag; index < block.Length; index++)
        {
            sum += block[index] * block[index - lag];
        }

        return sum;
    }

    // The signed shape of the sample with the format's own dither removed. Delta modulation cannot
    // hold still: tracking anything slower than two DAC steps per sample makes it alternate up and
    // down, so sample-to-sample direction says nothing about the sound. Smoothing to the envelope
    // window and decimating leaves the movement a listener would follow.
    private static float[] Trend(float[] waveform, double rateHz)
    {
        var window = Math.Max(1, (int)(rateHz * EnvelopeWindowSeconds));
        if (waveform.Length <= window * 2)
        {
            return waveform;
        }

        var smoothed = Dsp.MovingAverageValid(waveform, window);
        var decimated = new float[((smoothed.Length - 1) / window) + 1];
        for (var index = 0; index < decimated.Length; index++)
        {
            decimated[index] = smoothed[index * window];
        }

        return decimated;
    }

    private static int ArgMax(float[] values)
    {
        var best = 0;
        for (var index = 1; index < values.Length; index++)
        {
            if (values[index] > values[best])
            {
                best = index;
            }
        }

        return best;
    }
}
