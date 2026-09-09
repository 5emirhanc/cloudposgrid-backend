using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Marketplace;

/// <summary>Ürün ↔ pazaryeri barkodu eşleştirmesi (Mapping) + tek tuşla ilan açma (createProducts).</summary>
public sealed class MarketplaceListingService : IMarketplaceListingService
{
    private readonly IApplicationDbContext _db;
    private readonly IMarketplaceProviderFactory _providers;
    private readonly ISecretProtector _protector;
    private readonly IPublicUrlBuilder _urls;

    public MarketplaceListingService(IApplicationDbContext db, IMarketplaceProviderFactory providers,
        ISecretProtector protector, IPublicUrlBuilder urls)
    {
        _db = db;
        _providers = providers;
        _protector = protector;
        _urls = urls;
    }

    public async Task<IReadOnlyList<MarketplaceListingDto>> ListAsync(CancellationToken ct = default) =>
        await _db.MarketplaceListings
            .OrderBy(l => l.Product!.Name)
            .Select(l => new MarketplaceListingDto(l.Id, l.ProductId, l.Product!.Name, l.Product.Barcode,
                l.MarketplaceBarcode, l.IsActive, l.LastPushedStock, l.LastPushedAt,
                l.ListingStatus, l.TrendyolCategoryId, l.ListingError, l.ListedAt))
            .ToListAsync(ct);

    public async Task<MarketplaceListingDto> CreateAsync(CreateListingRequest req, CancellationToken ct = default)
    {
        var conn = await _db.MarketplaceConnections.FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("Önce bir pazaryeri bağlantısı ekleyin.");
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == req.ProductId, ct)
            ?? throw NotFoundException.For("Ürün", req.ProductId);
        var barcode = req.MarketplaceBarcode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(barcode)) throw new BusinessRuleException("Pazaryeri barkodu gerekli.");
        if (await _db.MarketplaceListings.AnyAsync(l => l.ConnectionId == conn.Id && l.MarketplaceBarcode == barcode, ct))
            throw new ConflictException("Bu pazaryeri barkodu zaten eşleştirilmiş.");

        var listing = new MarketplaceListing { ConnectionId = conn.Id, ProductId = product.Id, MarketplaceBarcode = barcode, IsActive = true };
        _db.MarketplaceListings.Add(listing);
        await _db.SaveChangesAsync(ct);
        return ToDto(listing, product);
    }

    public async Task<MarketplaceListingDto> UpdateAsync(Guid id, UpdateListingRequest req, CancellationToken ct = default)
    {
        var listing = await _db.MarketplaceListings.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw NotFoundException.For("Eşleştirme", id);
        var barcode = req.MarketplaceBarcode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(barcode)) throw new BusinessRuleException("Pazaryeri barkodu gerekli.");
        if (!string.Equals(listing.MarketplaceBarcode, barcode, StringComparison.OrdinalIgnoreCase) &&
            await _db.MarketplaceListings.AnyAsync(l => l.ConnectionId == listing.ConnectionId && l.MarketplaceBarcode == barcode && l.Id != id, ct))
            throw new ConflictException("Bu pazaryeri barkodu zaten eşleştirilmiş.");
        listing.MarketplaceBarcode = barcode;
        listing.IsActive = req.IsActive;
        await _db.SaveChangesAsync(ct);
        return ToDto(listing, listing.Product!);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var listing = await _db.MarketplaceListings.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw NotFoundException.For("Eşleştirme", id);
        _db.MarketplaceListings.Remove(listing);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> AutoMatchAsync(CancellationToken ct = default)
    {
        var conn = await _db.MarketplaceConnections.FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("Önce bir pazaryeri bağlantısı ekleyin.");

        var mappedProductIds = await _db.MarketplaceListings.Where(l => l.ConnectionId == conn.Id)
            .Select(l => l.ProductId).ToListAsync(ct);
        var usedBarcodes = new HashSet<string>(
            await _db.MarketplaceListings.Where(l => l.ConnectionId == conn.Id).Select(l => l.MarketplaceBarcode).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);

        // Barkodlu, aktif, hizmet-olmayan ve henüz eşleşmemiş ürünler → barkod = ürün barkodu ile eşle.
        var candidates = await _db.Products
            .Where(p => p.Barcode != null && p.IsActive && !p.IsService && !mappedProductIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Barcode })
            .ToListAsync(ct);

        var added = 0;
        foreach (var c in candidates)
        {
            if (c.Barcode is null || !usedBarcodes.Add(c.Barcode)) continue; // aynı barkod bağlantıda tekil olmalı
            _db.MarketplaceListings.Add(new MarketplaceListing
            {
                ConnectionId = conn.Id, ProductId = c.Id, MarketplaceBarcode = c.Barcode, IsActive = true,
            });
            added++;
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }

    // ---- İlan açma (createProducts) ----

    public async Task<IReadOnlyList<MarketplaceCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var (p, creds, _) = await ResolveAsync(ct);
        return await p.GetCategoriesAsync(creds, ct);
    }

    public async Task<IReadOnlyList<MarketplaceCategoryAttribute>> GetCategoryAttributesAsync(int categoryId, CancellationToken ct = default)
    {
        var (p, creds, _) = await ResolveAsync(ct);
        return await p.GetCategoryAttributesAsync(creds, categoryId, ct);
    }

    public async Task<IReadOnlyList<MarketplaceBrand>> SearchBrandsAsync(string query, CancellationToken ct = default)
    {
        var (p, creds, _) = await ResolveAsync(ct);
        return await p.SearchBrandsAsync(creds, query ?? "", ct);
    }

    public async Task<IReadOnlyList<MarketplaceCargoProvider>> GetCargoProvidersAsync(CancellationToken ct = default)
    {
        var (p, creds, _) = await ResolveAsync(ct);
        return await p.GetCargoProvidersAsync(creds, ct);
    }

    public async Task<MarketplaceListingDto> SubmitListingAsync(Guid productId, SubmitListingRequest req, CancellationToken ct = default)
    {
        var (provider, creds, conn) = await ResolveAsync(ct);
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw NotFoundException.For("Ürün", productId);

        // Trendyol zorunlulukları
        var barcode = product.Barcode?.Trim();
        if (string.IsNullOrWhiteSpace(barcode))
            throw new BusinessRuleException("İlan açmak için ürünün barkodu olmalı.");
        if (product.IsService)
            throw new BusinessRuleException("Hizmet kalemi pazaryerinde ilan olarak açılamaz.");
        if (product.SalePrice <= 0)
            throw new BusinessRuleException("İlan açmadan önce ürünün satış fiyatını girin.");

        // Barkod başka bir ürünün ilanına aitse (ConnectionId, MarketplaceBarcode) tekil index'i ihlal olur →
        // Trendyol'a GÖNDERMEDEN önce durdur (yoksa gönderim başarılı olur ama yerel kayıt 500 verir, ilan izlenemez kalır).
        var barcodeOwner = await _db.MarketplaceListings
            .FirstOrDefaultAsync(l => l.ConnectionId == conn.Id && l.MarketplaceBarcode == barcode && l.ProductId != productId, ct);
        if (barcodeOwner is not null)
            throw new ConflictException($"'{barcode}' barkodu zaten başka bir ürünle eşleşmiş. Önce eşleştirmeyi düzeltin.");

        // Görseller mutlak HTTPS olmalı (Trendyol oradan çeker). App:PublicApiUrl boşsa relatif kalır → sessizce reddedilmesin, göndermeden durdur.
        var present = new List<string?> { product.ImageUrl }.Concat(product.ImageUrls)
            .Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (present.Count == 0)
            throw new BusinessRuleException("İlan için en az 1 ürün görseli gerekli.");
        var images = present
            .Select(u => _urls.ToAbsolute(u)!)
            .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct().Take(8).ToList();
        if (images.Count == 0)
            throw new BusinessRuleException("Görseller Trendyol'a gönderilemiyor: sunucu genel API adresi (App:PublicApiUrl) ayarlı değil.");

        var salePrice = product.SalePrice;
        var listPrice = req.ListPrice is { } lp && lp >= salePrice ? lp : salePrice; // Trendyol: listPrice ≥ salePrice
        var quantity = (int)Math.Max(0, Math.Floor(product.CurrentStock));

        var data = new MarketplaceListingData(
            Barcode: barcode!,
            Title: product.Name.Length > 100 ? product.Name[..100] : product.Name,
            ProductMainId: product.Sku,
            BrandId: req.BrandId,
            CategoryId: req.CategoryId,
            Quantity: quantity,
            StockCode: product.Sku,
            DimensionalWeight: product.DimensionalWeight is { } dw && dw > 0m ? dw : 1m,
            Description: string.IsNullOrWhiteSpace(product.Description) ? product.Name : product.Description!,
            ListPrice: listPrice,
            SalePrice: salePrice,
            VatRate: product.VatRate,
            CargoCompanyId: req.CargoCompanyId,
            Images: images,
            Attributes: (req.Attributes ?? new List<ListingAttributeInput>())
                .Select(a => new MarketplaceListingAttribute(a.AttributeId, a.AttributeValueId, a.CustomValue)).ToList());

        var result = await provider.CreateListingAsync(creds, data, ct);
        if (!result.Success)
            throw new BusinessRuleException(result.Error ?? "İlan gönderilemedi.");

        // İlan satırını bul/oluştur → Submitted + batchId (asenkron durum takibi arka plan işinde).
        var listing = await _db.MarketplaceListings
            .FirstOrDefaultAsync(l => l.ConnectionId == conn.Id && l.ProductId == productId, ct);
        if (listing is null)
        {
            listing = new MarketplaceListing { ConnectionId = conn.Id, ProductId = productId, MarketplaceBarcode = barcode!, IsActive = true };
            _db.MarketplaceListings.Add(listing);
        }
        listing.MarketplaceBarcode = barcode!;
        listing.ListingStatus = MarketplaceListingStatus.Submitted;
        listing.BatchRequestId = result.BatchRequestId;
        listing.TrendyolCategoryId = req.CategoryId;
        listing.TrendyolBrandId = req.BrandId;
        listing.CargoCompanyId = req.CargoCompanyId;
        listing.AttributesJson = JsonSerializer.Serialize(req.Attributes ?? new List<ListingAttributeInput>());
        listing.ListingError = null;
        await _db.SaveChangesAsync(ct);
        return ToDto(listing, product);
    }

    private async Task<(IMarketplaceProvider Provider, MarketplaceCredentials Creds, MarketplaceConnection Conn)> ResolveAsync(CancellationToken ct)
    {
        var conn = await _db.MarketplaceConnections.FirstOrDefaultAsync(c => c.IsActive, ct)
            ?? throw new BusinessRuleException("Önce bir aktif pazaryeri bağlantısı ekleyin.");
        var provider = _providers.Get(conn.Channel)
            ?? throw new BusinessRuleException($"Desteklenmeyen kanal: {conn.Channel}");
        var creds = new MarketplaceCredentials(conn.SupplierId,
            _protector.Unprotect(conn.ApiKeyEnc), _protector.Unprotect(conn.ApiSecretEnc));
        return (provider, creds, conn);
    }

    private static MarketplaceListingDto ToDto(MarketplaceListing l, Product p) =>
        new(l.Id, l.ProductId, p.Name, p.Barcode, l.MarketplaceBarcode, l.IsActive, l.LastPushedStock, l.LastPushedAt,
            l.ListingStatus, l.TrendyolCategoryId, l.ListingError, l.ListedAt);
}
