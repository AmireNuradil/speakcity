using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpeakCity;

/// <summary>
/// The native window's illustrations. WPF cannot decode WebP, so the web keeps
/// ui/assets/*.webp and the native window uses JPEG twins compiled into this
/// assembly as WPF resources (regenerate with build/native_images.py). Being inside
/// the DLL, they cannot go missing from an install folder, and no build stage has
/// to copy them.
/// </summary>
public static class NativeAssets
{
    public const string LucyPicture = "lucy.jpg";
    public const string CityPicture = "city.jpg";
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.Ordinal);

    /// <summary>Maps a catalog image name ("airport.webp") to its native twin ("airport.jpg").</summary>
    public static string ForScenario(string? webImage) =>
        string.IsNullOrWhiteSpace(webImage) ? LucyPicture : Path.GetFileNameWithoutExtension(webImage) + ".jpg";

    /// <summary>Decodes a packaged picture or throws; the self-test relies on the throw.</summary>
    public static BitmapSource Load(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        var uri = new Uri($"pack://application:,,,/SpeakCity;component/Assets/{name}", UriKind.Absolute);
        try
        {
            var resource = Application.GetResourceStream(uri)
                ?? throw new InvalidOperationException($"Native picture missing: {name}");
            using var stream = resource.Stream;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
                throw new InvalidOperationException($"Native picture is empty: {name}");
            Cache[name] = image;
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException)
        {
            throw new InvalidOperationException($"Native picture missing or unreadable: {name}");
        }
    }

    /// <summary>The window's variant: a missing picture degrades to a plain card, never to a crash.</summary>
    public static BitmapSource? TryLoad(string name)
    {
        try { return Load(name); }
        catch (InvalidOperationException)
        {
            AppStartup.Note("assets", "missing", name);
            return null;
        }
    }

    /// <summary>
    /// A brush that behaves like CSS object-fit: cover with an object-position, for a
    /// box of known size (avatars). focusX/focusY are 0..1, as the web's percentages.
    /// </summary>
    public static Brush? Cover(BitmapSource? image, double focusX, double focusY, double width, double height)
    {
        if (image is null) return null;
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
        brush.Viewbox = CoverRect(image, focusX, focusY, width, height);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Paints a picture as the host's background, cropped like object-fit: cover and
    /// re-cropped whenever the host is resized. Border backgrounds follow CornerRadius,
    /// which gives rounded pictures without a clip geometry.
    /// </summary>
    public static void PaintCover(Border host, BitmapSource? image, double focusX, double focusY)
    {
        if (host.Tag is not CoverState state)
        {
            state = new CoverState();
            host.Tag = state;
            host.SizeChanged += (_, e) => state.Fit(host, e.NewSize);
        }
        state.Image = image;
        state.FocusX = focusX;
        state.FocusY = focusY;
        state.Fit(host, new Size(double.IsNaN(host.Width) ? host.ActualWidth : host.Width,
            double.IsNaN(host.Height) ? host.ActualHeight : host.Height));
    }

    private static Rect CoverRect(BitmapSource image, double focusX, double focusY, double width, double height)
    {
        if (width <= 0 || height <= 0) return new Rect(0, 0, 1, 1);
        double boxAspect = width / height;
        double imageAspect = image.PixelWidth / (double)image.PixelHeight;
        double w = 1, h = 1;
        if (imageAspect > boxAspect) w = boxAspect / imageAspect;
        else h = imageAspect / boxAspect;
        return new Rect((1 - w) * focusX, (1 - h) * focusY, w, h);
    }

    private sealed class CoverState
    {
        public BitmapSource? Image;
        public double FocusX;
        public double FocusY;

        public void Fit(Border host, Size size)
        {
            if (Image is null) { host.ClearValue(Border.BackgroundProperty); return; }
            if (size.Width <= 0 || size.Height <= 0 || double.IsNaN(size.Width) || double.IsNaN(size.Height))
            {
                host.Background = new ImageBrush(Image) { Stretch = Stretch.UniformToFill };
                return;
            }
            host.Background = new ImageBrush(Image)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = CoverRect(Image, FocusX, FocusY, size.Width, size.Height)
            };
        }
    }
}
