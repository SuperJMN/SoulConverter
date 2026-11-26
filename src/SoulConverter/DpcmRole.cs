namespace SoulConverter;

/// <summary>
/// What a DPCM sample is musically, which decides how the Game Boy plays it. <see cref="Artifact"/>
/// is not an instrument: it marks samples with no musical intent, such as the DAC-levelling blip
/// many drivers emit at startup, which are dropped instead of re-synthesised.
/// </summary>
public enum DpcmRole
{
    Kick,
    Snare,
    Hat,
    Tom,
    Tonal,
    NoiseSfx,
    Artifact,
}

/// <summary>Names for <see cref="DpcmRole"/> values, as reports and the command line spell them.</summary>
public static class DpcmRoleExtensions
{
    private static readonly (DpcmRole Role, string Name)[] Names =
    [
        (DpcmRole.Kick, "kick"), (DpcmRole.Snare, "snare"), (DpcmRole.Hat, "hat"), (DpcmRole.Tom, "tom"),
        (DpcmRole.Tonal, "tonal"), (DpcmRole.NoiseSfx, "noise_sfx"), (DpcmRole.Artifact, "artifact"),
    ];

    /// <summary>Every role name, in declaration order.</summary>
    public static IReadOnlyList<string> AllNames { get; } = [.. Names.Select(entry => entry.Name)];

    /// <summary>The role's name in reports, e.g. <c>noise_sfx</c>.</summary>
    public static string ToName(this DpcmRole role) => Names.First(entry => entry.Role == role).Name;

    /// <summary>Parses a role name; false when <paramref name="name"/> is not one.</summary>
    public static bool TryParse(string name, out DpcmRole role)
    {
        foreach (var entry in Names)
        {
            if (string.Equals(entry.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                role = entry.Role;
                return true;
            }
        }

        role = default;
        return false;
    }
}
