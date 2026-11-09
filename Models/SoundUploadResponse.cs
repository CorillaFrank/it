namespace HamulProto.Models;

public sealed class SoundUploadResponse
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Format { get; set; } = "";
    public int DurationMs { get; set; }
    public long SizeBytes { get; set; }
    public string Visibility { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
