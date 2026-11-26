using System.Globalization;

namespace SoulConverter.Dmg;

/// <summary>Which DMG generator a recipe needs. Each has a fixed timbre.</summary>
internal enum Host
{
    /// <summary>Square with a hardware frequency sweep.</summary>
    Ch1,

    /// <summary>Square, no sweep.</summary>
    Ch2,

    /// <summary>Wave, an arbitrary 32-step timbre.</summary>
    Ch3,

    /// <summary>Noise.</summary>
    Ch4,
}

/// <summary>
/// A self-contained percussive or tonal event on one DMG channel. <see cref="Frames"/> is how long
/// the host channel is unavailable, which is what the allocator prices. <see cref="PitchHz"/> is
/// the note the recipe sounds, or 0 when it has none: noise has no pitch, which is exactly what
/// makes it safe to drop anywhere.
/// </summary>
internal sealed record Recipe(
    Host Host,
    DpcmRole Role,
    int Frames,
    IReadOnlyList<(int Register, int Value)> Writes,
    double Amplitude,
    string Description,
    double PitchHz = 0.0);

/// <summary>
/// Turns a classified sample into Game Boy register writes. Every recipe leans on the DMG's own
/// length counter and envelope generator instead of per-frame writes: the hardware envelope runs
/// at 64 Hz and the length counter at 256 Hz, so one burst of writes at the attack buys a decay
/// shaped more finely than a 60 Hz log could draw. Recipes are parameterised by what was measured
/// in the sample: the kick's sweep starts from its own pitch, the snare's noise divisor is chosen
/// so the Game Boy's spectral centroid lands on the measured one.
/// </summary>
internal static class Percussion
{
    private const double LengthTickHz = 256.0;
    private const double EnvelopeTickHz = 64.0;
    private const double SweepTickHz = 128.0;

    // White noise band-limited to half the LFSR clock has its centroid at roughly a quarter of
    // that clock, so a target centroid asks for a four times faster LFSR.
    private const double NoiseCentroidRatio = 4.0;

    public static string Name(this Host host) => host switch
    {
        Host.Ch1 => "ch1",
        Host.Ch2 => "ch2",
        Host.Ch3 => "ch3",
        _ => "ch4",
    };

    /// <summary>
    /// How much is lost by dropping this voice, in the allocator's units. Kick and snare carry the
    /// groove; a hat is decoration. Amplitude enters as a square root because loudness is perceived
    /// roughly that way, and confidence scales it so a doubtful verdict is a worse bet.
    /// </summary>
    public static double RoleBenefit(SampleAnalysis analysis)
    {
        var baseBenefit = analysis.Role switch
        {
            DpcmRole.Kick => 1.0,
            DpcmRole.Snare => 0.95,
            DpcmRole.Tonal => 0.9,
            DpcmRole.Tom => 0.7,
            DpcmRole.Hat => 0.45,
            DpcmRole.NoiseSfx => 0.3,
            _ => 0.0,
        };
        var loudness = Math.Min(1.0, Math.Sqrt(Math.Max(0.0, analysis.Features.Amplitude)));
        var certainty = 0.5 + (0.5 * analysis.Confidence);
        return baseBenefit * loudness * certainty;
    }

    /// <summary>
    /// Every way this sample could be played, best first. The order encodes musical preference:
    /// the recipe that sounds most like the original comes first, cheaper fallbacks after it.
    /// </summary>
    public static List<Recipe> Candidates(SampleAnalysis analysis, double tickHz, double heldSeconds, bool allowTonalHosts, bool allowLayers)
    {
        var recipes = new List<Recipe>();
        switch (analysis.Role)
        {
            case DpcmRole.Kick:
                if (allowTonalHosts)
                {
                    recipes.Add(KickSquare(analysis, Host.Ch1, tickHz));
                    recipes.Add(KickSquare(analysis, Host.Ch2, tickHz));
                }

                recipes.Add(KickNoise(analysis, tickHz));
                break;
            case DpcmRole.Snare:
                recipes.Add(SnareNoise(analysis, tickHz));
                if (allowTonalHosts && allowLayers)
                {
                    recipes.Add(SnareBody(analysis, Host.Ch2, tickHz));
                    recipes.Add(SnareBody(analysis, Host.Ch1, tickHz));
                }

                break;
            case DpcmRole.Hat:
                recipes.Add(HatNoise(analysis, tickHz));
                break;
            case DpcmRole.Tom:
                if (allowTonalHosts)
                {
                    recipes.Add(TomWave(analysis, tickHz));
                }

                // Not the kick recipe as a fallback: its sweep erases the pitch that made this a
                // tom. Noise keeps the attack instead.
                recipes.Add(SnareNoise(analysis, tickHz));
                break;
            case DpcmRole.Tonal:
                if (allowTonalHosts)
                {
                    recipes.Add(TonalWave(analysis, tickHz, heldSeconds));
                    recipes.Add(TonalSquare(analysis, Host.Ch2, tickHz, heldSeconds));
                    recipes.Add(TonalSquare(analysis, Host.Ch1, tickHz, heldSeconds));
                }

                break;
            case DpcmRole.NoiseSfx:
                recipes.Add(SnareNoise(analysis, tickHz));
                break;
        }

        return recipes;
    }

    // The counter counts up to its maximum at 256 Hz, so the value loaded is the complement of the
    // duration. Longer durations saturate at the longest available.
    private static int LengthLoad(double seconds, int bits)
    {
        var span = 1 << bits;
        var steps = (int)Math.Round(seconds * LengthTickHz);
        return Math.Max(0, Math.Min(span - 1, span - steps));
    }

    // Pace 0 disables the envelope entirely, so a decay too fast to express is clamped to the
    // fastest real fade instead of to silence.
    private static int EnvelopePace(double seconds, int startVolume)
    {
        if (startVolume <= 0 || seconds <= 0)
        {
            return 1;
        }

        var ideal = seconds * EnvelopeTickHz / startVolume;
        return Math.Max(1, Math.Min(7, (int)Math.Round(ideal)));
    }

    // NR10 for a downward pitch sweep of roughly `octavesDown`. Each sweep tick subtracts
    // `period >> step` from the period; halving the period takes about 0.69 * 2^step ticks, which
    // fixes the pace once the wanted duration is known.
    private static int SweepRegister(double octavesDown, double seconds)
    {
        if (octavesDown <= 0 || seconds <= 0)
        {
            return 0;
        }

        var best = 0;
        var bestError = double.PositiveInfinity;
        for (var step = 1; step < 8; step++)
        {
            var ticksPerOctave = Math.Log(2.0) * (1 << step);
            for (var pace = 1; pace < 8; pace++)
            {
                var achieved = seconds * SweepTickHz / (pace * ticksPerOctave);
                var error = Math.Abs(achieved - octavesDown);
                if (error < bestError)
                {
                    best = (pace << 4) | 0x08 | step;
                    bestError = error;
                }
            }
        }

        return best;
    }

    private static List<(int Register, int Value)> SquareWrites(Host host, int period, int duty, int volume, int envelopePace, double lengthSeconds, int sweep = 0)
    {
        // NR20 does not exist; the base keeps the arithmetic uniform.
        var registerBase = host == Host.Ch1 ? GameBoy.NR10 : GameBoy.NR21 - 1;
        var length = LengthLoad(lengthSeconds, bits: 6);
        var writes = new List<(int Register, int Value)>();
        if (host == Host.Ch1)
        {
            writes.Add((GameBoy.NR10, sweep));
        }

        writes.Add((registerBase + 1, ((duty & 3) << 6) | length));
        writes.Add((registerBase + 2, ((volume & 0x0F) << 4) | (envelopePace & 7)));
        writes.Add((registerBase + 3, period & 0xFF));
        writes.Add((registerBase + 4, 0xC0 | ((period >> 8) & 7)));
        return writes;
    }

    private static List<(int Register, int Value)> NoiseWrites(int shift, int divisor, bool shortMode, int volume, int envelopePace, double lengthSeconds) =>
    [
        (GameBoy.NR41, LengthLoad(lengthSeconds, bits: 6)),
        (GameBoy.NR42, ((volume & 0x0F) << 4) | (envelopePace & 7)),
        (GameBoy.NR43, ((shift & 0x0F) << 4) | (shortMode ? 0x08 : 0) | (divisor & 7)),
        (GameBoy.NR44, 0xC0),
    ];

    // Loads a timbre and triggers CH3. Wave RAM can only be rewritten with the channel's DAC off,
    // which clicks; the click is placed on the attack transient, where the event masks it.
    private static List<(int Register, int Value)> WaveWrites(int period, int level, double lengthSeconds, int[] wave)
    {
        var length = LengthLoad(lengthSeconds, bits: 8);
        var writes = new List<(int Register, int Value)> { (GameBoy.NR30, 0x00) };
        var packed = GameBoy.PackWave(wave);
        for (var index = 0; index < packed.Length; index++)
        {
            writes.Add((GameBoy.WaveRamBase + index, packed[index]));
        }

        writes.Add((GameBoy.NR30, 0x80));
        writes.Add((GameBoy.NR31, length & 0xFF));
        writes.Add((GameBoy.NR32, (level & 3) << 5));
        writes.Add((GameBoy.NR33, period & 0xFF));
        writes.Add((GameBoy.NR34, 0xC0 | ((period >> 8) & 7)));
        return writes;
    }

    private static int Frames(double seconds, double tickHz) => Math.Max(1, (int)Math.Ceiling(seconds * tickHz));

    // A kick as a swept square: the standard chiptune substitute for a sample. The sweep is what
    // makes it read as a drum instead of a short bass note, and CH1 is the only channel with one.
    private static Recipe KickSquare(SampleAnalysis analysis, Host host, double tickHz)
    {
        var f = analysis.Features;
        var pitch = f.PitchHz >= 30.0 && f.PitchHz <= 400.0 ? f.PitchHz : 160.0;

        // Start above the measured pitch and fall onto it: a kick's perceived pitch is its body.
        var startHz = Math.Max(GameBoy.SquareMinHz, pitch * 2.2);
        var (period, _) = GameBoy.SquarePeriod(startHz);
        var body = Math.Max(0.03, Math.Min(f.EffectiveDuration, 0.20));
        var sweep = host == Host.Ch1 ? SweepRegister(1.2, body * 0.8) : 0;
        return new Recipe(
            host,
            DpcmRole.Kick,
            Frames(body, tickHz),
            SquareWrites(host, period, duty: 2, volume: 15, EnvelopePace(body, 15), body, sweep),
            f.Amplitude,
            Text($"{(sweep != 0 ? "swept" : "plain")} square from {startHz:0} Hz over {body * 1000:0} ms"),
            pitch);
    }

    // A kick as low noise: worse, but it never steals a melodic channel. At the bottom of the
    // noise table the LFSR runs slowly enough to be heard as a rumble, which reads as a thump.
    private static Recipe KickNoise(SampleAnalysis analysis, double tickHz)
    {
        var f = analysis.Features;
        var body = Math.Max(0.03, Math.Min(f.EffectiveDuration, 0.15));
        var (shift, divisor, _) = GameBoy.NearestNoise(Math.Max(120.0, f.PitchHz * NoiseCentroidRatio));
        return new Recipe(
            Host.Ch4,
            DpcmRole.Kick,
            Frames(body, tickHz),
            NoiseWrites(shift, divisor, shortMode: false, volume: 15, EnvelopePace(body, 15), body),
            f.Amplitude,
            Text($"low noise thump over {body * 1000:0} ms"));
    }

    private static Recipe SnareNoise(SampleAnalysis analysis, double tickHz)
    {
        var f = analysis.Features;
        var body = Math.Max(0.02, Math.Min(f.EffectiveDuration, 0.15));
        var (shift, divisor, _) = GameBoy.NearestNoise(Math.Max(200.0, f.CentroidHz * NoiseCentroidRatio));
        return new Recipe(
            Host.Ch4,
            DpcmRole.Snare,
            Frames(body, tickHz),
            NoiseWrites(shift, divisor, shortMode: false, volume: 15, EnvelopePace(body, 15), body),
            f.Amplitude,
            Text($"noise at {GameBoy.NoiseFrequency(shift, divisor):0} Hz for centroid {f.CentroidHz:0} Hz over {body * 1000:0} ms"));
    }

    // The tonal half of a snare, layered under the noise when a square is free. A real snare is a
    // crack plus a shell resonance; a frame or two of a mid square underneath restores the body.
    private static Recipe SnareBody(SampleAnalysis analysis, Host host, double tickHz)
    {
        var f = analysis.Features;
        var body = Math.Max(0.02, Math.Min(f.EffectiveDuration * 0.5, 0.05));
        var pitch = Math.Max(GameBoy.SquareMinHz, Math.Min(f.CentroidHz * 0.5, 1200.0));
        var (period, _) = GameBoy.SquarePeriod(pitch);
        return new Recipe(
            host,
            DpcmRole.Snare,
            Frames(body, tickHz),
            SquareWrites(host, period, duty: 0, volume: 9, EnvelopePace(body, 9), body),
            f.Amplitude * 0.5,
            Text($"square body layer over {body * 1000:0} ms"),
            pitch);
    }

    private static Recipe HatNoise(SampleAnalysis analysis, double tickHz)
    {
        var f = analysis.Features;
        var body = Math.Max(0.012, Math.Min(f.EffectiveDuration, 0.06));
        var (shift, divisor, _) = GameBoy.NearestNoise(Math.Max(2000.0, f.CentroidHz * NoiseCentroidRatio));

        // The 7-bit LFSR repeats every 127 steps, giving a metallic ring that suits a hat and
        // separates it from the snare sharing the channel.
        var shortMode = f.Flatness < 0.35;
        return new Recipe(
            Host.Ch4,
            DpcmRole.Hat,
            Frames(body, tickHz),
            NoiseWrites(shift, divisor, shortMode, volume: 11, EnvelopePace(body, 11), body),
            f.Amplitude * 0.7,
            Text($"{(shortMode ? "metallic" : "white")} tick over {body * 1000:0} ms"));
    }

    // A tom as a pitched wave note that rings. Its pitch has to be audible, so there is no sweep,
    // and it has to ring, so it lasts the whole recording and not just its loud head.
    private static Recipe TomWave(SampleAnalysis analysis, double tickHz)
    {
        var f = analysis.Features;
        var body = Math.Max(0.04, Math.Min(analysis.Decoded.DurationSeconds, 0.30));
        var pitch = f.PitchHz > 0 ? f.PitchHz : 220.0;
        var (period, _) = GameBoy.WavePeriod(pitch);
        return new Recipe(
            Host.Ch3,
            DpcmRole.Tom,
            Frames(body, tickHz),
            WaveWrites(period, level: 1, body, Dpcm.WaveTable(analysis)),
            f.Amplitude,
            Text($"wave tom at {pitch:0} Hz ringing over {body * 1000:0} ms"),
            pitch);
    }

    // A melodic sample as a real note with a timbre lifted from the sample. CH3 is the right host
    // because its waveform is programmable: a bass line sampled from a real instrument keeps some
    // of its character instead of becoming a generic square.
    private static Recipe TonalWave(SampleAnalysis analysis, double tickHz, double heldSeconds)
    {
        var f = analysis.Features;
        var pitch = f.PitchHz >= 30.0 && f.PitchHz <= 2000.0 ? f.PitchHz : 110.0;
        var (period, _) = GameBoy.WavePeriod(pitch);
        return new Recipe(
            Host.Ch3,
            DpcmRole.Tonal,
            Frames(heldSeconds, tickHz),
            WaveWrites(period, level: 1, heldSeconds, Dpcm.WaveTable(analysis)),
            f.Amplitude,
            Text($"wave note at {pitch:0} Hz with a timbre from the sample"),
            pitch);
    }

    private static Recipe TonalSquare(SampleAnalysis analysis, Host host, double tickHz, double heldSeconds)
    {
        var f = analysis.Features;
        var pitch = f.PitchHz >= 30.0 && f.PitchHz <= 2000.0 ? f.PitchHz : 110.0;
        var (period, _) = GameBoy.SquarePeriod(pitch);
        return new Recipe(
            host,
            DpcmRole.Tonal,
            Frames(heldSeconds, tickHz),
            SquareWrites(host, period, duty: 1, volume: 12, envelopePace: 0, heldSeconds),
            f.Amplitude,
            Text($"square note at {pitch:0} Hz"),
            pitch);
    }

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
