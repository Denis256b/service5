namespace SyncManager.Models;

/// <summary>
/// Запрос на создание задания на формирование отчета.
/// </summary>
public class CreateReportTaskRequest
{
    /// <summary>
    /// Тип отчета (например, "sales", "inventory").
    /// </summary>
    public string ReportType { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор провайдера-исполнителя.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Произвольная нагрузка (параметры формирования отчета).
    /// </summary>
    public string? Payload { get; set; }
}
