namespace SoulConverter.Tests.Support;

/// <summary>
/// A note of a test score. Voices 0 and 1 are the two melodic tones, 2 is the bass voice (the
/// triangle, the wave channel or the third tone) and 3 is noise, where <see cref="Hz"/> is the
/// rate the noise generator is clocked at. Volumes are linear, 0-15.
/// </summary>
public sealed record Note(int Voice, int StartFrame, int Frames, double Hz, int Volume)
{
    public int EndFrame => StartFrame + Frames;
}

/// <summary>What one voice does on one 60 Hz frame, as read back from a chip's register writes.</summary>
public readonly record struct VoiceState(bool Playing, double Hz, double Volume);

/// <summary>A few bars written by hand for the tests: self-made, so safe to keep in a public repository.</summary>
public sealed record Score(int Frames, IReadOnlyList<Note> Notes)
{
    /// <summary>
    /// Two melodic lines, a bass line and noise hits, inside the range of all three chips, with
    /// rests between notes so starts and stops can be told apart.
    /// </summary>
    public static Score Tune { get; } = new(96,
    [
        new(0, 2, 10, 440.00, 15), new(0, 16, 10, 523.25, 12), new(0, 30, 12, 659.25, 15), new(0, 48, 8, 880.00, 12), new(0, 62, 20, 440.00, 15),
        new(1, 4, 20, 220.00, 12), new(1, 30, 16, 261.63, 12), new(1, 52, 12, 329.63, 15), new(1, 70, 14, 392.00, 12),
        new(2, 2, 22, 130.81, 15), new(2, 30, 20, 164.81, 15), new(2, 56, 10, 196.00, 15), new(2, 72, 16, 146.83, 15),
        new(3, 2, 5, 6991.0, 15), new(3, 16, 5, 1748.0, 12), new(3, 30, 5, 6991.0, 15), new(3, 48, 6, 6991.0, 12), new(3, 70, 8, 1748.0, 15),
    ]);

    public Note? At(int voice, int frame) => Notes.FirstOrDefault(note => note.Voice == voice && frame >= note.StartFrame && frame < note.EndFrame);
}

/// <summary>Writes a <see cref="Score"/> as each chip's log, the way a simple sound driver would.</summary>
public static class ScoreEncoder
{
    private static readonly int[] NesNoisePeriods = [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068];

    public static byte[] Encode(SoundChip chip, Score score) => chip switch
    {
        SoundChip.Nes2A03 => TestVgm.Nes(Frames(score, [(0x4015, 0x0F)], NesStart, NesStop)),
        SoundChip.GameBoyDmg => TestVgm.Dmg(Frames(score, DmgInit(), DmgStart, DmgStop)),
        _ => TestVgm.Sn([.. Frames(score, [], SnStart, SnStop).Select(frame => frame.Select(write => write.Value).ToArray())]),
    };

    private static (int Address, int Value)[][] Frames(
        Score score,
        (int Address, int Value)[] init,
        Func<Note, IEnumerable<(int Address, int Value)>> start,
        Func<int, IEnumerable<(int Address, int Value)>> stop)
    {
        var frames = Enumerable.Range(0, score.Frames).Select(_ => new List<(int Address, int Value)>()).ToArray();
        frames[0].AddRange(init);
        foreach (var note in score.Notes.OrderBy(note => note.StartFrame))
        {
            frames[note.StartFrame].AddRange(start(note));
            frames[note.EndFrame].AddRange(stop(note.Voice));
        }

        return [.. frames.Select(frame => frame.ToArray())];
    }

    private static IEnumerable<(int Address, int Value)> NesStart(Note note)
    {
        if (note.Voice < 2)
        {
            var register = 0x4000 + (note.Voice * 4);
            var timer = (int)Math.Round(TestVgm.NesClock / (16 * note.Hz)) - 1;
            return [(register, 0xB0 | note.Volume), (register + 1, 0x08), (register + 2, timer & 0xFF), (register + 3, 0xF8 | (timer >> 8))];
        }

        if (note.Voice == 2)
        {
            var timer = (int)Math.Round(TestVgm.NesClock / (32 * note.Hz)) - 1;
            return [(0x4008, 0xFF), (0x400A, timer & 0xFF), (0x400B, 0xF8 | (timer >> 8))];
        }

        var index = Enumerable.Range(0, 16).MinBy(candidate => Math.Abs(Math.Log(TestVgm.NesClock / NesNoisePeriods[candidate] / note.Hz)));
        return [(0x400C, 0x30 | note.Volume), (0x400E, index), (0x400F, 0xF8)];
    }

    private static IEnumerable<(int Address, int Value)> NesStop(int voice) => voice switch
    {
        < 2 => [(0x4000 + (voice * 4), 0xB0)],
        2 => [(0x4008, 0x80)],
        _ => [(0x400C, 0x30)],
    };

    private static (int Address, int Value)[] DmgInit()
    {
        // Power on, full volume on both sides, and a triangle in wave RAM.
        var triangle = Enumerable.Range(0, 16).Select(step => 15 - step).Concat(Enumerable.Range(0, 16)).ToArray();
        return
        [
            (0xFF26, 0x80), (0xFF24, 0x77), (0xFF25, 0xFF), (0xFF1A, 0x00),
            .. Enumerable.Range(0, 16).Select(index => (0xFF30 + index, (triangle[index * 2] << 4) | triangle[(index * 2) + 1])),
        ];
    }

    private static IEnumerable<(int Address, int Value)> DmgStart(Note note)
    {
        if (note.Voice < 2)
        {
            var register = note.Voice == 0 ? 0xFF11 : 0xFF16;
            var period = (int)Math.Round(2048 - (131072 / note.Hz));
            return [(register, 0x80), (register + 1, note.Volume << 4), (register + 2, period & 0xFF), (register + 3, 0x80 | (period >> 8))];
        }

        if (note.Voice == 2)
        {
            var period = (int)Math.Round(2048 - (65536 / note.Hz));
            return [(0xFF1A, 0x80), (0xFF1C, 0x20), (0xFF1D, period & 0xFF), (0xFF1E, 0x80 | (period >> 8))];
        }

        var best = Enumerable.Range(0, 14 * 8).MinBy(code => Math.Abs(Math.Log(DmgNoiseRate(code / 8, code % 8) / note.Hz)));
        return [(0xFF21, note.Volume << 4), (0xFF22, ((best / 8) << 4) | (best % 8)), (0xFF23, 0x80)];
    }

    private static IEnumerable<(int Address, int Value)> DmgStop(int voice) => voice switch
    {
        0 => [(0xFF12, 0x00)],
        1 => [(0xFF17, 0x00)],
        2 => [(0xFF1C, 0x00)],
        _ => [(0xFF21, 0x00)],
    };

    private static double DmgNoiseRate(int shift, int divisor) => 262144.0 / ((divisor == 0 ? 0.5 : divisor) * (1 << shift));

    private static IEnumerable<(int Address, int Value)> SnStart(Note note)
    {
        var attenuation = (int)Math.Round(10 * Math.Log10(15.0 / note.Volume));
        if (note.Voice < 3)
        {
            var period = (int)Math.Round(TestVgm.PsgClock / (32 * note.Hz));
            return [(0, 0x80 | (note.Voice << 5) | (period & 0x0F)), (0, period >> 4), (0, 0x90 | (note.Voice << 5) | attenuation)];
        }

        // White noise at the nearest of the three fixed rates.
        var rate = Enumerable.Range(0, 3).MinBy(candidate => Math.Abs(Math.Log(TestVgm.PsgClock / (512 << candidate) / note.Hz)));
        return [(0, 0xE4 | rate), (0, 0xF0 | attenuation)];
    }

    private static IEnumerable<(int Address, int Value)> SnStop(int voice) => [(0, 0x9F | (voice << 5))];
}

/// <summary>
/// Reads what each voice plays, frame by frame, from a chip's register writes. It knows the three
/// chips only as far as the logs the tests deal with need: constant volumes, no envelopes. On the
/// 2A03 it does apply the two rules that silence a voice behind a driver's back: the sweep unit
/// muting a pulse whose target period overflows, and the triangle's linear and length counters.
/// </summary>
public static class ScoreDecoder
{
    private static readonly int[] NesNoisePeriods = [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068];

    /// <summary>For each 60 Hz frame, the four voices after that frame's writes.</summary>
    public static List<VoiceState[]> Decode(SoundChip chip, byte[] vgm) => chip switch
    {
        SoundChip.Nes2A03 => Nes(vgm),
        SoundChip.GameBoyDmg => Dmg(vgm),
        _ => Sn(vgm),
    };

    private static List<VoiceState[]> Nes(byte[] vgm)
    {
        var registers = new int[0x20];
        var keyed = new bool[4];
        var reload = false;
        var linear = 0;
        var length = 0;
        var states = new List<VoiceState[]>();
        foreach (var frame in TestVgm.Frames(vgm))
        {
            foreach (var write in frame)
            {
                registers[write.Register] = write.Value;
                keyed[0] |= write.Register == 0x03;
                keyed[1] |= write.Register == 0x07;
                keyed[3] |= write.Register == 0x0F;
                if (write.Register == 0x0B)
                {
                    reload = true;
                    length = 30;
                }
            }

            // A frame clocks the linear counter four times and the length counter twice.
            var control = (registers[0x08] & 0x80) != 0;
            linear = reload ? registers[0x08] & 0x7F : Math.Max(0, linear - 4);
            reload &= control;
            length = control ? length : Math.Max(0, length - 2);

            var enabled = registers[0x15];
            var triangleTimer = registers[0x0A] | ((registers[0x0B] & 7) << 8);
            states.Add(
            [
                Pulse(registers, 0x00, keyed[0] && (enabled & 1) != 0),
                Pulse(registers, 0x04, keyed[1] && (enabled & 2) != 0),
                new VoiceState(linear > 0 && length > 0 && triangleTimer >= 2 && (enabled & 4) != 0, TestVgm.NesClock / (32.0 * (triangleTimer + 1)), 15),
                new VoiceState(keyed[3] && (registers[0x0C] & 0x0F) > 0 && (enabled & 8) != 0, TestVgm.NesClock / NesNoisePeriods[registers[0x0E] & 0x0F], registers[0x0C] & 0x0F),
            ]);
        }

        return states;

        static VoiceState Pulse(int[] registers, int register, bool live)
        {
            var timer = registers[register + 2] | ((registers[register + 3] & 7) << 8);
            var sweep = registers[register + 1];
            var overflows = (sweep & 0x08) == 0 && timer + (timer >> (sweep & 7)) > 0x7FF;
            var volume = registers[register] & 0x0F;
            return new VoiceState(live && volume > 0 && timer >= 8 && !overflows, TestVgm.NesClock / (16.0 * (timer + 1)), volume);
        }
    }

    private static List<VoiceState[]> Dmg(byte[] vgm)
    {
        var registers = new int[0x30];
        var enabled = new bool[4];
        var volume = new int[4];
        int[] volumeRegisters = [0x02, 0x07, 0x0A, 0x11];
        int[] triggerRegisters = [0x04, 0x09, 0x0E, 0x13];
        var states = new List<VoiceState[]>();
        foreach (var frame in TestVgm.Frames(vgm))
        {
            foreach (var write in frame)
            {
                registers[write.Register] = write.Value;
                for (var channel = 0; channel < 4; channel++)
                {
                    var dacOn = channel == 2 ? (registers[0x0A] & 0x80) != 0 : (registers[volumeRegisters[channel]] & 0xF8) != 0;
                    enabled[channel] &= dacOn;
                    if (write.Register == triggerRegisters[channel] && (write.Value & 0x80) != 0)
                    {
                        enabled[channel] = dacOn;
                        volume[channel] = registers[volumeRegisters[channel]] >> 4;
                    }
                }
            }

            var level = (registers[0x0C] >> 5) & 3;
            var noise = registers[0x12];
            states.Add(
            [
                new VoiceState(enabled[0] && volume[0] > 0, 131072.0 / (2048 - (registers[0x03] | ((registers[0x04] & 7) << 8))), volume[0]),
                new VoiceState(enabled[1] && volume[1] > 0, 131072.0 / (2048 - (registers[0x08] | ((registers[0x09] & 7) << 8))), volume[1]),
                new VoiceState(enabled[2] && level != 0, 65536.0 / (2048 - (registers[0x0D] | ((registers[0x0E] & 7) << 8))), level == 0 ? 0 : 15 >> (level - 1)),
                new VoiceState(enabled[3] && volume[3] > 0, 262144.0 / (((noise & 7) == 0 ? 0.5 : noise & 7) * (1 << (noise >> 4))), volume[3]),
            ]);
        }

        return states;
    }

    private static List<VoiceState[]> Sn(byte[] vgm)
    {
        var periods = new int[3];
        int[] attenuations = [15, 15, 15, 15];
        var noiseControl = 0;
        var latched = 0;
        var states = new List<VoiceState[]>();
        foreach (var frame in TestVgm.Frames(vgm))
        {
            foreach (var write in frame)
            {
                var value = write.Value;
                latched = (value & 0x80) != 0 ? (value >> 4) & 7 : latched;
                var channel = latched >> 1;
                if ((latched & 1) != 0)
                {
                    attenuations[channel] = value & 0x0F;
                }
                else if (channel == 3)
                {
                    noiseControl = value & 7;
                }
                else
                {
                    periods[channel] = (value & 0x80) != 0 ? (periods[channel] & 0x3F0) | (value & 0x0F) : (periods[channel] & 0x00F) | ((value & 0x3F) << 4);
                }
            }

            states.Add(
            [
                Tone(0), Tone(1), Tone(2),
                new VoiceState(attenuations[3] < 15, TestVgm.PsgClock / (512 << (noiseControl & 3)), Linear(attenuations[3])),
            ]);
        }

        return states;

        VoiceState Tone(int channel) => new(attenuations[channel] < 15 && periods[channel] > 1, TestVgm.PsgClock / (32.0 * Math.Max(1, periods[channel])), Linear(attenuations[channel]));

        static double Linear(int attenuation) => attenuation >= 15 ? 0 : 15 * Math.Pow(10, -attenuation / 10.0);
    }
}
