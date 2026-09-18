using System;

namespace SyncDaemon;

/// <summary>
/// Базовый класс записи для синхронизации.
/// </summary>
public class SyncRecord
{
    public string PrimaryKey { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public string Data { get; set; } = string.Empty;

    public override bool Equals(object? obj)
    {
        return obj is SyncRecord other && PrimaryKey == other.PrimaryKey;
    }

    public override int GetHashCode()
    {
        return PrimaryKey.GetHashCode();
    }

    public override string ToString()
    {
        return $"{PrimaryKey} (modified: {LastModified:O})";
    }
}