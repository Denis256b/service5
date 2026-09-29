using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NotifyAgent;

/// <summary>
/// Немодальная форма статуса соединения с БД: при обрыве показывает
/// «Соединение с БД потеряно. Переподключаемся…», после восстановления —
/// «Соединение восстановлено» и автозакрывается через 5 с. Показывается поверх
/// остальных окон (SetWindowPos/HWND_TOPMOST), в панели задач не отображается.
/// </summary>
public sealed class ConnectionStatusForm : Form
{
    // Автозакрытие после восстановления соединения.
    private const int AutoCloseMilliseconds = 5000;

    // user32: держать окно поверх остальных (эквивалент Topmost = true).
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;

    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _autoCloseTimer;

    /// <summary>Создаёт форму статуса (не показанную).</summary>
    /// <param name="owner">Владелец — скрытая форма-владелец агента.</param>
    public ConnectionStatusForm(Form owner)
    {
        Owner = owner;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new System.Drawing.Size(340, 80);
        Text = "NotifyAgent";

        _label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_label);

        _autoCloseTimer = new System.Windows.Forms.Timer { Interval = AutoCloseMilliseconds };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            Close();
        };
    }

    /// <summary>Показывает форму с текстом обрыва соединения (UI-поток).</summary>
    public void ShowLost()
    {
        _autoCloseTimer.Stop();
        _label.Text = "Соединение с БД потеряно.\nПереподключаемся…";

        if (!Visible)
        {
            Show();
        }

        MakeTopmost(); // вернуть поверх при каждом показе
        Activate();
    }

    /// <summary>Переключает текст на «восстановлено» и запускает автозакрытие через 5 с (UI-поток).</summary>
    public void ShowRestored()
    {
        _label.Text = "Соединение восстановлено";
        _autoCloseTimer.Start();
    }

    /// <summary>Держит окно поверх остальных (SetWindowPos/HWND_TOPMOST).</summary>
    private void MakeTopmost()
    {
        if (IsHandleCreated)
        {
            SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoCloseTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
