using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixReady.Helpers;

/// <summary>
/// Bridges C# to the Python audio analysis pipeline.
///
/// Python handles all audio/music analysis (BPM, key, beats, downbeats,
/// structure) using librosa — the industry standard for MIR. C# handles
/// the web layer, drum synthesis, and audio mixing.
///
/// Communication: C# calls Python via process, Python outputs JSON to stdout.
/// </summary>
public static class PythonAnalyzer
{
    private static readonly string ScriptDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "scripts");

    private static readonly string AnalyzeScript = Path.Combine(ScriptDir, "analyze.py");

    /// <summary>
    /// Result of the Python audio analysis.
    /// </summary>
    public record AnalysisResult
    {
        [JsonPropertyName("bpm")]
        public double Bpm { get; init; }

        [JsonPropertyName("key")]
        public string Key { get; init; } = "C";

        [JsonPropertyName("bass_frequency")]
        public double BassFrequency { get; init; } = 45.0;

        [JsonPropertyName("genre")]
        public string Genre { get; init; } = "Pop";

        [JsonPropertyName("duration")]
        public double Duration { get; init; }

        [JsonPropertyName("beats")]
        public double[] Beats { get; init; } = Array.Empty<double>();

        [JsonPropertyName("downbeats")]
        public double[] Downbeats { get; init; } = Array.Empty<double>();

        [JsonPropertyName("bars")]
        public double[][] Bars { get; init; } = Array.Empty<double[]>();

        [JsonPropertyName("first_vocal_entry")]
        public double FirstVocalEntry { get; init; }

        [JsonPropertyName("first_strong_rhythm")]
        public double FirstStrongRhythm { get; init; }

        [JsonPropertyName("energy_curve")]
        public double[] EnergyCurve { get; init; } = Array.Empty<double>();

        [JsonPropertyName("recommended_crossfade_start")]
        public double RecommendedCrossfadeStart { get; init; }
    }

    /// <summary>
    /// Run the Python analyzer on an audio file.
    /// Returns a fully parsed analysis result.
    /// </summary>
    public static async Task<AnalysisResult> AnalyzeAsync(string audioFilePath, string? genreOverride = null)
    {
        EnsurePythonAvailable();

        var args = $"\"{ResolveScriptPath()}\" \"{audioFilePath}\"";
        if (!string.IsNullOrWhiteSpace(genreOverride))
            args += $" --genre \"{genreOverride}\"";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FindPython(),
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Python analysis failed (exit code {process.ExitCode}). Error: {stderr}");
        }

        var result = JsonSerializer.Deserialize<AnalysisResult>(stdout);
        if (result == null)
            throw new InvalidOperationException($"Python returned invalid JSON. Output: {stdout}");

        return result;
    }

    /// <summary>
    /// Check if Python analysis is available (Python installed + script exists).
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            FindPython();
            return File.Exists(ResolveScriptPath());
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveScriptPath()
    {
        // Try multiple locations
        var candidates = new[]
        {
            AnalyzeScript,
            Path.Combine(AppContext.BaseDirectory, "scripts", "analyze.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "scripts", "analyze.py"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(
            "Python analysis script not found. Expected at: scripts/analyze.py");
    }

    private static string FindPython()
    {
        // Try python3 first (Linux/Mac), then python (Windows)
        foreach (var cmd in new[] { "python3", "python" })
        {
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                p.WaitForExit(3000);
                if (p.ExitCode == 0) return cmd;
            }
            catch { }
        }

        throw new InvalidOperationException(
            "Python not found. Install Python 3.9+ and run: pip install -r scripts/requirements.txt");
    }

    private static void EnsurePythonAvailable()
    {
        if (!IsAvailable())
        {
            throw new InvalidOperationException(
                "Python audio analysis is not available. " +
                "Install Python 3.9+ and run: pip install -r scripts/requirements.txt");
        }
    }
}
