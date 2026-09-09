using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Ürün/stok kartı (tenant şemasında).</summary>
public class Product : BaseEntity
{
    public string Sku { get; set; } = null!;
    public string? Barcode { get; set; }
    public string Name { get; set; } = null!;

    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    public string Unit { get; set; } = "adet";

    // ---- Depo yeri (özellikle tamirci/servis: parçanın nerede olduğu) ----
    /// <summary>Ürünün bulunduğu raf (ör. "A-3").</summary>
    public string? ShelfLocation { get; set; }
    /// <summary>Ürünün bulunduğu depo bölgesi/alanı (ör. "Ön depo").</summary>
    public string? StorageArea { get; set; }

    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal VatRate { get; set; } = 20m;

    public decimal CurrentStock { get; set; }
    public decimal MinStock { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>En yakın son kullanma tarihi (SKT). Market/kafe için: yaklaşan/geçen SKT bildirim üretir.
    /// (Parti bazlı tam lot takibi ileri sürümde; küçük işletme için ürün başına en yakın SKT pratik yeterli.)</summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Alış ölçü birimi (ör. "koli") — satış birimi <see cref="Unit"/>'ten farklıysa. null = aynı birim.</summary>
    public string? PurchaseUnit { get; set; }
    /// <summary>1 alış birimi kaç satış birimidir (ör. 1 koli = 24 adet → 24). Alışta miktar bu katsayıyla stok birimine çevrilir.</summary>
    public decimal? PurchaseUnitFactor { get; set; }

    /// <summary>Hizmet kalemi mi? (kuaför hizmeti, tamirci işçiliği gibi) — stok takibi yapılmaz.</summary>
    public bool IsService { get; set; }

    // ---- Medya / Menü ----
    /// <summary>Birincil görsel (POS/menü küçük görseli). Pazaryeri ilanında ilk sıradaki görsel.</summary>
    public string? ImageUrl { get; set; }
    /// <summary>Ek görseller (pazaryeri ilanı için; Trendyol 8'e kadar kabul eder). İlan görselleri = [ImageUrl] + bunlar.</summary>
    public List<string> ImageUrls { get; set; } = new();
    public string? Description { get; set; }
    public bool IsVisibleOnMenu { get; set; } = true;
    public int MenuSortOrder { get; set; }

    // ---- Pazaryeri ----
    /// <summary>Marka adı (pazaryeri ilanı için; Trendyol kayıtlı marka ister, ilan sihirbazında brandId'ye eşlenir).</summary>
    public string? BrandName { get; set; }

    /// <summary>Desi / hacimsel ağırlık (pazaryeri kargo fiyatı için; Trendyol dimensionalWeight). Boş/0 ise ilanda 1 varsayılır.</summary>
    public decimal? DimensionalWeight { get; set; }

    /// <summary>Terazi barkoduna gömülü ürün kodu (ör. "12345"). Doluysa bu ürün TARTILABİLİR kalemdir:
    /// markette terazinin bastığı etiket okutulunca miktar/fiyat barkoddan çözülür. Boşsa normal ürün.
    /// Şablon (ön ek, hane sayıları, ondalık) tenant genelinde ayarlarda tutulur — terazi cihazı tektir.</summary>
    public string? ScaleItemCode { get; set; }

    // ---- Varyantlar (butik: beden/renk) ----
    /// <summary>Bu satır bir varyantsa bağlı olduğu ana ürün (parent). Gerçek FK yok — gruplama anahtarı.</summary>
    public Guid? ParentProductId { get; set; }
    /// <summary>Bu satır bir "varyant şablonu" (parent) mı? Parent DOĞRUDAN satılmaz/stoklanmaz; stok varyantlardadır.</summary>
    public bool IsVariantParent { get; set; }
    /// <summary>Varyant seçenek etiketi (ör. "M · Kırmızı"). Yalnız varyant satırlarında.</summary>
    public string? VariantValues { get; set; }
    /// <summary>Parent'ta: varyant öznitelik tanımları JSON — [{"name":"Beden","values":["S","M","L"]},...].</summary>
    public string? VariantAttributesJson { get; set; }

    /// <summary>Kritik stok seviyesinin altında mı?</summary>
    public bool IsLowStock => CurrentStock <= MinStock;

    public ICollection<StockMovement> Movements { get; set; } = new List<StockMovement>();
}
