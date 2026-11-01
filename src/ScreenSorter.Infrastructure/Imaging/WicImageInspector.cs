using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenSorter.Core.Vision;

namespace ScreenSorter.Infrastructure.Imaging;

/// <summary>
/// <see cref="IImageInspector"/> on WPF imaging (Windows Imaging Component).
/// Thread-safe: every call decodes into its own frozen bitmap.
/// </summary>
public sealed class WicImageInspector : IImageInspector
{
    // Small enough to be fast, large enough that sparse text on a white page still shows variance.
    private const int StatsWidth = 256;

    /// <inheritdoc />
    public double MeasureStdDev(string imagePath)
    {
        var gray = new FormatConvertedBitmap(Decode(imagePath, StatsWidth, 0), PixelFormats.Gray8, null, 0);
        gray.Freeze();

        var stride = gray.PixelWidth;
        var pixels = new byte[stride * gray.PixelHeight];
        gray.CopyPixels(pixels, stride, 0);

        double sum = 0, sumSq = 0;
        foreach (var p in pixels)
        {
            sum += p;
            sumSq += p * p;
        }
        var mean = sum / pixels.Length;
        return Math.Sqrt(Math.Max(0, sumSq / pixels.Length - mean * mean));
    }

    /// <inheritdoc />
    public byte[] ToJpeg(string imagePath, int maxSide, int jpegQuality)
    {
        var size = ReadSize(imagePath);
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(size.Width, size.Height));
        // Decode-time scaling keeps memory low for 4K screenshots; one dimension keeps the aspect ratio.
        var bitmap = size.Width >= size.Height
            ? Decode(imagePath, (int)Math.Round(size.Width * scale), 0)
            : Decode(imagePath, 0, (int)Math.Round(size.Height * scale));

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public byte[] ToCenterCropRgb(string imagePath, int size)
    {
        var original = ReadSize(imagePath);
        // Scaling one dimension keeps the aspect ratio. WIC's scaler differs from CLIP's bicubic, which is fine
        // as long as stored examples and queries use the same path.
        var scaled = original.Width >= original.Height ? Decode(imagePath, 0, size) : Decode(imagePath, size, 0);
        var rgb = new FormatConvertedBitmap(scaled, PixelFormats.Rgb24, null, 0);
        rgb.Freeze();

        var x = Math.Max(0, (rgb.PixelWidth - size) / 2);
        var y = Math.Max(0, (rgb.PixelHeight - size) / 2);
        var w = Math.Min(size, rgb.PixelWidth - x);
        var h = Math.Min(size, rgb.PixelHeight - y);
        var cropped = new CroppedBitmap(rgb, new System.Windows.Int32Rect(x, y, w, h));
        cropped.Freeze();

        // Rounding can leave the scaled short side a pixel under size; unset pixels stay black.
        var pixels = new byte[size * size * 3];
        cropped.CopyPixels(pixels, size * 3, 0);
        return pixels;
    }

    private static (int Width, int Height) ReadSize(string path)
    {
        return Wrap(path, () =>
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        });
    }

    private static BitmapSource Decode(string path, int width, int height)
    {
        return Wrap(path, () =>
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            if (width > 0)
                image.DecodePixelWidth = width;
            if (height > 0)
                image.DecodePixelHeight = height;
            image.EndInit();
            image.Freeze();
            return (BitmapSource)image;
        });
    }

    // WIC reports undecodable data as NotSupportedException/FileFormatException/COMException;
    // the contract promises InvalidDataException for those and IOException only for read failures.
    private static T Wrap<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (FileFormatException ex)
        {
            throw new InvalidDataException($"Cannot decode {path}", ex);
        }
        catch (Exception ex) when (ex is NotSupportedException or COMException or ArgumentException)
        {
            throw new InvalidDataException($"Cannot decode {path}", ex);
        }
    }
}
