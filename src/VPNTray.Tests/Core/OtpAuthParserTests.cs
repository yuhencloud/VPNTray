using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class OtpAuthParserTests
{
    [Fact]
    public void TryExtractSecret_ParsesOtpAuthUri()
    {
        var uri = "otpauth://totp/example:zhang.san?secret=ABCDEFGHIJKLMNOP2345&issuer=example";
        Assert.Equal("ABCDEFGHIJKLMNOP2345", OtpAuthParser.TryExtractSecret(uri));
    }

    [Fact]
    public void TryExtractSecret_ParsesUriWithSecretNotFirstParam()
    {
        var uri = "otpauth://totp/x?issuer=corp&secret=abcdefghijklmnop2345&period=30";
        Assert.Equal("ABCDEFGHIJKLMNOP2345", OtpAuthParser.TryExtractSecret(uri));
    }

    [Fact]
    public void TryExtractSecret_AcceptsBareBase32()
    {
        Assert.Equal("ABCDEFGHIJKLMNOP2345", OtpAuthParser.TryExtractSecret("abcd efgh ijkl mnop 2345"));
    }

    [Fact]
    public void TryExtractSecret_StripsPadding()
    {
        Assert.Equal("ABCDEFGHIJKLMNOPQR234567", OtpAuthParser.TryExtractSecret("ABCDEFGHIJKLMNOPQR234567===="));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/not-otp")]
    [InlineData("otpauth://totp/no-query")]
    [InlineData("otpauth://totp/x?issuer=corp")]           // no secret param
    [InlineData("otpauth://totp/x?secret=short")]          // too short
    [InlineData("otpauth://totp/x?secret=INVALID018CHARS!")] // bad Base32 chars
    [InlineData("hello world this is not base32 at all 0189")]
    public void TryExtractSecret_ReturnsNull_ForNonSecrets(string? text)
        => Assert.Null(OtpAuthParser.TryExtractSecret(text));

    [Fact]
    public void TryExtractSecret_UrlDecodesSecretValue()
    {
        var uri = "otpauth://totp/x?secret=ABCD%20EFGH%20IJKL%20MNOP%202345";
        Assert.Equal("ABCDEFGHIJKLMNOP2345", OtpAuthParser.TryExtractSecret(uri));
    }
}
