using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Threading;
using System.Text.Json;
using Microsoft.Win32;
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
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SokuNet";
    private static readonly TimeSpan UiPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan GamePollInterval = TimeSpan.FromMilliseconds(150);

    private readonly SettingsManager _settingsStore = new();
    private readonly AppSettings _settings;
    private readonly GameMonitor _gameMonitor = new();
    private readonly KeyboardActivityTracker _keyboard = new();
    private readonly AwayTracker _away;
    private readonly PlaytimeTracker _playtime;
    private readonly AnnouncementService _announcementService;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<RemoteNotification> _localNotifications = new();
    private IReadOnlyList<SokuNetAnnouncement> _announcementSnapshot = Array.Empty<SokuNetAnnouncement>();
    private Task? _announcementTask;
    private readonly ConsoleUi _consoleUi = new();
    private readonly Icon _applicationIcon;
    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _soundMenuItem;
    private readonly System.Windows.Forms.Timer _timer;
    private NetworkService? _network;
    private GameMonitorResult _game = GameMonitorResult.NotRunning;
    private bool _wasGameRunning;
    private bool _currentAway;
    private bool _disposed;
    private long _nextGamePoll;
    private bool _updatingStartupItem;
    private bool _announcementCheckedForCurrentGame;
    private long _gameSessionStartedAt;
    private long _nextHourlyNotificationAt;

    public SokuNetApplicationContext()
    {
        _settings = _settingsStore.Load();
        _away = new AwayTracker(TimeSpan.FromMinutes(5));
        _playtime = new PlaytimeTracker(_settingsStore.AppDirectory);
        _announcementService = new AnnouncementService(_settingsStore.AppDirectory);
        _announcementSnapshot = _announcementService.GetAnnouncements();
        _applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
        _overlay = new OverlayForm(_applicationIcon, _settings.NotificationSoundEnabled);

        var menu = new ContextMenuStrip();
        menu.Items.Add("通信設定", null, (_, _) => OpenNetworkSettings());
        menu.Items.Add("設定ファイルを開く", null, (_, _) => OpenConfigFile());
        menu.Items.Add("設定フォルダーを開く", null, (_, _) => OpenConfigFolder());
        menu.Items.Add(new ToolStripSeparator());

        _soundMenuItem = new ToolStripMenuItem("通知音を有効にする")
        {
            CheckOnClick = true,
            Checked = _settings.NotificationSoundEnabled
        };
        _soundMenuItem.CheckedChanged += (_, _) => SetNotificationSoundEnabled(_soundMenuItem.Checked);
        menu.Items.Add(_soundMenuItem);

        _startupMenuItem = new ToolStripMenuItem("Windowsログイン時に起動")
        {
            CheckOnClick = true,
            Checked = _settings.StartWithWindows
        };
        _startupMenuItem.CheckedChanged += OnStartupCheckChanged;
        menu.Items.Add(_startupMenuItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "SokuNet - 天則網（待機中）",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) =>
        {
            if (_game.IsRunning)
                _consoleUi.Show();
        };

        if (_settings.StartWithWindows)
            TrySetWindowsStartup(enabled: true);

        // Detect an already-running game before the first presence handshake so
        // friends do not receive a false "game started" event on SokuNet startup.
        _game = _gameMonitor.Update();
        long initialTimestamp = Stopwatch.GetTimestamp();
        _playtime.Update(_game.IsRunning, _game.SceneStatus, false, initialTimestamp);
        _nextGamePoll = initialTimestamp + ToStopwatchTicks(GamePollInterval);
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
        bool commandFocused = _consoleUi.IsFocused;
        bool keyboardInput = _keyboard.PollForNewKeyDown(
            gameForeground,
            commandFocused,
            out bool sPressed,
            out bool pPressed);
        bool watching = _game.SnapshotAvailable && _game.SceneStatus == GameSceneStatus.Watching;
        bool isAway = _away.Update(_game.IsRunning, gameForeground, keyboardInput, watching, now);
        _currentAway = isAway;

        bool gameEnded = !_game.IsRunning && _wasGameRunning;
        if (_game.IsRunning && !_wasGameRunning)
        {
            _consoleUi.Show();
            _gameSessionStartedAt = now;
            _nextHourlyNotificationAt = now + ToStopwatchTicks(TimeSpan.FromHours(1));
            _announcementCheckedForCurrentGame = false;
            if (!IsNetworkConfigured())
                _localNotifications.Enqueue(new RemoteNotification("天則網を確認してください"));
            StartAnnouncementCheck();
        }
        else if (!_game.IsRunning && _wasGameRunning)
        {
            _consoleUi.CloseWindow();
            _gameSessionStartedAt = 0;
            _nextHourlyNotificationAt = 0;
            _announcementCheckedForCurrentGame = false;
        }
        _wasGameRunning = _game.IsRunning;

        if (commandFocused && sPressed)
            _consoleUi.ToggleDetails();
        if (commandFocused && pPressed)
            TogglePrivacy(isAway);

        string gameStatus = GetGameStatusText(_game);
        PlaytimeSnapshot playtime = _playtime.Update(_game.IsRunning, _game.SceneStatus, isAway, now);
        if (gameEnded)
            TrySavePlaytime();
        QueueHourlyNotifications(now);

        _network?.UpdatePresence(new LocalPresence(
            _settings.DisplayName,
            gameStatus,
            isAway,
            _settings.PrivateMode));

        while (_network is not null && _network.TryDequeueNotification(out RemoteNotification? notification))
        {
            if (notification is not null)
                _overlay.EnqueueRemoteNotification(notification);
        }
        while (_localNotifications.TryDequeue(out RemoteNotification? localNotification))
        {
            if (localNotification is not null)
                _overlay.EnqueueRemoteNotification(localNotification);
        }

        _overlay.SetNotificationSoundEnabled(_settings.NotificationSoundEnabled);
        _overlay.UpdateGame(_game, gameForeground);

        if (_game.IsRunning)
        {
            _consoleUi.Render(
                _network?.GetFriends() ?? Array.Empty<FriendPresence>(),
                gameStatus,
                isAway,
                _settings.PrivateMode,
                _network?.ConnectionStatus ?? "未設定",
                _network?.RoleText ?? "未接続",
                _settings.DisplayName,
                IsNetworkConfigured(),
                Volatile.Read(ref _announcementSnapshot),
                playtime);
        }

        string trayStatus = _network?.ConnectionStatus ?? "未設定";
        string gameText = !_game.IsRunning ? "天則待機中" : gameForeground ? "天則前面" : "天則起動中";
        _trayIcon.Text = LimitTrayText($"SokuNet - {gameText} / {trayStatus}");
    }

    private bool IsNetworkConfigured() =>
        !string.IsNullOrWhiteSpace(_settings.DisplayName) &&
        !string.IsNullOrWhiteSpace(_settings.RelayUrl) &&
        !string.IsNullOrWhiteSpace(_settings.RoomCode);

    private void StartAnnouncementCheck()
    {
        if (_announcementCheckedForCurrentGame || !_game.IsRunning)
            return;
        _announcementCheckedForCurrentGame = true;
        _announcementTask = CheckAnnouncementsAsync();
    }

    private async Task CheckAnnouncementsAsync()
    {
        try
        {
            AnnouncementResult result = await _announcementService.CheckAsync(_shutdown.Token).ConfigureAwait(false);
            Volatile.Write(ref _announcementSnapshot, result.Announcements);
            if (!string.IsNullOrWhiteSpace(result.ChangeNotification))
                _localNotifications.Enqueue(new RemoteNotification(result.ChangeNotification));
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (JsonException) { }
    }

    private void QueueHourlyNotifications(long now)
    {
        if (!_game.IsRunning || _gameSessionStartedAt == 0 || _nextHourlyNotificationAt == 0)
            return;
        long hourTicks = ToStopwatchTicks(TimeSpan.FromHours(1));
        while (now >= _nextHourlyNotificationAt)
        {
            int hours = (int)Math.Max(1, (_nextHourlyNotificationAt - _gameSessionStartedAt) / hourTicks);
            _localNotifications.Enqueue(new RemoteNotification($"連続起動: {hours}時間"));
            _nextHourlyNotificationAt += hourTicks;
        }
    }

    private void TrySavePlaytime()
    {
        try { _playtime.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    private void TogglePrivacy(bool isAway)
    {
        bool oldValue = _settings.PrivateMode;
        _settings.PrivateMode = !oldValue;
        try
        {
            _settingsStore.Save(_settings);
            _network?.UpdatePresence(new LocalPresence(
                _settings.DisplayName,
                GetGameStatusText(_game),
                isAway,
                _settings.PrivateMode));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.PrivateMode = oldValue;
            MessageBox.Show("設定ファイルを保存できませんでした。SokuNetのフォルダーに書き込み権限があるか確認してください。",
                "SokuNet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetNotificationSoundEnabled(bool enabled)
    {
        _settings.NotificationSoundEnabled = enabled;
        _overlay.SetNotificationSoundEnabled(enabled);
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The check item still works for this session; the next start uses the saved config.
        }
    }

    private void SetStartWithWindows(bool enabled)
    {
        if (!TrySetWindowsStartup(enabled))
        {
            _updatingStartupItem = true;
            _startupMenuItem.Checked = !enabled;
            _updatingStartupItem = false;
            MessageBox.Show("Windowsログイン時の自動起動設定を変更できませんでした。",
                "SokuNet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.StartWithWindows = enabled;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void OnStartupCheckChanged(object? sender, EventArgs e)
    {
        if (!_updatingStartupItem)
            SetStartWithWindows(_startupMenuItem.Checked);
    }

    private static bool TrySetWindowsStartup(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (key is null)
                return false;
            if (enabled)
                key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    private void OpenConfigFile()
    {
        try
        {
            if (!File.Exists(_settingsStore.ConfigPath))
                _settingsStore.Save(_settings);
            Process.Start(new ProcessStartInfo("notepad.exe")
            {
                UseShellExecute = true,
                ArgumentList = { _settingsStore.ConfigPath }
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"設定ファイルを開けませんでした。\n{_settingsStore.ConfigPath}",
                "SokuNet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenConfigFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { _settingsStore.AppDirectory }
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            MessageBox.Show("SokuNetのフォルダーを開けませんでした。", "SokuNet",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.DisplayName = oldName;
            _settings.RelayUrl = oldUrl;
            _settings.RoomCode = oldCode;
            MessageBox.Show("通信設定を保存できませんでした。", "SokuNet",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RestartNetwork()
    {
        _network?.Dispose();
        _network = new NetworkService(_settings, new LocalPresence(
            _settings.DisplayName, GetGameStatusText(_game), _currentAway, _settings.PrivateMode));
    }

    private static string LimitTrayText(string value) => value.Length <= 63 ? value : value[..63];

    private static long ToStopwatchTicks(TimeSpan duration) =>
        (long)(duration.TotalSeconds * Stopwatch.Frequency);

    protected override void ExitThreadCore()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _shutdown.Cancel();
        try { _announcementTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _playtime.Update(_game.IsRunning, _game.SceneStatus, _currentAway, Stopwatch.GetTimestamp());
        TrySavePlaytime();
        _network?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _overlay.Close();
        _overlay.Dispose();
        _consoleUi.Dispose();
        _gameMonitor.Dispose();
        _announcementService.Dispose();
        _shutdown.Dispose();
        _settingsStore.Dispose();
        _applicationIcon.Dispose();
        base.ExitThreadCore();
    }
}
