using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SokuNet;

internal sealed class GameMonitor : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;

    private IntPtr _processHandle;
    private int _processId;
    private bool _disposed;

    public bool IsCurrentProcessForeground =>
        _processId != 0 && NativeMethods.IsProcessForeground(_processId);

    public GameMonitorResult Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Process? target = FindTargetProcess();
        if (target is null)
        {
            CloseProcessHandle();
            _processId = 0;
            return GameMonitorResult.NotRunning;
        }

        int pid;
        IntPtr windowHandle;
        try
        {
            pid = target.Id;
            windowHandle = target.MainWindowHandle;
        }
        catch (InvalidOperationException)
        {
            return GameMonitorResult.NotRunning;
        }
        finally
        {
            target.Dispose();
        }

        if (pid != _processId || _processHandle == IntPtr.Zero)
        {
            CloseProcessHandle();
            _processId = pid;
            _processHandle = NativeMethods.OpenProcess(
                ProcessVmRead | ProcessQueryInformation,
                inheritHandle: false,
                pid);
        }

        if (_processHandle == IntPtr.Zero)
        {
            return new GameMonitorResult(
                IsRunning: true,
                ProcessId: _processId,
                SnapshotAvailable: false,
                Snapshot: default,
                ReadError: Marshal.GetLastWin32Error(),
                WindowHandle: windowHandle);
        }

        if (!TryReadSnapshot(_processHandle, out GameSnapshot snapshot, out int error))
        {
            return new GameMonitorResult(
                IsRunning: true,
                ProcessId: _processId,
                SnapshotAvailable: false,
                Snapshot: default,
                ReadError: error,
                WindowHandle: windowHandle);
        }

        return new GameMonitorResult(
            IsRunning: true,
            ProcessId: _processId,
            SnapshotAvailable: true,
            Snapshot: snapshot,
            ReadError: 0,
            WindowHandle: windowHandle);
    }

    private static Process? FindTargetProcess()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("th123");
        }
        catch
        {
            return null;
        }

        if (processes.Length == 0)
            return null;

        uint foregroundPid = NativeMethods.GetForegroundProcessId();
        Process selected = processes.FirstOrDefault(p => unchecked((uint)p.Id) == foregroundPid)
            ?? processes.OrderBy(p => p.Id).First();

        foreach (Process process in processes)
        {
            if (!ReferenceEquals(process, selected))
                process.Dispose();
        }

        return selected;
    }

    private static bool TryReadSnapshot(IntPtr processHandle, out GameSnapshot snapshot, out int error)
    {
        snapshot = default;
        error = 0;

        // Read the scene pair, then modes, then the current scene again. If the
        // current scene changed during the read, discard the mixed snapshot.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            byte[] sceneBlockBefore = new byte[8];
            byte[] modeBlock = new byte[9];
            byte[] sceneBlockAfter = new byte[8];

            if (!ReadMemory(processHandle, GameMemoryLayout.NextSceneId, sceneBlockBefore, out error) ||
                !ReadMemory(processHandle, GameMemoryLayout.BattleSubMode, modeBlock, out error) ||
                !ReadMemory(processHandle, GameMemoryLayout.NextSceneId, sceneBlockAfter, out error))
            {
                return false;
            }

            uint currentBefore = BitConverter.ToUInt32(sceneBlockBefore, 4);
            uint currentAfter = BitConverter.ToUInt32(sceneBlockAfter, 4);
            if (currentBefore != currentAfter)
                continue;

            uint nextScene = BitConverter.ToUInt32(sceneBlockAfter, 0);
            byte subMode = modeBlock[0];
            byte mainMode = modeBlock[8];

            if (currentAfter > GameMemoryLayout.SceneEnding ||
                mainMode > GameMemoryLayout.ModePractice ||
                subMode > GameMemoryLayout.SubModeReplay)
            {
                error = 0;
                return false;
            }

            snapshot = new GameSnapshot(currentAfter, nextScene, mainMode, subMode);
            return true;
        }

        // The game changed scenes during both attempts. Treat this sample as transient.
        error = 0;
        return false;
    }

    private static bool ReadMemory(IntPtr processHandle, uint address, byte[] buffer, out int error)
    {
        bool success = NativeMethods.ReadProcessMemory(
            processHandle,
            new IntPtr(unchecked((int)address)),
            buffer,
            new UIntPtr((uint)buffer.Length),
            out UIntPtr bytesRead);

        if (success && bytesRead.ToUInt64() == (ulong)buffer.Length)
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    private void CloseProcessHandle()
    {
        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        CloseProcessHandle();
        _processId = 0;
        _disposed = true;
    }
}

internal readonly record struct GameMonitorResult(
    bool IsRunning,
    int ProcessId,
    bool SnapshotAvailable,
    GameSnapshot Snapshot,
    int ReadError,
    IntPtr WindowHandle)
{
    public static GameMonitorResult NotRunning => new(
        IsRunning: false,
        ProcessId: 0,
        SnapshotAvailable: false,
        Snapshot: default,
        ReadError: 0,
        WindowHandle: IntPtr.Zero);

    public GameSceneStatus SceneStatus => SnapshotAvailable
        ? GameStateClassifier.Classify(Snapshot)
        : GameSceneStatus.Unknown;
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        UIntPtr size,
        out UIntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    internal static uint GetForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return 0;
        _ = GetWindowThreadProcessId(window, out uint processId);
        return processId;
    }

    internal static bool IsProcessForeground(int processId) =>
        GetForegroundProcessId() == unchecked((uint)processId);
}
