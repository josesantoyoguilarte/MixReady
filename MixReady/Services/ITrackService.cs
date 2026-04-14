using MixReady.Models;

namespace MixReady.Services;

public interface ITrackService
{
    Track Create(string originalFileName, string filePath);
    Track? GetById(Guid id);
    void SetProcessedPath(Guid id, string processedFilePath);
    void SetBpm(Guid id, double bpm);
    void SetGenre(Guid id, string genre);
    void SetKey(Guid id, string key);
    void SetStatus(Guid id, TrackStatus status, string? errorMessage = null);
}
