using NAudio.Wave;

namespace MixReady.Helpers;

/// <summary>
/// Detects BPM from an audio file using a multi-strategy approach:
///
/// 1. Trims to the middle 70% to skip irregular intros/outros.
/// 2. Filters to the percussive range (60–200 Hz) where kick/bass dominate,
///    removing melodic and harmonic content that confuses tempo detection.
/// 3. Computes an onset-strength signal from half-wave rectified spectral flux.
/// 4. Runs autocorrelation on the onset signal.
/// 5. Finds the FIRST significant peak in the autocorrelation (not the global
///    max) — in music, the first significant peak in the autocorrelation of
///    an onset signal corresponds to the actual beat period, while later/higher
///    peaks are sub-harmonics (half-time, phrase-level patterns).
/// 6. Uses parabolic interpolation for sub-frame accuracy.
/// 7. Applies octave/multiple correction with genre-aware ranges.
/// </summary>
public static class BpmDetector
{
    private const double MinBpm = 60;
    private const double MaxBpm = 200;
    private const double DefaultBpm = 120;
    private const double TrimPercent = 0.15;

    private static readonly Dictionary<string, (double Low, double High)> GenreBpmRanges
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Reggaeton"]   = (85, 105),
        ["Reparto"]     = (90, 115),
        ["Dembow"]      = (115, 140),
        ["House"]       = (118, 132),
        ["Techno"]      = (125, 150),
        ["Drum & Bass"] = (160, 180),
        ["Hip-Hop"]     = (75, 110),
        ["Pop"]         = (100, 130),
        ["R&B"]         = (60, 100),
        ["Trance"]      = (128, 145),
        ["Dubstep"]     = (135, 145),
        ["Disco/Funk"]  = (110, 130),
        ["Salsa"]       = (80, 110),
        ["Merengue"]    = (120, 160),
        ["Bachata"]     = (125, 145),
        ["Cumbia"]      = (80, 110),
        ["Vallenato"]   = (110, 150),
    };

    public static double Detect(string filePath) => Detect(filePath, genre: null);

    public static double Detect(string filePath, string? genre)
    {
        using var reader = new AudioFileReader(filePath);
        var mono = reader.ToMono();
        var sampleRate = mono.WaveFormat.SampleRate;

        var allSamples = ReadAllSamples(mono);

        if (allSamples.Length < sampleRate * 4)
            return DefaultBpm;

        var samples = TrimToMiddle(allSamples);

        // --- Filter to percussive/bass range (60–200 Hz) ---
        // This removes vocals, melodies, and high-freq content that create
        // false onset peaks, leaving primarily kick drum and bass hits.
        var percussive = BandPassFilter(samples, sampleRate, 60.0, 200.0);

        // --- Build onset-strength signal ---
        var frameSize = Math.Max(512, NextPowerOfTwo(sampleRate / 43)); // ~23ms
        var hopSize = Math.Max(128, sampleRate / 200);                 // ~5ms hop
        var onsetSignal = ComputeOnsetStrength(percussive, frameSize, hopSize);

        if (onsetSignal.Length < 2)
            return DefaultBpm;

        var hopsPerSecond = (double)sampleRate / hopSize;

        // --- Autocorrelation: find first significant peak ---
        var rawBpm = FirstPeakAutocorrelationBpm(onsetSignal, hopsPerSecond);

        // --- Octave / multiple correction ---
        (double Low, double High)? genreRange = null;
        if (!string.IsNullOrWhiteSpace(genre) && GenreBpmRanges.TryGetValue(genre, out var range))
            genreRange = range;

        var bpm = CorrectOctaveErrors(rawBpm, onsetSignal, hopsPerSecond, genreRange);

        return Math.Clamp(Math.Round(bpm), MinBpm, MaxBpm);
    }

    // ---------------------------------------------------------------
    // Band-pass filter (isolate percussive range)
    // ---------------------------------------------------------------

    /// <summary>
    /// Simple two-pass filter: high-pass at lowCut, then low-pass at highCut.
    /// Isolates the kick drum / bass region to prevent melodic content from
    /// creating false onset detections.
    /// </summary>
    private static float[] BandPassFilter(float[] input, int sampleRate, double lowCut, double highCut)
    {
        var hp = ApplyBiquad(input, sampleRate, lowCut, highPass: true);
        return ApplyBiquad(hp, sampleRate, highCut, highPass: false);
    }

    private static float[] ApplyBiquad(float[] input, int sampleRate, double freqHz, bool highPass)
    {
        var output = new float[input.Length];
        double w0 = 2.0 * Math.PI * freqHz / sampleRate;
        double alpha = Math.Sin(w0) / (2.0 * 0.707);
        double cosW0 = Math.Cos(w0);
        double norm = 1.0 + alpha;

        double a0, a1, a2;
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

        double b1 = (-2.0 * cosW0) / norm;
        double b2 = (1.0 - alpha) / norm;

        double x1d = 0, x2d = 0, y1d = 0, y2d = 0;
        for (int i = 0; i < input.Length; i++)
        {
            double x0 = input[i];
            double y0 = a0 * x0 + a1 * x1d + a2 * x2d - b1 * y1d - b2 * y2d;
            output[i] = (float)y0;
            x2d = x1d; x1d = x0;
            y2d = y1d; y1d = y0;
        }

        return output;
    }

    // ---------------------------------------------------------------
    // Onset-strength signal (transient detection)
    // ---------------------------------------------------------------

    /// <summary>
    /// Computes onset strength using adaptive peak-picking transient detection.
    ///
    /// Unlike simple energy flux (which misses transients at constant energy),
    /// this measures the ratio of instantaneous energy to the local average.
    /// A sharp hit (transient) creates a high peak-to-mean ratio even if the
    /// overall energy level hasn't changed. The result is half-wave rectified
    /// and normalized.
    /// </summary>
    private static double[] ComputeOnsetStrength(float[] samples, int frameSize, int hopSize)
    {
        var frameCount = (samples.Length - frameSize) / hopSize;
        if (frameCount < 2)
            return Array.Empty<double>();

        // Pass 1: compute energy per frame
        var energies = new double[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            var offset = f * hopSize;
            double energy = 0;
            for (int i = 0; i < frameSize; i++)
            {
                var s = samples[offset + i];
                energy += s * (double)s;
            }
            energies[f] = energy / frameSize;
        }

        // Pass 2: compute onset strength as peak-to-local-mean ratio
        // with a lookback window of ~100ms for the local average.
        var lookback = Math.Max(3, frameCount / 100);
        var onset = new double[frameCount];

        for (int f = 1; f < frameCount; f++)
        {
            // Local mean over the lookback window
            double localMean = 0;
            var windowStart = Math.Max(0, f - lookback);
            for (int j = windowStart; j < f; j++)
                localMean += energies[j];
            localMean /= (f - windowStart);

            // Spectral flux (positive only)
            var flux = energies[f] - energies[f - 1];
            if (flux <= 0) { onset[f] = 0; continue; }

            // Peak-to-mean ratio amplifies true transients over sustained energy
            var ratio = localMean > 1e-12 ? energies[f] / localMean : 0;

            // Combine: flux detects changes, ratio detects sharpness
            onset[f] = flux * Math.Max(1.0, ratio);
        }

        return onset;
    }

    // ---------------------------------------------------------------
    // First-peak autocorrelation
    // ---------------------------------------------------------------

    /// <summary>
    /// Finds the BPM by picking the FIRST significant peak in the
    /// autocorrelation, not the global maximum.
    ///
    /// Why: in music, the autocorrelation of an onset signal has peaks at
    /// the beat period, the half-bar (2 beats), the full bar (4 beats), etc.
    /// The global maximum is often at a sub-harmonic (half-time or bar-level)
    /// because longer periods accumulate more structural similarity.
    /// The FIRST significant peak corresponds to the actual beat period.
    ///
    /// "Significant" = a local maximum that is at least 40% of the global
    /// peak's height. This threshold is low enough to catch the beat peak
    /// even in tracks with strong phrase-level patterns.
    /// </summary>
    private static double FirstPeakAutocorrelationBpm(double[] signal, double hopsPerSecond)
    {
        double mean = 0;
        for (int i = 0; i < signal.Length; i++) mean += signal[i];
        mean /= signal.Length;

        var centered = new double[signal.Length];
        for (int i = 0; i < signal.Length; i++)
            centered[i] = signal[i] - mean;

        var minLag = (int)(hopsPerSecond * 60.0 / MaxBpm);
        var maxLag = (int)(hopsPerSecond * 60.0 / MinBpm);
        maxLag = Math.Min(maxLag, signal.Length / 2);

        if (minLag >= maxLag || minLag < 1)
            return DefaultBpm;

        // Compute autocorrelation
        var correlations = new double[maxLag + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double c = 0;
            var limit = signal.Length - lag;
            for (int i = 0; i < limit; i++)
                c += centered[i] * centered[i + lag];
            correlations[lag] = c / limit;
        }

        // Find global peak value
        double globalPeak = double.MinValue;
        for (int lag = minLag; lag <= maxLag; lag++)
            globalPeak = Math.Max(globalPeak, correlations[lag]);

        if (globalPeak <= 0)
            return DefaultBpm;

        // Find the FIRST significant local maximum
        // (significant = at least 40% of global peak, and a local max)
        var threshold = globalPeak * 0.40;

        for (int lag = minLag + 1; lag < maxLag; lag++)
        {
            if (correlations[lag] >= threshold &&
                correlations[lag] >= correlations[lag - 1] &&
                correlations[lag] >= correlations[lag + 1])
            {
                // Found the first significant peak — this is likely the beat period
                var interpolatedLag = ParabolicInterpolation(correlations, lag, minLag, maxLag);
                return 60.0 * hopsPerSecond / interpolatedLag;
            }
        }

        // Fallback: use global peak if no clear first peak found
        int bestLag = minLag;
        for (int lag = minLag + 1; lag <= maxLag; lag++)
        {
            if (correlations[lag] > correlations[bestLag])
                bestLag = lag;
        }

        var fallbackLag = ParabolicInterpolation(correlations, bestLag, minLag, maxLag);
        return 60.0 * hopsPerSecond / fallbackLag;
    }

    private static double ParabolicInterpolation(double[] corr, int peak, int minLag, int maxLag)
    {
        if (peak <= minLag || peak >= maxLag)
            return peak;

        var yMinus = corr[peak - 1];
        var y0 = corr[peak];
        var yPlus = corr[peak + 1];

        var denominator = yMinus - 2.0 * y0 + yPlus;
        if (Math.Abs(denominator) < 1e-20)
            return peak;

        var shift = 0.5 * (yMinus - yPlus) / denominator;
        return peak + Math.Clamp(shift, -0.5, 0.5);
    }

    // ---------------------------------------------------------------
    // Octave / multiple correction
    // ---------------------------------------------------------------

    private static double CorrectOctaveErrors(
        double rawBpm,
        double[] onsetSignal,
        double hopsPerSecond,
        (double Low, double High)? genreRange = null)
    {
        var multipliers = new[] { 0.5, 2.0 / 3.0, 1.0, 3.0 / 2.0, 2.0 };
        var candidates = new List<double>();
        foreach (var m in multipliers)
        {
            var c = rawBpm * m;
            if (c >= MinBpm && c <= MaxBpm)
                candidates.Add(c);
        }

        if (candidates.Count == 0)
            return rawBpm;

        double mean = 0;
        for (int i = 0; i < onsetSignal.Length; i++) mean += onsetSignal[i];
        mean /= onsetSignal.Length;

        var centered = new double[onsetSignal.Length];
        for (int i = 0; i < onsetSignal.Length; i++)
            centered[i] = onsetSignal[i] - mean;

        var scored = new List<(double bpm, double correlation)>();

        foreach (var candidate in candidates)
        {
            var lag = (int)Math.Round(hopsPerSecond * 60.0 / candidate);
            if (lag <= 0 || lag >= onsetSignal.Length / 2)
                continue;

            double correlation = 0;
            var limit = onsetSignal.Length - lag;
            for (int i = 0; i < limit; i++)
                correlation += centered[i] * centered[i + lag];
            correlation /= limit;

            scored.Add((candidate, correlation));
        }

        if (scored.Count == 0)
            return rawBpm;

        // --- Genre-authoritative path ---
        // When the user explicitly provides a genre, we trust it completely.
        // Find the candidate in the genre's BPM range with the best correlation.
        // We do NOT require correlation > 0, because mean-centered autocorrelation
        // can produce negative values at valid beat lags — the user told us the
        // genre, so we pick the best candidate in range regardless.
        if (genreRange.HasValue)
        {
            (double bpm, double correlation)? bestInRange = null;
            foreach (var (candidate, correlation) in scored)
            {
                if (candidate >= genreRange.Value.Low && candidate <= genreRange.Value.High)
                {
                    if (!bestInRange.HasValue || correlation > bestInRange.Value.correlation)
                        bestInRange = (candidate, correlation);
                }
            }
            if (bestInRange.HasValue)
                return bestInRange.Value.bpm;
        }

        // --- Generic path ---
        // Strongly prefer the sweet spot (85-160 BPM). A candidate in the sweet
        // spot always beats a candidate outside it, regardless of raw correlation,
        // because sub-60/super-160 BPM results are almost always wrong octaves.
        var peakCorrelation = scored.Max(s => s.correlation);
        var minCorrelation = scored.Min(s => s.correlation);
        // Offset to make all correlations positive for scoring math
        var corrOffset = minCorrelation < 0 ? Math.Abs(minCorrelation) + 1e-6 : 0;

        double bestScore = double.MinValue;
        double bestBpm = rawBpm;

        foreach (var (candidate, correlation) in scored)
        {
            // Shift correlation to positive range so the math works
            var posCorr = correlation + corrOffset;
            double score = posCorr;

            var inSweetSpot = candidate >= 85 && candidate <= 160;

            if (inSweetSpot)
            {
                // Proximity to 110 BPM center (the most common DJ tempo)
                var distFromCenter = Math.Abs(candidate - 110) / 50.0;
                var proximityBonus = 1.0 + 0.4 * (1.0 - distFromCenter);
                // The large additive term ensures ANY sweet-spot candidate
                // outscores any non-sweet-spot candidate.
                score = posCorr * proximityBonus + (peakCorrelation + corrOffset) * 2.0;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestBpm = candidate;
            }
        }

        return bestBpm;
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static float[] ReadAllSamples(ISampleProvider provider)
    {
        var buffer = new float[provider.WaveFormat.SampleRate * 600];
        var totalRead = 0;
        int read;

        while ((read = provider.Read(buffer, totalRead, Math.Min(4096, buffer.Length - totalRead))) > 0)
        {
            totalRead += read;
            if (totalRead >= buffer.Length)
                break;
        }

        return buffer[..totalRead];
    }

    private static float[] TrimToMiddle(float[] samples)
    {
        var start = (int)(samples.Length * TrimPercent);
        var end = (int)(samples.Length * (1.0 - TrimPercent));

        if (end - start < samples.Length / 4)
            return samples;

        return samples[start..end];
    }

    private static int NextPowerOfTwo(int v)
    {
        v--;
        v |= v >> 1; v |= v >> 2; v |= v >> 4;
        v |= v >> 8; v |= v >> 16;
        return v + 1;
    }
}

/// <summary>
/// Extension to convert stereo to mono by averaging channels.
/// </summary>
internal static class SampleProviderExtensions
{
    public static ISampleProvider ToMono(this ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 1)
            return source;

        return new ManualStereoToMonoProvider(source);
    }
}

/// <summary>
/// Converts a stereo ISampleProvider to mono by averaging left and right channels.
/// </summary>
internal class ManualStereoToMonoProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public ManualStereoToMonoProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var stereoBuffer = new float[count * 2];
        var samplesRead = _source.Read(stereoBuffer, 0, count * 2);
        var monoSamples = samplesRead / 2;

        for (int i = 0; i < monoSamples; i++)
        {
            buffer[offset + i] = (stereoBuffer[i * 2] + stereoBuffer[i * 2 + 1]) / 2f;
        }

        return monoSamples;
    }
}
