using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Invoices;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Appointments;

public sealed class AppointmentService : IAppointmentService
{
    private readonly IApplicationDbContext _db;
    private readonly IInvoiceService _invoices;
    private readonly ISmsSender _sms;
    private readonly ICurrentBranch _branch;

    public AppointmentService(IApplicationDbContext db, IInvoiceService invoices, ISmsSender sms, ICurrentBranch branch)
    {
        _db = db;
        _invoices = invoices;
        _sms = sms;
        _branch = branch;
    }

    public async Task<List<AppointmentDto>> GetRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var q = _db.Appointments.Where(a => a.StartsAt >= from && a.StartsAt < to);
        if (_branch.HeaderBranchId is Guid b) q = q.Where(a => a.BranchId == b);
        return await q.OrderBy(a => a.StartsAt)
            .Select(a => new AppointmentDto(
                a.Id, a.CustomerName, a.Phone, a.ServiceName, a.StartsAt, a.DurationMinutes, a.Status, a.Price, a.Note, a.StaffId,
                a.ContactId, a.Contact != null ? a.Contact.Name : null))
            .ToListAsync(ct);
    }

    public async Task<AppointmentDto> CreateAsync(CreateAppointmentRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.CustomerName))
            throw new BusinessRuleException("Müşteri adı zorunlu.");
        if (req.ContactId is Guid rc && !await _db.Contacts.AnyAsync(c => c.Id == rc, ct))
            throw NotFoundException.For("Cari", rc);

        var appt = new Appointment
        {
            ContactId = req.ContactId,
            CustomerName = req.CustomerName.Trim(),
            Phone = req.Phone?.Trim(),
            ServiceName = req.ServiceName?.Trim(),
            StartsAt = req.StartsAt,
            DurationMinutes = req.DurationMinutes <= 0 ? 30 : req.DurationMinutes,
            Price = req.Price,
            Note = req.Note?.Trim(),
            ProductIds = string.IsNullOrWhiteSpace(req.ProductIds) ? null : req.ProductIds.Trim(),
            StaffId = req.StaffId,
            Status = AppointmentStatus.Scheduled,
            BranchId = await _branch.ResolveWriteBranchAsync(ct),
        };
        _db.Appointments.Add(appt);
        await _db.SaveChangesAsync(ct);
        // Navigation Add sonrası dolmaz; DTO'da cari adını gösterebilmek için elle bağla.
        if (appt.ContactId is Guid newCid)
            appt.Contact = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == newCid, ct);

        // Randevu onay SMS'i (telefon varsa). Sağlayıcı yoksa dev'de loglanır; SMS hatası randevuyu düşürmez.
        if (!string.IsNullOrWhiteSpace(appt.Phone))
        {
            try
            {
                var when = appt.StartsAt.ToString("dd.MM HH:mm");
                var msg = $"Randevunuz alındı: {when}{(string.IsNullOrWhiteSpace(appt.ServiceName) ? "" : " · " + appt.ServiceName)}.";
                await _sms.SendAsync(appt.Phone!.Trim(), msg, ct);
            }
            catch { /* SMS başarısız olsa da randevu oluşur */ }
        }

        return Map(appt);
    }

    public async Task<AppointmentDto> SetStatusAsync(Guid id, AppointmentStatus status, CancellationToken ct = default)
    {
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFoundException.For("Randevu", id);
        appt.Status = status;
        await _db.SaveChangesAsync(ct);
        return Map(appt);
    }

    public async Task<AppointmentDto> CollectAsync(Guid id, Guid cashAccountId, PaymentMethod method, CancellationToken ct = default)
    {
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFoundException.For("Randevu", id);
        if (appt.Status == AppointmentStatus.Cancelled)
            throw new BusinessRuleException("İptal edilmiş randevu tahsil edilemez.");
        // İdempotanlık: zaten tahsil edilmiş (Done) randevu ikinci kez tahsil edilemez — aksi halde
        // çift-tık/çift-istek mükerrer fatura ve mükerrer kasa geliri üretir.
        if (appt.Status == AppointmentStatus.Done)
            throw new BusinessRuleException("Bu randevu zaten tahsil edilmiş.");

        var now = DateTime.UtcNow;
        appt.Status = AppointmentStatus.Done;

        if (appt.Price > 0)
        {
            var ids = ParseProductIds(appt.ProductIds);
            var products = ids.Count > 0
                ? await _db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct)
                : new List<Product>();

            if (products.Count > 0)
            {
                // Seçili hizmetlerden satış faturası + tahsilat -> panel + satış raporu + finans hepsine düşer.
                var lines = products.Select(p => new CreateInvoiceLineRequest(p.Id, 1m, p.SalePrice, p.VatRate)).ToList();

                // Cari bağlıysa müşteri indirimi faturada UYGULANIR → tahsilat tutarı da indirimli olmalı.
                // Aksi halde ödeme faturayı aşar ve InvoiceService "Ödeme tutarı fatura tutarını aşamaz" der.
                var rate = 0m;
                if (appt.ContactId is Guid acid)
                {
                    var c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == acid, ct);
                    rate = c is null ? 0m : Math.Clamp(c.DiscountRate, 0m, 100m);
                }
                var grand = products.Sum(p =>
                {
                    var unit = rate > 0 ? Math.Round(p.SalePrice * (1 - rate / 100m), 2) : p.SalePrice;
                    return Math.Round(unit, 2) + Math.Round(unit * p.VatRate / 100m, 2);
                });

                var invReq = new CreateInvoiceRequest(
                    InvoiceType.Sales, appt.ContactId, now, $"Randevu: {appt.CustomerName}", lines,
                    new InvoicePaymentRequest(cashAccountId, grand, method));

                // InvoiceService kendi SaveChanges'inde randevunun Done durumunu da yazar (paylaşılan DbContext).
                // Satış, tahsil eden kişiye DEĞİL randevuya atanan personele yazılsın (personel bazlı rapor/prim).
                // appt.StaffId yoksa (personel atanmamış) InvoiceService mevcut kullanıcıya düşer.
                await _invoices.CreateAsync(invReq, ct, appt.StaffId);
                return Map(appt);
            }

            // Ürün bağlı değil (elle yazılmış hizmet) -> doğrudan kasa geliri (panel + finans raporu).
            var cash = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == cashAccountId, ct)
                ?? throw NotFoundException.For("Kasa", cashAccountId);
            cash.Balance += appt.Price;
            _db.FinanceTransactions.Add(new FinanceTransaction
            {
                CashAccountId = cash.Id,
                Type = FinanceType.Income,
                Category = "Randevu / Hizmet",
                Amount = appt.Price,
                Description = $"Randevu: {appt.CustomerName}",
                PaymentMethod = method,
                ContactId = appt.ContactId, // cari bağlıysa hareket müşterinin geçmişinde de görünsün
                BranchId = appt.BranchId, // randevunun şubesi — yoksa şube filtresi bu geliri gizlerdi
                Date = now,
            });
        }

        await _db.SaveChangesAsync(ct);
        return Map(appt);
    }

    private static List<Guid> ParseProductIds(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new List<Guid>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(s => Guid.TryParse(s, out var g) ? (Guid?)g : null)
                 .Where(g => g.HasValue).Select(g => g!.Value).ToList();

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFoundException.For("Randevu", id);
        _db.Appointments.Remove(appt);
        await _db.SaveChangesAsync(ct);
    }

    private static AppointmentDto Map(Appointment a) => new(
        a.Id, a.CustomerName, a.Phone, a.ServiceName, a.StartsAt, a.DurationMinutes, a.Status, a.Price, a.Note, a.StaffId,
        a.ContactId, a.Contact?.Name);
}
