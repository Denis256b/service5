namespace SyncManager;

/// <summary>
/// Настройки раздела отчётов веб-панели.
/// </summary>
public class ReportUiSettings
{
    /// <summary>
    /// Статический список провайдеров для формы создания задания.
    /// </summary>
    public List<ReportProviderInfo> Providers { get; set; } = new();
}

/// <summary>
/// Описание провайдера отчётов для выпадающего списка в форме.
/// </summary>
public class ReportProviderInfo
{
    /// <summary>
    /// Идентификатор провайдера (совпадает со значением IReportProvider.ProviderId).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Отображаемое имя провайдера.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
