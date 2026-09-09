using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Randevu (kuaför/güzellik). Müşteri + hizmet + zaman.</summary>
public class Appointment : BaseEntity
{
    public string CustomerName { get; set; } = null!;
    public string? Phone { get; set; }
    public string? ServiceName { get; set; }
    public DateTime StartsAt { get; set; }
    public int DurationMinutes { get; set; } = 30;
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;
    public decimal Price { get; set; }
    public string? Note { get; set; }

    /// <summary>Seçilen hizmet ürünlerinin Id'leri (CSV). Tahsilatta satış faturası bu ürünlerden oluşturulur.</summary>
    public string? ProductIds { get; set; }

    /// <summary>Randevunun atandığı personel (master DB User.Id). Çapraz-şema olduğundan gerçek FK yok
    /// (StockMovement.CreatedBy gibi); ad gösterimi personel listesinden istemcide eşleştirilir. Atanmamışsa null.</summary>
    public Guid? StaffId { get; set; }

    /// <summary>Randevunun bağlı olduğu cari. Seçilirse tahsilat faturası bu cariye kesilir
    /// (ekstre + bakiye + müşteri indirimi + sadakat puanı çalışır). Seçilmezse CustomerName
    /// serbest metin olarak kalır — geriye dönük uyum.</summary>
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    public Guid? BranchId { get; set; }
}
