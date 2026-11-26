namespace SoulConverter;

/// <summary>How a log is converted. The defaults suit a build that only needs the target log.</summary>
public sealed record ConversionOptions
{
    /// <summary>The default options.</summary>
    public static ConversionOptions Default { get; } = new();

    /// <summary>
    /// The resolution of the conversion grid, in ticks per second. 60 by default; raise it when
    /// the source updates a voice several times per frame (fast arpeggios), at the cost of size.
    /// </summary>
    public double TickHz { get; init; } = 60.0;

    /// <summary>How a Game Boy target follows the source's volume.</summary>
    public VolumeMode VolumeMode { get; init; } = VolumeMode.Retrigger;

    /// <summary>
    /// Raise notes below the target's range by whole octaves (and report it). When false, a Game
    /// Boy target clamps them to its lowest note instead.
    /// </summary>
    public bool TransposeOutOfRange { get; init; } = true;

    /// <summary>What a Game Boy target does with the NES sample channel.</summary>
    public DpcmStrategy Dpcm { get; init; } = DpcmStrategy.Off;

    /// <summary>Roles forced by hand for DPCM samples, by sample address (e.g. <c>0xEA80</c>).</summary>
    public IReadOnlyDictionary<int, DpcmRole>? DpcmRoles { get; init; }

    /// <summary>
    /// Also render both logs and measure how alike they sound: fills <see cref="ConversionResult.SourceAudio"/>,
    /// <see cref="ConversionResult.TargetAudio"/> and the report's metrics. Off by default because
    /// a build does not need it and rendering takes far longer than converting.
    /// </summary>
    public bool Analyze { get; init; }

    /// <summary>A name for the log in error messages and in the report, e.g. its file name.</summary>
    public string? SourceName { get; init; }
}
