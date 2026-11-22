using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SokuNet;

internal sealed class KeyboardActivityTracker
{
    private const int VirtualKeyCount = 256;
    private const short KeyDownMask = unchecked((short)0x8000);
    private readonly bool[] _previousDown = new bool[VirtualKeyCount];

    public bool PollForNewKeyDown(bool gameForeground)
    {
        bool anyNewKeyDown = false;

        // Mouse virtual keys are deliberately excluded. Key values are never stored
        // or logged; only down/up transitions are observed.
        for (int virtualKey = 1; virtualKey < VirtualKeyCount; virtualKey++)
        {
            if (virtualKey is 0x01 or 0x02 or 0x04 or 0x05 or 0x06 or 0xE7)
                continue;

            bool isDown = (KeyboardNativeMethods.GetAsyncKeyState(virtualKey) & KeyDownMask) != 0;
            if (gameForeground && isDown && !_previousDown[virtualKey])
                anyNewKeyDown = true;
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

        // A key-down edge resets the timer only while the game is foreground.
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

internal static partial class KeyboardNativeMethods
{
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);
}
