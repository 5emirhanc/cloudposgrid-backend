using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Notifications;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Automation;

public interface IAutomationEngine
{
    /// <summary>Aktif kuralları değerlendirir ve eşleşenlerin eylemini çalıştırır. Tetiklenen eylem sayısını döner.</summary>
    Task<int> RunAsync(CancellationToken ct = default);
}

/// <summary>
/// Kural motoru ÇALIŞTIRICISI (if-this-then-that). Kullanıcının tanımladığı <see cref="Domain.Entities.AutomationRule"/>
/// kayıtlarını periyodik olarak değerlendirir ve eşleşen her olay için eylemi (bildirim/SMS/e-posta) uygular.
///
/// TEKİLLEŞTİRME (kritik): her tetiklenen kural-olay çifti için ÖNCE bir bildirim kaydı açılır
/// (<c>DedupKey = auto:{ruleId}:{olayAnahtarı}:{gün}</c>). <see cref="INotificationService.RaiseAsync"/> aynı anahtarı
/// ikinci kez görürse null döner → o tur eylem TEKRAR ÇALIŞTIRILMAZ. Böylece bildirim kaydı aynı zamanda
/// "bu kural bu olay için çalıştı" defteri olur; SMS/e-posta da mükerrer gitmez (tarama 60 dk'da bir döner).
///
/// İZOLASYON: her kural kendi try/catch'inde çalışır — bozuk JSON'lu ya da hatalı tek bir kural diğerlerini durdurmaz.
/// Kural tanımları işletme genelidir (AutomationRule'da BranchId yok), bu yüzden motor da tenant geneli okur.
///
/// NOT — "task" eylemi: ayrı bir görev/todo modülü henüz yok. Bu eylem, yapılacak iş olarak bildirim merkezine
/// düşer (kaybolmaz); gerçek görev kaydı modülü eklendiğinde buraya bağlanır.
/// </summary>
public sealed class AutomationEngine : IAutomationEngine
{
    private readonly IApplicationDbContext _db;
    private readonly INotificationService _notify;
    private readonly ISmsSender _sms;
    private readonly IEmailSender _email;

    public AutomationEngine(IApplicationDbContext db, INotificationService notify, ISmsSender sms, IEmailSender email)
    {
        _db = db;
        _notify = notify;
        _sms = sms;
        _email = email;
    }

    /// <summary>Tek turda bir kuralın üretebileceği en fazla eylem (kaçak kural koruması).</summary>
    private const int MaxPerRule = 50;

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var rules = await _db.AutomationRules
            .Where(r => r.IsActive)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        if (rules.Count == 0) return 0;

        var fired = 0;
        foreach (var rule in rules)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                fired += rule.TriggerType switch
                {
                    "low_stock" => await RunLowStockAsync(rule, ct),
                    "overdue_receivable" => await RunOverdueAsync(rule, ct),
                    "appointment_soon" => await RunAppointmentAsync(rule, ct),
                    "daily_summary" => await RunDailySummaryAsync(rule, ct),
                    _ => 0, // bilinmeyen tetikleyici (ileri sürümden kalma) → sessizce atla
                };
            }
            catch (Exception)
            {
                // Tek kuralın hatası turu düşürmesin; diğer kurallar çalışmaya devam etsin.
            }
        }
        return fired;
    }

    // ---- Tetikleyiciler ----

    /// <summary>Stoğu eşiğin altına düşen ürünler. Eşik: ConditionJson {"threshold": N} — yoksa ürünün MinStock'u.</summary>
    private async Task<int> RunLowStockAsync(Domain.Entities.AutomationRule rule, CancellationToken ct)
    {
        var threshold = ReadDecimal(rule.ConditionJson, "threshold");

        var q = _db.Products.Where(p => p.IsActive && !p.IsVariantParent);
        q = threshold is decimal t
            ? q.Where(p => p.CurrentStock <= t)
            : q.Where(p => p.MinStock > 0 && p.CurrentStock <= p.MinStock);

        var items = await q
            .OrderBy(p => p.CurrentStock)
            .Take(MaxPerRule)
            .Select(p => new { p.Id, p.Name, p.CurrentStock, p.MinStock, p.Unit })
            .ToListAsync(ct);

        var n = 0;
        foreach (var p in items)
        {
            var msg = $"{p.Name} azaldı: {p.CurrentStock:0.##} {p.Unit} kaldı" +
                      (threshold is decimal th ? $" (kural eşiği {th:0.##})." : $" (min {p.MinStock:0.##}).");
            if (await FireAsync(rule, key: $"{p.Id}", title: $"Otomasyon: {rule.Name}", message: msg,
                    link: "/stok/urunler", icon: "package", severity: p.CurrentStock <= 0 ? "critical" : "warning",
                    contactPhone: null, ct)) n++;
        }
        return n;
    }

    /// <summary>Vadesi geçmiş alacaklar. Gecikme gün eşiği: ConditionJson {"days": N} (varsayılan 1).</summary>
    private async Task<int> RunOverdueAsync(Domain.Entities.AutomationRule rule, CancellationToken ct)
    {
        var days = (int)(ReadDecimal(rule.ConditionJson, "days") ?? 1m);
        if (days < 0) days = 0;
        var cutoff = DateTime.UtcNow.AddDays(-days);

        // Vadesi geçmiş borç (Debit) hareketi olan cariler — bakiyesi hâlâ alacaklı olanlar.
        var overdue = await _db.AccountTransactions
            .Where(x => x.Direction == TransactionDirection.Debit && x.DueDate != null && x.DueDate < cutoff)
            .Select(x => x.ContactId)
            .Distinct()
            .Take(MaxPerRule)
            .ToListAsync(ct);
        if (overdue.Count == 0) return 0;

        var contacts = await _db.Contacts
            .Where(c => c.IsActive && c.Balance > 0 && overdue.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.Balance, c.Phone })
            .ToListAsync(ct);

        var n = 0;
        foreach (var c in contacts)
        {
            var msg = $"{c.Name} — vadesi {days} günü aşan {c.Balance:0.00} ₺ alacak var.";
            if (await FireAsync(rule, key: $"{c.Id}", title: $"Otomasyon: {rule.Name}", message: msg,
                    link: "/cariler", icon: "alert-circle", severity: "warning",
                    contactPhone: c.Phone, ct)) n++;
        }
        return n;
    }

    /// <summary>Yaklaşan randevular. Ufuk: ConditionJson {"hours": N} (varsayılan 24).</summary>
    private async Task<int> RunAppointmentAsync(Domain.Entities.AutomationRule rule, CancellationToken ct)
    {
        var hours = (int)(ReadDecimal(rule.ConditionJson, "hours") ?? 24m);
        if (hours is < 1 or > 720) hours = 24;
        var now = DateTime.UtcNow;
        var until = now.AddHours(hours);

        var appts = await _db.Appointments
            .Where(a => a.Status == AppointmentStatus.Scheduled && a.StartsAt >= now && a.StartsAt <= until)
            .OrderBy(a => a.StartsAt)
            .Take(MaxPerRule)
            .Select(a => new { a.Id, a.CustomerName, a.ServiceName, a.StartsAt, a.Phone })
            .ToListAsync(ct);

        var n = 0;
        foreach (var a in appts)
        {
            var local = AppTime.ToLocal(a.StartsAt);
            var msg = $"{a.CustomerName} — {local:dd.MM HH:mm} · {a.ServiceName ?? "randevu"}";
            if (await FireAsync(rule, key: $"{a.Id}", title: $"Otomasyon: {rule.Name}", message: msg,
                    link: "/randevular", icon: "calendar-days", severity: "info",
                    contactPhone: a.Phone, ct)) n++;
        }
        return n;
    }

    /// <summary>Günlük özet: bugünkü satış adedi ve cirosu — gün başına BİR kez.</summary>
    private async Task<int> RunDailySummaryAsync(Domain.Entities.AutomationRule rule, CancellationToken ct)
    {
        var (from, to) = AppTime.DayRangeUtc(AppTime.Today);
        var sales = await _db.Invoices
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled
                        && i.Date >= from && i.Date < to)
            .Select(i => new { i.Subtotal })
            .ToListAsync(ct);

        var msg = sales.Count == 0
            ? "Bugün henüz satış yok."
            : $"Bugün {sales.Count} satış, {sales.Sum(s => s.Subtotal):0.00} ₺ ciro (KDV hariç).";

        // Anahtar sabit ("gun") → FireAsync gün damgası eklediği için günde tek kayıt.
        return await FireAsync(rule, key: "gun", title: $"Otomasyon: {rule.Name}", message: msg,
            link: "/raporlar", icon: "bar-chart-3", severity: "info", contactPhone: null, ct) ? 1 : 0;
    }

    // ---- Eylem uygulayıcı ----

    /// <summary>
    /// Bir kural-olay çifti için eylemi uygular. ÖNCE bildirim kaydı açılır (tekilleştirme kilidi); kayıt zaten
    /// varsa (aynı gün aynı kural+olay) hiçbir şey yapılmaz ve false döner — SMS/e-posta da tekrarlanmaz.
    /// </summary>
    private async Task<bool> FireAsync(
        Domain.Entities.AutomationRule rule, string key, string title, string message,
        string link, string icon, string severity, string? contactPhone, CancellationToken ct)
    {
        var today = AppTime.Today.ToString("yyyy-MM-dd");
        var id = await _notify.RaiseAsync(new RaiseNotificationRequest(
            Type: $"automation:{rule.TriggerType}",
            Title: title,
            Message: message,
            Severity: severity,
            Link: link,
            Icon: icon,
            DedupKey: $"auto:{rule.Id}:{key}:{today}"), ct);

        if (id is null) return false; // aynı kural bu olay için bugün zaten çalıştı

        // Bildirim ("notify") zaten yukarıda düştü. Diğer eylemler EK olarak uygulanır.
        switch (rule.ActionType)
        {
            case "sms":
                var phone = ReadString(rule.ActionConfigJson, "phone") ?? contactPhone;
                if (!string.IsNullOrWhiteSpace(phone))
                    await _sms.SendAsync(phone!, $"{title}: {message}", ct);
                break;

            case "email":
                var to = ReadString(rule.ActionConfigJson, "email");
                if (!string.IsNullOrWhiteSpace(to))
                    await _email.SendAsync(to!, title, $"<p>{System.Net.WebUtility.HtmlEncode(message)}</p>", ct);
                break;

            // "notify" → ek iş yok. "task" → görev modülü yokken bildirim olarak durur (yukarıda düştü).
        }
        return true;
    }

    // ---- Serbest JSON okuyucular (bozuk/eksik JSON kuralı düşürmez) ----

    private static decimal? ReadDecimal(string? json, string prop)
    {
        var el = ReadProp(json, prop);
        if (el is null) return null;
        if (el.Value.ValueKind == JsonValueKind.Number && el.Value.TryGetDecimal(out var d)) return d;
        if (el.Value.ValueKind == JsonValueKind.String
            && decimal.TryParse(el.Value.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static string? ReadString(string? json, string prop)
    {
        var el = ReadProp(json, prop);
        return el?.ValueKind == JsonValueKind.String ? el.Value.GetString() : null;
    }

    private static JsonElement? ReadProp(string? json, string prop)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(prop, out var el) ? el.Clone() : null;
        }
        catch (JsonException)
        {
            return null; // kullanıcı serbest metin yazmış olabilir → kuralı düşürme
        }
    }
}
