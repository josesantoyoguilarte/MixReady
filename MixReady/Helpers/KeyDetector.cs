using NAudio.Wave;

namespace MixReady.Helpers;

/// <summary>
/// Detects the musical key (root note) of a track by analyzing the dominant
/// pitch class in the sub-bass region (30–120 Hz).
///
/// This is specifically designed for tuning the 808 bass and kick drum in
/// generated intros so they harmonize with the original track.
///
/// Approach:
/// 1. Read the middle portion of the track (skip intro/outro).
/// 2. Low-pass filter to isolate sub-bass content.
/// 3. Measure energy at each of the 12 pitch classes (C, C#, D, ... B)
///    using a bank of narrow bandpass resonators in the sub-bass octave.
/// 4. The pitch class with the most energy is the root note.
/// 5. Return the sub-bass frequency for that note (octave 1, ~32–62 Hz).
/// </summary>
public static class KeyDetector
{
    /// <summary>
    /// All 12 pitch classes with their base frequencies in octave 1 (sub-bass).
    /// These are the standard A440 tuning frequencies.
    /// </summary>
    private static readonly (string Name, double FreqOctave1)[] PitchClasses =
    {
        ("C",  32.703),
        ("C#", 34.648),
        ("D",  36.708),
        ("D#", 38.891),
        ("E",  41.203),
        ("F",  43.654),
        ("F#", 46.249),
        ("G",  48.999),
        ("G#", 51.913),
        ("A",  55.000),
        ("A#", 58.270),
        ("B",  61.735),
    };

    /// <summary>
    /// Detect the root bass note of a track.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <returns>
    /// A tuple of (noteName, bassFrequency) where bassFrequency is in the
    /// sub-bass octave (~32–62 Hz), suitable for tuning kick/808.
    /// </returns>
    public static (string NoteName, double BassFrequency) Detect(string filePath)
    {
        using var reader = new AudioFileReader(filePath);
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;

        var allSamples = ReadAllSamples(reader);

        // Use the middle 60% of the track for stable content
        var start = (int)(allSamples.Length * 0.20);
        var end = (int)(allSamples.Length * 0.80);
        if (end - start < sampleRate * channels * 4)
        {
            start = 0;
            end = allSamples.Length;
        }
        var segment = allSamples[start..end];

        // Convert to mono
        var mono = ToMono(segment, channels);

        // Measure energy at each pitch class across sub-bass octaves 1 and 2
        var scores = new double[12];

        for (int pc = 0; pc < 12; pc++)
        {
            var freq1 = PitchClasses[pc].FreqOctave1;       // Octave 1: 32–62 Hz
            var freq2 = freq1 * 2.0;                          // Octave 2: 65–124 Hz

            // Measure energy at both octaves (sub-bass + bass)
            var energy1 = MeasurePitchEnergy(mono, sampleRate, freq1);
            var energy2 = MeasurePitchEnergy(mono, sampleRate, freq2);

            // Weight octave 1 (sub) more heavily — it's where the 808/kick lives
            scores[pc] = energy1 * 1.5 + energy2;
        }

        // Find the pitch class with the highest score
        int bestPc = 0;
        for (int i = 1; i < 12; i++)
        {
            if (scores[i] > scores[bestPc])
                bestPc = i;
        }

        return (PitchClasses[bestPc].Name, PitchClasses[bestPc].FreqOctave1);
    }

    /// <summary>
    /// Measures how much energy exists at a specific frequency using the
    /// Goertzel algorithm — essentially a single-bin DFT. This is far more
    /// efficient than a full FFT when we only need 12 specific frequencies.
    /// </summary>
    private static double MeasurePitchEnergy(float[] mono, int sampleRate, double targetFreq)
    {
        // Analyze in windows of ~4 cycles of the target frequency for good resolution
        var windowSize = (int)(sampleRate / targetFreq * 4);
        windowSize = Math.Max(windowSize, 1024);
        var hopSize = windowSize / 2;

        double totalEnergy = 0;
        int windowCount = 0;

        for (int pos = 0; pos + windowSize <= mono.Length; pos += hopSize)
        {
            totalEnergy += GoertzelMagnitude(mono, pos, windowSize, targetFreq, sampleRate);
            windowCount++;
        }

        return windowCount > 0 ? totalEnergy / windowCount : 0;
    }

    /// <summary>
    /// Goertzel algorithm: computes the magnitude of a single frequency bin.
    /// This is equivalent to computing one bin of a DFT but runs in O(N) with
    /// constant memory — perfect for detecting specific pitch classes.
    /// </summary>
    private static double GoertzelMagnitude(float[] samples, int offset, int length, double targetFreq, int sampleRate)
    {
        var k = (int)Math.Round(length * targetFreq / sampleRate);
        var w = 2.0 * Math.PI * k / length;
        var coeff = 2.0 * Math.Cos(w);

        double s0 = 0, s1 = 0, s2 = 0;

        for (int i = 0; i < length; i++)
        {
            s0 = samples[offset + i] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        // Magnitude squared
        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }

    private static float[] ReadAllSamples(AudioFileReader reader)
    {
        var sampleCount = (int)(reader.Length / (reader.WaveFormat.BitsPerSample / 8));
        var buffer = new float[sampleCount];
        var totalRead = 0;
        int read;

        while ((read = reader.Read(buffer, totalRead, Math.Min(4096, buffer.Length - totalRead))) > 0)
        {
            totalRead += read;
            if (totalRead >= buffer.Length) break;
        }

        return buffer[..totalRead];
    }

    private static float[] ToMono(float[] samples, int channels)
    {
        if (channels == 1) return samples;

        var mono = new float[samples.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
                sum += samples[i * channels + ch];
            mono[i] = sum / channels;
        }
        return mono;
    }
}
