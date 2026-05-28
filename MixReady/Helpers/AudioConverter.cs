using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MixReady.Helpers;

/// <summary>
/// On non-Windows hosts, NAudio's AudioFileReader can only decode .wav directly
/// (mp3/flac/ogg/aac fall through to MediaFoundation, which doesn't exist on macOS/Linux).
/// This helper transparently converts non-WAV inputs to a sibling .wav using the
/// project's Python venv (librosa + soundfile) and returns the playable path.
/// </summary>
public static class AudioConverter
{
    private static readonly object Gate = new();

    public static string EnsureWav(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
            return inputPath;

        var ext = Path.GetExtension(inputPath);
        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
            return inputPath;

        // On Windows, NAudio handles MP3/etc. natively via Media Foundation.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return inputPath;

        var wavPath = inputPath + ".wav";

        lock (Gate)
        {
            if (File.Exists(wavPath) && new FileInfo(wavPath).Length > 0)
                return wavPath;

            var python = FindPython();
            var script = FindConvertScript();
            if (python == null || script == null)
                throw new InvalidOperationException(
                    "Cannot convert audio: Python venv or convert_to_wav.py not found.");

            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{script}\" \"{inputPath}\" \"{wavPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start audio conversion process.");
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0 || !File.Exists(wavPath))
            {
                try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
                throw new InvalidOperationException(
                    $"Audio conversion failed (exit {proc.ExitCode}): {stderr.Trim()}");
            }

            return wavPath;
        }
    }

    private static string? FindPython()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".venv", "bin", "python3"),
            Path.Combine(AppContext.BaseDirectory, ".venv", "bin", "python3"),
            Path.Combine(Directory.GetCurrentDirectory(), ".venv", "bin", "python3"),
            "/opt/venv/bin/python3",
            "/usr/bin/python3",
        };
        foreach (var p in candidates)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string? FindConvertScript()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "scripts", "convert_to_wav.py"),
            Path.Combine(AppContext.BaseDirectory, "scripts", "convert_to_wav.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "scripts", "convert_to_wav.py"),
        };
        foreach (var p in candidates)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
