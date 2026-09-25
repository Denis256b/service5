namespace NotifyAgent;

/// <summary>
/// Минимальный логгер агента: построчный append в файл agent.log рядом с exe
/// + дублирование в консоль (для отладки). Serilog намеренно не подключается —
/// минимум зависимостей. Ошибки записи лога не роняют агента.
/// </summary>
public static class AgentLog
{
    private static readonly object _lock = new();

    // Путь к файлу лога; null, пока Init не вызван (запись только в консоль).
    private static string? _path;

    /// <summary>
    /// Устанавливает путь к файлу лога и пишет стартовую запись.
    /// </summary>
    /// <param name="path">Полный путь к agent.log.</param>
    public static void Init(string path)
    {
        _path = path;
        Write("INFO ", "NotifyAgent запущен");
    }

    /// <summary>Запись информационного сообщения.</summary>
    /// <param name="message">Текст сообщения.</param>
    public static void Info(string message) => Write("INFO ", message);

    /// <summary>Запись предупреждения (обрыв соединения, ошибка чтения событий).</summary>
    /// <param name="message">Текст сообщения.</param>
    public static void Warn(string message) => Write("WARN ", message);

    /// <summary>Запись ошибки (некритичной для работы агента).</summary>
    /// <param name="message">Текст сообщения.</param>
    public static void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// Форматирует строку лога (UTC-время + уровень + текст), печатает в консоль
    /// и дописывает в файл под блокировкой.
    /// </summary>
    private static void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        Console.WriteLine(line);

        var path = _path;
        if (path == null)
        {
            return;
        }

        try
        {
            lock (_lock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Логирование не должно ломать агента (например, при полном диске)
        }
    }
}
