namespace VPNTray.Core;

/// <summary>
/// Timing rules for sending a TOTP the app computed itself (appended-OTP login). The gateway
/// accepts each code once: a second login with a code it already saw is rejected, and a few
/// rejections in a row lock the account. So a code is only sent when it is one the gateway has
/// not seen yet and has enough life left to arrive before it rolls over.
/// </summary>
public static class OtpWindow
{
    public const int StepSeconds = 30;

    /// <summary>
    /// A code with less life left than this is not sent: the handshake takes a round trip or
    /// two, and a code that expires in flight is spent on a guaranteed failure.
    /// </summary>
    public static readonly TimeSpan MinRemaining = TimeSpan.FromSeconds(3);

    /// <summary>Lands just past the boundary, so the clock reading after the wait is in the new window.</summary>
    private static readonly TimeSpan BoundarySlack = TimeSpan.FromMilliseconds(250);

    /// <summary>The RFC 6238 time-step counter <paramref name="at"/> falls in.</summary>
    public static long Counter(DateTimeOffset at) => Math.Max(0, at.ToUnixTimeSeconds()) / StepSeconds;

    /// <summary>
    /// How long to wait before a code can be sent at <paramref name="now"/>. Zero when the
    /// current window is unused and has at least <see cref="MinRemaining"/> left; otherwise
    /// the time to the start of the next window.
    /// </summary>
    /// <param name="lastSentCounter">Window of the last code sent, or null if none was.</param>
    public static TimeSpan WaitBeforeSend(DateTimeOffset now, long? lastSentCounter)
    {
        long counter = Counter(now);
        var windowEnd = DateTimeOffset.FromUnixTimeSeconds((counter + 1) * StepSeconds);
        var remaining = windowEnd - now;

        bool alreadySent = lastSentCounter is long last && last >= counter;
        if (!alreadySent && remaining >= MinRemaining) return TimeSpan.Zero;
        return remaining + BoundarySlack;
    }
}
