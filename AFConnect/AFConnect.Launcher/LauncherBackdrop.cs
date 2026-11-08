using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AFConnect.Launcher;

/// <summary>
/// Draws the authorization sequence's archive backdrop, stage cells, and scanning reticle. Owns decoration
/// only, with no authentication state.
/// </summary>
public sealed class LauncherBackdrop : Control
{
    private static readonly Pen Hair = new(new SolidColorBrush(Color.Parse("#290A0A0A")));
    private static readonly Pen SoftHair = new(new SolidColorBrush(Color.Parse("#100A0A0A")));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#FF7500"));
    private static readonly IBrush Pending = new SolidColorBrush(Color.Parse("#260A0A0A"));
    private double elapsed;

    /// <summary>The number of connection steps actually completed, updated by the window.</summary>
    public int Step { get; set; }

    /// <summary>Whether to display the verification-complete circular reticle.</summary>
    public bool ShowReticle { get; set; }

    /// <summary>Whether to display the scan line while connecting.</summary>
    public bool IsScanning { get; set; }

    /// <summary>
    /// Advances decorative animation without requesting redraws while the window is hidden, minimized, or
    /// in a static stage.
    /// </summary>
    public void Advance(double seconds)
    {
        elapsed = seconds;
        InvalidateVisual();
    }

    /// <summary>
    /// Draws the original corners, light archive panels, and stage markers in the window's shared design
    /// coordinates.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        DrawCorner(context, 26, 26, 1, 1);
        DrawCorner(context, Bounds.Width - 26, 26, -1, 1);
        DrawCorner(context, 26, Bounds.Height - 26, 1, -1);
        DrawCorner(context, Bounds.Width - 26, Bounds.Height - 26, -1, -1);

        for (int index = 0; index < 5; index++)
        {
            double x = Bounds.Width - 310 + index * 40;
            double y = 146 + index * 24;
            var panel = new Rect(x, y, 168 - index * 14, 228 - index * 12);

            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(9, 255, 255, 255)), SoftHair, panel);
        }

        for (int index = 0; index < 6; index++)
        {
            IBrush brush = index < Step ? Brushes.Black : Pending;

            context.DrawRectangle(index == Step && Step < 6 ? Accent : brush, null,
                new Rect(46 + index * 21, 184, index == Step && Step < 6 ? 22 : 13, 4));
        }

        for (int index = 0; index < 12; index++)
            context.DrawLine(Hair, new Point((Bounds.Width - 346) / 2 + index * 30, Bounds.Height - 22),
                new Point((Bounds.Width - 346) / 2 + index * 30, Bounds.Height - 17));

        if (IsScanning)
        {
            double scanY = 194 + elapsed % 2.6 / 2.6 * 382;

            context.DrawLine(SoftHair, new Point(27, scanY), new Point(Bounds.Width - 27, scanY));
        }

        if (ShowReticle)
        {
            var center = new Point(Bounds.Width / 2, 317);

            DrawArc(context, center, 270, elapsed * 40, new Pen(Brushes.Black, 5));
            DrawArc(context, center, 234, -elapsed * 67 + 32, new Pen(Brushes.White, 2.2));
            context.DrawEllipse(Accent, null, new Point(center.X, 52), 4, 4);
        }
    }

    private static void DrawCorner(DrawingContext context, double x, double y, int horizontal, int vertical)
    {
        context.DrawLine(Hair, new Point(x, y), new Point(x + horizontal * 16, y));
        context.DrawLine(Hair, new Point(x, y), new Point(x, y + vertical * 16));
    }

    private static void DrawArc(DrawingContext context, Point center, double radius, double rotation, Pen pen)
    {
        var geometry = new StreamGeometry();

        using (var path = geometry.Open())
        {
            for (int index = 0; index <= 96; index++)
            {
                double angle = (rotation + 120 + index * 300.0 / 96) * Math.PI / 180;
                var point = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);

                if (index == 0) path.BeginFigure(point, false);
                else path.LineTo(point);
            }

            path.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
