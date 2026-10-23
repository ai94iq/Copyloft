using Copyloft.Desktop.Platform;
using SkiaSharp;

namespace Copyloft.Desktop.Tests;

public sealed class ImageClipCodecTests
{
    [Fact]
    public void A_dib_becomes_a_png() =>
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ImageClipCodec.ToPng(SampleDib(2, 2))!.Take(4));

    [Fact]
    public void The_thumbnail_fits_the_row()
    {
        var png = ImageClipCodec.ToPng(SampleDib(400, 200))!;

        using var thumbnail = SKBitmap.Decode(ImageClipCodec.Thumbnail(png)!);

        Assert.True(thumbnail.Width <= 240 && thumbnail.Height <= 160, $"{thumbnail.Width}x{thumbnail.Height}");
    }

    [Fact]
    public void A_small_image_is_not_upscaled_for_the_thumbnail()
    {
        using var thumbnail = SKBitmap.Decode(ImageClipCodec.Thumbnail(ImageClipCodec.ToPng(SampleDib(10, 6))!)!);

        Assert.Equal((10, 6), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public void A_png_round_trips_through_a_dib_with_its_pixels()
    {
        using var source = new SKBitmap(3, 2);
        source.Erase(SKColors.Red);
        source.SetPixel(0, 0, SKColors.Blue);           // top-left, catches a flipped row order
        var png = source.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        using var back = SKBitmap.Decode(ImageClipCodec.ToPng(ImageClipCodec.ToDib(png)!)!);

        Assert.Equal((3, 2), (back.Width, back.Height));
        Assert.Equal(SKColors.Blue, back.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, back.GetPixel(2, 1));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 40, 0, 0, 0, 1 })]          // header says 40 bytes, the block is cut short
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0 })]   // absurd header size
    public void A_corrupt_dib_is_skipped_not_thrown(byte[] dib) => Assert.Null(ImageClipCodec.ToPng(dib));

    // What Windows hands out for most screenshots: 32 bpp BI_BITFIELDS, three DWORD masks
    // between the header and the pixels.
    [Fact]
    public void A_bitfields_dib_keeps_its_pixels()
    {
        const int width = 2, height = 2;
        var dib = new byte[40 + 12 + width * height * 4];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), width);
        BitConverter.TryWriteBytes(dib.AsSpan(8), height);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)32);
        BitConverter.TryWriteBytes(dib.AsSpan(16), 3);                   // BI_BITFIELDS
        BitConverter.TryWriteBytes(dib.AsSpan(40), 0x00FF0000u);         // red mask
        BitConverter.TryWriteBytes(dib.AsSpan(44), 0x0000FF00u);         // green mask
        BitConverter.TryWriteBytes(dib.AsSpan(48), 0x000000FFu);         // blue mask
        for (var i = 52; i < dib.Length; i += 4)                         // BGRX: opaque orange
        {
            dib[i] = 0x00; dib[i + 1] = 0xA5; dib[i + 2] = 0xFF;
        }

        using var png = SKBitmap.Decode(ImageClipCodec.ToPng(dib)!);

        Assert.Equal(new SKColor(0xFF, 0xA5, 0x00), png.GetPixel(0, 0));
    }

    // 8 bpp: a palette sits between the header and the pixels.
    [Fact]
    public void A_palette_dib_keeps_its_pixels()
    {
        const int width = 4, height = 1;                                 // 4-byte rows, no padding
        var dib = new byte[40 + 256 * 4 + width * height];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), width);
        BitConverter.TryWriteBytes(dib.AsSpan(8), height);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)8);
        dib[40 + 4 * 7] = 0xFF;                                         // palette[7] = blue (BGRA)
        dib.AsSpan(40 + 1024).Fill(7);

        using var png = SKBitmap.Decode(ImageClipCodec.ToPng(dib)!);

        Assert.Equal(new SKColor(0x00, 0x00, 0xFF), png.GetPixel(3, 0));
    }

    [Fact]
    public void Garbage_is_not_a_png() => Assert.Null(ImageClipCodec.ToDib([1, 2, 3]));

    // A minimal 24-bpp DIB the decoder accepts.
    internal static byte[] SampleDib(int width, int height)
    {
        var rowSize = ((width * 3 + 3) / 4) * 4;
        var pixels = rowSize * height;
        var dib = new byte[40 + pixels];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), width);
        BitConverter.TryWriteBytes(dib.AsSpan(8), height);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)24);
        BitConverter.TryWriteBytes(dib.AsSpan(20), pixels);
        return dib;
    }

    // 9000 x 9000 at 1 bpp is a 10 MB block that would decode to 324 MB.
    [Fact]
    public void Huge_image_skipped()
    {
        const int side = 9000, stride = (side + 31) / 32 * 4;
        var dib = new byte[40 + 8 + stride * side];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), side);
        BitConverter.TryWriteBytes(dib.AsSpan(8), side);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)1);
        Assert.True((long)side * side > ImageClipCodec.MaxPixels);

        Assert.Null(ImageClipCodec.ToPng(dib));
    }
}
