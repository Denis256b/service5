using System;

namespace SyncCore;

public class SyncCommand
{
    public long Id { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string? Payload { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? Result { get; set; }
}