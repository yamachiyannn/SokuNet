namespace SokuNet;

internal sealed class ConsoleUI
{
    private int _previousLineCount;

    public void Render(
        GameMonitorResult game,
        bool gameForeground,
        bool isAway,
        AppSettings settings,
        bool settingsView,
        string? notice)
    {
        List<string> lines;

        if (settings.PrivateMode)
        {
            // Privacy mode intentionally renders only this single line.
            lines = ["情報を非公開にしています"];
        }
        else if (settingsView)
        {
            lines =
            [
                "SokuNet - 天則網 / 設定確認",
                "",
                $"情報公開: {(settings.PrivateMode ? "非公開" : "公開")}",
                $"離席判定: {settings.AwayTimeoutMinutes}分",
                $"設定ファイル: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\SokuNet\\config.json",
                "",
                "[S] 状態画面へ戻る    [P] 公開/非公開切替    [Q] 終了"
            ];
        }
        else
        {
            string processText = game.IsRunning ? "起動中" : "未起動";
            string activeText = game.IsRunning ? (gameForeground ? "前面" : "背面") : "-";
            string stateText = !game.IsRunning
                ? "-"
                : game.SnapshotAvailable
                    ? StatusText(game.SceneStatus)
                    : "取得不可";
            string awayText = !game.IsRunning ? "-" : (isAway ? "離席中" : "在席");

            lines =
            [
                "SokuNet - 天則網",
                "────────────────────────────────────────",
                "ローカル状態",
                $"SokuNet: 起動中",
                $"東方非想天則: {processText}",
                $"ゲームウィンドウ: {activeText}",
                $"ゲーム状態: {stateText}",
                $"離席状態: {awayText}",
                "情報公開: 公開中",
                "",
                "[Q] 終了    [P] 情報公開/非公開    [S] 設定確認",
                notice ?? string.Empty
            ];
        }

        WriteLines(lines);
    }

    private static string StatusText(GameSceneStatus status) => status switch
    {
        GameSceneStatus.Battle => "対戦中",
        GameSceneStatus.Watching => "観戦中",
        GameSceneStatus.Practice => "練習中",
        GameSceneStatus.Other => "その他",
        _ => "取得不可"
    };

    private void WriteLines(IReadOnlyList<string> lines)
    {
        try
        {
            Console.SetCursorPosition(0, 0);
            int lineCount = Math.Max(_previousLineCount, lines.Count);
            for (int i = 0; i < lineCount; i++)
            {
                string line = i < lines.Count ? lines[i] : string.Empty;
                int width = Math.Max(20, Console.WindowWidth - 1);
                if (line.Length > width)
                    line = line[..width];
                Console.Write(line.PadRight(width));
                Console.WriteLine();
            }
            _previousLineCount = lines.Count;
        }
        catch (IOException)
        {
            // A redirected or resized console should not terminate the monitor.
        }
        catch (ArgumentOutOfRangeException)
        {
            // The console buffer may be resized while rendering.
        }
    }
}
