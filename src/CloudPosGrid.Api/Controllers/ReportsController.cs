using CloudPosGrid.Application.Modules.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Detaylı satış ve finansal raporlar. Geri ofis verisidir — sahip/yönetici/muhasebe erişir.</summary>
[ApiController]
[Route("api/reports")]
[Authorize(Roles = "Owner,Admin,Accountant")]
public class ReportsController : ControllerBase
{
    private readonly IReportService _service;

    public ReportsController(IReportService service) => _service = service;

    [HttpGet("sales")]
    public async Task<ActionResult<SalesReportDto>> Sales([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Ok(await _service.GetSalesAsync(f, t, ct));
    }

    [HttpGet("financial")]
    public async Task<ActionResult<FinancialReportDto>> Financial([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Ok(await _service.GetFinancialAsync(f, t, ct));
    }

    /// <summary>Kâr-Zarar: dönem brüt kârı + kanal (Mağaza/Trendyol) ve kategori kırılımı + günlük kâr trendi.</summary>
    [HttpGet("profit")]
    public async Task<ActionResult<ProfitReportDto>> Profit([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Ok(await _service.GetProfitAsync(f, t, ct));
    }

    /// <summary>Gün sonu (Z) raporu — tek günün satış/tahsilat/kasa özeti. date verilmezse bugün.</summary>
    [HttpGet("daily-close")]
    public async Task<ActionResult<DailyCloseDto>> DailyClose([FromQuery] string? date, CancellationToken ct)
    {
        var d = DateTime.TryParse(date, out var pd) ? pd.Date : DateTime.UtcNow.Date;
        return Ok(await _service.GetDailyCloseAsync(d, ct));
    }

    /// <summary>Dönemsel fire/zayi maliyeti — kâr raporunda görünmeyen gizli kayıp.</summary>
    [HttpGet("waste")]
    public async Task<ActionResult<WasteReportDto>> Waste([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Ok(await _service.GetWasteAsync(f, t, ct));
    }

    /// <summary>Cari yaşlandırma (aging): alacak/borç bakiyeleri yaşa göre kovalanır (0-30, 31-60, 61-90, 90+ gün).</summary>
    [HttpGet("aging")]
    public async Task<ActionResult<AgingReportDto>> Aging(CancellationToken ct)
        => Ok(await _service.GetAgingAsync(ct));

    /// <summary>Saatlik satış ısı haritası (yerel saate göre gün×saat) — yoğun saat/gün tespiti.</summary>
    [HttpGet("hourly-sales")]
    [Filters.RequireEntitlement(Filters.Entitlement.AdvancedReports)] // Analitik ekranı — temel raporlar açık kalır
    public async Task<ActionResult<List<HourlySalesCellDto>>> HourlySales([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Ok(await _service.GetHourlySalesAsync(f, t, ct));
    }

    /// <summary>Personel bazlı satış (ciro/adet/ort. sepet) — prim ve performans; ad istemcide eşlenir.</summary>
    [HttpGet("staff-sales")]
    [Filters.RequireEntitlement(Filters.Entitlement.AdvancedReports)] // Analitik ekranı — temel raporlar açık kalır
    public async Task<ActionResult<List<StaffSalesDto>>> StaffSales([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var (f, t) = Range(from, to);
        return Ok(await _service.GetStaffSalesAsync(f, t, ct));
    }

    /// <summary>from/to (YYYY-MM-DD) -> [gün başlangıcı, bitiş+1) yarı açık aralık. Varsayılan son 30 gün.</summary>
    private static (DateTime from, DateTime to) Range(string? from, string? to)
    {
        var f = DateTime.TryParse(from, out var pf) ? pf.Date : DateTime.UtcNow.Date.AddDays(-29);
        var t = DateTime.TryParse(to, out var pt) ? pt.Date.AddDays(1) : DateTime.UtcNow.Date.AddDays(1);
        if (t <= f) t = f.AddDays(1);
        return (f, t);
    }
}
