using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Settings;

public sealed class SettingsService : ISettingsService
{
    private readonly IApplicationDbContext _db;
    private readonly IPlanEntitlementProvider _entitlements;

    public SettingsService(IApplicationDbContext db, IPlanEntitlementProvider entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    public async Task<SettingsDto> GetAsync(CancellationToken ct = default)
    {
        var s = await _db.Settings.FirstOrDefaultAsync(ct) ?? await EnsureAsync(ct);
        return Map(s);
    }

    public async Task<SettingsDto> UpdateAsync(UpdateSettingsRequest req, CancellationToken ct = default)
    {
        var s = await _db.Settings.FirstOrDefaultAsync(ct) ?? await EnsureAsync(ct);

        s.CompanyName = req.CompanyName.Trim();
        s.TaxOffice = req.TaxOffice?.Trim();
        s.TaxNo = req.TaxNo?.Trim();
        s.Address = req.Address?.Trim();
        s.Phone = req.Phone?.Trim();
        s.Email = req.Email?.Trim();
        s.Currency = string.IsNullOrWhiteSpace(req.Currency) ? "TRY" : req.Currency.Trim();
        s.DefaultVatRate = req.DefaultVatRate;
        s.LogoUrl = req.LogoUrl?.Trim();
        // Sadakat / puan yalnız Kurumsal + Zincir'de: yetki yoksa kapalı zorlanır (Pro'da açılamaz).
        var ent = await _entitlements.GetAsync(ct);
        s.LoyaltyEnabled = req.LoyaltyEnabled && ent.LoyaltyProgram;
        s.LoyaltyEarnPercent = Math.Clamp(req.LoyaltyEarnPercent, 0m, 100m);
        s.ManualDiscountEnabled = req.ManualDiscountEnabled;
        s.MaxManualDiscountPercent = Math.Clamp(req.MaxManualDiscountPercent, 0m, 100m);

        // Terazi barkodu: şablon ancak AÇIKKEN doğrulanır (kullanıcı yarım ayarı kaydedip sonra tamamlayabilsin).
        s.ScaleBarcodeItemDigits = Math.Clamp(req.ScaleBarcodeItemDigits, 1, 9);
        s.ScaleBarcodeValueDigits = Math.Clamp(req.ScaleBarcodeValueDigits, 1, 9);
        s.ScaleBarcodeDecimals = Math.Clamp(req.ScaleBarcodeDecimals, 0, 4);
        s.ScaleBarcodeEmbeds = req.ScaleBarcodeEmbeds;
        s.ScaleBarcodePriceIncludesVat = req.ScaleBarcodePriceIncludesVat;
        s.ScaleBarcodePrefixes = NormalizePrefixes(req.ScaleBarcodePrefixes, s.ScaleBarcodePrefixes);
        s.ScaleBarcodeEnabled = req.ScaleBarcodeEnabled;
        if (s.ScaleBarcodeEnabled)
        {
            var prefixes = s.ScaleBarcodePrefixes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (prefixes.Length == 0)
                throw new BusinessRuleException("En az bir terazi ön eki girin (ör. 28,29).");
            foreach (var p in prefixes)
            {
                // "2" ve "20" DAHİLİ barkod üretimine ayrıldı — terazi ön eki olarak kabul edilirse
                // kendi ürettiğimiz barkodlar terazi etiketi sanılıp rastgele haneler ağırlık okunur.
                if (p == "2" || p == "20")
                    throw new BusinessRuleException("'2' ve '20' ön ekleri dahili barkod üretimine ayrılmıştır; 21-29 arası kullanın (ör. 28,29).");
                if (p.Length + s.ScaleBarcodeItemDigits + s.ScaleBarcodeValueDigits + 1 != 13)
                    throw new BusinessRuleException(
                        $"'{p}' ön ekiyle şablon 13 haneye tamamlanmıyor (ön ek {p.Length} + ürün {s.ScaleBarcodeItemDigits} + değer {s.ScaleBarcodeValueDigits} + kontrol 1).");
            }
        }

        await _db.SaveChangesAsync(ct);
        return Map(s);
    }

    private async Task<TenantSettings> EnsureAsync(CancellationToken ct)
    {
        var s = new TenantSettings { CompanyName = "İşletmem", Currency = "TRY", DefaultVatRate = 20m };
        _db.Settings.Add(s);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    private static SettingsDto Map(TenantSettings s) => new(
        s.CompanyName, s.TaxOffice, s.TaxNo, s.Address, s.Phone, s.Email, s.Currency, s.DefaultVatRate, s.LogoUrl,
        s.LoyaltyEnabled, s.LoyaltyEarnPercent,
        s.ManualDiscountEnabled, s.MaxManualDiscountPercent,
        s.ScaleBarcodeEnabled, s.ScaleBarcodePrefixes, s.ScaleBarcodeItemDigits,
        s.ScaleBarcodeValueDigits, s.ScaleBarcodeDecimals, s.ScaleBarcodeEmbeds, s.ScaleBarcodePriceIncludesVat);

    /// <summary>Ön ek listesini temizler: yalnız 1-3 haneli rakam grupları, tekilleştirilmiş. Boş kalırsa eski değer korunur.</summary>
    private static string NormalizePrefixes(string? raw, string current)
    {
        if (raw is null) return current;
        var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length is >= 1 and <= 3 && p.All(ch => ch is >= '0' and <= '9'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return list.Count == 0 ? current : string.Join(',', list);
    }
}
