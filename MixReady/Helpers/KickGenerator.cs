using NAudio.Wave;

namespace MixReady.Helpers;

/// <summary>
/// Generates a synthesized kick drum pattern at a given BPM.
/// Produces a punchy, low-frequency kick suitable for DJ intros.
/// </summary>
public static class KickGenerator
{
    /// <summary>
    /// Generate a kick drum pattern as a float sample array.
    /// </summary>
    /// <param name="bpm">Beats per minute.</param>
    /// <param name="bars">Number of bars to generate (4 beats per bar).</param>
    /// <param name="sampleRate">Audio sample rate.</param>
    /// <param name="channels">Number of audio channels.</param>
    /// <returns>Float array of synthesized kick samples.</returns>
    public static float[] GenerateKickPattern(double bpm, int bars, int sampleRate, int channels)
    {
        var beatsPerBar = 4;
        var totalBeats = bars * beatsPerBar;
        var secondsPerBeat = 60.0 / bpm;
        var totalDuration = totalBeats * secondsPerBeat;
        var totalSamples = (int)(totalDuration * sampleRate) * channels;

        var output = new float[totalSamples];

        for (int beat = 0; beat < totalBeats; beat++)
        {
            var beatStartSeconds = beat * secondsPerBeat;
            var beatStartSample = (int)(beatStartSeconds * sampleRate);

            // Each kick lasts ~150ms
            var kickDurationSamples = (int)(0.15 * sampleRate);

            // Build the kick with increasing intensity toward the end
            var intensity = 0.4f + 0.6f * ((float)beat / totalBeats);

            WriteSingleKick(output, beatStartSample, kickDurationSamples, sampleRate, channels, intensity);
        }

        return output;
    }

    /// <summary>
    /// Writes a single synthesized kick drum hit into the output buffer.
    /// The kick is a sine wave that sweeps from a high frequency (~150 Hz) down to a
    /// low thump (~50 Hz) with an exponential amplitude decay -- classic 808-style kick.
    /// </summary>
    private static void WriteSingleKick(
        float[] output,
        int startSample,
        int durationSamples,
        int sampleRate,
        int channels,
        float intensity)
    {
        var startFreq = 150.0;
        var endFreq = 50.0;
        var phase = 0.0;

        for (int i = 0; i < durationSamples; i++)
        {
            var sampleIndex = (startSample + i) * channels;
            if (sampleIndex >= output.Length - (channels - 1))
                break;

            // Time ratio 0..1 through the kick
            var t = (double)i / durationSamples;

            // Frequency sweep: exponential drop from high to low
            var freq = startFreq * Math.Pow(endFreq / startFreq, t);

            // Phase accumulation for smooth frequency change
            phase += 2.0 * Math.PI * freq / sampleRate;

            // Amplitude envelope: fast exponential decay
            var envelope = Math.Exp(-5.0 * t);

            var sample = (float)(Math.Sin(phase) * envelope * intensity);

            // Write to all channels
            for (int ch = 0; ch < channels; ch++)
            {
                output[sampleIndex + ch] += sample;
            }
        }
    }
}
