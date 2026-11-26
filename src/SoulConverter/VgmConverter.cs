using System.Globalization;
using System.Text.Json.Nodes;
using SoulConverter.Analysis;
using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter;

/// <summary>
/// Converts VGM music and sound-effect logs between the NES 2A03, the Game Boy DMG and the
/// SN76489, any of them to any other. Everything happens in memory: bytes in, bytes and warnings
/// out, nothing printed and no files touched.
/// </summary>
public static class VgmConverter
{
    private const double EnvelopeWarnCorrelation = 0.8;
    private const double ActivityWarnAgreement = 0.9;

    /// <summary>
    /// Converts <paramref name="source"/>, a VGM or gzipped VGZ log of one of the supported chips
    /// (found from its header), into a log for <paramref name="target"/>. A source that already
    /// drives <paramref name="target"/> is returned as it is.
    /// </summary>
    /// <exception cref="VgmFormatException">The source is not a readable VGM log.</exception>
    /// <exception cref="UnsupportedChipException">The source drives a chip that is not supported.</exception>
    public static ConversionResult Convert(ReadOnlyMemory<byte> source, SoundChip target, ConversionOptions? options = null)
    {
        options ??= ConversionOptions.Default;
        if (!(options.TickHz >= ConversionOptions.MinTickHz && options.TickHz <= ConversionOptions.MaxTickHz))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.TickHz,
                string.Create(CultureInfo.InvariantCulture, $"The tick rate must be between {ConversionOptions.MinTickHz} and {ConversionOptions.MaxTickHz} Hz."));
        }

        var name = options.SourceName ?? "<memory>";
        var data = VgmLogReader.Decompress(source.Span);
        var log = VgmLogReader.Read(data, name);
        var reader = ChipFormats.For(log.Chip);
        var timeline = reader.Read(log, options);

        if (log.Chip == target)
        {
            return SameChip(data, log, timeline, options, name);
        }

        var writer = ChipFormats.For(target);
        var written = writer.Write(timeline, options);
        var warnings = new List<ConversionWarning>();
        if (written.Silent)
        {
            warnings.Add(new ConversionWarning(
                WarningCodes.Silent,
                $"the converted log never sounds: nothing the {log.Chip.DisplayName()} log plays can play on the {target.DisplayName()}"));
        }

        warnings.AddRange(written.Warnings);

        var report = new JsonObject
        {
            ["source"] = SourceSection(log, name),
            ["output"] = OutputSection(written, target),
            ["settings"] = new JsonObject
            {
                ["tick_hz"] = options.TickHz,
                ["volume_mode"] = options.VolumeMode == VolumeMode.Envelope ? "envelope" : "retrigger",
                ["transpose_out_of_range"] = options.TransposeOutOfRange,
                ["dpcm"] = Dmg.Preset.For(options.Dpcm).Name,
            },
        };
        foreach (var (key, section) in written.Sections.ToList())
        {
            written.Sections.Remove(key);
            report[key] = section;
        }

        double[]? sourceAudio = null, targetAudio = null;
        var metrics = new JsonObject();
        if (options.Analyze)
        {
            var sourceRender = reader.Render(log, timeline.OnGrid ? timeline : null, options.TickHz);
            var targetRender = writer.Render(VgmLogReader.Read(written.Vgm, "<converted>"), null, options.TickHz);
            sourceAudio = sourceRender.Samples;
            targetAudio = targetRender.Samples;
            metrics = Metrics.Compare(sourceRender, targetRender, "source", "target");
            AddMetricWarnings(warnings, metrics);
        }

        report["metrics"] = metrics;
        var warningArray = new JsonArray();
        foreach (var warning in warnings)
        {
            warningArray.Add(warning.Message);
        }

        report["warnings"] = warningArray;
        return new ConversionResult(
            written.Vgm, log.Chip, target, warnings, written.Silent, report, ReportText.Render(report), sourceAudio, targetAudio, options.Dpcm);
    }

    /// <summary>Converts a log read from <paramref name="source"/> to the end.</summary>
    /// <exception cref="VgmFormatException">The source is not a readable VGM log, or is too large.</exception>
    public static ConversionResult Convert(Stream source, SoundChip target, ConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Convert(VgmLogReader.ReadAtMost(source), target, options);
    }

    /// <summary>The chip a VGM or gzipped VGZ log drives, from its header.</summary>
    /// <exception cref="VgmFormatException">The source is not a readable VGM log.</exception>
    /// <exception cref="UnsupportedChipException">The source drives a chip that is not supported.</exception>
    public static SoundChip DetectChip(ReadOnlyMemory<byte> source, string? sourceName = null) =>
        VgmLogReader.DetectChip(VgmLogReader.Decompress(source.Span), sourceName ?? "<memory>");

    /// <summary>
    /// The distinct DPCM samples a NES 2A03 log plays, decoded, with the role each would be played
    /// as on a Game Boy, to judge the classification by ear.
    /// </summary>
    public static IReadOnlyList<DpcmSample> AnalyzeDpcm(ReadOnlyMemory<byte> source, ConversionOptions? options = null)
    {
        options ??= ConversionOptions.Default;
        var log = VgmLogReader.Read(VgmLogReader.Decompress(source.Span), options.SourceName ?? "<memory>");
        if (log.Chip != SoundChip.Nes2A03)
        {
            return [];
        }

        var timeline = ChipFormats.For(log.Chip).Read(log, options);
        var refs = timeline.Frames.Where(frame => frame.Dmc is { Started: true, Sample: not null }).Select(frame => frame.Dmc.Sample!);
        return
        [
            .. Dmg.Dpcm.AnalyzeAll(log.DpcmRom, refs, options.DpcmRoles).Select(entry => new DpcmSample(
                entry.Key.Address,
                entry.Key.Length,
                entry.Key.RateIndex,
                entry.Value.Decoded.RateHz,
                entry.Value.Role,
                entry.Value.Confidence,
                [.. entry.Value.Decoded.Waveform.Select(value => (double)value)])),
        ];
    }

    private static ConversionResult SameChip(byte[] data, VgmLog log, MusicalTimeline timeline, ConversionOptions options, string name)
    {
        var silent = !timeline.Frames.Any(frame => frame.AnyVoicePlaying || frame.Dmc.Started);
        var warnings = new List<ConversionWarning>();
        if (silent)
        {
            warnings.Add(new ConversionWarning(WarningCodes.Silent, $"the {log.Chip.DisplayName()} log never sounds"));
        }

        var warningArray = new JsonArray();
        foreach (var warning in warnings)
        {
            warningArray.Add(warning.Message);
        }

        var report = new JsonObject
        {
            ["source"] = SourceSection(log, name),
            ["output"] = new JsonObject
            {
                ["system"] = log.Chip.DisplayName(),
                ["bytes"] = data.Length,
                ["duration_seconds"] = Math.Round(log.DurationSeconds, 3),
                ["register_writes"] = log.Writes.Count,
                ["commands"] = Commands(data),
                ["unchanged"] = true,
            },
            ["settings"] = new JsonObject { ["tick_hz"] = options.TickHz },
            ["metrics"] = new JsonObject(),
            ["warnings"] = warningArray,
        };
        return new ConversionResult(data, log.Chip, log.Chip, warnings, silent, report, ReportText.Render(report), null, null, options.Dpcm);
    }

    private static JsonObject SourceSection(VgmLog log, string name) => new()
    {
        ["name"] = name,
        ["system"] = log.Chip.DisplayName(),
        ["clock_hz"] = log.ClockHz,
        ["vgm_version"] = log.VersionText,
        ["total_samples"] = log.TotalSamples,
        ["duration_seconds"] = Math.Round(log.DurationSeconds, 3),
        ["loop_sample"] = log.LoopSample,
        ["register_writes"] = log.Writes.Count,
    };

    private static JsonObject OutputSection(ChipWriteResult written, SoundChip target)
    {
        var output = new JsonObject
        {
            ["system"] = target.DisplayName(),
            ["bytes"] = written.Vgm.Length,
            ["duration_seconds"] = Math.Round(BitConverter.ToUInt32(written.Vgm, 0x18) / (double)VgmLog.SampleRate, 3),
            ["register_writes"] = written.RegisterWrites,
            ["commands"] = Commands(written.Vgm),
        };
        foreach (var (key, value) in written.Output.ToList())
        {
            written.Output.Remove(key);
            output[key] = value;
        }

        return output;
    }

    // The VGM command bytes a log uses, e.g. to check it stays inside what an importer accepts.
    private static JsonArray Commands(byte[] vgm)
    {
        var offset = (int)BitConverter.ToUInt32(vgm, 0x34);
        offset = offset == 0 ? 0x40 : offset + 0x34;
        var commands = new SortedSet<int>();
        while (offset < vgm.Length)
        {
            var command = vgm[offset];
            commands.Add(command);
            if (command == 0x66)
            {
                break;
            }

            offset += command switch
            {
                0xB3 or 0xB4 or 0x61 => 3,
                0x50 or 0x4F => 2,
                0x67 => 7 + (int)BitConverter.ToUInt32(vgm, offset + 3),
                0xE0 => 5,
                _ => 1,
            };
        }

        var array = new JsonArray();
        foreach (var command in commands)
        {
            array.Add($"0x{command:X2}");
        }

        return array;
    }

    private static void AddMetricWarnings(List<ConversionWarning> warnings, JsonObject metrics)
    {
        var envelope = metrics["envelope_correlation"]!.GetValue<double>();
        if (envelope < EnvelopeWarnCorrelation)
        {
            warnings.Add(new ConversionWarning(
                WarningCodes.Metrics,
                string.Create(CultureInfo.InvariantCulture, $"envelope correlation is {envelope:0.00}; the loudness shape of the conversion differs noticeably from the source.")));
        }

        foreach (var (name, channel) in metrics["channels"]!.AsObject())
        {
            var agreement = channel!["activity_agreement"]!.GetValue<double>();
            if (agreement < ActivityWarnAgreement)
            {
                warnings.Add(new ConversionWarning(
                    WarningCodes.Metrics,
                    string.Create(CultureInfo.InvariantCulture, $"{name}: channel on/off agreement is {agreement:0.00}; notes start or stop at different times.")));
            }
        }
    }
}

/// <summary>One distinct DPCM sample of a NES 2A03 log, decoded.</summary>
/// <param name="Address">Where the sample lives in the DMC's address space ($C000-$FFFF).</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="RateIndex">The DMC rate it is played at (0-15); the same bytes played slower are a different sound.</param>
/// <param name="RateHz">That rate in output bits per second, the sample rate of <paramref name="Waveform"/>.</param>
/// <param name="Role">What it is played as on a Game Boy.</param>
/// <param name="Confidence">How sure the classifier is, 0-1; 1 for a role forced by hand.</param>
/// <param name="Waveform">The decoded samples, in -1..1.</param>
public sealed record DpcmSample(int Address, int Length, int RateIndex, double RateHz, DpcmRole Role, double Confidence, IReadOnlyList<double> Waveform);
