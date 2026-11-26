using SoulConverter.Analysis;
using SoulConverter.Dmg;
using SoulConverter.Nes;
using SoulConverter.Sn;
using SoulConverter.Vgm;

namespace SoulConverter.Model;

/// <summary>The chips SoulConverter knows. Adding one is adding an entry here.</summary>
internal static class ChipFormats
{
    private static readonly IChipFormat[] All = [new NesFormat(), new DmgFormat(), new SnFormat()];

    public static IChipFormat For(SoundChip chip) =>
        All.FirstOrDefault(format => format.Chip == chip) ?? throw new ArgumentOutOfRangeException(nameof(chip), chip, "Unknown sound chip.");

    private sealed class NesFormat : IChipFormat
    {
        public SoundChip Chip => SoundChip.Nes2A03;

        public MusicalTimeline Read(VgmLog log, ConversionOptions options) => NesApu.Simulate(log, options.TickHz);

        public ChipWriteResult Write(MusicalTimeline timeline, ConversionOptions options) => NesWriter.Write(timeline, options);

        public RenderedAudio Render(VgmLog log, MusicalTimeline? timeline, double tickHz) =>
            TimelineSynth.Render(timeline ?? NesApu.Simulate(log, tickHz));
    }

    private sealed class DmgFormat : IChipFormat
    {
        public SoundChip Chip => SoundChip.GameBoyDmg;

        public MusicalTimeline Read(VgmLog log, ConversionOptions options) => DmgApu.Replay(log, options.TickHz);

        public ChipWriteResult Write(MusicalTimeline timeline, ConversionOptions options) => DmgWriter.Write(timeline, options);

        public RenderedAudio Render(VgmLog log, MusicalTimeline? timeline, double tickHz) => DmgSynth.Render(log, tickHz);
    }

    private sealed class SnFormat : IChipFormat
    {
        public SoundChip Chip => SoundChip.Sn76489;

        public MusicalTimeline Read(VgmLog log, ConversionOptions options) => SnPsg.Replay(log, options.TickHz);

        public ChipWriteResult Write(MusicalTimeline timeline, ConversionOptions options) => SnWriter.Write(timeline, options);

        public RenderedAudio Render(VgmLog log, MusicalTimeline? timeline, double tickHz) =>
            TimelineSynth.Render(timeline ?? SnPsg.Replay(log, tickHz));
    }
}
