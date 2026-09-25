using System.Windows.Forms;
using Windows.UI.Notifications;

namespace NotifyAgent;

/// <summary>
/// Точка входа tray-агента: гард одиночного экземпляра (именованный mutex),
/// запуск NotifyIcon и EventListener. Окно настроек применяется без перезапуска
/// процесса — EventListener пересоздаётся с новыми фильтрами/строкой подключения.
/// </summary>
internal static class Program
{
    // Имя именованного mutex гарда одиночного экземпляра (локальная сессия Windows).
    private const string SingleInstanceMutexName = @"Local\SyncBusNotifyAgent";

    /// <summary>
    /// Главная точка входа: инициализация WinForms, запуск агента в цикле сообщений.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // Ручная инициализация вместо ApplicationConfiguration.Initialize():
        // source-generated класс генерируется только для net6.0-windows+; дефолты
        // (SystemAware DPI, Segoe UI 9pt) совпадают с автогенерируемым вызовом.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var baseDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(baseDir, "agent.json");
        var statePath = Path.Combine(baseDir, "state.json");

        AgentLog.Init(Path.Combine(baseDir, "agent.log"));

        // Гард одиночного экземпляра: именованный mutex на всё время жизни процесса.
        using var mutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            AgentLog.Warn("NotifyAgent уже запущен — показываю уведомление в трее и выхожу");
            ShowAlreadyRunningAndExit();
            return;
        }

        var config = AgentConfig.Load(configPath);
        var state = EventState.Load(statePath);

        // Func возвращает живой конфиг: фильтры читаются на каждое событие.
        var notifier = new Notifier(LoadIcon(), () => config);

        var app = new AgentApp(config, state, notifier, configPath, statePath);
        app.Start();

        // Цикл сообщений без главного окна: работает до Application.Exit() (кнопка «Выход»).
        // Скрытая форма-владелец (notifier.Owner) уже создана в Notifier — она нужна
        // только для BeginInvoke на UI-потоке, окном не является.
        Application.Run();

        app.Stop();
        notifier.Dispose();
    }

    /// <summary>Загружает иконку из app.ico рядом с exe.</summary>
    private static Icon LoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        // Фолбэк на системной иконке, если файл иконки отсутствует.
        AgentLog.Warn($"Иконка {iconPath} не найдена — использую системную");
        return SystemIcons.Application;
    }

    /// <summary>
    /// Повторный запуск: тост «уже запущен» (тот же AUMID, что у агента),
    /// через несколько секунд процесс завершается сам.
    /// </summary>
    private static void ShowAlreadyRunningAndExit()
    {
        var notifier = ToastNotificationManager.CreateToastNotifier(Notifier.AppUserModelId);

        // Шаблон ToastText02 (заголовок + текст), без launch — переход не нужен.
        var template = ToastContentManager.CreateToastTemplate(ToastTemplateType.ToastText02);
        var texts = template.GetElementsByTagName("text");
        texts[0].AppendChild(template.CreateTextNode("NotifyAgent"));
        texts[1].AppendChild(template.CreateTextNode("Агент уже запущен — иконка в трее."));

        notifier.Show(new ToastNotification(template.Xml));

        // Даём Windows показать тост (~5 с), затем завершаем процесс.
        Thread.Sleep(6000);
    }

    /// <summary>
    /// Координатор агента: держит EventListener и пересоздаёт его при сохранении
    /// настроек (применение без перезапуска процесса).
    /// </summary>
    private sealed class AgentApp : IDisposable
    {
        private readonly AgentConfig _config;
        private readonly EventState _state;
        private readonly Notifier _notifier;
        private readonly string _configPath;
        private readonly string _statePath;

        private EventListener? _listener;

        public AgentApp(AgentConfig config, EventState state, Notifier notifier, string configPath, string statePath)
        {
            _config = config;
            _state = state;
            _notifier = notifier;
            _configPath = configPath;
            _statePath = statePath;
        }

        /// <summary>Запускает слушатель событий и подключает меню иконки.</summary>
        public void Start()
        {
            _listener = new EventListener(() => _config, _state, _notifier, _statePath);

            _notifier.WireMenu(OpenSettings, () => Application.Exit());
        }

        /// <summary>Открывает окно настроек (модально, на UI-потоке).</summary>
        private void OpenSettings()
        {
            using var form = new SettingsForm(_config, _configPath, ApplySaved);
            form.ShowDialog(_notifier.Owner);
        }

        /// <summary>
        /// Применяет сохранённые настройки без перезапуска: пересоздаёт EventListener
        /// с новыми фильтрами и строкой подключения.
        /// </summary>
        private void ApplySaved(AgentConfig saved)
        {
            // Сначала останавливаем старый слушатель (он разделяет EventState с новым —
            // параллельная запись state.json недопустима), затем создаём новый.
            var old = _listener;
            _listener = null;
            old?.Dispose();
            _listener = new EventListener(() => _config, _state, _notifier, _statePath);
        }

        /// <summary>Останавливает слушатель событий.</summary>
        public void Stop() => _listener?.Dispose();

        /// <summary>Останавливает слушатель и освобождает ресурсы.</summary>
        public void Dispose()
        {
            _listener?.Dispose();
            _listener = null;
        }
    }
}
