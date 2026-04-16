using Microsoft.AspNetCore.Mvc;
using MixReady.Helpers;
using MixReady.Jobs;
using MixReady.Models;
using MixReady.Services;
using MixReady.Storage;
using NAudio.Wave;
using Hangfire;
using System.Text.Json;

namespace MixReady.Controllers;

[ApiController]
[Route("api/[controller]")]
public class KitchenController : ControllerBase
{
    private readonly ITrackService _trackService;
    private readonly IFileStorageService _fileStorageService;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public KitchenController(
        ITrackService trackService,
        IFileStorageService fileStorageService,
        IBackgroundJobClient backgroundJobClient)
    {
        _trackService = trackService;
        _fileStorageService = fileStorageService;
        _backgroundJobClient = backgroundJobClient;
    }

    /// <summary>
    /// Start stem separation (demucs AI) for a track.
    /// </summary>
    [HttpPost("{id}/separate")]
    public IActionResult Separate(Guid id)
    {
        var track = _trackService.GetById(id);
        if (track == null) return NotFound();

        if (track.StemsReady)
            return Ok(new { status = "ready", message = "Stems already separated." });

        if (track.StemsSeparating)
            return Ok(new { status = "separating", message = "Already separating..." });

        _trackService.SetStemsSeparating(id, true);
        _backgroundJobClient.Enqueue<StemSeparationJob>(job => job.Execute(id));

        return Ok(new { status = "started", message = "Stem separation queued." });
    }

    /// <summary>
    /// Check stem separation status.
    /// </summary>
    [HttpGet("{id}/stems-status")]
    public IActionResult StemsStatus(Guid id)
    {
        var track = _trackService.GetById(id);
        if (track == null) return NotFound();

        return Ok(new
        {
            ready = track.StemsReady,
            separating = track.StemsSeparating,
            error = track.StemsError,
            stems = track.StemsReady
                ? new[] { "drums", "bass", "other", "vocals" }
                : Array.Empty<string>()
        });
    }

    /// <summary>
    /// Stream a specific stem (drums, bass, other, vocals).
    /// </summary>
    [HttpGet("{id}/stem/{name}")]
    public IActionResult GetStem(Guid id, string name)
    {
        var track = _trackService.GetById(id);
        if (track == null) return NotFound();
        if (!track.StemsReady) return BadRequest("Stems not ready. Run separate first.");

        var allowed = new[] { "drums", "bass", "other", "vocals" };
        if (!allowed.Contains(name.ToLower()))
            return BadRequest($"Invalid stem. Use: {string.Join(", ", allowed)}");

        var stemPath = Path.Combine(track.StemsDirectory!, $"{name.ToLower()}.wav");
        if (!System.IO.File.Exists(stemPath))
            return NotFound($"Stem file not found: {name}");

        var stream = new FileStream(stemPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "audio/wav", enableRangeProcessing: true);
    }

    /// <summary>
    /// Extract N bars from the instrumental (drums+bass+other, no vocals).
    /// Returns the extracted audio as WAV.
    /// </summary>
    [HttpPost("{id}/extract-bars")]
    public IActionResult ExtractBars(Guid id, [FromQuery] int bars = 8)
    {
        var track = _trackService.GetById(id);
        if (track == null) return NotFound();
        if (!track.StemsReady) return BadRequest("Stems not ready. Run separate first.");
        if (bars != 4 && bars != 8 && bars != 16) return BadRequest("bars must be 4, 8, or 16.");

        var bpm = track.DetectedBpm ?? 120;

        // Combine drums + bass + other stems
        var instrumental = CombineStems(track.StemsDirectory!, new[] { "drums", "bass", "other" });
        if (instrumental == null) return StatusCode(500, "Failed to read stems.");

        // Extract N bars
        var secondsPerBar = (60.0 / bpm) * 4;
        var sampleRate = 44100;
        var channels = 2;
        var samplesPerBar = (int)Math.Round(secondsPerBar * sampleRate) * channels;
        var totalSamples = samplesPerBar * bars;

        if (totalSamples > instrumental.Length)
            totalSamples = instrumental.Length;

        var extracted = instrumental[..totalSamples];

        // Write to temp file and return
        var outputPath = Path.Combine(Path.GetTempPath(), $"kitchen_extract_{id}_{bars}bars.wav");
        WriteWav(extracted, outputPath, sampleRate, channels);

        var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "audio/wav", $"intro_{bars}bars.wav", enableRangeProcessing: true);
    }

    /// <summary>
    /// Change BPM of a track and all its stems using time-stretching.
    /// </summary>
    [HttpPost("{id}/change-bpm")]
    public IActionResult ChangeBpm(Guid id, [FromQuery] double targetBpm)
    {
        var track = _trackService.GetById(id);
        if (track == null) return NotFound();

        var originalBpm = track.DetectedBpm ?? 120;
        if (targetBpm < 60 || targetBpm > 200)
            return BadRequest("targetBpm must be between 60 and 200.");

        var pythonPath = FindPython();
        var scriptPath = FindChangeBpmScript();
        if (pythonPath == null || scriptPath == null)
            return StatusCode(500, "Python or change_bpm.py not found.");

        // Update each stem if available
        if (track.StemsReady)
        {
            foreach (var stem in new[] { "drums", "bass", "other", "vocals" })
            {
                var stemPath = Path.Combine(track.StemsDirectory!, $"{stem}.wav");
                var tempPath = stemPath + ".tmp.wav";
                if (RunPythonSync(pythonPath, $"\"{scriptPath}\" \"{stemPath}\" \"{tempPath}\" {originalBpm} {targetBpm}") && System.IO.File.Exists(tempPath))
                {
                    System.IO.File.Copy(tempPath, stemPath, true);
                    System.IO.File.Delete(tempPath);
                }
            }
        }

        _trackService.SetBpm(id, targetBpm);
        return Ok(new { bpm = targetBpm, stemsUpdated = track.StemsReady });
    }

    /// <summary>
    /// Change key of a track and all its stems using pitch shifting.
    /// Semitones: positive = up, negative = down. Or pass targetKey (e.g. "Am").
    /// </summary>
    [HttpPost("{id}/change-key")]
    public IActionResult ChangeKey(Guid id, [FromQuery] int semitones = 0, [FromQuery] string? targetKey = null)
    {
        var track = _trackService.GetById(id);
        if (track == null) return NotFound();

        // Calculate semitones from targetKey if provided
        if (!string.IsNullOrEmpty(targetKey) && !string.IsNullOrEmpty(track.DetectedKey))
        {
            semitones = CalculateSemitones(track.DetectedKey, targetKey);
        }

        if (semitones == 0)
            return Ok(new { key = track.DetectedKey, semitones = 0, message = "No change needed." });

        if (semitones < -12 || semitones > 12)
            return BadRequest("Semitones must be between -12 and 12.");

        var pythonPath = FindPython();
        var scriptPath = FindChangeKeyScript();
        if (pythonPath == null || scriptPath == null)
            return StatusCode(500, "Python or change_key.py not found.");

        // Update each stem
        if (track.StemsReady)
        {
            foreach (var stem in new[] { "drums", "bass", "other", "vocals" })
            {
                var stemPath = Path.Combine(track.StemsDirectory!, $"{stem}.wav");
                var tempPath = stemPath + ".tmp.wav";
                if (RunPythonSync(pythonPath, $"\"{scriptPath}\" \"{stemPath}\" \"{tempPath}\" {semitones}") && System.IO.File.Exists(tempPath))
                {
                    System.IO.File.Copy(tempPath, stemPath, true);
                    System.IO.File.Delete(tempPath);
                }
            }
        }

        // Calculate new key name
        var newKey = ShiftKeyName(track.DetectedKey ?? "C", semitones);
        _trackService.SetKey(id, newKey);
        return Ok(new { key = newKey, semitones, stemsUpdated = track.StemsReady });
    }

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static int CalculateSemitones(string fromKey, string toKey)
    {
        var fromIdx = NoteIndex(fromKey);
        var toIdx = NoteIndex(toKey);
        var diff = (toIdx - fromIdx + 12) % 12;
        return diff > 6 ? diff - 12 : diff;
    }

    private static int NoteIndex(string key)
    {
        var note = key.TrimEnd('m', '#');
        if (key.Contains('#')) note = key.Split('m')[0];
        else note = key.TrimEnd('m');
        return Array.IndexOf(NoteNames, note) is int i and >= 0 ? i : 0;
    }

    private static string ShiftKeyName(string key, int semitones)
    {
        var isMinor = key.EndsWith("m");
        var notePart = isMinor ? key[..^1] : key;
        var idx = Array.IndexOf(NoteNames, notePart);
        if (idx < 0) idx = 0;
        var newIdx = ((idx + semitones) % 12 + 12) % 12;
        return NoteNames[newIdx] + (isMinor ? "m" : "");
    }

    private static bool RunPythonSync(string pythonPath, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return false;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);
        return proc.HasExited && proc.ExitCode == 0;
    }

    /// <summary>
    /// Cook: combine intro from one track with vocals from another.
    /// </summary>
    [HttpPost("cook")]
    public IActionResult Cook(
        [FromQuery] Guid introTrackId,
        [FromQuery] Guid vocalsTrackId,
        [FromQuery] int bars = 8,
        [FromQuery] bool includeVocals = true)
    {
        var introTrack = _trackService.GetById(introTrackId);
        var vocalsTrack = _trackService.GetById(vocalsTrackId);

        if (introTrack == null) return NotFound("Intro track not found.");
        if (vocalsTrack == null) return NotFound("Vocals track not found.");
        if (!introTrack.StemsReady) return BadRequest("Intro track stems not ready.");
        if (includeVocals && !vocalsTrack.StemsReady) return BadRequest("Vocals track stems not ready.");

        var bpm = introTrack.DetectedBpm ?? 120;
        var sampleRate = 44100;
        var channels = 2;

        // 1. Get instrumental intro (drums + bass + other)
        var instrumental = CombineStems(introTrack.StemsDirectory!, new[] { "drums", "bass", "other" });
        if (instrumental == null) return StatusCode(500, "Failed to read intro stems.");

        var secondsPerBar = (60.0 / bpm) * 4;
        var samplesPerBar = (int)Math.Round(secondsPerBar * sampleRate) * channels;
        var introLength = Math.Min(samplesPerBar * bars, instrumental.Length);
        var intro = instrumental[..introLength];

        float[] finalOutput;

        if (includeVocals)
        {
            // 2. Get vocals
            var vocalsPath = Path.Combine(vocalsTrack.StemsDirectory!, "vocals.wav");
            var vocals = ReadWav(vocalsPath);
            if (vocals == null) return StatusCode(500, "Failed to read vocals.");

            // 3. Combine: intro then vocals (with short crossfade)
            var crossfadeSamples = Math.Min(sampleRate * channels / 2, Math.Min(intro.Length, vocals.Length));
            var totalLength = intro.Length + vocals.Length - crossfadeSamples;
            finalOutput = new float[totalLength];

            // Copy intro
            Array.Copy(intro, finalOutput, intro.Length);

            // Crossfade and add vocals
            var overlapStart = intro.Length - crossfadeSamples;
            for (int i = 0; i < crossfadeSamples; i++)
            {
                var t = (float)i / crossfadeSamples;
                finalOutput[overlapStart + i] = intro[overlapStart + i] * (1 - t) + vocals[i] * t;
            }

            // Copy rest of vocals
            if (vocals.Length > crossfadeSamples)
            {
                Array.Copy(vocals, crossfadeSamples, finalOutput, intro.Length, vocals.Length - crossfadeSamples);
            }
        }
        else
        {
            finalOutput = intro;
        }

        // Write output
        var outputId = Guid.NewGuid();
        var outputPath = _fileStorageService.GetKitchenOutputPath(outputId);
        WriteWav(finalOutput, outputPath, sampleRate, channels);

        var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "audio/wav", "kitchen_output.wav", enableRangeProcessing: true);
    }

    /// <summary>
    /// Analyze a track (BPM, key, genre) if not already done.
    /// </summary>
    [HttpPost("{id}/analyze")]
    public IActionResult AnalyzeTrack(Guid id)
    {
        var track = _trackService.GetById(id);
        if (track == null) return NotFound();

        if (track.DetectedBpm.HasValue)
            return Ok(new { bpm = track.DetectedBpm, genre = track.DetectedGenre, key = track.DetectedKey });

        // Quick analysis using Python
        var pythonPath = FindPython();
        var scriptPath = FindAnalyzeScript();
        if (pythonPath == null || scriptPath == null)
            return StatusCode(500, "Python or analyze.py not found.");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\" \"{track.FilePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = System.Diagnostics.Process.Start(psi);
        if (process == null) return StatusCode(500, "Failed to start Python.");

        // Read stderr async to avoid deadlock (stdout can be large)
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(120_000);
        var stderr = stderrTask.Result;

        if (!process.HasExited || process.ExitCode != 0)
            return StatusCode(500, "Analysis failed.");

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var bpm = root.GetProperty("bpm").GetDouble();
            var key = root.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            var genre = root.TryGetProperty("genre", out var g) ? g.GetString() ?? "" : "";

            // Extract bar boundaries for waveform markers
            var bars = new List<double[]>();
            if (root.TryGetProperty("bars", out var barsEl) && barsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var bar in barsEl.EnumerateArray())
                {
                    if (bar.ValueKind == JsonValueKind.Array && bar.GetArrayLength() >= 2)
                        bars.Add(new[] { bar[0].GetDouble(), bar[1].GetDouble() });
                }
            }

            var duration = root.TryGetProperty("duration", out var dur) ? dur.GetDouble() : 0;

            _trackService.SetBpm(id, bpm);
            if (!string.IsNullOrEmpty(key)) _trackService.SetKey(id, key);
            if (!string.IsNullOrEmpty(genre)) _trackService.SetGenre(id, genre);

            return Ok(new { bpm, genre, key, duration, barCount = bars.Count, bars = bars.Take(200) });
        }
        catch
        {
            return StatusCode(500, "Failed to parse analysis result.");
        }
    }

    // --- Helpers ---

    private static float[]? CombineStems(string stemsDir, string[] stemNames)
    {
        float[]? combined = null;
        foreach (var name in stemNames)
        {
            var path = Path.Combine(stemsDir, $"{name}.wav");
            var samples = ReadWav(path);
            if (samples == null) continue;

            if (combined == null)
            {
                combined = new float[samples.Length];
                Array.Copy(samples, combined, samples.Length);
            }
            else
            {
                var len = Math.Min(combined.Length, samples.Length);
                for (int i = 0; i < len; i++)
                    combined[i] += samples[i];
            }
        }
        return combined;
    }

    private static float[]? ReadWav(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            using var reader = new AudioFileReader(path);
            var buf = new float[(int)(reader.Length / (reader.WaveFormat.BitsPerSample / 8))];
            int total = 0, read;
            while ((read = reader.Read(buf, total, Math.Min(8192, buf.Length - total))) > 0)
                total += read;
            return buf[..total];
        }
        catch { return null; }
    }

    private static void WriteWav(float[] samples, string path, int sampleRate, int channels)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(samples, 0, samples.Length);
    }

    private static string? FindPython()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var ver in new[] { "Python313", "Python312", "Python311", "Python310", "Python39" })
        {
            var path = Path.Combine(localAppData, "Programs", ver, "python.exe");
            if (System.IO.File.Exists(path)) return path;
        }
        return null;
    }

    private static string? FindSeparateScript()
    {
        return FindScript("separate.py");
    }

    private static string? FindChangeBpmScript()
    {
        return FindScript("change_bpm.py");
    }

    private static string? FindChangeKeyScript()
    {
        return FindScript("change_key.py");
    }

    private static string? FindAnalyzeScript()
    {
        return FindScript("analyze.py");
    }

    private static string? FindScript(string name)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "scripts", name),
            Path.Combine(AppContext.BaseDirectory, "scripts", name),
            Path.Combine(Directory.GetCurrentDirectory(), "scripts", name),
        };
        foreach (var p in candidates)
        {
            var full = Path.GetFullPath(p);
            if (System.IO.File.Exists(full)) return full;
        }
        return null;
    }

    // --- Mix endpoint: combine selected stems from any decks ---

    public record StemSelection(Guid TrackId, string Stem, double? StartTime = null, double? EndTime = null);
    public record MixRequest(StemSelection[] Stems, double? TargetBpm, int Bars);

    /// <summary>
    /// Mix selected stems from any tracks together.
    /// Optionally cut to N bars and match BPM.
    /// </summary>
    [HttpPost("mix")]
    public IActionResult Mix([FromBody] MixRequest request)
    {
        if (request.Stems == null || request.Stems.Length == 0)
            return BadRequest("No stems selected.");

        var validStems = new[] { "drums", "bass", "other", "vocals" };
        var sampleRate = 44100;
        var channels = 2;
        float[]? combined = null;

        foreach (var sel in request.Stems)
        {
            var track = _trackService.GetById(sel.TrackId);
            if (track == null) return NotFound($"Track {sel.TrackId} not found.");
            if (!track.StemsReady) return BadRequest($"Stems not ready for track {sel.TrackId}.");
            if (!validStems.Contains(sel.Stem.ToLower()))
                return BadRequest($"Invalid stem '{sel.Stem}'.");

            var stemPath = Path.Combine(track.StemsDirectory!, $"{sel.Stem.ToLower()}.wav");
            var samples = ReadWav(stemPath);
            if (samples == null) return StatusCode(500, $"Failed to read {sel.Stem} stem.");

            // Slice to selected region if specified
            if (sel.StartTime.HasValue || sel.EndTime.HasValue)
            {
                var startSample = sel.StartTime.HasValue ? (int)(sel.StartTime.Value * sampleRate) * channels : 0;
                var endSample = sel.EndTime.HasValue ? (int)(sel.EndTime.Value * sampleRate) * channels : samples.Length;
                startSample = Math.Clamp(startSample, 0, samples.Length);
                endSample = Math.Clamp(endSample, startSample, samples.Length);
                samples = samples[startSample..endSample];
            }

            if (combined == null)
            {
                combined = new float[samples.Length];
                Array.Copy(samples, combined, samples.Length);
            }
            else
            {
                var len = Math.Min(combined.Length, samples.Length);
                // Extend if this stem is longer
                if (samples.Length > combined.Length)
                {
                    var extended = new float[samples.Length];
                    Array.Copy(combined, extended, combined.Length);
                    combined = extended;
                }
                for (int i = 0; i < len; i++)
                    combined[i] += samples[i];
            }
        }

        if (combined == null) return BadRequest("No audio produced.");

        // Cut to bars if requested
        if (request.Bars > 0)
        {
            // Use BPM from first track or target BPM
            var firstTrack = _trackService.GetById(request.Stems[0].TrackId);
            var bpm = request.TargetBpm ?? firstTrack?.DetectedBpm ?? 120;
            var secondsPerBar = (60.0 / bpm) * 4;
            var samplesPerBar = (int)Math.Round(secondsPerBar * sampleRate) * channels;
            var cutLength = Math.Min(samplesPerBar * request.Bars, combined.Length);
            combined = combined[..cutLength];
        }

        // Soft clip
        for (int i = 0; i < combined.Length; i++)
        {
            if (combined[i] > 0.95f) combined[i] = 0.95f + (combined[i] - 0.95f) * 0.1f;
            if (combined[i] < -0.95f) combined[i] = -0.95f + (combined[i] + 0.95f) * 0.1f;
        }

        var outputPath = _fileStorageService.GetKitchenOutputPath(Guid.NewGuid());
        WriteWav(combined, outputPath, sampleRate, channels);

        var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "audio/wav", "kitchen_mix.wav", enableRangeProcessing: true);
    }
}
