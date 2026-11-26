namespace SoulConverter;

/// <summary>A sound chip SoulConverter reads and writes VGM logs for.</summary>
public enum SoundChip
{
    /// <summary>The NES / Famicom 2A03 APU: two pulses, a triangle, noise and a DPCM sample channel.</summary>
    Nes2A03,

    /// <summary>The Game Boy DMG APU: two squares, a wave channel and noise.</summary>
    GameBoyDmg,

    /// <summary>The SN76489 PSG of the Master System and the Game Gear: three square tones and noise.</summary>
    Sn76489,
}

/// <summary>Names for <see cref="SoundChip"/> values.</summary>
public static class SoundChipExtensions
{
    /// <summary>The chip's name as people write it, e.g. "NES 2A03".</summary>
    public static string DisplayName(this SoundChip chip) => chip switch
    {
        SoundChip.Nes2A03 => "NES 2A03",
        SoundChip.GameBoyDmg => "Game Boy DMG",
        SoundChip.Sn76489 => "SN76489",
        _ => throw new ArgumentOutOfRangeException(nameof(chip), chip, null),
    };
}
