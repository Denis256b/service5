using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace SyncDaemon
{
    /// <summary>
    /// Ядро синхронизации: сравнивает и применяет изменения между источниками.
    /// </summary>
    public class SyncEngine : ISyncEngine
    {
        private readonly ISourceDb _source;
        private readonly IDestinationDb _destination;
        private readonly int _batchSize;
        private readonly ILogger<SyncEngine> _logger;

        public SyncEngine(
            ISourceDb source,
            IDestinationDb destination,
            IOptions<SyncSettings> settings,
            ILogger<SyncEngine> logger)
        {
            _source = source;
            _destination = destination;
            _batchSize = settings?.Value?.BatchSize ?? 100;
            _logger = logger;
        }

        /// <summary>
        /// Выполняет полную итерацию синхронизации.
        /// </summary>
        /// <param name="since">Начальная точка синхронизации (UTC).</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>Результат синхронизации.</returns>
        public async Task<SyncResult> SyncAsync(DateTime since, CancellationToken ct)
        {
            _logger.LogInformation("Sync started since {Since}", since);

            // Шаг 1: Читаем изменения из источника
            var changes = await ReadChangesAsync(since, ct);
            if (!changes.Any())
            {
                _logger.LogInformation("No changes found since {Since}", since);
                return new SyncResult
                {
                    NextSync = DateTime.UtcNow,
                    Inserted = 0,
                    Updated = 0
                };
            }

            _logger.LogInformation("Found {Count} changes", changes.Count);

            // Шаг 2: Разделяем на вставляемые и обновляемые
            var (toInsert, toUpdate) = await ClassifyRecordsAsync(changes, ct);

            // Шаг 3: Применяем вставки
            var inserted = 0;
            if (toInsert.Any())
            {
                inserted = await _destination.InsertAsync(toInsert, ct);
                _logger.LogInformation("Inserted {Count} records into destination", inserted);
            }

            // Шаг 4: Применяем обновления
            var updated = 0;
            if (toUpdate.Any())
            {
                updated = await _destination.UpdateAsync(toUpdate, ct);
                _logger.LogInformation("Updated {Count} records in destination", updated);
            }

            // Шаг 5: Возвращаем результат
            var nextSync = DateTime.UtcNow;
            _logger.LogInformation(
                "Sync completed: inserted={Inserted}, updated={Updated}, nextSync={NextSync}",
                inserted, updated, nextSync);

            return new SyncResult
            {
                NextSync = nextSync,
                Inserted = inserted,
                Updated = updated
            };
        }

        /// <summary>
        /// Читаем изменения из источника пакетами.
        /// </summary>
        private async Task<List<SyncRecord>> ReadChangesAsync(DateTime since, CancellationToken ct)
        {
            var allChanges = new List<SyncRecord>();

            while (true)
            {
                var batch = await _source.ReadAsync(since, _batchSize, ct);
                var batchList = batch.ToList();

                if (!batchList.Any())
                    break;

                allChanges.AddRange(batchList);

                if (batchList.Count < _batchSize)
                    break;
            }

            return allChanges;
        }

        /// <summary>
        /// Определяем, какие записи нужно вставить, а какие обновить.
        /// </summary>
        private async Task<(List<SyncRecord> insert, List<SyncRecord> update)> ClassifyRecordsAsync(
            List<SyncRecord> changes, CancellationToken ct)
        {
            var toInsert = new List<SyncRecord>();
            var toUpdate = new List<SyncRecord>();

            // Получаем все PK из destination для проверки существования
            var existingKeys = await GetExistingKeysAsync(ct);

            foreach (var record in changes)
            {
                if (ct.IsCancellationRequested)
                    break;

                if (existingKeys.Contains(record.PrimaryKey))
                {
                    toUpdate.Add(record);
                }
                else
                {
                    toInsert.Add(record);
                }
            }

            return (toInsert, toUpdate);
        }

        /// <summary>
        /// Получаем список первичных ключей, существующих в destination.
        /// </summary>
        private async Task<HashSet<string>> GetExistingKeysAsync(CancellationToken ct)
        {
            // Для заглушки читаем все записи и собираем PK
            // В реальной реализации здесь был бы оптимизированный запрос
            var allRecords = await _destination.ReadAllAsync(int.MaxValue, ct);
            return new HashSet<string>(allRecords.Select(r => r.PrimaryKey));
        }
    }
}