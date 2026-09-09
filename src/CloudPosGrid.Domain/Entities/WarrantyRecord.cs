using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Servis/garanti kaydı: satılan bir ürünün garanti takibi. Müşteri + ürün + satın alma tarihi
/// ve garanti süresi (ay) tutulur; garanti bitiş tarihi ve süresinin dolup dolmadığı DTO'da hesaplanır.
/// </summary>
public class WarrantyRecord : BaseEntity
{
    /// <summary>Müşteri adı (serbest metin). Cari bağlıysa cari adıyla eşleşebilir.</summary>
    public string CustomerName { get; set; } = null!;

    /// <summary>Bağlı cari (opsiyonel). Seçilirse müşteri cari kartıyla ilişkilendirilir; yoksa yalnız CustomerName kullanılır.</summary>
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    /// <summary>Garanti kapsamındaki ürün adı (serbest metin — satılan ürün stoğa bağlı olmayabilir).</summary>
    public string ProductName { get; set; } = null!;

    /// <summary>Ürün seri numarası (opsiyonel).</summary>
    public string? SerialNo { get; set; }

    /// <summary>Satın alma tarihi — garanti bu tarihten itibaren başlar.</summary>
    public DateTime PurchaseDate { get; set; }

    /// <summary>Garanti süresi (ay). Bitiş tarihi = PurchaseDate + WarrantyMonths ay.</summary>
    public int WarrantyMonths { get; set; }

    /// <summary>Serbest not (arıza/servis açıklaması vb.).</summary>
    public string? Note { get; set; }

    /// <summary>Kaydın ait olduğu şube (çok-şube izolasyonu). null = şube-bağımsız.</summary>
    public Guid? BranchId { get; set; }
}
