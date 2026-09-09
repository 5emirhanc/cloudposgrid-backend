namespace CloudPosGrid.Application.Modules.Menu;

public record MenuItemDto(Guid Id, string Name, string? Description, decimal SalePrice, string? ImageUrl);
public record MenuCategoryDto(string Name, IReadOnlyList<MenuItemDto> Items);
// OrderingEnabled: masadan sipariş (QR) açık mı? Yalnız Kurumsal + erişimi açık işletmede true.
// Controller doldurur; MenuService plandan habersiz olduğu için varsayılan false.
public record MenuDto(string CompanyName, string? LogoUrl, string Currency, IReadOnlyList<MenuCategoryDto> Categories, bool OrderingEnabled = false);

public interface IMenuService
{
    Task<MenuDto> GetMenuAsync(CancellationToken ct = default);
}
