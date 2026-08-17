using VPNTray.Util;

namespace VPNTray.Tests.Util;

public class AppVersionTests
{
    [Fact]
    public void Display_IsTheAssemblyVersion_WithoutBuildMetadata()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", AppVersion.Display);
        Assert.DoesNotContain("+", AppVersion.Display);
    }

    [Theory]
    // The SDK appends "+<commit>" whenever SourceRevisionId is set, which a title bar
    // has no use for.
    [InlineData("1.1.0+9e4f1c2abc", "1.1.0")]
    [InlineData("1.1.0-beta.2+9e4f1c2", "1.1.0-beta.2")]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData("  1.1.0  ", "1.1.0")]
    public void Trim_DropsEverythingFromTheFirstPlus(string informational, string expected)
        => Assert.Equal(expected, AppVersion.Trim(informational));
}
