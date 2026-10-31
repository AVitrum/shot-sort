namespace ScreenSorter.Core.Vision;

/// <summary>Pixel-level checks and conversions on screenshot files.</summary>
public interface IImageInspector
{
    /// <summary>Measures how uniform the image is.</summary>
    /// <param name="imagePath">A PNG or JPEG file.</param>
    /// <returns>Standard deviation of grayscale values (0–255) on a downscaled copy; near 0 for black or blank images.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="InvalidDataException">The file is not a decodable image.</exception>
    double MeasureStdDev(string imagePath);

    /// <summary>Downscales the image so its longest side is at most <paramref name="maxSide"/> and encodes it as JPEG.</summary>
    /// <param name="imagePath">A PNG or JPEG file.</param>
    /// <param name="maxSide">Maximum width or height in pixels.</param>
    /// <param name="jpegQuality">JPEG quality 1–100.</param>
    /// <returns>JPEG bytes.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="InvalidDataException">The file is not a decodable image.</exception>
    byte[] ToJpeg(string imagePath, int maxSide, int jpegQuality);
}
