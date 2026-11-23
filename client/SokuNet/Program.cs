using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SokuNet;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, name: @"Local\SokuNet.TrayApplication", createdNew: out bool createdNew);
        if (!createdNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new SokuNetApplicationContext());
    }
}

internal sealed class SokuNetApplicationContext : ApplicationContext
{
    private static readonly TimeSpan UiPollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan GamePollInterval = TimeSpan.FromMilliseconds(100);
    private readonly SettingsManager _settingsStore = new();
    private readonly AppSettings _settings;
    private readonly GameMonitor _gameMonitor = new();
    private readonly KeyboardActivityTracker _keyboard = new();
    private readonly AwayTracker _away;
    private readonly OverlayForm _overlay = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private GameMonitorResult _game = GameMonitorResult.NotRunning;
    private bool _wasGameRunning;
    private bool _pendingGameStartNotice;
    private bool _disposed;
    private long _nextGamePoll;

    public SokuNetApplicationContext()
    {
        _settings = _settingsStore.Load();
        _away = new AwayTracker(TimeSpan.FromMinutes(_settings.AwayTimeoutMinutes));

        var menu = new ContextMenuStrip();
        menu.Items.Add("ローカル状態を表示", null, (_, _) => _overlay.ToggleSummary());
        menu.Items.Add("情報公開 / 非公開を切り替え", null, (_, _) => TogglePrivacy());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "SokuNet - 天則網（待機中）",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => _overlay.ToggleSummary();

        _timer = new System.Windows.Forms.Timer { Interval = (int)UiPollInterval.TotalMilliseconds };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= _nextGamePoll)
        {
            _game = _gameMonitor.Update();
            _nextGamePoll = now + ToStopwatchTicks(GamePollInterval);
        }
        bool gameForeground = _game.IsRunning && _gameMonitor.IsCurrentProcessForeground;

        bool keyboardInput = _keyboard.PollForNewKeyDown(
            gameForeground,
            out bool sPressed,
            out bool pPressed,
            out bool qPressed,
            out bool escapePressed);

        bool isAway = _away.Update(_game.IsRunning, gameForeground, keyboardInput, now);

        if (_game.IsRunning && !_wasGameRunning)
            _pendingGameStartNotice = true;
        _wasGameRunning = _game.IsRunning;

        if (_pendingGameStartNotice && gameForeground)
        {
            _overlay.ShowSystemNotice("東方非想天則を検知しました。Sキーでローカル状態を表示");
            _pendingGameStartNotice = false;
        }

        if (gameForeground)
        {
            if (sPressed)
                _overlay.CycleLocalView();
            if (pPressed)
                TogglePrivacy();
            if (qPressed)
            {
                ExitThread();
                return;
            }
            if (escapePressed)
                _overlay.HideLocalView();
        }

        _overlay.UpdateGame(_game, gameForeground, isAway, _settings);
        _trayIcon.Text = _game.IsRunning
            ? (gameForeground ? "SokuNet - 天則起動中" : "SokuNet - 天則起動中（背面）")
            : "SokuNet - 天則待機中";
    }

    private static long ToStopwatchTicks(TimeSpan duration) =>
        (long)(duration.TotalSeconds * Stopwatch.Frequency);

    private void TogglePrivacy()
    {
        bool oldValue = _settings.PrivateMode;
        _settings.PrivateMode = !oldValue;
        try
        {
            _settingsStore.Save(_settings);
            _overlay.ShowSystemNotice(_settings.PrivateMode ? "情報を非公開にしました" : "情報を公開しました");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings.PrivateMode = oldValue;
            _overlay.ShowSystemNotice("設定を保存できませんでした");
        }
    }

    protected override void ExitThreadCore()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _overlay.Close();
        _overlay.Dispose();
        _gameMonitor.Dispose();
        _settingsStore.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class KeyboardActivityTracker
{
    private const int VirtualKeyCount = 256;
    private const short KeyDownMask = unchecked((short)0x8000);
    private readonly bool[] _previousDown = new bool[VirtualKeyCount];

    public bool PollForNewKeyDown(
        bool gameForeground,
        out bool sPressed,
        out bool pPressed,
        out bool qPressed,
        out bool escapePressed)
    {
        bool anyNewKeyDown = false;
        sPressed = false;
        pPressed = false;
        qPressed = false;
        escapePressed = false;

        for (int virtualKey = 1; virtualKey < VirtualKeyCount; virtualKey++)
        {
            if (virtualKey is 0x01 or 0x02 or 0x04 or 0x05 or 0x06 or 0xE7)
                continue;

            bool isDown = (KeyboardNativeMethods.GetAsyncKeyState(virtualKey) & KeyDownMask) != 0;
            bool pressed = gameForeground && isDown && !_previousDown[virtualKey];
            if (pressed)
            {
                anyNewKeyDown = true;
                switch (virtualKey)
                {
                    case 0x53: sPressed = true; break; // S
                    case 0x50: pPressed = true; break; // P
                    case 0x51: qPressed = true; break; // Q
                    case 0x1B: escapePressed = true; break; // Escape
                }
            }
            _previousDown[virtualKey] = isDown;
        }

        return anyNewKeyDown;
    }
}

internal sealed class AwayTracker
{
    private readonly long _thresholdTicks;
    private long _inactiveDurationTicks;
    private long _lastTimestamp;
    private bool _wasGameForeground;

    public AwayTracker(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        _thresholdTicks = (long)(threshold.TotalSeconds * Stopwatch.Frequency);
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    public bool Update(bool processRunning, bool gameForeground, bool keyboardInput, long now)
    {
        if (!processRunning)
        {
            Reset(now);
            return false;
        }

        long elapsed = Math.Max(0, now - _lastTimestamp);
        if (_wasGameForeground && gameForeground && !keyboardInput)
            _inactiveDurationTicks = Math.Min(long.MaxValue - elapsed, _inactiveDurationTicks) + elapsed;

        if (gameForeground && keyboardInput)
            _inactiveDurationTicks = 0;

        _wasGameForeground = gameForeground;
        _lastTimestamp = now;
        return _inactiveDurationTicks >= _thresholdTicks;
    }

    public void Reset(long? now = null)
    {
        _inactiveDurationTicks = 0;
        _wasGameForeground = false;
        _lastTimestamp = now ?? Stopwatch.GetTimestamp();
    }
}

internal static class KeyboardNativeMethods
{
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);
}
