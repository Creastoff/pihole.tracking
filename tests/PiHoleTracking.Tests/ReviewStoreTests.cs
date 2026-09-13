using System.Text.Json;

namespace PiHoleTracking.Tests;

public sealed class ReviewStoreTests
{
    [Fact]
    public async Task DecisionsAndInvestigationsPersistAcrossStoreInstances()
    {
        using var data = new TemporaryDataDirectory();
        var firstStore = new ReviewStore(data.Environment);

        await firstStore.MarkKnownAsync("known.example");
        await firstStore.AddBlockedAsync("blocked.example");
        await firstStore.MarkPiholeSyncedAsync("blocked.example");
        await firstStore.SaveInvestigationAsync("investigate.example", "Check this service before blocking.");

        var reloaded = new ReviewStore(data.Environment).Snapshot();

        Assert.Equal(new[] { "known.example" }, reloaded.KnownDomains);
        Assert.Equal(new[] { "blocked.example" }, reloaded.BlockedDomains);
        Assert.Equal(new[] { "blocked.example" }, reloaded.PiholeSyncedDomains);
        var investigation = Assert.Single(reloaded.Investigations!);
        Assert.Equal("investigate.example", investigation.Domain);
        Assert.Equal("Check this service before blocking.", investigation.Notes);
    }

    [Fact]
    public async Task ReviewStateFileDoesNotContainConfigurationOrCachedImport()
    {
        using var data = new TemporaryDataDirectory();
        var store = new ReviewStore(data.Environment);

        await store.MarkKnownAsync("known.example");

        var json = await File.ReadAllTextAsync(Path.Combine(data.RootPath, "review-state.json"));

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("Config", out _));
        Assert.False(document.RootElement.TryGetProperty("CachedImport", out _));
    }

    [Fact]
    public async Task RemovingAReviewDecisionPersistsTheRemoval()
    {
        using var data = new TemporaryDataDirectory();
        var store = new ReviewStore(data.Environment);

        await store.MarkKnownAsync("known.example");
        await store.AddBlockedAsync("blocked.example");
        await store.SaveInvestigationAsync("investigate.example", "Initial note");
        await store.UnmarkKnownAsync("known.example");
        await store.RemoveBlockedAsync("blocked.example");
        await store.RemoveInvestigationAsync("investigate.example");

        var reloaded = new ReviewStore(data.Environment).Snapshot();

        Assert.Empty(reloaded.KnownDomains!);
        Assert.Empty(reloaded.BlockedDomains!);
        Assert.Empty(reloaded.Investigations!);
    }
}
