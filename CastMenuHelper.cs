using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sharpcaster.Models;

namespace DeathFmTray;

/// <summary>
/// Builds and shows the "Cast to" popup menu, shared between the tray icon's
/// context menu and the player window's titlebar system menu.
///
/// This is a standalone ContextMenuStrip rather than a static submenu of
/// either host menu: WinForms mispositions a ToolStripDropDown (it can jump
/// to the top-left corner of the screen) if its Items collection is mutated
/// while it's already open, which device discovery - taking a couple of
/// seconds - would require. Discovering first and only showing the menu once
/// it's fully built sidesteps that entirely.
/// </summary>
public static class CastMenuHelper
{
    public static async Task ShowAsync(CastService castService, Point screenLocation)
    {
        Cursor? previousCursor = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;

        IReadOnlyList<ChromecastReceiver> receivers;
        try
        {
            receivers = await castService.DiscoverAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            receivers = Array.Empty<ChromecastReceiver>();
        }
        finally
        {
            Cursor.Current = previousCursor;
        }

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable()),
            ForeColor = Color.WhiteSmoke,
            BackColor = DarkMenuColorTable.BackgroundColor,
            ShowImageMargin = false
        };

        if (castService.State != CastState.Idle)
        {
            var stopItem = new ToolStripMenuItem($"Stop casting ({castService.CastingDeviceName})");
            stopItem.Click += (_, _) => _ = castService.StopCastingAsync();
            menu.Items.Add(stopItem);
            menu.Items.Add(new ToolStripSeparator());
        }

        if (receivers.Count == 0)
        {
            menu.Items.Add("No devices found").Enabled = false;
        }
        else
        {
            foreach (ChromecastReceiver receiver in receivers)
            {
                var deviceItem = new ToolStripMenuItem(receiver.Name)
                {
                    Checked = castService.State != CastState.Idle && castService.CastingDeviceName == receiver.Name
                };
                deviceItem.Click += (_, _) => _ = CastToAsync(castService, receiver);
                menu.Items.Add(deviceItem);
            }
        }

        // ContextMenuStrip's usual auto-close-on-outside-click relies on a
        // global mouse hook that doesn't reliably see clicks landing on
        // WebView2's embedded Chromium child window, so it stays open
        // regardless of where you click. An explicit message filter closes it
        // on any click outside its bounds regardless of which control got it.
        OutsideClickCloser? closer = null;
        closer = new OutsideClickCloser(menu);
        Application.AddMessageFilter(closer);
        menu.Closed += (_, _) => Application.RemoveMessageFilter(closer);

        menu.Show(screenLocation);
    }

    private sealed class OutsideClickCloser(ContextMenuStrip menu) : IMessageFilter
    {
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONDOWN = 0x00A4;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_NCLBUTTONDOWN or WM_NCRBUTTONDOWN
                && !menu.Bounds.Contains(Cursor.Position))
            {
                menu.Close();
            }
            return false;
        }
    }

    /// <summary>Renders the popup with the same near-black/red palette as the rest of the app.</summary>
    private sealed class DarkMenuColorTable : ProfessionalColorTable
    {
        public static readonly Color BackgroundColor = Color.FromArgb(0x1a, 0x1a, 0x1a);
        private static readonly Color BorderColor = Color.FromArgb(0x40, 0x00, 0x00);
        private static readonly Color AccentColor = Color.FromArgb(0x8b, 0x00, 0x00);

        public override Color ToolStripDropDownBackground => BackgroundColor;
        public override Color ImageMarginGradientBegin => BackgroundColor;
        public override Color ImageMarginGradientMiddle => BackgroundColor;
        public override Color ImageMarginGradientEnd => BackgroundColor;
        public override Color MenuBorder => BorderColor;
        public override Color MenuItemBorder => AccentColor;
        public override Color MenuItemSelected => AccentColor;
        public override Color MenuItemSelectedGradientBegin => AccentColor;
        public override Color MenuItemSelectedGradientEnd => AccentColor;
        public override Color MenuItemPressedGradientBegin => AccentColor;
        public override Color MenuItemPressedGradientEnd => AccentColor;
        public override Color SeparatorDark => Color.FromArgb(0x3a, 0x3a, 0x3a);
        public override Color SeparatorLight => Color.FromArgb(0x3a, 0x3a, 0x3a);
    }

    private static async Task CastToAsync(CastService castService, ChromecastReceiver receiver)
    {
        try
        {
            await castService.CastToAsync(receiver);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't cast to {receiver.Name}: {ex.Message}", "Death.FM Player",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
