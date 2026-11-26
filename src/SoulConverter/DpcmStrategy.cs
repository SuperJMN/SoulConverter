namespace SoulConverter;

/// <summary>
/// What to do with the NES sample channel on a Game Boy, which has none. A named trade-off between
/// keeping the NES voices as they are and adding the fifth one by borrowing their channels.
/// </summary>
public enum DpcmStrategy
{
    /// <summary>Drop the DPCM channel. The report still says what is lost.</summary>
    Off,

    /// <summary>Percussion on the noise channel only; the melody is never touched.</summary>
    Safe,

    /// <summary>Drums and sampled bass on a tonal channel where one is free enough.</summary>
    Balanced,

    /// <summary>Everything: layered drums and melodic DPCM, borrowing freely.</summary>
    Full,
}

/// <summary>How the Game Boy follows a source voice's volume.</summary>
public enum VolumeMode
{
    /// <summary>A fixed volume per tick equal to the source's, re-triggering the channel whenever it changes.</summary>
    Retrigger,

    /// <summary>The source's envelope mapped onto the Game Boy's envelope unit once per note: smaller and click-free.</summary>
    Envelope,
}
