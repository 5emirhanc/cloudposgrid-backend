using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Campaigns;

public record CampaignDto(
    Guid Id, string Name, string Type, Guid? CategoryId, Guid? ProductId, decimal? Percent,
    int? BuyQty, int? GetQty, DateTime? StartDate, DateTime? EndDate,
    int? StartHour, int? EndHour, string? DaysMask, bool IsActive);

public record SaveCampaignRequest(
    string Name, string Type, Guid? CategoryId, Guid? ProductId, decimal? Percent,
    int? BuyQty, int? GetQty, DateTime? StartDate, DateTime? EndDate,
    int? StartHour, int? EndHour, string? DaysMask, bool IsActive = true);

public record EvaluateCartItem(Guid ProductId, decimal Quantity, decimal UnitPrice);
public record EvaluateCartRequest(List<EvaluateCartItem> Items);
public record CampaignDiscountLine(Guid ProductId, decimal DiscountAmount, string CampaignName);
public record CampaignEvaluationDto(IReadOnlyList<CampaignDiscountLine> Lines, decimal TotalDiscount);

public interface ICampaignService
{
    Task<IReadOnlyList<CampaignDto>> ListAsync(CancellationToken ct = default);
    Task<CampaignDto> CreateAsync(SaveCampaignRequest req, CancellationToken ct = default);
    Task<CampaignDto> UpdateAsync(Guid id, SaveCampaignRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>Sepete uygulanacak kampanya indirimini HESAPLAR (fiyatı değiştirmez; POS sonucu uygular).</summary>
    Task<CampaignEvaluationDto> EvaluateAsync(EvaluateCartRequest req, CancellationToken ct = default);
}

/// <summary>
/// Kampanya/promosyon motoru (#16). Tanım CRUD + saf değerlendirme (Evaluate). Fiyatlandırma/fatura yoluna
/// DOKUNMAZ — sepet satırları için uygulanacak indirim hesaplanır; POS bunu mevcut indirim mekanizmasından geçirir.
/// Satır başına EN İYİ tek kampanya uygulanır (stacking yok). Happy hour yerel saate göre (AppTime).
/// </summary>
public sealed class CampaignService : ICampaignService
{
    private static readonly string[] Types = ["category_percent", "product_percent", "happy_hour", "buy_x_get_y"];

    private readonly IApplicationDbContext _db;

    public CampaignService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<CampaignDto>> ListAsync(CancellationToken ct = default)
        => await _db.Campaigns.AsNoTracking().OrderByDescending(c => c.CreatedAt).Select(Project).ToListAsync(ct);

    public async Task<CampaignDto> CreateAsync(SaveCampaignRequest req, CancellationToken ct = default)
    {
        Validate(req);
        var e = Apply(new Campaign(), req);
        _db.Campaigns.Add(e);
        await _db.SaveChangesAsync(ct);
        return Map(e);
    }

    public async Task<CampaignDto> UpdateAsync(Guid id, SaveCampaignRequest req, CancellationToken ct = default)
    {
        Validate(req);
        var e = await _db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFoundException.For("Kampanya", id);
        Apply(e, req);
        await _db.SaveChangesAsync(ct);
        return Map(e);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFoundException.For("Kampanya", id);
        _db.Campaigns.Remove(e);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<CampaignEvaluationDto> EvaluateAsync(EvaluateCartRequest req, CancellationToken ct = default)
    {
        var items = (req.Items ?? new()).Where(i => i.Quantity > 0m).ToList();
        if (items.Count == 0) return new CampaignEvaluationDto([], 0m);

        var nowLocal = AppTime.LocalNow;
        var today = AppTime.Today;
        // Tarih penceresi geçerli + aktif kampanyalar.
        var campaigns = (await _db.Campaigns.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct))
            .Where(c => (c.StartDate == null || c.StartDate.Value.Date <= today.ToDateTime(TimeOnly.MinValue))
                     && (c.EndDate == null || c.EndDate.Value.Date >= today.ToDateTime(TimeOnly.MinValue)))
            .ToList();
        if (campaigns.Count == 0) return new CampaignEvaluationDto([], 0m);

        // Ürün → kategori eşlemesi.
        var productIds = items.Select(i => i.ProductId).Distinct().ToList();
        var cats = await _db.Products.Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.CategoryId }).ToListAsync(ct);
        var catByProduct = cats.ToDictionary(x => x.Id, x => x.CategoryId);

        var lines = new List<CampaignDiscountLine>();
        foreach (var item in items)
        {
            catByProduct.TryGetValue(item.ProductId, out var catId);
            var lineTotal = item.Quantity * item.UnitPrice;
            decimal best = 0m; string? bestName = null;

            foreach (var c in campaigns)
            {
                decimal disc = 0m;
                switch (c.Type)
                {
                    case "product_percent":
                        if (c.ProductId == item.ProductId && c.Percent is > 0m)
                            disc = lineTotal * c.Percent.Value / 100m;
                        break;
                    case "category_percent":
                        if (c.CategoryId != null && c.CategoryId == catId && c.Percent is > 0m)
                            disc = lineTotal * c.Percent.Value / 100m;
                        break;
                    case "happy_hour":
                        if (c.Percent is > 0m && InWindow(c, nowLocal)
                            && (c.ProductId == null && c.CategoryId == null
                                || c.ProductId == item.ProductId
                                || (c.CategoryId != null && c.CategoryId == catId)))
                            disc = lineTotal * c.Percent.Value / 100m;
                        break;
                    case "buy_x_get_y":
                        if (c.ProductId == item.ProductId && c.BuyQty is > 0 && c.GetQty is > 0)
                        {
                            var bundle = c.BuyQty.Value + c.GetQty.Value;
                            var freeUnits = Math.Floor(item.Quantity / bundle) * c.GetQty.Value;
                            disc = freeUnits * item.UnitPrice;
                        }
                        break;
                }
                if (disc > best) { best = disc; bestName = c.Name; }
            }

            if (best > 0m && bestName != null)
                lines.Add(new CampaignDiscountLine(item.ProductId, Math.Round(best, 2), bestName));
        }

        return new CampaignEvaluationDto(lines, Math.Round(lines.Sum(l => l.DiscountAmount), 2));
    }

    /// <summary>Happy hour saat/gün penceresi içinde mi (yerel saat).</summary>
    private static bool InWindow(Campaign c, DateTime nowLocal)
    {
        if (c.StartHour is int sh && c.EndHour is int eh)
        {
            var hour = nowLocal.Hour;
            var inHours = sh <= eh ? (hour >= sh && hour < eh) : (hour >= sh || hour < eh); // gece aşan aralık
            if (!inHours) return false;
        }
        if (!string.IsNullOrWhiteSpace(c.DaysMask))
        {
            var weekday = ((int)nowLocal.DayOfWeek + 6) % 7; // 0=Pzt..6=Paz
            var allowed = c.DaysMask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!allowed.Contains(weekday.ToString())) return false;
        }
        return true;
    }

    private static void Validate(SaveCampaignRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new BusinessRuleException("Kampanya adı boş olamaz.");
        if (!Types.Contains(req.Type)) throw new BusinessRuleException("Geçersiz kampanya türü.");
        if (req.Type is "category_percent" or "product_percent" or "happy_hour" && req.Percent is not > 0m)
            throw new BusinessRuleException("Bu kampanya türü için yüzde girin.");
        if (req.Type == "category_percent" && req.CategoryId is null)
            throw new BusinessRuleException("Kategori kampanyası için kategori seçin.");
        if (req.Type == "product_percent" && req.ProductId is null)
            throw new BusinessRuleException("Ürün kampanyası için ürün seçin.");
        if (req.Type == "buy_x_get_y" && (req.ProductId is null || req.BuyQty is not > 0 || req.GetQty is not > 0))
            throw new BusinessRuleException("'X al Y öde' için ürün + al/öde adetleri girin.");
    }

    private static Campaign Apply(Campaign e, SaveCampaignRequest r)
    {
        e.Name = r.Name.Trim();
        e.Type = r.Type;
        e.CategoryId = r.CategoryId;
        e.ProductId = r.ProductId;
        e.Percent = r.Percent;
        e.BuyQty = r.BuyQty;
        e.GetQty = r.GetQty;
        e.StartDate = r.StartDate;
        e.EndDate = r.EndDate;
        e.StartHour = r.StartHour;
        e.EndHour = r.EndHour;
        e.DaysMask = string.IsNullOrWhiteSpace(r.DaysMask) ? null : r.DaysMask.Trim();
        e.IsActive = r.IsActive;
        return e;
    }

    private static readonly System.Linq.Expressions.Expression<Func<Campaign, CampaignDto>> Project = c => new CampaignDto(
        c.Id, c.Name, c.Type, c.CategoryId, c.ProductId, c.Percent, c.BuyQty, c.GetQty,
        c.StartDate, c.EndDate, c.StartHour, c.EndHour, c.DaysMask, c.IsActive);

    private static CampaignDto Map(Campaign c) => new(
        c.Id, c.Name, c.Type, c.CategoryId, c.ProductId, c.Percent, c.BuyQty, c.GetQty,
        c.StartDate, c.EndDate, c.StartHour, c.EndHour, c.DaysMask, c.IsActive);
}
