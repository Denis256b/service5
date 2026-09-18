namespace SyncDaemon;

public class SyncSettings
{
    public DatabaseSettings Source { get; set; } = new();
    public DatabaseSettings Destination { get; set; } = new();
    public int IntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Интервал опроса команд (SyncCommands) в секундах.
    /// </summary>
    public int CommandPollIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Тайм-аут обработки команды в минутах (зависшие команды сбрасываются в pending).
    /// </summary>
    public int ProcessingTimeoutMinutes { get; set; } = 5;
}

public class DatabaseSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string PrimaryKey { get; set; } = string.Empty;
}