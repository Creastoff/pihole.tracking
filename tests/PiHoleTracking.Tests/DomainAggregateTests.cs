namespace PiHoleTracking.Tests;

public sealed class DomainAggregateTests
{
    [Fact]
    public void Add_AggregatesCountsClientsAndTimeRange()
    {
        var aggregate = new DomainAggregate("example.com");
        var firstTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var lastTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        aggregate.Add("FORWARDED", firstTimestamp, "Laptop", "192.168.1.10");
        aggregate.Add("GRAVITY", lastTimestamp, "Laptop", "192.168.1.10");
        aggregate.Add("BLOCKED", lastTimestamp, "Phone", "192.168.1.11");

        var result = aggregate.ToResult();

        Assert.Equal("example.com", result.Domain);
        Assert.Equal(3, result.QueryCount);
        Assert.Equal(2, result.BlockedCount);
        Assert.Equal(2, result.Clients.Count);
        Assert.Contains("Laptop", result.Clients);
        Assert.Contains("Phone", result.Clients);
        Assert.Equal(2, result.ClientIps.Count);
        Assert.Equal("GRAVITY", result.PrimaryStatus);
        Assert.NotNull(result.FirstSeen);
        Assert.NotNull(result.LastSeen);
        Assert.True(result.FirstSeen <= result.LastSeen);
    }

    [Fact]
    public void Add_IgnoresNonPositiveTimestamps()
    {
        var aggregate = new DomainAggregate("example.com");

        aggregate.Add("UNKNOWN", 0, null, null);

        var result = aggregate.ToResult();
        Assert.Equal(1, result.QueryCount);
        Assert.Null(result.FirstSeen);
        Assert.Null(result.LastSeen);
    }
}
