using System.Runtime.Versioning;
using Copyloft.Desktop.Platform.Windows;

namespace Copyloft.Desktop.Tests;

// Which Windows messages count as the user stepping away; checked without a real session event.
public sealed class SessionWatcherTests
{
    private const uint SessionChange = 0x02B1;
    private const uint PowerBroadcast = 0x0218;

    [Theory]
    [InlineData(SessionChange, 0x7, true)]     // WTS_SESSION_LOCK (Win+L)
    [InlineData(SessionChange, 0x1, true)]     // WTS_CONSOLE_DISCONNECT (switch user)
    [InlineData(SessionChange, 0x4, true)]     // WTS_REMOTE_DISCONNECT
    [InlineData(SessionChange, 0x8, false)]    // WTS_SESSION_UNLOCK
    [InlineData(PowerBroadcast, 0x4, true)]    // PBT_APMSUSPEND
    [InlineData(PowerBroadcast, 0x12, false)]  // PBT_APMRESUMEAUTOMATIC
    [InlineData(0x0016u, 0x7, false)]          // WM_ENDSESSION is Ending, not Locking
    [SupportedOSPlatform("windows")]
    public void Away_messages(uint message, int code, bool expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows messages");

        Assert.Equal(expected, SessionWatcher.IsSteppingAway(message, code));
    }
}
