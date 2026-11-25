using System.Net.Http;
using System.Drawing;
using System.Windows.Forms;

namespace SokuNet;

internal sealed class AdminPasswordDialog : Form
{
    private readonly TextBox _passwordBox = new();
    public string TakePassword()
    {
        string password = _passwordBox.Text;
        _passwordBox.Clear();
        return password;
    }

    public AdminPasswordDialog()
    {
        Text = "SokuNet 管理者認証";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(540, 278);
        Font = new Font("Meiryo UI", 9.5f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = "管理者認証に失敗すると、入力した文字列も暗号化して保存され、管理者用履歴で復元表示できます。\n認証に成功した入力は保存しません。\n送信前に確認ウィンドウを2回表示します。\n入力可能：1～64文字（英数字・記号・空白・日本語など）。空欄のみ入力できません。\n他サービスのパスワードなど、SokuNet管理者用以外の秘密は入力しないでください。\n試行回数の目安は10回程度です（強制制限はありません）。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _passwordBox.Dock = DockStyle.Fill;
        // Deliberately use a normal text box, as requested. The two confirmations
        // below show the exact value before it is sent.
        _passwordBox.UseSystemPasswordChar = false;
        _passwordBox.MaxLength = 64;
        layout.Controls.Add(_passwordBox, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var ok = new Button { Text = "認証", Width = 88, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "キャンセル", Width = 88, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (_passwordBox.Text.Length == 0)
            {
                MessageBox.Show(this, "1～64文字で入力してください。", "入力確認",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

internal sealed class AdminParticipantsForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();
    private readonly Button _refreshButton = new();
    private readonly Button _failuresButton = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<AdminParticipant>>> _reload;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AdminAuthFailure>>> _loadFailures;
    private bool _loading;

    public AdminParticipantsForm(
        IReadOnlyList<AdminParticipant> participants,
        Func<CancellationToken, Task<IReadOnlyList<AdminParticipant>>> reload,
        Func<CancellationToken, Task<IReadOnlyList<AdminAuthFailure>>> loadFailures)
    {
        _reload = reload;
        _loadFailures = loadFailures;
        Text = "SokuNet 管理者用 - 導入者一覧";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(680, 380);
        Size = new Size(850, 500);
        Font = new Font("Meiryo UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(_summary, 0, 0);
        _failuresButton.Text = "認証失敗履歴";
        _failuresButton.Dock = DockStyle.Fill;
        _failuresButton.Click += async (_, _) => await ShowAuthFailuresAsync();
        header.Controls.Add(_failuresButton, 1, 0);
        _refreshButton.Text = "更新";
        _refreshButton.Dock = DockStyle.Fill;
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        header.Controls.Add(_refreshButton, 2, 0);
        root.Controls.Add(header, 0, 0);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "displayName", HeaderText = "表示名", FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "firstConnectedAt", HeaderText = "初回接続", FillWeight = 27 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "lastConnectedAt", HeaderText = "最終接続", FillWeight = 27 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "接続状態", FillWeight = 15 });
        root.Controls.Add(_grid, 0, 1);

        var footer = new Label
        {
            Dock = DockStyle.Fill,
            Text = "表示名・接続日時はサーバーに保存された記録です。接続状態は現在のWebSocket接続から判定します。",
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        LoadParticipants(participants);
    }

    private void LoadParticipants(IReadOnlyList<AdminParticipant> participants)
    {
        _grid.Rows.Clear();
        foreach (AdminParticipant participant in participants)
        {
            _grid.Rows.Add(
                participant.DisplayName,
                participant.FirstConnectedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
                participant.LastConnectedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
                participant.IsOnline ? "オンライン" : "オフライン");
        }

        int online = participants.Count(participant => participant.IsOnline);
        _summary.Text = $"導入者 {participants.Count} 人　／　オンライン {online} 人";
    }

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        _refreshButton.Enabled = false;
        _summary.Text = "更新中...";
        try
        {
            IReadOnlyList<AdminParticipant> participants = await _reload(CancellationToken.None);
            LoadParticipants(participants);
        }
        catch (AdminAuthenticationException)
        {
            MessageBox.Show(this, "管理者用パスワードを確認できませんでした。ウィンドウを閉じて再認証してください。",
                "SokuNet 管理者用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (AdminNotConfiguredException)
        {
            MessageBox.Show(this, "Cloudflare WorkerにSOKUNET_ADMIN_SECRETが設定されていません。Deploy SokuNet relayを確認してください。",
                "SokuNet 管理者用", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (AdminEndpointUnavailableException)
        {
            MessageBox.Show(this, "管理者用APIが見つかりません。Workerのデプロイを確認してください。",
                "SokuNet 管理者用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, $"一覧を更新できませんでした。\n{exception.Message}",
                "SokuNet 管理者用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _loading = false;
            _refreshButton.Enabled = true;
        }
    }
    private async Task ShowAuthFailuresAsync()
    {
        _failuresButton.Enabled = false;
        try
        {
            IReadOnlyList<AdminAuthFailure> failures = await _loadFailures(CancellationToken.None);
            using var form = new AdminAuthFailuresForm(failures);
            form.ShowDialog(this);
        }
        catch (AdminAuthenticationException)
        {
            MessageBox.Show(this, "管理者認証の有効期限が切れました。ウィンドウを閉じて再認証してください。",
                "SokuNet 管理者用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, $"認証失敗履歴を取得できませんでした。\n{exception.Message}",
                "SokuNet 管理者用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _failuresButton.Enabled = true;
        }
    }
}


internal sealed class AdminAuthFailuresForm : Form
{
    public AdminAuthFailuresForm(IReadOnlyList<AdminAuthFailure> failures)
    {
        Text = "SokuNet 管理者用 - 認証失敗履歴";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(580, 340);
        Size = new Size(700, 420);
        Font = new Font("Meiryo UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "日時", FillWeight = 24 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "申告表示名", FillWeight = 20 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "入力内容（復号表示）", FillWeight = 42 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "24時間内の回数", FillWeight = 14 });
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        foreach (AdminAuthFailure failure in failures)
        {
            grid.Rows.Add(
                failure.AttemptedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
                failure.DisplayName,
                failure.Password,
                failure.AttemptNumber.ToString());
        }
        root.Controls.Add(grid, 0, 0);

        var footer = new Label
        {
            Dock = DockStyle.Fill,
            Text = $"直近の失敗 {failures.Count} 件を表示しています。入力内容はWorker Secret由来の鍵で暗号化して保存し、管理者認証後に復号しています。",
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(footer, 0, 1);
        Controls.Add(root);
    }
}
