using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using SyncCore;
using System.Threading.Tasks;

using SyncDaemon;

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
    .Enrich.WithProperty("Source", "SyncDaemon")
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
    // Bind sync settings from appsettings.json
    services.Configure<SyncSettings>(hostContext.Configuration.GetSection("Sync"));

    // Register stub database sources
    services.AddSingleton<ISourceDb>(sp =>
        new StubDbSource("source", sp.GetRequiredService<ILogger<StubDbSource>>()));
    services.AddSingleton<IDestinationDb>(sp =>
        new StubDbSource("destination", sp.GetRequiredService<ILogger<StubDbSource>>()));

    // Register sync engine and service
    services.AddSingleton<ISyncEngine, SyncEngine>();
    services.AddSingleton<ISyncService, SyncService>();

    // Register database context
    services.AddDbContext<SyncBusContext>(options =>
        options.UseNpgsql(hostContext.Configuration.GetConnectionString("SyncBus")));

    // Register command poller
    services.AddHostedService<CommandPoller>();

    // Примечание: IServiceScopeFactory регистрируется автоматически самим
    // DI-контейнером (ServiceProvider реализует этот интерфейс),
    // явная регистрация не требуется.
});

var host = builder.Build();

// Гард одиночности: fail-fast, если другой экземпляр SyncDaemon уже запущен.
// Соединение, удерживающее advisory lock, должно жить до выхода процесса.
var configuration = host.Services.GetRequiredService<IConfiguration>();
using var singleInstanceGuard = SingleInstanceGuard.Acquire(
    configuration.GetConnectionString("SyncBus")!,
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SingleInstanceGuard"));

// Apply database migrations on startup
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

// Restore state from database
// ISyncService — singleton, резолвим напрямую из корневого провайдера
var syncService = host.Services.GetRequiredService<ISyncService>();
if (syncService is SyncService syncServiceImpl)
{
    await syncServiceImpl.RestoreFromDatabaseAsync();
}

await host.RunAsync();
