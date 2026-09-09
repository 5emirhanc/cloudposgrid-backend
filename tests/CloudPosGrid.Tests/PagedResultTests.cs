using CloudPosGrid.Application.Common;

namespace CloudPosGrid.Tests;

public class PagedResultTests
{
    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    [InlineData(45, 20, 3)]
    public void TotalPages_IsCeilingOfTotalOverPageSize(int total, int pageSize, int expected)
    {
        var result = new PagedResult<string>(Array.Empty<string>(), total, 1, pageSize);
        Assert.Equal(expected, result.TotalPages);
    }

    [Fact]
    public void PagedQuery_ClampsInvalidPageAndPageSize()
    {
        var q = new PagedQuery { Page = 0, PageSize = 5000 };
        Assert.Equal(1, q.Page);       // < 1  -> 1
        Assert.Equal(20, q.PageSize);  // > 200 -> 20 (varsayılan)
        Assert.Equal(0, q.Skip);
    }

    [Fact]
    public void PagedQuery_ComputesSkipFromPage()
    {
        var q = new PagedQuery { Page = 3, PageSize = 25 };
        Assert.Equal(50, q.Skip);
    }
}
