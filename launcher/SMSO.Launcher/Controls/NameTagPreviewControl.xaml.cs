using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SMSO.Launcher.Controls;

public partial class NameTagPreviewControl : UserControl
{
    private const double PreviewFontSize = 22.0;
    private const int MaxDisplayChars = 16;
    private const double CanvasPad = 4.0;

    public NameTagPreviewControl()
    {
        InitializeComponent();
    }

    public void UpdatePreview(string? username, Color textTop, Color textBottom, Color outline,
        bool gradientEnabled, bool colorsValid)
    {
        var display = GetDisplayName(username);

        if (string.IsNullOrEmpty(display))
        {
            PreviewImage.Source = null;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        PreviewImage.Source = RenderNameTag(display, textTop, textBottom, outline, gradientEnabled, colorsValid);
    }

    private BitmapSource RenderNameTag(string display, Color textTop, Color textBottom, Color outline,
        bool gradientEnabled, bool colorsValid)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold,
            FontStretches.Normal);

        var formatted = new FormattedText(
            display,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            PreviewFontSize,
            Brushes.White,
            dpi.PixelsPerDip);

        var origin = new Point(CanvasPad, CanvasPad);
        var geometry = formatted.BuildGeometry(origin);
        geometry.Freeze();

        var outlineOffset = CalcOutlineOffset(PreviewFontSize);
        var bounds = geometry.Bounds;
        var extra = outlineOffset + 2;
        var width = (int)Math.Ceiling(bounds.Width + CanvasPad * 2 + extra * 2);
        var height = (int)Math.Ceiling(bounds.Height + CanvasPad * 2 + extra * 2);
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (colorsValid && outlineOffset > 0)
            {
                var outlineBrush = new SolidColorBrush(outline);
                outlineBrush.Freeze();
                foreach (var (dx, dy) in BuildOutlineOffsets(outlineOffset, PreviewFontSize))
                {
                    dc.PushTransform(new TranslateTransform(dx, dy));
                    dc.DrawGeometry(outlineBrush, null, geometry);
                    dc.Pop();
                }
            }

            Brush fill;
            if (!colorsValid)
            {
                fill = Brushes.White;
            }
            else if (gradientEnabled)
            {
                var gradient = new LinearGradientBrush(textTop, textBottom, new Point(0.5, 0), new Point(0.5, 1));
                gradient.Freeze();
                fill = gradient;
            }
            else
            {
                var solid = new SolidColorBrush(textTop);
                solid.Freeze();
                fill = solid;
            }

            dc.DrawGeometry(fill, null, geometry);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96.0 * dpi.DpiScaleX, 96.0 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static int CalcOutlineOffset(double fontSize)
    {
        var offset = (int)(fontSize / 11.0 + 0.35);
        return Math.Clamp(offset, 1, 2);
    }

    private static IEnumerable<(double dx, double dy)> BuildOutlineOffsets(int offsetPx, double fontSize)
    {
        var useDiagonals = fontSize >= 12.0;
        for (var layer = 1; layer <= offsetPx; layer++)
        {
            for (var dy = -layer; dy <= layer; dy++)
            {
                for (var dx = -layer; dx <= layer; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    var cheb = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (cheb != layer)
                        continue;

                    if (!useDiagonals && dx != 0 && dy != 0)
                        continue;

                    yield return (dx, dy);
                }
            }
        }
    }

    private static string GetDisplayName(string? username)
    {
        var trimmed = (username ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        return trimmed.Length <= MaxDisplayChars ? trimmed : trimmed[..MaxDisplayChars];
    }
}
