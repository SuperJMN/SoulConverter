namespace SoulConverter;

/// <summary>The input is not a VGM log SoulConverter can read.</summary>
public class VgmFormatException : Exception
{
    public VgmFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>The input declares or drives a chip other than the ones SoulConverter converts.</summary>
public sealed class UnsupportedChipException : VgmFormatException
{
    public UnsupportedChipException(string message)
        : base(message)
    {
    }
}
