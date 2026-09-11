using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DeathFmTray;

/// <summary>
/// The app's real "root" - a NotifyIcon plus context menu that owns the
/// PlayerForm's lifetime. Using ApplicationContext (rather than a normal
/// Application.Run(form)) means closing/hiding the window never quits the app;
/// only "Exit" from the tray menu does.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly PlayerForm _playerForm;
    private readonly NotifyIcon _trayIcon;

    public TrayAppContext()
    {
        _settings = SettingsStore.Load();
        _playerForm = new PlayerForm(_settings);

        _trayIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Death.FM Player",
            ContextMenuStrip = BuildContextMenu(),
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowPlayer();

        if (ShouldStartMinimized())
        {
            // Stay tray-only until the user opens the player.
        }
        else
        {
            _playerForm.Show();
        }
    }

    private bool ShouldStartMinimized()
    {
        bool launchedMinimized = Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        return launchedMinimized || _settings.StartMinimizedToTray;
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            using Stream? stream = typeof(TrayAppContext).Assembly.GetManifestResourceStream("tray.ico");
            if (stream is null)
                return SystemIcons.Application;
            return new Icon(stream);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("Show Player", null, (_, _) => ShowPlayer())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        });

        menu.Items.Add(new ToolStripSeparator());

        var startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        startWithWindowsItem.Click += (_, _) =>
        {
            StartupManager.SetEnabled(startWithWindowsItem.Checked);
            _settings.StartWithWindows = startWithWindowsItem.Checked;
            SettingsStore.Save(_settings);
        };
        menu.Items.Add(startWithWindowsItem);

        var startMinimizedItem = new ToolStripMenuItem("Start Minimized to Tray")
        {
            Checked = _settings.StartMinimizedToTray,
            CheckOnClick = true
        };
        startMinimizedItem.Click += (_, _) =>
        {
            _settings.StartMinimizedToTray = startMinimizedItem.Checked;
            SettingsStore.Save(_settings);
        };
        menu.Items.Add(startMinimizedItem);

        var minimizeToTrayItem = new ToolStripMenuItem("Minimize to Tray on Close")
        {
            Checked = _settings.MinimizeToTrayOnClose,
            CheckOnClick = true
        };
        minimizeToTrayItem.Click += (_, _) =>
        {
            _settings.MinimizeToTrayOnClose = minimizeToTrayItem.Checked;
            SettingsStore.Save(_settings);
        };
        menu.Items.Add(minimizeToTrayItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        return menu;
    }

    private void ShowPlayer() => _playerForm.ShowAndActivate();

    private void ExitApplication()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _playerForm.ForceClose();
        ExitThread();
    }
}
