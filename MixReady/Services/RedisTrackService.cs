using System.Text.Json;
using MixReady.Models;
using StackExchange.Redis;

namespace MixReady.Services;

/// <summary>
/// Redis-backed TrackService for multi-container QA/production deployments.
/// Same interface as the in-memory TrackService — selected via MIXREADY_STORE=redis.
/// </summary>
public class RedisTrackService : ITrackService
{
    private readonly IDatabase _db;
    private const string Prefix = "track:";
    private const string IndexKey = "tracks:ids";

    public RedisTrackService(IConnectionMultiplexer redis)
    {
        _db = redis.GetDatabase();
    }

    private static string Key(Guid id) => $"{Prefix}{id}";

    private static string Serialize(Track t) => JsonSerializer.Serialize(t);

    private static Track? Deserialize(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Track>(json);

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

        _db.StringSet(Key(track.Id), Serialize(track));
        _db.SetAdd(IndexKey, track.Id.ToString());
        return track;
    }

    public Track? GetById(Guid id)
    {
        var json = _db.StringGet(Key(id));
        return Deserialize(json);
    }

    private void Update(Guid id, Action<Track> mutate)
    {
        var track = GetById(id);
        if (track == null) return;
        mutate(track);
        _db.StringSet(Key(id), Serialize(track));
    }

    public void SetProcessedPath(Guid id, string processedFilePath) =>
        Update(id, t => t.ProcessedFilePath = processedFilePath);

    public void SetBpm(Guid id, double bpm) =>
        Update(id, t => t.DetectedBpm = bpm);

    public void SetGenre(Guid id, string genre) =>
        Update(id, t => t.DetectedGenre = genre);

    public void SetKey(Guid id, string key) =>
        Update(id, t => t.DetectedKey = key);

    public void SetStatus(Guid id, TrackStatus status, string? errorMessage = null) =>
        Update(id, t =>
        {
            t.Status = status;
            t.ErrorMessage = errorMessage;
            if (status == TrackStatus.Queued)
                t.QueuedAt = DateTime.UtcNow;
        });

    public int CountByStatus(TrackStatus status, Guid? beforeId = null)
    {
        var ids = _db.SetMembers(IndexKey);
        var tracks = ids
            .Select(v => GetById(Guid.Parse(v!)))
            .Where(t => t != null && t.Status == status)
            .ToList();

        if (beforeId == null)
            return tracks.Count;

        var refTrack = GetById(beforeId.Value);
        if (refTrack?.QueuedAt == null)
            return tracks.Count;

        return tracks.Count(t => t!.QueuedAt < refTrack.QueuedAt);
    }

    public void SetStemsDirectory(Guid id, string stemsDir) =>
        Update(id, t =>
        {
            t.StemsDirectory = stemsDir;
            t.StemsSeparating = false;
            t.StemsError = null;
        });

    public void SetStemsError(Guid id, string error) =>
        Update(id, t =>
        {
            t.StemsError = error;
            t.StemsSeparating = false;
        });

    public void SetStemsSeparating(Guid id, bool separating) =>
        Update(id, t =>
        {
            t.StemsSeparating = separating;
            if (separating) t.StemsError = null;
        });
}
