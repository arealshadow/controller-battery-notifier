using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ControllerBatteryNotifier.Services;

/// <summary>
/// Draws the tray icon: a battery outline (green / amber / red fill depending on level)
/// with the percentage rendered inside it (optional).
/// </summary>
public static class IconRenderer
{
    private static readonly Color OutlineColor = Color.FromRgb(0x6E, 0x6E, 0x6E);
    private static readonly Color UnknownColor = Color.FromRgb(0x9E, 0x9E, 0x9E);

    public static Brush ColorForLevel(int level) => new SolidColorBrush(level switch
    {
        >= 50 => Color.FromRgb(0x3F, 0xB9, 0x50),   // green
        >= 25 => Color.FromRgb(0xFB, 0xC0, 0x2D),   // amber
        _ => Color.FromRgb(0xE5, 0x39, 0x35),       // red
    });

    /// <param name="level">0-100, or null for "no device / unknown".</param>
    /// <param name="showPercent">Whether to draw the percentage inside the battery.</param>
    public static BitmapSource Render(int? level, bool showPercent)
    {
        const int size = 256;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Thick outline so the shape still reads at 16x16 tray size
            // (24/256 ≈ 1.5 device pixels at 16 px).
            var outline = new Pen(new SolidColorBrush(OutlineColor), 24);
            outline.Freeze();

            // Battery body — deliberately large: fills most of the icon.
            var body = new RectangleGeometry(new Rect(14, 58, 204, 140), 26, 26);
            dc.DrawGeometry(Brushes.Transparent, outline, body);

            // Terminal nub (centered on the body's vertical midpoint).
            dc.DrawRectangle(new SolidColorBrush(OutlineColor), null, new Rect(222, 98, 20, 60));

            bool hasLevel = level is >= 0 and <= 100;

            if (hasLevel)
            {
                var pct = (int)level!;
                var fillWidth = Math.Max(20, 168 * pct / 100.0);
                var fill = new RectangleGeometry(new Rect(32, 76, fillWidth, 104), 14, 14);
                dc.DrawGeometry(ColorForLevel(pct), null, fill);

                if (showPercent)
                    DrawText(dc, size, pct.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                // Unknown state: dim battery + question mark.
                if (showPercent)
                    DrawText(dc, size, "?", new SolidColorBrush(UnknownColor));
            }
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawText(DrawingContext dc, int canvasSize, string text,
        SolidColorBrush? color = null)
    {
        color ??= Brushes.White;

        var typeface = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        // Offscreen render at a fixed 96 DPI — no visual to ask about DPI.
        const double pixelsPerDip = 1.0;

        // Big, bold digits (148/256 ≈ 9.25 px at a 16 px tray size);
        // shrink slightly for 3-digit values so "100" still fits the body.
        var fontSize = text.Length > 2 ? 112 : 148;

        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, fontSize, color, pixelsPerDip);

        var center = new Point(canvasSize / 2.0, 128);
        var origin = new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2);

        var geometry = ft.BuildGeometry(origin);

        // Halo behind the text so it stays readable over any fill / tray theme.
        // Thick + near-opaque: at 16 px this is the difference between readable and muddy.
        var haloPen = new Pen(new SolidColorBrush(Color.FromArgb(0xF0, 0x10, 0x16, 0x20)), 24);
        haloPen.Freeze();
        dc.DrawGeometry(Brushes.Transparent, haloPen, geometry);
        dc.DrawGeometry(color, null, geometry);
    }
}