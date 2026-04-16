namespace MixReady.Models;

public enum TrackStatus
{
    Uploaded,
    Queued,
    Processing,
    Completed,
    Failed
}

public class Track
{
    public Guid Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? ProcessedFilePath { get; set; }
    public TrackStatus Status { get; set; } = TrackStatus.Uploaded;
    public double? DetectedBpm { get; set; }
    public string? DetectedGenre { get; set; }
    public string? DetectedKey { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? QueuedAt { get; set; }
    public string? StemsDirectory { get; set; }
    public string? StemsError { get; set; }
    public bool StemsReady => !string.IsNullOrEmpty(StemsDirectory) && Directory.Exists(StemsDirectory);
    public bool StemsSeparating { get; set; }
}
