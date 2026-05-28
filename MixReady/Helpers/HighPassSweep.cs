namespace MixReady.Helpers;

/// <summary>
/// Companion to <see cref="LowPassSweep"/> for high-pass filter sweeps.
/// Implements a per-block loudness-compensated biquad high-pass whose cutoff
/// rises exponentially from <c>startCutoffHz</c> to <c>endCutoffHz</c>.
/// </summary>
public static class HighPassSweep
{
    public static void Apply(
        float[] samples,
        int sampleRate,
        int channels,
        double startCutoffHz = 20,
        double endCutoffHz = 6000)
    {
        var blockSize = 512 * channels;
        var totalBlocks = (samples.Length + blockSize - 1) / blockSize;

        var x1 = new double[channels];
        var x2 = new double[channels];
        var y1 = new double[channels];
        var y2 = new double[channels];

        for (int block = 0; block < totalBlocks; block++)
        {
            var blockStart = block * blockSize;
            var blockEnd = System.Math.Min(blockStart + blockSize, samples.Length);
            var blockLen = blockEnd - blockStart;

            double preRmsSum = 0;
            for (int i = blockStart; i < blockEnd; i++)
                preRmsSum += samples[i] * (double)samples[i];
            var preRms = System.Math.Sqrt(preRmsSum / System.Math.Max(1, blockLen));

            var t = totalBlocks <= 1 ? 1.0 : (double)block / (totalBlocks - 1);
            var cutoff = startCutoffHz * System.Math.Pow(endCutoffHz / startCutoffHz, t);
            cutoff = System.Math.Min(cutoff, sampleRate * 0.45);

            var (a0, a1, a2, b1, b2) = CalculateHighPassCoefficients(sampleRate, cutoff);

            for (int i = blockStart; i < blockEnd; i++)
            {
                var ch = i % channels;
                double x0 = samples[i];
                double y0 = a0 * x0 + a1 * x1[ch] + a2 * x2[ch] - b1 * y1[ch] - b2 * y2[ch];

                samples[i] = (float)y0;

                x2[ch] = x1[ch]; x1[ch] = x0;
                y2[ch] = y1[ch]; y1[ch] = y0;
            }

            if (preRms > 1e-8)
            {
                double postRmsSum = 0;
                for (int i = blockStart; i < blockEnd; i++)
                    postRmsSum += samples[i] * (double)samples[i];
                var postRms = System.Math.Sqrt(postRmsSum / System.Math.Max(1, blockLen));

                if (postRms > 1e-8)
                {
                    var gain = (float)(preRms / postRms);
                    gain = System.Math.Min(gain, 6.0f);
                    for (int i = blockStart; i < blockEnd; i++)
                    {
                        samples[i] *= gain;
                        samples[i] = System.Math.Clamp(samples[i], -1.0f, 1.0f);
                    }
                }
            }
        }
    }

    /// <summary>Biquad high-pass coefficients (RBJ Audio EQ Cookbook, Q = 0.707).</summary>
    private static (double a0, double a1, double a2, double b1, double b2) CalculateHighPassCoefficients(
        int sampleRate, double cutoffHz)
    {
        double w0 = 2.0 * System.Math.PI * cutoffHz / sampleRate;
        double alpha = System.Math.Sin(w0) / (2.0 * 0.707);

        double cosW0 = System.Math.Cos(w0);
        double b0 = 1.0 + alpha;

        return (
            a0: ((1.0 + cosW0) / 2.0) / b0,
            a1: -(1.0 + cosW0) / b0,
            a2: ((1.0 + cosW0) / 2.0) / b0,
            b1: (-2.0 * cosW0) / b0,
            b2: (1.0 - alpha) / b0
        );
    }
}
