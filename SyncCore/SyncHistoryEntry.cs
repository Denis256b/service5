using System;

namespace SyncCore;

/// <summary>
/// Запись истории синхронизации.
/// </summary>
public class SyncHistoryEntry
{
    /// <summary>
    /// Уникальный идентификатор записи.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Время выполнения синхронизации.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Триггер синхронизации (ручная, автоматическая).
    /// </summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// Количество вставленных записей.
    /// </summary>
    public int Inserted { get; set; }

    /// <summary>
    /// Количество обновленных записей.
    /// </summary>
    public int Updated { get; set; }

    /// <summary>
    /// Общее количество обработанных записей.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Длительность синхронизации в миллисекундах.
    /// </summary>
    public long Duration { get; set; }

    /// <summary>
    /// Статус выполнения (успех, ошибка).
    /// </summary>
    public string Status { get; set; } = string.Empty;
}