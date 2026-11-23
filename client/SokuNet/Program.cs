using System.Diagnostics;
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
    private NetworkService? _network;
    private GameMonitorResult _game = GameMonitorResult.NotRunning;
    private bool _wasGameRunning;
    private bool _pendingGameStartNotice;
    private bool _disposed;
    private long _nextGamePoll;
    private bool _lastAway;

    public SokuNetApplicationContext()
    {
        _settings = _settingsStore.Load();
        _away = new AwayTracker(TimeSpan.FromMinutes(_settings.AwayTimeoutMinutes));

        var menu = new ContextMenuStrip();
        menu.Items.Add("ローカル状態を表示", null, (_, _) => _overlay.ToggleSummary());
        menu.Items.Add("通信設定...", null, (_, _) => OpenNetworkSettings());
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

        RestartNetwork();
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

        string gameStatus = GetGameStatusText(_game);
        _network?.UpdatePresence(new LocalPresence(
            _settings.DisplayName,
            gameStatus,
            isAway,
            _settings.PrivateMode));

        while (_network is not null && _network.TryDequeueNotification(out RemoteNotification? notification))
        {
            if (notification is not null)
                _overlay.EnqueueRemoteStateChange(notification.PlayerName, notification.StateText);
        }

        _overlay.UpdateNetworkInfo(_network?.ConnectionStatus ?? "未設定", _network?.RoleText ?? "未接続");
        _overlay.UpdateGame(_game, gameForeground, isAway, _settings);

        string trayStatus = _network?.ConnectionStatus ?? "未設定";
        string gameText = !_game.IsRunning ? "天則待機中" : gameForeground ? "天則起動中" : "天則背面";
        _trayIcon.Text = LimitTrayText($"SokuNet - {gameText} / {trayStatus}");
        _lastAway = isAway;
    }

    private static string GetGameStatusText(GameMonitorResult game)
    {
        if (!game.IsRunning)
            return "未起動";
        if (!game.SnapshotAvailable)
            return "取得不可";
        return game.SceneStatus switch
        {
            GameSceneStatus.Battle => "対戦中",
            GameSceneStatus.Watching => "観戦中",
            GameSceneStatus.Practice => "練習中",
            GameSceneStatus.Other => "その他",
            _ => "取得不可"
        };
    }

    private static string LimitTrayText(string value) => value.Length <= 63 ? value : value[..63];

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
            _network?.UpdatePresence(new LocalPresence(
                _settings.DisplayName,
                GetGameStatusText(_game),
                _lastAway,
                _settings.PrivateMode));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings.PrivateMode = oldValue;
            _overlay.ShowSystemNotice("設定を保存できませんでした");
        }
    }

    private void OpenNetworkSettings()
    {
        using var dialog = new NetworkSettingsForm(_settings);
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        string oldName = _settings.DisplayName;
        string oldUrl = _settings.RelayUrl;
        string oldCode = _settings.RoomCode;
        _settings.DisplayName = dialog.DisplayName;
        _settings.RelayUrl = dialog.RelayUrl;
        _settings.RoomCode = dialog.RoomCode;
        try
        {
            _settingsStore.Save(_settings);
            RestartNetwork();
            _overlay.ShowSystemNotice("通信設定を保存しました");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings.DisplayName = oldName;
            _settings.RelayUrl = oldUrl;
            _settings.RoomCode = oldCode;
            _overlay.ShowSystemNotice("通信設定を保存できませんでした");
        }
    }

    private void RestartNetwork()
    {
        _network?.Dispose();
        _network = new NetworkService(_settings);
    }

    protected override void ExitThreadCore()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _network?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _overlay.Close();
        _overlay.Dispose();
        _gameMonitor.Dispose();
        _settingsStore.Dispose();
        base.ExitThreadCore();
    }
}
