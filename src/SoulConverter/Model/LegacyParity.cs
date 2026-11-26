namespace SoulConverter.Model;

/// <summary>
/// Switches the Game Boy reader and the 2A03 writer to what the Python converter did, so the port
/// can be compared with it write for write. Only the parity harness sets it.
/// </summary>
internal static class LegacyParity
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SOULCONVERTER_PARITY") == "python";
}
