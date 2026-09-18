using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncCore;

namespace SyncDaemon
{
    /// <summary>
    /// Сервис оркестрации синхронизации.
    /// Хранит состояние последнего sync и координирует SyncEngine.
    /// </summary>
    public class SyncService : ISyncService, IDisposable
    {
        private readonly ISyncEngine _engine;
        private readonly ILogger<SyncService> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private DateTime _lastSyncTime;
        private int _intervalSeconds;
        private bool _isRunning;
        private Task? _syncTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly List<SyncHistoryEntry> _history;
        private int _totalInserted;
        private int _totalUpdated;

        public SyncService(ISyncEngine engine, ILogger<SyncService> logger, IServiceScopeFactory serviceScopeFactory)
        {
            _engine = engine;
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
            _lastSyncTime = DateTime.UtcNow; // начинаем с текущего времени
            _history = new List<SyncHistoryEntry>();
            _totalInserted = 0;
            _totalUpdated = 0;
        }

        /// <summary>
        /// Возвращает true, если синхронизация запущена.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Запускает периодическую синхронизацию.
        /// </summary>
        /// <param name="intervalSeconds">Интервал синхронизации в секундах.</param>
        public void Start(int intervalSeconds)
        {
            if (_isRunning)
                return;

            _intervalSeconds = intervalSeconds;
            _isRunning = true;
            
            _cancellationTokenSource = new CancellationTokenSource();
            _syncTask = Task.Run(() => RunSyncLoopAsync(_cancellationTokenSource.Token));
        }

        /// <summary>
        /// Останавливает периодическую синхронизацию.
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _cancellationTokenSource?.Cancel();
            await _syncTask!;
            _cancellationTokenSource?.Dispose();
        }

        /// <summary>
        /// Выполняет одну итерацию синхронизации.
        /// </summary>
        /// <returns>Результат последней итерации.</returns>
        public async Task<SyncHistoryEntry> ExecuteOnceAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            SyncHistoryEntry? result = null;
            
            try
            {
                _logger.LogInformation("Executing sync, lastSyncTime={LastSync}", _lastSyncTime);
                
                var syncResult = await _engine.SyncAsync(_lastSyncTime, CancellationToken.None);
                
                _lastSyncTime = syncResult.NextSync;
                _totalInserted += syncResult.Inserted;
                _totalUpdated += syncResult.Updated;

                result = new SyncHistoryEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Trigger = "manual",
                    Inserted = syncResult.Inserted,
                    Updated = syncResult.Updated,
                    Total = syncResult.Inserted + syncResult.Updated,
                    Duration = stopwatch.ElapsedMilliseconds,
                    Status = "success"
                };

                _history.Add(result);
                
                // Сохраняем в БД
                await SaveStatusAndHistoryAsync(result, syncResult.Inserted, syncResult.Updated);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync failed");
                
                result = new SyncHistoryEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Trigger = "manual",
                    Inserted = 0,
                    Updated = 0,
                    Total = 0,
                    Duration = stopwatch.ElapsedMilliseconds,
                    Status = "error"
                };

                _history.Add(result);
                
                // Сохраняем в БД
                await SaveStatusAndHistoryAsync(result, 0, 0);
            }

            return result;
        }

        /// <summary>
        /// Возвращает текущий статус синхронизации.
        /// </summary>
        /// <param name="intervalSeconds">Интервал синхронизации в секундах.</param>
        /// <param name="batchSize">Размер пакета для синхронизации.</param>
        /// <returns>Статус синхронизации.</returns>
        public SyncStatus GetStatus(int intervalSeconds, int batchSize)
        {
            var uptime = _isRunning ? (long)(DateTime.UtcNow - _lastSyncTime).TotalSeconds : 0;
            
            return new SyncStatus
            {
                IsRunning = _isRunning,
                StartTime = _lastSyncTime,
                TotalInserted = _totalInserted,
                TotalUpdated = _totalUpdated,
                TotalCopied = _totalInserted + _totalUpdated,
                LastSyncTime = _lastSyncTime,
                IntervalSeconds = intervalSeconds,
                BatchSize = batchSize,
                Uptime = uptime
            };
        }

        /// <summary>
        /// Возвращает историю синхронизаций.
        /// </summary>
        /// <returns>Список записей истории.</returns>
        public List<SyncHistoryEntry> GetHistory()
        {
            return new List<SyncHistoryEntry>(_history);
        }

        /// <summary>
        /// Восстанавливает состояние из БД при старте.
        /// </summary>
        public async Task RestoreFromDatabaseAsync()
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SyncBusContext>();
            
            try
            {
                var status = await context.SyncStatus.FirstOrDefaultAsync();
                if (status != null)
                {
                    _lastSyncTime = status.LastSyncTime;
                    _totalInserted = status.TotalInserted;
                    _totalUpdated = status.TotalUpdated;
                    _isRunning = status.IsRunning;
                    
                    // Если демон запущен, перезапускаем синхронизацию
                    if (_isRunning)
                    {
                        _cancellationTokenSource = new CancellationTokenSource();
                        _syncTask = Task.Run(() => RunSyncLoopAsync(_cancellationTokenSource.Token));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore state from database");
            }
        }

        private async Task SaveStatusAndHistoryAsync(SyncHistoryEntry historyEntry, int inserted, int updated)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SyncBusContext>();
            
            try
            {
                // Сохраняем историю
                await context.SyncHistory.AddAsync(historyEntry);
                
                // Обновляем статус
                var status = await context.SyncStatus.FirstOrDefaultAsync();
                if (status == null)
                {
                    status = new SyncStatus
                    {
                        Id = 1,
                        IsRunning = _isRunning,
                        LastSyncTime = _lastSyncTime,
                        TotalInserted = _totalInserted,
                        TotalUpdated = _totalUpdated,
                        IntervalSeconds = _intervalSeconds,
                        BatchSize = 100, // дефолтный размер пакета
                        UpdatedAt = DateTime.UtcNow
                    };
                    context.SyncStatus.Add(status);
                }
                else
                {
                    status.IsRunning = _isRunning;
                    status.LastSyncTime = _lastSyncTime;
                    status.TotalInserted = _totalInserted;
                    status.TotalUpdated = _totalUpdated;
                    status.IntervalSeconds = _intervalSeconds;
                    status.BatchSize = 100;
                    status.UpdatedAt = DateTime.UtcNow;
                }
                
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save status and history to database");
            }
        }

        private async Task RunSyncLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    _logger.LogInformation("Periodic sync started, lastSyncTime={LastSync}", _lastSyncTime);
                    
                    var syncResult = await _engine.SyncAsync(_lastSyncTime, ct);
                    
                    _lastSyncTime = syncResult.NextSync;
                    _totalInserted += syncResult.Inserted;
                    _totalUpdated += syncResult.Updated;

                    var historyEntry = new SyncHistoryEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Trigger = "automatic",
                        Inserted = syncResult.Inserted,
                        Updated = syncResult.Updated,
                        Total = syncResult.Inserted + syncResult.Updated,
                        Duration = stopwatch.ElapsedMilliseconds,
                        Status = "success"
                    };

                    _history.Add(historyEntry);
                    
                    // Сохраняем в БД
                    await SaveStatusAndHistoryAsync(historyEntry, syncResult.Inserted, syncResult.Updated);
                    
                    _logger.LogInformation(
                        "Periodic sync completed: inserted={Inserted}, updated={Updated}, duration={Duration}ms",
                        syncResult.Inserted, syncResult.Updated, stopwatch.ElapsedMilliseconds);

                    await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Periodic sync failed");
                    // Continue with next iteration even if there's an error
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Dispose();
        }
    }
}