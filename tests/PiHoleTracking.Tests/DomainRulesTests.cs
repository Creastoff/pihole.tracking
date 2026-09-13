namespace PiHoleTracking.Tests;

public sealed class DomainRulesTests
{
    [Fact]
    public void TryNormalize_TrimsLowercasesAndRemovesTrailingDot()
    {
        var valid = DomainRules.TryNormalize("  Example.COM.  ", out var normalized);

        Assert.True(valid);
        Assert.Equal("example.com", normalized);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("_acme-challenge.example.com")]
    [InlineData("sub_domain.example.co.uk")]
    public void TryNormalize_AcceptsValidDomainNames(string value)
    {
        Assert.True(DomainRules.TryNormalize(value, out _));
    }

    [Theory]
    [InlineData("example")]
    [InlineData(".example.com")]
    [InlineData("example..com")]
    [InlineData("-example.com")]
    [InlineData("example-.com")]
    [InlineData("example.com/")]
    public void TryNormalize_RejectsInvalidDomainNames(string value)
    {
        var valid = DomainRules.TryNormalize(value, out var normalized);

        Assert.False(valid);
        Assert.NotNull(normalized);
    }
}
