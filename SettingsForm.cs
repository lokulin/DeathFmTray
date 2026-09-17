using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeathFmTray;

/// <summary>
/// Editor for the settings that previously required hand-editing
/// %AppData%\DeathFmTray\settings.json: Last.fm and Discord API credentials.
/// Everything else (start with Windows, minimize to tray, etc.) already has
/// its own tray menu checkbox and doesn't need a dialog.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly PlayerForm _playerForm;

    private readonly TextBox _lastFmApiKeyBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _lastFmApiSecretBox = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _lastFmStatusLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _lastFmConnectButton = new() { Width = 100, Anchor = AnchorStyles.Left };

    private readonly TextBox _discordClientIdBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _discordImageKeyBox = new() { Dock = DockStyle.Fill };

    public SettingsForm(AppSettings settings, PlayerForm playerForm)
    {
        _settings = settings;
        _playerForm = playerForm;

        Text = "Death.FM Player Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 300);
        Padding = new Padding(16);

        HandleCreated += (_, _) => WindowChromeHelper.ApplyDarkTitleBar(
            this,
            captionColor: Color.FromArgb(0x22, 0x00, 0x00),
            textColor: Color.White);

        BuildLayout();

        _lastFmApiKeyBox.Text = _settings.LastFmApiKey ?? "";
        _lastFmApiSecretBox.Text = _settings.LastFmApiSecret ?? "";
        _discordClientIdBox.Text = _settings.DiscordClientId ?? "";
        _discordImageKeyBox.Text = _settings.DiscordDefaultImageKey ?? "";

        UpdateLastFmStatus();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < layout.RowCount; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(SectionHeader("Last.fm scrobbling"), 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);

        layout.Controls.Add(new Label { Text = "API key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_lastFmApiKeyBox, 1, 1);

        layout.Controls.Add(new Label { Text = "Shared secret:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_lastFmApiSecretBox, 1, 2);

        _lastFmConnectButton.Click += OnLastFmConnectClicked;
        layout.Controls.Add(_lastFmConnectButton, 1, 3);

        layout.Controls.Add(_lastFmStatusLabel, 1, 4);

        layout.Controls.Add(new Panel { Height = 12 }, 0, 5);

        layout.Controls.Add(SectionHeader("Discord Rich Presence"), 0, 6);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 6)!, 2);

        layout.Controls.Add(new Label { Text = "Client ID:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 7);
        layout.Controls.Add(_discordClientIdBox, 1, 7);

        layout.Controls.Add(new Label { Text = "Default image key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 8);
        layout.Controls.Add(_discordImageKeyBox, 1, 8);

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
            // Commit the key/secret immediately so Connect works right after
            // typing them in, without needing a separate Save step first.
            _settings.LastFmApiKey = _lastFmApiKeyBox.Text.Trim();
            _settings.LastFmApiSecret = _lastFmApiSecretBox.Text.Trim();
            await _playerForm.ConnectLastFmAsync();
        }

        UpdateLastFmStatus();
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        _settings.LastFmApiKey = _lastFmApiKeyBox.Text.Trim();
        _settings.LastFmApiSecret = _lastFmApiSecretBox.Text.Trim();
        _settings.DiscordClientId = _discordClientIdBox.Text.Trim();
        _settings.DiscordDefaultImageKey = _discordImageKeyBox.Text.Trim();
        SettingsStore.Save(_settings);

        _playerForm.RestartDiscordPresence();

        DialogResult = DialogResult.OK;
        Close();
    }
}
