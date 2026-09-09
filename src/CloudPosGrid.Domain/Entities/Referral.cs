using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Referans (tavsiye) programı kaydı: mevcut bir cari (<see cref="ReferrerContactId"/>) için
/// benzersiz bir tavsiye <see cref="Code"/> üretilir; bu kodla gelen yeni müşteri
/// <see cref="ReferredContactId"/> olarak bağlanır ve tavsiye eden <see cref="RewardAmount"/>
/// kadar ödül kazanır. Program işletme genelidir; şube izolasyonu yoktur (BranchId yok).
/// </summary>
public class Referral : BaseEntity
{
    /// <summary>Tavsiye eden cari (mevcut müşteri).</summary>
    public Guid ReferrerContactId { get; set; }

    /// <summary>Tavsiye edene özel benzersiz kod (Guid'den türetilen 8 hane, büyük harf).</summary>
    public string Code { get; set; } = null!;

    /// <summary>Bu kodla gelen yeni cari (henüz gelmediyse null).</summary>
    public Guid? ReferredContactId { get; set; }

    /// <summary>Tavsiye edene verilecek ödül tutarı (₺).</summary>
    public decimal RewardAmount { get; set; }

    /// <summary>Durum. İzinli değerler: "Pending" | "Rewarded" | "Cancelled".</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>Serbest not.</summary>
    public string? Note { get; set; }
}
