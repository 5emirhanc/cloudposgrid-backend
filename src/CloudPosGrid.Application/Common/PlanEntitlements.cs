using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Common;

/// <summary>
/// Bir işletmenin planına (ve deneme durumuna) göre hangi özelliklere/limitlere sahip olduğunun
/// TEK doğruluk kaynağı. Hem backend zorlaması hem de UserDto üzerinden frontend kilitleri bunu kullanır.
///
/// Karar (kullanıcı onaylı):
/// - Deneme (Trial): sınırlı — en çok 30 ürün, tek kullanıcı; personel/gelişmiş rapor/çok şube KAPALI.
/// - Profesyonel (Pro): tüm çekirdek özellikler, sınırsız ürün, tek kullanıcı, temel raporlar.
/// - Kurumsal (Enterprise): çekirdek + personel/rol + gelişmiş rapor + masadan sipariş + 3 şubeye kadar.
/// - Zincir (Chain): Kurumsal'daki her şey + SINIRSIZ şube + pazaryeri entegrasyonu.
/// </summary>
/// <param name="MaxProducts">Ürün üst sınırı; null = sınırsız.</param>
/// <param name="MaxUsers">Kullanıcı üst sınırı; null = sınırsız.</param>
/// <param name="StaffManagement">Personel &amp; rol yönetimi (çoklu kullanıcı, PIN) açık mı?</param>
/// <param name="AdvancedReports">Gelişmiş raporlar &amp; dışa aktarma açık mı?</param>
/// <param name="MultiBranch">Birden fazla şube açılabilir mi? (kaba UI kilidi; sayısal sınır MaxBranches).</param>
/// <param name="MaxBranches">Toplam şube üst sınırı (varsayılan şube dahil); null = sınırsız.
/// Profesyonel 1, Kurumsal 3, Zincir sınırsız.</param>
/// <param name="QrOrdering">QR menüden MASADAN SİPARİŞ açık mı? Kurumsal + Zincir. Profesyonel'de
/// QR menü yalnızca görüntülenir (sipariş verilemez).</param>
/// <param name="MarketplaceIntegration">Pazaryeri (Trendyol vb.) entegrasyonu açık mı? Yalnız Zincir.</param>
/// <param name="LoyaltyProgram">Sadakat / puan sistemi açık mı? Kurumsal + Zincir.</param>
/// <param name="SmartReplenishment">Akıllı sipariş önerisi (tükenme tahmini) açık mı? Yalnız Zincir.</param>
/// <param name="AiAssistant">Doğal dil AI asistanı (satış/kâr sorularını yanıtlar) açık mı? Yalnız Zincir.</param>
/// <param name="MarketingTools">Pazarlama/müşteri araçları — kampanyalar, otomasyon kuralları, hediye çekleri.
/// Kurumsal + Zincir. (Analitik ayrı: <see cref="AdvancedReports"/> bayrağına bağlıdır.)</param>
public sealed record PlanEntitlements(
    int? MaxProducts,
    int? MaxUsers,
    bool StaffManagement,
    bool AdvancedReports,
    bool MultiBranch,
    int? MaxBranches,
    bool QrOrdering,
    bool MarketplaceIntegration,
    bool LoyaltyProgram,
    bool SmartReplenishment,
    bool AiAssistant,
    bool MarketingTools)
{
    public static PlanEntitlements For(TenantPlan plan, TenantStatus status)
    {
        // Deneme sürümü plandan bağımsız olarak sınırlıdır (tek şube; masadan sipariş/pazaryeri/AI kapalı).
        if (status == TenantStatus.Trial)
            return new(MaxProducts: 30, MaxUsers: 1, StaffManagement: false, AdvancedReports: false, MultiBranch: false, MaxBranches: 1, QrOrdering: false, MarketplaceIntegration: false, LoyaltyProgram: false, SmartReplenishment: false, AiAssistant: false, MarketingTools: false);

        return plan switch
        {
            // Zincir: her şey + sınırsız şube + pazaryeri + akıllı sipariş önerisi + AI asistan + pazarlama araçları.
            TenantPlan.Chain => new(MaxProducts: null, MaxUsers: null, StaffManagement: true, AdvancedReports: true, MultiBranch: true, MaxBranches: null, QrOrdering: true, MarketplaceIntegration: true, LoyaltyProgram: true, SmartReplenishment: true, AiAssistant: true, MarketingTools: true),
            // Kurumsal: personel/rol + gelişmiş rapor/analitik + masadan sipariş + pazarlama araçları; 3 şubeye kadar; pazaryeri/akıllı öneri/AI YOK (Zincir'e özel).
            TenantPlan.Enterprise => new(MaxProducts: null, MaxUsers: null, StaffManagement: true, AdvancedReports: true, MultiBranch: true, MaxBranches: 3, QrOrdering: true, MarketplaceIntegration: false, LoyaltyProgram: true, SmartReplenishment: false, AiAssistant: false, MarketingTools: true),
            // Pro (Profesyonel) ve eski Starter: tam çekirdek, tek kullanıcı, tek şube, temel raporlar.
            // QR menü görüntüleme var; masadan sipariş + çok şube + pazaryeri + AI + analitik/pazarlama araçları YOK.
            _ => new(MaxProducts: null, MaxUsers: 1, StaffManagement: false, AdvancedReports: false, MultiBranch: false, MaxBranches: 1, QrOrdering: false, MarketplaceIntegration: false, LoyaltyProgram: false, SmartReplenishment: false, AiAssistant: false, MarketingTools: false),
        };
    }
}
