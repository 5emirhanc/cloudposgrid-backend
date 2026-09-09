namespace CloudPosGrid.Application.Modules.Reporting;

/// <summary>
/// Zamanlı gün sonu özeti e-postasının hazır içeriği: konu satırı + HTML gövde.
/// İçeriği bu modül üretir; GÖNDERİMİ (IEmailSender) arka plandaki hosted service yapar.
/// </summary>
public record DailySummaryContent(string Subject, string HtmlBody);

/// <summary>
/// Zamanlı rapor içeriği üreticisi. Salt-okumadır: verilerden e-posta gövdesi hazırlar, e-posta GÖNDERMEZ.
/// Arka plan işi (hosted service) tarafından çağrılır; ayrıca /api/scheduled-report/preview ile önizlenebilir.
/// </summary>
public interface IScheduledReportService
{
    /// <summary>
    /// Bugünün (<see cref="CloudPosGrid.Application.Common.AppTime.Today"/>, TR gün sınırı) satış/finans/kasa/stok
    /// özetini e-posta içeriği (konu + HTML tablo) olarak üretir. Yalnız içerik döner — gönderim yapmaz.
    /// </summary>
    Task<DailySummaryContent> BuildDailySummaryAsync(CancellationToken ct = default);
}
