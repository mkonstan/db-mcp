using DbMcp.Data.Services;

namespace DbMcp.Data.Tests;

public class BatchSplitterTests
{
    [Fact]
    public void Split_NoSeparatorLine_ReturnsSingleSegment()
    {
        var result = BatchSplitter.Split("SELECT 1;\nSELECT 2;", "GO");

        Assert.Single(result);
    }

    [Fact]
    public void Split_TwoBatches_SplitsOnToken()
    {
        var result = BatchSplitter.Split("A\nGO\nB", "GO");

        Assert.Equal(2, result.Count);
        Assert.Contains("A", result[0]);
        Assert.Contains("B", result[1]);
    }

    [Fact]
    public void Split_HandlesCrLfAndLf()
    {
        var lf = BatchSplitter.Split("A\nGO\nB", "GO");
        var crlf = BatchSplitter.Split("A\r\nGO\r\nB", "GO");

        Assert.Equal(2, lf.Count);
        Assert.Equal(2, crlf.Count);
    }

    [Fact]
    public void Split_TokenMatchIsCaseSensitive()
    {
        var result = BatchSplitter.Split("A\ngo\nB", "GO");

        Assert.Single(result);
    }

    [Fact]
    public void Split_TokenMustBeAloneOnLine()
    {
        var withSemicolon = BatchSplitter.Split("A\nGO;\nB", "GO");
        var withPrefix = BatchSplitter.Split("A\nEXEC GO\nB", "GO");

        Assert.Single(withSemicolon);
        Assert.Single(withPrefix);
    }

    [Fact]
    public void Split_TrimsSurroundingWhitespaceOnSeparatorLine()
    {
        var result = BatchSplitter.Split("A\n  GO  \nB", "GO");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Split_DropsEmptyAndWhitespaceSegments()
    {
        var result = BatchSplitter.Split("GO\nGO\nA\nGO", "GO");

        Assert.Single(result);
        Assert.Contains("A", result[0]);
    }

    [Fact]
    public void Split_LeadingAndTrailingSeparators()
    {
        var result = BatchSplitter.Split("GO\nA\nGO\nB\nGO", "GO");

        Assert.Equal(2, result.Count);
        Assert.Contains("A", result[0]);
        Assert.Contains("B", result[1]);
    }

    [Fact]
    public void Split_ArbitraryToken()
    {
        var stars = BatchSplitter.Split("A\n****\nB", "****");
        var backticks = BatchSplitter.Split("A\n```\nB", "```");

        Assert.Equal(2, stars.Count);
        Assert.Equal(2, backticks.Count);
    }
}
