using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Dmg;

/// <summary>A named trade-off; <see cref="Boldness"/> scales the benefit before it is compared with the cost.</summary>
internal sealed record Preset(
    DpcmStrategy Strategy,
    string Name,
    bool Enabled,
    bool AllowTonalHosts,
    bool AllowLayers,
    bool AllowMelodicDpcm,
    double Boldness,
    string Description)
{
    public static readonly IReadOnlyList<Preset> All =
    [
        new(DpcmStrategy.Off, "off", false, false, false, false, 0.0, "drop the DPCM channel entirely"),
        new(DpcmStrategy.Safe, "safe", true, false, false, false, 1.0, "percussion on the noise channel only, melody never touched"),
        new(DpcmStrategy.Balanced, "balanced", true, true, false, true, 1.0, "drums and sampled bass on a tonal channel where one is free enough"),
        new(DpcmStrategy.Full, "full", true, true, true, true, 1.8, "everything: layered drums and melodic DPCM, borrowing freely"),
    ];

    public static Preset For(DpcmStrategy strategy) => All.First(preset => preset.Strategy == strategy);
}

/// <summary>One DPCM event placed on a host channel for a span of ticks.</summary>
internal sealed record ScheduledVoice(int Tick, int Frames, Host Host, Recipe Recipe, SampleAnalysis Analysis, double Cost, double Benefit, int? LayerOf = null)
{
    public int End => Tick + Frames;
}

internal sealed class AllocationStats
{
    public int Hits;
    public int Placed;
    public int DroppedArtifact;
    public int DroppedTooCostly;
    public int DroppedNoHost;
    public int Layers;

    public OrderedDictionary<string, int> PerRole { get; } = [];

    public OrderedDictionary<string, int> PerHost { get; } = [];

    public OrderedDictionary<string, int> PreemptedFrames { get; } = [];

    public double RealisedFraction
    {
        get
        {
            var playable = Hits - DroppedArtifact;
            return playable != 0 ? Placed / (double)playable : 0.0;
        }
    }
}

/// <summary>Everything the DMG writer needs to weave the fifth voice in.</summary>
internal sealed class AllocationPlan(Preset preset)
{
    private static readonly Host[] Hosts = [Host.Ch1, Host.Ch2, Host.Ch3, Host.Ch4];
    private readonly Dictionary<Host, HashSet<int>> busy = [];
    private readonly Dictionary<int, List<ScheduledVoice>> starts = [];
    private readonly Dictionary<int, List<Host>> releases = [];

    public Preset Preset { get; } = preset;

    public List<ScheduledVoice> Voices { get; } = [];

    public OrderedDictionary<SampleRef, SampleAnalysis> Analyses { get; set; } = [];

    public OrderedDictionary<SampleRef, int> HitCounts { get; } = [];

    public AllocationStats Stats { get; } = new();

    /// <summary>True when the NES part may not use <paramref name="host"/> on this tick.</summary>
    public bool IsBorrowed(Host host, int tick) => busy.TryGetValue(host, out var ticks) && ticks.Contains(tick);

    public IReadOnlyList<ScheduledVoice> StartingAt(int tick) => starts.TryGetValue(tick, out var voices) ? voices : [];

    /// <summary>The channels handed back on this tick, in channel order.</summary>
    public IReadOnlyList<Host> ReleasedAt(int tick) => releases.TryGetValue(tick, out var hosts) ? hosts : [];

    /// <summary>Rebuilds the tick-indexed views after the voice list is final.</summary>
    public void Index()
    {
        busy.Clear();
        starts.Clear();
        releases.Clear();
        foreach (var host in Hosts)
        {
            busy[host] = [];
        }

        foreach (var voice in Voices)
        {
            for (var tick = voice.Tick; tick < voice.End; tick++)
            {
                busy[voice.Host].Add(tick);
            }

            if (!starts.TryGetValue(voice.Tick, out var list))
            {
                starts[voice.Tick] = list = [];
            }

            list.Add(voice);
        }

        foreach (var host in Hosts)
        {
            foreach (var tick in busy[host])
            {
                if (!busy[host].Contains(tick + 1))
                {
                    if (!releases.TryGetValue(tick + 1, out var list))
                    {
                        releases[tick + 1] = list = [];
                    }

                    list.Add(host);
                }
            }
        }
    }
}

/// <summary>
/// Decides which Game Boy channel each DPCM voice borrows, and gives it back. The Game Boy has
/// four generators and the NES was already using all four, so a fifth voice can only exist by
/// taking someone's turn. This prices that theft, and the price is perceptual: silencing a noise
/// channel already playing a 16 ms hat under a drum hit costs almost nothing, because the hit
/// masks it; silencing a sustained bass note costs a great deal.
///
/// Two invariants hold throughout. Every borrowed channel is handed back: it is never left muted
/// or playing the drum's settings. And the DMC was monophonic, so a voice is truncated when the
/// next sample starts.
/// </summary>
internal static class Allocator
{
    // What it costs, per frame of full-volume playing, to silence each channel. The wave channel is
    // dear because it usually carries the bass line and because restoring CH3 means reloading wave
    // RAM, which clicks. Noise is not cheap by default: what makes it affordable is that it nearly
    // always plays decoration, which is priced separately below.
    private static double ChannelWeight(Host host) => host == Host.Ch3 ? 1.4 : 1.0;

    // Cutting a note that starts inside the borrowed span loses the whole note, not just its tail.
    private const double AttackPenalty = 1.6;

    // A noise burst this short is decoration, and on the NES it almost always sits on top of a
    // DPCM hit, which masks it. This is the single measurement that makes drums fit at all.
    private const int DecorativeNoiseFrames = 2;
    private const double DecorativeNoiseDiscount = 0.1;

    // A noise run that ends this close to its own peak was held on purpose; one that ends quieter
    // was struck and left to fade, which is percussion.
    private const double SustainedNoiseRatio = 0.8;

    // How much dearer than the cheapest option the best-sounding host may be before cost overrules
    // timbre.
    private const double PreferenceTolerance = 0.15;

    // How close two voices must be, in semitones, before the new one is heard as fighting the old.
    private const double ClashSemitones = 12.0;

    // What a full clash costs per frame: more than the preference tolerance, less than the price of
    // interrupting a real note.
    private const double ClashWeight = 0.6;

    // How far either side of a hit the register still counts as occupied: about 66 ms, the span
    // over which hearing groups events close in time and pitch into one stream.
    private const int ClashContextFrames = 4;

    /// <summary>
    /// Works out what the fifth voice plays, and at whose expense. Voices are placed in the order
    /// they occur: the DMC was monophonic and each hit is independent, so a greedy pass over time
    /// gives the same answer as a search would, and stays explainable.
    /// </summary>
    public static AllocationPlan Plan(MusicalTimeline timeline, DpcmRom rom, Preset preset, IReadOnlyDictionary<int, DpcmRole>? overrides)
    {
        var result = new AllocationPlan(preset);
        var hits = new List<(int Tick, SampleRef Sample)>();
        for (var index = 0; index < timeline.Frames.Count; index++)
        {
            if (timeline.Frames[index].Dmc is { Started: true, Sample: { } sample })
            {
                hits.Add((index, sample));
                result.HitCounts[sample] = result.HitCounts.GetValueOrDefault(sample) + 1;
            }
        }

        result.Stats.Hits = hits.Count;

        // Even with the fifth voice switched off the report says what is thrown away and what it
        // was: exactly what someone needs to decide whether to switch it on.
        if (hits.Count != 0)
        {
            result.Analyses = Dpcm.AnalyzeAll(rom, hits.Select(hit => hit.Sample), overrides);
        }

        if (!preset.Enabled || hits.Count == 0)
        {
            result.Index();
            return result;
        }

        var occupancy = Occupancy.Measure(timeline);
        var totalTicks = timeline.Frames.Count;
        for (var position = 0; position < hits.Count; position++)
        {
            var (tick, sample) = hits[position];
            var analysis = result.Analyses[sample];
            if (analysis.Role == DpcmRole.Artifact)
            {
                result.Stats.DroppedArtifact++;
                continue;
            }

            if (analysis.Role == DpcmRole.Tonal && !preset.AllowMelodicDpcm)
            {
                result.Stats.DroppedTooCostly++;
                continue;
            }

            // The DMC could only play one sample at a time: the next hit cuts this one short, and
            // so must its stand-in. A sample that runs out before the next hit stops there too,
            // unless it was set to loop: holding a note through a gap the original left silent
            // invents a sustain.
            var nextTick = position + 1 < hits.Count ? hits[position + 1].Tick : totalTicks;
            var room = Math.Max(1, Math.Min(nextTick, totalTicks) - tick);
            if (!timeline.Frames[tick].Dmc.Looping)
            {
                var sampleFrames = analysis.Decoded.DurationSeconds * timeline.TickHz;
                room = Math.Max(1, Math.Min(room, (int)Math.Round(sampleFrames)));
            }

            var options = Percussion.Candidates(analysis, timeline.TickHz, room / timeline.TickHz, preset.AllowTonalHosts, preset.AllowLayers);
            if (options.Count == 0)
            {
                result.Stats.DroppedNoHost++;
                continue;
            }

            var benefit = Percussion.RoleBenefit(analysis) * preset.Boldness;
            var placed = Place(result, options, tick, room, occupancy, benefit, analysis);
            if (placed is null)
            {
                result.Stats.DroppedTooCostly++;
                continue;
            }

            result.Stats.Placed++;
            var roleName = analysis.Role.ToName();
            result.Stats.PerRole[roleName] = result.Stats.PerRole.GetValueOrDefault(roleName) + 1;

            if (preset.AllowLayers)
            {
                AddLayer(result, options, placed, tick, room, occupancy, benefit, analysis);
            }
        }

        result.Index();
        foreach (var voice in result.Voices)
        {
            var name = voice.Host.Name();
            result.Stats.PerHost[name] = result.Stats.PerHost.GetValueOrDefault(name) + 1;
            var stolen = (int)Math.Round(occupancy.Cost(voice.Host, voice.Tick, voice.Frames));
            if (stolen != 0)
            {
                result.Stats.PreemptedFrames[name] = result.Stats.PreemptedFrames.GetValueOrDefault(name) + stolen;
            }
        }

        return result;
    }

    // Takes the best-sounding host the voice can afford, unless another is much cheaper. The
    // candidate list is in musical preference order, but taking the first affordable entry means
    // never looking at a channel sitting idle next to the one being interrupted: a sustained bass
    // note would evict the wave channel for the whole song while a square stayed silent.
    // Preference still decides between hosts that cost about the same.
    private static ScheduledVoice? Place(AllocationPlan result, List<Recipe> options, int tick, int room, Occupancy occupancy, double benefit, SampleAnalysis analysis)
    {
        var priced = new List<(Recipe Recipe, int Frames, double Cost)>();
        foreach (var recipe in options)
        {
            var frames = Math.Max(1, Math.Min(recipe.Frames, room));
            if (!Conflicts(result, recipe.Host, tick, frames))
            {
                priced.Add((recipe, frames, occupancy.Rate(recipe.Host, tick, frames) + occupancy.Clash(recipe.PitchHz, recipe.Host, tick, frames)));
            }
        }

        if (priced.Count == 0)
        {
            return null;
        }

        var cheapest = priced.Min(item => item.Cost);
        foreach (var (recipe, frames, cost) in priced)
        {
            if (cost < benefit && cost <= cheapest + PreferenceTolerance)
            {
                var voice = new ScheduledVoice(tick, frames, recipe.Host, recipe, analysis, cost, benefit);
                result.Voices.Add(voice);
                return voice;
            }
        }

        return null;
    }

    // Reinforces a hit on a second channel when one happens to be free. A layer is a luxury, so it
    // is only taken when it costs nothing at all: it is never the reason a melodic note goes
    // missing, nor the reason a bass note has a drum sitting on its register.
    private static void AddLayer(AllocationPlan result, List<Recipe> options, ScheduledVoice primary, int tick, int room, Occupancy occupancy, double benefit, SampleAnalysis analysis)
    {
        foreach (var recipe in options)
        {
            var frames = Math.Max(1, Math.Min(recipe.Frames, room));
            if (recipe.Host == primary.Host
                || Conflicts(result, recipe.Host, tick, frames)
                || occupancy.Cost(recipe.Host, tick, frames) > 0.0
                || occupancy.Clash(recipe.PitchHz, recipe.Host, tick, frames) > 0.0)
            {
                continue;
            }

            result.Voices.Add(new ScheduledVoice(tick, frames, recipe.Host, recipe, analysis, 0.0, benefit * 0.3, LayerOf: tick));
            result.Stats.Layers++;
            return;
        }
    }

    // Has an earlier voice already booked this channel over this span?
    private static bool Conflicts(AllocationPlan result, Host host, int tick, int frames)
    {
        for (var index = result.Voices.Count - 1; index >= 0; index--)
        {
            var voice = result.Voices[index];
            if (voice.End <= tick)
            {
                break;
            }

            if (voice.Host == host && voice.Tick < tick + frames && tick < voice.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Per-tick activity of the four source voices, as the cost model sees it.</summary>
    private sealed class Occupancy
    {
        private readonly double[][] volume;
        private readonly bool[][] attack;
        private readonly bool[] decorative;
        private readonly List<(Host Host, double Hz)>[] pitches;

        private Occupancy(double[][] volume, bool[][] attack, List<(Host Host, double Hz)>[] pitches)
        {
            this.volume = volume;
            this.attack = attack;
            this.pitches = pitches;
            decorative = DecorativeNoise(volume[(int)Host.Ch4], BurstLengths(volume[(int)Host.Ch4]));
        }

        public static Occupancy Measure(MusicalTimeline timeline)
        {
            var count = timeline.Frames.Count;
            var volume = Enumerable.Range(0, 4).Select(_ => new double[count]).ToArray();
            var attack = Enumerable.Range(0, 4).Select(_ => new bool[count]).ToArray();
            var pitches = new List<(Host Host, double Hz)>[count];
            for (var tick = 0; tick < count; tick++)
            {
                var frame = timeline.Frames[tick];
                var sounding = new List<(Host Host, double Hz)>();
                for (var channel = 0; channel < 3; channel++)
                {
                    var tone = frame.Tone(channel);
                    volume[channel][tick] = tone.Playing ? tone.Volume / 15.0 : 0.0;
                    attack[channel][tick] = tone.Retriggered && tone.Playing;
                    if (tone.Playing && tone.FrequencyHz > 0.0)
                    {
                        sounding.Add(((Host)channel, tone.FrequencyHz));
                    }
                }

                volume[3][tick] = frame.Noise.Playing ? frame.Noise.Volume / 15.0 : 0.0;
                attack[3][tick] = frame.Noise.Retriggered && frame.Noise.Playing;
                pitches[tick] = sounding;
            }

            return new Occupancy(volume, attack, pitches);
        }

        public double Cost(Host host, int start, int frames)
        {
            var weight = ChannelWeight(host);
            var levels = volume[(int)host];
            var attacks = attack[(int)host];
            var total = 0.0;
            for (var tick = start; tick < Math.Min(start + frames, levels.Length); tick++)
            {
                var level = levels[tick];
                if (level <= 0.0)
                {
                    continue;
                }

                var frameCost = level * weight;
                if (attacks[tick])
                {
                    frameCost += level * weight * AttackPenalty;
                }

                if (host == Host.Ch4 && decorative[tick])
                {
                    frameCost *= DecorativeNoiseDiscount;
                }

                total += frameCost;
            }

            return total;
        }

        // Cost per frame held, which is what a benefit per hit compares against. Weighed against a
        // benefit that does not grow with duration, no sustained note could ever be afforded, which
        // is wrong: a bass note that plays for a second is also giving for that second.
        public double Rate(Host host, int start, int frames) => Cost(host, start, frames) / Math.Max(1, frames);

        // What it costs to put a note into a register another part is using. An idle channel is not
        // an idle register: masking and beating happen between frequencies, not channels. The
        // host's own part is excluded, since it is being replaced. The worst moment sets the price:
        // a beat between two close tones is heard the instant it happens.
        public double Clash(double pitchHz, Host host, int start, int frames)
        {
            if (pitchHz <= 0.0 || pitches.Length == 0)
            {
                return 0.0;
            }

            var first = Math.Max(0, start - ClashContextFrames);
            var last = Math.Min(start + frames + ClashContextFrames, pitches.Length);
            var worst = 0.0;
            for (var tick = first; tick < last; tick++)
            {
                foreach (var (otherHost, other) in pitches[tick])
                {
                    if (otherHost == host)
                    {
                        continue;
                    }

                    var distance = Math.Abs(12.0 * Math.Log2(pitchHz / other));
                    if (distance < ClashSemitones)
                    {
                        worst = Math.Max(worst, 1.0 - (distance / ClashSemitones));
                    }
                }
            }

            return ClashWeight * worst;
        }

        // For each tick, how many consecutive ticks its sounding run lasts.
        private static int[] BurstLengths(double[] levels)
        {
            var bursts = new int[levels.Length];
            var runEnd = levels.Length;
            for (var tick = levels.Length - 1; tick >= 0; tick--)
            {
                if (levels[tick] <= 0.0)
                {
                    runEnd = tick;
                    continue;
                }

                bursts[tick] = runEnd - tick;
            }

            var start = 0;
            while (start < levels.Length)
            {
                if (bursts[start] == 0)
                {
                    start++;
                    continue;
                }

                var length = bursts[start];
                for (var offset = 0; offset < length; offset++)
                {
                    bursts[start + offset] = length;
                }

                start += length;
            }

            return bursts;
        }

        // Which noise runs are percussive decoration and not a real part. A run that fades from its
        // attack is a hit, and a hit is masked by the DPCM stroke landing on top of it. A run held
        // at a steady level is somebody playing the noise channel. Runs of a frame or two are
        // decoration whatever their shape, because nothing sustained fits in 33 ms.
        private static bool[] DecorativeNoise(double[] levels, int[] bursts)
        {
            var result = new bool[levels.Length];
            var start = 0;
            while (start < levels.Length)
            {
                var length = bursts[start];
                if (length <= 0)
                {
                    start++;
                    continue;
                }

                var run = levels.AsSpan(start, length);
                var peak = 0.0;
                foreach (var level in run)
                {
                    peak = Math.Max(peak, level);
                }

                if (length <= DecorativeNoiseFrames || run[^1] < SustainedNoiseRatio * peak)
                {
                    result.AsSpan(start, length).Fill(true);
                }

                start += length;
            }

            return result;
        }
    }
}
