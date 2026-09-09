using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Invoices;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Purchasing;

public record PurchaseOrderLineDto(
    Guid Id, Guid ProductId, string ProductName,
    decimal OrderedQuantity, decimal ReceivedQuantity, decimal RemainingQuantity,
    decimal UnitPrice, decimal VatRate, decimal LineTotal);

/// <summary>Mal kabul geçmişi: siparişe bağlı alış faturaları (kısmi teslimatta birden fazla olur).</summary>
public record PurchaseOrderReceiptDto(Guid InvoiceId, string InvoiceNumber, DateTime Date, decimal GrandTotal, bool IsCancelled);

public record PurchaseOrderDto(
    Guid Id, string Number, Guid ContactId, string ContactName, string? ContactPhone, PurchaseOrderStatus Status,
    DateTime OrderDate, DateTime? ExpectedDate, string? Note,
    decimal Subtotal, decimal VatTotal, decimal GrandTotal, decimal ReceivedRatio,
    IReadOnlyList<PurchaseOrderLineDto> Lines, IReadOnlyList<PurchaseOrderReceiptDto> Receipts);

public record PurchaseOrderListItemDto(
    Guid Id, string Number, string ContactName, PurchaseOrderStatus Status,
    DateTime OrderDate, DateTime? ExpectedDate, decimal GrandTotal, int LineCount, decimal ReceivedRatio);

public record CreatePurchaseOrderLineRequest(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal VatRate);

public record CreatePurchaseOrderRequest(
    Guid ContactId, DateTime? OrderDate, DateTime? ExpectedDate, string? Note,
    List<CreatePurchaseOrderLineRequest> Lines);

/// <summary>Mal kabul satırı. UnitPrice verilmezse sipariş fiyatı kullanılır; fatura farklı geldiyse
/// gerçek fiyat girilir (ürünün alış fiyatı da ondan güncellenir).</summary>
public record ReceiveLineRequest(Guid PurchaseOrderLineId, decimal Quantity, decimal? UnitPrice);

public record ReceivePurchaseOrderRequest(
    List<ReceiveLineRequest> Lines, DateTime? Date, string? Note,
    InvoicePaymentRequest? Payment, bool AllowOverReceipt = false);

public record ReceivePurchaseOrderResultDto(
    PurchaseOrderDto Order, Guid InvoiceId, string InvoiceNumber, decimal InvoiceGrandTotal);

public class PurchaseOrderQuery : PagedQuery
{
    public PurchaseOrderStatus? Status { get; set; }
    public Guid? ContactId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    /// <summary>Yalnız yolda olan siparişler (Gönderildi + Kısmen geldi).</summary>
    public bool OpenOnly { get; set; }
}

public interface IPurchaseOrderService
{
    Task<PagedResult<PurchaseOrderListItemDto>> GetAsync(PurchaseOrderQuery query, CancellationToken ct = default);
    Task<PurchaseOrderDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PurchaseOrderDto> CreateAsync(CreatePurchaseOrderRequest req, CancellationToken ct = default);
    Task<PurchaseOrderDto> UpdateAsync(Guid id, CreatePurchaseOrderRequest req, CancellationToken ct = default);
    /// <summary>Taslağı tedarikçiye gönderilmiş sayar → artık kilitlidir ve "yolda stok" olarak sayılır.</summary>
    Task<PurchaseOrderDto> SendAsync(Guid id, CancellationToken ct = default);
    /// <summary>Mal kabul: gelen miktarları işler ve siparişe bağlı ALIŞ FATURASI keser
    /// (stok girişi, tedarikçi carisi, kasa çıkışı hepsi oradan gelir).</summary>
    Task<ReceivePurchaseOrderResultDto> ReceiveAsync(Guid id, ReceivePurchaseOrderRequest req, CancellationToken ct = default);
    Task<PurchaseOrderDto> CancelAsync(Guid id, CancellationToken ct = default);
}
