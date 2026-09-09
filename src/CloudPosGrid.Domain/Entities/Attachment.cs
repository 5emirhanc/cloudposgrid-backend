using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Genel dosya/ek: herhangi bir kayda (cari, fatura, ürün, sipariş, garanti vb.) iliştirilen dosya URL'i + metadata.
/// Dosyanın kendisi mevcut yükleme servisiyle saklanır (api/uploads/image → erişim URL'i döner); bu satır YALNIZ
/// o URL'in metadata'sını (sahiplik, ad, içerik tipi, not) tutar — dosya içeriğini saklamaz.
/// Sahiplik <see cref="OwnerType"/> + <see cref="OwnerId"/> ile polimorfiktir (çapraz-modül olduğundan gerçek FK yok).
/// Şube izolasyonu gerekmez (BranchId yok) — ek, sahip kaydının şubesini miras alır.
/// </summary>
public class Attachment : BaseEntity
{
    /// <summary>Sahip kayıt tipi (izinli: Contact | Invoice | Product | Order | Warranty).</summary>
    public string OwnerType { get; set; } = null!;

    /// <summary>Sahip kaydın kimliği (ilgili tablonun Id'si; polimorfik olduğundan gerçek FK yoktur).</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Dosyanın erişim URL'i (yükleme servisinin döndürdüğü adres).</summary>
    public string Url { get; set; } = null!;

    /// <summary>Kullanıcıya gösterilecek dosya adı.</summary>
    public string FileName { get; set; } = null!;

    /// <summary>MIME içerik tipi (ör. image/png, application/pdf). Opsiyonel.</summary>
    public string? ContentType { get; set; }

    /// <summary>Ek açıklama/not. Opsiyonel.</summary>
    public string? Note { get; set; }
}
