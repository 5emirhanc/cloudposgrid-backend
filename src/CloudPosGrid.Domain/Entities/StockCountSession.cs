using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Stok sayım oturumu (tenant şemasında). Sayım taslağı SUNUCUDA tutulur → cihaz/kullanıcı arası
/// devam edilebilir; uygulanınca (Applied) geçmiş kaydı olur. Aynı anda EN FAZLA bir Open oturum
/// (yeni taslak kaydı mevcut Open'ı günceller).
/// </summary>
public class StockCountSession : BaseEntity
{
    public StockCountStatus Status { get; set; } = StockCountStatus.Open;

    public Guid? BranchId { get; set; }

    /// <summary>Sayımı başlatan/uygulayan kullanıcı (master DB User.Id — çapraz-şema, FK yok).</summary>
    public Guid? CreatedByUserId { get; set; }
    /// <summary>Kim yaptı — denormalize (master DB'ye join gerekmesin; audit deseniyle tutarlı).</summary>
    public string? CreatedByName { get; set; }

    public DateTime? AppliedAt { get; set; }

    /// <summary>Sayılan ürün adedi (uygulama anındaki değer).</summary>
    public int CountedCount { get; set; }
    /// <summary>Stoğu fiilen değişen (düzeltilen) ürün adedi — uygulamada set edilir.</summary>
    public int AdjustedCount { get; set; }

    public ICollection<StockCountSessionItem> Items { get; set; } = new List<StockCountSessionItem>();
}

/// <summary>Sayım oturumundaki tek ürün satırı: sayılan miktar + sayım anındaki sistem stoğu.</summary>
public class StockCountSessionItem : BaseEntity
{
    public Guid SessionId { get; set; }
    public StockCountSession Session { get; set; } = null!;

    public Guid ProductId { get; set; }
    public decimal CountedQuantity { get; set; }
    /// <summary>Bu satır kaydedildiğinde sistemdeki stok (referans/geçmiş için).</summary>
    public decimal SystemQuantitySnapshot { get; set; }
}
