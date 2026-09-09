using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// İşletme-içi sorumluluk izi (tenant şemasında): kritik para/stok eylemlerini KİMİN yaptığını kaydeder.
/// Salt-ekle (append-only) — güncellenmez/silinmez. Platform tarafındaki <see cref="AuditLog"/> ile karıştırılmamalı:
/// o master şemada süper-admin eylemlerini tutar, bu ise işletmenin kendi personel eylemlerini.
/// </summary>
public class AuditEvent : BaseEntity
{
    /// <summary>Eylemi yapan kullanıcı (master DB User.Id — çapraz-şema, gerçek FK yok).</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Eylemi yapanın e-postası (denormalize — master DB'ye join gerekmesin).</summary>
    public string? ActorEmail { get; set; }

    /// <summary>Makine-okunur eylem kodu: "InvoiceVoided", "InvoiceRefunded", "CashTransactionCreated"...</summary>
    public string Action { get; set; } = null!;

    /// <summary>Etkilenen kayıt türü: "Invoice", "FinanceTransaction", "Stock"...</summary>
    public string? TargetType { get; set; }

    public Guid? TargetId { get; set; }

    /// <summary>İnsan-okunur kısa açıklama (fatura no, tutar vb.).</summary>
    public string? Details { get; set; }

    /// <summary>Eylemin yapıldığı şube. Şubeye kilitli bir yönetici BAŞKA şubenin finansal izini görmemeli
    /// (fatura no + tutar sızıntısı); diğer varlıklarla aynı global query filter uygulanır.
    /// Kısıtsız kullanıcının (Owner) yazdığı izlerde null → yalnız kısıtsız kullanıcılar görür.</summary>
    public Guid? BranchId { get; set; }
}
