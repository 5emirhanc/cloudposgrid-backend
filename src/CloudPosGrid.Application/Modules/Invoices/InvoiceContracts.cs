using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Invoices;

public record InvoiceLineDto(
    Guid Id, Guid ProductId, string ProductName, decimal Quantity,
    decimal UnitPrice, decimal VatRate, decimal LineTotal, decimal VatAmount, decimal RefundedQuantity);

public record InvoiceDto(
    Guid Id, InvoiceType Type, string Number, Guid? ContactId, string? ContactName,
    DateTime Date, decimal Subtotal, decimal VatTotal, decimal GrandTotal, decimal PaidAmount,
    decimal Discount, decimal PointsEarned,
    decimal ManualDiscount, string? DiscountReason,
    InvoiceStatus Status, string? Note, IReadOnlyList<InvoiceLineDto> Lines,
    DateTime? DueDate = null);

public record InvoiceListItemDto(
    Guid Id, InvoiceType Type, string Number, string? ContactName,
    DateTime Date, decimal GrandTotal, decimal PaidAmount, InvoiceStatus Status);

public record CreateInvoiceLineRequest(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal VatRate);

public record InvoicePaymentRequest(Guid CashAccountId, decimal Amount, PaymentMethod Method);

public record RefundLineRequest(Guid InvoiceLineId, decimal Quantity);

/// <summary>Kısmi iade: seçili satır/miktarlar; CashAccountId verilirse nakit geri ödeme, yoksa cariye alacak (credit note).</summary>
public record RefundInvoiceRequest(List<RefundLineRequest> Lines, Guid? CashAccountId, string? Note);

public record CreateInvoiceRequest(
    InvoiceType Type, Guid? ContactId, DateTime? Date, string? Note,
    List<CreateInvoiceLineRequest> Lines, InvoicePaymentRequest? Payment, string Channel = "Store",
    // Pazaryeri: sipariş zaten pazaryerinde gerçekleşti; stok yetmese bile satış kaydedilmeli (stok negatife düşebilir).
    bool AllowOversell = false,
    // Sadakat: bu satışta kullanılacak puan (₺). Cari bakiyesi + fatura toplamıyla sınırlanır.
    decimal? RedeemPoints = null,
    // Çevrimdışı POS: istemci-üretimi benzersiz satış kimliği (idempotency). Aynı kimlik yeniden gelirse
    // yeni fatura kesilmez, mevcut fatura döner (çevrimdışı kuyruk yeniden gönderiminde çift satış olmaz).
    string? ClientSaleId = null,
    // Elle indirim (kasiyer). Yüzde VEYA tutar — ikisi birden verilemez. Satır fiyatlarına işlenir
    // (KDV matrahı da düşer). Kasiyer için ayarlardaki azami oranla sınırlıdır; Owner/Admin sınırsızdır.
    // DİKKAT: yeni alanlar HER ZAMAN sona eklenir — CreateInvoiceRequest pozisyonel çağrılıyor.
    decimal? ManualDiscountPercent = null,
    decimal? ManualDiscountAmount = null,
    DiscountReason? ManualDiscountReason = null,
    // Teklif dönüşümü: teklifteki fiyatlar zaten nihai/pazarlıklı → cari indirimi İKİNCİ kez uygulanmasın.
    bool SkipContactDiscount = false,
    // Mal kabul: bu alış faturası hangi tedarikçi siparişinden doğdu (iptalinde sipariş de geri alınır).
    Guid? PurchaseOrderId = null,
    // Ödeme vadesi (veresiye/açık hesap). null = peşin/vadesiz. Cari hareketine de işlenir → vadeye göre yaşlandırma.
    DateTime? DueDate = null,
    // Çoklu/karma ödeme (split tender): "parça nakit + parça kart". Doluysa tek Payment yerine bu liste işlenir;
    // toplam fatura tutarını aşamaz. Boş/null ise tek Payment (geri uyumlu) kullanılır.
    List<InvoicePaymentRequest>? Payments = null);

public class InvoiceQuery : PagedQuery
{
    public InvoiceType? Type { get; set; }
    public InvoiceStatus? Status { get; set; }
    public Guid? ContactId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public interface IInvoiceService
{
    Task<PagedResult<InvoiceListItemDto>> GetAsync(InvoiceQuery query, CancellationToken ct = default);
    Task<InvoiceDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    /// <param name="sellerUserId">Satışı yapan personel (SellerUserId). Sunucu-tarafı override; verilmezse
    /// mevcut kullanıcı. Randevu tahsilatı atanan personeli geçer — istemci gövdesinden set EDİLEMEZ.</param>
    Task<InvoiceDto> CreateAsync(CreateInvoiceRequest req, CancellationToken ct = default, Guid? sellerUserId = null);
    /// <summary>Faturayı iptal/iade eder: bağlı tüm stok, kasa, cari ve ödeme hareketlerini ters
    /// kayıtla geri alır, faturayı (ve varsa bağlı adisyonu) iptalli işaretler.</summary>
    Task<InvoiceDto> VoidAsync(Guid id, CancellationToken ct = default);
    /// <summary>Satış faturasından seçili satır/miktarları KISMEN iade eder: stok geri girer, para/cari
    /// geri ödenir, kazanılan puan oransal geri alınır. Tam iptal için <see cref="VoidAsync"/> kullanılır.</summary>
    Task<InvoiceDto> RefundAsync(Guid id, RefundInvoiceRequest req, CancellationToken ct = default);
}
