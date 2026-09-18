using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncCore;

namespace SyncDaemon;

public class CommandPoller : BackgroundService
{
    private readonly ILogger<CommandPoller> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISyncService _syncService;
    private readonly int _pollIntervalSeconds;
    private readonly int _processingTimeoutMinutes;

    public CommandPoller(
        ILogger<CommandPoller> logger,
        IServiceScopeFactory serviceScopeFactory,
        ISyncService syncService,
        IOptions<SyncSettings> settings)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _syncService = syncService;
        _pollIntervalSeconds = settings.Value.CommandPollIntervalSeconds;
        _processingTimeoutMinutes = settings.Value.ProcessingTimeoutMinutes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Command poller started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingCommandsAsync();
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in command poller");
                // Continue with next iteration even if there's an error
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Command poller stopped");
    }

    private async Task ProcessPendingCommandsAsync()
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SyncBusContext>();
        
        try
        {
            // Ищем старейшую pending команду
            var command = await context.SyncCommands
                .Where(c => c.Status == "pending")
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync();

            if (command == null)
            {
                return;
            }

            // Атомарно устанавливаем статус в processing
            command.Status = "processing";
            command.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            try
            {
                // Выполняем команду
                var result = await ExecuteCommandAsync(command);
                
                // Обновляем результат
                command.Status = "done";
                command.Result = result;
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing command {CommandId}", command.Id);
                
                // Возвращаем команду в pending если произошла ошибка
                command.Status = "error";
                command.Result = ex.ToString();
                await context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing commands");
        }

        // Проверяем и сбрасываем зависшие команды в статусе processing
        try
        {
            var timeoutThreshold = DateTime.UtcNow.AddMinutes(-_processingTimeoutMinutes);
            
            var staleCommands = await context.SyncCommands
                .Where(c => c.Status == "processing" && c.ProcessedAt < timeoutThreshold)
                .ToListAsync();

            foreach (var command in staleCommands)
            {
                _logger.LogWarning("Resetting stale command {CommandId} in processing state", command.Id);
                command.Status = "pending";
                command.ProcessedAt = null;
            }
            
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting stale commands");
        }
    }

    private async Task<string> ExecuteCommandAsync(SyncCommand command)
    {
        switch (command.CommandType.ToLower())
        {
            case "start":
                _syncService.Start(ParseIntervalFromPayload(command.Payload));
                return "Started successfully";
                
            case "stop":
                await _syncService.StopAsync();
                return "Stopped successfully";
                
            case "execute":
                var result = await _syncService.ExecuteOnceAsync();
                return $"Executed successfully. Inserted: {result.Inserted}, Updated: {result.Updated}";
                
            default:
                throw new NotSupportedException($"Unknown command type: {command.CommandType}");
        }
    }

    private int ParseIntervalFromPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return 30; // дефолтный интервал
            
        try
        {
            var interval = int.Parse(payload);
            return interval > 0 ? interval : 30;
        }
        catch
        {
            return 30;
        }
    }
}