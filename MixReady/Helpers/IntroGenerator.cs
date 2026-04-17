using NAudio.Wave;

namespace MixReady.Helpers;

/// <summary>
/// Generates a professional DJ-friendly intro for a track.
///
/// Hybrid Python + C# pipeline:
///   Python (librosa): BPM, key, beats, downbeats, bars, vocal entry, structure
///   C# (NAudio):      drum pattern synthesis, crossfade mixing, normalization
///
/// If Python is not available, falls back to the C# analysis pipeline.
/// </summary>
public static class IntroGenerator
{
    /// <summary>
    /// Generate a beat-driven DJ intro.
    /// 
    /// Two modes:
    ///   useGrooveExtraction=true  (default): Extract the track's own drum groove,
    ///     isolate it, reinforce kick/clap, loop it, add riser. Result sounds like
    ///     the same track -- same swing, same sound design, pro DJ edit quality.
    ///   
    ///   useGrooveExtraction=false: Synthesize drums from scratch using the
    ///     genre-specific pattern engine. Generic but works for any track.
    /// </summary>
    public static (double bpm, string genre, string key) Generate(
        string inputPath,
        string outputPath,
        int introBars = 8,
        int crossfadeBars = 2,
        string? genreOverride = null,
        bool useGrooveExtraction = true,
        int extractBars = 8,
        bool loop = false,
        bool introOnly = false,
        bool skipOriginalIntro = false,
        string? stemsDirectory = null,
        string[]? selectedStems = null,
        double? regionStartSeconds = null,
        double? regionEndSeconds = null,
        double? songStartSeconds = null)
    {
        // Step 1: Analyze the track
        var analysis = AnalyzeTrack(inputPath, genreOverride);

        var bpm = analysis.Bpm;
        var genre = analysis.Genre;
        var keyName = analysis.Key;
        var bassFreq = analysis.BassFrequency;
        var crossfadeStartSeconds = analysis.RecommendedCrossfadeStart;

        // Step 2: Read the full track
        using var reader = new AudioFileReader(inputPath);
        var format = reader.WaveFormat;
        var originalSamples = ReadAllSamples(reader);

        // Step 3: Measure the target loudness and bass energy
        var targetRms = MeasurePeakRms(originalSamples, format.SampleRate, format.Channels);
        var originalBassRms = MeasureBassRms(originalSamples, format.SampleRate, format.Channels);

        // Step 4: Build the intro
        // If loop=true, the extracted section is repeated to double its length.
        // If loop=false, just use the extracted bars directly.
        var actualIntroBars = loop ? extractBars * 2 : extractBars;

        float[] drumIntro;

        // If stems are available and specific stems selected, build intro from those stems
        if (!string.IsNullOrEmpty(stemsDirectory) && selectedStems != null && selectedStems.Length > 0
            && Directory.Exists(stemsDirectory))
        {
            drumIntro = BuildIntroFromSelectedStems(
                stemsDirectory, selectedStems,
                format.SampleRate, format.Channels,
                bpm, actualIntroBars, extractBars, loop,
                regionStartSeconds, regionEndSeconds);

            // Fallback if stem-based intro is silent
            var stemRms = DrumIsolator.CalculateRms(drumIntro);
            if (stemRms < targetRms * 0.05f)
            {
                drumIntro = GrooveExtractor.BuildIntroFromGroove(
                    inputPath, originalSamples, format.SampleRate, format.Channels,
                    bpm, bassFreq, actualIntroBars, extractBars, loop);
            }
        }
        else if (useGrooveExtraction)
        {
            drumIntro = GrooveExtractor.BuildIntroFromGroove(
                inputPath,
                originalSamples, format.SampleRate, format.Channels,
                bpm, bassFreq, actualIntroBars, extractBars, loop);

            // Sanity check: if extraction is near-silent, fall back to synth
            var introRms = DrumIsolator.CalculateRms(drumIntro);
            if (introRms < targetRms * 0.05f)
            {
                drumIntro = DrumPatternGenerator.Generate(
                    bpm, actualIntroBars, format.SampleRate, format.Channels, genre, bassFreq);
            }
        }
        else
        {
            drumIntro = DrumPatternGenerator.Generate(
                bpm, actualIntroBars, format.SampleRate, format.Channels, genre, bassFreq);
        }

        // Step 5: Normalize to match the original track
        NormalizeToTarget(drumIntro, targetRms);
        MatchBassLevel(drumIntro, format.SampleRate, format.Channels, originalBassRms);

        // Step 6: Write output
        float[] finalOutput;

        if (introOnly)
        {
            // Just the intro (extracted section, looped if requested)
            finalOutput = drumIntro;
        }
        else
        {
            var secondsPerBar = (60.0 / bpm) * 4;
            var crossfadeSeconds = secondsPerBar * crossfadeBars;

            int crossfadeStartSample;
            if (songStartSeconds.HasValue)
            {
                // User explicitly set where the song should start
                crossfadeStartSample = (int)(songStartSeconds.Value * format.SampleRate) * format.Channels;
            }
            else if (skipOriginalIntro)
            {
                // Skip intro, no explicit song start -- use recommended point
                crossfadeStartSample = (int)(crossfadeStartSeconds * format.SampleRate) * format.Channels;
            }
            else
            {
                // Keep the full original song -- crossfade at the very beginning
                crossfadeStartSample = 0;
            }

            finalOutput = CombineWithCrossfadeAtPosition(
                drumIntro,
                originalSamples,
                format.SampleRate,
                format.Channels,
                crossfadeSeconds,
                crossfadeStartSample);
        }

        // Step 7: Write output
        using var writer = new WaveFileWriter(outputPath, format);
        writer.WriteSamples(finalOutput, 0, finalOutput.Length);

        return (bpm, genre, keyName);
    }

    // -----------------------------------------------------------------
    // Analysis (Python primary, C# fallback)
    // -----------------------------------------------------------------

    private record UnifiedAnalysis(
        double Bpm,
        string Genre,
        string Key,
        double BassFrequency,
        double RecommendedCrossfadeStart);

    private static UnifiedAnalysis AnalyzeTrack(string inputPath, string? genreOverride)
    {
        // --- Try Python (librosa) first ---
        if (PythonAnalyzer.IsAvailable())
        {
            try
            {
                var py = PythonAnalyzer.AnalyzeAsync(inputPath, genreOverride)
                    .GetAwaiter().GetResult();

                return new UnifiedAnalysis(
                    Bpm: py.Bpm,
                    Genre: !string.IsNullOrWhiteSpace(genreOverride) ? genreOverride : py.Genre,
                    Key: py.Key,
                    BassFrequency: py.BassFrequency,
                    RecommendedCrossfadeStart: py.RecommendedCrossfadeStart);
            }
            catch
            {
                // Python failed - fall through to C# fallback
            }
        }

        // --- C# fallback ---
        string? resolved = null;
        if (!string.IsNullOrWhiteSpace(genreOverride))
        {
            resolved = GenreAnalyzer.SupportedGenres
                .FirstOrDefault(g => g.Equals(genreOverride, StringComparison.OrdinalIgnoreCase))
                ?? genreOverride;
        }

        var bpm = BpmDetector.Detect(inputPath, resolved);
        var genre = resolved ?? GenreAnalyzer.Analyze(inputPath, bpm);
        var (keyName, bassFreq) = KeyDetector.Detect(inputPath);

        double crossfadeStart = 0;
        try
        {
            using var r = new AudioFileReader(inputPath);
            var fmt = r.WaveFormat;
            var smp = ReadAllSamples(r);
            var ta = TrackAnalyzer.Analyze(smp, fmt.SampleRate, fmt.Channels, bpm);
            crossfadeStart = ta.RecommendedCrossfadeStart / (double)(fmt.SampleRate * fmt.Channels);
        }
        catch { }

        return new UnifiedAnalysis(bpm, genre, keyName, bassFreq, crossfadeStart);
    }

    // -----------------------------------------------------------------
    // Loudness & bass matching
    // -----------------------------------------------------------------

    private static float MeasurePeakRms(float[] samples, int sampleRate, int channels)
    {
        var windowSize = sampleRate * 2 * channels;
        if (samples.Length <= windowSize)
            return DrumIsolator.CalculateRms(samples);

        float peakRms = 0;
        var step = windowSize / 2;
        for (int pos = 0; pos + windowSize <= samples.Length; pos += step)
        {
            double sum = 0;
            for (int i = pos; i < pos + windowSize; i++)
                sum += samples[i] * (double)samples[i];
            peakRms = Math.Max(peakRms, (float)Math.Sqrt(sum / windowSize));
        }
        return peakRms;
    }

    private static void NormalizeToTarget(float[] samples, float targetRms)
    {
        var currentRms = DrumIsolator.CalculateRms(samples);
        if (currentRms < 1e-8f || targetRms < 1e-8f) return;

        var gain = targetRms / currentRms;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
            if (samples[i] > 0.98f) samples[i] = 0.98f + (samples[i] - 0.98f) * 0.1f;
            if (samples[i] < -0.98f) samples[i] = -0.98f + (samples[i] + 0.98f) * 0.1f;
        }
    }

    private static float MeasureBassRms(float[] samples, int sampleRate, int channels)
    {
        var mono = new float[samples.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
                sum += samples[i * channels + ch];
            mono[i] = sum / channels;
        }

        var bassFiltered = ApplyLowPass(mono, sampleRate, 120.0);

        var windowSize = sampleRate * 2;
        if (bassFiltered.Length <= windowSize)
        {
            double s = 0;
            for (int i = 0; i < bassFiltered.Length; i++)
                s += bassFiltered[i] * (double)bassFiltered[i];
            return (float)Math.Sqrt(s / bassFiltered.Length);
        }

        float peakRms = 0;
        var step = windowSize / 2;
        for (int pos = 0; pos + windowSize <= bassFiltered.Length; pos += step)
        {
            double sum = 0;
            for (int i = pos; i < pos + windowSize; i++)
                sum += bassFiltered[i] * (double)bassFiltered[i];
            peakRms = Math.Max(peakRms, (float)Math.Sqrt(sum / windowSize));
        }
        return peakRms;
    }

    private static void MatchBassLevel(float[] introSamples, int sampleRate, int channels, float targetBassRms)
    {
        if (targetBassRms < 1e-8f) return;
        var introBassRms = MeasureBassRms(introSamples, sampleRate, channels);
        if (introBassRms < 1e-8f) return;

        var ratio = Math.Clamp(targetBassRms / introBassRms, 0.5f, 2.0f);
        if (Math.Abs(ratio - 1.0f) < 0.05f) return;

        var gain = 1.0f + (ratio - 1.0f) * 0.7f;
        for (int i = 0; i < introSamples.Length; i++)
        {
            introSamples[i] *= gain;
            if (introSamples[i] > 0.98f) introSamples[i] = 0.98f + (introSamples[i] - 0.98f) * 0.1f;
            if (introSamples[i] < -0.98f) introSamples[i] = -0.98f + (introSamples[i] + 0.98f) * 0.1f;
        }
    }

    // -----------------------------------------------------------------
    // Build intro from selected stems
    // -----------------------------------------------------------------

    private static float[] BuildIntroFromSelectedStems(
        string stemsDir, string[] selectedStems,
        int sampleRate, int channels,
        double bpm, int totalBars, int extractBars, bool loop,
        double? regionStartSeconds = null, double? regionEndSeconds = null)
    {
        var secondsPerBar = (60.0 / bpm) * 4;
        var samplesPerBar = (int)Math.Round(secondsPerBar * sampleRate) * channels;

        // If a region is specified, use that; otherwise use first N bars
        int regionStartSample, regionLength;
        if (regionStartSeconds.HasValue && regionEndSeconds.HasValue && regionEndSeconds > regionStartSeconds)
        {
            regionStartSample = (int)(regionStartSeconds.Value * sampleRate) * channels;
            var regionEndSample = (int)(regionEndSeconds.Value * sampleRate) * channels;
            regionLength = regionEndSample - regionStartSample;
        }
        else
        {
            regionStartSample = 0;
            regionLength = samplesPerBar * extractBars;
        }

        // Combine selected stems from the same region
        float[]? combined = null;
        foreach (var stem in selectedStems)
        {
            var path = Path.Combine(stemsDir, $"{stem.ToLower()}.wav");
            if (!File.Exists(path)) continue;

            using var reader = new AudioFileReader(path);
            var samples = ReadAllSamples(reader);

            // Clamp region to available samples
            var start = Math.Min(regionStartSample, samples.Length);
            var end = Math.Min(start + regionLength, samples.Length);
            var len = end - start;
            if (len <= 0) continue;

            if (combined == null)
            {
                combined = new float[len];
                Array.Copy(samples, start, combined, 0, len);
            }
            else
            {
                var mixLen = Math.Min(combined.Length, len);
                for (int i = 0; i < mixLen; i++)
                    combined[i] += samples[start + i];
            }
        }

        if (combined == null)
            return new float[regionLength > 0 ? regionLength : samplesPerBar * extractBars];

        // Loop if requested
        if (loop && totalBars > extractBars)
        {
            var looped = new float[combined.Length * 2];
            Array.Copy(combined, looped, combined.Length);
            Array.Copy(combined, 0, looped, combined.Length, combined.Length);
            return looped;
        }

        return combined;
    }

    // -----------------------------------------------------------------
    // Audio I/O and mixing
    // -----------------------------------------------------------------

    private static float[] ApplyLowPass(float[] input, int sampleRate, double cutoffHz)
    {
        var output = new float[input.Length];
        var rc = 1.0 / (2.0 * Math.PI * cutoffHz);
        var dt = 1.0 / sampleRate;
        var alpha = dt / (rc + dt);
        output[0] = input[0];
        for (int i = 1; i < input.Length; i++)
            output[i] = (float)(output[i - 1] + alpha * (input[i] - output[i - 1]));
        return output;
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

    private static float[] CombineWithCrossfadeAtPosition(
        float[] drumIntro,
        float[] original,
        int sampleRate,
        int channels,
        double crossfadeSeconds,
        int crossfadeStartSample)
    {
        crossfadeStartSample = Math.Clamp(crossfadeStartSample, 0, Math.Max(0, original.Length - channels));

        var crossfadeSamples = (int)(crossfadeSeconds * sampleRate) * channels;
        var availableOriginal = original.Length - crossfadeStartSample;
        crossfadeSamples = Math.Min(crossfadeSamples, Math.Min(drumIntro.Length, availableOriginal));

        if (crossfadeSamples <= 0)
        {
            var simple = new float[drumIntro.Length + original.Length];
            Array.Copy(drumIntro, 0, simple, 0, drumIntro.Length);
            Array.Copy(original, 0, simple, drumIntro.Length, original.Length);
            return simple;
        }

        var nonOverlapIntro = drumIntro.Length - crossfadeSamples;
        var remainingOriginal = Math.Max(0, original.Length - crossfadeStartSample - crossfadeSamples);
        var totalLength = nonOverlapIntro + crossfadeSamples + remainingOriginal;
        var output = new float[totalLength];

        if (nonOverlapIntro > 0)
            Array.Copy(drumIntro, 0, output, 0, nonOverlapIntro);

        for (int i = 0; i < crossfadeSamples; i++)
        {
            var t = (float)i / crossfadeSamples;
            var drumFade = (float)Math.Cos(t * Math.PI / 2);
            var trackFade = (float)Math.Sin(t * Math.PI / 2);
            output[nonOverlapIntro + i] = drumIntro[nonOverlapIntro + i] * drumFade
                                        + original[crossfadeStartSample + i] * trackFade;
        }

        if (remainingOriginal > 0)
        {
            Array.Copy(original, crossfadeStartSample + crossfadeSamples,
                       output, nonOverlapIntro + crossfadeSamples, remainingOriginal);
        }

        return output;
    }
}
