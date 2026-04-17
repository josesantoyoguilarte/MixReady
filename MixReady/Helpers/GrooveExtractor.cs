using NAudio.Wave;
using NAudio.Wave;

namespace MixReady.Helpers;

/// <summary>
/// Builds a DJ intro by finding the best rhythmic section from the track,
/// removing vocals, and looping the instrumental groove.
///
/// Pipeline:
///   1. Scan the entire track for the best 4-bar rhythmic section
///      (strong drums, regular beat, high energy, consistent bars)
///   2. Remove vocals from that section:
///      - Demucs (AI): if Python + demucs installed — clean stem separation
///      - Biquad (C# fallback): notch out 300-3000 Hz vocal band
///   3. Beat-align to the downbeat
///   4. Loop for 8-16 bars with volume build
///   5. Smooth loop points to prevent clicks
///
/// Result: the track's own groove — same kick, clap, hat, swing — no vocals.
/// </summary>
public static class GrooveExtractor
{
    /// <summary>
    /// Find the best rhythmic section, remove vocals, and build an intro.
    /// </summary>
    public static float[] BuildIntroFromGroove(
        string inputPath,
        float[] trackSamples,
        int sampleRate,
        int channels,
        double bpm,
        double bassFreq,
        int introBars = 8,
        int extractBars = 8,
        bool loop = false)
    {
        var secondsPerBar = (60.0 / bpm) * 4;
        var samplesPerBar = (int)Math.Round(secondsPerBar * sampleRate) * channels;

        // --- Step 1: Remove vocals from the ENTIRE track first ---
        var instrumental = RemoveVocalsFromFullTrack(inputPath, trackSamples, sampleRate, channels);

        // --- Step 2: Find the best rhythmic section ---
        var bestSection = FindBestRhythmicSection(
            instrumental, sampleRate, channels, bpm, barsToExtract: extractBars);

        // --- Step 3: Beat-align ---
        bestSection = BeatAlign(bestSection, sampleRate, channels, bpm);

        float[] intro;
        if (loop)
        {
            // --- Step 4: Loop to introBars ---
            intro = BuildLoop(bestSection, samplesPerBar, introBars);
        }
        else
        {
            // --- No loop: use extracted section as-is ---
            intro = bestSection;
        }

        // --- Tiny fade at start to prevent speaker pop ---
        SmoothLoopPoints(intro, bestSection.Length, sampleRate, channels);

        return intro;
    }

    /// <summary>
    /// Backward-compatible overload.
    /// </summary>
    public static float[] BuildIntroFromGroove(
        float[] trackSamples,
        int sampleRate,
        int channels,
        double bpm,
        double bassFreq,
        int introBars = 8)
    {
        return BuildIntroFromGroove("", trackSamples, sampleRate, channels, bpm, bassFreq, introBars);
    }

    // -----------------------------------------------------------------
    // Vocal removal — operates on the FULL track
    // -----------------------------------------------------------------

    /// <summary>
    /// Remove vocals from the entire track.
    ///
    /// Priority 1: Demucs AI (if available).
    ///   Separates into 4 stems, recombines drums + bass + other.
    ///   Result: clean instrumental with original sound quality.
    ///
    /// Priority 2: Biquad notch (C# fallback).
    ///   Cuts 300-3000 Hz from the full track.
    ///   Keeps kicks, clap transients, hats. Removes vocal fundamentals.
    /// </summary>
    private static float[] RemoveVocalsFromFullTrack(
        string inputPath,
        float[] trackSamples,
        int sampleRate,
        int channels)
    {
        // --- Try demucs ---
        if (!string.IsNullOrEmpty(inputPath))
        {
            var pythonPath = FindPythonDirect();
            var scriptPath = FindSeparateScript();

            if (pythonPath != null && scriptPath != null)
            {
                try
                {
                    var result = RunDemucs(pythonPath, scriptPath, inputPath);
                    if (result != null && result.Length > 0)
                        return result;
                }
                catch (Exception ex)
                {
                    // Write error to a file for debugging since Console/Debug may not be visible
                    var errorLog = Path.Combine(Path.GetTempPath(), "mixready_demucs_error.txt");
                    File.WriteAllText(errorLog, $"{DateTime.Now}: {ex}\n");
                }
            }
        }

        // --- Biquad fallback ---
        return DrumIsolator.Isolate(trackSamples, sampleRate, channels);
    }

    /// <summary>
    /// Run demucs end-to-end, synchronously. Returns the combined instrumental (no vocals).
    /// Writes a log file to temp for debugging.
    /// </summary>
    private static float[]? RunDemucs(string pythonPath, string scriptPath, string inputPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mixready_stems", Guid.NewGuid().ToString("N"));
        var logPath = Path.Combine(Path.GetTempPath(), "mixready_demucs_log.txt");
        var log = new System.Text.StringBuilder();
        log.AppendLine($"[{DateTime.Now}] Starting demucs separation");
        log.AppendLine($"Python: {pythonPath}");
        log.AppendLine($"Script: {scriptPath}");
        log.AppendLine($"Input: {inputPath}");
        log.AppendLine($"Output dir: {tempDir}");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" \"{inputPath}\" \"{tempDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                log.AppendLine("ERROR: Failed to start process");
                File.WriteAllText(logPath, log.ToString());
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(300_000);

            log.AppendLine($"Exit code: {(process.HasExited ? process.ExitCode.ToString() : "TIMEOUT")}");
            log.AppendLine($"Stdout: {stdout}");
            log.AppendLine($"Stderr (last 300): {(stderr.Length > 300 ? stderr[^300..] : stderr)}");

            if (!process.HasExited)
            {
                try { process.Kill(); } catch { }
                File.WriteAllText(logPath, log.ToString());
                return null;
            }

            if (process.ExitCode != 0)
            {
                File.WriteAllText(logPath, log.ToString());
                return null;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                log.AppendLine("ERROR: No stdout");
                File.WriteAllText(logPath, log.ToString());
                return null;
            }

            var result = System.Text.Json.JsonSerializer.Deserialize<PythonAnalyzer.SeparationResult>(stdout);
            if (result == null || !string.IsNullOrEmpty(result.Error))
            {
                log.AppendLine($"ERROR: {result?.Error ?? "null result"}");
                File.WriteAllText(logPath, log.ToString());
                return null;
            }

            // Combine drums + bass + other (skip vocals)
            float[]? combined = null;
            foreach (var stemPath in new[] { result.DrumsPath, result.BassPath, result.OtherPath })
            {
                if (string.IsNullOrEmpty(stemPath) || !File.Exists(stemPath))
                {
                    log.AppendLine($"Stem missing: {stemPath ?? "null"}");
                    continue;
                }

                log.AppendLine($"Reading stem: {stemPath}");
                using var reader = new NAudio.Wave.AudioFileReader(stemPath);
                var buf = new float[(int)(reader.Length / (reader.WaveFormat.BitsPerSample / 8))];
                int totalRead = 0, read;
                while ((read = reader.Read(buf, totalRead, Math.Min(8192, buf.Length - totalRead))) > 0)
                    totalRead += read;

                if (combined == null)
                {
                    combined = new float[totalRead];
                    Array.Copy(buf, combined, totalRead);
                }
                else
                {
                    for (int i = 0; i < Math.Min(combined.Length, totalRead); i++)
                        combined[i] += buf[i];
                }
            }

            log.AppendLine($"Combined: {combined?.Length ?? 0} samples");
            log.AppendLine("SUCCESS");
            File.WriteAllText(logPath, log.ToString());
            return combined;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }
    }

    /// <summary>Find Python executable directly (no async, no cache dependency).</summary>
    private static string? FindPythonDirect()
    {
        // Docker / Linux paths first
        foreach (var p in new[] { "/opt/venv/bin/python3", "/usr/bin/python3" })
            if (File.Exists(p)) return p;

        // Windows local dev paths
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var ver in new[] { "Python313", "Python312", "Python311", "Python310", "Python39" })
        {
            var path = Path.Combine(localAppData, "Programs", ver, "python.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Find the separate.py script.</summary>
    private static string? FindSeparateScript()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "scripts", "separate.py"),
            Path.Combine(AppContext.BaseDirectory, "scripts", "separate.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "scripts", "separate.py"),
        };
        foreach (var p in candidates)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>
    /// Scan the entire (vocal-removed) track and find the section with the strongest,
    /// most regular rhythmic pattern.
    /// </summary>
    private static float[] FindBestRhythmicSection(
        float[] samples, int sampleRate, int channels, double bpm, int barsToExtract)
    {
        var secondsPerBar = (60.0 / bpm) * 4;
        var samplesPerBar = (int)Math.Round(secondsPerBar * sampleRate) * channels;
        var extractLength = samplesPerBar * barsToExtract;

        if (samples.Length <= extractLength)
            return (float[])samples.Clone();

        // Skip first 10% and last 10% of the track (intros/outros are weak)
        var searchStart = samples.Length / 10;
        var searchEnd = samples.Length - extractLength - samples.Length / 10;

        // Snap searchStart to a bar boundary
        if (samplesPerBar > 0)
            searchStart = (searchStart / samplesPerBar) * samplesPerBar;

        if (searchStart >= searchEnd)
        {
            searchStart = 0;
            searchEnd = samples.Length - extractLength;
        }

        var bestScore = double.MinValue;
        var bestStart = searchStart;

        // Search in bar-sized steps
        var step = Math.Max(samplesPerBar, 1);
        for (int pos = searchStart; pos < searchEnd; pos += step)
        {
            var score = ScoreRhythmicSection(samples, pos, extractLength, sampleRate, channels, bpm);
            if (score > bestScore)
            {
                bestScore = score;
                bestStart = pos;
            }
        }

        return samples[bestStart..(bestStart + extractLength)];
    }

    /// <summary>
    /// Score a candidate section for rhythmic quality.
    /// Prioritizes: strong beat + high energy + sharp transients + low vocals.
    /// </summary>
    private static double ScoreRhythmicSection(
        float[] samples, int start, int length,
        int sampleRate, int channels, double bpm)
    {
        // Extract mono
        var monoLength = length / channels;
        var mono = new float[monoLength];
        for (int i = 0; i < monoLength; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
                sum += samples[start + i * channels + ch];
            mono[i] = sum / channels;
        }

        // --- 1. Overall energy (RMS) ---
        double energySum = 0;
        for (int i = 0; i < mono.Length; i++)
            energySum += mono[i] * (double)mono[i];
        var rms = Math.Sqrt(energySum / mono.Length);

        if (rms < 1e-8) return 0;

        // --- 2. Beat regularity ---
        var windowSize = Math.Max(1, sampleRate / 100);
        var windowCount = mono.Length / windowSize;
        if (windowCount < 4) return 0;

        var windowEnergies = new double[windowCount];
        double peakEnergy = 0;
        double totalWindowEnergy = 0;

        for (int w = 0; w < windowCount; w++)
        {
            double e = 0;
            for (int i = 0; i < windowSize && w * windowSize + i < mono.Length; i++)
            {
                var s = mono[w * windowSize + i];
                e += s * (double)s;
            }
            e /= windowSize;
            windowEnergies[w] = e;
            totalWindowEnergy += e;
            peakEnergy = Math.Max(peakEnergy, e);
        }

        var avgEnergy = totalWindowEnergy / windowCount;
        if (avgEnergy < 1e-12) return 0;

        var peakToAvg = peakEnergy / avgEnergy;
        var secondsPerBeat = 60.0 / bpm;
        var windowsPerBeat = secondsPerBeat * 100.0;
        var beatRegularity = MeasureBeatAlignment(windowEnergies, windowsPerBeat, avgEnergy);

        // --- 3. Energy consistency across bars ---
        var samplesPerBar = (int)Math.Round((60.0 / bpm) * 4 * sampleRate);
        var barCount = mono.Length / samplesPerBar;
        var barEnergies = new double[Math.Max(1, barCount)];

        for (int b = 0; b < barCount; b++)
        {
            double barE = 0;
            var barStart = b * samplesPerBar;
            var barLen = Math.Min(samplesPerBar, mono.Length - barStart);
            for (int i = 0; i < barLen; i++)
            {
                var s = mono[barStart + i];
                barE += s * (double)s;
            }
            barEnergies[b] = Math.Sqrt(barE / Math.Max(1, barLen));
        }

        double barMean = 0;
        for (int b = 0; b < barEnergies.Length; b++) barMean += barEnergies[b];
        barMean /= barEnergies.Length;

        double barVariance = 0;
        for (int b = 0; b < barEnergies.Length; b++)
            barVariance += (barEnergies[b] - barMean) * (barEnergies[b] - barMean);
        barVariance /= barEnergies.Length;

        var consistency = barMean > 0 ? 1.0 - Math.Sqrt(barVariance) / barMean : 0;
        consistency = Math.Max(0, consistency);

        // --- Combine scores ---
        // Find the hardest-hitting, most consistent rhythmic section.
        // Do NOT penalize vocals — the best groove sections always have vocals.
        // Vocals get removed AFTER we pick the best section.
        return rms * 20.0 +
               beatRegularity * 35.0 +
               peakToAvg * 2.0 +
               consistency * 25.0;
    }

    private static double MeasureBeatAlignment(double[] windowEnergies, double windowsPerBeat, double avgEnergy)
    {
        if (windowEnergies.Length < 4 || windowsPerBeat < 1) return 0;

        var threshold = avgEnergy * 1.3;
        var peaks = new List<int>();

        for (int i = 1; i < windowEnergies.Length - 1; i++)
        {
            if (windowEnergies[i] > threshold &&
                windowEnergies[i] >= windowEnergies[i - 1] &&
                windowEnergies[i] >= windowEnergies[i + 1])
            {
                peaks.Add(i);
            }
        }

        if (peaks.Count < 3) return 0;

        var tolerance = windowsPerBeat * 0.15;
        int onBeat = 0;

        for (int i = 1; i < peaks.Count; i++)
        {
            var interval = peaks[i] - peaks[i - 1];
            foreach (var mult in new[] { 0.5, 1.0, 2.0 })
            {
                if (Math.Abs(interval - windowsPerBeat * mult) < tolerance)
                {
                    onBeat++;
                    break;
                }
            }
        }

        return (double)onBeat / (peaks.Count - 1);
    }

    /// <summary>
    /// Fine-tune alignment to the nearest beat boundary.
    /// </summary>
    private static float[] BeatAlign(float[] section, int sampleRate, int channels, double bpm)
    {
        var samplesPerBeat = (int)Math.Round(60.0 / bpm * sampleRate) * channels;
        if (samplesPerBeat <= 0 || section.Length < samplesPerBeat)
            return section;

        // Find the strongest transient in the first beat-length of audio
        // That's likely the downbeat — trim everything before it
        var searchLen = Math.Min(samplesPerBeat, section.Length);
        var windowSize = Math.Max(1, sampleRate / 200) * channels; // 5ms windows
        double maxEnergy = 0;
        int maxPos = 0;

        for (int pos = 0; pos < searchLen - windowSize; pos += channels)
        {
            double e = 0;
            for (int i = 0; i < windowSize; i++)
            {
                var s = section[pos + i];
                e += s * (double)s;
            }
            if (e > maxEnergy)
            {
                maxEnergy = e;
                maxPos = pos;
            }
        }

        // Trim to start at the transient (downbeat)
        if (maxPos > 0 && maxPos < section.Length / 2)
            return section[maxPos..];

        return section;
    }

    /// <summary>
    /// Build a full intro by looping the extracted section.
    /// No fades or crossfades between repetitions — the section is beat-aligned
    /// so the end flows naturally back into the start on the beat grid.
    /// </summary>
    private static float[] BuildLoop(float[] section, int samplesPerBar, int totalBars)
    {
        var loopLength = section.Length;
        var totalLength = samplesPerBar * totalBars;
        var output = new float[totalLength];

        for (int i = 0; i < totalLength; i++)
        {
            output[i] = section[i % loopLength];
        }

        return output;
    }

    /// <summary>
    /// Apply a gradual volume build: bars start at 70% and build to 100%.
    /// This creates a natural DJ-style intro feel.
    /// </summary>
    private static void ApplyVolumeBuild(float[] output, int samplesPerBar, int totalBars)
    {
        for (int bar = 0; bar < totalBars; bar++)
        {
            // Linear build from 70% to 100%
            var barGain = 0.7f + 0.3f * ((float)bar / Math.Max(1, totalBars - 1));

            var barStart = bar * samplesPerBar;
            var barEnd = Math.Min(barStart + samplesPerBar, output.Length);

            for (int i = barStart; i < barEnd; i++)
                output[i] *= barGain;
        }
    }

    /// <summary>
    /// Tiny fade at the very first samples to prevent a speaker pop on playback start.
    /// No processing at loop boundaries — the beat alignment handles that.
    /// </summary>
    private static void SmoothLoopPoints(float[] output, int loopLength, int sampleRate, int channels)
    {
        // 2ms fade-in at the very start only (prevents speaker pop)
        var startFade = Math.Min((int)(0.002 * sampleRate) * channels, output.Length);
        for (int i = 0; i < startFade; i++)
            output[i] *= (float)i / startFade;
    }

    // -----------------------------------------------------------------
    // DSP helpers
    // -----------------------------------------------------------------

    private static double BandEnergy(float[] mono, int sampleRate, double lowHz, double highHz)
    {
        var hp = ApplyBiquad(mono, sampleRate, lowHz, highPass: true);
        var bp = ApplyBiquad(hp, sampleRate, highHz, highPass: false);

        double energy = 0;
        for (int i = 0; i < bp.Length; i++)
            energy += bp[i] * (double)bp[i];

        return energy / Math.Max(1, bp.Length);
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
}

