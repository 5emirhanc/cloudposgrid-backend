using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Bayi / yeniden-satıcı (#25) — CloudPosGrid'i son işletmelere satan iş ortağı. Kendi girişiyle
/// müşteri işletmelerini onboard eder ve panelinden yönetir. Süper-admin'in KENDİ getirdiği işletmelere
/// scope'lanmış hali (yalnız <see cref="Tenant.DealerId"/> = bu bayi olan tenant'ları görür). public/master şemada.
/// </summary>
public class Dealer : BaseEntity
{
    public string Name { get; set; } = null!;

    /// <summary>Bayi giriş e-postası (tüm bayiler arasında benzersiz).</summary>
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;

    /// <summary>Atıf kodu — işletme kaydında bayiye bağlamak için (benzersiz).</summary>
    public string Code { get; set; } = null!;

    /// <summary>Komisyon oranı (%): getirdiği aktif aboneliklerden bayinin payı (off-platform mahsuplaşma).</summary>
    public decimal CommissionRate { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Bu bayinin onboard ettiği müşteri işletmeleri.</summary>
    public ICollection<Tenant> Tenants { get; set; } = new List<Tenant>();
}
