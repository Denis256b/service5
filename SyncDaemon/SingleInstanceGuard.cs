using Microsoft.Extensions.Logging;
using Npgsql;

namespace SyncDaemon;

/// <summary>
/// Гард одиночности SyncDaemon: при старте занимает advisory lock в Postgres
/// (pg_try_advisory_lock). Если замок уже занят другим экземпляром — процесс
/// завершается с кодом 1 (fail-fast). Замок привязан к сессии БД и освобождается
/// базой данных автоматически при закрытии соединения (смерть/крах процесса),
/// поэтому перезапуск после падения работает.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// Идентификатор advisory lock: произвольная константа, уникальная в пределах приложения.
    /// </summary>
    public const long LockId = 5123456789012345678L;

    private readonly NpgsqlConnection _connection;

    private SingleInstanceGuard(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Пытается занять advisory lock одиночности. Если другой экземпляр уже его держит —
    /// логирует ошибку и завершает процесс с кодом 1. Возвращаемый объект нужно держать
    /// живым на всё время жизни процесса: соединение удерживает замок.
    /// </summary>
    /// <param name="connectionString">Строка подключения к базе SyncBus.</param>
    /// <param name="logger">Логгер для сообщения о конфликте экземпляров.</param>
    public static SingleInstanceGuard Acquire(string connectionString, ILogger logger)
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@lockId)";
        command.Parameters.Add(new NpgsqlParameter("@lockId", LockId));

        var acquired = (bool)command.ExecuteScalar()!;

        if (!acquired)
        {
            logger.LogError(
                "SyncDaemon уже запущен: advisory lock занят другим экземпляром — выход");
            connection.Dispose();
            Environment.Exit(1);
        }

        return new SingleInstanceGuard(connection);
    }

    /// <summary>
    /// Закрывает соединение (освобождает advisory lock).
    /// </summary>
    public void Dispose() => _connection.Dispose();
}
