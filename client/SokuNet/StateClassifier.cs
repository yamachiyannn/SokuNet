namespace SokuNet;

internal enum GameSceneStatus
{
    Other,
    Battle,
    Watching,
    Practice,
    Unknown
}

internal readonly record struct GameSnapshot(
    uint SceneId,
    uint NextSceneId,
    byte MainMode,
    byte SubMode);

internal static class GameMemoryLayout
{
    // SokuLib/src/SokuAddresses.hpp (th123.exe virtual addresses)
    public const uint NextSceneId = 0x008A0040;
    public const uint SceneId = 0x008A0044;
    public const uint BattleSubMode = 0x00898688;
    public const uint BattleMode = 0x00898690;

    // SokuLib/src/Scenes.hpp
    public const uint SceneLogo = 0;
    public const uint SceneOpening = 1;
    public const uint SceneTitle = 2;
    public const uint SceneSelect = 3;
    public const uint SceneBattle = 5;
    public const uint SceneLoading = 6;
    public const uint SceneSelectServer = 8;
    public const uint SceneSelectClient = 9;
    public const uint SceneLoadingServer = 10;
    public const uint SceneLoadingClient = 11;
    public const uint SceneLoadingWatch = 12;
    public const uint SceneBattleServer = 13;
    public const uint SceneBattleClient = 14;
    public const uint SceneBattleWatch = 15;
    public const uint SceneSelectScenario = 16;
    public const uint SceneEnding = 20;

    // SokuLib/src/BattleMode.hpp
    public const byte ModeVsClient = 4;
    public const byte ModeVsServer = 5;
    public const byte ModeVsWatch = 6;
    public const byte ModePractice = 8;

    public const byte SubModePlaying1 = 0;
    public const byte SubModePlaying2 = 1;
    public const byte SubModeReplay = 2;
}

internal static class GameStateClassifier
{
    private static readonly HashSet<uint> OnlineBattleScenes =
    [
        GameMemoryLayout.SceneSelectServer,
        GameMemoryLayout.SceneSelectClient,
        GameMemoryLayout.SceneLoadingServer,
        GameMemoryLayout.SceneLoadingClient,
        GameMemoryLayout.SceneBattleServer,
        GameMemoryLayout.SceneBattleClient
    ];

    public static GameSceneStatus Classify(GameSnapshot snapshot)
    {
        // Watching has priority: Scene 12 is explicitly the watch-loading scene.
        if (snapshot.SceneId is GameMemoryLayout.SceneLoadingWatch or GameMemoryLayout.SceneBattleWatch)
            return GameSceneStatus.Watching;

        // Host/client role labels are intentionally ignored. Some reports describe
        // role-side SceneID swaps on rematches; all six IDs mean the same presence state.
        if (OnlineBattleScenes.Contains(snapshot.SceneId))
            return GameSceneStatus.Battle;

        // Practice uses the generic battle/loading scenes. Mode values alone are
        // insufficient because they remain set after returning to the title screen.
        bool isGenericBattleOrLoadingScene =
            snapshot.SceneId == GameMemoryLayout.SceneBattle ||
            snapshot.SceneId == GameMemoryLayout.SceneLoading;

        if (isGenericBattleOrLoadingScene &&
            snapshot.MainMode == GameMemoryLayout.ModePractice &&
            snapshot.SubMode == GameMemoryLayout.SubModePlaying1)
        {
            return GameSceneStatus.Practice;
        }

        // Unknown/unassigned IDs inside the known scene range are grouped as Other.
        // Values beyond the SokuLib scene range are treated as unreadable/unknown.
        if (snapshot.SceneId > GameMemoryLayout.SceneEnding)
            return GameSceneStatus.Unknown;

        // Replay (MainMode 3 / SubMode 2 in the observed trace) is intentionally Other.
        return GameSceneStatus.Other;
    }
}
