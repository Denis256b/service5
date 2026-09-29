using System.Data;
using Npgsql;

namespace NotifyAgent;

/// <summary>
/// Слушатель событий агента: постоянное соединение LISTEN/NOTIFY на каналах
/// <c>sync_ui_changes</c> и <c>report_tasks</c> (паттерн DbChangeListener /
/// ReportNotificationListener). Каждое NOTIFY и catch-up сразу после
/// (пере)подключения будят чтение новых событий по курсорам: SyncHistory — по Id,
/// ReportTaskEvents — по кортежу (Timestamp, Id). Разрешённые события фильтруются
/// по настройкам и передаются в Notifier. При обрыве соединения показывается форма
/// статуса, переподключение через 5 с; опрос идёт на отдельном короткоживущем
/// соединении (команды нельзя выполнять поверх зависшего WaitAsync того же соединения).
/// </summary>
public sealed class EventListener : IDisposable
{
    private const string SyncUiChangesChannel = "sync_ui_changes";
    private const string ReportTasksChannel = "report_tasks";

    // Задержка переподключения при обрыве соединения LISTEN/NOTIFY.
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    // Ограничение длины текста уведомления (~200 символов).
    private const int MaxMessageLength = 200;

    private readonly Func<AgentConfig> _config;
    private readonly EventState _state;
    private readonly Notifier _notifier;
    private readonly string _connectionString;
    private readonly string _statePath;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    /// <summary>
    /// Создаёт слушатель и запускает фоновый цикл (LISTEN/NOTIFY + catch-up по курсорам).
    /// </summary>
    /// <param name="config">Доступ к актуальному конфигу (фильтры читаются на каждое событие).</param>
    /// <param name="state">Состояние курсоров; сохраняется в файл после каждого опроса.</param>
    /// <param name="notifier">Доставка balloon-уведомлений.</param>
    /// <param name="statePath">Полный путь к state.json.</param>
    public EventListener(Func<AgentConfig> config, EventState state, Notifier notifier, string statePath)
    {
        _config = config;
        _state = state;
        _notifier = notifier;
        _connectionString = config().Db.ConnectionString;
        _statePath = statePath;

        AgentLog.Info("Слушатель событий запущен (каналы: sync_ui_changes, report_tasks; доставка по NOTIFY + catch-up)");
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Останавливает цикл (отмена токена) и дожидается завершения.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loopTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Остановка по отмене — не ошибка
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Основной цикл: держит соединение LISTEN/NOTIFY; обрыв → переподключение
    /// через 5 с. Catch-up сразу после (пере)подключения и каждое NOTIFY запускают
    /// опрос новых событий по курсорам.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NpgsqlConnection? listenConnection = null;

            try
            {
                listenConnection = new NpgsqlConnection(_connectionString);
                await listenConnection.OpenAsync(ct);

                // LISTEN принимает ровно одно имя канала — подписываемся отдельными командами.
                foreach (var channel in new[] { SyncUiChangesChannel, ReportTasksChannel })
                {
                    using var listenCommand = new NpgsqlCommand($"LISTEN {channel}", listenConnection);
                    await listenCommand.ExecuteNonQueryAsync(ct);
                }

                AgentLog.Info("Подписка на каналы sync_ui_changes, report_tasks установлена");
                _notifier.ReportConnectionRestored();

                // Catch-up сразу после (пере)подключения: читаем всё накопленное по курсорам.
                await PollEventsSafeAsync(ct);

                // Ожидание ведёт одна долгоживущая задача WaitAsync(token). Задачу нельзя
                // пересоздавать, пока старая ещё не завершилась: второй параллельный вызов
                // WaitAsync на том же соединении запрещён. Тайм-аута нет — доставка только по NOTIFY.
                var waitTask = listenConnection.WaitAsync(ct);

                while (!ct.IsCancellationRequested)
                {
                    if (listenConnection.State != ConnectionState.Open)
                    {
                        throw new InvalidOperationException("Соединение LISTEN/NOTIFY закрыто");
                    }

                    // Ждём асинхронное сообщение (NOTIFY). Исключение здесь означает обрыв
                    // соединения или отмену — их обрабатывает внешний цикл.
                    await waitTask;
                    waitTask = listenConnection.WaitAsync(ct); // старая задача уже завершена

                    // Уведомление — читаем новые события (на отдельном соединении).
                    await PollEventsSafeAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // нормальная остановка
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"Соединение LISTEN/NOTIFY разорвано — переподключение через 5 с: {ex.Message}");
                _notifier.ReportConnectionLost(ex.Message);

                try
                {
                    await Task.Delay(ReconnectDelay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                listenConnection?.Dispose();
            }
        }

        AgentLog.Info("Слушатель событий остановлен");
    }

    /// <summary>
    /// Опрос новых событий на отдельном короткоживущем соединении. Ошибка чтения
    /// логируется и не роняет слушатель (следующее NOTIFY или переподключение повторит попытку).
    /// </summary>
    private async Task PollEventsSafeAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            // Baseline выполняется один раз: до него обе таблицы только читают максимум,
            // чтобы при первом запуске агент не «прокричал» всю историю.
            if (!_state.BaselineDone)
            {
                await InitBaselineAsync(connection, ct);
                _state.Save(_statePath);
                return;
            }

            await PollSyncHistoryAsync(connection, ct);
            await PollReportTaskEventsAsync(connection, ct);

            _state.Save(_statePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // остановка — обрабатывает внешний цикл
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Не удалось прочитать новые события: {ex.Message}");
        }
    }

    /// <summary>
    /// Первичный baseline: курсоры ставятся на максимум существующих записей без
    /// уведомлений. Выполняется один раз (после — BaselineDone = true).
    /// </summary>
    private async Task InitBaselineAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        using var historyCommand = connection.CreateCommand();
        historyCommand.CommandText = "SELECT COALESCE(MAX(\"Id\"), 0) FROM \"SyncHistory\"";
        _state.LastSeenHistoryId = (long)(await historyCommand.ExecuteScalarAsync(ct))!;

        using var eventsCommand = connection.CreateCommand();
        eventsCommand.CommandText = """
            SELECT COALESCE(MAX("Timestamp"), to_timestamp(0)), COALESCE(MAX("Id"), 0)
            FROM "ReportTaskEvents";
            """;

        await using var reader = await eventsCommand.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            _state.LastSeenEventTimestamp = reader.GetDateTime(0).ToUniversalTime();
            _state.LastSeenEventId = reader.GetInt64(1);
        }

        _state.BaselineDone = true;
        AgentLog.Info($"Baseline выполнен: SyncHistory.Id = {_state.LastSeenHistoryId}, ReportTaskEvents.Id = {_state.LastSeenEventId}");
    }

    /// <summary>
    /// Читает новые записи SyncHistory по курсору Id. Уведомляется только статус
    /// error, если включена настройка syncError.
    /// </summary>
    private async Task PollSyncHistoryAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT "Id", "Timestamp", "Trigger", "Status", "Inserted", "Updated"
            FROM "SyncHistory"
            WHERE "Id" > @cursor
            ORDER BY "Id";
            """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("@cursor", _state.LastSeenHistoryId));

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var trigger = reader.GetString(2);
            var status = reader.GetString(3);
            var inserted = reader.GetInt32(4);
            var updated = reader.GetInt32(5);
            _state.LastSeenHistoryId = id;

            if (status != "error" || !_config().SyncError)
            {
                continue;
            }

            var message = Truncate($"Триггер: {trigger}, вставлено: {inserted}, обновлено: {updated}", MaxMessageLength);
            _notifier.Enqueue(new AgentNotification(
                EventId: id,
                SuppressKey: "syncError",
                Title: "Ошибка синхронизации",
                Message: message,
                Url: BuildUrl("/Sync/Dashboard")));
        }
    }

    /// <summary>
    /// Читает новые события аудита ReportTaskEvents по курсору (Timestamp, Id).
    /// Каждое событие = один переход статуса задания; текст ошибки берётся из Message.
    /// </summary>
    private async Task PollReportTaskEventsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT "Id", "TaskId", "Status", "Timestamp", "Message"
            FROM "ReportTaskEvents"
            WHERE ("Timestamp", "Id") > (@cursorTs, @cursorId)
            ORDER BY "Timestamp", "Id";
            """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("@cursorTs", _state.LastSeenEventTimestamp));
        command.Parameters.Add(new NpgsqlParameter("@cursorId", _state.LastSeenEventId));

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var taskId = reader.GetInt64(1);
            var status = reader.GetString(2);
            var timestamp = reader.GetDateTime(3).ToUniversalTime();
            var message = reader.IsDBNull(4) ? null : reader.GetString(4);

            _state.LastSeenEventTimestamp = timestamp;
            _state.LastSeenEventId = id;

            RaiseReportEvent(id, taskId, status, message);
        }
    }

    /// <summary>
    /// Мапит статус события аудита на настройку и создаёт уведомление (если включено).
    /// Дедупликация — по уникальному Id события аудита; ключ подавления повторов —
    /// статус + задание (повтор того же перехода внутри SuppressHours не показывается).
    /// </summary>
    private void RaiseReportEvent(long eventId, long taskId, string status, string? message)
    {
        var cfg = _config();

        (bool Enabled, string Title) notification = status switch
        {
            "error" => (cfg.ReportError, $"Ошибка отчёта №{taskId}"),
            "done" => (cfg.ReportDone, $"Отчёт готов №{taskId}"),
            "processing" => (cfg.ReportProcessing, $"Отчёт в обработке №{taskId}"),
            "cancelled" => (cfg.ReportCancelled, $"Отчёт отменён №{taskId}"),
            "retry" => (cfg.ReportRetry, $"Повтор отчёта №{taskId}"),
            _ => (false, string.Empty) // pending и прочие — не уведомляем
        };

        if (!notification.Enabled)
        {
            return;
        }

        var text = status == "error" && string.IsNullOrEmpty(message) ? "Неизвестная ошибка" : message ?? string.Empty;
        _notifier.Enqueue(new AgentNotification(
            EventId: eventId,
            SuppressKey: $"{status}:task:{taskId}",
            Title: notification.Title,
            Message: Truncate(text, MaxMessageLength),
            Url: BuildUrl("/reports")));
    }

    /// <summary>Собирает адрес веб-панели из BaseUrl (без завершающего слеша).</summary>
    private string BuildUrl(string path)
    {
        var baseUrl = _config().Web.BaseUrl.TrimEnd('/');
        return string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : $"{baseUrl}{path}";
    }

    /// <summary>Обрезает текст до maxLength символов.</summary>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
