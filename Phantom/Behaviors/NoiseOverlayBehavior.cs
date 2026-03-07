using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Phantom.Behaviors;

/// <summary>
/// Generates a tiling film-grain noise texture and applies it as the Fill
/// of the attached Rectangle. Creates a cinematic noise overlay effect.
/// </summary>
public static class NoiseOverlayBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(NoiseOverlayBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Rectangle rect) return;

        if ((bool)e.NewValue)
            rect.Loaded += OnLoaded;
        else
            rect.Loaded -= OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Rectangle rect) return;
        rect.Loaded -= OnLoaded;

        const int size = 256;
        var pixels = new byte[size * size];
        Random.Shared.NextBytes(pixels);

        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Gray8, null);
        bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size, 0);
        bmp.Freeze();

        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();

        rect.Fill = brush;
    }
}
