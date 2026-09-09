using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Hediye çeki (gift card): önceden bakiye yüklenmiş, koda bağlı değer taşıyıcı.
/// Akış: kesim (issue) → bakiye sorgu → manuel harcama (redeem) → bakiye bitince "Used".
/// Bu sürümde POS ödeme entegrasyonu YOKTUR (yalnız kesim + bakiye + manuel redeem).
/// Şube izolasyonu YOK — çek herhangi bir şubede kullanılabilir (BranchId eklenmez).
/// </summary>
public class GiftCard : BaseEntity
{
    /// <summary>Benzersiz çek kodu (Guid'den türetilmiş 12 haneli sayısal değer).</summary>
    public string Code { get; set; } = null!;

    /// <summary>Kesim anındaki başlangıç bakiyesi.</summary>
    public decimal InitialBalance { get; set; }

    /// <summary>Kalan kullanılabilir bakiye.</summary>
    public decimal Balance { get; set; }

    /// <summary>Yaşam döngüsü. İzinli değerler: "Active" | "Used" | "Cancelled".</summary>
    public string Status { get; set; } = "Active";

    /// <summary>İlgili cari (opsiyonel — çek belirli bir müşteriye bağlıysa).</summary>
    public Guid? ContactId { get; set; }

    /// <summary>Son kullanma tarihi (opsiyonel). Geçmişse harcamaya izin verilmez.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Serbest not (opsiyonel).</summary>
    public string? Note { get; set; }
}
