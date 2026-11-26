using System.Text.Json.Nodes;

namespace SoulConverter;

/// <summary>Something the target chip could not play as the source did, or that the caller should know.</summary>
/// <param name="Code">A stable identifier from <see cref="WarningCodes"/>.</param>
/// <param name="Message">A sentence for a person.</param>
public sealed record ConversionWarning(string Code, string Message)
{
    public override string ToString() => Message;
}

/// <summary>The identifiers <see cref="ConversionWarning.Code"/> takes.</summary>
public static class WarningCodes
{
    /// <summary>The converted log never sounds: nothing in the source can play on the target.</summary>
    public const string Silent = "silent";

    /// <summary>DPCM samples (or direct DAC output) were dropped.</summary>
    public const string DpcmDropped = "dpcm-dropped";

    /// <summary>Some DPCM hits found no Game Boy channel free enough to carry them.</summary>
    public const string DpcmHitsDropped = "dpcm-hits-dropped";

    /// <summary>A DPCM sample was classified with low confidence.</summary>
    public const string DpcmLowConfidence = "dpcm-low-confidence";

    /// <summary>The source streams PCM through the DAC, which cannot be re-synthesised.</summary>
    public const string PcmStream = "pcm-stream";

    /// <summary>Notes below the target's range were raised by whole octaves.</summary>
    public const string Transposed = "transposed";

    /// <summary>A noise rate has no close match on the target.</summary>
    public const string NoiseMapping = "noise-mapping";

    /// <summary>Duty cycles the target does not have play as square waves.</summary>
    public const string Duty = "duty";

    /// <summary>A triangle or wave-table voice plays with another waveform.</summary>
    public const string Timbre = "timbre";

    /// <summary>Short-mode or 7-bit noise plays as periodic noise, or the reverse.</summary>
    public const string PeriodicNoise = "periodic-noise";

    /// <summary>Stereo panning plays as mono.</summary>
    public const string Stereo = "stereo";

    /// <summary>A voice lost its volume control or part of its dynamics.</summary>
    public const string Dynamics = "dynamics";

    /// <summary>The renders of the two logs differ noticeably (only with <see cref="ConversionOptions.Analyze"/>).</summary>
    public const string Metrics = "metrics";
}

/// <summary>A converted log and what is known about the conversion.</summary>
public sealed class ConversionResult
{
    internal ConversionResult(
        byte[] vgm,
        SoundChip source,
        SoundChip target,
        IReadOnlyList<ConversionWarning> warnings,
        bool isSilent,
        JsonObject report,
        string summary,
        double[]? sourceAudio,
        double[]? targetAudio,
        DpcmStrategy dpcm)
    {
        Vgm = vgm;
        Source = source;
        Target = target;
        Warnings = warnings;
        IsSilent = isSilent;
        Report = report;
        Summary = summary;
        SourceAudio = sourceAudio;
        TargetAudio = targetAudio;
        Dpcm = dpcm;
    }

    /// <summary>The target chip's log, as an uncompressed VGM file.</summary>
    public byte[] Vgm { get; }

    /// <summary>The chip the source log drives.</summary>
    public SoundChip Source { get; }

    /// <summary>The chip <see cref="Vgm"/> drives.</summary>
    public SoundChip Target { get; }

    /// <summary>What the target could not play as the source did. Never a reason the conversion failed.</summary>
    public IReadOnlyList<ConversionWarning> Warnings { get; }

    /// <summary>
    /// True when <see cref="Vgm"/> never sounds, e.g. a sound effect made only of DPCM converted to
    /// a chip without a sample channel. A build can then try another source log.
    /// </summary>
    public bool IsSilent { get; }

    /// <summary>The conversion's facts and measurements, ready to serialise as JSON.</summary>
    public JsonObject Report { get; }

    /// <summary>A human-readable rendering of <see cref="Report"/>.</summary>
    public string Summary { get; }

    /// <summary>The source log rendered to mono 44100 Hz samples, when <see cref="ConversionOptions.Analyze"/> is on.</summary>
    public double[]? SourceAudio { get; }

    /// <summary>The converted log rendered to mono 44100 Hz samples, when <see cref="ConversionOptions.Analyze"/> is on.</summary>
    public double[]? TargetAudio { get; }

    /// <summary>The DPCM strategy this result was converted with.</summary>
    public DpcmStrategy Dpcm { get; }
}
