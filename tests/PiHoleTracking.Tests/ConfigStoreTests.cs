namespace PiHoleTracking.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public async Task ConfigurationPersistsSeparatelyAndNormalizesValues()
    {
        using var data = new TemporaryDataDirectory();
        var store = new ConfigStore(data.Environment);

        await store.UpdateConfigAsync(new ReviewConfigRequest(
            "https://pi.hole/admin/",
            "86400",
            false,
            false,
            "blocked",
            "alpha",
            "192.168.1.10"));

        var reloaded = new ConfigStore(data.Environment).Snapshot();

        Assert.Equal("https://pi.hole", reloaded.PiholeUrl);
        Assert.Equal("86400", reloaded.QueryRange);
        Assert.False(reloaded.IncludeDisk);
        Assert.False(reloaded.HidePiholeBlocked);
        Assert.Equal("blocked", reloaded.Filter);
        Assert.Equal("alpha", reloaded.Sort);
        Assert.Equal("192.168.1.10", reloaded.IpFilter);
        Assert.True(File.Exists(Path.Combine(data.RootPath, "config.json")));
        Assert.False(File.Exists(Path.Combine(data.RootPath, "review-state.json")));
    }

    [Fact]
    public async Task InvalidOptionsFallBackToTheExistingConfiguration()
    {
        using var data = new TemporaryDataDirectory();
        var store = new ConfigStore(data.Environment);

        await store.UpdateConfigAsync(new ReviewConfigRequest(
            "not-a-url",
            "not-a-range",
            null,
            null,
            "not-a-filter",
            "not-a-sort",
            "   "));

        var result = store.Snapshot();

        Assert.Equal(ReviewConfig.Default, result);
    }
}
