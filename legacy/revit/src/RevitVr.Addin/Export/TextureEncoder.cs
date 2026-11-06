using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitVr.Addin.Export;

public sealed record EncodedTexture(byte[] Data, string Mime, int Width, int Height, bool Resized);

/// <summary>
/// Turns whatever Revit's appearance asset points at into bytes a GLB can carry.
///
/// Two things force this to exist rather than just copying the file. glTF allows only PNG and
/// JPEG, so a TiFF or BMP texture would make the scene invalid — it has to be transcoded. And a
/// Revit library is full of 4K scans: 20 of those is 400 MB through adb to a headset with 8 GB,
/// which is both slow to load and expensive in VRAM. Anything above <see cref="MaxDimension"/> is
/// scaled down once, here, on the PC.
///
/// Files that are already small PNG/JPEG are passed through byte for byte — no recompression, no
/// generation loss.
/// </summary>
public static class TextureEncoder
{
    /// <summary>Longest side a texture may keep. 2K is what a standalone headset wants.</summary>
    public const int MaxDimension = 2048;

    /// <summary>Raw files above this are re-encoded even if their dimensions are modest.</summary>
    public const long MaxBytes = 4_000_000;

    private const int JpegQuality = 88;

    /// <summary>Reads and prepares an image file. Returns null when it cannot be used.</summary>
    public static EncodedTexture? Prepare(string path)
    {
        byte[] raw;
        try
        {
            if (!File.Exists(path)) return null;
            raw = File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }

        return Prepare(raw, Path.GetFileName(path));
    }

    /// <summary>Prepares already-loaded image bytes.</summary>
    public static EncodedTexture? Prepare(byte[] raw, string name)
    {
        if (raw is null || raw.Length == 0) return null;
        var mime = SniffMime(raw);

        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            // Header only: this reads the size without decoding the pixels.
            var probe = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var width = probe.PixelWidth;
            var height = probe.PixelHeight;
            if (width <= 0 || height <= 0) return null;

            var largest = Math.Max(width, height);
            var wantsResize = largest > MaxDimension;
            var passthrough = (mime == "image/jpeg" || mime == "image/png") && !wantsResize && raw.Length <= MaxBytes;
            if (passthrough) return new EncodedTexture(raw, mime!, width, height, false);

            // Now it is worth decoding the pixels.
            using var whole = new MemoryStream(raw, writable: false);
            BitmapSource source = BitmapFrame.Create(whole, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);

            var scale = wantsResize ? (double)MaxDimension / largest : 1.0;
            if (scale < 1.0)
            {
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                source = scaled;
            }

            // PNG keeps cut-outs (foliage, signage) transparent; JPEG is 10x smaller for photos.
            // "Is it a PNG" is not the question — "does it actually have transparency" is, and
            // most Revit PNG textures are fully opaque photographs stored in the wrong format.
            var keepAlpha = mime == "image/png" && HasTransparency(source);
            BitmapEncoder encoder = keepAlpha ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var output = new MemoryStream();
            encoder.Save(output);

            var bytes = output.ToArray();
            // Recompressing a small image that was already compressed can come out bigger, and
            // then the original is strictly better. This must not apply when the image was
            // resized: there the point is the pixel budget, not the file size.
            if (!wantsResize && (mime == "image/jpeg" || mime == "image/png") && raw.Length <= MaxBytes
                && bytes.Length >= raw.Length)
                return new EncodedTexture(raw, mime!, width, height, false);

            return new EncodedTexture(bytes, keepAlpha ? "image/png" : "image/jpeg",
                                      source.PixelWidth, source.PixelHeight, wantsResize);
        }
        catch
        {
            // Unknown or corrupt format. Keep a valid jpg/png as-is; anything else is unusable.
            if (mime is "image/jpeg" or "image/png")
                return new EncodedTexture(raw, mime, 0, 0, false);
            return null;
        }
    }

    /// <summary>
    /// True when the image has an alpha channel with at least one non-opaque pixel. Anything else
    /// can go out as JPEG, which for a photograph is a fraction of the size.
    /// </summary>
    private static bool HasTransparency(BitmapSource source)
    {
        try
        {
            if (source.Format.BitsPerPixel != 32 || source.Format.Masks.Count < 4) return false;

            var bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var stride = bgra.PixelWidth * 4;
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);

            for (var i = 3; i < pixels.Length; i += 4)
                if (pixels[i] != 255) return true;

            return false;
        }
        catch
        {
            // Cannot tell: keep PNG, which is always safe for transparency.
            return true;
        }
    }

    /// <summary>Identifies the image by its magic bytes, not by its extension.</summary>
    public static string? SniffMime(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 4 && b[0] == 'I' && b[1] == 'I' && b[2] == 0x2A && b[3] == 0x00) return "image/tiff";
        if (b.Length >= 4 && b[0] == 'M' && b[1] == 'M' && b[2] == 0x00 && b[3] == 0x2A) return "image/tiff";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') return "image/bmp";
        return null;
    }
}
