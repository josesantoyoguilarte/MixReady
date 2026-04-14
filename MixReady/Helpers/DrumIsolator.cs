using NAudio.Wave;

namespace MixReady.Helpers;

/// <summary>
/// Isolates drum elements from a full mix using frequency band separation,
/// then gain-matches the result to the original section's loudness.
/// 
/// Strategy:
/// - Keep the low band (&lt;150 Hz) at full volume ? kick drums, sub-bass
/// - Keep the high band (&gt;10 kHz) at full volume ? hi-hats, cymbals, shakers
/// - Completely remove the mid band (150 Hz–10 kHz) ? vocals, melody, synths, snare body
/// - Normalize the isolated output so its RMS matches the original section
/// </summary>
public static class DrumIsolator
{
    /// <summary>
    /// Isolate drum frequencies from the given samples.
    /// The mid band (where vocals/melody live) is completely removed.
    /// The output is gain-matched to the original section's RMS loudness.
    /// </summary>
    public static float[] Isolate(float[] samples, int sampleRate, int channels)
    {
        // Measure original RMS before any processing
        var originalRms = CalculateRms(samples);

        var output = new float[samples.Length];

        // Process each channel independently
        var samplesPerChannel = samples.Length / channels;

        for (int ch = 0; ch < channels; ch++)
        {
            // Extract single channel
            var channelData = new float[samplesPerChannel];
            for (int i = 0; i < samplesPerChannel; i++)
                channelData[i] = samples[i * channels + ch];

            // Keep only lows (kicks) and highs (hats) — zero mids (vocals/melody)
            var lowBand = ApplyBiquadLowPass(channelData, sampleRate, 150.0);
            var highBand = ApplyBiquadHighPass(channelData, sampleRate, 10000.0);

            for (int i = 0; i < samplesPerChannel; i++)
            {
                output[i * channels + ch] = lowBand[i] + highBand[i];
            }
        }

        // Fade in first ~10ms to suppress biquad filter startup transient
        var fadeLen = Math.Min((int)(0.01 * sampleRate) * channels, output.Length);
        for (int i = 0; i < fadeLen; i++)
            output[i] *= (float)i / fadeLen;

        // Gain-match: bring isolated drums up to the same RMS as the original section
        var isolatedRms = CalculateRms(output);
        if (isolatedRms > 1e-8f)
        {
            var gain = originalRms / isolatedRms;
            gain = Math.Min(gain, 6.0f);
            for (int i = 0; i < output.Length; i++)
            {
                output[i] *= gain;
                output[i] = Math.Clamp(output[i], -1.0f, 1.0f);
            }
        }

        return output;
    }

    /// <summary>
    /// Calculate the RMS (Root Mean Square) loudness of a sample buffer.
    /// </summary>
    internal static float CalculateRms(float[] samples)
    {
        if (samples.Length == 0) return 0;

        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i] * (double)samples[i];

        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>
    /// Simple biquad low-pass filter (2nd order Butterworth approximation).
    /// </summary>
    private static float[] ApplyBiquadLowPass(float[] input, int sampleRate, double cutoffHz)
    {
        var output = new float[input.Length];
        var (a0, a1, a2, b1, b2) = CalculateLowPassCoefficients(sampleRate, cutoffHz);

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;

        for (int i = 0; i < input.Length; i++)
        {
            double x0 = input[i];
            double y0 = a0 * x0 + a1 * x1 + a2 * x2 - b1 * y1 - b2 * y2;

            output[i] = (float)y0;

            x2 = x1; x1 = x0;
            y2 = y1; y1 = y0;
        }

        return output;
    }

    /// <summary>
    /// Simple biquad high-pass filter (2nd order Butterworth approximation).
    /// </summary>
    private static float[] ApplyBiquadHighPass(float[] input, int sampleRate, double cutoffHz)
    {
        var output = new float[input.Length];
        var (a0, a1, a2, b1, b2) = CalculateHighPassCoefficients(sampleRate, cutoffHz);

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;

        for (int i = 0; i < input.Length; i++)
        {
            double x0 = input[i];
            double y0 = a0 * x0 + a1 * x1 + a2 * x2 - b1 * y1 - b2 * y2;

            output[i] = (float)y0;

            x2 = x1; x1 = x0;
            y2 = y1; y1 = y0;
        }

        return output;
    }

    /// <summary>
    /// Biquad low-pass coefficients (Robert Bristow-Johnson's Audio EQ Cookbook).
    /// </summary>
    private static (double a0, double a1, double a2, double b1, double b2) CalculateLowPassCoefficients(
        int sampleRate, double cutoffHz)
    {
        double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        double alpha = Math.Sin(w0) / (2.0 * 0.707); // Q = 0.707 (Butterworth)

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

    /// <summary>
    /// Biquad high-pass coefficients (Robert Bristow-Johnson's Audio EQ Cookbook).
    /// </summary>
    private static (double a0, double a1, double a2, double b1, double b2) CalculateHighPassCoefficients(
        int sampleRate, double cutoffHz)
    {
        double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        double alpha = Math.Sin(w0) / (2.0 * 0.707);

        double cosW0 = Math.Cos(w0);
        double b0 = 1.0 + alpha;

        return (
            a0: ((1.0 + cosW0) / 2.0) / b0,
            a1: (-(1.0 + cosW0)) / b0,
            a2: ((1.0 + cosW0) / 2.0) / b0,
            b1: (-2.0 * cosW0) / b0,
            b2: (1.0 - alpha) / b0
        );
    }
}
