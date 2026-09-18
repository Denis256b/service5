using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportContracts;
using SyncCore;

namespace ReportDaemon;

/// <summary>
/// Фоновый сервис опроса базы данных на наличие новых заданий
/// по формированию отчетов. Циклически проверяет таблицу ReportTasks,
/// берет старейшее задание в статусе pending и маршрутизирует его
/// в провайдер отчетов по значению ProviderId. Задания обрабатываются
/// параллельно (до MaxParallelReports одновременно), каждое — в собственном
/// DI-скоупе и с жёстким тайм-аутом обработки (ProcessingTimeoutMinutes).
/// </summary>
/// <remarks>
/// Запуск формирования отчета — событийный: триггер на таблице ReportTasks
/// шлёт pg_notify при вставке задания и при переходе статуса в pending, а
/// ReportNotificationListener мгновенно будит цикл (ReportTaskSignal).
/// Периодический опрос (PollIntervalSeconds) остаётся резервным механизмом:
/// он подхватывает задания, созданные пока демон был недоступен.
/// Многоэкземплярность: задание берётся атомарным условным UPDATE (claim) —
/// его обрабатывает ровно один экземпляр. Собственные задания исключаются из
/// сброса зависших (stale-reset) по OwnerInstanceId. Файл отчета после успеха
/// сохраняется в базу (ReportTaskFile), чтобы скачиваться с любой машины.
/// </remarks>
public class ReportPoller : BackgroundService
{
    private readonly ILogger<ReportPoller> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly Dictionary<string, IReportProvider> _providers;
    private readonly ReportSettings _settings;
    private readonly int _maxParallel;
    private readonly string _instanceId;
    private readonly ReportTaskSignal _signal;

    public ReportPoller(
        ILogger<ReportPoller> logger,
        IServiceScopeFactory serviceScopeFactory,
        IEnumerable<IReportProvider> providers,
        IOptions<ReportSettings> settings,
        InstanceIdProvider instanceIdProvider,
        ReportTaskSignal signal)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);
        _settings = settings.Value;
        _instanceId = instanceIdProvider.InstanceId;
        _signal = signal;

        // Степень параллелизма: минимум 1 (значение < 1 — clamp + Warning)
        if (_settings.MaxParallelReports < 1)
        {
            logger.LogWarning(
                "Report:MaxParallelReports = {Value} — значение меньше 1, используется 1",
                _settings.MaxParallelReports);
            _maxParallel = 1;
        }
        else
        {
            _maxParallel = _settings.MaxParallelReports;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Опрос заданий на формирование отчетов запущен (экземпляр: {InstanceId}, интервал: {Interval} с, параллельно: {MaxParallel})",
            _instanceId, _settings.PollIntervalSeconds, _maxParallel);

        // Семафор ограничивает количество одновременно обрабатываемых заданий
        var semaphore = new SemaphoreSlim(_maxParallel);

        // При старте сбрасываем зависшие задания (восстановление после краша)
        await ResetStaleTasksAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Захватываем слот параллелизма ДО взятия задания из БД
            try
            {
                await semaphore.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Ожидается при запросе отмены — выходим из цикла
                break;
            }

            Task? work = null;
            var slotReleased = false;
            try
            {
                using var claimScope = _serviceScopeFactory.CreateScope();
                var context = claimScope.ServiceProvider.GetRequiredService<SyncBusContext>();
                var task = await ClaimOldestPendingAsync(context, stoppingToken);

                if (task == null)
                {
                    // Нет заданий: сбрасываем зависшие и ждём пробуждения.
                    // Сигнал LISTEN/NOTIFY будит цикл мгновенно (вставка задания / переход в pending);
                    // тайм-аут — виток резервного периодического опроса.
                    await ResetStaleTasksAsync(stoppingToken);
                    slotReleased = true;
                    semaphore.Release();
                    await _signal.WaitAsync(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                // Задание взято — запускаем обработку в отдельном потоке (не блокируем цикл)
                work = ProcessReportTaskAsync(task.Id, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Остановка демона: освобождаем слот (если ещё не освобождён) и выходим из цикла
                if (!slotReleased)
                {
                    semaphore.Release();
                }

                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка взятия задания на формирование отчета");
                if (!slotReleased)
                {
                    semaphore.Release();
                }

                // При остановке демона не задерживаемся — выходим из цикла
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            // Слот освобождается по завершении задания (успех/ошибка/тайм-аут)
            _ = work!.ContinueWith(
                _ => semaphore.Release(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        _logger.LogInformation("Опрос заданий на формирование отчетов остановлен");
    }

    /// <summary>
    /// Берёт старейшее задание в статусе pending атомарным условным UPDATE (claim)
    /// и возвращает сущность. Возвращает null, если заданий нет или задание
    /// уже забрал другой экземпляр.
    /// </summary>
    private async Task<ReportTask?> ClaimOldestPendingAsync(SyncBusContext context, CancellationToken stoppingToken)
    {
        // Ищем старейшее задание в статусе pending (read-only SELECT)
        var taskId = await context.ReportTasks
            .Where(t => t.Status == "pending")
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(stoppingToken);

        if (taskId == 0)
        {
            return null;
        }

        // Атомарный claim: условный UPDATE, affected rows = 0 → задание забрал другой экземпляр.
        // OwnerInstanceId фиксирует, какой экземпляр взял задание в обработку.
        var updated = await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ReportTasks\" SET \"Status\" = 'processing', \"ProcessedAt\" = now(), \"OwnerInstanceId\" = {_instanceId} WHERE \"Id\" = {taskId} AND \"Status\" = 'pending'",
            stoppingToken);

        if (updated == 0)
        {
            _logger.LogInformation("Задание {TaskId} уже взято другим экземпляром — пропускаем", taskId);
            return null;
        }

        // Аудит-событие после успешного claim; ошибка записи логируется и не ломает claim
        try
        {
            AddAuditEvent(context, taskId, "processing", "Задание взято в обработку");
            await context.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось записать событие аудита для задания {TaskId} (claim выполнен)", taskId);
        }

        return await context.ReportTasks.FirstOrDefaultAsync(t => t.Id == taskId, stoppingToken);
    }

    /// <summary>
    /// Добавляет событие аудита перехода статуса задания (Actor = "daemon").
    /// </summary>
    private static void AddAuditEvent(SyncBusContext context, long taskId, string status, string? message)
    {
        context.ReportTaskEvents.Add(new ReportTaskEvent
        {
            TaskId = taskId,
            Status = status,
            Timestamp = DateTime.UtcNow,
            Message = message,
            Actor = "daemon"
        });
    }

    /// <summary>
    /// Обрабатывает одно задание в собственном DI-скоупе и контексте БД.
    /// Провайдер вызывается с жёстким тайм-аутом (ProcessingTimeoutMinutes).
    /// После успеха содержимое отчета (байты) сохраняется в базу (ReportTaskFile).
    /// Метод не бросает исключений — все ошибки фиксируются в статусе задания.
    /// </summary>
    private async Task ProcessReportTaskAsync(long taskId, CancellationToken stoppingToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SyncBusContext>();

        ReportTask? task = null;
        try
        {
            // Перезагружаем задание из собственного контекста (контекст claim-скоупа уже закрыт)
            task = await context.ReportTasks.FirstOrDefaultAsync(t => t.Id == taskId, stoppingToken);
            if (task == null)
            {
                _logger.LogWarning("Задание {TaskId} не найдено при обработке", taskId);
                return;
            }

            if (string.IsNullOrWhiteSpace(task.ProviderId))
            {
                // Не указан провайдер — фиксируем явную ошибку задания
                task.Status = "error";
                task.Result = "Не указан провайдер отчета (ProviderId)";
                AddAuditEvent(context, task.Id, "error", "Не указан провайдер отчета (ProviderId)");
                await context.SaveChangesAsync(stoppingToken);

                _logger.LogWarning("Задание {TaskId}: не указан провайдер отчета (ProviderId)", taskId);
            }
            else if (!_providers.TryGetValue(task.ProviderId, out var provider))
            {
                // Провайдер не зарегистрирован в демоне
                task.Status = "error";
                task.Result = $"Провайдер '{task.ProviderId}' не зарегистрирован в демоне";
                AddAuditEvent(context, task.Id, "error", $"Провайдер '{task.ProviderId}' не зарегистрирован в демоне");
                await context.SaveChangesAsync(stoppingToken);

                _logger.LogWarning(
                    "Задание {TaskId}: провайдер '{ProviderId}' не зарегистрирован в демоне",
                    taskId, task.ProviderId);
            }
            else
            {
                // Маппинг сущности на контракт и формирование отчета провайдером
                var request = new ReportRequest(task.Id, task.ReportType, task.Payload);

                // Жёсткий тайм-аут задания: linked-токен (остановка демона + ProcessingTimeoutMinutes)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromMinutes(_settings.ProcessingTimeoutMinutes));

                _logger.LogInformation(
                    "Начата обработка задания {TaskId} (провайдер {ProviderId})",
                    taskId, task.ProviderId);

                var report = await provider.GenerateAsync(request, cts.Token);

                // Провайдер вернул содержимое отчета — сохраняем байты в базу (ReportTaskFile)
                var content = report.Content;

                // Удаляем возможную старую запись (защита от нарушения уникального индекса TaskId)
                var existingFile = await context.ReportTaskFiles
                    .FirstOrDefaultAsync(f => f.TaskId == task.Id, stoppingToken);
                if (existingFile != null)
                {
                    context.ReportTaskFiles.Remove(existingFile);
                }

                context.ReportTaskFiles.Add(new ReportTaskFile
                {
                    TaskId = task.Id,
                    Content = content,
                    Size = content.Length,
                    ContentType = "application/vnd.ms-excel",
                    CreatedAt = DateTime.UtcNow
                });

                // Обновляем результат (FilePath — имя файла для скачивания)
                task.Status = "done";
                task.FilePath = report.FileName;
                task.Result = $"Отчет сформирован: {report.FileName} ({report.RowCount} строк)";
                AddAuditEvent(context, task.Id, "done", $"Отчет сформирован: {report.FileName}");
                await context.SaveChangesAsync(stoppingToken);

                _logger.LogInformation(
                    "Задание {TaskId} обработано, отчет сохранен в БД ({Size} байт)",
                    taskId, content.Length);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Остановка демона: статус не трогаем — задание останется processing,
            // stale-reset при следующем старте вернёт его в pending (существующая семантика восстановления)
            _logger.LogInformation("Остановка демона: обработка задания {TaskId} прервана", taskId);
        }
        catch (OperationCanceledException)
        {
            // Сработал тайм-аут задания
            if (task != null)
            {
                task.Status = "error";
                task.Result = $"Превышено время обработки отчета ({_settings.ProcessingTimeoutMinutes} мин)";
                AddAuditEvent(context, task.Id, "error", $"Превышено время обработки отчета ({_settings.ProcessingTimeoutMinutes} мин)");
                await context.SaveChangesAsync();
            }

            _logger.LogWarning(
                "Задание {TaskId}: превышено время обработки отчета ({Timeout} мин)",
                taskId, _settings.ProcessingTimeoutMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка формирования отчета по заданию {TaskId}", taskId);

            // Фиксируем ошибку в статусе задания
            if (task != null)
            {
                task.Status = "error";
                task.Result = ex.ToString();
                AddAuditEvent(context, task.Id, "error", ex.Message);
                await context.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    /// Сбрасывает зависшие задания (processing дольше ProcessingTimeoutMinutes) в pending.
    /// Собственные задания (OwnerInstanceId = этот экземпляр) не сбрасываются —
    /// их обрабатывает текущий процесс. Вызывается при старте и в idle-витках. Не бросает исключений.
    /// </summary>
    private async Task ResetStaleTasksAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SyncBusContext>();

            var timeoutThreshold = DateTime.UtcNow.AddMinutes(-_settings.ProcessingTimeoutMinutes);

            // Только чужие задания: OwnerInstanceId IS NULL или не равен этому экземпляру
            var staleTasks = await context.ReportTasks
                .Where(t => t.Status == "processing"
                    && t.ProcessedAt < timeoutThreshold
                    && (t.OwnerInstanceId == null || t.OwnerInstanceId != _instanceId))
                .ToListAsync(stoppingToken);

            foreach (var stale in staleTasks)
            {
                _logger.LogWarning("Сброс зависшего задания {TaskId} в статусе processing", stale.Id);
                stale.Status = "pending";
                stale.ProcessedAt = null;
                stale.OwnerInstanceId = null;
                AddAuditEvent(context, stale.Id, "pending", "Сброс зависшего задания (восстановление)");
            }

            await context.SaveChangesAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Ожидается при остановке демона — сброс не критичен
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка сброса зависших заданий");
        }
    }
}
