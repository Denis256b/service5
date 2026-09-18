using System.Collections.Concurrent;
using System.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SyncManager.Hubs;

namespace SyncManager.Services;

/// <summary>
/// Фоновый сервис подписки на уведомления PostgreSQL (LISTEN/NOTIFY) для
/// realtime-обновлений веб-панели. Триггеры БД (<c>TRG_SyncStatus_Notify</c>,
/// <c>TRG_SyncHistory_Notify</c>, <c>TRG_ReportTasks_Notify</c>) шлют pg_notify
/// при изменениях SyncStatus, вставке записей SyncHistory и смене статуса заданий
/// отчётов; сервис мапит канал на scope (<c>sync_ui_changes</c> → "sync",
/// <c>report_tasks</c> → "reports") и шлёт клиентам SignalR-хаба сигнал
/// <c>refresh</c> — сами данные браузер запрашивает через REST-эндпоинты.
/// </summary>
/// <remarks>
/// Структура повторяет ReportDaemon/ReportNotificationListener: собственное
/// постоянное соединение (не пул EF Core), ожидание одной долгоживущей задачей
/// NpgsqlConnection.WaitAsync(token) с эмуляцией тайм-аута через Task.Delay
/// (совместимо со сборками Npgsql 4.x, где нет перегрузки WaitAsync(timeout, token)).
/// При обрыве соединения сервис автоматически переподключается через 5 с; пропущенные
/// изменения подхватывает резервный опрос браузера (каждые 30 с) и первичный refresh
/// при загрузке страницы.
/// </remarks>
public class DbChangeListener : BackgroundService
{
    // Каналы LISTEN/NOTIFY и маппинг на scope сигнала refresh для клиентов.
    private const string SyncUiChangesChannel = "sync_ui_changes";
    private const string ReportTasksChannel = "report_tasks";

    // Максимальное время ожидания уведомления перед повторной проверкой соединения.
    // На задержку реакции не влияет: уведомление будит цикл мгновенно.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<DbChangeListener> _logger;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly string _connectionString;

    // Уведомления, доставленные событием Notification (оно срабатывает на потоке
    // чтения соединения в любой момент), и накопленные для отправки клиентам.
    private readonly ConcurrentQueue<PendingNotification> _pendingNotifications = new();

    // Счётчик отправленных сигналов и последний payload для лога.
    private int _notificationsSent;
    private volatile string? _lastPayload;

    public DbChangeListener(
        ILogger<DbChangeListener> logger,
        IHubContext<SyncHub> hubContext,
        IConfiguration configuration)
    {
        _logger = logger;
        _hubContext = hubContext;

        _connectionString = configuration.GetConnectionString("SyncBus")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:SyncBus");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Слушатель изменений БД запущен (каналы: {Channels})",
            $"{SyncUiChangesChannel}, {ReportTasksChannel}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenOnceAsync(stoppingToken);
                break; // нормальная остановка по токену отмены
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Соединение LISTEN/NOTIFY разорвано — переподключение через 5 с");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Слушатель изменений БД остановлен");
    }

    /// <summary>
    /// Открывает соединение, подписывается на оба канала и шлёт клиентам сигналы
    /// refresh до отмены или обрыва (обрыв бросает исключение — внешний цикл переподключается).
    /// </summary>
    private async Task ListenOnceAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(stoppingToken);

        // LISTEN принимает ровно одно имя канала — подписываемся отдельными командами.
        foreach (var channel in new[] { SyncUiChangesChannel, ReportTasksChannel })
        {
            using var listenCommand = new NpgsqlCommand($"LISTEN {channel}", connection);
            await listenCommand.ExecuteNonQueryAsync(stoppingToken);
        }

        _logger.LogInformation("Подписка на каналы {Channels} установлена",
            $"{SyncUiChangesChannel}, {ReportTasksChannel}");
        connection.Notification += OnNotification;

        try
        {
            // В Npgsql 4.x нет перегрузки WaitAsync(timeout, token): ожидание ведёт одна
            // долгоживущая задача WaitAsync(token), тайм-аут эмулируется Task.Delay.
            // Задачу нельзя пересоздавать на каждый виток: второй параллельный вызов
            // WaitAsync на том же соединении запрещён (идёт user action).
            var waitTask = connection.WaitAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (connection.State != ConnectionState.Open)
                {
                    // Соединение потеряно — выходим для переподключения во внешнем цикле
                    throw new InvalidOperationException("Соединение LISTEN/NOTIFY закрыто");
                }

                // Уведомления, доставленные обработчиком в предыдущем витке (событие
                // Notification не привязано к WaitAsync), отправляем без ожидания.
                await SendPendingNotificationsAsync(stoppingToken);

                // Ждём асинхронное сообщение или тайм-аут, что наступит раньше
                var completed = await Task.WhenAny(waitTask, Task.Delay(WaitTimeout, stoppingToken));

                if (completed != waitTask)
                {
                    continue; // тайм-аут — виток повторяется, ожидание остаётся в силе
                }

                // Дожидаемся результата ожидания: исключение здесь означает обрыв
                // соединения или отмену — их обрабатывает внешний цикл
                await waitTask;

                // WaitAsync завершается по первому сообщению пачки; остальные обработчик
                // уже поставил в очередь — опустошаем её полностью.
                while (_pendingNotifications.Count > 0)
                {
                    await SendPendingNotificationsAsync(stoppingToken);
                }

                // Новое ожидание на следующий виток (старая задача уже завершена)
                waitTask = connection.WaitAsync(stoppingToken);
            }
        }
        finally
        {
            connection.Notification -= OnNotification;
        }
    }

    /// <summary>
    /// Отправляет накопленные сигналы refresh всем подключённым клиентам.
    /// Ошибка отправки не роняет слушатель (останутся резервный опрос и reconnected-refresh).
    /// </summary>
    private async Task SendPendingNotificationsAsync(CancellationToken ct)
    {
        var sends = new List<Task>();

        while (_pendingNotifications.TryDequeue(out var pending))
        {
            _lastPayload = $"канал={pending.Channel}, payload={pending.Payload}";
            Interlocked.Increment(ref _notificationsSent);
            sends.Add(SendRefreshAsync(pending.Scope, ct));
        }

        if (sends.Count == 0)
        {
            return;
        }

        await Task.WhenAll(sends);

        _logger.LogInformation(
            "Сигналы refresh отправлены ({Count}, последнее: {Payload})",
            sends.Count, _lastPayload ?? string.Empty);
    }

    /// <summary>
    /// Отправка одного сигнала refresh (scope — какой блок данных обновить).
    /// </summary>
    private async Task SendRefreshAsync(string scope, CancellationToken ct)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("refresh", new { scope }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // остановка сервиса — обрабатывает внешний цикл
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось отправить сигнал refresh (scope: {Scope})", scope);
        }
    }

    /// <summary>
    /// Обработчик события Notification: мапит канал на scope и ставит сигнал в очередь.
    /// </summary>
    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        var scope = MapChannelToScope(e.Channel);
        if (scope == null)
        {
            return; // неизвестный канал — не наш
        }

        _pendingNotifications.Enqueue(new PendingNotification(scope, e.Channel, e.Payload));
    }

    /// <summary>
    /// Маппинг канала pg_notify на scope сигнала refresh для клиента.
    /// </summary>
    private static string? MapChannelToScope(string channel) => channel switch
    {
        SyncUiChangesChannel => "sync",
        ReportTasksChannel => "reports",
        _ => null
    };

    /// <summary>
    /// Очередное уведомление: scope для клиента + исходные данные канала (для лога).
    /// </summary>
    private readonly record struct PendingNotification(string Scope, string Channel, string? Payload);
}
