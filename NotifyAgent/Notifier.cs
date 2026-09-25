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
    /// только после закрытия предыдущего (BalloonTipClosed) или клика по нему. Клик по
    /// уведомлению немедленно открывает Url через Process.Start (UseShellExecute), не
    /// ожидая BalloonTipClosed (Windows может его не прислать). Дедупликация: одно balloon на id события;
/// повтор того же SuppressKey внутри SuppressHours подавляется. Обращения к NotifyIcon
/// выполняются на UI-потоке (BeginInvoke). Если Windows не прислал BalloonTipClosed
/// (balloon «проглочен»), watchdog принудительно сбрасывает показ через
/// BalloonMilliseconds + 5 с, чтобы очередь не застревала.
/// </summary>
public sealed class Notifier : IDisposable
{
    // Длительность показа одного balloon в миллисекундах.
    private const int BalloonMilliseconds = 10000;

    // Ограничения Windows на текст balloon (заголовок и текст).
    private const int MaxTitleLength = 63;
    private const int MaxMessageLength = 255;

    // Максимальная глубина очереди: при переполнении сбрасывается старейшее уведомление.
    private const int MaxQueueDepth = 100;

    private readonly Form _owner;
    private readonly NotifyIcon _icon;
    private readonly Func<AgentConfig> _config;
    private readonly ConcurrentQueue<AgentNotification> _queue = new();

    // Watchdog застрявшего показа (UI-поток): принудительный сброс, если Windows
    // не прислал BalloonTipClosed (balloon «проглочен»).
    private readonly System.Windows.Forms.Timer _watchdog;

    // Дедупликация: показанные id событий и время последнего показа по SuppressKey.
    private readonly object _dedupLock = new();
    private readonly HashSet<long> _shownEventIds = new();
    private readonly Dictionary<string, DateTime> _lastShownByKey = new();

    // 1, пока какое-то balloon показывается (гарантия «по одному»).
    private int _showing;

    // Текущее balloon (Url открывается немедленно по клику, без ожидания BalloonTipClosed).
    private AgentNotification? _current;

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
        // Создаём handle сразу на UI-потоке: BeginInvoke работает и до старта цикла сообщений.
        // CreateControl() не подходит — для невидимой формы (Visible = false) WinForms
        // откладывает создание handle, поэтому обращаемся к Handle напрямую.
        _ = _owner.Handle;

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "NotifyAgent — уведомления SyncBus",
            Visible = true
        };
        _icon.BalloonTipClicked += OnBalloonTipClicked;
        _icon.BalloonTipClosed += OnBalloonTipClosed;

        // Конструктор вызывается на UI-потоке (Program.Main, до Application.Run) —
        // WinForms Timer корректно привяжется к потоку сообщений.
        _watchdog = new System.Windows.Forms.Timer { Interval = BalloonMilliseconds + 5000 };
        _watchdog.Tick += OnWatchdogTick;
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
            AgentLog.Info($"Отклонено «{notification.Title}»: уведомления выключены");
            return; // глобальный выключатель
        }

        lock (_dedupLock)
        {
            // Одно balloon на id события: повторное прочтение той же записи не показывается.
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

        // Ограничение глубины очереди: сбрасываем старейшие, пока не уместится новое.
        while (_queue.Count >= MaxQueueDepth && _queue.TryDequeue(out _))
        {
            AgentLog.Warn("Переполнение очереди уведомлений — сброшено старейшее");
        }

        _queue.Enqueue(notification);
        AgentLog.Info($"В очередь: {notification.Title} (в очереди: {_queue.Count})");
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

        AgentLog.Info($"Уведомление: {notification.Title}");
        _icon.ShowBalloonTip(
            BalloonMilliseconds,
            Truncate(notification.Title, MaxTitleLength),
            Truncate(notification.Message, MaxMessageLength),
            ToolTipIcon.Warning);

        // Если Windows не прислает BalloonTipClosed — watchdog сбросит показ.
        _watchdog.Start();
    }

    /// <summary>
    /// Обработчик клика по balloon: немедленно открывает Url (не ждём BalloonTipClosed —
    /// Windows может его не прислать), снимает флаг показа и показывает следующее из очереди.
    /// </summary>
    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        // Двойной клик по уже завершённому balloon не откроет браузер повторно.
        if (Interlocked.CompareExchange(ref _showing, 0, 1) != 1)
        {
            return;
        }

        var current = _current;
        if (current is not null && !string.IsNullOrEmpty(current.Url))
        {
            AgentLog.Info($"Клик по уведомлению «{current.Title}» — открываю {current.Url}");
            OpenUrl(current.Url);
        }
        else
        {
            AgentLog.Info($"Клик по уведомлению «{(current?.Title ?? "(нет)")}» — адрес не задан (BaseUrl пуст)");
        }

        // Balloon после клика исчезает: останавливаем watchdog и показываем следующее из очереди.
        _watchdog.Stop();
        TryShowNext();
    }

    /// <summary>
    /// Обработчик закрытия balloon (истечение времени): логирует событие и завершает показ.
    /// Опоздавшее/дублирующееся событие после клика или сброса watchdog — no-op в EndShow.
    /// </summary>
    private void OnBalloonTipClosed(object? sender, EventArgs e)
    {
        AgentLog.Info($"BalloonTipClosed: «{_current?.Title ?? "(нет)"}»");
        EndShow();
    }

    /// <summary>
    /// Общее идемпотентное завершение показа (UI-поток): останавливает watchdog, снимает
    /// флаг показа и показывает следующее из очереди. Позднее/дублирующееся BalloonTipClosed
    /// после клика или сброса watchdog становится no-op.
    /// </summary>
    private void EndShow()
    {
        _watchdog.Stop();
        if (Interlocked.CompareExchange(ref _showing, 0, 1) == 1)
        {
            TryShowNext();
        }
    }

    /// <summary>
    /// Tick watchdog (UI-поток): если флаг показа всё ещё стоит спустя
    /// BalloonMilliseconds + 5 с — Windows не прислал BalloonTipClosed;
    /// принудительно сбрасываем показ и переходим к следующему из очереди.
    /// </summary>
    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        // Пустой показ (watchdog остановлен на каждом пути закрытия): просто глушим тик.
        if (_showing == 0)
        {
            _watchdog.Stop();
            return;
        }

        AgentLog.Warn("Balloon не закрылся за 15 с (Windows не прислал BalloonTipClosed) — принудительный сброс");
        EndShow();
    }

    /// <summary>Открывает адрес в стандартном браузере (UseShellExecute).</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AgentLog.Info($"Открыт адрес: {url}");
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
        _watchdog.Stop();
        _watchdog.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _owner.Dispose();
    }
}
