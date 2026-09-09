using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Settings;

public record SettingsDto(
    string CompanyName, string? TaxOffice, string? TaxNo, string? Address,
    string? Phone, string? Email, string Currency, decimal DefaultVatRate, string? LogoUrl,
    bool LoyaltyEnabled, decimal LoyaltyEarnPercent,
    bool ManualDiscountEnabled, decimal MaxManualDiscountPercent,
    bool ScaleBarcodeEnabled, string ScaleBarcodePrefixes, int ScaleBarcodeItemDigits,
    int ScaleBarcodeValueDigits, int ScaleBarcodeDecimals, ScaleEmbedMode ScaleBarcodeEmbeds,
    bool ScaleBarcodePriceIncludesVat);

public record UpdateSettingsRequest(
    string CompanyName, string? TaxOffice, string? TaxNo, string? Address,
    string? Phone, string? Email, string Currency, decimal DefaultVatRate, string? LogoUrl,
    bool LoyaltyEnabled = false, decimal LoyaltyEarnPercent = 0m,
    bool ManualDiscountEnabled = false, decimal MaxManualDiscountPercent = 0m,
    bool ScaleBarcodeEnabled = false, string? ScaleBarcodePrefixes = null, int ScaleBarcodeItemDigits = 5,
    int ScaleBarcodeValueDigits = 5, int ScaleBarcodeDecimals = 3, ScaleEmbedMode ScaleBarcodeEmbeds = ScaleEmbedMode.Weight,
    bool ScaleBarcodePriceIncludesVat = true);

public interface ISettingsService
{
    Task<SettingsDto> GetAsync(CancellationToken ct = default);
    Task<SettingsDto> UpdateAsync(UpdateSettingsRequest req, CancellationToken ct = default);
}
