using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Bayiye YAPILAN ödeme (hakediş mahsuplaşması). Süper-admin bayiye para gönderdiğinde kaydeder.
///
/// Bayinin bakiyesi = hak ettiği toplam komisyon (<see cref="TenantPayment.CommissionAmount"/>
/// toplamı) − buradaki ödemelerin toplamı. Bu kayıt olmadan "bu bayiye ne kadar borçluyuz"
/// sorusunun cevabı hiçbir yerde yoktu.
/// </summary>
public class DealerPayout : BaseEntity
{
    public Guid DealerId { get; set; }
    public Dealer Dealer { get; set; } = null!;

    /// <summary>Bayiye gönderilen tutar (TL).</summary>
    public decimal Amount { get; set; }

    public DateTime PaidAt { get; set; }

    /// <summary>Açıklama — havale referansı, dönem bilgisi vb.</summary>
    public string? Note { get; set; }
}
