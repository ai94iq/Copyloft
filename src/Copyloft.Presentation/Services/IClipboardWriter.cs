namespace Copyloft.Presentation.Services;

// Writing clipboard content back. An interface so view models can be tested without Win32.
// Both writes report whether the clipboard took the content, so paste-back can skip a failed one.
public interface IClipboardWriter
{
    Task<bool> WriteText(string text);

    // Puts a PNG back on the clipboard so image clips paste into apps that take bitmaps.
    Task<bool> WriteImageAsync(byte[] png);
}
