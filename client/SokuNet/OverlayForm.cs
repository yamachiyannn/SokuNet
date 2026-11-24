using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SokuNet;

/// <summary>
/// Steam-like notification popup only. It never displays local status or a friend list.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int ExStyleTransparent = 0x00000020;
    private const int ExStyleLayered = 0x00080000;
    private const int ExStyleToolWindow = 0x00000080;
    private const int ExStyleNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint HwndTopmost = 0xFFFFFFFF;
    private const uint CombatSceneServer = 13;
    private const uint CombatSceneClient = 14;

    private readonly Icon _applicationIcon;
    private readonly TimeSpan _notificationDuration;
    private readonly ConcurrentQueue<RemoteNotification> _pendingNotifications = new();
    private RemoteNotification? _currentNotification;
    private DateTime _notificationUntil;
    private bool _notificationHasStarted;
    private bool _notificationPaused;
    private TimeSpan _pausedRemaining;
    private GameMonitorResult _game = GameMonitorResult.NotRunning;
    private bool _gameForeground;
    private bool _notificationSoundEnabled;

    public OverlayForm(Icon applicationIcon, bool notificationSoundEnabled, int notificationDurationSeconds)
    {
        _applicationIcon = applicationIcon;
        _notificationSoundEnabled = notificationSoundEnabled;
        _notificationDuration = TimeSpan.FromSeconds(Math.Clamp(notificationDurationSeconds, 1, 30));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        // The overlay is rendered through UpdateLayeredWindow so only its panel
        // background is translucent; icon and text remain fully opaque.
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(470, 112);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= ExStyleLayered | ExStyleTransparent | ExStyleToolWindow | ExStyleNoActivate;
            return parameters;
        }
    }

    public void SetNotificationSoundEnabled(bool enabled) => _notificationSoundEnabled = enabled;

    public void EnqueueRemoteNotification(RemoteNotification notification)
    {
        if (!string.IsNullOrWhiteSpace(notification.Message))
            _pendingNotifications.Enqueue(notification);
    }

    public void UpdateGame(GameMonitorResult game, bool gameForeground)
    {
        _game = game;
        _gameForeground = gameForeground;
        DateTime now = DateTime.UtcNow;

        // Keep the same notification at the head while the game is not in a
        // safe scene. Its display time is paused rather than reordered or lost.
        if (!CanDisplayNotification())
        {
            PauseCurrentNotification();
            return;
        }

        if (_currentNotification is not null && _notificationPaused)
        {
            _notificationUntil = now + _pausedRemaining;
            _notificationPaused = false;
        }

        if (_currentNotification is not null && _notificationHasStarted && now >= _notificationUntil)
        {
            _currentNotification = null;
            _notificationHasStarted = false;
            _notificationPaused = false;
        }

        if (_currentNotification is null && _pendingNotifications.TryDequeue(out RemoteNotification? next))
        {
            _currentNotification = next;
            _notificationHasStarted = false;
            _notificationPaused = false;
        }

        if (_currentNotification is null)
        {
            if (Visible)
                Hide();
            return;
        }

        if (!PositionOverGame())
        {
            PauseCurrentNotification();
            return;
        }

        if (!Visible)
            Show();
        _ = NativeMethods.SetWindowPos(Handle, new IntPtr(unchecked((int)HwndTopmost)),
            Left, Top, Width, Height, SwpNoActivate | SwpShowWindow);
        if (!_notificationHasStarted)
        {
            _notificationHasStarted = true;
            _notificationUntil = now + _notificationDuration;
            PlayNotificationSound();
        }
        Invalidate();
    }

    private void PauseCurrentNotification()
    {
        if (_currentNotification is not null && _notificationHasStarted && !_notificationPaused)
        {
            // Give the user a full readable display after the match finishes.
            _pausedRemaining = _notificationDuration;
            _notificationPaused = true;
        }

        if (Visible)
            Hide();
    }

    private bool CanDisplayNotification() =>
        _game.IsRunning &&
        _gameForeground &&
        _game.SnapshotAvailable &&
        !IsCombatScene(_game);

    private static bool IsCombatScene(GameMonitorResult game) =>
        game.Snapshot.SceneId == CombatSceneServer || game.Snapshot.SceneId == CombatSceneClient;

    private void PlayNotificationSound()
    {
        if (!_notificationSoundEnabled)
            return;
        try { _ = NativeMethods.MessageBeep(0x00000040); }
        catch { /* Windows may have no active sound device or system sound. */ }
    }

    private bool PositionOverGame()
    {
        if (_game.WindowHandle == IntPtr.Zero)
            return false;
        if (!NativeMethods.GetClientRect(_game.WindowHandle, out NativeMethods.Rect clientRect))
            return false;

        var topLeft = new NativeMethods.Point { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new NativeMethods.Point { X = clientRect.Right, Y = clientRect.Bottom };
        if (!NativeMethods.ClientToScreen(_game.WindowHandle, ref topLeft) ||
            !NativeMethods.ClientToScreen(_game.WindowHandle, ref bottomRight))
            return false;

        int x = Math.Max(topLeft.X + 8, bottomRight.X - Width - 22);
        int y = Math.Max(topLeft.Y + 8, bottomRight.Y - Height - 22);
        Location = new Point(x, y);
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_currentNotification is null || Width <= 0 || Height <= 0)
            return;

        using var surface = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(surface))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // 78% opaque neutral gray background; the four corners use a small radius.
            using var panelPath = RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 4);
            using var panelBrush = new SolidBrush(Color.FromArgb(200, 96, 99, 104));
            using var borderPen = new Pen(Color.FromArgb(135, 218, 221, 226), 1.0f);
            graphics.FillPath(panelBrush, panelPath);
            graphics.DrawPath(borderPen, panelPath);

            graphics.DrawIcon(_applicationIcon, new Rectangle(16, 25, 36, 36));
            using var headerFont = new Font("Meiryo UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var messageFont = new Font("Meiryo UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var mutedBrush = new SolidBrush(Color.FromArgb(255, 232, 234, 237));
            using var textBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
            string category = _currentNotification.Category switch
            {
                NotificationCategory.Friend => "フレンド",
                NotificationCategory.Announcement => "お知らせ",
                _ => "システム"
            };
            graphics.DrawString($"SokuNet  ·  {category}", headerFont, mutedBrush, new PointF(66, 18));
            using var messageFormat = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.LineLimit
            };
            graphics.DrawString(_currentNotification.Message, messageFont, textBrush,
                new RectangleF(66, 42, Width - 84, 48), messageFormat);
        }

        UpdateLayeredSurface(surface);
    }

    private void UpdateLayeredSurface(Bitmap surface)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return;

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr dibHandle = IntPtr.Zero;
        IntPtr previousBitmap = IntPtr.Zero;
        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
                return;

            var bitmapInfo = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = Width,
                    Height = -Height, // top-down 32-bit premultiplied BGRA pixels
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = (uint)(Width * Height * 4)
                }
            };
            dibHandle = NativeMethods.CreateDIBSection(memoryDc, ref bitmapInfo, 0,
                out IntPtr dibBits, IntPtr.Zero, 0);
            if (dibHandle == IntPtr.Zero || dibBits == IntPtr.Zero)
                return;

            // A DIB section preserves the per-pixel alpha channel required by
            // UpdateLayeredWindow (unlike a normal GDI drawing surface).
            var bounds = new Rectangle(0, 0, Width, Height);
            BitmapData bitmapData = surface.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int rowBytes = Width * 4;
                byte[] pixels = new byte[rowBytes * Height];
                for (int row = 0; row < Height; row++)
                {
                    IntPtr sourceRow = IntPtr.Add(bitmapData.Scan0, row * bitmapData.Stride);
                    Marshal.Copy(sourceRow, pixels, row * rowBytes, rowBytes);
                }
                Marshal.Copy(pixels, 0, dibBits, pixels.Length);
            }
            finally
            {
                surface.UnlockBits(bitmapData);
            }

            previousBitmap = NativeMethods.SelectObject(memoryDc, dibHandle);
            var destination = new NativeMethods.Point { X = Left, Y = Top };
            var source = new NativeMethods.Point { X = 0, Y = 0 };
            var size = new NativeMethods.Size { Width = Width, Height = Height };
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1
            };

            _ = NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size,
                memoryDc, ref source, 0, ref blend, 2);
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero)
                _ = NativeMethods.SelectObject(memoryDc, previousBitmap);
            if (dibHandle != IntPtr.Zero)
                _ = NativeMethods.DeleteObject(dibHandle);
            if (memoryDc != IntPtr.Zero)
                _ = NativeMethods.DeleteDC(memoryDc);
            _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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
        internal static extern bool GetClientRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr window, ref Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MessageBeep(uint type);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Size { public int Width, Height; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ColorsUsed;
            public uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc,
            ref Point destination, ref Size size, IntPtr sourceDc, ref Point source,
            uint colorKey, ref BlendFunction blend, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo bitmapInfo,
            uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr value);
    }
}
