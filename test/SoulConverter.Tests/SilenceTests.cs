using SoulConverter.Tests.Support;

namespace SoulConverter.Tests;

/// <summary>
/// A log that leaves the target silent still converts, and says so, so that a build can fall back
/// to another log of the same asset.
/// </summary>
public sealed class SilenceTests
{
    // A sound effect made only of a DPCM sample: no pulse, triangle or noise ever plays.
    private static readonly byte[] DpcmOnlyEffect = TestVgm.Nes(
        [(0x4010, 0x0F), (0x4012, 0x00), (0x4013, 0x10), (0x4015, 0x10)],
        [],
        []);

    [Theory]
    [InlineData(SoundChip.Sn76489)]
    [InlineData(SoundChip.GameBoyDmg)]
    public void A_dpcm_only_effect_leaves_a_chip_without_samples_silent_and_says_so(SoundChip target)
    {
        var result = VgmConverter.Convert(DpcmOnlyEffect, target);

        Assert.True(result.IsSilent);
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.Silent);
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.DpcmDropped);
    }

    [Fact]
    public void A_silent_conversion_is_still_a_log_of_the_target_with_the_source_length()
    {
        var result = VgmConverter.Convert(DpcmOnlyEffect, SoundChip.Sn76489);

        Assert.Equal(SoundChip.Sn76489, VgmConverter.DetectChip(result.Vgm));
        Assert.Equal(3 * TestVgm.FrameSamples, TestVgm.TotalSamples(result.Vgm));
        Assert.Empty(TestVgm.Writes(result.Vgm));
    }

    [Fact]
    public void A_log_that_plays_is_not_silent_and_carries_no_such_warning()
    {
        var result = VgmConverter.Convert(DemoSongs.FourChannel(), SoundChip.Sn76489);

        Assert.False(result.IsSilent);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == WarningCodes.Silent);
    }

    [Fact]
    public void A_log_that_only_sets_registers_up_is_silent_on_the_nes_too()
    {
        var result = VgmConverter.Convert(TestVgm.Dmg([(0xFF26, 0x80), (0xFF24, 0x77), (0xFF25, 0xFF), (0xFF12, 0x00)], []), SoundChip.Nes2A03);

        Assert.True(result.IsSilent);
    }

    [Fact]
    public void A_silent_log_of_the_target_chip_is_reported_as_silent()
    {
        var result = VgmConverter.Convert(TestVgm.Sn([0x9F], []), SoundChip.Sn76489);

        Assert.True(result.IsSilent);
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.Silent);
    }

    [Fact]
    public void Every_warning_is_also_in_the_report_and_in_the_summary()
    {
        var result = VgmConverter.Convert(DpcmOnlyEffect, SoundChip.Sn76489);

        Assert.Equal(result.Warnings.Select(warning => warning.Message), result.Report["warnings"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.All(result.Warnings, warning => Assert.Contains(warning.Message, result.Summary, StringComparison.Ordinal));
    }
}
