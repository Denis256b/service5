namespace SyncDaemon;

/// <summary>
/// Источник данных для чтения изменений.
/// </summary>
public interface ISourceDb
{
    Task<IEnumerable<SyncRecord>> ReadAsync(DateTime? since, int batchSize, CancellationToken ct);
}