using System.Numerics;

namespace SoulConverter.Analysis;

/// <summary>The few signal-processing primitives the sample analysis and the reports need.</summary>
internal static class Dsp
{
    /// <summary>A Hann window of <paramref name="size"/> points, zero at both ends.</summary>
    public static double[] Hanning(int size)
    {
        var window = new double[size];
        if (size == 1)
        {
            window[0] = 1.0;
            return window;
        }

        for (var n = 0; n < size; n++)
        {
            window[n] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * n / (size - 1)));
        }

        return window;
    }

    /// <summary>
    /// The magnitudes of the real FFT of <paramref name="block"/> (a power of two long): bins
    /// 0..size/2.
    /// </summary>
    public static double[] RealFftMagnitudes(ReadOnlySpan<double> block)
    {
        var size = block.Length;
        var data = new Complex[size];
        for (var index = 0; index < size; index++)
        {
            data[index] = block[index];
        }

        Fft(data);
        var magnitudes = new double[(size / 2) + 1];
        for (var index = 0; index < magnitudes.Length; index++)
        {
            magnitudes[index] = data[index].Magnitude;
        }

        return magnitudes;
    }

    /// <summary>The moving average of <paramref name="values"/>, the same length, zero-padded at the ends.</summary>
    public static float[] MovingAverageSame(ReadOnlySpan<float> values, int window)
    {
        // The centre of a "same" convolution with a box of `window` ones.
        var output = new float[Math.Max(values.Length, window)];
        var total = values.Length + window - 1;
        var offset = (total - output.Length) / 2;
        var scale = 1f / window;
        for (var index = 0; index < output.Length; index++)
        {
            var full = index + offset;
            var first = Math.Max(0, full - window + 1);
            var last = Math.Min(values.Length - 1, full);
            var sum = 0.0;
            for (var k = first; k <= last; k++)
            {
                sum += values[k] * scale;
            }

            output[index] = (float)sum;
        }

        return output;
    }

    /// <summary>The moving average of <paramref name="values"/> where the window fits entirely.</summary>
    public static float[] MovingAverageValid(ReadOnlySpan<float> values, int window)
    {
        var output = new float[values.Length - window + 1];
        var scale = 1f / window;
        for (var index = 0; index < output.Length; index++)
        {
            var sum = 0.0;
            for (var k = 0; k < window; k++)
            {
                sum += values[index + k] * scale;
            }

            output[index] = (float)sum;
        }

        return output;
    }

    /// <summary><paramref name="count"/> evenly spaced values from <paramref name="start"/> to <paramref name="stop"/>, both included.</summary>
    public static double[] Linspace(double start, double stop, int count)
    {
        var values = new double[count];
        if (count == 1)
        {
            values[0] = start;
            return values;
        }

        var step = (stop - start) / (count - 1);
        for (var index = 0; index < count; index++)
        {
            values[index] = start + (index * step);
        }

        values[^1] = stop;
        return values;
    }

    /// <summary>Linear interpolation of <paramref name="values"/> (at 0, 1, 2...) at <paramref name="position"/>.</summary>
    public static double Interpolate(ReadOnlySpan<float> values, double position)
    {
        if (position <= 0)
        {
            return values[0];
        }

        if (position >= values.Length - 1)
        {
            return values[^1];
        }

        var index = (int)Math.Floor(position);
        var slope = (double)values[index + 1] - values[index];
        return (slope * (position - index)) + values[index];
    }

    public static double Mean(ReadOnlySpan<double> values)
    {
        var sum = 0.0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum / values.Length;
    }

    private static void Fft(Complex[] data)
    {
        var size = data.Length;
        for (int i = 1, j = 0; i < size; i++)
        {
            var bit = size >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        for (var length = 2; length <= size; length <<= 1)
        {
            var angle = -2.0 * Math.PI / length;
            var half = length / 2;
            var roots = new Complex[half];
            for (var k = 0; k < half; k++)
            {
                roots[k] = new Complex(Math.Cos(angle * k), Math.Sin(angle * k));
            }

            for (var start = 0; start < size; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var even = data[start + k];
                    var odd = data[start + k + half] * roots[k];
                    data[start + k] = even + odd;
                    data[start + k + half] = even - odd;
                }
            }
        }
    }
}
