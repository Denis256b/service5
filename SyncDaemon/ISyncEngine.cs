using System;
using System.Threading;
using System.Threading.Tasks;

namespace SyncDaemon;

/// <summary>
/// Интерфейс движка синхронизации.
/// </summary>
public interface ISyncEngine
{
    /// <summary>
    /// Выполняет одну итерацию синхронизации.
    /// </summary>
    /// <param name="since">Начальная точка синхронизации (UTC).</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат синхронизации.</returns>
    Task<SyncResult> SyncAsync(DateTime since, CancellationToken ct);
}