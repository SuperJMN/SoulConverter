using SoulConverter.Model;
using SoulConverter.Vgm;

namespace SoulConverter.Sn;

/// <summary>
/// The SN76489 replayed into musical state on a tick grid: three square tones with a 10-bit
/// period and a 4-bit attenuation in 2 dB steps, and a noise generator clocked at one of three
/// fixed rates or by the third tone, white or periodic. The chip has no envelopes or counters, so
/// its state only changes when it is written.
/// </summary>
internal sealed class SnPsg
{
    private readonly int[] periods = new int[3];
    private readonly int[] attenuations = [15, 15, 15, 15];
    private readonly bool[] addressed = new bool[4];
    private readonly double clockHz;
    private int noiseControl;
    private int latched;
    private int stereo = 0xFF;
    private bool noiseRestarted;

    private SnPsg(double clockHz) => this.clockHz = clockHz;

    /// <summary>The linear volume (0-15) of an attenuation: 2 dB per step, 15 is off.</summary>
    public static int Volume(int attenuation) =>
        attenuation >= 15 ? 0 : Math.Max(1, (int)Math.Round(15.0 * Math.Pow(10.0, -attenuation / 10.0)));

    public static MusicalTimeline Replay(VgmLog log, double tickHz)
    {
        if (!(tickHz > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(tickHz), tickHz, "The tick rate must be positive.");
        }

        var psg = new SnPsg(log.ClockHz);
        var samplesPerTick = VgmLog.SampleRate / tickHz;
        var tickCount = Math.Max(1, (int)Math.Ceiling(log.TotalSamples / samplesPerTick));
        var ticks = new List<MusicalFrame>(tickCount);
        var writes = log.Writes;
        var writeIndex = 0;
        for (var index = 0; index < tickCount; index++)
        {
            var sample = (int)Math.Round(index * samplesPerTick);
            while (writeIndex < writes.Count && writes[writeIndex].Sample <= sample)
            {
                var write = writes[writeIndex++];
                if (write.Register == VgmLogReader.SnStereoRegister)
                {
                    psg.stereo = write.Value;
                }
                else
                {
                    psg.Write(write.Value);
                }
            }

            ticks.Add(psg.Snapshot(index, sample));
        }

        return new MusicalTimeline
        {
            Log = log,
            TickHz = tickHz,
            Frames = ticks,
            OnGrid = true,
            LoopFrame = log.LoopSample is { } loop ? Math.Min(tickCount - 1, (int)Math.Round(loop / samplesPerTick)) : null,
            TotalSamples = log.TotalSamples,
        };
    }

    // A byte with bit 7 set latches a register (%1cctdddd: channel, type, low data); one without
    // carries the upper bits of the latched tone period, or replaces the latched 4-bit register.
    private void Write(int value)
    {
        if ((value & 0x80) != 0)
        {
            latched = (value >> 4) & 7;
        }

        var channel = latched >> 1;
        addressed[channel] = true;
        var isVolume = (latched & 1) != 0;
        if (isVolume)
        {
            attenuations[channel] = value & 0x0F;
        }
        else if (channel == 3)
        {
            // Writing the noise control restarts the shift register.
            noiseControl = value & 0x07;
            noiseRestarted = true;
        }
        else if ((value & 0x80) != 0)
        {
            periods[channel] = (periods[channel] & 0x3F0) | (value & 0x0F);
        }
        else
        {
            periods[channel] = (periods[channel] & 0x00F) | ((value & 0x3F) << 4);
        }
    }

    private MusicalFrame Snapshot(int index, int sample)
    {
        var tones = new ToneVoice[3];
        var split = false;
        for (var channel = 0; channel < 3; channel++)
        {
            var volume = Routed(channel) ? Volume(attenuations[channel]) : 0;

            // Periods 0 and 1 give no audible tone on the Sega PSG.
            var playing = volume > 0 && periods[channel] > 1;
            tones[channel] = new ToneVoice(
                Playing: playing,
                FrequencyHz: periods[channel] > 1 ? ToneFrequency(channel) : 0.0,
                Volume: volume,
                Duty: 2,
                Timbre: Timbre.Square,
                Addressed: addressed[channel]);
            split |= playing && OneSided(channel);
        }

        var rate = noiseControl & 3;
        var noiseVolume = Routed(3) ? Volume(attenuations[3]) : 0;
        var clockRate = rate == 3 ? (periods[2] > 0 ? ToneFrequency(2) : 0.0) : clockHz / (512 << rate);
        var noise = new NoiseVoice(
            Playing: noiseVolume > 0 && clockRate > 0,
            ClockRateHz: clockRate,
            Volume: noiseVolume,
            Short: (noiseControl & 0x04) == 0,
            Retriggered: noiseRestarted,
            RateIndex: rate,
            Addressed: addressed[3]);
        split |= noise.Playing && OneSided(3);
        noiseRestarted = false;

        return new MusicalFrame
        {
            Index = index,
            Sample = sample,
            Tone1 = tones[0],
            Tone2 = tones[1],
            Tone3 = tones[2],
            Noise = noise,
            StereoSplit = split,
        };
    }

    private double ToneFrequency(int channel) => clockHz / (32.0 * periods[channel]);

    // The Game Gear stereo byte: bits 0-3 route channels 0-3 to the right, bits 4-7 to the left.
    private bool Routed(int channel) => (stereo & (0x11 << channel)) != 0;

    private bool OneSided(int channel) => ((stereo >> channel) & 1) != ((stereo >> (channel + 4)) & 1);
}
