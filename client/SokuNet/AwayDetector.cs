using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SokuNet;

internal sealed class KeyboardActivityTracker
{
    private const int VirtualKeyCount = 256;
    private const short KeyDownMask = unchecked((short)0x8000);
    private readonly bool[] _previousDown = new bool[VirtualKeyCount];

    public bool PollForNewKeyDown(
        bool gameForeground,
        bool commandWindowFocused,
        out bool sPressed,
        out bool pPressed)
    {
        bool gameKeyboardInput = false;
        sPressed = false;
        pPressed = false;

        // Only key-down edges are inspected. The pressed key and its contents are
        // never stored or transmitted; S/P are commands only while CMD is focused.
        for (int virtualKey = 1; virtualKey < VirtualKeyCount; virtualKey++)
        {
            if (virtualKey is 0x01 or 0x02 or 0x04 or 0x05 or 0x06 or 0xE7)
                continue;

            bool isDown = (KeyboardNativeMethods.GetAsyncKeyState(virtualKey) & KeyDownMask) != 0;
            bool pressed = isDown && !_previousDown[virtualKey];
            if (pressed)
            {
                if (gameForeground)
                    gameKeyboardInput = true;

                if (commandWindowFocused)
                {
                    switch (virtualKey)
                    {
                        case 0x53: sPressed = true; break;
                        case 0x50: pPressed = true; break;
                    }
                }
            }

            _previousDown[virtualKey] = isDown;
        }

        return gameKeyboardInput;
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

internal static partial class KeyboardNativeMethods
{
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);
}
