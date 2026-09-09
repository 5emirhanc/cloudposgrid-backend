using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Purchasing;

/// <summary>Bir ürün-tedarikçi eşleme satırı (tedarikçi adı + ürün adı çözülmüş halde).</summary>
public record ProductSupplierDto(
    Guid Id, Guid ProductId, string ProductName, Guid ContactId, string ContactName,
    string? SupplierSku, decimal LastPurchasePrice, int LeadTimeDays,
    decimal MinOrderQuantity, bool IsPreferred);

/// <summary>Upsert girdisindeki tek satır (Id yok — tüm liste sil-yeniden kur ile değiştirilir).</summary>
public record ProductSupplierItem(
    Guid ContactId, string? SupplierSku, decimal LastPurchasePrice, int LeadTimeDays,
    decimal MinOrderQuantity, bool IsPreferred);

/// <summary>Bir ürünün tedarikçi eşlemelerinin TAMAMINI değiştirir (verilmeyen satırlar silinir).</summary>
public record UpsertProductSuppliersRequest(List<ProductSupplierItem> Suppliers);

public interface IProductSupplierService
{
    /// <summary>Bir ürünün tedarikçi eşlemelerini tedarikçi adıyla birlikte getirir (tercih edilenler önce).</summary>
    Task<IReadOnlyList<ProductSupplierDto>> ListByProductAsync(Guid productId, CancellationToken ct = default);
    /// <summary>Ürünün tedarikçi listesini tümüyle yeniden yazar (sil-yeniden kur). Sonuç: güncel liste.</summary>
    Task<IReadOnlyList<ProductSupplierDto>> UpsertAsync(Guid productId, UpsertProductSuppliersRequest req, CancellationToken ct = default);
    /// <summary>Tek bir tedarikçi eşlemesini kaldırır.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Ürün-tedarikçi eşleme yönetimi: bir ürünün tedarik kaynaklarını ve her kaynağın alım koşullarını tutar.
/// Sipariş önerisi/satın alma tarafı tercih edilen tedarikçiyi ve teslim süresini buradan okur.
/// Entity tenant geneli olduğundan (BranchId yok) şube filtresi uygulanmaz.
/// </summary>
public sealed class ProductSupplierService : IProductSupplierService
{
    private readonly IApplicationDbContext _db;

    public ProductSupplierService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProductSupplierDto>> ListByProductAsync(Guid productId, CancellationToken ct = default)
    {
        var rows = await _db.ProductSuppliers.Where(x => x.ProductId == productId)
            .OrderByDescending(x => x.IsPreferred).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var productName = await _db.Products.Where(p => p.Id == productId)
            .Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "";
        var contactIds = rows.Select(x => x.ContactId).Distinct().ToList();
        var contacts = await _db.Contacts.Where(c => contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var names = contacts.ToDictionary(c => c.Id, c => c.Name);

        return rows.Select(x => new ProductSupplierDto(
            x.Id, x.ProductId, productName, x.ContactId,
            names.TryGetValue(x.ContactId, out var n) ? n : "—",
            x.SupplierSku, x.LastPurchasePrice, x.LeadTimeDays, x.MinOrderQuantity, x.IsPreferred)).ToList();
    }

    public async Task<IReadOnlyList<ProductSupplierDto>> UpsertAsync(Guid productId, UpsertProductSuppliersRequest req, CancellationToken ct = default)
    {
        if (!await _db.Products.AnyAsync(p => p.Id == productId, ct))
            throw NotFoundException.For("Ürün", productId);

        var items = (req.Suppliers ?? new()).ToList();
        // Tüm tedarikçi carileri geçerli olmalı.
        var contactIds = items.Select(i => i.ContactId).Distinct().ToList();
        if (contactIds.Count > 0)
        {
            var validCount = await _db.Contacts.CountAsync(c => contactIds.Contains(c.Id), ct);
            if (validCount != contactIds.Count)
                throw new BusinessRuleException("Eşlemede geçersiz tedarikçi carisi var.");
        }

        // Sil-yeniden kur (RecipeService deseni): ProductSupplier'ın izlenen üst navigasyonu YOK (ProductId/
        // ContactId düz FK), bu yüzden RemoveRange temiz DELETE üretir; EF tek SaveChanges'te DELETE'leri
        // INSERT'lerden önce yürütür → (ProductId,ContactId) benzersiz indeksi ihlal edilmez.
        var existing = await _db.ProductSuppliers.Where(x => x.ProductId == productId).ToListAsync(ct);
        _db.ProductSuppliers.RemoveRange(existing);

        // Aynı tedarikçi iki kez verilirse SON satır geçerli (benzersiz indeks ihlalini önler).
        foreach (var g in items.GroupBy(i => i.ContactId))
        {
            var last = g.Last();
            _db.ProductSuppliers.Add(new ProductSupplier
            {
                ProductId = productId,
                ContactId = g.Key,
                SupplierSku = Clean(last.SupplierSku),
                LastPurchasePrice = Math.Max(0m, last.LastPurchasePrice),
                LeadTimeDays = Math.Max(0, last.LeadTimeDays),
                MinOrderQuantity = Math.Max(0m, last.MinOrderQuantity),
                IsPreferred = last.IsPreferred,
            });
        }
        await _db.SaveChangesAsync(ct);

        return await ListByProductAsync(productId, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.ProductSuppliers.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw NotFoundException.For("Ürün-tedarikçi eşlemesi", id);
        _db.ProductSuppliers.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
