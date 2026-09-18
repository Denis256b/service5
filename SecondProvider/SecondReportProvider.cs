using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using ReportContracts;

namespace SecondProvider;

/// <summary>
/// Второй тестовый провайдер отчетов (stub). Формирует файл .xls с
/// отличимыми от эталонного провайдера строками данных и префиксом имени
/// файла, чтобы по скачанному файлу было видно, какой провайдер обработал задание.
/// </summary>
public class SecondReportProvider : IReportProvider
{
    private readonly ILogger<SecondReportProvider> _logger;

    public SecondReportProvider(ILogger<SecondReportProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => "sample2";

    /// <inheritdoc />
    public Task<GeneratedReportResult> GenerateAsync(ReportRequest request, CancellationToken ct)
    {
        var fileName = $"report2_{request.ReportType}_{request.TaskId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xls";

        // Заглушка бизнес-логики: генерируем фиктивные строки данных (отличимые от эталонных)
        var rows = BuildStubRows();

        // Формируем содержимое .xls (таблица в формате HTML, открываемая Excel)
        var content = BuildXlsContent(request, rows);
        var bytes = new UTF8Encoding(true).GetBytes(content);

        _logger.LogInformation(
            "Сформирован отчет {FileName} ({RowCount} строк) по заданию {TaskId}",
            fileName, rows.Count, request.TaskId);

        return Task.FromResult(new GeneratedReportResult(bytes, fileName, rows.Count));
    }

    /// <summary>
    /// Заглушка: формирует фиктивный набор строк отчета (отличается от эталонного провайдера).
    /// </summary>
    private static List<string[]> BuildStubRows()
    {
        return new List<string[]>
        {
            new[] { "Второй провайдер: позиция А", "1000", DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd") },
            new[] { "Второй провайдер: позиция Б", "2000", DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd") },
            new[] { "Второй провайдер: позиция В", "3000", DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd") },
        };
    }

    /// <summary>
    /// Собирает содержимое файла .xls (HTML-таблица, открываемая Excel).
    /// </summary>
    private static string BuildXlsContent(ReportRequest request, List<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<html xmlns:x=\"urn:schemas-microsoft-com:office:excel\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta http-equiv=\"Content-Type\" content=\"application/vnd.ms-excel; charset=UTF-8\">");
        sb.AppendLine($"<!-- Отчет сформирован вторым тестовым провайдером (sample2) по заданию {request.TaskId}, тип: {WebUtility.HtmlEncode(request.ReportType)} -->");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<table border=\"1\">");

        // Шапка отчета
        sb.AppendLine("<tr><th>Задание</th><th>Тип отчета</th><th>Дата формирования</th></tr>");
        sb.AppendLine($"<tr><td>{request.TaskId}</td><td>{WebUtility.HtmlEncode(request.ReportType)}</td>" +
                      $"<td>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</td></tr>");

        // Заголовки столбцов данных
        sb.AppendLine("<tr><th>Позиция</th><th>Значение</th><th>Дата</th></tr>");

        // Данные (заглушка)
        foreach (var row in rows)
        {
            sb.AppendLine($"<tr><td>{WebUtility.HtmlEncode(row[0])}</td>" +
                          $"<td>{WebUtility.HtmlEncode(row[1])}</td>" +
                          $"<td>{WebUtility.HtmlEncode(row[2])}</td></tr>");
        }

        sb.AppendLine("</table>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }
}
