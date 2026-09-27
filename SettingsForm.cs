using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeathFmTray;

/// <summary>
/// Editor for the settings that previously required hand-editing
/// %AppData%\DeathFmTray\settings.json. Last.fm/Discord API credentials are
/// now baked into the app itself (see AppCredentials) rather than something
/// each user has to register and paste in, so this dialog just exposes the
/// Last.fm Connect/Disconnect flow, which is still per-user.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly PlayerForm _playerForm;

    private readonly Label _lastFmStatusLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _lastFmConnectButton = new() { Width = 100, Anchor = AnchorStyles.Left };

    public SettingsForm(AppSettings settings, PlayerForm playerForm)
    {
        _settings = settings;
        _playerForm = playerForm;

        Text = "Death.FM Player Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 140);
        Padding = new Padding(16);

        HandleCreated += (_, _) => WindowChromeHelper.ApplyDarkTitleBar(
            this,
            captionColor: Color.FromArgb(0x22, 0x00, 0x00),
            textColor: Color.White);

        BuildLayout();

        UpdateLastFmStatus();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < layout.RowCount; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(SectionHeader("Last.fm scrobbling"), 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);

        _lastFmConnectButton.Click += OnLastFmConnectClicked;
        layout.Controls.Add(_lastFmConnectButton, 1, 1);

        layout.Controls.Add(_lastFmStatusLabel, 1, 2);

        Controls.Add(layout);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 0),
        };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var saveButton = new Button { Text = "Save", Width = 90 };
        saveButton.Click += OnSaveClicked;
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(saveButton);
        Controls.Add(buttonPanel);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private static Label SectionHeader(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Control.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 6),
    };

    private void UpdateLastFmStatus()
    {
        if (_playerForm.IsLastFmAuthorized)
        {
            _lastFmStatusLabel.Text = $"Connected as {_playerForm.LastFmUsername}";
            _lastFmConnectButton.Text = "Disconnect";
        }
        else
        {
            _lastFmStatusLabel.Text = "Not connected";
            _lastFmConnectButton.Text = "Connect...";
        }
    }

    private async void OnLastFmConnectClicked(object? sender, EventArgs e)
    {
        if (_playerForm.IsLastFmAuthorized)
        {
            _playerForm.DisconnectLastFm();
        }
        else
        {
            await _playerForm.ConnectLastFmAsync();
        }

        UpdateLastFmStatus();
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        SettingsStore.Save(_settings);

        DialogResult = DialogResult.OK;
        Close();
    }
}
