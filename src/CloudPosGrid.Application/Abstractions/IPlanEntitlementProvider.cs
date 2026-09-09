using CloudPosGrid.Application.Common;

namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Geçerli isteğin işletmesi için plan yetkilerini (entitlements) sağlar. Planı master'dan taze okur
/// (kısa süre cache'lenir) — böylece admin paketi yükseltince kısa sürede yansır, JWT bayatlığı olmaz.
/// </summary>
public interface IPlanEntitlementProvider
{
    Task<PlanEntitlements> GetAsync(CancellationToken ct = default);

    /// <summary>Bir işletmenin cache'lenmiş yetkilerini düşürür — plan/durum değişince (admin aktivasyon,
    /// uzatma, askıya alma, iptal) ANINDA yansısın; aksi halde TTL kadar bayat yetki servis edilir.
    /// NOT: bellek-içi cache yalnız BU örneği temizler. İkinci bir API örneği eklenmeden önce dağıtık
    /// invalidasyon gerekir (Redis şart değil; Postgres LISTEN/NOTIFY yeterli).</summary>
    void Invalidate(Guid tenantId);
}
