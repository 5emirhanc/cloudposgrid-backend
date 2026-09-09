using System.Globalization;
using System.Text;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Reporting;

/// <summary>
/// Zamanlı gün sonu özeti içeriğini üretir (salt-okuma). E-posta GÖNDERMEZ — onu hosted service yapar.
/// Arka planda (istek dışı) çalıştığından şube başlığı yoktur → özet tenant genelindedir (tüm şubeler).
/// Gün sınırı işletme yerel gününe (TR) göredir; UTC saklanan veriler <see cref="AppTime"/> ile aralığa çevrilir.
/// </summary>
public sealed class ScheduledReportService : IScheduledReportService
{
    private static readonly CultureInfo Tr = new("tr-TR");
    private readonly IApplicationDbContext _db;

    public ScheduledReportService(IApplicationDbContext db) => _db = db;

    public async Task<DailySummaryContent> BuildDailySummaryAsync(CancellationToken ct = default)
    {
        // "Bugün" işletme yerel gününe göre (TR); veriler UTC saklandığından aralık [from, to) UTC'ye çevrilir.
        var today = AppTime.Today;
        var (from, to) = AppTime.DayRangeUtc(today);

        // Satış cirosu + adedi — iptal (Cancelled) hariç. Tek sorgu iki kez değerlendirilir (ciro + adet).
        var salesQuery = _db.Invoices
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled
                        && i.Date >= from && i.Date < to);
        var salesRevenue = await salesQuery.SumAsync(i => (decimal?)i.GrandTotal, ct) ?? 0m;
        var salesCount = await salesQuery.CountAsync(ct);

        // Bugünkü tahsilat (gelir) ve gider — kasa hareketleri.
        var income = await _db.FinanceTransactions
            .Where(x => x.Type == FinanceType.Income && x.Date >= from && x.Date < to)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        var expense = await _db.FinanceTransactions
            .Where(x => x.Type == FinanceType.Expense && x.Date >= from && x.Date < to)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        var net = income - expense;

        // Kasa toplamı — aktif kasaların güncel bakiyeleri.
        var cashTotal = await _db.CashAccounts
            .Where(a => a.IsActive)
            .SumAsync(a => (decimal?)a.Balance, ct) ?? 0m;

        // Düşük stok ürün sayısı — aktif, varyant şablonu olmayan, stoğu asgariye inmiş ürünler.
        var lowStock = await _db.Products
            .CountAsync(p => p.IsActive && !p.IsVariantParent && p.CurrentStock <= p.MinStock, ct);

        var dateText = today.ToString("dd.MM.yyyy", Tr);
        var subject = $"CloudPosGrid Gün Sonu Özeti — {dateText}";
        var htmlBody = BuildHtml(dateText, salesRevenue, salesCount, income, expense, net, cashTotal, lowStock);
        return new DailySummaryContent(subject, htmlBody);
    }

    /// <summary>Temiz, basit ve inline-stilli HTML özet tablosu (e-posta istemcileri için satır-içi CSS).</summary>
    private static string BuildHtml(string dateText, decimal salesRevenue, int salesCount,
        decimal income, decimal expense, decimal net, decimal cashTotal, int lowStock)
    {
        var netColor = net >= 0m ? "#16a34a" : "#dc2626";

        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;max-width:520px;margin:0 auto;color:#222;\">");
        sb.Append("<h2 style=\"margin:0 0 2px;font-size:18px;\">Gün Sonu Özeti</h2>");
        sb.Append($"<p style=\"margin:0 0 16px;color:#888;font-size:13px;\">{dateText}</p>");
        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:14px;\">");
        sb.Append(Row("Satış cirosu", Money(salesRevenue)));
        sb.Append(Row("Satış adedi", salesCount.ToString("N0", Tr)));
        sb.Append(Row("Tahsilat (gelir)", Money(income)));
        sb.Append(Row("Gider", Money(expense)));
        sb.Append(Row("Net (gelir − gider)", Money(net), netColor));
        sb.Append(Row("Kasa toplamı", Money(cashTotal)));
        sb.Append(Row("Düşük stok ürün", lowStock.ToString("N0", Tr) + " adet"));
        sb.Append("</table>");
        sb.Append("<p style=\"margin:16px 0 0;color:#aaa;font-size:12px;\">Bu özet CloudPosGrid tarafından otomatik oluşturulmuştur.</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Etiket + değer için bir tablo satırı (değer sağa hizalı, kalın). İsteğe bağlı vurgu rengi.</summary>
    private static string Row(string label, string value, string? color = null)
    {
        var valueStyle = "padding:10px 14px;border-bottom:1px solid #eee;text-align:right;font-weight:600;"
                         + (color is null ? string.Empty : $"color:{color};");
        return $"<tr><td style=\"padding:10px 14px;border-bottom:1px solid #eee;color:#555;\">{label}</td>"
               + $"<td style=\"{valueStyle}\">{value}</td></tr>";
    }

    private static string Money(decimal v) => v.ToString("N2", Tr) + " ₺";
}
