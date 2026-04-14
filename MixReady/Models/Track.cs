namespace MixReady.Models;

public enum TrackStatus
{
    Uploaded,
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
}
