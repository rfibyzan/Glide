using Glide.Core.Search;

namespace Glide.Core.Tests;

public class QueryParserTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("report", new[] { "report" })]
    [InlineData("  Report   FINAL ", new[] { "report", "final" })]
    [InlineData("a\tb\nc", new[] { "a", "b", "c" })]
    public void SplitsOnWhitespaceAndNormalizesCase(string input, string[] expected)
    {
        Assert.Equal(expected, QueryParser.Parse(input));
    }

    [Fact]
    public void RemovesDuplicateTermsWhileKeepingOrder()
    {
        Assert.Equal(["doc", "rep"], QueryParser.Parse("doc rep doc"));
    }

    [Fact]
    public void HandlesNullAsEmpty()
    {
        Assert.Empty(QueryParser.Parse(null));
    }
}
