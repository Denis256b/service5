using System;

namespace SyncCore;

/// <summary>
/// Задание на формирование отчета.
/// </summary>
public class ReportTask
{
    /// <summary>
    /// Уникальный идентификатор задания.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Тип отчета (например, "sales", "inventory").
    /// </summary>
    public string ReportType { get; set; } = string.Empty;

    /// <summary>
    /// Параметры формирования отчета (произвольная нагрузка).
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>
    /// Идентификатор провайдера отчетов, который должен обработать задание
    /// (совпадает со значением IReportProvider.ProviderId).
    /// </summary>
    public string? ProviderId { get; set; }

    /// <summary>
    /// Статус задания: pending, processing, done, error, cancelled.
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>
    /// Время создания задания.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Время начала обработки задания.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Путь к сформированному файлу отчета (.xls).
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Результат обработки или текст ошибки.
    /// </summary>
    public string? Result { get; set; }

    /// <summary>
    /// Количество повторных попыток обработки (информационный счётчик, лимитов нет).
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Идентификатор экземпляра ReportDaemon, взявшего задание в обработку
    /// (устанавливается атомарным claim; null — задание не обработано ни одним экземпляром).
    /// </summary>
    public string? OwnerInstanceId { get; set; }
}
