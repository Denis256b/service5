using System.Text.Json;

namespace NotifyAgent;

/// <summary>
/// Конфигурация tray-агента: хранится в agent.json рядом с exe. Читается при
/// старте, перезаписывается кнопкой «Сохранить» окна настроек и применяется
/// без перезапуска (EventListener пересоздаётся с новыми фильтрами).
/// </summary>
public class AgentConfig
{
    /// <summary>Глобальный вкл/выкл всех уведомлений.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Новая запись SyncHistory со статусом error.</summary>
    public bool SyncError { get; set; } = true;

    /// <summary>Задание отчёта перешло в status error (текст из Message).</summary>
    public bool ReportError { get; set; } = true;

    /// <summary>Задание отчёта перешло в status done.</summary>
    public bool ReportDone { get; set; }

    /// <summary>Задание отчёта перешло в status processing.</summary>
    public bool ReportProcessing { get; set; }

    /// <summary>Задание отчёта перешло в status cancelled.</summary>
    public bool ReportCancelled { get; set; }

    /// <summary>Повторная попытка задания (событие retry).</summary>
    public bool ReportRetry { get; set; }

    /// <summary>
    /// Окно подавления повторов одного события в часах: повтор того же события
    /// (тип + задание) внутри окна не уведомляется. 0 — подавление выключено.
    /// </summary>
    public int SuppressHours { get; set; } = 24;

    /// <summary>Настройки веб-панели (адрес для перехода по клику).</summary>
    public WebSettings Web { get; set; } = new();

    /// <summary>Настройки подключения к базе syncbus.</summary>
    public DbSettings Db { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Читает конфиг из файла. Если файла нет — возвращает значения по умолчанию;
    /// нечитаемый JSON логируется и тоже заменяется дефолтами (агент обязан стартовать).
    /// Отсутствующие поля сохраняют дефолтные значения модели.
    /// </summary>
    /// <param name="path">Полный путь к agent.json.</param>
    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AgentConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions) ?? new AgentConfig();
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Не удалось прочитать конфиг {path}: {ex.Message} — используются значения по умолчанию");
            return new AgentConfig();
        }
    }

    /// <summary>Перезаписывает файл конфига текущими значениями.</summary>
    /// <param name="path">Полный путь к agent.json.</param>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}

/// <summary>Настройки веб-панели SyncManager.</summary>
public class WebSettings
{
    /// <summary>
    /// Базовый адрес веб-панели для перехода по клику на уведомление
    /// (например, https://sync.lan). Без завершающего слеша.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5000";
}

/// <summary>Настройки подключения к базе syncbus.</summary>
public class DbSettings
{
    /// <summary>
    /// Строка подключения к syncbus (та же, что у демонов). Отдельную read-only
    /// роль PostgreSQL — на будущее.
    /// </summary>
    public string ConnectionString { get; set; } = "Host=localhost;Database=syncbus;Username=postgres;Password=postgres";
}
