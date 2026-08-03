using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class TotpGeneratorTests
{
    // RFC 6238 Appendix B uses secret "12345678901234567890" (ASCII).
    // Base32 of that ASCII is "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ".
    private const string Rfc6238Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    public void Code_MatchesRfc6238Vectors(long unixSeconds, string expected)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(expected, TotpGenerator.Code(Rfc6238Secret, at, digits: 6));
    }

    [Fact]
    public void Base32Decode_DecodesAsciiSecret()
    {
        var bytes = TotpGenerator.Base32Decode("GEZDGNBVGY3TQOJQ");
        Assert.Equal("1234567890", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Base32Decode_IgnoresPaddingSpacesAndCase()
    {
        var a = TotpGenerator.Base32Decode("gezd gnbv gy3t qojq=");
        var b = TotpGenerator.Base32Decode("GEZDGNBVGY3TQOJQ");
        Assert.Equal(b, a);
    }

    [Theory]
    [InlineData("gezd gnbv gy3t qojq=", "GEZDGNBVGY3TQOJQ")]
    [InlineData("GEZDGNBVGY3TQOJQ", "GEZDGNBVGY3TQOJQ")]
    public void NormalizeSecret_UpperCasesAndStripsGroupingAndPadding(string input, string expected)
        => Assert.Equal(expected, TotpGenerator.NormalizeSecret(input));

    [Fact]
    public void Base32Decode_ThrowsOnInvalidChar()
    {
        Assert.Throws<TotpException>(() => TotpGenerator.Base32Decode("0189!"));
    }

    [Fact]
    public void Code_ThrowsOnInvalidDigits()
    {
        Assert.Throws<TotpException>(() => TotpGenerator.Code(Rfc6238Secret, DateTimeOffset.UnixEpoch, digits: 0));
    }
}
