namespace ReportDaemon;

/// <summary>
/// Уникальный идентификатор экземпляра демона (генерируется один раз на старт процесса).
/// Используется для пометки заданий, взятых в обработку (ReportTasks.OwnerInstanceId),
/// и для исключения собственных заданий из сброса зависших (stale-reset).
/// </summary>
public class InstanceIdProvider
{
    /// <summary>
    /// Идентификатор экземпляра: 32-символьная hex-строка (влезает в varchar(36)).
    /// </summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
}
