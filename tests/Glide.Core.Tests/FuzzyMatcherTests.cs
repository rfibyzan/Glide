using Glide.Core.Search;

namespace Glide.Core.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void EmptyPatternMatchesEverythingWithZeroScore()
    {
        Assert.Equal(0, FuzzyMatcher.Score(string.Empty, "anything.txt"));
    }

    [Fact]
    public void ReturnsNullWhenPatternIsNotASubsequence()
    {
        Assert.Null(FuzzyMatcher.Score("xyz", "report.docx"));
    }

    [Fact]
    public void ReturnsNullWhenPatternIsLongerThanCandidate()
    {
        Assert.Null(FuzzyMatcher.Score("longer", "abc"));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        int? lower = FuzzyMatcher.Score("readme", "README.md");
        int? upper = FuzzyMatcher.Score("README", "README.md");

        Assert.NotNull(lower);
        Assert.NotNull(upper);
    }

    [Fact]
    public void ConsecutiveRunBeatsScatteredCharacters()
    {
        int? consecutive = FuzzyMatcher.Score("abc", "abcX");
        int? scattered = FuzzyMatcher.Score("abc", "aXbXc");

        Assert.NotNull(consecutive);
        Assert.NotNull(scattered);
        Assert.True(consecutive > scattered, $"consecutive={consecutive} scattered={scattered}");
    }

    [Fact]
    public void WordBoundaryStartBeatsMidWordStart()
    {
        int? boundary = FuzzyMatcher.Score("fp", "file-picker");
        int? midWord = FuzzyMatcher.Score("fp", "flip");

        Assert.NotNull(boundary);
        Assert.NotNull(midWord);
        Assert.True(boundary > midWord, $"boundary={boundary} midWord={midWord}");
    }

    [Fact]
    public void CamelCaseBoundaryCounts()
    {
        int? camel = FuzzyMatcher.Score("mw", "MainWindow.xaml");
        int? midWord = FuzzyMatcher.Score("mw", "amxw");

        Assert.NotNull(camel);
        Assert.NotNull(midWord);
        Assert.True(camel > midWord, $"camel={camel} midWord={midWord}");
    }

    [Fact]
    public void PrefixBeatsInnerMatch()
    {
        int? prefix = FuzzyMatcher.Score("note", "notes.txt");
        int? inner = FuzzyMatcher.Score("note", "mynotes.txt");

        Assert.NotNull(prefix);
        Assert.NotNull(inner);
        Assert.True(prefix > inner, $"prefix={prefix} inner={inner}");
    }

    [Fact]
    public void LeadingGapIsPenalizedButBounded()
    {
        int? early = FuzzyMatcher.Score("a", "a----------------b");
        int? late = FuzzyMatcher.Score("a", "bbbbbbbbbbbbbbbbbbbbbba");

        Assert.NotNull(early);
        Assert.NotNull(late);
        Assert.True(early > late, $"early={early} late={late}");
    }

    [Fact]
    public void ScoreIsDeterministic()
    {
        int? first = FuzzyMatcher.Score("rpt", "reports\\annual-report.txt");
        int? second = FuzzyMatcher.Score("rpt", "reports\\annual-report.txt");

        Assert.Equal(first, second);
    }

    [Fact]
    public void MatchPositionsFollowTheCandidate()
    {
        int[] positions = FuzzyMatcher.MatchPositions("abc", "a_b_c");

        Assert.Equal([0, 2, 4], positions);
    }

    [Fact]
    public void MatchPositionsAreEmptyWhenNoMatch()
    {
        Assert.Empty(FuzzyMatcher.MatchPositions("zz", "abc"));
    }
}
