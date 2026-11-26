using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SoulConverter.Cli;

/// <summary>
/// The <c>soulconverter</c> command: it parses arguments, reads and writes files and prints what
/// the library reports. Every conversion decision is the library's.
/// </summary>
public static class CommandLine
{
    private static readonly string[] SourceExtensions = [".vgm", ".vgz"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string Usage =
        """
        usage: soulconverter convert INPUT [-o OUT] [--to auto|nes|gb|sms] [--tick-hz N]
                                     [--volume-mode retrigger|envelope] [--no-transpose]
                                     [--dpcm off|safe|balanced|full] [--variants]
                                     [--dpcm-role ADDR=ROLE]... [--dpcm-dump DIR]
                                     [--report DIR] [--wav] [--spectrogram] [--quiet]
               soulconverter demo [-o OUT] [--song four-channel|drum-kit|sampled-bass]

        Converts VGM/VGZ logs between the NES 2A03 (nes), the Game Boy DMG (gb) and the
        SN76489 of the Master System and Game Gear (sms). INPUT is a file or a directory.
        """;

    /// <summary>Runs the command; returns 0 on success, 2 when some files of a batch failed, 1 when all did.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "convert":
                    return Convert(ConvertArguments.Parse(args[1..]), output, error);
                case "demo":
                    return Demo(args[1..], output);
                case "-h" or "--help":
                    output.WriteLine(Usage);
                    return 0;
                default:
                    throw new ArgumentException("expected a command: convert or demo");
            }
        }
        catch (ArgumentException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            error.WriteLine(Usage);
            return 1;
        }
    }

    private static int Demo(string[] args, TextWriter output)
    {
        var path = "demo.vgm";
        var song = "four-channel";
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-o" or "--out":
                    path = Value(args, ref index);
                    break;
                case "--song":
                    song = Value(args, ref index);
                    break;
                default:
                    throw new ArgumentException($"unknown option {args[index]}");
            }
        }

        var bytes = song switch
        {
            "four-channel" => DemoSongs.FourChannel(),
            "drum-kit" => DemoSongs.DrumKit(),
            "sampled-bass" => DemoSongs.SampledBass(),
            _ => throw new ArgumentException($"--song expects four-channel, drum-kit or sampled-bass, got '{song}'"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, bytes);
        output.WriteLine($"wrote {path}");
        return 0;
    }

    private static int Convert(ConvertArguments arguments, TextWriter output, TextWriter error)
    {
        var batch = Directory.Exists(arguments.Input);
        var sources = batch
            ? [.. Directory.EnumerateFiles(arguments.Input).Where(IsSource).Order(StringComparer.Ordinal)]
            : File.Exists(arguments.Input) ? new List<string> { arguments.Input } : [];
        if (sources.Count == 0)
        {
            error.WriteLine($"error: no VGM files found at {arguments.Input}");
            return 1;
        }

        if (batch && arguments.Out is not null)
        {
            Directory.CreateDirectory(arguments.Out);
        }

        if (arguments.Report is not null)
        {
            Directory.CreateDirectory(arguments.Report);
        }

        var failures = 0;
        foreach (var source in sources)
        {
            try
            {
                ConvertOne(source, arguments, batch, output);
            }
            // One log that fails must not stop the rest of a batch.
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures++;
                error.WriteLine($"error: {source}: {exception.Message}");
            }
        }

        return failures == sources.Count ? 1 : failures != 0 ? 2 : 0;
    }

    private static void ConvertOne(string source, ConvertArguments arguments, bool batch, TextWriter output)
    {
        var bytes = File.ReadAllBytes(source);
        var sourceChip = VgmConverter.DetectChip(bytes, source);
        var target = arguments.To ?? sourceChip switch
        {
            SoundChip.Nes2A03 => SoundChip.GameBoyDmg,
            SoundChip.GameBoyDmg => SoundChip.Nes2A03,
            _ => throw new ArgumentException("an SN76489 log has two possible targets; say which with --to nes or --to gb"),
        };
        var destination = OutputPath(source, arguments.Out, batch, target);
        var options = new ConversionOptions
        {
            TickHz = arguments.TickHz,
            VolumeMode = arguments.VolumeMode,
            TransposeOutOfRange = !arguments.NoTranspose,
            Dpcm = arguments.Dpcm,
            DpcmRoles = arguments.DpcmRoles,
            Analyze = !arguments.Quiet || arguments.Wav || arguments.Spectrogram || arguments.Report is not null,
            SourceName = Path.GetFullPath(source),
        };

        var variants = arguments.Variants && sourceChip == SoundChip.Nes2A03 && target == SoundChip.GameBoyDmg;
        var results = variants
            ? [.. Enum.GetValues<DpcmStrategy>().Select(strategy => VgmConverter.Convert(bytes, target, options with { Dpcm = strategy }))]
            : new List<ConversionResult> { VgmConverter.Convert(bytes, target, options) };

        foreach (var result in results)
        {
            var path = variants ? VariantPath(destination, result.Dpcm) : destination;
            Emit(result, path, arguments, output);
        }

        if (arguments.DpcmDump is not null)
        {
            DumpSamples(bytes, arguments.DpcmDump, options, arguments.Quiet, output);
        }

        if (variants && !arguments.Quiet)
        {
            output.WriteLine(ReportText.VariantTable(results));
            output.WriteLine();
        }
    }

    private static void Emit(ConversionResult result, string destination, ConvertArguments arguments, TextWriter output)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);

        // `.vgz` is not a different format, it is a gzipped VGM. Writing the plain bytes under
        // that name produces a file some players refuse to open.
        File.WriteAllBytes(destination, destination.EndsWith(".vgz", StringComparison.OrdinalIgnoreCase) ? Gzip(result.Vgm) : result.Vgm);
        result.Report["output"]!["path"] = Path.GetFullPath(destination);

        var stem = SidecarStem(destination);
        if (arguments.Wav && result.SourceAudio is not null && result.TargetAudio is not null)
        {
            File.WriteAllBytes(Path.Combine(directory, $"{stem}.{Suffix(result.Source)}.wav"), WavFile.Encode(result.SourceAudio));
            File.WriteAllBytes(Path.Combine(directory, $"{stem}.{Suffix(result.Target)}.wav"), WavFile.Encode(result.TargetAudio));
        }

        if (arguments.Spectrogram && result.SourceAudio is not null && result.TargetAudio is not null)
        {
            File.WriteAllBytes(Path.Combine(directory, $"{stem}.compare.png"), SpectrogramImage.EncodePng(result.SourceAudio, result.TargetAudio));
        }

        if (arguments.Report is not null)
        {
            File.WriteAllText(Path.Combine(arguments.Report, $"{stem}.json"), result.Report.ToJsonString(JsonOptions));
        }

        if (!arguments.Quiet)
        {
            output.WriteLine(result.Summary);
            output.WriteLine();
        }
    }

    // One WAV per distinct sample, so the classification can be checked by ear. The rate is part
    // of the name: the same bytes played slower are a different sound.
    private static void DumpSamples(byte[] source, string directory, ConversionOptions options, bool quiet, TextWriter output)
    {
        Directory.CreateDirectory(directory);
        foreach (var sample in VgmConverter.AnalyzeDpcm(source, options))
        {
            var path = Path.Combine(directory, $"{sample.Address:X4}_{sample.Length}_r{sample.RateIndex}_{sample.Role.ToName()}.wav");
            File.WriteAllBytes(path, WavFile.Encode([.. sample.Waveform], (int)sample.RateHz));
            if (!quiet)
            {
                output.WriteLine($"wrote {path}");
            }
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal))
        {
            gzip.Write(data);
        }

        return buffer.ToArray();
    }

    private static bool IsSource(string path) => SourceExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static string Suffix(SoundChip chip) => chip switch
    {
        SoundChip.Nes2A03 => "nes",
        SoundChip.GameBoyDmg => "gb",
        _ => "sms",
    };

    private static string OutputPath(string source, string? @out, bool batch, SoundChip target)
    {
        var stem = Path.GetFileName(source);
        var extension = SourceExtensions.FirstOrDefault(candidate => stem.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
        stem = extension is null ? stem : stem[..^extension.Length];
        var name = $"{stem}.{Suffix(target)}.vgm";
        if (@out is null)
        {
            return Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, name);
        }

        return batch || Directory.Exists(@out) ? Path.Combine(@out, name) : @out;
    }

    private static string VariantPath(string destination, DpcmStrategy strategy)
    {
        var name = Path.GetFileName(destination);
        var extension = name.EndsWith(".vgz", StringComparison.OrdinalIgnoreCase) ? ".vgz"
            : name.EndsWith(".gb.vgm", StringComparison.OrdinalIgnoreCase) ? ".gb.vgm"
            : Path.GetExtension(name) is { Length: > 0 } given ? given.ToLowerInvariant() : ".gb.vgm";
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, $"{SidecarStem(destination)}.{strategy.ToString().ToLowerInvariant()}{extension}");
    }

    // The base name of the WAV, PNG and JSON companions, without a target suffix.
    private static string SidecarStem(string destination)
    {
        var name = Path.GetFileName(destination);
        foreach (var chip in new[] { "gb", "nes", "sms" })
        {
            foreach (var extension in SourceExtensions)
            {
                if (name.EndsWith($".{chip}{extension}", StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^(chip.Length + extension.Length + 1)];
                }
            }
        }

        return Path.GetFileNameWithoutExtension(name);
    }

    private static string Value(string[] args, ref int index) =>
        index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{args[index]} expects a value");

    private sealed class ConvertArguments
    {
        public string Input { get; private set; } = string.Empty;

        public string? Out { get; private set; }

        public SoundChip? To { get; private set; }

        public double TickHz { get; private set; } = 60.0;

        public VolumeMode VolumeMode { get; private set; } = VolumeMode.Retrigger;

        public bool NoTranspose { get; private set; }

        public DpcmStrategy Dpcm { get; private set; } = DpcmStrategy.Off;

        public bool Variants { get; private set; }

        public Dictionary<int, DpcmRole> DpcmRoles { get; } = [];

        public string? DpcmDump { get; private set; }

        public string? Report { get; private set; }

        public bool Wav { get; private set; }

        public bool Spectrogram { get; private set; }

        public bool Quiet { get; private set; }

        public static ConvertArguments Parse(string[] args)
        {
            var parsed = new ConvertArguments();
            string? input = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "-o" or "--out":
                        parsed.Out = Value(args, ref index);
                        break;
                    case "--to":
                        parsed.To = Value(args, ref index) switch
                        {
                            "auto" => null,
                            "nes" => SoundChip.Nes2A03,
                            "gb" => SoundChip.GameBoyDmg,
                            "sms" or "gg" => SoundChip.Sn76489,
                            var other => throw new ArgumentException($"--to expects auto, nes, gb or sms, got '{other}'"),
                        };
                        break;
                    case "--tick-hz":
                        var text = Value(args, ref index);
                        parsed.TickHz = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var tickHz) && tickHz >= ConversionOptions.MinTickHz && tickHz <= ConversionOptions.MaxTickHz
                            ? tickHz
                            : throw new ArgumentException(string.Create(
                                CultureInfo.InvariantCulture,
                                $"--tick-hz expects a number from {ConversionOptions.MinTickHz} to {ConversionOptions.MaxTickHz}, got '{text}'"));
                        break;
                    case "--volume-mode":
                        parsed.VolumeMode = Value(args, ref index) switch
                        {
                            "retrigger" => VolumeMode.Retrigger,
                            "envelope" => VolumeMode.Envelope,
                            var other => throw new ArgumentException($"--volume-mode expects retrigger or envelope, got '{other}'"),
                        };
                        break;
                    case "--no-transpose":
                        parsed.NoTranspose = true;
                        break;
                    case "--dpcm":
                        var strategy = Value(args, ref index);
                        parsed.Dpcm = Enum.GetValues<DpcmStrategy>().Cast<DpcmStrategy?>().FirstOrDefault(candidate => candidate.ToString()!.ToLowerInvariant() == strategy)
                            ?? throw new ArgumentException($"--dpcm expects off, safe, balanced or full, got '{strategy}'");
                        break;
                    case "--variants":
                        parsed.Variants = true;
                        break;
                    case "--dpcm-role":
                        var (address, role) = ParseRole(Value(args, ref index));
                        parsed.DpcmRoles[address] = role;
                        break;
                    case "--dpcm-dump":
                        parsed.DpcmDump = Value(args, ref index);
                        break;
                    case "--report":
                        parsed.Report = Value(args, ref index);
                        break;
                    case "--wav":
                        parsed.Wav = true;
                        break;
                    case "--spectrogram":
                        parsed.Spectrogram = true;
                        break;
                    case "--quiet":
                        parsed.Quiet = true;
                        break;
                    case var option when option.StartsWith('-'):
                        throw new ArgumentException($"unknown option {option}");
                    case var positional when input is null:
                        input = positional;
                        break;
                    default:
                        throw new ArgumentException($"unexpected argument {args[index]}");
                }
            }

            parsed.Input = input ?? throw new ArgumentException("convert expects an input file or directory");
            return parsed;
        }

        // Parses `0xEA80=kick`.
        private static (int Address, DpcmRole Role) ParseRole(string entry)
        {
            var parts = entry.Split('=', 2);
            var number = parts[0].Trim();
            var hexadecimal = number.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (parts.Length == 2
                && int.TryParse(hexadecimal ? number[2..] : number, hexadecimal ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var address)
                && DpcmRoleExtensions.TryParse(parts[1], out var role))
            {
                return (address, role);
            }

            throw new ArgumentException($"--dpcm-role '{entry}': expected ADDR=ROLE with an address and one of {string.Join(", ", DpcmRoleExtensions.AllNames)}");
        }
    }
}
