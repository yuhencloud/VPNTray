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
    public void Load_MigratesSchemaV1File_ThenSaveRewritesItWithoutTheLegacyKey()
    {
        var store = new ConfigStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, """
            {
              "Username": "first.last",
              "PasswordPrefix": "Secret2021",
              "TotpSecret": "GEZDGNBVGY3TQOJQ",
              "Gateway": "vpn.example.com",
              "ServerCertPin": "pin-sha256:AAAA",
              "SchemaVersion": 1
            }
            """);

        var migrated = store.Load();
        Assert.NotNull(migrated);
        Assert.Equal("Secret2021", migrated!.Password);

        // Settings rebuilds the config from scratch on save, so the version resets to current.
        store.Save(new VpnConfig { Username = migrated.Username, Password = migrated.Password });

        var json = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("PasswordPrefix", json);
        Assert.Contains("\"Password\": \"Secret2021\"", json);
        Assert.Contains("\"SchemaVersion\": 2", json);
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
