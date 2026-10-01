namespace MockAPI.Tests;

public sealed class SemanticVersionAssertTests
{
    [Theory]
    [InlineData("0.0.0")]
    [InlineData("2.10.4")]
    [InlineData("3.0.0-alpha.1")]
    [InlineData("3.0.0-0")]
    [InlineData("3.0.0-01a")]
    [InlineData("3.0.0+build.001")]
    [InlineData("3.0.0-rc.2+abc123")]
    public void IsValid_AcceptsSemanticVersions(string version)
    {
        SemanticVersionAssert.IsValid(version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v3.0.0")]
    [InlineData("3.0")]
    [InlineData("03.0.0")]
    [InlineData("3.00.0")]
    [InlineData("3.0.00")]
    [InlineData("3.0.0-alpha.01")]
    [InlineData("3.0.0-")]
    [InlineData("3.0.0+")]
    [InlineData("3.0.0+build..1")]
    [InlineData("3.0.0\n")]
    [InlineData(" 3.0.0")]
    public void IsValid_RejectsMissingOrMalformedVersions(string? version)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => SemanticVersionAssert.IsValid(version));
    }
}
