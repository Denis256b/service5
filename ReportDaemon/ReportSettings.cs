namespace ReportDaemon;

/// <summary>
/// Настройки формирования отчетов.
/// </summary>
public class ReportSettings
{
    /// <summary>
    /// Интервал опроса базы данных на новые задания в секундах.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Тайм-аут обработки задания в минутах (зависшие задания сбрасываются в pending).
    /// Все экземпляры ReportDaemon обязаны иметь одинаковое значение —
    /// иначе экземпляр с меньшим тайм-аутом может сбросить задание, которое другой честно обрабатывает.
    /// </summary>
    public int ProcessingTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// Максимальное количество отчетов, формируемых параллельно (минимум 1).
    /// </summary>
    public int MaxParallelReports { get; set; } = 3;
}
