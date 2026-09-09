using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Notifications;

/// <summary>
/// Tenant başına proaktif tarama: bildirim merkezine (zil) uyarı düşürür ve gerektiğinde SMS gönderir.
/// Üretici (producer) — bildirim merkezi (#5) + SMS altyapısını (#1) canlandırır:
///  • Düşük/kritik stok (#5 zemin) — sadece zil.
///  • Yaklaşan randevu hatırlatması (#9) — zil + SMS.
///  • Geciken alacak / dunning (#10) — zil + SMS.
/// Tekilleştirme (DedupKey) SMS tekrarını da engeller: SMS yalnız YENİ bildirim oluşunca gider.
/// </summary>
public interface INotificationScanService
{
    Task<int> ScanAsync(CancellationToken ct = default);
}

public sealed class NotificationScanService : INotificationScanService
{
    private readonly IApplicationDbContext _db;
    private readonly INotificationService _notify;
    private readonly ISmsSender _sms;

    public NotificationScanService(IApplicationDbContext db, INotificationService notify, ISmsSender sms)
    {
        _db = db;
        _notify = notify;
        _sms = sms;
    }

    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        var raised = 0;
        raised += await ScanLowStockAsync(ct);
        raised += await ScanExpiryAsync(ct);
        raised += await ScanAppointmentsAsync(ct);
        raised += await ScanOverdueReceivablesAsync(ct);
        raised += await ScanBirthdaysAsync(ct);
        return raised;
    }

    /// <summary>Bugün doğum günü olan müşteriler → zil + kutlama SMS'i (#34). Yılda bir kez (cari başına).</summary>
    private async Task<int> ScanBirthdaysAsync(CancellationToken ct)
    {
        var today = AppTime.Today;
        // Doğum günü takvim tarihidir (girildiği gibi ay/gün eşleşir); yıl önemsiz, timezone dönüşümü gerekmez.
        var celebrants = await _db.Contacts
            .Where(c => c.IsActive && c.Birthday != null
                        && c.Birthday!.Value.Month == today.Month && c.Birthday!.Value.Day == today.Day)
            .Select(c => new { c.Id, c.Name, c.Phone })
            .Take(200)
            .ToListAsync(ct);

        var n = 0;
        foreach (var c in celebrants)
        {
            var id = await _notify.RaiseAsync(new RaiseNotificationRequest(
                Type: "birthday",
                Title: $"🎂 Doğum günü: {c.Name}",
                Message: $"{c.Name} bugün doğum gününü kutluyor — bir kutlama mesajı/indirim iyi olur.",
                Severity: "info",
                Link: "/cariler",
                Icon: "gift",
                DedupKey: $"birthday:{c.Id}:{today.Year}"), ct);
            if (id is null) continue; // bu yıl zaten kutlandı → SMS de gitmez
            n++;

            if (!string.IsNullOrWhiteSpace(c.Phone))
                await _sms.SendAsync(c.Phone!,
                    $"Sayin {c.Name}, dogum gununuz kutlu olsun! Nice mutlu, saglikli yillar dileriz.", ct);
        }
        return n;
    }

    /// <summary>Yaklaşan (≤7 gün) veya geçmiş SKT → zil. Fireyi indirimle satışa çevirmeyi sağlar (market/kafe).</summary>
    private async Task<int> ScanExpiryAsync(CancellationToken ct)
    {
        var today = AppTime.Today;
        var todayKey = today.ToString("yyyy-MM-dd");
        var horizonUtc = AppTime.StartOfDayUtc(today.AddDays(8)); // 7 gün + bugün
        var expiring = await _db.Products
            .Where(p => p.IsActive && !p.IsVariantParent && p.CurrentStock > 0 && p.ExpiryDate != null && p.ExpiryDate < horizonUtc)
            .Select(p => new { p.Id, p.Name, p.ExpiryDate, p.CurrentStock, p.Unit })
            .Take(100)
            .ToListAsync(ct);

        var n = 0;
        foreach (var p in expiring)
        {
            var expLocal = DateOnly.FromDateTime(AppTime.ToLocal(p.ExpiryDate!.Value));
            var expired = expLocal < today;
            var id = await _notify.RaiseAsync(new RaiseNotificationRequest(
                Type: "expiry",
                Title: expired ? $"SKT geçti: {p.Name}" : $"SKT yaklaşıyor: {p.Name}",
                Message: expired
                    ? $"{p.Name} son kullanma tarihi geçti ({expLocal:dd.MM.yyyy}) — {p.CurrentStock:0.##} {p.Unit} stokta."
                    : $"{p.Name} SKT {expLocal:dd.MM.yyyy} — {p.CurrentStock:0.##} {p.Unit} kaldı; indirimle eritmeyi düşünün.",
                Severity: expired ? "critical" : "warning",
                Link: "/stok/urunler",
                Icon: "calendar-clock",
                DedupKey: $"expiry:{p.Id}:{todayKey}"), ct);
            if (id is not null) n++;
        }
        return n;
    }

    /// <summary>Düşük/kritik stok → zil (sadece in-app). Varyant parent'ları (stoksuz şablon) hariç.</summary>
    private async Task<int> ScanLowStockAsync(CancellationToken ct)
    {
        var today = AppTime.Today.ToString("yyyy-MM-dd");
        var low = await _db.Products
            .Where(p => p.IsActive && !p.IsVariantParent && p.MinStock > 0 && p.CurrentStock <= p.MinStock)
            .Select(p => new { p.Id, p.Name, p.CurrentStock, p.MinStock, p.Unit })
            .Take(100)
            .ToListAsync(ct);

        var n = 0;
        foreach (var p in low)
        {
            var critical = p.CurrentStock <= 0;
            var id = await _notify.RaiseAsync(new RaiseNotificationRequest(
                Type: "low_stock",
                Title: critical ? $"Tükendi: {p.Name}" : $"Düşük stok: {p.Name}",
                Message: critical
                    ? $"{p.Name} stokta kalmadı (min {p.MinStock})."
                    : $"{p.Name} azaldı: {p.CurrentStock:0.##} {p.Unit} kaldı (min {p.MinStock}).",
                Severity: critical ? "critical" : "warning",
                Link: "/stok/urunler",
                Icon: "package",
                DedupKey: $"low_stock:{p.Id}:{today}"), ct);
            if (id is not null) n++;
        }
        return n;
    }

    /// <summary>Önümüzdeki 24 saatteki randevular → zil + SMS (bir kez, randevu başına).</summary>
    private async Task<int> ScanAppointmentsAsync(CancellationToken ct)
    {
        var now = AppTime.UtcNow;
        var until = now.AddHours(24);
        var upcoming = await _db.Appointments
            .Where(a => a.Status == AppointmentStatus.Scheduled && a.StartsAt >= now && a.StartsAt <= until)
            .Select(a => new { a.Id, a.CustomerName, a.Phone, a.ServiceName, a.StartsAt, a.BranchId })
            .Take(200)
            .ToListAsync(ct);

        var n = 0;
        foreach (var a in upcoming)
        {
            var local = AppTime.ToLocal(a.StartsAt);
            var whenText = $"{local:dd.MM HH:mm}";
            var id = await _notify.RaiseAsync(new RaiseNotificationRequest(
                Type: "appointment_soon",
                Title: $"Yaklaşan randevu: {a.CustomerName}",
                Message: $"{whenText}" + (string.IsNullOrWhiteSpace(a.ServiceName) ? "" : $" · {a.ServiceName}"),
                Severity: "info",
                Link: "/randevular",
                Icon: "calendar-clock",
                BranchId: a.BranchId,
                DedupKey: $"appt_reminder:{a.Id}"), ct);
            if (id is null) continue; // zaten hatırlatıldı → SMS de gitmez
            n++;

            if (!string.IsNullOrWhiteSpace(a.Phone))
            {
                var svc = string.IsNullOrWhiteSpace(a.ServiceName) ? "randevunuz" : a.ServiceName;
                await _sms.SendAsync(a.Phone!,
                    $"Sayin {a.CustomerName}, {whenText} tarihli {svc} hatirlatmasi. Iyi gunler dileriz.", ct);
            }
        }
        return n;
    }

    /// <summary>Vadesi geçmiş alacaklı cariler → zil + SMS (dunning). Günde bir kez (cari başına).</summary>
    private async Task<int> ScanOverdueReceivablesAsync(CancellationToken ct)
    {
        var today = AppTime.Today;
        var todayStartUtc = AppTime.StartOfDayUtc(today);
        var dayKey = today.ToString("yyyy-MM-dd");

        // Vadesi geçmiş borç hareketi olan carilerin id'leri (Debit = bize borç, DueDate geçmiş).
        var overdueContactIds = await _db.AccountTransactions
            .Where(t => t.Direction == TransactionDirection.Debit && t.DueDate != null && t.DueDate < todayStartUtc)
            .Select(t => t.ContactId)
            .Distinct()
            .ToListAsync(ct);
        if (overdueContactIds.Count == 0) return 0;

        var contacts = await _db.Contacts
            .Where(c => overdueContactIds.Contains(c.Id) && c.Balance > 0m)
            .Select(c => new { c.Id, c.Name, c.Phone, c.Balance })
            .Take(200)
            .ToListAsync(ct);

        var n = 0;
        foreach (var c in contacts)
        {
            var id = await _notify.RaiseAsync(new RaiseNotificationRequest(
                Type: "receivable_overdue",
                Title: $"Vadesi geçen alacak: {c.Name}",
                Message: $"{c.Name} · vadesi geçmiş bakiye {c.Balance:N0} ₺ — tahsilat zamanı.",
                Severity: "warning",
                Link: "/cariler",
                Icon: "alert-triangle",
                DedupKey: $"dunning:{c.Id}:{dayKey}"), ct);
            if (id is null) continue;
            n++;

            if (!string.IsNullOrWhiteSpace(c.Phone))
            {
                await _sms.SendAsync(c.Phone!,
                    $"Sayin {c.Name}, {c.Balance:N0} TL tutarinda vadesi gecmis bakiyeniz bulunmaktadir. Bilginize.", ct);
            }
        }
        return n;
    }
}
