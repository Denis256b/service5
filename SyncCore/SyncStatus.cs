using System;

namespace SyncCore;

/// <summary>
/// Статус синхронизации для отображения в интерфейсе.
/// </summary>
public class SyncStatus
{
    /// <summary>
    /// Уникальный идентификатор записи.
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// Возвращает true, если синхронизация выполняется.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Время запуска синхронизации.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Общее количество вставленных записей.
    /// </summary>
    public int TotalInserted { get; set; }

    /// <summary>
    /// Общее количество обновленных записей.
    /// </summary>
    public int TotalUpdated { get; set; }

    /// <summary>
    /// Общее количество скопированных записей.
    /// </summary>
    public int TotalCopied { get; set; }

    /// <summary>
    /// Время последней синхронизации.
    /// </summary>
    public DateTime LastSyncTime { get; set; }

    /// <summary>
    /// Интервал синхронизации в секундах.
    /// </summary>
    public int IntervalSeconds { get; set; }

    /// <summary>
    /// Размер пакета для синхронизации.
    /// </summary>
    public int BatchSize { get; set; }

    /// <summary>
    /// Время работы в секундах.
    /// </summary>
    public long Uptime { get; set; }
    
    /// <summary>
    /// Время последнего обновления записи.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}