using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SokuNet;

internal enum LocalOverlayPage
{
    Hidden,
    Summary,
    Details
}

internal sealed class OverlayForm : Form
{
    private const int ExStyleTransparent = 0x00000020;
    private const int ExStyleToolWindow = 0x00000080;
    private const int ExStyleNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint CombatSceneServer = 13;
    private const uint CombatSceneClient = 14;

    private GameMonitorResult _game = GameMonitorResult.NotRunning;
    private bool _gameForeground;
    private bool _isAway;
    private AppSettings _settings = new();
    private LocalOverlayPage _page;
    private string? _notice;
    private DateTime _noticeUntil;
    private readonly ConcurrentQueue<string> _pendingRemoteNotifications = new();
    private bool _isShowingRemoteNotification;
    private string _networkStatus = "未設定";
    private string _networkRole = "未接続";

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Opacity = 0.94;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(500, 170);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= ExStyleTransparent | ExStyleToolWindow | ExStyleNoActivate;
            return cp;
        }
    }

    public void ToggleSummary()
    {
        if (_page == LocalOverlayPage.Hidden)
            _page = LocalOverlayPage.Summary;
        else
            _page = LocalOverlayPage.Hidden;
        RefreshVisibility();
    }

    public void CycleLocalView()
    {
        _page = _page switch
        {
            LocalOverlayPage.Hidden => LocalOverlayPage.Summary,
            LocalOverlayPage.Summary => LocalOverlayPage.Details,
            _ => LocalOverlayPage.Summary
        };
        RefreshVisibility();
    }

    public void HideLocalView()
    {
        _page = LocalOverlayPage.Hidden;
        RefreshVisibility();
    }

    public void ShowSystemNotice(string message)
    {
        _notice = message;
        _noticeUntil = DateTime.UtcNow.AddSeconds(4);
        RefreshVisibility();
    }

    // Network events are queued while the game is in SceneID 13 or 14.
    public void EnqueueRemoteStateChange(string playerName, string stateText)
    {
        string safeName = LimitText(playerName, 28);
        string safeState = LimitText(stateText, 60);
        _pendingRemoteNotifications.Enqueue($"{safeName} の状態が変わりました：{safeState}");
    }

    public void UpdateNetworkInfo(string status, string role)
    {
        _networkStatus = status;
        _networkRole = role;
    }

    public void UpdateGame(GameMonitorResult game, bool gameForeground, bool isAway, AppSettings settings)
    {
        _game = game;
        _gameForeground = gameForeground;
        _isAway = isAway;
        _settings = settings;

        // If a remote notice was already on screen when the match starts (or the
        // game becomes unreadable / loses focus), put it back in the queue.
        if (_isShowingRemoteNotification &&
            (_settings.PrivateMode || !_game.IsRunning || !_gameForeground || !_game.SnapshotAvailable || IsCombatScene(_game)))
        {
            if (!string.IsNullOrEmpty(_notice))
                _pendingRemoteNotifications.Enqueue(_notice);
            _notice = null;
            _isShowingRemoteNotification = false;
        }

        PositionOverGame();
        TryBeginNextRemoteNotification();
        RefreshVisibility();
        Invalidate();
    }

    private void TryBeginNextRemoteNotification()
    {
        if (_isShowingRemoteNotification)
            return;
        if (_settings.PrivateMode || !_game.IsRunning || !_gameForeground || !_game.SnapshotAvailable || IsCombatScene(_game))
            return;
        if (!_pendingRemoteNotifications.TryDequeue(out string? nextNotice))
            return;

        _notice = nextNotice;
        _noticeUntil = DateTime.UtcNow.AddSeconds(6);
        _isShowingRemoteNotification = true;
    }

    private static bool IsCombatScene(GameMonitorResult game) =>
        game.SnapshotAvailable &&
        (game.Snapshot.SceneId == CombatSceneServer || game.Snapshot.SceneId == CombatSceneClient);

    private void PositionOverGame()
    {
        if (!_game.IsRunning || _game.WindowHandle == IntPtr.Zero)
            return;

        if (!NativeMethods.GetClientRect(_game.WindowHandle, out NativeMethods.Rect clientRect))
            return;

        var topLeft = new NativeMethods.Point { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new NativeMethods.Point { X = clientRect.Right, Y = clientRect.Bottom };
        if (!NativeMethods.ClientToScreen(_game.WindowHandle, ref topLeft) ||
            !NativeMethods.ClientToScreen(_game.WindowHandle, ref bottomRight))
            return;

        int width = _page == LocalOverlayPage.Details ? 560 : 500;
        int height = _page switch
        {
            LocalOverlayPage.Summary => 170,
            LocalOverlayPage.Details => 356,
            _ => 110
        };
        Size = new Size(width, height);
        int x = Math.Max(topLeft.X, bottomRight.X - width - 24);
        int y = Math.Max(topLeft.Y, bottomRight.Y - height - 24);
        Location = new Point(x, y);
    }

    private void RefreshVisibility()
    {
        bool hasNotice = _notice is not null && DateTime.UtcNow < _noticeUntil;
        if (!hasNotice)
        {
            _notice = null;
            _isShowingRemoteNotification = false;
            TryBeginNextRemoteNotification();
            hasNotice = _notice is not null && DateTime.UtcNow < _noticeUntil;
        }

        bool shouldShow = _game.IsRunning && _gameForeground && (_page != LocalOverlayPage.Hidden || hasNotice);
        if (shouldShow)
        {
            PositionOverGame();
            if (!Visible)
            {
                Show();
                NativeMethods.SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, SwpNoActivate | SwpShowWindow);
            }
        }
        else if (Visible)
        {
            Hide();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Visible)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var panelPath = RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        using var panelBrush = new SolidBrush(Color.FromArgb(218, 18, 21, 28));
        using var borderPen = new Pen(Color.FromArgb(180, 110, 130, 155), 1.2f);
        e.Graphics.FillPath(panelBrush, panelPath);
        e.Graphics.DrawPath(borderPen, panelPath);

        int y = 15;
        if (_settings.PrivateMode)
        {
            DrawLine(e.Graphics, "情報を非公開にしています", y, bold: true, size: 13f);
            return;
        }

        if (_notice is not null && DateTime.UtcNow < _noticeUntil)
        {
            DrawLine(e.Graphics, _notice, y, bold: true, size: 12.5f);
            y += 32;
            if (_page != LocalOverlayPage.Hidden)
                using (var separator = new Pen(Color.FromArgb(100, 190, 200, 215)))
                    e.Graphics.DrawLine(separator, 16, y - 6, Width - 16, y - 6);
        }

        if (_page == LocalOverlayPage.Summary)
        {
            DrawLine(e.Graphics, $"天則状態：{GetGameStatusText()}", y); y += 29;
            DrawLine(e.Graphics, $"離席中？：{(_game.IsRunning ? (_isAway ? "はい" : "いいえ") : "-")}", y); y += 29;
            DrawLine(e.Graphics, $"情報公開：{(_settings.PrivateMode ? "非公開中" : "公開中")}", y); y += 35;
            DrawLine(e.Graphics, "[Q] 終了    [P] 情報公開/非公開    [S] 詳細表示    [Esc] 閉じる", y, size: 9.5f);
        }
        else if (_page == LocalOverlayPage.Details)
        {
            DrawLine(e.Graphics, "SokuNet - 詳細情報", y, bold: true, size: 13f); y += 34;
            DrawLine(e.Graphics, $"SokuNet：起動中", y); y += 26;
            DrawLine(e.Graphics, $"東方非想天則：{(_game.IsRunning ? "起動中" : "未起動")}", y); y += 26;
            DrawLine(e.Graphics, $"ゲームウィンドウ：{(_game.IsRunning ? (_gameForeground ? "前面" : "背面") : "-")}", y); y += 26;
            DrawLine(e.Graphics, $"天則状態：{GetGameStatusText()}", y); y += 26;
            DrawLine(e.Graphics, $"SceneID：{(_game.SnapshotAvailable ? _game.Snapshot.SceneId.ToString() : "取得不可")}", y); y += 26;
            DrawLine(e.Graphics, $"離席判定：{_settings.AwayTimeoutMinutes}分", y); y += 26;
            DrawLine(e.Graphics, $"通信：{_networkStatus} / {_networkRole}", y); y += 26;
            DrawLine(e.Graphics, $"設定ファイル：{_settingsStorePath()}", y, size: 8.5f); y += 30;
            DrawLine(e.Graphics, "[Q] 終了    [P] 情報公開/非公開    [S] 状態表示    [Esc] 閉じる", y, size: 9.5f);
        }
    }

    private string GetGameStatusText()
    {
        if (!_game.IsRunning)
            return "未起動";
        if (!_game.SnapshotAvailable)
            return "取得不可";
        return _game.SceneStatus switch
        {
            GameSceneStatus.Battle => "対戦中",
            GameSceneStatus.Watching => "観戦中",
            GameSceneStatus.Practice => "練習中",
            GameSceneStatus.Other => "その他",
            _ => "取得不可"
        };
    }

    private static string _settingsStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SokuNet", "config.json");

    private void DrawLine(Graphics graphics, string text, int y, bool bold = false, float size = 11.5f)
    {
        using var font = new Font("Meiryo UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(Color.FromArgb(245, 245, 248));
        graphics.DrawString(text, font, brush, new PointF(18, y));
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string LimitText(string value, int maxLength)
    {
        string clean = new(value.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The application context owns the lifetime; closing the overlay is allowed during shutdown.
        base.OnFormClosing(e);
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { public int X, Y; }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
