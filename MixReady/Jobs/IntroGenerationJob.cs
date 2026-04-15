using MixReady.Helpers;
using MixReady.Helpers;
using MixReady.Models;
using MixReady.Services;
using MixReady.Storage;

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

    public Task Execute(Guid trackId, string? genreOverride = null, bool useGrooveExtraction = true, int extractBars = 8, bool loop = false, bool introOnly = false)
    {
        var track = _trackService.GetById(trackId)
            ?? throw new InvalidOperationException($"Track {trackId} not found.");

        _trackService.SetStatus(trackId, TrackStatus.Processing);

        try
        {
            var outputPath = _fileStorageService.GetProcessedPath(trackId);

            var (detectedBpm, detectedGenre, detectedKey) = IntroGenerator.Generate(
                track.FilePath, outputPath, genreOverride: genreOverride,
                useGrooveExtraction: useGrooveExtraction, extractBars: extractBars,
                loop: loop, introOnly: introOnly);

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
