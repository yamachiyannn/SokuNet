using System.Runtime.InteropServices;
using System.Text;

namespace SokuNet;

/// <summary>Owns the CMD window shown while th123.exe is running.</summary>
internal sealed class ConsoleUi : IDisposable
{
    private const uint ScClose = 0xF060;
    private const uint MfByCommand = 0x00000000;
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    private IntPtr _window;
    private bool _allocated;
    private bool _detailsVisible;
    private string _lastFrame = string.Empty;
    private bool _disposed;
    private bool _controlHandlerInstalled;
    private static readonly ConsoleControlHandler ControlHandler = HandleConsoleControl;

    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _saveSettings;

    public ConsoleUi(AppSettings settings, Action<AppSettings> saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
    }

    public bool IsVisible => _window != IntPtr.Zero && IsWindow(_window) && IsWindowVisible(_window);
    public bool IsFocused => _window != IntPtr.Zero && IsWindow(_window) && GetForegroundWindow() == _window;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window == IntPtr.Zero || !IsWindow(_window))
        {
            if (!AllocConsole() && Marshal.GetLastWin32Error() != 5) return;
            _allocated = true;
            _controlHandlerInstalled = SetConsoleCtrlHandler(ControlHandler, add: true);
            _window = GetConsoleWindow();
            if (_window == IntPtr.Zero) { CloseWindow(); return; }
            try
            {
                var utf8 = new UTF8Encoding(false);
                Console.OutputEncoding = utf8;
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
                Console.Title = "SokuNet - 天則網";
                Console.CursorVisible = false;
            }
            catch (IOException) { }

            TryRestoreWindowSize();

            IntPtr systemMenu = GetSystemMenu(_window, revert: false);
            if (systemMenu != IntPtr.Zero)
            {
                _ = DeleteMenu(systemMenu, ScClose, MfByCommand);
                _ = DrawMenuBar(_window);
            }
        }
        // Quick Edit pauses console output while text is selected. Windows then
        // prefixes the title with "選択" and the UI may appear frozen until a key
        // is pressed. Disable it so mouse selection cannot suspend SokuNet.
        DisableQuickEditMode();
        _ = ShowWindow(_window, 4);
        _lastFrame = string.Empty;
    }

    public void CloseWindow()
    {
        if (_allocated)
        {
            RememberWindowSize();
            bool detached = false;
            try { detached = FreeConsole(); } catch { }
            if (!detached && _window != IntPtr.Zero && IsWindow(_window))
            {
                _ = ShowWindow(_window, 0);
                return;
            }
        }
        if (_controlHandlerInstalled)
        {
            try { _ = SetConsoleCtrlHandler(ControlHandler, add: false); } catch { }
            _controlHandlerInstalled = false;
        }
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Console.SetIn(TextReader.Null);
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        _allocated = false;
        _window = IntPtr.Zero;
        _lastFrame = string.Empty;
    }

    public void ToggleDetails()
    {
        _detailsVisible = !_detailsVisible;
        _lastFrame = string.Empty;
    }

    public void Render(
        IReadOnlyCollection<FriendPresence> friends,
        string gameStatus,
        bool isAway,
        bool isPrivate,
        string connectionStatus,
        string roleText,
        string displayName,
        bool networkConfigured,
        IReadOnlyList<SokuNetAnnouncement> announcements,
        PlaytimeSnapshot playtime)
    {
        if (!IsVisible) return;
        string frame = BuildFrame(friends, gameStatus, isAway, isPrivate, connectionStatus,
            roleText, displayName, networkConfigured, announcements, playtime);
        if (frame == _lastFrame) return;

        try
        {
            IntPtr inputHandle = GetStdHandle(StdInputHandle);
            if (inputHandle != IntPtr.Zero && inputHandle != new IntPtr(-1))
                _ = FlushConsoleInputBuffer(inputHandle);
            Console.Clear();
            if (!networkConfigured)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("============================================================");
                Console.WriteLine("                    SokuNet - 天則網");
                Console.WriteLine("============================================================");
                Console.WriteLine();
                Console.WriteLine("※タスクトレイのアイコンを右クリックし、通信設定を入力してください");
                Console.ResetColor();
            }
            else
            {
                DrawConfiguredFrame(friends, gameStatus, isAway, isPrivate, connectionStatus,
                    roleText, displayName, announcements, playtime);
            }
            _lastFrame = frame;
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private string BuildFrame(
        IReadOnlyCollection<FriendPresence> friends, string gameStatus, bool isAway, bool isPrivate,
        string connectionStatus, string roleText, string displayName, bool networkConfigured,
        IReadOnlyList<SokuNetAnnouncement> announcements, PlaytimeSnapshot playtime)
    {
        var builder = new StringBuilder();
        builder.Append(networkConfigured).Append('|').Append(_detailsVisible).Append('|')
            .Append(gameStatus).Append('|').Append(isAway).Append('|').Append(isPrivate).Append('|')
            .Append(connectionStatus).Append('|').Append(roleText).Append('|').Append(displayName).Append('|')
            .Append(FormatTime(playtime.GameRunning)).Append('|').Append(FormatTime(playtime.Practice)).Append('|')
            .Append(FormatTime(playtime.Battle));
        foreach (SokuNetAnnouncement item in announcements)
            builder.Append("|A:").Append(item.Id).Append(':').Append(item.Title).Append(':').Append(item.Message).Append(':').Append(item.Url);
        foreach (FriendPresence friend in friends.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase))
            builder.Append("|F:").Append(friend.PlayerId).Append(':').Append(friend.DisplayName).Append(':')
                .Append(friend.GameStatus).Append(':').Append(friend.IsAway).Append(':').Append(friend.IsPrivate);
        return builder.ToString();
    }

    private void DrawConfiguredFrame(
        IReadOnlyCollection<FriendPresence> friends, string gameStatus, bool isAway, bool isPrivate,
        string connectionStatus, string roleText, string displayName,
        IReadOnlyList<SokuNetAnnouncement> announcements, PlaytimeSnapshot playtime)
    {
        WriteLine("============================================================", ConsoleColor.DarkGray);
        WriteLine("                    SokuNet - 天則網", ConsoleColor.Cyan);
        WriteLine("============================================================", ConsoleColor.DarkGray);

        Console.WriteLine();
        WriteLine("お知らせ", ConsoleColor.Cyan);
        WriteLine("------------------------------------------------------------", ConsoleColor.White);
        if (announcements.Count == 0)
            WriteLine("  現在お知らせはありません。", ConsoleColor.DarkGray);
        else
        {
            foreach (SokuNetAnnouncement announcement in announcements)
            {
                WriteLine($"  {announcement.Title}: {announcement.Message}", ConsoleColor.Yellow);
                if (!string.IsNullOrWhiteSpace(announcement.Url))
                    WriteLine($"    {announcement.Url}", ConsoleColor.DarkGray);
            }
        }

        Console.WriteLine();
        WriteLine("フレンド", ConsoleColor.White);
        WriteLine("------------------------------------------------------------", ConsoleColor.White);
        FriendPresence[] ordered = friends.OrderBy(friend => friend.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ordered.Length == 0)
            WriteLine("  現在オンラインのフレンドはいません。", ConsoleColor.DarkGray);
        else
        {
            foreach (FriendPresence friend in ordered)
            {
                string state = friend.IsPrivate ? "非公開" : friend.IsAway ? "離席中" : friend.GameStatus;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write("  " + Fit(friend.DisplayName, 20) + " : ");
                Console.ForegroundColor = GetStateColor(state);
                Console.WriteLine(state);
            }
        }

        if (_detailsVisible)
        {
            Console.WriteLine();
            WriteLine("自分の状態", ConsoleColor.White);
            WriteLine("------------------------------------------------------------", ConsoleColor.White);
            WriteLine($"  表示名       : {displayName}", ConsoleColor.Gray);
            WriteLine($"  天則状態     : {gameStatus}", ConsoleColor.Gray);
            WriteLine($"  離席中？     : {(isAway ? "はい" : "いいえ")}", isAway ? ConsoleColor.White : ConsoleColor.Gray);
            WriteLine($"  情報公開     : {(isPrivate ? "非公開" : "公開中")}", isPrivate ? ConsoleColor.DarkGray : ConsoleColor.Gray);
            WriteLine($"  通信         : {connectionStatus}", ConsoleColor.Gray);
            WriteLine($"  役割         : {roleText}", ConsoleColor.Gray);
            Console.WriteLine();
            WriteLine($"  天則起動時間 : {FormatTime(playtime.GameRunning)}", ConsoleColor.White);
            WriteLine($"  総練習時間   : {FormatTime(playtime.Practice)}", ConsoleColor.White);
            WriteLine($"  総対戦時間   : {FormatTime(playtime.Battle)}", ConsoleColor.White);
        }

        Console.WriteLine();
        WriteLine("------------------------------------------------------------", ConsoleColor.White);
        WriteLine("  [S] 自分の状態を表示 / 非表示    [P] 情報公開 / 非公開", ConsoleColor.Gray);
        WriteLine("  ※ S・P はこのCMDウィンドウを選択している間だけ有効", ConsoleColor.DarkGray);
        Console.ResetColor();
    }

    private static ConsoleColor GetStateColor(string state) => state switch
    {
        "対戦中" => ConsoleColor.Red,
        "練習中" => ConsoleColor.Green,
        "観戦中" => ConsoleColor.Blue,
        "非公開" => ConsoleColor.DarkGray,
        "離席中" => ConsoleColor.White,
        _ => ConsoleColor.White
    };

    private void TryRestoreWindowSize()
    {
        if (_settings.ConsoleWindowWidth <= 0 || _settings.ConsoleWindowHeight <= 0)
            return;

        try
        {
            int maxWidth = Console.LargestWindowWidth;
            int maxHeight = Console.LargestWindowHeight;
            if (maxWidth < 40 || maxHeight < 10)
                return;

            int width = Math.Clamp(_settings.ConsoleWindowWidth, 40, maxWidth);
            int height = Math.Clamp(_settings.ConsoleWindowHeight, 10, maxHeight);

            if (Console.BufferWidth < width)
                Console.BufferWidth = width;
            if (Console.BufferHeight < height)
                Console.BufferHeight = height;

            Console.SetWindowSize(width, height);
        }
        catch (Exception exception) when (exception is IOException
            or ArgumentOutOfRangeException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            // Keep the console's host-selected size if the saved size is not
            // supported by the current display or console host.
        }
    }

    private void RememberWindowSize()
    {
        if (_window == IntPtr.Zero || !IsWindow(_window))
            return;

        try
        {
            int width = Console.WindowWidth;
            int height = Console.WindowHeight;
            if (width < 40 || height < 10)
                return;
            if (_settings.ConsoleWindowWidth == width && _settings.ConsoleWindowHeight == height)
                return;

            _settings.ConsoleWindowWidth = width;
            _settings.ConsoleWindowHeight = height;
            try { _saveSettings(_settings); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The current session continues even if the size cannot be saved.
            }
        }
        catch (Exception exception) when (exception is IOException
            or ArgumentOutOfRangeException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            // The console may already be detaching or unavailable.
        }
    }

    private static void DisableQuickEditMode()
    {
        IntPtr inputHandle = GetStdHandle(StdInputHandle);
        if (inputHandle == IntPtr.Zero || inputHandle == new IntPtr(-1))
            return;
        if (!GetConsoleMode(inputHandle, out uint mode))
            return;

        mode = (mode | EnableExtendedFlags) & ~EnableQuickEditMode;
        _ = SetConsoleMode(inputHandle, mode);
    }

    private static void WriteLine(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
    }

    private static string FormatTime(TimeSpan value)
    {
        long totalMinutes = Math.Max(0, (long)value.TotalMinutes);
        return $"{totalMinutes / 60}時間 {totalMinutes % 60:D2}分";
    }

    private static string Fit(string value, int maxLength)
    {
        string clean = new(value.Where(character => !char.IsControl(character)).ToArray());
        if (clean.Length > maxLength) clean = clean[..maxLength];
        return clean.PadRight(maxLength);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseWindow();
    }

    private delegate bool ConsoleControlHandler(uint controlType);
    private static bool HandleConsoleControl(uint controlType) => controlType is 0 or 1 or 2;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(ConsoleControlHandler handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushConsoleInputBuffer(IntPtr consoleInput);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);
    [DllImport("user32.dll")] private static extern bool DrawMenuBar(IntPtr window);
}
