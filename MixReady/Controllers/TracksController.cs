using Hangfire;
using Microsoft.AspNetCore.Mvc;
using MixReady.Helpers;
using MixReady.Jobs;
using MixReady.Models;
using MixReady.Services;
using MixReady.Storage;

namespace MixReady.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TracksController : ControllerBase
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".aac"
    };

    private const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100 MB

    private readonly ITrackService _trackService;
    private readonly IFileStorageService _fileStorageService;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public TracksController(
        ITrackService trackService,
        IFileStorageService fileStorageService,
        IBackgroundJobClient backgroundJobClient)
    {
        _trackService = trackService;
        _fileStorageService = fileStorageService;
        _backgroundJobClient = backgroundJobClient;
    }

    /// <summary>
    /// Upload a song file. Accepted formats: .mp3, .wav, .flac, .ogg, .aac (max 100 MB).
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        if (file.Length > MaxFileSizeBytes)
            return BadRequest($"File exceeds maximum size of {MaxFileSizeBytes / (1024 * 1024)} MB.");

        var extension = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(extension))
            return BadRequest($"Unsupported file type '{extension}'. Allowed types: {string.Join(", ", AllowedExtensions)}");

        var filePath = await _fileStorageService.SaveOriginalAsync(file);
        var track = _trackService.Create(file.FileName, filePath);

        return Ok(new { trackId = track.Id });
    }

    /// <summary>
    /// Trigger intro generation for a track.
    /// Optionally pass a genre to override auto-detection.
    /// Mode: "groove" (default) extracts the track's own drums, "synth" generates from scratch.
    /// Bars: 4, 8, or 16 — how many bars to extract (default 8).
    /// Loop: if true, loops the extracted section to double its length (e.g. 8 bars becomes 16).
    /// IntroOnly: if true, returns just the intro. If false (default), returns intro + full song.
    /// </summary>
    [HttpPost("{id}/generate-intro")]
    public IActionResult GenerateIntro(
        Guid id,
        [FromQuery] string? genre = null,
        [FromQuery] string mode = "groove",
        [FromQuery] int bars = 8,
        [FromQuery] bool loop = false,
        [FromQuery] bool introOnly = false)
    {
        var track = _trackService.GetById(id);
        if (track == null)
            return NotFound();

        if (bars != 4 && bars != 8 && bars != 16)
            return BadRequest(new { error = "bars must be 4, 8, or 16." });

        if (!string.IsNullOrWhiteSpace(genre) &&
            !GenreAnalyzer.SupportedGenres.Contains(genre, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                error = $"Unsupported genre '{genre}'.",
                supportedGenres = GenreAnalyzer.SupportedGenres
            });
        }

        var useGroove = !mode.Equals("synth", StringComparison.OrdinalIgnoreCase);
        var jobId = _backgroundJobClient.Enqueue<IntroGenerationJob>(job => job.Execute(id, genre, useGroove, bars, loop, introOnly));

        return Ok(new { jobId, mode = useGroove ? "groove" : "synth", bars, loop, introOnly });
    }

    /// <summary>
    /// Diagnostic: check Python and demucs availability.
    /// Also tests the actual Python path resolution.
    /// </summary>
    [HttpGet("diagnostics/python")]
    public IActionResult PythonDiagnostics()
    {
        var pythonAvailable = PythonAnalyzer.IsAvailable();
        var demucsAvailable = PythonAnalyzer.IsSeparationAvailable();

        // Test actual python path
        string? pythonPath = null;
        string? scriptPath = null;
        string? pythonTest = null;
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var ver in new[] { "Python313", "Python312", "Python311", "Python310", "Python39" })
            {
                var p = System.IO.Path.Combine(localAppData, "Programs", ver, "python.exe");
                if (System.IO.File.Exists(p)) { pythonPath = p; break; }
            }

            // Check script path
            var candidates = new[]
            {
                System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "scripts", "separate.py")),
                System.IO.Path.Combine(AppContext.BaseDirectory, "scripts", "separate.py"),
                System.IO.Path.Combine(Directory.GetCurrentDirectory(), "scripts", "separate.py"),
            };
            foreach (var c in candidates)
            {
                if (System.IO.File.Exists(c)) { scriptPath = c; break; }
            }

            // Quick test: can python import demucs?
            if (pythonPath != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = "-c \"import demucs; print('demucs OK')\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    pythonTest = proc.StandardOutput.ReadToEnd().Trim();
                    var err = proc.StandardError.ReadToEnd().Trim();
                    proc.WaitForExit(5000);
                    if (proc.ExitCode != 0) pythonTest = $"FAILED: {err}";
                }
            }
        }
        catch (Exception ex)
        {
            pythonTest = $"Exception: {ex.Message}";
        }

        return Ok(new
        {
            pythonAvailable,
            demucsAvailable,
            pythonPath,
            scriptPath,
            pythonTest,
            baseDirectory = AppContext.BaseDirectory,
            message = demucsAvailable
                ? "Demucs AI vocal removal is available."
                : pythonAvailable
                    ? "Python found but demucs script missing."
                    : "Python not found. Using biquad fallback (vocals will leak)."
        });
    }

    /// <summary>
    /// Analyze a track's genre and structure without generating an intro.
    /// Uses Python (librosa) when available for accurate analysis.
    /// </summary>
    [HttpGet("{id}/analyze")]
    public async Task<IActionResult> Analyze(Guid id)
    {
        var track = _trackService.GetById(id);
        if (track == null)
            return NotFound();

        // Try Python analysis first
        if (PythonAnalyzer.IsAvailable())
        {
            try
            {
                var py = await PythonAnalyzer.AnalyzeAsync(track.FilePath);
                return Ok(new
                {
                    trackId = track.Id,
                    analysisEngine = "python-librosa",
                    detectedBpm = py.Bpm,
                    detectedKey = py.Key,
                    bassFrequency = py.BassFrequency,
                    genre = py.Genre,
                    duration = py.Duration,
                    structure = new
                    {
                        totalBeats = py.Beats.Length,
                        totalBars = py.Bars.Length,
                        firstVocalEntrySeconds = py.FirstVocalEntry,
                        firstStrongRhythmSeconds = py.FirstStrongRhythm,
                        recommendedCrossfadeSeconds = py.RecommendedCrossfadeStart,
                        energyCurve = py.EnergyCurve.Take(32).ToArray(),
                        downbeats = py.Downbeats.Take(32).ToArray()
                    },
                    supportedGenres = GenreAnalyzer.SupportedGenres
                });
            }
            catch { /* Fall through to C# */ }
        }

        // C# fallback
        var bpm = BpmDetector.Detect(track.FilePath);
        var result = GenreAnalyzer.AnalyzeWithScores(track.FilePath, bpm);
        var (keyName, _) = KeyDetector.Detect(track.FilePath);

        using var reader = new NAudio.Wave.AudioFileReader(track.FilePath);
        var format = reader.WaveFormat;
        var sampleCount = (int)(reader.Length / (format.BitsPerSample / 8));
        var samples = new float[sampleCount];
        var totalRead = 0;
        int read;
        while ((read = reader.Read(samples, totalRead, Math.Min(4096, samples.Length - totalRead))) > 0)
        {
            totalRead += read;
            if (totalRead >= samples.Length) break;
        }
        samples = samples[..totalRead];

        var analysis = TrackAnalyzer.Analyze(samples, format.SampleRate, format.Channels, bpm);

        return Ok(new
        {
            trackId = track.Id,
            analysisEngine = "csharp-fallback",
            detectedBpm = bpm,
            detectedKey = keyName,
            bestMatch = result.BestMatch,
            scores = result.Scores.Take(5).Select(s => new
            {
                genre = s.Genre,
                confidence = $"{s.Confidence}%"
            }),
            structure = new
            {
                totalBeats = analysis.BeatPositions.Length,
                totalBars = analysis.BarBoundaries.Length,
                firstVocalEntrySeconds = analysis.FirstVocalEntry / (double)(format.SampleRate * format.Channels),
                firstStrongRhythmSeconds = analysis.FirstStrongRhythmSection / (double)(format.SampleRate * format.Channels),
                recommendedCrossfadeSeconds = analysis.RecommendedCrossfadeStart / (double)(format.SampleRate * format.Channels),
                energyCurve = analysis.EnergyPerBar.Take(32).Select(e => Math.Round(e, 2)).ToArray()
            },
            supportedGenres = GenreAnalyzer.SupportedGenres
        });
    }

    /// <summary>
    /// Check the processing status of a track.
    /// Returns ready=true and a downloadUrl when processing is complete.
    /// </summary>
    [HttpGet("{id}/status")]
    public IActionResult GetStatus(Guid id)
    {
        var track = _trackService.GetById(id);
        if (track == null)
            return NotFound();

        var ready = track.Status == TrackStatus.Completed;

        return Ok(new
        {
            trackId = track.Id,
            status = track.Status.ToString(),
            ready,
            message = track.Status switch
            {
                TrackStatus.Uploaded => "Waiting to start...",
                TrackStatus.Processing => ">>> PROCESSING — please wait, demucs AI is removing vocals... <<<",
                TrackStatus.Completed => ">>> DONE! Download your intro using the downloadUrl below. <<<",
                TrackStatus.Failed => $">>> FAILED: {track.ErrorMessage} <<<",
                _ => ""
            },
            downloadUrl = ready ? $"/api/Tracks/{track.Id}/download" : null,
            detectedBpm = track.DetectedBpm,
            detectedGenre = track.DetectedGenre,
            detectedKey = track.DetectedKey
        });
    }

    /// <summary>
    /// Download the processed track.
    /// </summary>
    [HttpGet("{id}/download")]
    public IActionResult Download(Guid id)
    {
        var track = _trackService.GetById(id);
        if (track == null)
            return NotFound();

        if (string.IsNullOrEmpty(track.ProcessedFilePath) || !System.IO.File.Exists(track.ProcessedFilePath))
            return NotFound("Processed file not available yet.");

        var fileBytes = System.IO.File.ReadAllBytes(track.ProcessedFilePath);
        return File(fileBytes, "audio/wav", $"{Path.GetFileNameWithoutExtension(track.OriginalFileName)}_intro.wav");
    }
}
