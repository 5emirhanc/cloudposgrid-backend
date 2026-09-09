using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Appointments;

public record AppointmentDto(
    Guid Id, string CustomerName, string? Phone, string? ServiceName,
    DateTime StartsAt, int DurationMinutes, AppointmentStatus Status, decimal Price, string? Note, Guid? StaffId,
    Guid? ContactId, string? ContactName);

public record CreateAppointmentRequest(
    string CustomerName, string? Phone, string? ServiceName,
    DateTime StartsAt, int DurationMinutes, decimal Price, string? Note, string? ProductIds = null, Guid? StaffId = null,
    // Cari bağı: seçilirse tahsilat faturası bu cariye kesilir (ekstre/bakiye/indirim/puan).
    Guid? ContactId = null);

public record UpdateAppointmentStatusRequest(AppointmentStatus Status);

/// <summary>Randevuyu tamamla + tahsil et: seçili hizmetlerden satış faturası (ürün yoksa kasa geliri) oluşturur.</summary>
public record CollectAppointmentRequest(Guid CashAccountId, PaymentMethod Method);

public interface IAppointmentService
{
    Task<List<AppointmentDto>> GetRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<AppointmentDto> CreateAsync(CreateAppointmentRequest req, CancellationToken ct = default);
    Task<AppointmentDto> SetStatusAsync(Guid id, AppointmentStatus status, CancellationToken ct = default);
    Task<AppointmentDto> CollectAsync(Guid id, Guid cashAccountId, PaymentMethod method, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
