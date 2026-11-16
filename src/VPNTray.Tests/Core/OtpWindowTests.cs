using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class OtpWindowTests
{
    /// <summary>Exactly on a 30-second boundary.</summary>
    private static readonly DateTimeOffset WindowStart = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);

    [Fact]
    public void Counter_IsTheRfc6238TimeStep()
        => Assert.Equal(1_700_000_010 / 30, OtpWindow.Counter(WindowStart.AddSeconds(29)));

    [Fact]
    public void WaitBeforeSend_IsZero_ForAnUnusedWindowWithTimeLeft()
        => Assert.Equal(TimeSpan.Zero, OtpWindow.WaitBeforeSend(WindowStart.AddSeconds(10), lastSentCounter: null));

    [Fact]
    public void WaitBeforeSend_IsZero_WhenTheLastCodeWasFromAnEarlierWindow()
    {
        long previous = OtpWindow.Counter(WindowStart) - 1;
        Assert.Equal(TimeSpan.Zero, OtpWindow.WaitBeforeSend(WindowStart.AddSeconds(10), previous));
    }

    [Fact]
    public void WaitBeforeSend_WaitsForTheNextWindow_WhenThisWindowsCodeWasAlreadySent()
    {
        var now = WindowStart.AddSeconds(10);
        var wait = OtpWindow.WaitBeforeSend(now, OtpWindow.Counter(now));

        Assert.InRange(wait, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(21));
        Assert.Equal(OtpWindow.Counter(now) + 1, OtpWindow.Counter(now + wait));
    }

    [Fact]
    public void WaitBeforeSend_WaitsForTheNextWindow_WhenTheCodeIsAboutToExpire()
    {
        var now = WindowStart.AddSeconds(28.5);
        var wait = OtpWindow.WaitBeforeSend(now, lastSentCounter: null);

        Assert.True(wait > TimeSpan.Zero);
        Assert.Equal(OtpWindow.Counter(now) + 1, OtpWindow.Counter(now + wait));
    }
}
