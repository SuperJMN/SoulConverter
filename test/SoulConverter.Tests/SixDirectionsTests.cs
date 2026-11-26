using SoulConverter.Tests.Support;

namespace SoulConverter.Tests;

/// <summary>
/// Each of the six directions plays the source's notes, timing and volumes on the target, within
/// the tolerance the design sets: 20 cents (modulo octaves), one 60 Hz frame at either end of a
/// note, and one step of the target's volume scale. The target is read with the tests' own decoder, not the library's.
/// </summary>
public sealed class SixDirectionsTests
{
    private const double PitchToleranceCents = 20;
    private static readonly double[] PsgNoiseRates = [TestVgm.PsgClock / 512, TestVgm.PsgClock / 1024, TestVgm.PsgClock / 2048];

    public static TheoryData<SoundChip, SoundChip> Directions() => new()
    {
        { SoundChip.Nes2A03, SoundChip.GameBoyDmg },
        { SoundChip.Nes2A03, SoundChip.Sn76489 },
        { SoundChip.GameBoyDmg, SoundChip.Nes2A03 },
        { SoundChip.GameBoyDmg, SoundChip.Sn76489 },
        { SoundChip.Sn76489, SoundChip.Nes2A03 },
        { SoundChip.Sn76489, SoundChip.GameBoyDmg },
    };

    public static TheoryData<SoundChip> Chips() => [SoundChip.Nes2A03, SoundChip.GameBoyDmg, SoundChip.Sn76489];

    [Theory]
    [MemberData(nameof(Chips))]
    public void The_test_decoder_reads_back_the_score_a_chip_log_was_written_from(SoundChip chip)
    {
        var played = ScoreDecoder.Decode(chip, ScoreEncoder.Encode(chip, Score.Tune));

        Assert.Empty(Differences(Score.Tune, played, chip, chip));
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void The_target_plays_the_notes_the_timing_and_the_volumes_of_the_source(SoundChip source, SoundChip target)
    {
        var result = VgmConverter.Convert(ScoreEncoder.Encode(source, Score.Tune), target);

        Assert.Equal(source, result.Source);
        Assert.Equal(target, result.Target);
        Assert.False(result.IsSilent);
        Assert.Empty(Differences(Score.Tune, ScoreDecoder.Decode(target, result.Vgm), source, target));
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void The_target_lasts_as_long_as_the_source_and_declares_only_its_own_chip(SoundChip source, SoundChip target)
    {
        var result = VgmConverter.Convert(ScoreEncoder.Encode(source, Score.Tune), target);

        Assert.Equal(Score.Tune.Frames * TestVgm.FrameSamples, TestVgm.TotalSamples(result.Vgm));
        Assert.Equal(target, VgmConverter.DetectChip(result.Vgm));
    }

    // Every way the target departs from the score beyond the tolerance, as sentences.
    private static List<string> Differences(Score score, List<VoiceState[]> played, SoundChip source, SoundChip target)
    {
        var differences = new List<string>();
        for (var voice = 0; voice < 4; voice++)
        {
            var expectedFrames = 0;
            var coveredFrames = 0;
            for (var frame = 1; frame < score.Frames - 1; frame++)
            {
                var note = score.At(voice, frame);
                var state = played[frame][voice];
                expectedFrames += note is null ? 0 : 1;
                coveredFrames += note is not null && state.Playing ? 1 : 0;

                // A frame is settled when its neighbours agree with it: a note may start or stop
                // one frame early or late, but not more.
                var settled = Same(note, score.At(voice, frame - 1)) && Same(note, score.At(voice, frame + 1));
                if (!settled)
                {
                    continue;
                }

                if (state.Playing != (note is not null))
                {
                    differences.Add($"voice {voice} frame {frame}: {(state.Playing ? "plays" : "is silent")}, the score {(note is null ? "rests" : "plays")}");
                }
                else if (note is not null)
                {
                    differences.AddRange(NoteDifferences(voice, frame, note, state, source, target));
                }
            }

            // Each note may lose a frame at either end, so the whole voice keeps nine frames in ten.
            if (coveredFrames < 0.90 * expectedFrames)
            {
                differences.Add($"voice {voice}: plays on {coveredFrames} of the score's {expectedFrames} frames");
            }
        }

        return differences;
    }

    private static bool Same(Note? left, Note? right) => ReferenceEquals(left, right);

    private static IEnumerable<string> NoteDifferences(int voice, int frame, Note note, VoiceState state, SoundChip source, SoundChip target)
    {
        if (voice == 3)
        {
            // Noise: the nearest of the PSG's three rates, or within an octave on the other chips.
            var expected = target == SoundChip.Sn76489 ? PsgNoiseRates.MinBy(rate => Math.Abs(Math.Log(rate / note.Hz))) : note.Hz;
            var octaves = Math.Abs(Math.Log2(state.Hz / expected));
            if (octaves > (target == SoundChip.Sn76489 ? 0.01 : 1.0))
            {
                yield return $"noise frame {frame}: clocked at {state.Hz:0} Hz, expected about {expected:0} Hz";
            }
        }
        else
        {
            var cents = 1200 * Math.Log2(state.Hz / note.Hz);
            var residual = Math.Abs(cents - (1200 * Math.Round(cents / 1200)));
            if (residual > PitchToleranceCents)
            {
                yield return $"voice {voice} frame {frame}: {state.Hz:0.0} Hz is {residual:0} cents off {note.Hz:0.0} Hz";
            }
        }

        // The 2A03 triangle has no volume and the DMG wave channel four levels, so the bass voice
        // is compared on and off only.
        if (voice == 2)
        {
            yield break;
        }

        var volumeOff = target == SoundChip.Sn76489 || source == SoundChip.Sn76489
            ? Math.Abs(20 * Math.Log10(state.Volume / note.Volume)) > 2.0
            : Math.Abs(state.Volume - note.Volume) > 1.0;
        if (volumeOff)
        {
            yield return $"voice {voice} frame {frame}: volume {state.Volume:0.0}, the score says {note.Volume}";
        }
    }
}
