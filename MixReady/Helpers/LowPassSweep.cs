namespace MixReady.Helpers;

/// <summary>
/// Applies a loudness-compensated low-pass filter sweep to audio samples.
/// 
/// The filter starts with a low cutoff frequency (muffled, bassy sound) and
/// gradually "opens up" to full frequency over the duration of the input.
/// 
/// Crucially, each block is gain-compensated so the perceived loudness stays
/// constant throughout the sweep -- the intro starts at the same volume as the
/// middle of the track. Only the frequency content changes, not the energy.
/// </summary>
public static class LowPassSweep
{
    /// <summary>
    /// Apply a loudness-compensated low-pass filter sweep.
    /// </summary>
    /// <param name="samples">Interleaved audio samples (modified in place).</param>
    /// <param name="sampleRate">Sample rate.</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="startCutoffHz">Starting cutoff frequency (default 250 Hz).</param>
    /// <param name="endCutoffHz">Ending cutoff frequency (default 18000 Hz -- fully open).</param>
    public static void Apply(
        float[] samples,
        int sampleRate,
        int channels,
        double startCutoffHz = 250,
        double endCutoffHz = 18000)
    {
        var blockSize = 512 * channels;
        var totalBlocks = (samples.Length + blockSize - 1) / blockSize;

        // Per-channel filter state
        var x1 = new double[channels];
        var x2 = new double[channels];
        var y1 = new double[channels];
        var y2 = new double[channels];

        for (int block = 0; block < totalBlocks; block++)
        {
            var blockStart = block * blockSize;
            var blockEnd = Math.Min(blockStart + blockSize, samples.Length);
            var blockLen = blockEnd - blockStart;

            // Measure pre-filter RMS of this block
            double preRmsSum = 0;
            for (int i = blockStart; i < blockEnd; i++)
                preRmsSum += samples[i] * (double)samples[i];
            var preRms = Math.Sqrt(preRmsSum / blockLen);

            // Sweep ratio: 0 ? 1
            var t = (double)block / totalBlocks;

            // Exponential sweep (logarithmic in frequency, natural to hearing)
            var cutoff = startCutoffHz * Math.Pow(endCutoffHz / startCutoffHz, t);
            cutoff = Math.Min(cutoff, sampleRate * 0.45);

            var (a0, a1, a2, b1, b2) = CalculateLowPassCoefficients(sampleRate, cutoff);

            // Apply filter to this block
            for (int i = blockStart; i < blockEnd; i++)
            {
                var ch = i % channels;
                double x0 = samples[i];
                double y0 = a0 * x0 + a1 * x1[ch] + a2 * x2[ch] - b1 * y1[ch] - b2 * y2[ch];

                samples[i] = (float)y0;

                x2[ch] = x1[ch]; x1[ch] = x0;
                y2[ch] = y1[ch]; y1[ch] = y0;
            }

            // Measure post-filter RMS and apply gain compensation
            if (preRms > 1e-8)
            {
                double postRmsSum = 0;
                for (int i = blockStart; i < blockEnd; i++)
                    postRmsSum += samples[i] * (double)samples[i];
                var postRms = Math.Sqrt(postRmsSum / blockLen);

                if (postRms > 1e-8)
                {
                    var gain = (float)(preRms / postRms);
                    gain = Math.Min(gain, 6.0f); // Safety cap

                    for (int i = blockStart; i < blockEnd; i++)
                    {
                        samples[i] *= gain;
                        samples[i] = Math.Clamp(samples[i], -1.0f, 1.0f);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Biquad low-pass coefficients (Robert Bristow-Johnson's Audio EQ Cookbook).
    /// Q = 0.707 for Butterworth response (no resonance peak).
    /// </summary>
    private static (double a0, double a1, double a2, double b1, double b2) CalculateLowPassCoefficients(
        int sampleRate, double cutoffHz)
    {
        double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        double alpha = Math.Sin(w0) / (2.0 * 0.707);

        double cosW0 = Math.Cos(w0);
        double b0 = 1.0 + alpha;

        return (
            a0: ((1.0 - cosW0) / 2.0) / b0,
            a1: (1.0 - cosW0) / b0,
            a2: ((1.0 - cosW0) / 2.0) / b0,
            b1: (-2.0 * cosW0) / b0,
            b2: (1.0 - alpha) / b0
        );
    }
}
