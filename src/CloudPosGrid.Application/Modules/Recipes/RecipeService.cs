using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Recipes;

public record RecipeComponentDto(Guid ComponentProductId, string ComponentName, string Unit, decimal Quantity, decimal UnitCost);
public record RecipeDto(Guid ProductId, IReadOnlyList<RecipeComponentDto> Components, decimal TotalCost);
public record SetRecipeComponent(Guid ComponentProductId, decimal Quantity);
public record SetRecipeRequest(List<SetRecipeComponent> Components);

public interface IRecipeService
{
    Task<RecipeDto> GetAsync(Guid productId, CancellationToken ct = default);
    Task<RecipeDto> SetAsync(Guid productId, SetRecipeRequest req, CancellationToken ct = default);
}

/// <summary>
/// Reçete/BOM yönetimi: bir bitmiş ürünün (ör. Latte) bileşenlerini (çekirdek/süt/bardak) tanımlar.
/// Satışta InvoiceService bu satırlara bakıp KENDİ stoğu yerine bileşenleri düşer.
/// </summary>
public sealed class RecipeService : IRecipeService
{
    private readonly IApplicationDbContext _db;

    public RecipeService(IApplicationDbContext db) => _db = db;

    public async Task<RecipeDto> GetAsync(Guid productId, CancellationToken ct = default)
    {
        var rows = await _db.RecipeComponents.Where(r => r.ProductId == productId).ToListAsync(ct);
        return await BuildDtoAsync(productId, rows, ct);
    }

    public async Task<RecipeDto> SetAsync(Guid productId, SetRecipeRequest req, CancellationToken ct = default)
    {
        if (!await _db.Products.AnyAsync(p => p.Id == productId, ct))
            throw NotFoundException.For("Ürün", productId);

        var components = (req.Components ?? new()).Where(c => c.Quantity > 0m).ToList();
        // Ürün kendini bileşen yapamaz (sonsuz döngü / anlamsız).
        if (components.Any(c => c.ComponentProductId == productId))
            throw new BusinessRuleException("Ürün kendi reçetesine bileşen olarak eklenemez.");
        // Bileşenler geçerli ürün olmalı.
        var compIds = components.Select(c => c.ComponentProductId).Distinct().ToList();
        if (compIds.Count > 0)
        {
            var validCount = await _db.Products.CountAsync(p => compIds.Contains(p.Id), ct);
            if (validCount != compIds.Count) throw new BusinessRuleException("Reçetede geçersiz bileşen ürün var.");
        }

        // Sil-yeniden kur (basit ve tutarlı): mevcut satırları kaldır, yenilerini ekle.
        var existing = await _db.RecipeComponents.Where(r => r.ProductId == productId).ToListAsync(ct);
        _db.RecipeComponents.RemoveRange(existing);
        // Aynı bileşen iki kez verilirse miktarları topla (unique index ihlalini önle).
        foreach (var g in components.GroupBy(c => c.ComponentProductId))
        {
            _db.RecipeComponents.Add(new RecipeComponent
            {
                ProductId = productId,
                ComponentProductId = g.Key,
                Quantity = g.Sum(x => x.Quantity),
            });
        }
        await _db.SaveChangesAsync(ct);

        var rows = await _db.RecipeComponents.Where(r => r.ProductId == productId).ToListAsync(ct);
        return await BuildDtoAsync(productId, rows, ct);
    }

    private async Task<RecipeDto> BuildDtoAsync(Guid productId, List<RecipeComponent> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return new RecipeDto(productId, [], 0m);
        var ids = rows.Select(r => r.ComponentProductId).Distinct().ToList();
        var comps = await _db.Products.Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Unit, p.PurchasePrice }).ToListAsync(ct);
        var map = comps.ToDictionary(c => c.Id);
        var items = rows.Select(r =>
        {
            map.TryGetValue(r.ComponentProductId, out var c);
            return new RecipeComponentDto(r.ComponentProductId, c?.Name ?? "—", c?.Unit ?? "", r.Quantity, c?.PurchasePrice ?? 0m);
        }).ToList();
        var total = Math.Round(items.Sum(i => i.UnitCost * i.Quantity), 2);
        return new RecipeDto(productId, items, total);
    }
}
