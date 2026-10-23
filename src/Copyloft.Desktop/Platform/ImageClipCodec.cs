using SkiaSharp;

namespace Copyloft.Desktop.Platform;

// Converts between the clipboard's DIB and the PNG the database stores, and builds the row
// thumbnail. Skia, so it runs on any thread and any OS. Bad input returns null: a copy the
// decoder cannot read is skipped, never fatal.
public static class ImageClipCodec
{
    private const int ThumbnailWidth = 240;
    private const int ThumbnailHeight = 160;
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;

    public static byte[]? ToPng(byte[] dib)
    {
        if (dib.Length < 12) return null;
        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 12 || headerSize > dib.Length) return null;

        try
        {
            using var bitmap = Decode(WrapDibAsBmp(dib, headerSize));
            return bitmap is null ? null : EncodePng(bitmap);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static byte[]? Thumbnail(byte[] png)
    {
        try
        {
            using var source = Decode(png);
            if (source is null) return null;

            var scale = Math.Min(1.0, Math.Min(
                (double)ThumbnailWidth / source.Width, (double)ThumbnailHeight / source.Height));
            var info = new SKImageInfo(
                Math.Max(1, (int)Math.Round(source.Width * scale)),
                Math.Max(1, (int)Math.Round(source.Height * scale)));
            using var scaled = source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
            return scaled is null ? null : EncodePng(scaled);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A 32-bpp bottom-up BI_RGB DIB, the format every Windows app reads from CF_DIB.
    public static byte[]? ToDib(byte[] png)
    {
        try
        {
            using var decoded = Decode(png);
            if (decoded is null) return null;

            using var bgra = new SKBitmap(new SKImageInfo(
                decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            if (!decoded.CopyTo(bgra, SKColorType.Bgra8888)) return null;

            var stride = bgra.Width * 4;
            var dib = new byte[InfoHeaderSize + stride * bgra.Height];
            BitConverter.TryWriteBytes(dib.AsSpan(0), InfoHeaderSize);
            BitConverter.TryWriteBytes(dib.AsSpan(4), bgra.Width);
            BitConverter.TryWriteBytes(dib.AsSpan(8), bgra.Height);       // positive: bottom-up
            BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);
            BitConverter.TryWriteBytes(dib.AsSpan(14), (short)32);
            BitConverter.TryWriteBytes(dib.AsSpan(20), stride * bgra.Height);

            var pixels = bgra.GetPixelSpan();
            for (var row = 0; row < bgra.Height; row++)
                pixels.Slice(row * stride, stride)
                    .CopyTo(dib.AsSpan(InfoHeaderSize + (bgra.Height - 1 - row) * stride));
            return dib;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 64 megapixels, about 256 MiB decoded: room for a screenshot across three 4K screens. A
    // small file can claim far more (a PNG bomb), so the size is read from the header first.
    internal const long MaxPixels = 64L * 1024 * 1024;

    private static SKBitmap? Decode(byte[] encoded)
    {
        using var stream = new SKMemoryStream(encoded);
        using var codec = SKCodec.Create(stream);
        if (codec is null || (long)codec.Info.Width * codec.Info.Height > MaxPixels) return null;

        return SKBitmap.Decode(codec);
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Where the pixels start inside the DIB: after the header, the colour masks a 40-byte header
    // keeps outside itself (BI_BITFIELDS, which most screenshots use), and the palette.
    internal static int PixelOffset(byte[] dib, int headerSize)
    {
        const int BiBitfields = 3, BiAlphaBitfields = 6;
        if (headerSize < 40) return headerSize + (BitConverter.ToInt16(dib, 10) <= 8 ? (1 << BitConverter.ToInt16(dib, 10)) * 3 : 0);

        var bitCount = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var colorsUsed = BitConverter.ToInt32(dib, 32);
        var masks = headerSize != 40 ? 0 : compression switch
        {
            BiBitfields => 12,
            BiAlphaBitfields => 16,
            _ => 0,
        };
        var paletteEntries = colorsUsed > 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
        return headerSize + masks + paletteEntries * 4;
    }

    // A 14-byte BITMAPFILEHEADER in front of the DIB makes it a .bmp the decoder accepts.
    private static byte[] WrapDibAsBmp(byte[] dib, int headerSize)
    {
        var file = new byte[FileHeaderSize + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), FileHeaderSize + PixelOffset(dib, headerSize));
        dib.CopyTo(file, FileHeaderSize);
        return file;
    }
}
