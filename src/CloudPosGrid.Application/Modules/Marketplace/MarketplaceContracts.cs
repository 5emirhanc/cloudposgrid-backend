using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Marketplace;

// ---- Bağlantı (kanal + kimlik) ----
/// <summary>Pazaryeri bağlantısı — API kimlikleri ASLA dönmez (yalnız tanımlı mı bilgisi).</summary>
public record MarketplaceConnectionDto(
    Guid Id, string Channel, string SupplierId, bool IsActive,
    DateTime? LastStockSyncAt, DateTime? LastOrderSyncAt, string? LastStatus, string? LastMessage,
    bool HasCredentials, DateTime CreatedAt,
    // Kâr raporunda bu kanalın kârından düşülen kesintiler (satıcı elle girer).
    decimal CommissionRate, decimal ShippingCost);

public record CreateConnectionRequest(
    string Channel, string SupplierId, string ApiKey, string ApiSecret,
    decimal CommissionRate = 0m, decimal ShippingCost = 0m);

/// <summary>ApiKey/ApiSecret null bırakılırsa mevcut kimlik korunur (yalnız verilenler güncellenir).</summary>
public record UpdateConnectionRequest(
    string SupplierId, string? ApiKey, string? ApiSecret, bool IsActive,
    decimal CommissionRate = 0m, decimal ShippingCost = 0m);

// ---- Eşleştirme (ürün ↔ pazaryeri barkodu) + ilan durumu ----
public record MarketplaceListingDto(
    Guid Id, Guid ProductId, string ProductName, string? ProductBarcode,
    string MarketplaceBarcode, bool IsActive, decimal? LastPushedStock, DateTime? LastPushedAt,
    MarketplaceListingStatus ListingStatus, int? TrendyolCategoryId, string? ListingError, DateTime? ListedAt);

public record CreateListingRequest(Guid ProductId, string MarketplaceBarcode);
public record UpdateListingRequest(string MarketplaceBarcode, bool IsActive);

// ---- İlan açma (createProducts) ----
/// <summary>İlan sihirbazından gelen tek öznitelik seçimi (değer id'si veya serbest metin).</summary>
public record ListingAttributeInput(long AttributeId, long? AttributeValueId, string? CustomValue);

/// <summary>Bir ürünü Trendyol'da ilan olarak açma isteği. Başlık/açıklama/görsel/fiyat üründen; burada kategori/marka/kargo/öznitelik.
/// ListPrice null ise SalePrice kullanılır (Trendyol listPrice ≥ salePrice ister).</summary>
public record SubmitListingRequest(
    int CategoryId, int BrandId, int CargoCompanyId,
    decimal? ListPrice, IReadOnlyList<ListingAttributeInput> Attributes);

// ---- Çekilen sipariş ----
public record MarketplaceOrderDto(
    Guid Id, string Channel, string MarketplaceOrderNumber, string? BuyerName, decimal GrandTotal,
    string? MarketplaceStatus, DateTime OrderDate, Guid? InvoiceId,
    MarketplaceOrderSyncStatus SyncStatus, string? SyncError, DateTime CreatedAt);

public class MarketplaceOrderQuery : PagedQuery
{
    public MarketplaceOrderSyncStatus? SyncStatus { get; set; }
}

// ---- Senkron sonucu ----
public record SyncResultDto(bool Success, string Message, int OrdersImported, int StockPushed, DateTime SyncedAt);

// ---- Servis arayüzleri ----
public interface IMarketplaceConnectionService
{
    Task<IReadOnlyList<MarketplaceConnectionDto>> ListAsync(CancellationToken ct = default);
    Task<MarketplaceConnectionDto> CreateAsync(CreateConnectionRequest req, CancellationToken ct = default);
    Task<MarketplaceConnectionDto> UpdateAsync(Guid id, UpdateConnectionRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>Kimlikleri hafif bir çağrıyla doğrular.</summary>
    Task<SyncResultDto> TestAsync(Guid id, CancellationToken ct = default);
}

public interface IMarketplaceListingService
{
    Task<IReadOnlyList<MarketplaceListingDto>> ListAsync(CancellationToken ct = default);
    Task<MarketplaceListingDto> CreateAsync(CreateListingRequest req, CancellationToken ct = default);
    Task<MarketplaceListingDto> UpdateAsync(Guid id, UpdateListingRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>Barkodu olup henüz eşleşmemiş ürünleri, bağlantı için otomatik eşleştirir. Oluşan eşleşme sayısını döner.</summary>
    Task<int> AutoMatchAsync(CancellationToken ct = default);

    // ---- İlan açma referans verileri (aktif Trendyol bağlantısının kimliğiyle sağlayıcıya proxy) ----
    Task<IReadOnlyList<MarketplaceCategory>> GetCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MarketplaceCategoryAttribute>> GetCategoryAttributesAsync(int categoryId, CancellationToken ct = default);
    Task<IReadOnlyList<MarketplaceBrand>> SearchBrandsAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<MarketplaceCargoProvider>> GetCargoProvidersAsync(CancellationToken ct = default);
    /// <summary>Ürünü Trendyol'da ilan olarak açar (createProducts); listing satırını Submitted + BatchRequestId ile günceller/oluşturur.</summary>
    Task<MarketplaceListingDto> SubmitListingAsync(Guid productId, SubmitListingRequest req, CancellationToken ct = default);
}

public interface IMarketplaceOrderService
{
    Task<PagedResult<MarketplaceOrderDto>> GetAsync(MarketplaceOrderQuery query, CancellationToken ct = default);
    /// <summary>Bildirim çanı için: son 24 saatte içe alınan pazaryeri siparişleri (en yeni ilk).</summary>
    Task<IReadOnlyList<MarketplaceOrderDto>> GetPendingAsync(CancellationToken ct = default);
}

public interface IMarketplaceSyncService
{
    /// <summary>Geçerli tenant'ın tüm aktif bağlantılarını senkronlar (sipariş çek + stok gönder).</summary>
    Task<SyncResultDto> SyncTenantAsync(CancellationToken ct = default);
    /// <summary>Tek bir bağlantıyı senkronlar ("şimdi senkronize et").</summary>
    Task<SyncResultDto> SyncConnectionAsync(Guid connectionId, CancellationToken ct = default);
}
