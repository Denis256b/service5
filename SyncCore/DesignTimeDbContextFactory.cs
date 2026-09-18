using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SyncCore;

/// <summary>
/// Фабрика контекста для design-time сценариев (dotnet-ef migrations add).
/// Строка подключения берётся из переменной окружения <c>ConnectionStrings__SyncBus</c>;
/// если она не задана — безопасная заглушка без реального пароля.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SyncBusContext>
{
    /// <inheritdoc />
    public SyncBusContext CreateDbContext(string[] args)
    {
        // Строка подключения: приоритетно переменная окружения ConnectionStrings__SyncBus,
        // иначе безопасная заглушка без пароля (реальные учётные данные не попадают в git).
        var fromEnv = Environment.GetEnvironmentVariable("ConnectionStrings__SyncBus");
        var connectionString = string.IsNullOrWhiteSpace(fromEnv)
            ? "Host=localhost;Database=syncbus;Username=CHANGE_ME;Password=CHANGE_ME"
            : fromEnv!;

        var options = new DbContextOptionsBuilder<SyncBusContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new SyncBusContext(options);
    }
}
