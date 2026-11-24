using System.Drawing;
using System.Windows.Forms;

namespace SokuNet;

internal sealed class NetworkSettingsForm : Form
{
    private readonly TextBox _displayName = new();
    private readonly TextBox _relayUrl = new();
    private readonly TextBox _roomCode = new();

    public string DisplayName => _displayName.Text.Trim();
    public string RelayUrl => string.IsNullOrWhiteSpace(_relayUrl.Text)
        ? AppSettings.DefaultRelayUrl
        : _relayUrl.Text.Trim().TrimEnd('/');
    public string RoomCode => _roomCode.Text.Trim();

    public NetworkSettingsForm(AppSettings settings)
    {
        Text = "SokuNet 通信設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 300);
        Font = new Font("Meiryo UI", 9.5f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 5,
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        _displayName.Text = settings.DisplayName;
        _relayUrl.Text = string.IsNullOrWhiteSpace(settings.RelayUrl)
            ? AppSettings.DefaultRelayUrl
            : settings.RelayUrl;
        _roomCode.Text = settings.RoomCode;

        AddField(layout, 0, "表示名", _displayName);
        AddField(layout, 1, "中継サーバーURL", _relayUrl);
        AddField(layout, 2, "グループ合言葉", _roomCode);

        var help = new Label
        {
            Text = "表示名: 自由に設定してください（例 yamachiyannn）\nURL: 指定がない限り標準のものを使用\n合言葉: @yamachiyannn が伝えたもの以外では動作しません",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };
        layout.Controls.Add(help, 0, 3);
        layout.SetColumnSpan(help, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = new Button { Text = "保存", Width = 90, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "キャンセル", Width = 90, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveAndClose();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddField(TableLayoutPanel layout, int row, string labelText, TextBox textBox)
    {
        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(3, 7, 3, 5);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(textBox, 1, row);
    }

    private void SaveAndClose()
    {
        if (DisplayName.Length > 24)
        {
            MessageBox.Show(this, "表示名は24文字以内で入力してください。", "入力確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (RelayUrl.Length > 0 &&
            (!Uri.TryCreate(RelayUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "wss")))
        {
            MessageBox.Show(this, "中継サーバーURLには https:// または wss:// から始まるURLを入力してください。", "入力確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (RoomCode.Length > 0)
        {
            bool safeCode = RoomCode.Length >= 16 && RoomCode.All(c =>
                (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c is '-' or '_');
            if (!safeCode)
            {
                MessageBox.Show(this, "グループ合言葉は16文字以上の半角英数字・ハイフン・アンダースコアで入力してください。", "入力確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }

}
