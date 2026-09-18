using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SyncCore;

namespace SyncDaemon;

/// <summary>
/// Интерфейс сервиса оркестрации синхронизации.
/// </summary>
public interface ISyncService : IDisposable
{
    /// <summary>
    /// Возвращает true, если синхронизация запущена.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Запускает периодическую синхронизацию.
    /// </summary>
    /// <param name="intervalSeconds">Интервал синхронизации в секундах.</param>
    void Start(int intervalSeconds);

    /// <summary>
    /// Останавливает периодическую синхронизацию.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Выполняет одну итерацию синхронизации.
    /// </summary>
    /// <returns>Результат последней итерации.</returns>
    Task<SyncHistoryEntry> ExecuteOnceAsync();

    /// <summary>
    /// Возвращает текущий статус синхронизации.
    /// </summary>
    /// <param name="intervalSeconds">Интервал синхронизации в секундах.</param>
    /// <param name="batchSize">Размер пакета для синхронизации.</param>
    /// <returns>Статус синхронизации.</returns>
    SyncStatus GetStatus(int intervalSeconds, int batchSize);

    /// <summary>
    /// Возвращает историю синхронизаций.
    /// </summary>
    /// <returns>Список записей истории.</returns>
    List<SyncHistoryEntry> GetHistory();
}