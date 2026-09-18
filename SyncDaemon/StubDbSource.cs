using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SyncDaemon;

/// <summary>
/// In-memory заглушка, реализующая оба интерфейса.
/// </summary>
public class StubDbSource : ISourceDb, IDestinationDb
{
    private readonly ConcurrentDictionary<string, SyncRecord> _records;
    private readonly string _name;
    private readonly ILogger<StubDbSource> _logger;

    public StubDbSource(string name, ILogger<StubDbSource> logger)
    {
        _records = new ConcurrentDictionary<string, SyncRecord>();
        _name = name;
        _logger = logger;
    }

    public Task<IEnumerable<SyncRecord>> ReadAsync(DateTime? since, int batchSize, CancellationToken ct)
    {
        var records = _records.Values
            .Where(r => since == null || r.LastModified > since)
            .OrderBy(r => r.LastModified)
            .Take(batchSize)
            .ToList();

        _logger.LogInformation("Read {Count} records from {Name}", records.Count, _name);
        return Task.FromResult<IEnumerable<SyncRecord>>(records);
    }

    public Task<int> InsertAsync(IEnumerable<SyncRecord> records, CancellationToken ct)
    {
        var count = 0;
        foreach (var record in records)
        {
            _records[record.PrimaryKey] = record;
            count++;
        }
        
        _logger.LogInformation("Inserted {Count} records into {Name}", count, _name);
        return Task.FromResult(count);
    }

    public Task<int> UpdateAsync(IEnumerable<SyncRecord> records, CancellationToken ct)
    {
        var count = 0;
        foreach (var record in records)
        {
            if (_records.TryGetValue(record.PrimaryKey, out var existing))
            {
                // Обновляем существующую запись
                _records[record.PrimaryKey] = record;
                count++;
            }
        }
        
        _logger.LogInformation("Updated {Count} records in {Name}", count, _name);
        return Task.FromResult(count);
    }

    public Task<int> DeleteAsync(IEnumerable<string> primaryKeys, CancellationToken ct)
    {
        var count = 0;
        foreach (var key in primaryKeys)
        {
            if (_records.TryRemove(key, out _))
            {
                count++;
            }
        }
        
        _logger.LogInformation("Deleted {Count} records from {Name}", count, _name);
        return Task.FromResult(count);
    }

    public Task<IEnumerable<SyncRecord>> ReadAllAsync(int batchSize, CancellationToken ct)
    {
        var records = _records.Values
            .OrderBy(r => r.LastModified)
            .Take(batchSize)
            .ToList();

        _logger.LogInformation("Read all {Count} records from {Name}", records.Count, _name);
        return Task.FromResult<IEnumerable<SyncRecord>>(records);
    }
}