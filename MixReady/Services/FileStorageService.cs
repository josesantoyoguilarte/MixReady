public class FileStorageService : IFileStorageService
{
    private readonly string _originalsPath;
    private readonly string _processedPath;

    public FileStorageService(IWebHostEnvironment env)
    {
        var root = Environment.GetEnvironmentVariable("MIXREADY_STORAGE_ROOT")
                   ?? Path.Combine(env.ContentRootPath, "storage");

        _originalsPath = Path.Combine(root, "originals");
        _processedPath = Path.Combine(root, "processed");

        Directory.CreateDirectory(_originalsPath);
        Directory.CreateDirectory(_processedPath);
    }

    public async Task<string> SaveOriginalAsync(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var id = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_originalsPath, $"{id}{ext}");

        using (var stream = new FileStream(originalPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        if (ext != ".wav")
        {
            var wavPath = Path.Combine(_originalsPath, $"{id}.wav");
            if (TryConvertToWav(originalPath, wavPath))
            {
                try { File.Delete(originalPath); } catch { }
                return wavPath;
            }
        }

        return originalPath;
    }

    public string GetProcessedPath(Guid trackId)
    {
        return Path.Combine(_processedPath, $"{trackId}.wav");
    }

    public string GetStemsPath(Guid trackId)
    {
        var path = Path.Combine(_originalsPath, "..", "stems", trackId.ToString());
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        return full;
    }

    public string GetKitchenOutputPath(Guid id)
    {
        return Path.Combine(_processedPath, $"kitchen_{id}.wav");
    }

    private static bool TryConvertToWav(string inputPath, string outputPath)
    {
        var pythonPath = FindPython();
        if (pythonPath == null) return false;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"-c \"import librosa; import soundfile as sf; y, sr = librosa.load(r'{inputPath.Replace("'", "\\'")}', sr=None, mono=False); sf.write(r'{outputPath.Replace("'", "\\'")}', y.T if y.ndim > 1 else y, sr)\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;

            proc.WaitForExit(60_000);
            return proc.HasExited && proc.ExitCode == 0 && File.Exists(outputPath);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindPython()
    {
        foreach (var p in new[] { "/opt/venv/bin/python3", "/usr/bin/python3" })
            if (File.Exists(p)) return p;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var ver in new[] { "Python313", "Python312", "Python311", "Python310", "Python39" })
        {
            var path = Path.Combine(localAppData, "Programs", ver, "python.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
