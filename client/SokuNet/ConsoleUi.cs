using System.Runtime.InteropServices;
using System.Text;

namespace SokuNet;

/// <summary>
/// Owns the CMD window shown while th123.exe is running. The app itself remains
/// a tray application; the console is allocated on demand and detached on game exit
/// so the console window is actually closed rather than merely hidden.
/// </summary>
internal sealed class ConsoleUi : IDisposable
{
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint ScClose = 0xF060;
    private const uint MfByCommand = 0x00000000;
    private const int StdInputHandle = -10;

    private IntPtr _window;
    private bool _allocated;
    private bool _detailsVisible;
    private string _lastFrame = string.Empty;
    private bool _disposed;
    private bool _controlHandlerInstalled;
    private static readonly ConsoleControlHandler ControlHandler = HandleConsoleControl;

    public bool IsVisible => _window != IntPtr.Zero && IsWindow(_window) && IsWindowVisible(_window);

    public bool IsFocused
    {
        get
        {
            if (_window == IntPtr.Zero || !IsWindow(_window))
                return false;
            return GetForegroundWindow() == _window;
        }
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window == IntPtr.Zero || !IsWindow(_window))
        {
            if (!AllocConsole() && Marshal.GetLastWin32Error() != 5)
                return;

            _allocated = true;
            _controlHandlerInstalled = SetConsoleCtrlHandler(ControlHandler, add: true);
            _window = GetConsoleWindow();
            if (_window == IntPtr.Zero)
            {
                CloseWindow();
                return;
            }

            try
            {
                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                Console.OutputEncoding = utf8;
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
                Console.Title = "SokuNet - 天則網";
                Console.CursorVisible = false;
            }
            catch (IOException) { }

            // A console window must not be able to terminate the tray application.
            IntPtr systemMenu = GetSystemMenu(_window, revert: false);
            if (systemMenu != IntPtr.Zero)
            {
                _ = DeleteMenu(systemMenu, ScClose, MfByCommand);
                _ = DrawMenuBar(_window);
            }
        }

        _ = ShowWindow(_window, SwShowNoActivate);
        _lastFrame = string.Empty;
    }

    /// <summary>
    /// Closes the dedicated console window while keeping the tray application alive.
    /// FreeConsole detaches this process; because this console is created exclusively
    /// for SokuNet, Windows closes its console window when the process detaches.
    /// A later Show() call allocates a fresh console window.
    /// </summary>
    public void CloseWindow()
    {
        if (_allocated)
        {
            bool detached = false;
            try { detached = FreeConsole(); } catch { }
            if (!detached && _window != IntPtr.Zero && IsWindow(_window))
            {
                // Fallback for an unusual detach failure. Keep the console attached
                // and its streams/handler intact so a later Show() can reuse it.
                _ = ShowWindow(_window, SwHide);
                return;
            }
        }

        if (_controlHandlerInstalled)
        {
            try { _ = SetConsoleCtrlHandler(ControlHandler, add: false); } catch { }
            _controlHandlerInstalled = false;
        }

        // Do not leave Console writers pointing at handles that have just closed.
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
        string displayName)
    {
        if (!IsVisible)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("============================================================");
        builder.AppendLine("                    SokuNet - 天則網");
        builder.AppendLine("============================================================");
        builder.AppendLine();
        builder.AppendLine("フレンド");
        builder.AppendLine("------------------------------------------------------------");

        FriendPresence[] ordered = friends
            .OrderBy(friend => friend.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ordered.Length == 0)
        {
            builder.AppendLine("  現在オンラインのフレンドはいません。");
        }
        else
        {
            foreach (FriendPresence friend in ordered)
            {
                string state = friend.IsPrivate
                    ? "非公開"
                    : friend.IsAway
                        ? "離席中"
                        : friend.GameStatus;
                builder.Append("  ")
                    .Append(Fit(friend.DisplayName, 20))
                    .Append(" : ")
                    .AppendLine(state);
            }
        }

        if (_detailsVisible)
        {
            builder.AppendLine();
            builder.AppendLine("自分の状態");
            builder.AppendLine("------------------------------------------------------------");
            builder.Append("  表示名       : ").AppendLine(displayName);
            builder.Append("  天則状態     : ").AppendLine(gameStatus);
            builder.Append("  離席中？     : ").AppendLine(isAway ? "はい" : "いいえ");
            builder.Append("  情報公開     : ").AppendLine(isPrivate ? "非公開" : "公開中");
            builder.Append("  通信         : ").AppendLine(connectionStatus);
            builder.Append("  役割         : ").AppendLine(roleText);
        }

        builder.AppendLine();
        builder.AppendLine("------------------------------------------------------------");
        builder.AppendLine("  [S] 自分の状態を表示 / 非表示    [P] 情報公開 / 非公開");
        builder.AppendLine("  ※ S・P はこのCMDウィンドウを選択している間だけ有効");

        string frame = builder.ToString();
        try
        {
            // This console is a status display, not a text-entry shell. Discard
            // queued keystrokes after S/P have been sampled by the app.
            IntPtr inputHandle = GetStdHandle(StdInputHandle);
            if (inputHandle != IntPtr.Zero && inputHandle != new IntPtr(-1))
                _ = FlushConsoleInputBuffer(inputHandle);

            if (frame == _lastFrame)
                return;

            Console.Clear();
            Console.Write(frame);
            _lastFrame = frame;
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private static string Fit(string value, int maxLength)
    {
        string clean = new(value.Where(character => !char.IsControl(character)).ToArray());
        if (clean.Length > maxLength)
            clean = clean[..maxLength];
        return clean.PadRight(maxLength);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseWindow();
    }

    private delegate bool ConsoleControlHandler(uint controlType);

    private static bool HandleConsoleControl(uint controlType) => controlType is 0 or 1 or 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleControlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushConsoleInputBuffer(IntPtr consoleInput);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetSystemMenu(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawMenuBar(IntPtr window);
}
