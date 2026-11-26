using SoulConverter.Vgm;

namespace SoulConverter.Model;

/// <summary>What a tone voice sounds like when its chip plays it.</summary>
internal enum Timbre
{
    /// <summary>A pulse with one of four duty cycles (2A03 pulses, DMG squares).</summary>
    Pulse,

    /// <summary>The 2A03's fixed 32-step triangle.</summary>
    Triangle,

    /// <summary>The DMG's programmable 32-step wave table.</summary>
    Wave,

    /// <summary>A plain 50% square (SN76489 tones).</summary>
    Square,
}

/// <summary>
/// One tone voice at one instant: whether it sounds, at which frequency and linear volume (0-15),
/// with which duty and timbre, and whether its source restarted it since the previous frame. The
/// envelope fields say how the source produced the volume, for a target with an envelope unit of
/// its own. <see cref="Addressed"/> is false until the source writes to the voice at all, so a
/// writer can leave a voice the source never used untouched.
/// </summary>
internal readonly record struct ToneVoice(
    bool Playing = false,
    double FrequencyHz = 0.0,
    int Volume = 0,
    int Duty = 2,
    Timbre Timbre = Timbre.Pulse,
    bool Retriggered = false,
    bool ConstantVolume = true,
    int EnvelopePeriod = 0,
    bool EnvelopeLoop = false,
    bool Addressed = true);

/// <summary>
/// The noise voice at one instant: whether it sounds, the rate its generator is clocked at, its
/// linear volume (0-15), whether it runs its short, periodic sequence, and whether its source
/// restarted it. <see cref="RateIndex"/> is the source's own index for that rate, for reports.
/// </summary>
internal readonly record struct NoiseVoice(
    bool Playing = false,
    double ClockRateHz = 0.0,
    int Volume = 0,
    bool Short = false,
    bool Retriggered = false,
    bool ConstantVolume = true,
    int EnvelopePeriod = 0,
    bool EnvelopeLoop = false,
    int RateIndex = 0,
    bool Addressed = true,
    bool Started = true);

/// <summary>Identifies one DPCM sample: where it lives and how fast it plays.</summary>
internal sealed record SampleRef(int Address, int Length, int RateIndex)
{
    // NTSC DMC output rates, in CPU cycles per output bit, indexed by the low nibble of $4010.
    public static readonly int[] RateTable = [428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54];

    public const double NtscCpuHz = 1789773.0;

    public double RateHz => NtscCpuHz / RateTable[RateIndex];

    public double DurationSeconds => Length * 8 / RateHz;
}

/// <summary>
/// What the sample channel does on one frame. It is monophonic, so at most one sample is in flight.
/// </summary>
internal readonly record struct SampleVoice(
    bool Playing = false,
    SampleRef? Sample = null,
    bool Started = false,
    double ElapsedSeconds = 0.0,
    bool Looping = false,
    int DirectLevel = 0);

/// <summary>
/// The musical state from one VGM sample position until the next frame: three tone voices (the
/// pulses, squares or tones 0-1, then the triangle, wave channel or tone 2), the noise voice and
/// the sample voice.
/// </summary>
internal sealed class MusicalFrame
{
    private static readonly bool[] NoPowerWrites = [];

    public required int Index { get; init; }

    public required int Sample { get; init; }

    public ToneVoice Tone1 { get; init; }

    public ToneVoice Tone2 { get; init; }

    public ToneVoice Tone3 { get; init; }

    public NoiseVoice Noise { get; init; }

    public SampleVoice Dmc { get; init; }

    /// <summary>The source switched its whole sound unit on (true) or off (false), in order.</summary>
    public IReadOnlyList<bool> PowerWrites { get; init; } = NoPowerWrites;

    /// <summary>A sounding voice is routed to one stereo side only.</summary>
    public bool StereoSplit { get; init; }

    public ToneVoice Tone(int index) => index switch
    {
        0 => Tone1,
        1 => Tone2,
        _ => Tone3,
    };

    public bool AnyVoicePlaying => Tone1.Playing || Tone2.Playing || Tone3.Playing || Noise.Playing;
}

/// <summary>
/// A log replayed into musical state. Every reader produces one and every writer consumes one,
/// which is what makes a conversion a reader and a writer instead of a pair written by hand.
/// </summary>
internal sealed class MusicalTimeline
{
    public required VgmLog Log { get; init; }

    public required double TickHz { get; init; }

    public required List<MusicalFrame> Frames { get; init; }

    /// <summary>True when the frames are exactly the <see cref="TickHz"/> grid, one per tick.</summary>
    public required bool OnGrid { get; init; }

    public int? LoopFrame { get; init; }

    public required int TotalSamples { get; init; }

    public SoundChip Source => Log.Chip;

    public int? LoopSample => Log.LoopSample;

    public double SamplesPerTick => VgmLog.SampleRate / TickHz;

    /// <summary>
    /// The same music on the tick grid: each tick holds the state in force at its sample, and a
    /// restart anywhere since the previous tick is kept, so a writer that works frame by frame
    /// does not miss a note that began between two ticks.
    /// </summary>
    public MusicalTimeline ToGrid()
    {
        if (OnGrid)
        {
            return this;
        }

        var samplesPerTick = SamplesPerTick;
        var tickCount = Math.Max(1, (int)Math.Ceiling(TotalSamples / samplesPerTick));
        var ticks = new List<MusicalFrame>(tickCount);
        var cursor = 0;
        MusicalFrame? current = null;
        for (var index = 0; index < tickCount; index++)
        {
            var sample = (int)Math.Round(index * samplesPerTick);
            bool tone1 = false, tone2 = false, tone3 = false, noise = false, stereo = false;
            while (cursor < Frames.Count && Frames[cursor].Sample <= sample)
            {
                current = Frames[cursor++];
                tone1 |= current.Tone1.Retriggered;
                tone2 |= current.Tone2.Retriggered;
                tone3 |= current.Tone3.Retriggered;
                noise |= current.Noise.Retriggered;
            }

            stereo = current?.StereoSplit ?? false;
            ticks.Add(current is null
                ? new MusicalFrame { Index = index, Sample = sample }
                : new MusicalFrame
                {
                    Index = index,
                    Sample = sample,
                    Tone1 = current.Tone1 with { Retriggered = tone1 },
                    Tone2 = current.Tone2 with { Retriggered = tone2 },
                    Tone3 = current.Tone3 with { Retriggered = tone3 },
                    Noise = current.Noise with { Retriggered = noise },
                    Dmc = current.Dmc,
                    StereoSplit = stereo,
                });
        }

        return new MusicalTimeline
        {
            Log = Log,
            TickHz = TickHz,
            Frames = ticks,
            OnGrid = true,
            LoopFrame = Log.LoopSample is { } loop ? Math.Min(tickCount - 1, (int)Math.Round(loop / samplesPerTick)) : null,
            TotalSamples = TotalSamples,
        };
    }
}
