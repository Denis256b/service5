using System.Text.Json;

namespace NotifyAgent;

/// <summary>
/// Локальное состояние агента: курсоры прочитанных событий + мапа статусов
/// заданий отчётов. Хранится в state.json рядом с exe — перезапуск агента не
/// повторяет старые события (baseline-логика как в текущем вебе).
/// </summary>
public class EventState
{
    /// <summary>
    /// Выполнен ли первичный baseline: до первой инициализации курсоры ставятся
    /// на максимум существующих записей без уведомлений (чтобы при первом запуске
    /// агент не «прокричал» всю историю).
    /// </summary>
    public bool BaselineDone { get; set; }

    /// <summary>Курсор SyncHistory: последнее прочитанное значение Id.</summary>
    public long LastSeenHistoryId { get; set; }

    /// <summary>Курсор ReportTaskEvents: последнее прочитанное значение Timestamp (UTC).</summary>
    public DateTime LastSeenEventTimestamp { get; set; } = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Курсор ReportTaskEvents: Id события с последним Timestamp — уточнение для
    /// случаев, когда несколько событий имеют одинаковый Timestamp (кортежное
    /// сравнение (Timestamp, Id) > (курсорTs, курсорId)).
    /// </summary>
    public long LastSeenEventId { get; set; }

    /// <summary>Мапа статусов заданий отчётов: TaskId → последний известный Status.</summary>
    public Dictionary<long, string> TaskStatuses { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Читает состояние из файла. Если файла нет — возвращает новое (baseline не выполнен).
    /// Повреждённый JSON заменяется новым состоянием: курсоры пересчитаются baseline-логикой.
    /// </summary>
    /// <param name="path">Полный путь к state.json.</param>
    public static EventState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new EventState();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<EventState>(json, JsonOptions) ?? new EventState();
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Не удалось прочитать состояние {path}: {ex.Message} — состояние сброшено");
            return new EventState();
        }
    }

    /// <summary>Перезаписывает файл состояния текущими значениями.</summary>
    /// <param name="path">Полный путь к state.json.</param>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
