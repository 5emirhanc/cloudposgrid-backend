using CloudPosGrid.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Menu;

/// <summary>Public QR menü için aktif + menüde görünür ürünleri kategori bazında derler.</summary>
public sealed class MenuService : IMenuService
{
    private readonly IApplicationDbContext _db;

    public MenuService(IApplicationDbContext db) => _db = db;

    public async Task<MenuDto> GetMenuAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);

        var rows = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.IsVisibleOnMenu)
            .OrderBy(p => p.MenuSortOrder).ThenBy(p => p.Name)
            .Select(p => new
            {
                CategoryName = p.Category != null ? p.Category.Name : null,
                Item = new MenuItemDto(p.Id, p.Name, p.Description, p.SalePrice, p.ImageUrl),
            })
            .ToListAsync(ct);

        var categories = rows
            .GroupBy(r => r.CategoryName ?? "Diğer")
            .Select(g => new MenuCategoryDto(g.Key, g.Select(x => x.Item).ToList()))
            .ToList();

        return new MenuDto(
            settings?.CompanyName ?? "Menü",
            settings?.LogoUrl,
            settings?.Currency ?? "TRY",
            categories);
    }
}
