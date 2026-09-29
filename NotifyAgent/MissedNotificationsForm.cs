using System.Diagnostics;
using System.Windows.Forms;

namespace NotifyAgent;

/// <summary>
/// Немодальное сводное окно накопившихся за время блокировки экрана уведомлений:
/// ListView с колонками «Время / Заголовок / Текст», кнопки «Открыть панель»
/// (скрыта при пустом BaseUrl) и «Закрыть»; двойной клик по строке открывает URL
/// этого уведомления.
/// </summary>
public sealed class MissedNotificationsForm : Form
{
    private readonly List<AgentNotification> _notifications;

    /// <summary>Создаёт сводное окно со списком накопившихся уведомлений.</summary>
    /// <param name="notifications">Уведомления, накопившиеся за время блокировки экрана.</param>
    /// <param name="baseUrl">Адрес веб-панели для кнопки «Открыть панель» (пусто — кнопка скрыта).</param>
    /// <param name="owner">Владелец — форма-владелец агента.</param>
    public MissedNotificationsForm(List<AgentNotification> notifications, string baseUrl, Form owner)
    {
        _notifications = notifications;
        Owner = owner;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new System.Drawing.Size(640, 320);
        Text = "NotifyAgent — накопленные уведомления";

        var listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false
        };
        listView.Columns.Add("Время", 120);
        listView.Columns.Add("Заголовок", 220);
        listView.Columns.Add("Текст", 280);

        foreach (var n in notifications)
        {
            var item = new ListViewItem(n.EnqueuedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"));
            item.SubItems.Add(n.Title);
            item.SubItems.Add(n.Message);
            listView.Items.Add(item);
        }

        // Двойной клик по строке открывает URL этого уведомления.
        listView.DoubleClick += (_, _) =>
        {
            if (listView.SelectedItems.Count > 0)
            {
                var index = listView.SelectedItems[0].Index;
                if (index >= 0 && index < _notifications.Count)
                {
                    OpenUrl(_notifications[index].Url);
                }
            }
        };

        var buttonsPanel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        var openPanelButton = new Button { Text = "Открыть панель", Width = 130, Location = new System.Drawing.Point(12, 8) };
        openPanelButton.Click += (_, _) => OpenUrl(baseUrl);

        var closeButton = new Button { Text = "Закрыть", Width = 90, Location = new System.Drawing.Point(150, 8) };
        closeButton.Click += (_, _) => Close();

        buttonsPanel.Controls.Add(openPanelButton);
        buttonsPanel.Controls.Add(closeButton);

        // Кнопка «Открыть панель» видна только при непустом BaseUrl.
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            openPanelButton.Visible = false;
        }

        Controls.Add(listView);
        Controls.Add(buttonsPanel);
        AcceptButton = closeButton;
    }

    /// <summary>Открывает адрес в стандартном браузере (UseShellExecute).</summary>
    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

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
}
