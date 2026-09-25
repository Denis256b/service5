using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Forms;

namespace NotifyAgent;

/// <summary>
/// Одно уведомление, доставляемое balloon-подсказкой в трей.
/// </summary>
/// <param name="EventId">Уникальный id источника (Id записи SyncHistory / Id события ReportTaskEvents) — дедупликация по нему.</param>
/// <param name="SuppressKey">Ключ подавления повторов: статус + задание (для отчётов) или тип (для синхронизации).</param>
/// <param name="Title">Заголовок balloon-уведомления.</param>
/// <param name="Message">Текст уведомления (~200 символов).</param>
/// <param name="Url">Адрес веб-панели для перехода по клику; пустая строка — без перехода.</param>
public sealed record AgentNotification(long EventId, string SuppressKey, string Title, string Message, string Url);

/// <summary>
/// Доставка системных уведомлений: NotifyIcon + очередь balloon-подсказок. Показ
/// строго по одному (событийно, без блокировки UI-потока): новое balloon показывается
/// только после закрытия предыдущего (BalloonTipClosed). Клик по уведомлению открывает
/// Url через Process.Start (UseShellExecute). Дедупликация: одно balloon на id события;
/// повтор того же SuppressKey внутри SuppressHours подавляется. Обращения к NotifyIcon
/// выполняются на UI-потоке (BeginInvoke).
/// </summary>
public sealed class Notifier : IDisposable
{
    // Длительность показа одного balloon в миллисекундах.
    private const int BalloonMilliseconds = 10000;

    // Ограничения Windows на текст balloon (заголовок и текст).
    private const int MaxTitleLength = 63;
    private const int MaxMessageLength = 255;

    private readonly Form _owner;
    private readonly NotifyIcon _icon;
    private readonly Func<AgentConfig> _config;
    private readonly ConcurrentQueue<AgentNotification> _queue = new();

    // Дедупликация: показанные id событий и время последнего показа по SuppressKey.
    private readonly object _dedupLock = new();
    private readonly HashSet<long> _shownEventIds = new();
    private readonly Dictionary<string, DateTime> _lastShownByKey = new();

    // 1, пока какое-то balloon показывается (гарантия «по одному»).
    private int _showing;

    // Текущее balloon и флаг клика по нему (клик обрабатывается в BalloonTipClosed).
    private AgentNotification? _current;
    private bool _clicked;

    /// <summary>
    /// Создаёт иконку в трее (скрытая форма-владелец для UI-потока).
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
        // Создаём handle сразу: BeginInvoke работает и до старта цикла сообщений.
        _owner.CreateControl();

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "NotifyAgent — уведомления SyncBus",
            Visible = true
        };
        _icon.BalloonTipClicked += OnBalloonTipClicked;
        _icon.BalloonTipClosed += OnBalloonTipClosed;
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
    /// Проверяет фильтры/дедупликацию и ставит уведомление в очередь показа.
    /// Может вызываться с любого потока (очередь и дедупликация потокобезопасны).
    /// </summary>
    /// <param name="notification">Уведомление для доставки.</param>
    public void Enqueue(AgentNotification notification)
    {
        var cfg = _config();
        if (!cfg.Enabled)
        {
            return; // глобальный выключатель
        }

        lock (_dedupLock)
        {
            // Одно balloon на id события: повторное прочтение той же записи не показывается.
            if (!_shownEventIds.Add(notification.EventId))
            {
                return;
            }

            // Повтор того же типа события (на том же задании) внутри SuppressHours подавляется.
            if (cfg.SuppressHours > 0 &&
                _lastShownByKey.TryGetValue(notification.SuppressKey, out var lastShown) &&
                DateTime.UtcNow - lastShown < TimeSpan.FromHours(cfg.SuppressHours))
            {
                return;
            }

            _lastShownByKey[notification.SuppressKey] = DateTime.UtcNow;
        }

        _queue.Enqueue(notification);
        TryShowNext();
    }

    /// <summary>
    /// Показывает следующее уведомление на UI-потоке, если сейчас ничего не показывается.
    /// Событийно: закрытие текущего balloon (BalloonTipClosed) вызывает этот метод снова.
    /// </summary>
    private void TryShowNext()
    {
        if (!_owner.IsHandleCreated || _owner.IsDisposed)
        {
            return;
        }

        _owner.BeginInvoke(new Action(() =>
        {
            // Пока одно balloon активно — следующее ждёт в очереди.
            if (Interlocked.CompareExchange(ref _showing, 1, 0) != 0)
            {
                return;
            }

            if (!_queue.TryDequeue(out var notification))
            {
                Interlocked.Exchange(ref _showing, 0); // очередь пуста — снимаем флаг
                return;
            }

            ShowOne(notification);
        }));
    }

    /// <summary>Показывает одно balloon. Выполняется на UI-потоке.</summary>
    private void ShowOne(AgentNotification notification)
    {
        _current = notification;
        _clicked = false;

        AgentLog.Info($"Уведомление: {notification.Title}");
        _icon.ShowBalloonTip(
            BalloonMilliseconds,
            Truncate(notification.Title, MaxTitleLength),
            Truncate(notification.Message, MaxMessageLength),
            ToolTipIcon.Warning);
    }

    /// <summary>Обработчик клика по balloon: фиксирует, что нужен переход по Url.</summary>
    private void OnBalloonTipClicked(object? sender, EventArgs e) => _clicked = true;

    /// <summary>
    /// Обработчик закрытия balloon (клик или истечение времени): при клике открывает
    /// Url, снимает флаг показа и показывает следующее из очереди.
    /// </summary>
    private void OnBalloonTipClosed(object? sender, EventArgs e)
    {
        var current = _current;
        if (_clicked && current is { } c && !string.IsNullOrEmpty(c.Url))
        {
            OpenUrl(c.Url);
        }

        Interlocked.Exchange(ref _showing, 0);
        TryShowNext();
    }

    /// <summary>Открывает адрес в стандартном браузере (UseShellExecute).</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Не удалось открыть {url}: {ex.Message}");
        }
    }

    /// <summary>Обрезает строку до maxLength (ограничения Windows на текст balloon).</summary>
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
