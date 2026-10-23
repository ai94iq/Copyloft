using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Copyloft.Desktop.Platform;

namespace Copyloft.Desktop.Platform.Windows;

// Reads and writes plain-text clipboard content through the Win32 API. Another app can hold the
// clipboard open briefly, so opening is retried a few times before giving up. Reads run off the
// UI thread because the retries can sleep.
[SupportedOSPlatform("windows")]
internal static class Win32Clipboard
{
    private const int Retries = 5;
    private const int RetryDelayMs = 10;

    public static string? TryReadText()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var handle = NativeMethods.GetClipboardData(NativeMethods.ClipboardFormatUnicodeText);
                    if (handle == IntPtr.Zero) return null;

                    var allocation = (ulong)NativeMethods.GlobalSize(handle);
                    var pointer = NativeMethods.GlobalLock(handle);
                    if (pointer == IntPtr.Zero) return null;
                    try
                    {
                        return ReadText(pointer, ReadableBytes(pointer, Math.Min(allocation, MaxClipBytes)));
                    }
                    finally
                    {
                        NativeMethods.GlobalUnlock(handle);
                    }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            if (attempt >= Retries) return null;
            Thread.Sleep(RetryDelayMs);
        }
    }

    // True when the copying app asked clipboard tools not to record this copy, as password
    // managers do: ExcludeClipboardContentFromMonitorProcessing or "Clipboard Viewer Ignore" by
    // being there, CanIncludeInClipboardHistory by holding a DWORD 0.
    public static bool IsExcluded()
    {
        var exclude = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        var ignore = NativeMethods.RegisterClipboardFormat("Clipboard Viewer Ignore");
        var history = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");

        for (var attempt = 0; ; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (Available(exclude) || Available(ignore)) return true;
                    return Available(history) && ReadDword(history) == 0;
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            if (attempt >= Retries) return false;
            Thread.Sleep(RetryDelayMs);
        }
    }

    private static bool Available(uint format) => format != 0 && NativeMethods.IsClipboardFormatAvailable(format);

    private static int? ReadDword(uint format)
    {
        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            return ReadableBytes(pointer, sizeof(int)) == sizeof(int) ? Marshal.ReadInt32(pointer) : null;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    // True when the clipboard holds an image (CF_DIB, which Windows also synthesizes from CF_DIBV5).
    public static bool HasImage()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    return NativeMethods.IsClipboardFormatAvailable(NativeMethods.ClipboardFormatDib);
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            if (attempt >= Retries) return false;
            Thread.Sleep(RetryDelayMs);
        }
    }

    // The raw CF_DIB bytes; the codec turns them into a PNG.
    public static byte[]? TryReadImage()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var handle = NativeMethods.GetClipboardData(NativeMethods.ClipboardFormatDib);
                    if (handle == IntPtr.Zero) return null;

                    var allocation = (ulong)NativeMethods.GlobalSize(handle);
                    var pointer = NativeMethods.GlobalLock(handle);
                    if (pointer == IntPtr.Zero) return null;
                    try
                    {
                        return ReadBounded(pointer, ReadableBytes(pointer, allocation));
                    }
                    finally
                    {
                        NativeMethods.GlobalUnlock(handle);
                    }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            if (attempt >= Retries) return null;
            Thread.Sleep(RetryDelayMs);
        }
    }

    // Sanity bound for one clip; anything larger is not a screenshot or a paste.
    private const long MaxClipBytes = 128L * 1024 * 1024;

    // How many bytes from pointer on are committed, readable memory, up to limit. GlobalSize
    // describes the allocation, not what is mapped, and reading past the mapping is an access
    // violation .NET cannot catch.
    internal static ulong ReadableBytes(IntPtr pointer, ulong limit)
    {
        ulong total = 0;
        var address = pointer;
        var infoSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();
        while (total < limit)
        {
            if (NativeMethods.VirtualQuery(address, out var info, infoSize) == 0) break;
            if (info.State != NativeMethods.MemCommit || info.Protect == 0 ||
                (info.Protect & (NativeMethods.PageNoAccess | NativeMethods.PageGuard)) != 0) break;

            var regionEnd = (ulong)info.BaseAddress + info.RegionSize;
            total += regionEnd - (ulong)address;
            address = (IntPtr)(long)regionEnd;
        }

        return Math.Min(total, limit);
    }

    // CF_UNICODETEXT up to its first null character, and never past the readable bytes: a block
    // without one would otherwise be scanned until the read faults.
    internal static string? ReadText(IntPtr pointer, ulong readable)
    {
        var chars = (int)(readable / sizeof(char));
        if (chars == 0) return null;

        var text = new char[chars];
        Marshal.Copy(pointer, text, 0, chars);
        var end = Array.IndexOf(text, '\0');
        return new string(text, 0, end < 0 ? chars : end);
    }

    // Reads a CF_DIB block with the size cross-checked against the block's own header. GlobalSize
    // can overstate the allocation for handles rendered by another app, and trusting it walked off
    // the mapped memory in Marshal.Copy - an access violation .NET cannot catch, so the process
    // died. The copy length is the smaller of the header-derived size and the allocation.
    internal static byte[]? ReadBounded(IntPtr pointer, ulong allocation)
    {
        const int HeaderSize = 40;                       // BITMAPINFOHEADER
        const long MaxImageBytes = MaxClipBytes;

        if (allocation < HeaderSize || allocation > MaxImageBytes) return null;

        var header = new byte[HeaderSize];
        Marshal.Copy(pointer, header, 0, HeaderSize);

        var headerBytes = BitConverter.ToInt32(header, 0);           // biSize
        if (headerBytes < 12 || (ulong)headerBytes > allocation) return null;
        var width = BitConverter.ToInt32(header, 4);
        var height = BitConverter.ToInt32(header, 8);
        var bitCount = BitConverter.ToUInt16(header, 14);            // biBitCount
        // The masks and palette between header and pixels count too, and rows pad to 4 bytes;
        // missing either left the block short and undecodable (BI_BITFIELDS screenshots lost 12).
        var stride = ((long)Math.Abs(width) * bitCount + 31) / 32 * 4;
        var pixels = stride * Math.Abs(height);
        var expected = (long)ImageClipCodec.PixelOffset(header, headerBytes) + pixels;
        if (expected <= HeaderSize) return null;

        var size = (int)Math.Min(expected, (long)allocation);
        var bytes = new byte[size];
        Marshal.Copy(pointer, bytes, 0, size);
        return bytes;
    }

    public static bool TryWriteText(string text)
    {
        // The text plus the terminating null character.
        var bytes = new byte[(text.Length + 1) * sizeof(char)];
        System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return TrySetData(NativeMethods.ClipboardFormatUnicodeText, bytes);
    }

    // A CF_DIB block (the codec's ToDib output); every Windows app reads images from it.
    public static bool TryWriteImage(byte[] dib) => TrySetData(NativeMethods.ClipboardFormatDib, dib);

    private static bool TrySetData(uint format, byte[] data)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!NativeMethods.EmptyClipboard()) return false;

                    var handle = NativeMethods.GlobalAlloc(
                        NativeMethods.GlobalMemoryMoveable | NativeMethods.GlobalMemoryZeroInit, (nuint)data.Length);
                    if (handle == IntPtr.Zero) return false;

                    var pointer = NativeMethods.GlobalLock(handle);
                    if (pointer == IntPtr.Zero)
                    {
                        NativeMethods.GlobalFree(handle);
                        return false;
                    }
                    try
                    {
                        Marshal.Copy(data, 0, pointer, data.Length);
                    }
                    finally
                    {
                        NativeMethods.GlobalUnlock(handle);
                    }

                    // Ownership passes to the clipboard on success; only free it if that failed.
                    if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero)
                    {
                        NativeMethods.GlobalFree(handle);
                        return false;
                    }

                    return true;
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            if (attempt >= Retries) return false;
            Thread.Sleep(RetryDelayMs);
        }
    }
}
