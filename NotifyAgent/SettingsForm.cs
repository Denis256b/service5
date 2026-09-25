using System.Windows.Forms;

namespace NotifyAgent;

/// <summary>
/// Окно настроек tray-агента: глобальный вкл/выкл, чекбоксы событий, окно
/// подавления повторов (SuppressHours), адрес веб-панели (BaseUrl) и строка
/// подключения к БД. Кнопка «Сохранить» перезаписывает agent.json и вызывает
/// колбэк применения — EventListener пересоздаётся без перезапуска процесса.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AgentConfig _config;
    private readonly string _configPath;
    private readonly Action<AgentConfig> _onSaved;

    private readonly CheckBox _enabledBox = new();
    private readonly CheckBox _syncErrorBox = new();
    private readonly CheckBox _reportErrorBox = new();
    private readonly CheckBox _reportDoneBox = new();
    private readonly CheckBox _reportProcessingBox = new();
    private readonly CheckBox _reportCancelledBox = new();
    private readonly CheckBox _reportRetryBox = new();

    private readonly NumericUpDown _suppressHours = new();
    private readonly TextBox _baseUrl = new();
    private readonly TextBox _connectionString = new();

    /// <summary>
    /// Создаёт окно настроек, заполняя контролы текущими значениями конфига.
    /// </summary>
    /// <param name="config">Конфиг (изменяется и сохраняется при «Сохранить»).</param>
    /// <param name="configPath">Полный путь к agent.json.</param>
    /// <param name="onSaved">Колбэк после сохранения: применяет изменения без перезапуска.</param>
    public SettingsForm(AgentConfig config, string configPath, Action<AgentConfig> onSaved)
    {
        _config = config;
        _configPath = configPath;
        _onSaved = onSaved;

        BuildUi();
        LoadValues();
    }

    /// <summary>Собирает контролы окна (раскладка — TableLayoutPanel).</summary>
    private void BuildUi()
    {
        Text = "NotifyAgent — настройки уведомлений";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new System.Drawing.Size(560, 420);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Глобальный вкл/выкл.
        _enabledBox.Text = "Включить уведомления";
        AddRow(layout, 0, _enabledBox);

        // Разделитель-заголовок «События».
        var eventsHeader = new Label { Text = "События:", AutoSize = true, Font = DefaultFontBold() };
        AddRow(layout, 1, eventsHeader);

        _syncErrorBox.Text = "Ошибка синхронизации (syncError)";
        AddRow(layout, 2, _syncErrorBox);
        _reportErrorBox.Text = "Ошибка отчёта (reportError)";
        AddRow(layout, 3, _reportErrorBox);
        _reportDoneBox.Text = "Отчёт готов (reportDone)";
        AddRow(layout, 4, _reportDoneBox);
        _reportProcessingBox.Text = "Отчёт в обработке (reportProcessing)";
        AddRow(layout, 5, _reportProcessingBox);
        _reportCancelledBox.Text = "Отчёт отменён (reportCancelled)";
        AddRow(layout, 6, _reportCancelledBox);
        _reportRetryBox.Text = "Повторная попытка (reportRetry)";
        AddRow(layout, 7, _reportRetryBox);

        // SuppressHours.
        var suppressLabel = new Label { Text = "Подавление повторов, часов:", AutoSize = true };
        _suppressHours.Minimum = 0;
        _suppressHours.Maximum = 168; // неделя
        _suppressHours.Width = 80;
        AddRow(layout, 8, suppressLabel, _suppressHours);

        // BaseUrl.
        var baseUrlLabel = new Label { Text = "Адрес веб-панели (BaseUrl):", AutoSize = true };
        _baseUrl.Width = 400;
        AddRow(layout, 9, baseUrlLabel, _baseUrl);

        // ConnectionString.
        var connLabel = new Label { Text = "Строка подключения к БД:", AutoSize = true };
        _connectionString.Width = 460;
        AddRow(layout, 10, connLabel, _connectionString);

        // Кнопки.
        var saveButton = new Button { Text = "Сохранить", DialogResult = DialogResult.None };
        saveButton.Click += OnSaveClick;
        var cancelButton = new Button { Text = "Отмена" };
        cancelButton.Click += (_, _) => Close();

        var buttonsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill
        };
        buttonsPanel.Controls.Add(cancelButton);
        buttonsPanel.Controls.Add(saveButton);
        AddRow(layout, 11, buttonsPanel);

        Controls.Add(layout);
    }

    /// <summary>Добавляет строку в раскладку: подпись (или одиночный контрол) + значение.</summary>
    private static void AddRow(TableLayoutPanel layout, int row, Control label, Control? value = null)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        label.Dock = DockStyle.Fill;
        if (value == null)
        {
            layout.Controls.Add(label, 0, row);
            layout.SetColumnSpan(label, 2);
        }
        else
        {
            layout.Controls.Add(label, 0, row);
            value.Dock = DockStyle.Fill;
            layout.Controls.Add(value, 1, row);
        }
    }

    /// <summary>Жирный шрифт для заголовка раздела.</summary>
    private Font DefaultFontBold() => new(Font.FontFamily, Font.Size, FontStyle.Bold);

    /// <summary>Заполняет контролы значениями из конфига.</summary>
    private void LoadValues()
    {
        _enabledBox.Checked = _config.Enabled;
        _syncErrorBox.Checked = _config.SyncError;
        _reportErrorBox.Checked = _config.ReportError;
        _reportDoneBox.Checked = _config.ReportDone;
        _reportProcessingBox.Checked = _config.ReportProcessing;
        _reportCancelledBox.Checked = _config.ReportCancelled;
        _reportRetryBox.Checked = _config.ReportRetry;
        _suppressHours.Value = Math.Clamp(_config.SuppressHours, (int)_suppressHours.Minimum, (int)_suppressHours.Maximum);
        _baseUrl.Text = _config.Web.BaseUrl;
        _connectionString.Text = _config.Db.ConnectionString;
    }

    /// <summary>
    /// Сохраняет значения контролов в конфиг, перезаписывает agent.json и вызывает
    /// колбэк применения (EventListener пересоздаётся). Пустая строка подключения
    /// не допускается.
    /// </summary>
    private void OnSaveClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_connectionString.Text))
        {
            MessageBox.Show(this, "Укажите строку подключения к базе syncbus.", "NotifyAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _config.Enabled = _enabledBox.Checked;
        _config.SyncError = _syncErrorBox.Checked;
        _config.ReportError = _reportErrorBox.Checked;
        _config.ReportDone = _reportDoneBox.Checked;
        _config.ReportProcessing = _reportProcessingBox.Checked;
        _config.ReportCancelled = _reportCancelledBox.Checked;
        _config.ReportRetry = _reportRetryBox.Checked;
        _config.SuppressHours = (int)_suppressHours.Value;
        _config.Web.BaseUrl = _baseUrl.Text.Trim();
        _config.Db.ConnectionString = _connectionString.Text.Trim();

        try
        {
            _config.Save(_configPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось сохранить конфиг: {ex.Message}", "NotifyAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        AgentLog.Info("Настройки сохранены — применяю без перезапуска");
        _onSaved(_config);
        Close();
    }
}
