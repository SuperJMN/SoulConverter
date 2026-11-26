using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SoulConverter;

/// <summary>Renders conversion reports as text for a person.</summary>
public static class ReportText
{
    /// <summary>A human-readable rendering of a <see cref="ConversionResult.Report"/>.</summary>
    public static string Render(JsonObject report)
    {
        var source = report["source"]!.AsObject();
        var output = report["output"]!.AsObject();
        var settings = report["settings"]!.AsObject();
        var lines = new List<string>
        {
            $"source : {Text(source["name"])}",
            Line($"         VGM {Text(source["vgm_version"])}, {Text(source["system"])} @ {Int(source["clock_hz"])} Hz, {Number(source["duration_seconds"]):0.00}s, {Int(source["register_writes"])} writes")
                + (source["loop_sample"] is { } loop && loop.GetValue<int>() != 0 ? $", loop @ sample {loop.GetValue<int>()}" : string.Empty),
            Line($"output : {Text(output["system"])}, {Int(output["bytes"])} bytes, {Number(output["duration_seconds"]):0.00}s, {Int(output["register_writes"])} writes, commands {string.Join(' ', output["commands"]!.AsArray().Select(Text))}"),
        };

        if (output["unchanged"] is not null)
        {
            lines.Add("         the source already drives the target chip; it is returned unchanged");
        }

        var settingsLine = Line($"settings: tick {Number(settings["tick_hz"]):0.0###} Hz");
        if (settings["volume_mode"] is not null && report["channels"] is not null)
        {
            settingsLine += $", volume mode {Text(settings["volume_mode"])}, transpose {(settings["transpose_out_of_range"]!.GetValue<bool>() ? "on" : "off")}";
        }

        lines.Add(settingsLine);

        var metrics = report["metrics"] as JsonObject is { Count: > 0 } measured ? measured : null;
        if (report["channels"] is JsonObject channels)
        {
            lines.Add(string.Empty);
            lines.Add($"{"channel",-10} {"active",7} {"notes",7} {"retrig",7} {"transp",7} {"cents",8} {"on/off",7}");
            foreach (var (name, channel) in channels)
            {
                var stats = metrics?["channels"]?[name];
                var cents = stats is null ? new string(' ', 8) : Line($"{Number(stats["mean_abs_cents_mod_octave"]),8:0.0}");
                var agreement = stats is null ? new string(' ', 7) : Line($"{Number(stats["activity_agreement"]),7:0.00}");
                lines.Add($"{name,-10} {Int(channel!["active_ticks"]),7} {Int(channel["note_ons"]),7} {Int(channel["retriggers"]),7} {Int(channel["transposed_ticks"]),7} {cents} {agreement}");
            }
        }

        if (report["mapping"] is JsonObject mapping)
        {
            lines.Add(string.Empty);
            lines.Add($"mapping: {Text(mapping["pulse1"])}; {Text(mapping["pulse2"])}");
            lines.Add($"         {Text(mapping["wave"])}; {Text(mapping["noise"])}");
            lines.Add($"         volume updates {Int(mapping["volume_updates"])}, period updates {Int(mapping["pulse_period_updates"]) + Int(mapping["wave_period_updates"]) + Int(mapping["noise_period_updates"])}");
        }

        if (metrics is not null)
        {
            lines.Add(string.Empty);
            lines.Add(Line($"envelope correlation : {Number(metrics["envelope_correlation"]):0.000}"));
            lines.Add(Line($"spectral correlation : {Number(metrics["spectral_correlation"]):0.000}"));
            lines.Add(Line($"rms ratio (out/in)   : {Number(metrics["rms_ratio"]):0.000}"));
            var onsets = metrics["onsets"]!;
            if (Int(onsets["reference"]) != 0)
            {
                lines.Add(Line($"percussive attacks   : {Int(onsets["matched"])}/{Int(onsets["reference"])} kept ({Number(onsets["recall"]):0.00})"));
            }
        }

        if (report["dpcm"] is JsonObject dpcm && (Int(dpcm["rom_bytes"]) != 0 || Int(dpcm["hits"]) != 0))
        {
            lines.Add(string.Empty);
            lines.Add($"DPCM ({Text(dpcm["preset"])}): {Text(dpcm["strategy"])}");
            if (Int(dpcm["hits"]) != 0)
            {
                var layered = Int(dpcm["layers"]) != 0 ? $" (+{Int(dpcm["layers"])} layered)" : string.Empty;
                var hosts = dpcm["per_host"]!.AsObject();
                var hostText = hosts.Count == 0 ? "-" : string.Join(", ", hosts.Select(entry => $"{entry.Key} {Int(entry.Value)}"));
                lines.Add($"  {Int(dpcm["placed"])}/{Int(dpcm["hits"])} hits re-synthesised{layered}; hosts {hostText}");
                var preempted = dpcm["preempted_frames"]!.AsObject();
                if (preempted.Count != 0)
                {
                    lines.Add($"  frames taken from the original arrangement: {string.Join(", ", preempted.Select(entry => $"{entry.Key} {Int(entry.Value)}"))}");
                }
            }

            foreach (var sample in dpcm["samples"]!.AsArray())
            {
                var flag = sample!["overridden"]!.GetValue<bool>() ? " (forced)" : string.Empty;
                lines.Add(Line($"  {Text(sample["address"])} {Int(sample["length"]),4}B {Number(sample["duration_ms"]),6:0.0}ms x{Int(sample["hits"]),-3} -> {Text(sample["role"]),-8}{flag} conf {Number(sample["confidence"]):0.00}"));
                var why = sample["why"]!.AsArray();
                lines.Add($"      why: {(why.Count == 0 ? "no rule matched" : string.Join(", ", why.Select(Text)))}");
                if (sample["recipe"] is { } recipe)
                {
                    lines.Add($"      plays: {Text(recipe)} on {string.Join(", ", sample["hosts"]!.AsArray().Select(Text))}");
                }
            }
        }

        if (report["noise_mapping_cents"] is JsonObject { Count: > 0 } noiseMapping)
        {
            lines.Add(string.Empty);
            lines.Add($"noise period mapping : {string.Join(", ", noiseMapping.Select(entry => Line($"{entry.Key}:{Number(entry.Value):+0;-0}c")))}");
        }

        var warnings = report["warnings"]!.AsArray();
        if (warnings.Count != 0)
        {
            lines.Add(string.Empty);
            lines.Add("warnings:");
            lines.AddRange(warnings.Select(warning => $"  - {Text(warning)}"));
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Compares the DPCM strategies side by side. Deliberately not ranked: the numbers say how
    /// closely each variant resembles a chip the Game Boy is not; which one is the better piece of
    /// music is for the listener.
    /// </summary>
    public static string VariantTable(IReadOnlyList<ConversionResult> results)
    {
        var text = new StringBuilder();
        text.Append("DPCM strategies (listen before choosing; the numbers only measure\n");
        text.Append("resemblance to a different chip, not whether it sounds good)\n\n");
        text.Append($"{"preset",9} {"bytes",8} {"hits",9} {"attacks",9} {"envelope",9} {"spectral",9}");
        foreach (var result in results)
        {
            var dpcm = result.Report["dpcm"] as JsonObject;
            var metrics = result.Report["metrics"] as JsonObject is { Count: > 0 } measured ? measured : null;
            var hits = dpcm is not null && Int(dpcm["hits"]) != 0 ? $"{Int(dpcm["placed"])}/{Int(dpcm["hits"])}" : "-";
            var attacks = metrics is not null && Int(metrics["onsets"]!["reference"]) != 0 ? Line($"{Number(metrics["onsets"]!["recall"]):0.000}") : "-";
            var envelope = metrics is null ? 0.0 : Number(metrics["envelope_correlation"]);
            var spectral = metrics is null ? 0.0 : Number(metrics["spectral_correlation"]);
            text.Append('\n');
            text.Append(Line($"{Text(result.Report["settings"]!["dpcm"]),9} {result.Vgm.Length,8} {hits,9} {attacks,9} {envelope,9:0.000} {spectral,9:0.000}"));
            text.Append('\n');
            text.Append($"{string.Empty,9} {Text(dpcm?["strategy"])}");
        }

        return text.ToString();
    }

    private static string Line(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? string.Empty;

    private static int Int(JsonNode? node) => node?.GetValue<int>() ?? 0;

    private static double Number(JsonNode? node) => node is null ? 0.0 : node.GetValueKind() == System.Text.Json.JsonValueKind.Number ? node.GetValue<double>() : 0.0;
}
