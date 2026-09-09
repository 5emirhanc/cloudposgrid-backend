using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Stock;

public record ProductOptionDto(Guid Id, Guid ProductId, string GroupName, string Name, decimal PriceDelta, int SortOrder);
public record ProductOptionItem(string GroupName, string Name, decimal PriceDelta, int SortOrder);
/// <summary>Bir ürünün TÜM opsiyonlarını değiştirir (sil-yeniden kur).</summary>
public record SaveProductOptionsRequest(List<ProductOptionItem> Options);

public interface IProductOptionService
{
    Task<IReadOnlyList<ProductOptionDto>> ListByProductAsync(Guid productId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductOptionDto>> SaveAsync(Guid productId, SaveProductOptionsRequest req, CancellationToken ct = default);
}

/// <summary>
/// Ürün opsiyon yönetimi (#29). Bir ürünün seçeneklerini (grup + ad + fiyat farkı) tutar. POS satışta bunları
/// gösterip seçileni fiyata/nota uygular (fatura yolu değişmez). Sil-yeniden kur ile toplu güncellenir.
/// </summary>
public sealed class ProductOptionService : IProductOptionService
{
    private readonly IApplicationDbContext _db;

    public ProductOptionService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProductOptionDto>> ListByProductAsync(Guid productId, CancellationToken ct = default)
        => await _db.ProductOptions.Where(o => o.ProductId == productId)
            .OrderBy(o => o.GroupName).ThenBy(o => o.SortOrder).ThenBy(o => o.Name)
            .Select(o => new ProductOptionDto(o.Id, o.ProductId, o.GroupName, o.Name, o.PriceDelta, o.SortOrder))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ProductOptionDto>> SaveAsync(Guid productId, SaveProductOptionsRequest req, CancellationToken ct = default)
    {
        if (!await _db.Products.AnyAsync(p => p.Id == productId, ct))
            throw NotFoundException.For("Ürün", productId);

        var existing = await _db.ProductOptions.Where(o => o.ProductId == productId).ToListAsync(ct);
        _db.ProductOptions.RemoveRange(existing);

        foreach (var o in req.Options ?? new())
        {
            if (string.IsNullOrWhiteSpace(o.GroupName) || string.IsNullOrWhiteSpace(o.Name)) continue;
            _db.ProductOptions.Add(new ProductOption
            {
                ProductId = productId,
                GroupName = o.GroupName.Trim(),
                Name = o.Name.Trim(),
                PriceDelta = o.PriceDelta,
                SortOrder = o.SortOrder,
            });
        }
        await _db.SaveChangesAsync(ct);
        return await ListByProductAsync(productId, ct);
    }
}
