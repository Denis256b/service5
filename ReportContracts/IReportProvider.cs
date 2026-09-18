namespace ReportContracts
{
    /// <summary>
    /// Задание на формирование отчета (плоский DTO, без EF Core).
    /// </summary>
    public class ReportRequest
    {
        /// <summary>Уникальный идентификатор задания.</summary>
        public long TaskId { get; }

        /// <summary>Тип отчета (например, "sales", "inventory").</summary>
        public string ReportType { get; }

        /// <summary>Параметры формирования отчета (произвольная нагрузка).</summary>
        public string? Payload { get; }

        public ReportRequest(long taskId, string reportType, string? payload)
        {
            TaskId = taskId;
            ReportType = reportType;
            Payload = payload;
        }
    }

    /// <summary>
    /// Результат сформированного отчета.
    /// </summary>
    public class GeneratedReportResult
    {
        /// <summary>Содержимое отчета в байтах.</summary>
        public byte[] Content { get; }

        /// <summary>Имя файла (для именования при скачивании).</summary>
        public string FileName { get; }

        /// <summary>Количество строк данных в отчете.</summary>
        public int RowCount { get; }

        public GeneratedReportResult(byte[] content, string fileName, int rowCount)
        {
            Content = content;
            FileName = fileName;
            RowCount = rowCount;
        }
    }

    /// <summary>
    /// Провайдер отчетов информационной системы.
    /// Реализации обнаруживаются демоном по этому интерфейсу и регистрируются в DI-контейнере.
    /// </summary>
    public interface IReportProvider
    {
        /// <summary>
        /// Уникальный идентификатор провайдера (совпадает со значением ReportTasks.ProviderId).
        /// </summary>
        string ProviderId { get; }

        /// <summary>
        /// Формирует отчет по заданию и возвращает содержимое отчета и количество строк.
        /// </summary>
        /// <param name="request">Задание на формирование отчета.</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>Содержимое отчета (байты), имя файла и количество строк.</returns>
        Task<GeneratedReportResult> GenerateAsync(ReportRequest request, CancellationToken ct);
    }
}
