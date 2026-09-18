namespace SyncDaemon;

/// <summary>
/// Целевая база данных для записи.
/// </summary>
public interface IDestinationDb
{
    Task<int> InsertAsync(IEnumerable<SyncRecord> records, CancellationToken ct);
    Task<int> UpdateAsync(IEnumerable<SyncRecord> records, CancellationToken ct);
    Task<int> DeleteAsync(IEnumerable<string> primaryKeys, CancellationToken ct);
    Task<IEnumerable<SyncRecord>> ReadAllAsync(int batchSize, CancellationToken ct);
}