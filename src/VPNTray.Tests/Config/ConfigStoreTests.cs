using VPNTray.Config;

namespace VPNTray.Tests.Config;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpntray-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_ReturnsNull_WhenFileMissing()
        => Assert.Null(new ConfigStore(_dir).Load());

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var store = new ConfigStore(_dir);
        store.Save(new VpnConfig { Username = "u", Gateway = "g", TotpSecret = "s" });
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("u", loaded!.Username);
        Assert.Equal("g", loaded.Gateway);
    }

    [Fact]
    public void Load_BacksUpAndReturnsNull_OnCorruptFile()
    {
        var store = new ConfigStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, "{ this is not valid json");
        Assert.Null(store.Load());
        Assert.True(File.Exists(store.FilePath + ".broken"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
