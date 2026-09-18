using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ReportContracts;
using SampleProvider;
using SecondProvider;
using SyncCore;

using ReportDaemon;

var builder = Host.CreateDefaultBuilder(args);

// Логирование: консоль (Information) + PostgreSQL (Warning и выше).
// Строку подключения читаем до Build(): IHostBuilder не exposes свойство
// Configuration, поэтому строим конфигурацию отдельно. Источники повторяют
// Host.CreateDefaultBuilder (appsettings.json + переменные окружения), чтобы
// переопределение через ConnectionStrings__SyncBus работало одинаково для
// хоста и для Serilog-синка.
var connectionString = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build()
    .GetConnectionString("SyncBus")!;

var serilogLogger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.WithProperty("Source", "ReportDaemon")
    .WriteTo.Console()
    .WriteTo.PostgreSQL(
        connectionString: connectionString,
        tableName: "Logs",
        restrictedToMinimumLevel: LogEventLevel.Warning,
        needAutoCreateTable: true)
    .CreateLogger();

builder.UseSerilog(serilogLogger);

// Configure services
builder.ConfigureServices((hostContext, services) =>
{
    // Bind report settings from appsettings.json
    services.Configure<ReportSettings>(hostContext.Configuration.GetSection("Report"));

    // Идентификатор экземпляра (для OwnerInstanceId и stale-reset)
    services.AddSingleton<InstanceIdProvider>();

    // Register database context
    services.AddDbContext<SyncBusContext>(options =>
        options.UseNpgsql(hostContext.Configuration.GetConnectionString("SyncBus")));

    // Провайдеры отчётов: регистрируем явно.
    // Новый провайдер = ProjectReference в ReportDaemon.csproj + одна строка ниже.
    services.AddSingleton<IReportProvider, SampleReportProvider>();
    services.AddSingleton<IReportProvider, SecondReportProvider>();

    // Сигнал мгновенного пробуждения опросного цикла (LISTEN/NOTIFY)
    services.AddSingleton<ReportTaskSignal>();

    // Register report poller (опрос БД на новые задания по отчетам)
    services.AddHostedService<ReportPoller>();

    // Слушатель уведомлений PostgreSQL: вставка задания / переход статуса в pending
    services.AddHostedService<ReportNotificationListener>();
});

var host = builder.Build();

// Валидация провайдеров отчетов: дубликат ProviderId — fail-fast при старте
using (var scope = host.Services.CreateScope())
{
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ReportProviders");
    var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<IReportProvider>>().ToList();

    if (providers.Count == 0)
    {
        startupLogger.LogWarning(
            "Не зарегистрировано ни одного провайдера отчетов — задания будут завершаться ошибкой");
    }

    var duplicates = providers
        .GroupBy(p => p.ProviderId, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToList();

    if (duplicates.Count > 0)
    {
        throw new InvalidOperationException(
            $"Дубликаты ProviderId среди провайдеров отчетов: {string.Join(", ", duplicates)}");
    }

    foreach (var provider in providers)
    {
        startupLogger.LogInformation(
            "Зарегистрирован провайдер отчетов: {ProviderId} ({Type})",
            provider.ProviderId, provider.GetType().Name);
    }
}

// Apply database migrations on startup
// Параллельный запуск миграций с SyncDaemon безопасен — Npgsql берёт собственный advisory lock
using (var scope = host.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<SyncBusContext>();
    try
    {
        await context.Database.MigrateAsync();
        Console.WriteLine("Database migrations applied successfully");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to apply database migrations: {ex.Message}");
        throw;
    }
}

await host.RunAsync();
