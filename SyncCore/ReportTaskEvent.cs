using System;

namespace SyncCore;

/// <summary>
/// Событие аудита задания на формирование отчета (переход статуса).
/// </summary>
public class ReportTaskEvent
{
    /// <summary>
    /// Уникальный идентификатор события.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Идентификатор задания (ReportTasks.Id).
    /// </summary>
    public long TaskId { get; set; }

    /// <summary>
    /// Статус, в который переведено задание: created, pending, processing, done, error, cancelled, retry.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Время события (UTC).
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Краткое сообщение о событии.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Инициатор события: "daemon" для системных переходов, имя пользователя — для действий из UI.
    /// </summary>
    public string? Actor { get; set; }
}
