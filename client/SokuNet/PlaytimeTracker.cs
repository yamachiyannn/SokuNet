using System.Diagnostics;
using System.Text.Json;

namespace SokuNet;

internal readonly record struct PlaytimeSnapshot(TimeSpan GameRunning, TimeSpan Practice, TimeSpan Battle)
{
    public static PlaytimeSnapshot Empty => new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
}

/// <summary>Accumulates local-only game time using Stopwatch and persists it every ten minutes.</summary>
internal sealed class PlaytimeTracker
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(10);
    private readonly string _path;
    private long _gameTicks;
    private long _practiceTicks;
    private long _battleTicks;
    private long _lastTimestamp;
    private long _lastSaveTimestamp;
    private bool _hasSample;
    private bool _previouslyRunning;
    private GameSceneStatus _previousStatus;
    private bool _previouslyAway;

    public PlaytimeTracker(string appDirectory)
    {
        _path = Path.Combine(appDirectory, "playtime.json");
        Load();
        _lastTimestamp = Stopwatch.GetTimestamp();
        _lastSaveTimestamp = _lastTimestamp;
    }

    public PlaytimeSnapshot Update(bool gameRunning, GameSceneStatus status, bool isAway, long now)
    {
        if (_hasSample)
        {
            long elapsed = Math.Max(0, now - _lastTimestamp);
            if (_previouslyRunning)
            {
                _gameTicks = SaturatingAdd(_gameTicks, StopwatchTicksToTimeSpanTicks(elapsed));
                if (!_previouslyAway)
                {
                    if (_previousStatus == GameSceneStatus.Practice)
                        _practiceTicks = SaturatingAdd(_practiceTicks, StopwatchTicksToTimeSpanTicks(elapsed));
                    else if (_previousStatus == GameSceneStatus.Battle)
                        _battleTicks = SaturatingAdd(_battleTicks, StopwatchTicksToTimeSpanTicks(elapsed));
                }
            }
        }

        _hasSample = true;
        _lastTimestamp = now;
        _previouslyRunning = gameRunning;
        _previousStatus = status;
        _previouslyAway = isAway;

        if (now - _lastSaveTimestamp >= (long)(AutosaveInterval.TotalSeconds * Stopwatch.Frequency))
        {
            try { Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _lastSaveTimestamp = now;
        }

        return Snapshot;
    }

    public PlaytimeSnapshot Snapshot => new(
        TimeSpan.FromTicks(_gameTicks), TimeSpan.FromTicks(_practiceTicks), TimeSpan.FromTicks(_battleTicks));

    public void Save()
    {
        string temporaryPath = _path + ".tmp";
        string json = JsonSerializer.Serialize(new PlaytimeData
        {
            GameRunningTicks = _gameTicks,
            PracticeTicks = _practiceTicks,
            BattleTicks = _battleTicks
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            PlaytimeData? data = JsonSerializer.Deserialize<PlaytimeData>(File.ReadAllText(_path));
            if (data is null) return;
            _gameTicks = Math.Max(0, data.GameRunningTicks);
            _practiceTicks = Math.Max(0, data.PracticeTicks);
            _battleTicks = Math.Max(0, data.BattleTicks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _gameTicks = _practiceTicks = _battleTicks = 0;
        }
    }

    private static long StopwatchTicksToTimeSpanTicks(long value) =>
        (long)((double)value * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

    private static long SaturatingAdd(long current, long amount) =>
        amount > long.MaxValue - current ? long.MaxValue : current + amount;

    private sealed class PlaytimeData
    {
        public long GameRunningTicks { get; set; }
        public long PracticeTicks { get; set; }
        public long BattleTicks { get; set; }
    }
}
