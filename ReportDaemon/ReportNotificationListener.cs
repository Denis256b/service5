using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ReportDaemon;

/// <summary>
/// Фоновый сервис подписки на уведомления PostgreSQL (LISTEN/NOTIFY) по каналу
/// <c>report_tasks</c>. Триггер <c>TRG_ReportTasks_Notify</c> на таблице ReportTasks
/// шлёт pg_notify при вставке задания и при переходе статуса в pending — сервис
/// мгновенно будит опросный цикл (ReportTaskSignal), чтобы задание было взято
/// без ожидания следующего витка периодического опроса.
/// </summary>
/// <remarks>
/// Использует собственное постоянное соединение (не пул EF Core). Ожидание ведётся
/// одной долгоживущей задачей NpgsqlConnection.WaitAsync(token) с эмуляцией тайм-аута
/// через Task.Delay — совместимо со сборками Npgsql 4.x, где нет перегрузки
/// WaitAsync(timeout, token). Детали пришедшего асинхронного сообщения доставляет
/// событие Notification. При обрыве соединения сервис автоматически переподключается;
/// периодический опрос (ReportPoller) остаётся резервным механизмом подхвата заданий.
/// </remarks>
public class ReportNotificationListener : BackgroundService
{
    private const string ChannelName = "report_tasks";

    // Максимальное время ожидания уведомления перед повторной проверкой соединения.
    // На задержку реакции не влияет: уведомление будит цикл мгновенно.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<ReportNotificationListener> _logger;
    private readonly string _connectionString;
    private readonly ReportTaskSignal _signal;

    // Счётчик полученных уведомлений (событие Notification может срабатывать
    // в любой момент, включая время выполнения команд) и последний payload для лога
    private int _notificationsReceived;
    private volatile string? _lastPayload;

    public ReportNotificationListener(
        ILogger<ReportNotificationListener> logger,
        IConfiguration configuration,
        ReportTaskSignal signal)
    {
        _logger = logger;
        _signal = signal;

        _connectionString = configuration.GetConnectionString("SyncBus")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:SyncBus");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Слушатель уведомлений о заданиях отчётов запущен (канал: {Channel})", ChannelName);

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

        _logger.LogInformation("Слушатель уведомлений о заданиях отчётов остановлен");
    }

    /// <summary>
    /// Открывает соединение, подписывается на канал и ждёт уведомления
    /// до отмены или обрыва (обрыв бросает исключение — внешний цикл переподключается).
    /// </summary>
    private async Task ListenOnceAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(stoppingToken);

        using (var listenCommand = new NpgsqlCommand($"LISTEN {ChannelName}", connection))
        {
            await listenCommand.ExecuteNonQueryAsync(stoppingToken);
        }

        _logger.LogInformation("Подписка на канал {Channel} установлена", ChannelName);
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

                // Ждём асинхронное сообщение или тайм-аут, что наступит раньше
                var completed = await Task.WhenAny(waitTask, Task.Delay(WaitTimeout, stoppingToken));

                if (completed != waitTask)
                {
                    continue; // тайм-аут — задания подхватит периодический опрос
                }

                // Дожидаемся результата ожидания: исключение здесь означает обрыв
                // соединения или отмену — их обрабатывает внешний цикл
                await waitTask;

                // Сбрасываем счётчик; > 0 — было хотя бы одно уведомление pg_notify
                var count = Interlocked.Exchange(ref _notificationsReceived, 0);
                if (count == 0)
                {
                    continue; // пришло только служебное сообщение (notice) — не наше дело
                }

                _logger.LogInformation(
                    "Уведомление о задании отчёта получено ({Count}, последнее: {Payload}) — опросный цикл пробуждён",
                    count, _lastPayload ?? string.Empty);

                // Пробуждаем опросный цикл: он сам атомарно возьмёт старейшее pending-задание
                _signal.Signal();

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
    /// Обработчик события Notification: фиксирует получение уведомления.
    /// </summary>
    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        _lastPayload = $"канал={e.Channel}, заданиеId={e.Payload}";
        Interlocked.Increment(ref _notificationsReceived);
    }
}
