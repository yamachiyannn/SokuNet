using System.Diagnostics;
using System.Text;

namespace SokuNet;

internal static class Program
{
    private static readonly TimeSpan MainLoopInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan GamePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromMilliseconds(200);
    private static int _stopRequested;

    private static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            Console.Title = "SokuNet - 天則網";
            Console.CursorVisible = false;
        }
        catch (IOException)
        {
            // Console hosts may not support title/cursor operations.
        }

        using var settingsStore = new SettingsManager();
        AppSettings settings = settingsStore.Load();
        using var gameMonitor = new GameMonitor();

        var keyboard = new KeyboardActivityTracker();
        var away = new AwayTracker(TimeSpan.FromMinutes(settings.AwayTimeoutMinutes));
        var ui = new ConsoleUI();

        GameMonitorResult game = GameMonitorResult.NotRunning;
        bool settingsView = false;
        string? notice = null;
        long nextGamePoll = 0;
        long nextUiRefresh = 0;
        long lastNoticeAt = 0;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Interlocked.Exchange(ref _stopRequested, 1);
        };

        try
        {
            Console.Clear();
            while (Volatile.Read(ref _stopRequested) == 0)
            {
                long now = Stopwatch.GetTimestamp();

                while (TryReadConsoleKey(out ConsoleKey key))
                {
                    switch (key)
                    {
                        case ConsoleKey.Q:
                            Interlocked.Exchange(ref _stopRequested, 1);
                            break;
                        case ConsoleKey.P:
                            settings.PrivateMode = !settings.PrivateMode;
                            try
                            {
                                settingsStore.Save(settings);
                                notice = settings.PrivateMode
                                    ? "情報を非公開にしました。"
                                    : "情報を公開しました。";
                                lastNoticeAt = now;
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                settings.PrivateMode = !settings.PrivateMode;
                                notice = $"設定を保存できませんでした: {ex.Message}";
                                lastNoticeAt = now;
                            }
                            break;
                        case ConsoleKey.S:
                            settingsView = !settingsView;
                            break;
                    }
                }

                if (now >= nextGamePoll)
                {
                    game = gameMonitor.Update();
                    nextGamePoll = now + ToStopwatchTicks(GamePollInterval);
                    if (!game.IsRunning)
                        away.Reset(now);
                }

                bool gameIsForeground = game.IsRunning && gameMonitor.IsCurrentProcessForeground;
                bool keyboardInput = keyboard.PollForNewKeyDown(gameIsForeground);
                bool isAway = away.Update(game.IsRunning, gameIsForeground, keyboardInput, now);

                if (now >= nextUiRefresh)
                {
                    ui.Render(game, gameIsForeground, isAway, settings, settingsView,
                        notice is not null && now - lastNoticeAt < ToStopwatchTicks(TimeSpan.FromSeconds(3))
                            ? notice
                            : null);
                    nextUiRefresh = now + ToStopwatchTicks(UiRefreshInterval);
                }

                Thread.Sleep(MainLoopInterval);
            }
        }
        finally
        {
            try
            {
                Console.CursorVisible = true;
                Console.SetCursorPosition(0, Math.Min(Console.CursorTop, Math.Max(0, Console.BufferHeight - 1)));
            }
            catch (IOException)
            {
                // Ignore console teardown errors.
            }
        }

        return 0;
    }

    private static bool TryReadConsoleKey(out ConsoleKey key)
    {
        key = default;
        try
        {
            if (!Console.KeyAvailable)
                return false;
            key = Console.ReadKey(intercept: true).Key;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static long ToStopwatchTicks(TimeSpan duration) =>
        (long)(duration.TotalSeconds * Stopwatch.Frequency);
}
