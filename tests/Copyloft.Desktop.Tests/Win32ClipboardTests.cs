using System.Runtime.Versioning;
using Copyloft.Desktop.Platform;
using Copyloft.Desktop.Platform.Windows;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Copyloft.Desktop.Tests;

// Uses the real clipboard of the machine running the tests.
public sealed class Win32ClipboardTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void An_image_written_as_a_dib_reads_back_with_its_size()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 clipboard");

        using var source = new SKBitmap(4, 3);
        source.Erase(SKColors.Green);
        var png = source.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        Assert.True(Win32Clipboard.TryWriteImage(ImageClipCodec.ToDib(png)!));

        using var back = SKBitmap.Decode(ImageClipCodec.ToPng(Win32Clipboard.TryReadImage()!)!);
        Assert.Equal((4, 3), (back.Width, back.Height));
    }

    // A BI_BITFIELDS block carries 12 mask bytes after its header; the bounded read must keep
    // them or the codec gets a block that is short by exactly that much.
    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_bitfields_block_is_read_whole()
    {
        var dib = new byte[40 + 12 + 2 * 2 * 4];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), 2);
        BitConverter.TryWriteBytes(dib.AsSpan(8), 2);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)32);
        BitConverter.TryWriteBytes(dib.AsSpan(16), 3);
        var memory = Marshal.AllocHGlobal(dib.Length + 64);       // the allocation can be larger
        try
        {
            Marshal.Copy(dib, 0, memory, dib.Length);

            Assert.Equal(dib.Length, Win32Clipboard.ReadBounded(memory, (ulong)dib.Length + 64)!.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    // DIB rows are padded to 4 bytes: a 3-pixel 24 bpp row takes 12 bytes, not 9.
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Padded_rows_are_read_whole()
    {
        var dib = ImageClipCodecTests.SampleDib(3, 2);              // 40 + 2 rows * 12 bytes
        var memory = Marshal.AllocHGlobal(dib.Length);
        try
        {
            Marshal.Copy(dib, 0, memory, dib.Length);

            Assert.Equal(dib.Length, Win32Clipboard.ReadBounded(memory, (ulong)dib.Length)!.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    // One committed page followed by a reserved one: a block whose size claim runs past the
    // mapping, as another app's rendered handle can.
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Read_stops_at_mapping()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 memory");
        var block = NativeMethods.VirtualAlloc(IntPtr.Zero, 8192, NativeMethods.MemReserve, NativeMethods.PageNoAccess);
        NativeMethods.VirtualAlloc(block, 4096, NativeMethods.MemCommit, NativeMethods.PageReadWrite);
        try
        {
            Marshal.Copy(Enumerable.Repeat((byte)'a', 4096).ToArray(), 0, block, 4096);   // "愡..." with no null

            Assert.Equal(4096UL, Win32Clipboard.ReadableBytes(block, 1 << 20));
            Assert.Equal(50UL, Win32Clipboard.ReadableBytes(block + 100, 50));
            Assert.Equal(2048, Win32Clipboard.ReadText(block, Win32Clipboard.ReadableBytes(block, 1 << 20))!.Length);
        }
        finally
        {
            NativeMethods.VirtualFree(block, 0, NativeMethods.MemRelease);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Text_ends_at_null()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 memory");
        var memory = Marshal.StringToHGlobalUni("hi\0junk");
        try
        {
            Assert.Equal("hi", Win32Clipboard.ReadText(memory, 14));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}
