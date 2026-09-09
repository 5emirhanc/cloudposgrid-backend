using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Yeni bir işletme için PostgreSQL şeması oluşturur, tabloları kurar ve sektöre göre
/// varsayılan kayıtları (kasa, kategoriler, hizmet kalemleri, ayarlar) seed'ler.
/// </summary>
public interface ITenantProvisioner
{
    /// <param name="demoData">true ise sektör seed'i yerine zengin demo verisi (ürün/masa/cari/satış geçmişi) yüklenir.</param>
    Task ProvisionAsync(Guid tenantId, string schemaName, string companyName, BusinessType businessType, bool demoData = false, CancellationToken ct = default);
}
