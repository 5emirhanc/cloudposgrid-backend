using CloudPosGrid.Application.Abstractions;

namespace CloudPosGrid.Infrastructure.Payments;

/// <summary>
/// Varsayılan ödeme sağlayıcısı: tahsilat fiziksel/harici olarak yapılır (nakit, harici kart cihazı).
/// Gerçek kart çekimi YAPMAZ; satış motoru yalnızca ödemeyi kayda geçer. Gerçek bir ağ geçidi
/// (iyzico/PayTR/banka sanal POS) eklendiğinde bu sınıfın yerine o geçer (config: Payment:Provider).
/// </summary>
public sealed class ManualPaymentProvider : IPaymentProvider
{
    public string Name => "manual";
    public bool IsGateway => false;

    public Task<PaymentResult> ChargeAsync(PaymentChargeRequest req, CancellationToken ct = default)
        => Task.FromResult(new PaymentResult(true, null, null));
}
