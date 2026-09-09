using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Stock;

// ---- Kategori ----
public record CategoryDto(Guid Id, string Name, bool IsActive, int ProductCount);
public record CreateCategoryRequest(string Name);
public record UpdateCategoryRequest(string Name, bool IsActive);

// ---- Ürün ----
public record ProductDto(
    Guid Id, string Sku, string? Barcode, string Name,
    Guid? CategoryId, string? CategoryName, string Unit,
    decimal PurchasePrice, decimal SalePrice, decimal VatRate,
    decimal CurrentStock, decimal MinStock, bool IsLowStock, bool IsActive,
    bool IsService, string? ImageUrl, string? Description, bool IsVisibleOnMenu, int MenuSortOrder,
    string? ShelfLocation, string? StorageArea,
    DateTime CreatedAt,
    // Pazaryeri: marka + ek görseller + desi
    string? BrandName, List<string> ImageUrls,
    decimal? DimensionalWeight = null,
    // Varyantlar (butik beden/renk): parent şablon + varyant satırları
    Guid? ParentProductId = null, bool IsVariantParent = false, string? VariantValues = null, string? VariantAttributesJson = null,
    // Çok-şube TAM stok: AKTİF şubenin bakiyesi (şube seçiliyse). null = "tüm şubeler" → CurrentStock toplamı gösterilir.
    decimal? BranchStock = null,
    // Terazi barkoduna gömülü ürün kodu (market tartılabilir kalem). Doluysa etiket okutmayla miktar çözülür.
    string? ScaleItemCode = null,
    // Son kullanma tarihi (SKT) — yaklaşan/geçen SKT bildirim üretir.
    DateTime? ExpiryDate = null,
    // Çoklu ölçü birimi: alış birimi (ör. "koli") + 1 alış birimi kaç satış birimidir.
    string? PurchaseUnit = null, decimal? PurchaseUnitFactor = null);

public record CreateProductRequest(
    string? Sku, string? Barcode, string Name, Guid? CategoryId, string Unit,
    decimal PurchasePrice, decimal SalePrice, decimal VatRate,
    decimal OpeningStock, decimal MinStock, bool IsService = false,
    string? ImageUrl = null, string? Description = null, bool IsVisibleOnMenu = true, int MenuSortOrder = 0,
    string? ShelfLocation = null, string? StorageArea = null,
    string? BrandName = null, List<string>? ImageUrls = null, decimal? DimensionalWeight = null,
    string? ScaleItemCode = null, DateTime? ExpiryDate = null,
    string? PurchaseUnit = null, decimal? PurchaseUnitFactor = null);

public record UpdateProductRequest(
    string Sku, string? Barcode, string Name, Guid? CategoryId, string Unit,
    decimal PurchasePrice, decimal SalePrice, decimal VatRate, decimal MinStock, bool IsActive,
    bool IsService = false,
    string? ImageUrl = null, string? Description = null, bool IsVisibleOnMenu = true, int MenuSortOrder = 0,
    string? ShelfLocation = null, string? StorageArea = null,
    string? BrandName = null, List<string>? ImageUrls = null, decimal? DimensionalWeight = null,
    string? ScaleItemCode = null, DateTime? ExpiryDate = null,
    string? PurchaseUnit = null, decimal? PurchaseUnitFactor = null);

/// <summary>Barkod okutma sonucu. IsScaleBarcode=false ise normal ürün (Quantity=1, UnitPrice=null → satış fiyatı).
/// Terazi barkodunda Quantity ondalık ağırlıktır ya da (fiyat modunda) UnitPrice gömülü tutardan çözülür.</summary>
public record ScanResultDto(ProductDto Product, decimal Quantity, decimal? UnitPrice, bool IsScaleBarcode);

// ---- Toplu fiyat güncelleme (zam) ----
/// <summary>Toplu zam önizleme satırı.</summary>
public record BulkPricePreviewItem(Guid Id, string Name, decimal OldPrice, decimal NewPrice);

/// <summary>Toplu fiyat güncelleme. Target: "Sale"|"Purchase" · Mode: "Percent"|"Amount"
/// Rounding: "None"|"Ninety"(x,90)|"Fifty"(x,50)|"Whole"(tam). Preview=true ise KAYDETMEZ, sadece hesaplar.</summary>
public record BulkPriceUpdateRequest(
    string Target, string Mode, decimal Value,
    Guid? CategoryId = null, List<Guid>? ProductIds = null,
    string? Rounding = null, bool Preview = false);

public record BulkPriceResultDto(int AffectedCount, bool Applied, List<BulkPricePreviewItem> Sample);

// ---- Varyantlar (butik: beden/renk) ----
/// <summary>Varyant öznitelik tanımı (parent'ta saklanır): ör. Name="Beden", Values=["S","M","L"].</summary>
public record VariantAttributeDef(string Name, List<string> Values);

/// <summary>Tek bir varyant satırı girdisi — parent altında oluşturulacak ürün. Boş alanlar parent varsayılanından alınır.</summary>
public record VariantInput(
    string Label,                   // "M · Kırmızı" (zorunlu, gösterim etiketi)
    string? Sku = null,             // boşsa otomatik
    string? Barcode = null,         // boşsa otomatik EAN-13
    decimal? SalePrice = null,      // boşsa parent SalePrice
    decimal? PurchasePrice = null,  // boşsa parent PurchasePrice
    decimal OpeningStock = 0,
    decimal? MinStock = null);      // boşsa parent MinStock

/// <summary>Varyantlı ürün oluşturma: parent şablon + öznitelik tanımları + varyant satırları.</summary>
public record CreateProductWithVariantsRequest(
    string Name,
    Guid? CategoryId,
    string Unit,
    decimal PurchasePrice,
    decimal SalePrice,
    decimal VatRate,
    decimal MinStock,
    List<VariantAttributeDef> Attributes,
    List<VariantInput> Variants,
    string? Description = null,
    string? ImageUrl = null,
    string? BrandName = null,
    bool IsVisibleOnMenu = true);

/// <summary>Varyantlı ürün oluşturma sonucu: parent + oluşturulan varyantlar.</summary>
public record ProductWithVariantsDto(ProductDto Parent, List<ProductDto> Variants);

// ---- Akıllı stok tahminleme (sipariş önerisi) ----
/// <summary>Satış hızına göre tükenme riski taşıyan ürün: kaç gün kaldı + önerilen sipariş miktarı.</summary>
public record ReplenishmentItemDto(
    Guid ProductId, string ProductName, string? CategoryName, string Unit,
    decimal CurrentStock, decimal MinStock, decimal DailyVelocity, int DaysUntilStockout,
    decimal SuggestedReorderQty, decimal SalePrice,
    // Öneriden doğrudan sipariş kurabilmek için alış fiyatı + KDV; OnOrderQuantity = yolda olan (sipariş verilmiş
    // ama henüz gelmemiş) miktar — öneriden düşülür ki aynı mal iki kez ısmarlanmasın.
    decimal PurchasePrice = 0m, decimal VatRate = 0m, decimal OnOrderQuantity = 0m);

// ---- Toplu içe aktarma (CSV/Excel) ----
/// <summary>Tek bir içe aktarma satırı. Kategori ADA göre bulunur/oluşturulur; SKU/barkod boşsa üretilir.</summary>
public record ImportProductRow(
    string? Name, string? Sku, string? Barcode, string? CategoryName, string? Unit,
    decimal? PurchasePrice, decimal? SalePrice, decimal? VatRate, decimal? OpeningStock, decimal? MinStock, bool? IsService);

public record ImportProductsRequest(List<ImportProductRow> Rows);
public record ImportRowError(int Row, string Name, string Reason);
public record ImportResultDto(int Imported, int Skipped, List<ImportRowError> Errors);

public class ProductQuery : PagedQuery
{
    public Guid? CategoryId { get; set; }
    public bool? LowStock { get; set; }
    public bool IncludeInactive { get; set; }
}

// ---- Stok hareketi ----
public record StockMovementDto(
    Guid Id, Guid ProductId, string ProductName, StockMovementType Type,
    decimal Quantity, decimal UnitCost, StockMovementReference Reference,
    string? Note, decimal StockAfter, DateTime CreatedAt);

public record CreateStockMovementRequest(
    Guid ProductId, StockMovementType Type, decimal Quantity, decimal? UnitCost, string? Note);

// ---- Fire / zayi ----
/// <summary>Fire kaydı: stoktan düşer, Reference=Waste + neden ile işaretlenir (maliyeti fire raporunda görünür).</summary>
public record RecordWasteRequest(Guid ProductId, decimal Quantity, WasteReason Reason, string? Note);

// ---- Şubeler arası stok transferi ----
public record StockTransferItem(Guid ProductId, decimal Quantity);
/// <summary>Bir şubeden diğerine ürün(ler) sevk et. Toplam stok DEĞİŞMEZ; yalnız şube dağılımı değişir.</summary>
public record StockTransferRequest(Guid FromBranchId, Guid ToBranchId, List<StockTransferItem> Items, string? Note);
public record StockTransferResultDto(int ItemCount, decimal TotalQuantity, string FromBranchName, string ToBranchName);

// ---- Stok sayımı (envanter) ----
public record StockCountItemRequest(Guid ProductId, decimal CountedQuantity);
public record ApplyStockCountRequest(List<StockCountItemRequest> Items, string? Note);
public record StockCountResultDto(int TotalItems, int AdjustedCount, int UnchangedCount);

// ---- Kalıcı stok sayım oturumu (sunucu-tarafı taslak + geçmiş) ----
public record StockCountSessionItemDto(Guid ProductId, string ProductName, decimal CountedQuantity, decimal CurrentStock);

public record StockCountSessionDto(
    Guid Id, string Status, DateTime CreatedAt, DateTime? AppliedAt,
    string? CreatedByName, int CountedCount, int AdjustedCount,
    List<StockCountSessionItemDto> Items);

/// <summary>Açık sayım taslağını sunucuya kaydeder (mevcut Open oturumu güncellenir, yoksa açılır).</summary>
public record SaveStockCountSessionRequest(List<StockCountItemRequest> Items);

/// <summary>Geçmiş sayım özeti (satır detayı olmadan — liste için).</summary>
public record StockCountHistoryDto(
    Guid Id, string Status, DateTime CreatedAt, DateTime? AppliedAt,
    string? CreatedByName, int CountedCount, int AdjustedCount);

public class StockMovementQuery : PagedQuery
{
    public Guid? ProductId { get; set; }
    public StockMovementType? Type { get; set; }
}

// ---- Servis arayüzleri ----
public interface ICategoryService
{
    Task<List<CategoryDto>> GetAllAsync(CancellationToken ct = default);
    Task<CategoryDto> CreateAsync(CreateCategoryRequest req, CancellationToken ct = default);
    Task<CategoryDto> UpdateAsync(Guid id, UpdateCategoryRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IProductService
{
    Task<PagedResult<ProductDto>> GetAsync(ProductQuery query, CancellationToken ct = default);
    Task<ProductDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ProductDto?> GetByBarcodeAsync(string barcode, CancellationToken ct = default);
    /// <summary>POS okutma: terazi barkodunu çözer (ondalık miktar / gömülü fiyat); değilse normal ürün (miktar 1).</summary>
    Task<ScanResultDto?> ScanAsync(string code, CancellationToken ct = default);
    Task<ProductDto> CreateAsync(CreateProductRequest req, CancellationToken ct = default);
    /// <summary>Varyantlı ürün oluşturur: parent şablon (satılmaz/stoksuz) + her varyant ayrı Product (kendi barkod/stok/fiyat).</summary>
    Task<ProductWithVariantsDto> CreateWithVariantsAsync(CreateProductWithVariantsRequest req, CancellationToken ct = default);
    Task<ImportResultDto> ImportAsync(ImportProductsRequest req, CancellationToken ct = default);
    /// <summary>Toplu fiyat güncelleme (kategori/seçim/tümü). Preview=true ise yalnız hesaplar, kaydetmez.</summary>
    Task<BulkPriceResultDto> BulkUpdatePricesAsync(BulkPriceUpdateRequest req, CancellationToken ct = default);
    Task<ProductDto> UpdateAsync(Guid id, UpdateProductRequest req, CancellationToken ct = default);
    /// <summary>Barkodsuz ürüne benzersiz, dahili (taranabilir EAN-13) barkod üretir ve atar. Barkodu varsa değiştirmez.</summary>
    Task<ProductDto> GenerateBarcodeAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<List<ProductDto>> GetLowStockAsync(CancellationToken ct = default);
    Task<List<StockMovementDto>> GetProductMovementsAsync(Guid productId, CancellationToken ct = default);
}

public interface IStockService
{
    Task<StockMovementDto> CreateMovementAsync(CreateStockMovementRequest req, CancellationToken ct = default);
    /// <summary>Şubeler arası stok transferi: kaynak şubeden düş, hedef şubeye ekle. Toplam stok değişmez.</summary>
    Task<StockTransferResultDto> TransferAsync(StockTransferRequest req, CancellationToken ct = default);
    /// <summary>Fire/zayi kaydı: aktif şubeden düşer, nedeniyle işaretlenir (fire maliyeti raporlanabilir).</summary>
    Task<StockMovementDto> RecordWasteAsync(RecordWasteRequest req, CancellationToken ct = default);
    Task<PagedResult<StockMovementDto>> GetMovementsAsync(StockMovementQuery query, CancellationToken ct = default);
    /// <summary>Sayım: her ürünün sayılan miktarını, fark varsa Adjustment hareketiyle stoğa uygular (değere ayarlar).</summary>
    Task<StockCountResultDto> ApplyStockCountAsync(ApplyStockCountRequest req, CancellationToken ct = default);

    /// <summary>Açık (Open) sayım taslağını döner; yoksa null.</summary>
    Task<StockCountSessionDto?> GetOpenCountSessionAsync(CancellationToken ct = default);
    /// <summary>Açık taslağı kaydeder (mevcut Open oturumu değiştirir, yoksa açar).</summary>
    Task<StockCountSessionDto> SaveCountSessionAsync(SaveStockCountSessionRequest req, CancellationToken ct = default);
    /// <summary>Açık taslağı iptal eder (Cancelled) — varsa.</summary>
    Task DiscardCountSessionAsync(CancellationToken ct = default);
    /// <summary>Uygulanan/iptal edilen geçmiş sayımlar (en yeni önce).</summary>
    Task<List<StockCountHistoryDto>> GetCountHistoryAsync(int limit, CancellationToken ct = default);
}

/// <summary>Akıllı stok tahminleme: son <c>windowDays</c> satış hızına göre <c>horizonDays</c> içinde tükenecek ürünler.</summary>
public interface IReplenishmentService
{
    Task<List<ReplenishmentItemDto>> GetAsync(int windowDays = 30, int horizonDays = 7, int coverDays = 30, CancellationToken ct = default);
}
