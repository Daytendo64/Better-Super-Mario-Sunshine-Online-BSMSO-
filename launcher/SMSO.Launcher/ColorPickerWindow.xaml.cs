using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SMSO.Launcher;

public partial class ColorPickerWindow : Window
{
    private const int SatValSize = 220;
    private const int HueSize = 220;

    private static readonly Color[] BasicColors =
    {
        Color.FromRgb(255, 255, 255), Color.FromRgb(192, 192, 192), Color.FromRgb(128, 128, 128), Color.FromRgb(64, 64, 64), Color.FromRgb(0, 0, 0),
        Color.FromRgb(255, 0, 0), Color.FromRgb(255, 128, 0), Color.FromRgb(255, 255, 0), Color.FromRgb(128, 255, 0), Color.FromRgb(0, 255, 0),
        Color.FromRgb(0, 255, 128), Color.FromRgb(0, 255, 255), Color.FromRgb(0, 128, 255), Color.FromRgb(0, 0, 255), Color.FromRgb(128, 0, 255),
        Color.FromRgb(255, 0, 255), Color.FromRgb(255, 128, 192), Color.FromRgb(255, 64, 64), Color.FromRgb(255, 128, 64), Color.FromRgb(255, 255, 128),
        Color.FromRgb(128, 255, 128), Color.FromRgb(128, 255, 255), Color.FromRgb(128, 128, 255), Color.FromRgb(192, 128, 255), Color.FromRgb(255, 128, 255),
        Color.FromRgb(128, 0, 0), Color.FromRgb(128, 64, 0), Color.FromRgb(128, 128, 0), Color.FromRgb(64, 128, 0), Color.FromRgb(0, 128, 0),
        Color.FromRgb(0, 128, 64), Color.FromRgb(0, 128, 128), Color.FromRgb(0, 64, 128), Color.FromRgb(0, 0, 128), Color.FromRgb(64, 0, 128),
        Color.FromRgb(128, 0, 128), Color.FromRgb(128, 64, 64), Color.FromRgb(128, 96, 64), Color.FromRgb(128, 128, 64), Color.FromRgb(64, 128, 64),
    };

    private readonly WriteableBitmap _satValBitmap;
    private readonly WriteableBitmap _hueBitmap;
    private readonly Color _initialColor;

    private double _hue;
    private double _saturation = 1.0;
    private double _value = 1.0;
    private bool _syncing;
    private bool _draggingSatVal;
    private bool _draggingHue;

    public Color SelectedColor { get; private set; }

    public ColorPickerWindow(Color initialColor)
    {
        InitializeComponent();
        _initialColor = initialColor;
        SelectedColor = initialColor;

        _satValBitmap = new WriteableBitmap(SatValSize, SatValSize, 96, 96, PixelFormats.Bgr32, null);
        _hueBitmap = new WriteableBitmap(24, HueSize, 96, 96, PixelFormats.Bgr32, null);
        SatValImage.Source = _satValBitmap;
        HueImage.Source = _hueBitmap;

        CurrentColorSwatch.Background = new SolidColorBrush(_initialColor);
        BuildBasicColors();
        WireRgbBoxes();
        SetColorFromRgb(initialColor);
    }

    public static bool TryPick(Window owner, Color initialColor, out Color selectedColor)
    {
        var dialog = new ColorPickerWindow(initialColor) { Owner = owner };
        if (dialog.ShowDialog() == true)
        {
            selectedColor = dialog.SelectedColor;
            return true;
        }

        selectedColor = default;
        return false;
    }

    private void BuildBasicColors()
    {
        foreach (var color in BasicColors)
        {
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(3),
                BorderBrush = (Brush)FindResource("SmsBorder"),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(color),
                Cursor = Cursors.Hand,
                ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
            };
            swatch.MouseLeftButtonUp += (_, _) => SetColorFromRgb(color);
            BasicColorsPanel.Children.Add(swatch);
        }
    }

    private void WireRgbBoxes()
    {
        RedBox.TextChanged += (_, _) => { if (!_syncing) TryApplyFromBoxes(); };
        GreenBox.TextChanged += (_, _) => { if (!_syncing) TryApplyFromBoxes(); };
        BlueBox.TextChanged += (_, _) => { if (!_syncing) TryApplyFromBoxes(); };
    }

    private void SetColorFromRgb(Color color)
    {
        RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);
        SelectedColor = color;
        RefreshAll();
    }

    private void TryApplyFromBoxes()
    {
        if (!TryReadChannel(RedBox, out var r) ||
            !TryReadChannel(GreenBox, out var g) ||
            !TryReadChannel(BlueBox, out var b))
        {
            return;
        }

        SetColorFromRgb(Color.FromRgb(r, g, b));
    }

    private void RefreshAll()
    {
        _syncing = true;
        try
        {
            SelectedColor = HsvToRgb(_hue, _saturation, _value);
            NewColorSwatch.Background = new SolidColorBrush(SelectedColor);
            HexLabel.Text = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
            RedBox.Text = SelectedColor.R.ToString();
            GreenBox.Text = SelectedColor.G.ToString();
            BlueBox.Text = SelectedColor.B.ToString();
            RenderHueStrip();
            RenderSatValPlane();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RenderHueStrip()
    {
        _hueBitmap.Lock();
        try
        {
            var stride = _hueBitmap.BackBufferStride;
            var pixels = new byte[stride * HueSize];
            for (var y = 0; y < HueSize; y++)
            {
                var hue = y / (double)(HueSize - 1) * 360.0;
                var rgb = HsvToRgb(hue, 1.0, 1.0);
                for (var x = 0; x < 24; x++)
                {
                    var i = y * stride + x * 4;
                    pixels[i] = rgb.B;
                    pixels[i + 1] = rgb.G;
                    pixels[i + 2] = rgb.R;
                    pixels[i + 3] = 255;
                }
            }

            _hueBitmap.WritePixels(new Int32Rect(0, 0, 24, HueSize), pixels, stride, 0);
        }
        finally
        {
            _hueBitmap.Unlock();
        }
    }

    private void RenderSatValPlane()
    {
        _satValBitmap.Lock();
        try
        {
            var stride = _satValBitmap.BackBufferStride;
            var pixels = new byte[stride * SatValSize];
            for (var y = 0; y < SatValSize; y++)
            {
                var value = 1.0 - y / (double)(SatValSize - 1);
                for (var x = 0; x < SatValSize; x++)
                {
                    var saturation = x / (double)(SatValSize - 1);
                    var rgb = HsvToRgb(_hue, saturation, value);
                    var i = y * stride + x * 4;
                    pixels[i] = rgb.B;
                    pixels[i + 1] = rgb.G;
                    pixels[i + 2] = rgb.R;
                    pixels[i + 3] = 255;
                }
            }

            _satValBitmap.WritePixels(new Int32Rect(0, 0, SatValSize, SatValSize), pixels, stride, 0);
        }
        finally
        {
            _satValBitmap.Unlock();
        }
    }

    private void SatValImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSatVal = true;
        SatValImage.CaptureMouse();
        UpdateSatValFromMouse(e.GetPosition(SatValImage));
    }

    private void SatValImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingSatVal)
            return;
        UpdateSatValFromMouse(e.GetPosition(SatValImage));
    }

    private void HueImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        HueImage.CaptureMouse();
        UpdateHueFromMouse(e.GetPosition(HueImage));
    }

    private void HueImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingHue)
            return;
        UpdateHueFromMouse(e.GetPosition(HueImage));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_draggingSatVal)
        {
            _draggingSatVal = false;
            SatValImage.ReleaseMouseCapture();
        }

        if (_draggingHue)
        {
            _draggingHue = false;
            HueImage.ReleaseMouseCapture();
        }
    }

    private void UpdateSatValFromMouse(Point pos)
    {
        _saturation = Math.Clamp(pos.X / SatValSize, 0, 1);
        _value = Math.Clamp(1.0 - pos.Y / SatValSize, 0, 1);
        RefreshAll();
    }

    private void UpdateHueFromMouse(Point pos)
    {
        _hue = Math.Clamp(pos.Y / HueSize * 360.0, 0, 360);
        RefreshAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadChannel(RedBox, out var r) ||
            !TryReadChannel(GreenBox, out var g) ||
            !TryReadChannel(BlueBox, out var b))
        {
            MessageBox.Show(this, "Enter valid RGB values from 0 to 255.", "Invalid color",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedColor = Color.FromRgb(r, g, b);
        DialogResult = true;
    }

    private static bool TryReadChannel(TextBox box, out byte value)
    {
        value = 0;
        if (!int.TryParse(box.Text, out var parsed) || parsed < 0 || parsed > 255)
            return false;
        value = (byte)parsed;
        return true;
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
        {
            h = 0;
            return;
        }

        if (max == rf)
            h = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf)
            h = 60 * (((bf - rf) / delta) + 2);
        else
            h = 60 * (((rf - gf) / delta) + 4);

        if (h < 0)
            h += 360;
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        if (s <= 0)
        {
            var gray = (byte)Math.Round(v * 255);
            return Color.FromRgb(gray, gray, gray);
        }

        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        double r1, g1, b1;
        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        return Color.FromRgb(
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }
}
