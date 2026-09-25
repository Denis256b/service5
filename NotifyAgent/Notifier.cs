using System.Windows.Forms;
using Windows.UI.Notifications;

namespace NotifyAgent;

/// <summary>
/// Одно уведомление, доставляемое системным тостом (Windows.UI.Notifications).
/// </summary>
/// <param name="EventId">Уникальный id источника (Id записи SyncHistory / Id события ReportTaskEvents) — дедупликация по нему.</param>
/// <param name="SuppressKey">Ключ подавления повторов: статус + задание (для отчётов) или тип (для синхронизации).</param>
/// <param name="Title">Заголовок тоста.</param>
/// <param name="Message">Текст уведомления (~200 символов).</param>
/// <param name="Url">Адрес веб-панели для перехода по клику; пустая строка — без перехода.</param>
public sealed record AgentNotification(long EventId, string SuppressKey, string Title, string Message, string Url);

/// <summary>
/// Доставка системных уведомлений: NotifyIcon (иконка в трее) + тосты Windows.UI.Notifications.
/// Каждое принятое уведомление сразу отправляется как тост — очередь показа и тайминг
/// управляет сама ОС (несколько тостов могут показаться одновременно, застреваний не бывает).
/// Клик по тосту открывает Url через элемент &lt;launch&gt; в XML тоста: браузер запускает
/// Windows, процесс агента не участвует. Дедупликация: один тост на id события; повтор того
/// же SuppressKey внутри SuppressHours подавляется.
/// </summary>
public sealed class Notifier : IDisposable
{
    /// <summary>Идентификатор приложения (AUMID) для тостов — используется и в «уже запущен».</summary>
    public const string AppUserModelId = "SyncBus.NotifyAgent";

    // Ограничения Windows на текст тоста (заголовок и текст).
    private const int MaxTitleLength = 63;
    private const int MaxMessageLength = 255;

    private readonly Form _owner;
    private readonly NotifyIcon _icon;
    private readonly Func<AgentConfig> _config;
    private readonly ToastNotifier _toastNotifier;

    // Дедупликация: показанные id событий и время последнего показа по SuppressKey.
    private readonly object _dedupLock = new();
    private readonly HashSet<long> _shownEventIds = new();
    private readonly Dictionary<string, DateTime> _lastShownByKey = new();

    /// <summary>
    /// Создаёт иконку в трее (скрытая форма-владелец) и нотификатор тостов.
    /// </summary>
    /// <param name="icon">Иконка NotifyIcon.</param>
    /// <param name="config">Доступ к актуальному конфигу (SuppressHours/Enabled читаются на каждое уведомление).</param>
    public Notifier(Icon icon, Func<AgentConfig> config)
    {
        _config = config;

        _owner = new Form
        {
            ShowInTaskbar = false,
            Visible = false,
            Size = new System.Drawing.Size(0, 0),
            StartPosition = FormStartPosition.Manual
        };
        // Создаём handle сразу: форма нужна как owner для ShowDialog в SettingsForm.
        _owner.CreateControl();

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "NotifyAgent — уведомления SyncBus",
            Visible = true
        };

        // Доставка тостами: очередь показа и тайминг ведёт Windows; вызовы WinRT потокобезопасны.
        _toastNotifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
    }

    /// <summary>Форма-владелец: её нужно передать в Application.Run.</summary>
    public Form Owner => _owner;

    /// <summary>
    /// Подключает контекстное меню иконки (Настройки / Выход).
    /// </summary>
    /// <param name="openSettings">Открытие окна настроек.</param>
    /// <param name="exit">Завершение агента.</param>
    public void WireMenu(Action openSettings, Action exit)
    {
        var menu = new ContextMenuStrip();
        var settingsItem = new ToolStripMenuItem("Настройки...");
        settingsItem.Click += (_, _) => openSettings();
        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);
        _icon.ContextMenuStrip = menu;
    }

    /// <summary>
    /// Проверяет фильтры/дедупликацию и сразу отправляет уведомление тостом.
    /// Может вызываться с любого потока (дедупликация под замком, вызовы WinRT потокобезопасны).
    /// </summary>
    /// <param name="notification">Уведомление для доставки.</param>
    public void Enqueue(AgentNotification notification)
    {
        var cfg = _config();
        if (!cfg.Enabled)
        {
            AgentLog.Info($"Отклонено «{notification.Title}»: уведомления выключены");
            return; // глобальный выключатель
        }

        lock (_dedupLock)
        {
            // Один тост на id события: повторное прочтение той же записи не показывается.
            if (!_shownEventIds.Add(notification.EventId))
            {
                AgentLog.Info($"Отклонено «{notification.Title}»: дедупликация по EventId");
                return;
            }

            // Повтор того же типа события (на том же задании) внутри SuppressHours подавляется.
            if (cfg.SuppressHours > 0 &&
                _lastShownByKey.TryGetValue(notification.SuppressKey, out var lastShown) &&
                DateTime.UtcNow - lastShown < TimeSpan.FromHours(cfg.SuppressHours))
            {
                AgentLog.Info($"Отклонено «{notification.Title}»: подавление повторов (SuppressHours)");
                return;
            }

            _lastShownByKey[notification.SuppressKey] = DateTime.UtcNow;
        }

        try
        {
            // Системная очередь Windows: несколько тостов могут показаться одновременно.
            _toastNotifier.Show(BuildToast(notification));
            AgentLog.Info($"Тост отправлен: {notification.Title}");
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Не удалось отправить тост «{notification.Title}»: {ex.Message}");
        }
    }

    /// <summary>
    /// Собирает XML тоста: шаблон ToastText02 (заголовок + текст); при непустом Url
    /// добавляет элемент &lt;launch ActivationType="protocol" QueryParameters="{url}"/&gt; —
    /// по клику Windows откроет адрес в стандартном браузере.
    /// </summary>
    private static ToastNotification BuildToast(AgentNotification notification)
    {
        var template = ToastContentManager.CreateToastTemplate(ToastTemplateType.ToastText02);

        // Два элемента <text> шаблона: заголовок и текст. DOM экранирует значения
        // текстовых узлов и атрибутов при сериализации — ручное экранирование не нужно.
        var texts = template.GetElementsByTagName("text");
        texts[0].AppendChild(template.CreateTextNode(Truncate(notification.Title, MaxTitleLength)));
        texts[1].AppendChild(template.CreateTextNode(Truncate(notification.Message, MaxMessageLength)));

        if (!string.IsNullOrEmpty(notification.Url))
        {
            var launch = template.CreateElement("launch");
            launch.SetAttribute("ActivationType", "protocol");
            launch.SetAttribute("QueryParameters", notification.Url);
            template.AppendChild(launch);
        }

        return new ToastNotification(template.Xml);
    }

    /// <summary>Обрезает строку до maxLength (ограничения Windows на текст тоста).</summary>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>Скрывает иконку и освобождает ресурсы.</summary>
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _owner.Dispose();
    }
}
