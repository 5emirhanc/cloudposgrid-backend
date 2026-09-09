namespace CloudPosGrid.Application.Common;

/// <summary>Sayfalı liste sonucu.</summary>
public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(Total / (double)PageSize) : 0;

    public PagedResult() { }

    public PagedResult(IReadOnlyList<T> items, int total, int page, int pageSize)
    {
        Items = items;
        Total = total;
        Page = page;
        PageSize = pageSize;
    }
}

/// <summary>Sayfalama/arama için ortak sorgu parametreleri.</summary>
public class PagedQuery
{
    private int _page = 1;
    private int _pageSize = 20;

    public int Page { get => _page; set => _page = value < 1 ? 1 : value; }
    public int PageSize { get => _pageSize; set => _pageSize = value is < 1 or > 200 ? 20 : value; }
    public string? Search { get; set; }

    public int Skip => (Page - 1) * PageSize;
}
