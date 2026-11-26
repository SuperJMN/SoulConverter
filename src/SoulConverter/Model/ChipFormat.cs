using System.Text.Json.Nodes;
using SoulConverter.Vgm;

namespace SoulConverter.Model;

/// <summary>What a writer produced: the target log, and what it has to say about it.</summary>
internal sealed class ChipWriteResult
{
    public required byte[] Vgm { get; init; }

    /// <summary>True when no voice ever sounds in the written log.</summary>
    public required bool Silent { get; init; }

    public required int RegisterWrites { get; init; }

    public List<ConversionWarning> Warnings { get; } = [];

    /// <summary>The writer's own sections of the report, merged at the top level.</summary>
    public JsonObject Sections { get; } = [];

    /// <summary>The writer's own entries of the report's <c>output</c> section.</summary>
    public JsonObject Output { get; } = [];
}

/// <summary>
/// One chip: a reader that replays its log into musical state and a writer that turns musical
/// state into its register writes. A conversion is one chip's reader and another's writer, so a
/// new chip is one more implementation of this and no more pairs.
/// </summary>
internal interface IChipFormat
{
    SoundChip Chip { get; }

    MusicalTimeline Read(VgmLog log, ConversionOptions options);

    ChipWriteResult Write(MusicalTimeline timeline, ConversionOptions options);

    /// <summary>
    /// Renders a log of this chip to mono 44100 Hz samples, with each voice's frequency per tick.
    /// <paramref name="timeline"/> is the log already read on that tick grid, when there is one.
    /// </summary>
    RenderedAudio Render(VgmLog log, MusicalTimeline? timeline, double tickHz);
}

/// <summary>A render and, per voice name, the frequency it plays on each tick (0 when silent).</summary>
internal sealed record RenderedAudio(double[] Samples, IReadOnlyDictionary<string, double[]> Frequencies);
