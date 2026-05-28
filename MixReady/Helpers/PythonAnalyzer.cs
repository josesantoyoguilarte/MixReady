using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixReady.Helpers;

/// <summary>
/// Bridges C# to the Python audio analysis pipeline.
///
/// Python handles all audio/music analysis (BPM, key, beats, downbeats,
/// structure) using librosa -- the industry standard for MIR. C# handles
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
    /// Result of demucs stem separation.
    /// </summary>
    public record SeparationResult
    {
        [JsonPropertyName("drums")]
        public string? DrumsPath { get; init; }

        [JsonPropertyName("bass")]
        public string? BassPath { get; init; }

        [JsonPropertyName("vocals")]
        public string? VocalsPath { get; init; }

        [JsonPropertyName("other")]
        public string? OtherPath { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }

    /// <summary>
    /// Separate a track into stems using demucs (AI source separation).
    /// Returns paths to drums.wav, bass.wav, vocals.wav, other.wav.
    /// </summary>
    public static async Task<SeparationResult> SeparateAsync(string audioFilePath, string outputDir)
    {
        EnsurePythonAvailable();

        var separateScript = ResolveSeparateScriptPath();
        var args = $"\"{separateScript}\" \"{audioFilePath}\" \"{outputDir}\"";

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
            return new SeparationResult { Error = $"Demucs failed: {stderr}" };
        }

        var result = JsonSerializer.Deserialize<SeparationResult>(stdout);
        return result ?? new SeparationResult { Error = "Invalid JSON from separator" };
    }

    /// <summary>
    /// Check if demucs stem separation is available.
    /// </summary>
    public static bool IsSeparationAvailable()
    {
        try
        {
            FindPython();
            return File.Exists(ResolveSeparateScriptPath());
        }
        catch
        {
            return false;
        }
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

    private static string ResolveSeparateScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(ScriptDir, "separate.py"),
            Path.Combine(AppContext.BaseDirectory, "scripts", "separate.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "scripts", "separate.py"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(
            "Stem separation script not found. Expected at: scripts/separate.py");
    }

    private static string? _cachedPythonPath;

    private static string FindPython()
    {
        if (_cachedPythonPath != null)
            return _cachedPythonPath;

        // 0a. Project-local venv (resolved relative to running binary and CWD)
        var projectVenvCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".venv", "bin", "python3"),
            Path.Combine(AppContext.BaseDirectory, ".venv", "bin", "python3"),
            Path.Combine(Directory.GetCurrentDirectory(), ".venv", "bin", "python3"),
        };
        foreach (var p in projectVenvCandidates)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full) && TryRunPython(full))
            {
                _cachedPythonPath = full;
                return full;
            }
        }

        // 0b. Docker / Linux venv path (QA containers)
        foreach (var p in new[] { "/opt/venv/bin/python3", "/usr/bin/python3" })
        {
            if (File.Exists(p) && TryRunPython(p))
            {
                _cachedPythonPath = p;
                return p;
            }
        }

        // 1. Check known user-install locations FIRST
        //    This avoids the Windows Store python.exe alias which hangs or fails.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            foreach (var ver in new[] { "Python313", "Python312", "Python311", "Python310", "Python39" })
            {
                var path = Path.Combine(localAppData, "Programs", ver, "python.exe");
                if (File.Exists(path) && TryRunPython(path))
                {
                    _cachedPythonPath = path;
                    return path;
                }
            }
        }

        // 2. Check system-wide installs
        foreach (var root in new[] { @"C:\Python313", @"C:\Python312", @"C:\Python311", @"C:\Python310", @"C:\Python39" })
        {
            var path = Path.Combine(root, "python.exe");
            if (File.Exists(path) && TryRunPython(path))
            {
                _cachedPythonPath = path;
                return path;
            }
        }

        // 3. Try PATH commands last
        foreach (var cmd in new[] { "python3", "python" })
        {
            if (TryRunPython(cmd))
            {
                _cachedPythonPath = cmd;
                return cmd;
            }
        }

        throw new InvalidOperationException(
            "Python not found. Install Python 3.9+ and run: pip install -r scripts/requirements.txt");
    }

    private static bool TryRunPython(string pythonCmd)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonCmd,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            if (!p.HasExited) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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
