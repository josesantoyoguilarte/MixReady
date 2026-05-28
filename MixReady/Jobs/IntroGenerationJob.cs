using MixReady.Helpers;
using MixReady.Models;
using MixReady.Services;

namespace MixReady.Jobs;

public class IntroGenerationJob
{
    private readonly ITrackService _trackService;
    private readonly IFileStorageService _fileStorageService;

    public IntroGenerationJob(ITrackService trackService, IFileStorageService fileStorageService)
    {
        _trackService = trackService;
        _fileStorageService = fileStorageService;
    }

    public Task Execute(Guid trackId, string? genreOverride = null, bool useGrooveExtraction = true, int extractBars = 8, bool loop = false, bool introOnly = false, bool skipOriginalIntro = false, string? stems = null, double? regionStart = null, double? regionEnd = null, double? songStart = null, string transition = "crossfade", int transitionBars = 2)
    {
        var track = _trackService.GetById(trackId)
            ?? throw new InvalidOperationException($"Track {trackId} not found.");

        _trackService.SetStatus(trackId, TrackStatus.Processing);

        try
        {
            var outputPath = _fileStorageService.GetProcessedPath(trackId);

            // Parse selected stems
            string[]? selectedStems = null;
            if (!string.IsNullOrEmpty(stems))
                selectedStems = stems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var (detectedBpm, detectedGenre, detectedKey) = IntroGenerator.Generate(
                track.FilePath, outputPath, genreOverride: genreOverride,
                useGrooveExtraction: useGrooveExtraction, extractBars: extractBars,
                loop: loop, introOnly: introOnly, skipOriginalIntro: skipOriginalIntro,
                stemsDirectory: track.StemsReady ? track.StemsDirectory : null,
                selectedStems: selectedStems,
                regionStartSeconds: regionStart,
                regionEndSeconds: regionEnd,
                songStartSeconds: songStart,
                transition: transition,
                transitionBars: transitionBars);

            _trackService.SetBpm(trackId, detectedBpm);
            _trackService.SetGenre(trackId, detectedGenre);
            _trackService.SetKey(trackId, detectedKey);
            _trackService.SetProcessedPath(trackId, outputPath);
            _trackService.SetStatus(trackId, TrackStatus.Completed);
        }
        catch (Exception ex)
        {
            _trackService.SetStatus(trackId, TrackStatus.Failed, ex.Message);
            throw;
        }

        return Task.CompletedTask;
    }
}
