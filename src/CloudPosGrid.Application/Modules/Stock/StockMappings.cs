using System.Linq.Expressions;
using CloudPosGrid.Domain.Entities;

namespace CloudPosGrid.Application.Modules.Stock;

/// <summary>EF Select içinde çevrilebilir (translatable) projeksiyonlar.</summary>
public static class StockMappings
{
    public static readonly Expression<Func<Product, ProductDto>> ToProductDto = p => new ProductDto(
        p.Id, p.Sku, p.Barcode, p.Name, p.CategoryId,
        p.Category != null ? p.Category.Name : null,
        p.Unit, p.PurchasePrice, p.SalePrice, p.VatRate,
        p.CurrentStock, p.MinStock, p.CurrentStock <= p.MinStock, p.IsActive, p.IsService,
        p.ImageUrl, p.Description, p.IsVisibleOnMenu, p.MenuSortOrder,
        p.ShelfLocation, p.StorageArea, p.CreatedAt,
        p.BrandName, p.ImageUrls, p.DimensionalWeight,
        p.ParentProductId, p.IsVariantParent, p.VariantValues, p.VariantAttributesJson,
        (decimal?)null, // BranchStock: ProductService şube seçiliyse doldurur (ifade ağacı opsiyonel argüman kabul etmez)
        p.ScaleItemCode, p.ExpiryDate, p.PurchaseUnit, p.PurchaseUnitFactor);

    public static readonly Expression<Func<StockMovement, StockMovementDto>> ToMovementDto = m => new StockMovementDto(
        m.Id, m.ProductId, m.Product.Name, m.Type, m.Quantity, m.UnitCost,
        m.Reference, m.Note, m.StockAfter, m.CreatedAt);
}
