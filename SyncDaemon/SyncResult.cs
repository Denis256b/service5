using System;

namespace SyncDaemon;

/// <summary>
/// Результат синхронизации.
/// </summary>
public class SyncResult
{
    /// <summary>
    /// Время следующей синхронизации.
    /// </summary>
    public DateTime NextSync { get; set; }

    /// <summary>
    /// Количество вставленных записей.
    /// </summary>
    public int Inserted { get; set; }

    /// <summary>
    /// Количество обновленных записей.
    /// </summary>
    public int Updated { get; set; }
}