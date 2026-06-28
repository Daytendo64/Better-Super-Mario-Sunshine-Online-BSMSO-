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
            var outlineBrush = new SolidColorBrush(colorsValid ? outline : Colors.Black);
            outlineBrush.Freeze();
            if (outlineOffset > 0)
            {
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
        var offset = fontSize * 0.11 + 0.35;
        return (int)Math.Clamp(Math.Round(offset), 1, 3);
    }

    private static IEnumerable<(double dx, double dy)> BuildOutlineOffsets(int offsetPx, double fontSize)
    {
        _ = fontSize;
        for (var dy = -offsetPx; dy <= offsetPx; dy++)
        {
            for (var dx = -offsetPx; dx <= offsetPx; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                var cheb = Math.Max(Math.Abs(dx), Math.Abs(dy));
                if (cheb < 1 || cheb > offsetPx)
                    continue;

                yield return (dx, dy);
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
