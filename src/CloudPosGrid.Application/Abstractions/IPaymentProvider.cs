using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Abstractions;

public record PaymentChargeRequest(decimal Amount, string Currency, PaymentMethod Method, string? Reference);

public record PaymentResult(bool Success, string? TransactionId, string? Error);

/// <summary>
/// Kart tahsilat sağlayıcısı soyutlaması. "manual" = tahsilat fiziksel/harici yapılır (nakit, harici POS)
/// ve gerçek çekim yoktur; iyzico/PayTR/banka gibi gerçek sağlayıcılar <see cref="ChargeAsync"/>'i uygular.
/// Aktif sağlayıcı config'ten seçilir (Payment:Provider).
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Sağlayıcı adı (manual, iyzico, paytr…).</summary>
    string Name { get; }

    /// <summary>Gerçek kart çekimi yapan bir ağ geçidi mi? (manuel'de false — satış akışı çekim çağırmaz.)</summary>
    bool IsGateway { get; }

    Task<PaymentResult> ChargeAsync(PaymentChargeRequest req, CancellationToken ct = default);
}
