namespace PiHoleTracking.Tests;

public sealed class PiholeSessionStoreTests
{
    [Fact]
    public void AddTryGetAndRemoveManageSessions()
    {
        var store = new PiholeSessionStore();
        var id = store.Add("http://pi.hole", "sid", "csrf");

        Assert.True(store.TryGet(id, out var session));
        Assert.Equal(new PiholeSession("http://pi.hole", "sid", "csrf"), session);

        store.Remove(id);

        Assert.False(store.TryGet(id, out _));
    }

    [Fact]
    public void TryGetRejectsMissingIds()
    {
        var store = new PiholeSessionStore();

        Assert.False(store.TryGet(null, out _));
        Assert.False(store.TryGet("", out _));
    }
}
