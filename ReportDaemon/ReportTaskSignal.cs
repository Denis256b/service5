namespace ReportDaemon;

/// <summary>
/// Сигнал «появилось новое задание отчёта» для мгновенного пробуждения опросного цикла.
/// Отправляется слушателем уведомлений PostgreSQL (LISTEN/NOTIFY). Идемпотентен:
/// повторная отправка до ожидания не накапливается — достаточно одного сигнала,
/// чтобы цикл проснулся и забрал все ожидающие задания атомарным claim.
/// </summary>
public sealed class ReportTaskSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    /// <summary>
    /// Отправляет сигнал опросному циклу (не блокирует, идемпотентен).
    /// </summary>
    public void Signal()
    {
        if (_semaphore.CurrentCount == 0)
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Ждёт сигнала или тайм-аута. Возвращает true, если сигнал получен, false — по тайм-ауту.
    /// Бросает OperationCanceledException при отмене (остановка демона).
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await _semaphore.WaitAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Освобождает ресурсы.
    /// </summary>
    public void Dispose() => _semaphore.Dispose();
}
