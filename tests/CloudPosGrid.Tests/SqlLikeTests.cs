using CloudPosGrid.Application.Common;

namespace CloudPosGrid.Tests;

public class SqlLikeTests
{
    [Fact]
    public void Contains_WrapsTermWithWildcards()
        => Assert.Equal("%abc%", SqlLike.Contains("abc"));

    [Fact]
    public void Contains_TrimsWhitespace()
        => Assert.Equal("%abc%", SqlLike.Contains("  abc  "));

    [Theory]
    [InlineData("50%", "%50\\%%")]   // % kaçışlanır
    [InlineData("a_b", "%a\\_b%")]   // _ kaçışlanır
    [InlineData("c\\d", "%c\\\\d%")] // \ kaçışlanır
    public void Contains_EscapesLikeSpecialChars(string input, string expected)
        => Assert.Equal(expected, SqlLike.Contains(input));
}
