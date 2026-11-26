using System.Globalization;
using System.Text.Json.Nodes;
using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Dmg;

/// <summary>
/// Turns musical state into Game Boy DMG register writes: tone 1 and 2 on the squares (CH1, CH2),
/// tone 3 on the wave channel (CH3, with a triangle or a square in wave RAM), noise on CH4. The
/// sample voice has no counterpart; with a DPCM strategy it is re-synthesised on channels borrowed
/// from the others, which are left alone for the span of each borrowed voice and restored the tick
/// after.
/// </summary>
internal static class DmgWriter
{
    public static readonly string[] ChannelNames = ["pulse1", "pulse2", "triangle", "noise"];

    private const double NoiseWarnCents = 200.0;

    public static ChipWriteResult Write(MusicalTimeline source, ConversionOptions options)
    {
        var timeline = source.ToGrid();
        var preset = Preset.For(options.Dpcm);
        var plan = Allocator.Plan(timeline, timeline.Log.DpcmRom, preset, options.DpcmRoles);
        var stats = new MappingStats();
        var ticks = Convert(timeline, options, preset.Enabled ? plan : null, stats);

        var writer = new VgmWriter(SoundChip.GameBoyDmg);
        for (var index = 0; index < ticks.Count; index++)
        {
            writer.WaitUntil(timeline.Frames[index].Sample);
            if (timeline.LoopFrame == index)
            {
                writer.MarkLoop();
            }

            foreach (var (register, value) in ticks[index])
            {
                writer.Write(register, value);
            }
        }

        var vgm = writer.Finish(timeline.TotalSamples, timeline.Log.Gd3, $"Converted from {timeline.Source.DisplayName()} by SoulConverter");
        var result = new ChipWriteResult
        {
            Vgm = vgm,
            Silent = stats.ActiveTicks.Sum() == 0 && plan.Stats.Placed == 0,
            RegisterWrites = stats.RegisterWrites,
        };

        result.Output["ticks"] = ticks.Count;
        result.Output["loop_tick"] = timeline.LoopFrame;
        result.Sections["dpcm"] = DpcmSection(timeline.Log, plan);
        result.Sections["dropped"] = new JsonObject { ["pcm_stream_commands"] = timeline.Log.Dropped.PcmStreamCommands };
        var channels = new JsonObject();
        for (var channel = 0; channel < ChannelNames.Length; channel++)
        {
            channels[ChannelNames[channel]] = new JsonObject
            {
                ["active_ticks"] = stats.ActiveTicks[channel],
                ["note_ons"] = stats.NoteOns[channel],
                ["retriggers"] = stats.Retriggers[channel],
                ["transposed_ticks"] = stats.TransposedTicks[channel],
                ["max_octaves_up"] = stats.MaxOctavesUp[channel],
            };
        }

        result.Sections["channels"] = channels;
        var noiseMapping = new JsonObject();
        foreach (var (index, cents) in stats.NoiseErrorCents.OrderBy(entry => entry.Key))
        {
            noiseMapping[index.ToString(CultureInfo.InvariantCulture)] = Math.Round(cents, 1);
        }

        result.Sections["noise_mapping_cents"] = noiseMapping;
        AddWarnings(result, timeline, plan, stats);
        return result;
    }

    private static void AddWarnings(ChipWriteResult result, MusicalTimeline timeline, AllocationPlan plan, MappingStats stats)
    {
        var log = timeline.Log;
        var directDacWrites = log.Writes.Count(write => log.Chip == SoundChip.Nes2A03 && write.Register == 0x11);
        if (!plan.Preset.Enabled && (log.DpcmRom.Count != 0 || plan.Stats.Hits != 0))
        {
            result.Warnings.Add(new ConversionWarning(
                WarningCodes.DpcmDropped,
                $"DPCM dropped: {log.Dropped.DpcmRegisterWrites} register writes and {log.DpcmRom.Count} bytes of sample data over " +
                $"{plan.Stats.Hits} hits. The Game Boy has no sample channel; re-synthesise them with the safe, balanced or full DPCM strategy."));
        }
        else if (plan.Stats.Hits != 0)
        {
            var missed = plan.Stats.DroppedTooCostly + plan.Stats.DroppedNoHost;
            if (missed != 0)
            {
                result.Warnings.Add(new ConversionWarning(
                    WarningCodes.DpcmHitsDropped,
                    $"{missed} of {plan.Stats.Hits} DPCM hits were dropped because no Game Boy channel was free enough to carry them."));
            }

            foreach (var (sample, analysis) in plan.Analyses.OrderByDescending(entry => plan.HitCounts.GetValueOrDefault(entry.Key)))
            {
                if (!analysis.Overridden && analysis.Confidence < 0.35 && plan.HitCounts.GetValueOrDefault(sample) > 1)
                {
                    result.Warnings.Add(new ConversionWarning(
                        WarningCodes.DpcmLowConfidence,
                        string.Create(CultureInfo.InvariantCulture, $"sample 0x{sample.Address:X4} was classified as {analysis.Role.ToName()} with low confidence ({analysis.Confidence:0.00}); listen to it and force its role if it is wrong.")));
                }
            }
        }

        if (directDacWrites > 50 && log.DpcmRom.Count == 0)
        {
            result.Warnings.Add(new ConversionWarning(
                WarningCodes.PcmStream,
                $"{directDacWrites} direct writes to $4011 with no sample data: this song streams PCM in software (usually speech), which cannot be re-synthesised and is not converted."));
        }

        for (var channel = 0; channel < ChannelNames.Length; channel++)
        {
            if (stats.TransposedTicks[channel] != 0)
            {
                result.Warnings.Add(new ConversionWarning(
                    WarningCodes.Transposed,
                    $"{ChannelNames[channel]}: {stats.TransposedTicks[channel]} ticks transposed up to {stats.MaxOctavesUp[channel]} octave(s); the Game Boy cannot reach those low notes."));
            }
        }

        foreach (var (index, cents) in stats.NoiseErrorCents.OrderBy(entry => entry.Key))
        {
            if (Math.Abs(Math.Round(cents, 1)) > NoiseWarnCents)
            {
                result.Warnings.Add(new ConversionWarning(
                    WarningCodes.NoiseMapping,
                    string.Create(CultureInfo.InvariantCulture, $"noise period index {index} maps {Math.Round(cents, 1):+0;-0} cents off; the Game Boy noise divisor grid has no closer match.")));
            }
        }

        if (timeline.Source == SoulConverter.SoundChip.Sn76489 && stats.ActiveTicks[2] != 0)
        {
            result.Warnings.Add(new ConversionWarning(
                WarningCodes.Dynamics,
                "the third tone plays on the wave channel, which only has four volume levels"));
        }
    }

    private static List<List<(int Register, int Value)>> Convert(MusicalTimeline timeline, ConversionOptions options, AllocationPlan? plan, MappingStats stats)
    {
        var registers = new RegisterFile();
        var waveNibbles = timeline.Source == SoundChip.Sn76489 ? GameBoy.SquareWave : GameBoy.TriangleWave;
        var squares = new[]
        {
            new SquareChannel(0, GameBoy.NR11, GameBoy.NR12, GameBoy.NR13, GameBoy.NR14),
            new SquareChannel(1, GameBoy.NR21, GameBoy.NR22, GameBoy.NR23, GameBoy.NR24),
        };
        var wave = new WaveChannel();
        var noise = new NoiseChannel();
        var envelopeMode = options.VolumeMode == VolumeMode.Envelope;
        var transpose = options.TransposeOutOfRange;

        var ticks = new List<List<(int Register, int Value)>>(timeline.Frames.Count);
        for (var index = 0; index < timeline.Frames.Count; index++)
        {
            var frame = timeline.Frames[index];
            if (index == 0)
            {
                EmitInit(registers, waveNibbles);
            }

            if (plan is not null)
            {
                foreach (var host in plan.ReleasedAt(index))
                {
                    stats.DpcmRestores++;
                    switch (host)
                    {
                        // Clearing `Active` forces the next tick to re-emit the whole note, but two
                        // pieces of state outlive a trigger and are undone by hand: CH1's sweep would
                        // detune the next note, and CH3's wave RAM would leave tone 3 with a drum's
                        // timbre.
                        case Host.Ch1:
                            squares[0].Active = false;
                            registers.Set(GameBoy.NR10, 0x00);
                            registers.Set(GameBoy.NR12, 0x00);
                            break;
                        case Host.Ch2:
                            squares[1].Active = false;
                            registers.Set(GameBoy.NR22, 0x00);
                            break;
                        case Host.Ch3:
                            wave.Active = false;
                            registers.Force(GameBoy.NR30, 0x00);
                            LoadWave(registers, waveNibbles, force: false);

                            // Reloading wave RAM means cycling the DAC, which disables the channel,
                            // so here it does need a fresh trigger, unlike an ordinary rest.
                            wave.Started = false;
                            break;
                        default:
                            noise.Active = false;
                            registers.Set(GameBoy.NR42, 0x00);
                            break;
                    }
                }
            }

            if (plan is null || !plan.IsBorrowed(Host.Ch1, index))
            {
                squares[0].Update(frame.Tone1, registers, stats, envelopeMode, transpose);
            }

            if (plan is null || !plan.IsBorrowed(Host.Ch2, index))
            {
                squares[1].Update(frame.Tone2, registers, stats, envelopeMode, transpose);
            }

            if (plan is null || !plan.IsBorrowed(Host.Ch3, index))
            {
                wave.Update(frame.Tone3, registers, stats, transpose);
            }

            if (plan is null || !plan.IsBorrowed(Host.Ch4, index))
            {
                noise.Update(frame.Noise, registers, stats, envelopeMode);
            }

            if (plan is not null)
            {
                foreach (var voice in plan.StartingAt(index))
                {
                    switch (voice.Host)
                    {
                        case Host.Ch1:
                            squares[0].Active = false;
                            break;
                        case Host.Ch2:
                            squares[1].Active = false;
                            break;
                        case Host.Ch3:
                            wave.Active = false;
                            break;
                        default:
                            noise.Active = false;
                            break;
                    }

                    foreach (var (register, value) in voice.Recipe.Writes)
                    {
                        // Trigger bits self-clear on the hardware; remembering them would make the
                        // next tick re-emit the register just to drop a bit that is no longer set.
                        var isTrigger = register is GameBoy.NR14 or GameBoy.NR24 or GameBoy.NR34 or GameBoy.NR44;
                        registers.Force(register, value, isTrigger ? value & ~0x80 : null);
                    }

                    stats.DpcmVoices++;
                }
            }

            var writes = registers.Drain();
            stats.RegisterWrites += writes.Count;
            ticks.Add(writes);
        }

        return ticks;
    }

    // Powers the APU up and loads wave RAM before any channel is triggered.
    private static void EmitInit(RegisterFile registers, int[] waveNibbles)
    {
        registers.Force(GameBoy.NR52, 0x80);
        registers.Force(GameBoy.NR50, 0x77);
        registers.Force(GameBoy.NR51, 0xFF);
        registers.Force(GameBoy.NR10, 0x00); // the source's sweep is already in the pitch
        registers.Force(GameBoy.NR30, 0x00); // wave RAM is only safe to load with CH3 off
        LoadWave(registers, waveNibbles, force: true);
        registers.Force(GameBoy.NR12, 0x00);
        registers.Force(GameBoy.NR22, 0x00);
        registers.Force(GameBoy.NR42, 0x00);
    }

    private static void LoadWave(RegisterFile registers, int[] waveNibbles, bool force)
    {
        var packed = GameBoy.PackWave(waveNibbles);
        for (var offset = 0; offset < packed.Length; offset++)
        {
            if (force)
            {
                registers.Force(GameBoy.WaveRamBase + offset, packed[offset]);
            }
            else
            {
                registers.Set(GameBoy.WaveRamBase + offset, packed[offset]);
            }
        }
    }

    // What the sample channel contained and what became of it.
    private static JsonObject DpcmSection(VgmLog log, AllocationPlan plan)
    {
        var stats = plan.Stats;
        var samples = new List<(int Hits, JsonObject Entry)>();
        foreach (var (sample, analysis) in plan.Analyses)
        {
            var features = analysis.Features;
            var voices = plan.Voices.Where(voice => voice.Analysis.Ref == sample).ToList();
            var hosts = new JsonArray();
            foreach (var host in voices.Select(voice => voice.Host.Name()).Distinct().Order(StringComparer.Ordinal))
            {
                hosts.Add(host);
            }

            var why = new JsonArray();
            foreach (var reason in analysis.Reasons)
            {
                why.Add(reason);
            }

            var hits = plan.HitCounts.GetValueOrDefault(sample);
            samples.Add((hits, new JsonObject
            {
                ["address"] = $"0x{sample.Address:X4}",
                ["length"] = sample.Length,
                ["rate_index"] = sample.RateIndex,
                ["duration_ms"] = Math.Round(sample.DurationSeconds * 1000, 1),
                ["rate_hz"] = Math.Round(sample.RateHz, 1),
                ["hits"] = hits,
                ["role"] = analysis.Role.ToName(),
                ["confidence"] = analysis.Confidence,
                ["overridden"] = analysis.Overridden,
                ["why"] = why,
                ["pitch_hz"] = Math.Round(features.PitchHz, 1),
                ["centroid_hz"] = Math.Round(features.CentroidHz, 1),
                ["bands"] = new JsonObject
                {
                    ["low"] = Math.Round(features.Low, 3),
                    ["mid"] = Math.Round(features.Mid, 3),
                    ["high"] = Math.Round(features.High, 3),
                },
                ["placed"] = voices.Count(voice => voice.LayerOf is null),
                ["hosts"] = hosts,
                ["recipe"] = voices.FirstOrDefault()?.Recipe.Description,
            }));
        }

        var sampleArray = new JsonArray();
        foreach (var (_, entry) in samples.OrderByDescending(item => item.Hits))
        {
            sampleArray.Add(entry);
        }

        return new JsonObject
        {
            ["preset"] = plan.Preset.Name,
            ["strategy"] = plan.Preset.Description,
            ["rom_bytes"] = log.DpcmRom.Count,
            ["rom_blocks"] = log.DpcmRom.BlockCount,
            ["register_writes"] = log.Dropped.DpcmRegisterWrites,
            ["direct_dac_writes"] = log.Writes.Count(write => log.Chip == SoundChip.Nes2A03 && write.Register == 0x11),
            ["hits"] = stats.Hits,
            ["placed"] = stats.Placed,
            ["layers"] = stats.Layers,
            ["realised_fraction"] = Math.Round(stats.RealisedFraction, 3),
            ["dropped"] = new JsonObject
            {
                ["artifact"] = stats.DroppedArtifact,
                ["too_costly"] = stats.DroppedTooCostly,
                ["no_host"] = stats.DroppedNoHost,
            },
            ["per_role"] = Counts(stats.PerRole),
            ["per_host"] = Counts(stats.PerHost),
            ["preempted_frames"] = Counts(stats.PreemptedFrames),
            ["samples"] = sampleArray,
        };

        static JsonObject Counts(OrderedDictionary<string, int> counts)
        {
            var node = new JsonObject();
            foreach (var (name, count) in counts)
            {
                node[name] = count;
            }

            return node;
        }
    }

    // The closest Game Boy envelope step (1/64 s units) to a NES step ((V+1)/240 s).
    private static int GbEnvelopePeriod(int sourcePeriod) => Math.Clamp((int)Math.Round(64.0 * (sourcePeriod + 1) / 240.0), 1, 7);

    // NRx2 for a note-on, mapping the source's envelope onto the GB envelope unit.
    private static int EnvelopeByte(bool constantVolume, int volume, int envelopePeriod) =>
        constantVolume ? volume << 4 : (15 << 4) | GbEnvelopePeriod(envelopePeriod);

    private sealed class MappingStats
    {
        public int[] TransposedTicks { get; } = new int[4];

        public int[] MaxOctavesUp { get; } = new int[4];

        public int[] Retriggers { get; } = new int[4];

        public int[] NoteOns { get; } = new int[4];

        public int[] ActiveTicks { get; } = new int[4];

        public Dictionary<int, double> NoiseErrorCents { get; } = [];

        public int RegisterWrites;
        public int DpcmVoices;
        public int DpcmRestores;
    }

    /// <summary>Tracks last-written values so only changed registers are emitted.</summary>
    private sealed class RegisterFile
    {
        private readonly Dictionary<int, int> values = [];
        private List<(int Register, int Value)> pending = [];

        public bool Set(int register, int value)
        {
            value &= 0xFF;
            if (values.TryGetValue(register, out var current) && current == value)
            {
                return false;
            }

            values[register] = value;
            pending.Add((register, value));
            return true;
        }

        // Always emits the write, but remembers `remember` as the visible state. Trigger bits are
        // self-clearing on the hardware, so remembering the value without them keeps the delta
        // compression from re-emitting the register just to drop a bit that is no longer set.
        public void Force(int register, int value, int? remember = null)
        {
            value &= 0xFF;
            values[register] = (remember ?? value) & 0xFF;
            pending.Add((register, value));
        }

        public List<(int Register, int Value)> Drain()
        {
            var writes = pending;
            pending = [];
            return writes;
        }
    }

    private sealed class SquareChannel(int channel, int nrx1, int nrx2, int nrx3, int nrx4)
    {
        public bool Active;

        public void Update(ToneVoice state, RegisterFile registers, MappingStats stats, bool envelopeMode, bool allowTranspose)
        {
            if (!state.Playing)
            {
                if (Active)
                {
                    registers.Set(nrx2, 0x00); // DAC off silences the channel
                    Active = false;
                }

                return;
            }

            var (period, octaves) = GameBoy.SquarePeriod(state.FrequencyHz, allowTranspose);
            if (octaves != 0)
            {
                stats.TransposedTicks[channel]++;
                stats.MaxOctavesUp[channel] = Math.Max(stats.MaxOctavesUp[channel], octaves);
            }

            stats.ActiveTicks[channel]++;
            var noteOn = !Active || state.Retriggered;
            registers.Set(nrx1, state.Duty << 6);

            bool trigger;
            if (envelopeMode)
            {
                if (noteOn)
                {
                    registers.Set(nrx2, EnvelopeByte(state.ConstantVolume, state.Volume, state.EnvelopePeriod));
                }

                trigger = noteOn;
            }
            else
            {
                var volumeChanged = registers.Set(nrx2, state.Volume << 4);
                trigger = noteOn || volumeChanged;
            }

            registers.Set(nrx3, period & 0xFF);
            var high = (period >> 8) & 0x07;
            if (trigger)
            {
                registers.Force(nrx4, high | 0x80, remember: high);
                stats.Retriggers[channel]++;
                if (noteOn)
                {
                    stats.NoteOns[channel]++;
                }
            }
            else
            {
                registers.Set(nrx4, high);
            }

            Active = true;
        }
    }

    private sealed class WaveChannel
    {
        private const int Channel = 2;

        public bool Active;
        public bool Started;

        public void Update(ToneVoice state, RegisterFile registers, MappingStats stats, bool allowTranspose)
        {
            if (!state.Playing)
            {
                if (Active)
                {
                    // Silence with the output level, not the DAC. Clearing NR30 disconnects the DAC
                    // and drops the output from wherever the wave was to nothing, which pops, and a
                    // bass line rests between almost every note. NR32 = 0 mutes the same channel
                    // without moving the DAC.
                    registers.Set(GameBoy.NR32, 0x00);
                    Active = false;
                }

                return;
            }

            var (period, octaves) = GameBoy.WavePeriod(state.FrequencyHz, allowTranspose);
            if (octaves != 0)
            {
                stats.TransposedTicks[Channel]++;
                stats.MaxOctavesUp[Channel] = Math.Max(stats.MaxOctavesUp[Channel], octaves);
            }

            stats.ActiveTicks[Channel]++;

            // Start the channel once and then leave it running. Triggering CH3 rewinds wave RAM to
            // its first sample, so the output jumps there from wherever it was: a click. The NES
            // triangle has no such thing: its sequencer runs on through note changes and rests,
            // which is why the part sounds smooth. Coming back from a rest needs no trigger either:
            // nothing disabled the channel, its level was simply turned down.
            var noteOn = !Started;
            registers.Set(GameBoy.NR30, 0x80);
            registers.Set(GameBoy.NR32, OutputLevel(state.Volume) << 5);
            registers.Set(GameBoy.NR33, period & 0xFF);
            var high = (period >> 8) & 0x07;
            if (noteOn)
            {
                registers.Force(GameBoy.NR34, high | 0x80, remember: high);
                stats.Retriggers[Channel]++;
                stats.NoteOns[Channel]++;
            }
            else
            {
                registers.Set(GameBoy.NR34, high);
            }

            Active = true;
            Started = true;
        }

        // CH3 has four output levels: mute, 100%, 50% and 25% (codes 0, 1, 2, 3).
        private static int OutputLevel(int volume) => volume switch
        {
            >= 11 => 1,
            >= 6 => 2,
            _ => 3,
        };
    }

    private sealed class NoiseChannel
    {
        private const int Channel = 3;

        public bool Active;

        public void Update(NoiseVoice state, RegisterFile registers, MappingStats stats, bool envelopeMode)
        {
            if (!state.Playing)
            {
                if (Active)
                {
                    registers.Set(GameBoy.NR42, 0x00);
                    Active = false;
                }

                return;
            }

            var (shift, divisor, errorCents) = GameBoy.NearestNoise(state.ClockRateHz);
            stats.NoiseErrorCents[state.RateIndex] = errorCents;
            stats.ActiveTicks[Channel]++;

            var noteOn = !Active || state.Retriggered;
            bool trigger;
            if (envelopeMode)
            {
                if (noteOn)
                {
                    registers.Set(GameBoy.NR42, EnvelopeByte(state.ConstantVolume, state.Volume, state.EnvelopePeriod));
                }

                trigger = noteOn;
            }
            else
            {
                var volumeChanged = registers.Set(GameBoy.NR42, state.Volume << 4);
                trigger = noteOn || volumeChanged;
            }

            registers.Set(GameBoy.NR43, (shift << 4) | (state.Short ? 0x08 : 0) | divisor);
            if (trigger)
            {
                registers.Force(GameBoy.NR44, 0x80, remember: 0x00);
                stats.Retriggers[Channel]++;
                if (noteOn)
                {
                    stats.NoteOns[Channel]++;
                }
            }

            Active = true;
        }
    }
}
