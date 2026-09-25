using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeathFmTray;

/// <summary>
/// A small floating "cast" icon overlaid in a corner of PlayerForm's WebView2
/// (the death.fm page itself has no such button, and there's no other free UI
/// real estate in this thin wrapper - see CastMenuHelper for why this needs to
/// be a fixed on-screen target rather than a menu item).
/// </summary>
public sealed class CastButton : Control
{
    private bool _isCasting;

    public CastButton()
    {
        // WebView2 is its own native window (not GDI-painted), so true
        // per-pixel transparency over it doesn't composite - a "transparent"
        // BackColor here just renders as an opaque solid instead. Paint a
        // real opaque backdrop matching the page's near-black chrome instead.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Black;
        Size = new Size(26, 26);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsCasting
    {
        get => _isCasting;
        set
        {
            if (_isCasting == value)
                return;
            _isCasting = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var backdropBrush = new SolidBrush(Color.Black))
            g.FillEllipse(backdropBrush, 0, 0, Width - 1, Height - 1);

        // #FF0000 matches the app's own red accent (album-info bar, transport
        // buttons); casting swaps to green so the active state still reads
        // clearly against all that red.
        Color iconColor = _isCasting ? Color.LimeGreen : Color.Red;
        using var pen = new Pen(iconColor, 1.6f);

        // Material Design "cast" glyph, scaled into a 24x24 box centered in the control:
        // a screen/monitor outline plus a wifi-style signal fanning from its
        // bottom-left corner.
        float scale = Math.Min(Width, Height) / 24f;
        float ox = (Width - 24 * scale) / 2f;
        float oy = (Height - 24 * scale) / 2f;
        RectangleF R(float x, float y, float w, float h) => new(ox + x * scale, oy + y * scale, w * scale, h * scale);

        g.DrawRectangle(pen, Rectangle.Round(R(1, 3, 20, 14)));

        // Concentric quarter-arcs radiating from the bottom-left corner of the screen.
        for (int i = 0; i < 3; i++)
        {
            float radius = 4 + i * 3.5f;
            RectangleF arcBounds = R(1 - radius, 17 - radius, radius * 2, radius * 2);
            g.DrawArc(pen, arcBounds, 270, 90);
        }

        using var dotBrush = new SolidBrush(iconColor);
        g.FillEllipse(dotBrush, R(1, 15.5f, 3, 3));
    }
}
