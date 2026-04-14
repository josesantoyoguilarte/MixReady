using System.Collections.Concurrent;
using MixReady.Models;

namespace MixReady.Services;

public class TrackService : ITrackService
{
    private static readonly ConcurrentDictionary<Guid, Track> _tracks = new();

    public Track Create(string originalFileName, string filePath)
    {
        var track = new Track
        {
            Id = Guid.NewGuid(),
            OriginalFileName = originalFileName,
            FilePath = filePath,
            Status = TrackStatus.Uploaded,
            CreatedAt = DateTime.UtcNow
        };

        _tracks[track.Id] = track;
        return track;
    }

    public Track? GetById(Guid id)
    {
        _tracks.TryGetValue(id, out var track);
        return track;
    }

    public void SetProcessedPath(Guid id, string processedFilePath)
    {
        if (_tracks.TryGetValue(id, out var track))
        {
            track.ProcessedFilePath = processedFilePath;
        }
    }

    public void SetBpm(Guid id, double bpm)
    {
        if (_tracks.TryGetValue(id, out var track))
        {
            track.DetectedBpm = bpm;
        }
    }

    public void SetGenre(Guid id, string genre)
    {
        if (_tracks.TryGetValue(id, out var track))
        {
            track.DetectedGenre = genre;
        }
    }

    public void SetKey(Guid id, string key)
    {
        if (_tracks.TryGetValue(id, out var track))
        {
            track.DetectedKey = key;
        }
    }

    public void SetStatus(Guid id, TrackStatus status, string? errorMessage = null)
    {
        if (_tracks.TryGetValue(id, out var track))
        {
            track.Status = status;
            track.ErrorMessage = errorMessage;
        }
    }
}
