namespace PiHoleTracking.Tests;

public sealed class PiholeUrlTests
{
    [Theory]
    [InlineData("http://pi.hole", "http://pi.hole")]
    [InlineData("http://pi.hole/admin/", "http://pi.hole")]
    [InlineData("https://pi.hole/api/?unused=true#fragment", "https://pi.hole")]
    [InlineData("http://192.168.1.10:8080/pihole", "http://192.168.1.10:8080/pihole")]
    public void TryNormalize_ReturnsApiBaseUrl(string value, string expected)
    {
        var valid = PiholeUrl.TryNormalize(value, out var normalized);

        Assert.True(valid);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pi.hole")]
    [InlineData("ftp://pi.hole")]
    public void TryNormalize_RejectsInvalidUrls(string? value)
    {
        var valid = PiholeUrl.TryNormalize(value, out var normalized);

        Assert.False(valid);
        Assert.Empty(normalized);
    }
}
