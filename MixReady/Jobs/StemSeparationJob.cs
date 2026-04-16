using MixReady.Services;
using MixReady.Storage;

namespace MixReady.Jobs;

public class StemSeparationJob
{
    private readonly ITrackService _trackService;
    private readonly IFileStorageService _fileStorageService;

    public StemSeparationJob(ITrackService trackService, IFileStorageService fileStorageService)
    {
        _trackService = trackService;
        _fileStorageService = fileStorageService;
    }

    public Task Execute(Guid trackId)
    {
        var track = _trackService.GetById(trackId)
            ?? throw new InvalidOperationException($"Track {trackId} not found.");

        var stemsDir = _fileStorageService.GetStemsPath(trackId);
        Directory.CreateDirectory(stemsDir);

        var logPath = Path.Combine(Path.GetTempPath(), $"mixready_stems_{trackId}.txt");
        var log = new System.Text.StringBuilder();
        log.AppendLine($"[{DateTime.Now}] Starting stem separation for {trackId}");

        try
        {
            var pythonPath = FindPython();
            var scriptPath = FindSeparateScript();

            if (pythonPath == null || scriptPath == null)
            {
                _trackService.SetStemsError(trackId, "Python or separate.py not found");
                return Task.CompletedTask;
            }

            log.AppendLine($"Python: {pythonPath}");
            log.AppendLine($"Script: {scriptPath}");
            log.AppendLine($"Input: {track.FilePath}");
            log.AppendLine($"Output: {stemsDir}");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" \"{track.FilePath}\" \"{stemsDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                _trackService.SetStemsError(trackId, "Failed to start Python process");
                return Task.CompletedTask;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(300_000);

            log.AppendLine($"Exit code: {(process.HasExited ? process.ExitCode.ToString() : "TIMEOUT")}");
            log.AppendLine($"Stdout: {stdout}");

            if (!process.HasExited || process.ExitCode != 0)
            {
                _trackService.SetStemsError(trackId, $"Demucs failed: {stderr[..Math.Min(200, stderr.Length)]}");
                File.WriteAllText(logPath, log.ToString());
                return Task.CompletedTask;
            }

            // Verify stems exist
            var expectedStems = new[] { "drums.wav", "bass.wav", "other.wav", "vocals.wav" };
            var missing = expectedStems.Where(s => !File.Exists(Path.Combine(stemsDir, s))).ToArray();

            if (missing.Length > 0)
            {
                _trackService.SetStemsError(trackId, $"Missing stems: {string.Join(", ", missing)}");
                File.WriteAllText(logPath, log.ToString());
                return Task.CompletedTask;
            }

            _trackService.SetStemsDirectory(trackId, stemsDir);
            log.AppendLine("SUCCESS - all stems ready");
            File.WriteAllText(logPath, log.ToString());
        }
        catch (Exception ex)
        {
            _trackService.SetStemsError(trackId, ex.Message);
            log.AppendLine($"EXCEPTION: {ex}");
            File.WriteAllText(logPath, log.ToString());
        }

        return Task.CompletedTask;
    }

    private static string? FindPython()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var ver in new[] { "Python313", "Python312", "Python311", "Python310", "Python39" })
        {
            var path = Path.Combine(localAppData, "Programs", ver, "python.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

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
}
