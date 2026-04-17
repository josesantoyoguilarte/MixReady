namespace MixReady.Helpers;

/// <summary>
/// Scans a full track to find the section with the strongest, cleanest drum pattern.
/// 
/// Analysis criteria (scored and combined):
/// 1. Low-frequency energy ratio ÃÂ¢Ã¢âÂ¬Ã¢â¬ drums (kicks) live below 200 Hz; a drum-heavy
///    section has a higher proportion of energy in the bass
/// 2. Transient sharpness ÃÂ¢Ã¢âÂ¬Ã¢â¬ clean drums produce sharp spikes (high peak-to-RMS ratio)
///    while vocals/pads produce sustained energy
/// 3. Beat regularity ÃÂ¢Ã¢âÂ¬Ã¢â¬ in a clean drum section, energy peaks recur at even intervals
///    matching the detected BPM
/// 4. Mid-frequency dip ÃÂ¢Ã¢âÂ¬Ã¢â¬ sections where vocals/melody dominate have strong 300ÃÂ¢Ã¢âÂ¬Ã¢â¬Å4000 Hz
///    energy; drum-only sections have less
/// 
/// The entire track is searched (no arbitrary skipping) so the absolute best
/// drum section is always found regardless of song structure.
/// </summary>
public static class DrumSectionFinder
{
    public static (int startIndex, float[] section) FindBestSection(
        float[] samples,
        int sampleRate,
        int channels,
        double bpm,
        int barsToExtract = 8)
    {
        var beatsPerBar = 4;
        var secondsPerBar = (60.0 / bpm) * beatsPerBar;
        // Use Round instead of truncation to minimize per-bar drift
        var samplesPerBar = (int)Math.Round(secondsPerBar * sampleRate) * channels;
        var extractLength = samplesPerBar * barsToExtract;

        if (samples.Length <= extractLength)
            return (0, (float[])samples.Clone());

        var searchEnd = samples.Length - extractLength;
        var bestScore = double.MinValue;
        var bestStart = 0;

        // Step in bar-sized increments across the entire track
        var step = Math.Max(samplesPerBar, 1);
        for (int pos = 0; pos < searchEnd; pos += step)
        {
            var score = ScoreSection(samples, pos, extractLength, sampleRate, channels, bpm);
            if (score > bestScore)
            {
                bestScore = score;
                bestStart = pos;
            }
        }

        return (bestStart, samples[bestStart..(bestStart + extractLength)]);
    }

    /// <summary>
    /// Multi-criteria scoring of a candidate section.
    /// </summary>
    private static double ScoreSection(
        float[] samples, int start, int length,
        int sampleRate, int channels, double bpm)
    {
        // --- 1. Low-frequency energy ratio ---
        // Extract mono for frequency analysis
        var mono = ExtractMono(samples, start, length, channels);
        var monoRate = sampleRate;

        var lowEnergy = BandEnergy(mono, monoRate, 20, 200);
        var midEnergy = BandEnergy(mono, monoRate, 300, 4000);
        var fullEnergy = BandEnergy(mono, monoRate, 20, 18000);

        if (fullEnergy < 1e-12)
            return 0;

        // High low-to-full ratio = kick-heavy
        var lowRatio = lowEnergy / fullEnergy;

        // Low mid-to-full ratio = less vocals/melody
        var midRatio = midEnergy / fullEnergy;

        // --- 2. Transient sharpness (peak-to-RMS) ---
        var windowSize = (monoRate / 100); // 10ms windows
        var windowCount = mono.Length / windowSize;

        if (windowCount < 4)
            return 0;

        var windowEnergies = new double[windowCount];
        double totalEnergy = 0;
        double peakWindowEnergy = 0;

        for (int w = 0; w < windowCount; w++)
        {
            double energy = 0;
            for (int i = 0; i < windowSize; i++)
            {
                var s = mono[w * windowSize + i];
                energy += s * s;
            }
            energy /= windowSize;
            windowEnergies[w] = energy;
            totalEnergy += energy;
            peakWindowEnergy = Math.Max(peakWindowEnergy, energy);
        }

        var avgEnergy = totalEnergy / windowCount;
        if (avgEnergy < 1e-12)
            return 0;

        var peakToAvg = peakWindowEnergy / avgEnergy;

        // --- 3. Beat regularity ---
        // Expected interval between beats in windows
        var secondsPerBeat = 60.0 / bpm;
        var windowsPerBeat = secondsPerBeat / (1.0 / 100); // 10ms windows = 100 per second
        var beatRegularity = MeasureBeatRegularity(windowEnergies, windowsPerBeat, avgEnergy);

        // --- 4. Overall energy (prefer loud, full sections) ---
        var overallRms = Math.Sqrt(avgEnergy);

        // --- Combine scores ---
        // Weights tuned to prioritize: beat regularity > low freq > transients > penalize mids
        var score =
            beatRegularity * 30.0 +   // Strong regular beat pattern is most important
            lowRatio * 20.0 +          // Kick-heavy sections
            peakToAvg * 1.5 +          // Sharp transients
            overallRms * 10.0 +        // Prefer energetic sections
            (1.0 - midRatio) * 15.0;   // Penalize vocal/melody presence

        return score;
    }

    /// <summary>
    /// Measure how regularly energy peaks align with the expected beat grid.
    /// Returns 0ÃÂ¢Ã¢âÂ¬Ã¢â¬Å1 where 1 = perfect metronomic beat alignment.
    /// </summary>
    private static double MeasureBeatRegularity(double[] windowEnergies, double windowsPerBeat, double avgEnergy)
    {
        if (windowEnergies.Length < 4 || windowsPerBeat < 1)
            return 0;

        // Find windows that are energy peaks (above 1.3ÃÆÃ¢â¬" average)
        var threshold = avgEnergy * 1.3;
        var peakPositions = new List<int>();

        for (int i = 1; i < windowEnergies.Length - 1; i++)
        {
            if (windowEnergies[i] > threshold &&
                windowEnergies[i] >= windowEnergies[i - 1] &&
                windowEnergies[i] >= windowEnergies[i + 1])
            {
                peakPositions.Add(i);
            }
        }

        if (peakPositions.Count < 3)
            return 0;

        // For each peak, check if the distance to the nearest other peak
        // is close to a multiple of the expected beat interval.
        // Tolerance: ÃâÃÂ±15% of a beat
        var tolerance = windowsPerBeat * 0.15;
        int onBeatCount = 0;

        for (int i = 1; i < peakPositions.Count; i++)
        {
            var interval = peakPositions[i] - peakPositions[i - 1];
            // Check if interval is close to 1ÃÆÃ¢â¬", 2ÃÆÃ¢â¬", or 0.5ÃÆÃ¢â¬" the beat
            var ratios = new[] { 0.5, 1.0, 2.0 };
            foreach (var r in ratios)
            {
                var expected = windowsPerBeat * r;
                if (Math.Abs(interval - expected) < tolerance)
                {
                    onBeatCount++;
                    break;
                }
            }
        }

        return (double)onBeatCount / (peakPositions.Count - 1);
    }

    /// <summary>
    /// Extract a mono signal from interleaved samples at the given offset/length.
    /// </summary>
    private static float[] ExtractMono(float[] samples, int start, int length, int channels)
    {
        var monoLength = length / channels;
        var mono = new float[monoLength];

        for (int i = 0; i < monoLength; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
                sum += samples[start + i * channels + ch];
            mono[i] = sum / channels;
        }

        return mono;
    }

    /// <summary>
    /// Estimate energy within a frequency band using a simple bandpass approach.
    /// Applies a high-pass then low-pass biquad filter and measures RMSÃâÃÂ².
    /// </summary>
    private static double BandEnergy(float[] mono, int sampleRate, double lowHz, double highHz)
    {
        // High-pass at lowHz
        var hp = ApplyBiquad(mono, sampleRate, lowHz, highPass: true);
        // Low-pass at highHz
        var bp = ApplyBiquad(hp, sampleRate, highHz, highPass: false);

        double energy = 0;
        for (int i = 0; i < bp.Length; i++)
            energy += bp[i] * (double)bp[i];

        return energy / bp.Length;
    }

    /// <summary>
    /// Simple biquad filter (Butterworth Q=0.707).
    /// </summary>
    private static float[] ApplyBiquad(float[] input, int sampleRate, double freqHz, bool highPass)
    {
        var output = new float[input.Length];
        double w0 = 2.0 * Math.PI * freqHz / sampleRate;
        double alpha = Math.Sin(w0) / (2.0 * 0.707);
        double cosW0 = Math.Cos(w0);
        double norm = 1.0 + alpha;

        double a0, a1, a2, b1, b2;

        if (highPass)
        {
            a0 = ((1.0 + cosW0) / 2.0) / norm;
            a1 = (-(1.0 + cosW0)) / norm;
            a2 = ((1.0 + cosW0) / 2.0) / norm;
        }
        else
        {
            a0 = ((1.0 - cosW0) / 2.0) / norm;
            a1 = (1.0 - cosW0) / norm;
            a2 = ((1.0 - cosW0) / 2.0) / norm;
        }

        b1 = (-2.0 * cosW0) / norm;
        b2 = (1.0 - alpha) / norm;

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
}
