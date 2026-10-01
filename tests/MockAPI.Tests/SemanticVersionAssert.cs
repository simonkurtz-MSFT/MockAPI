namespace MockAPI.Tests;

internal static class SemanticVersionAssert
{
    private const string Number = "(?:0|[1-9][0-9]*)";
    private const string PrereleaseIdentifier = $"(?:{Number}|[0-9]*[A-Za-z-][0-9A-Za-z-]*)";
    private const string MetadataIdentifier = "[0-9A-Za-z-]+";

    internal const string Pattern =
        $@"{Number}\.{Number}\.{Number}(?:-{PrereleaseIdentifier}(?:\.{PrereleaseIdentifier})*)?(?:\+{MetadataIdentifier}(?:\.{MetadataIdentifier})*)?";

    internal static void IsValid(string? version)
    {
        Assert.NotNull(version);
        Assert.Matches($@"\A{Pattern}\z", version);
    }
}
