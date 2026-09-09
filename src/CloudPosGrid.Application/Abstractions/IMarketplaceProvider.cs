namespace CloudPosGrid.Application.Abstractions;

/// <summary>Bir pazaryeri hesabının kimlik bilgileri (çözülmüş/düz — yalnız bellek içi kullanım).</summary>
public record MarketplaceCredentials(string SupplierId, string ApiKey, string ApiSecret);

/// <summary>Pazaryerine gönderilecek tek ürünün güncel stoğu.</summary>
public record MarketplaceStockItem(string Barcode, int Quantity);

/// <summary>Stok gönderme sonucu. BatchRequestId dolu ise gönderim asenkron kabul edildi; gerçek başarı
/// GetBatchResultAsync ile doğrulanır (yoksa senkron kabul → hemen kesinleşir).</summary>
public record MarketplacePushResult(bool Success, int PushedCount, string? Error, string? BatchRequestId = null);

/// <summary>Pazaryerinden çekilen sipariş satırı (barkod + adet + birim fiyat).</summary>
public record MarketplaceOrderLineData(string Barcode, decimal Quantity, decimal UnitPrice);

/// <summary>Pazaryerinden çekilen tek sipariş (normalize edilmiş — adapter çıktısı).</summary>
public record MarketplaceOrderData(
    string OrderNumber,
    string? BuyerName,
    DateTime OrderDate,
    string? Status,
    decimal GrandTotal,
    IReadOnlyList<MarketplaceOrderLineData> Lines,
    string? RawJson);

// ---- İlan açma (createProducts) referans verileri ----

/// <summary>Pazaryeri kategori ağacı düğümü.</summary>
public record MarketplaceCategory(int Id, string Name, int? ParentId, bool IsLeaf);

/// <summary>Bir kategori özniteliğinin izin verilen değeri.</summary>
public record MarketplaceAttributeValue(long Id, string Name);

/// <summary>Kategoriye özgü öznitelik (renk/beden vb.); Required=zorunlu, AllowCustom=serbest metin girilebilir.</summary>
public record MarketplaceCategoryAttribute(
    long Id, string Name, bool Required, bool AllowCustom, IReadOnlyList<MarketplaceAttributeValue> Values);

/// <summary>Pazaryerinde kayıtlı marka.</summary>
public record MarketplaceBrand(int Id, string Name);

/// <summary>Kargo/gönderim sağlayıcısı (kargo şablonu).</summary>
public record MarketplaceCargoProvider(int Id, string Name);

/// <summary>İlanda doldurulmuş tek öznitelik: değer listesinden seçim (AttributeValueId) ya da serbest metin (CustomValue).</summary>
public record MarketplaceListingAttribute(long AttributeId, long? AttributeValueId, string? CustomValue);

/// <summary>Pazaryerinde ilan açmak için normalize edilmiş ürün verisi (adapter, kanalın formatına çevirir).</summary>
public record MarketplaceListingData(
    string Barcode,
    string Title,
    string ProductMainId,
    int BrandId,
    int CategoryId,
    int Quantity,
    string StockCode,
    decimal DimensionalWeight,
    string Description,
    decimal ListPrice,
    decimal SalePrice,
    decimal VatRate,
    int CargoCompanyId,
    IReadOnlyList<string> Images,
    IReadOnlyList<MarketplaceListingAttribute> Attributes);

/// <summary>İlan gönderim sonucu: başarılıysa asenkron takip için BatchRequestId döner.</summary>
public record MarketplaceCreateResult(bool Success, string? BatchRequestId, string? Error);

/// <summary>Toplu istekteki tek kalemin sonucu.</summary>
public record MarketplaceBatchItemResult(string? Barcode, string Status, string? Reason);

/// <summary>Toplu istek durumu: Found=istek bulundu mu, Status genel durum (Processing/Completed vb.), Items kalem sonuçları.</summary>
public record MarketplaceBatchResult(bool Found, string Status, IReadOnlyList<MarketplaceBatchItemResult> Items);

/// <summary>
/// Bir pazaryeri kanalının ADAPTER'ı: bizim modelimiz ↔ o pazaryerinin API formatı çevirimini yapar.
/// Trendyol için <c>TrendyolProvider</c>; ileride Hepsiburada/N11 aynı arayüzü uygular.
/// </summary>
public interface IMarketplaceProvider
{
    /// <summary>Kanal adı ("Trendyol").</summary>
    string Channel { get; }

    /// <summary>Verilen ürünlerin güncel stoğunu pazaryerine gönderir (biz → pazaryeri).</summary>
    Task<MarketplacePushResult> PushStockAsync(
        MarketplaceCredentials credentials, IReadOnlyList<MarketplaceStockItem> items, CancellationToken ct = default);

    /// <summary>Belirtilen tarihten sonraki yeni siparişleri çeker (pazaryeri → biz).</summary>
    Task<IReadOnlyList<MarketplaceOrderData>> FetchOrdersAsync(
        MarketplaceCredentials credentials, DateTime since, CancellationToken ct = default);

    /// <summary>Saklanan ham sipariş JSON'unu normalize modele geri çevirir (takılı siparişleri watermark
    /// ilerledikten sonra bile yeniden işlemek/reconcile için). Parse edilemezse null.</summary>
    MarketplaceOrderData? ParseOrder(string rawJson);

    /// <summary>Kimlikleri hafif bir çağrıyla doğrular (Success=true ise bağlantı çalışıyor).</summary>
    Task<MarketplacePushResult> TestConnectionAsync(MarketplaceCredentials credentials, CancellationToken ct = default);

    // ---- İlan açma (createProducts) ----

    /// <summary>Pazaryeri kategori ağacını getirir.</summary>
    Task<IReadOnlyList<MarketplaceCategory>> GetCategoriesAsync(MarketplaceCredentials credentials, CancellationToken ct = default);

    /// <summary>Bir kategorinin zorunlu/opsiyonel özniteliklerini getirir.</summary>
    Task<IReadOnlyList<MarketplaceCategoryAttribute>> GetCategoryAttributesAsync(
        MarketplaceCredentials credentials, int categoryId, CancellationToken ct = default);

    /// <summary>Ada göre kayıtlı markaları arar.</summary>
    Task<IReadOnlyList<MarketplaceBrand>> SearchBrandsAsync(
        MarketplaceCredentials credentials, string query, CancellationToken ct = default);

    /// <summary>Kargo/gönderim sağlayıcılarını getirir.</summary>
    Task<IReadOnlyList<MarketplaceCargoProvider>> GetCargoProvidersAsync(MarketplaceCredentials credentials, CancellationToken ct = default);

    /// <summary>Ürünü pazaryerinde ilan olarak açar (asenkron); başarılıysa BatchRequestId döner.</summary>
    Task<MarketplaceCreateResult> CreateListingAsync(
        MarketplaceCredentials credentials, MarketplaceListingData data, CancellationToken ct = default);

    /// <summary>Asenkron ilan gönderiminin (batchRequestId) durumunu sorgular.</summary>
    Task<MarketplaceBatchResult> GetBatchResultAsync(
        MarketplaceCredentials credentials, string batchRequestId, CancellationToken ct = default);
}

/// <summary>Kanal adına göre doğru sağlayıcıyı verir (MVP: yalnız Trendyol).</summary>
public interface IMarketplaceProviderFactory
{
    IMarketplaceProvider? Get(string channel);
}
