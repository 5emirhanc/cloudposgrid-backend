using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Invoices;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Quotes;

public record QuoteLineDto(
    Guid Id, Guid ProductId, string ProductName, decimal Quantity,
    decimal UnitPrice, decimal VatRate, decimal LineTotal, decimal VatAmount);

/// <summary>IsExpired durum alanı DEĞİL, ValidUntil'den türetilir (arka plan işi gerektirmez).</summary>
public record QuoteDto(
    Guid Id, string Number, QuoteStatus Status, bool IsExpired,
    Guid? ContactId, string? ContactName, string? CustomerName,
    DateTime Date, DateTime ValidUntil,
    decimal Subtotal, decimal VatTotal, decimal GrandTotal,
    string? Note, Guid? InvoiceId, IReadOnlyList<QuoteLineDto> Lines);

public record QuoteListItemDto(
    Guid Id, string Number, QuoteStatus Status, bool IsExpired,
    string? ContactName, string? CustomerName,
    DateTime Date, DateTime ValidUntil, decimal GrandTotal, Guid? InvoiceId);

public record SaveQuoteLineRequest(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal VatRate);

/// <summary>Oluşturma ve güncelleme aynı sözleşmeyi kullanır (form aynı).</summary>
public record SaveQuoteRequest(
    Guid? ContactId, string? CustomerName, DateTime? Date, DateTime? ValidUntil,
    string? Note, List<SaveQuoteLineRequest> Lines);

public record SetQuoteStatusRequest(QuoteStatus Status);

/// <summary>Teklifi satışa çevir. Ödeme verilmezse tutar cariye borç yazılır (cari zorunlu olur).</summary>
public record ConvertQuoteRequest(InvoicePaymentRequest? Payment);

public class QuoteQuery : PagedQuery
{
    public QuoteStatus? Status { get; set; }
    public Guid? ContactId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public interface IQuoteService
{
    Task<PagedResult<QuoteListItemDto>> GetAsync(QuoteQuery query, CancellationToken ct = default);
    Task<QuoteDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<QuoteDto> CreateAsync(SaveQuoteRequest req, CancellationToken ct = default);
    Task<QuoteDto> UpdateAsync(Guid id, SaveQuoteRequest req, CancellationToken ct = default);
    Task<QuoteDto> SetStatusAsync(Guid id, QuoteStatus status, CancellationToken ct = default);
    /// <summary>Kabul edilen teklifi satış faturasına çevirir — mali etki (stok/cari/kasa/puan) TAM BİR KEZ burada oluşur.</summary>
    Task<QuoteDto> ConvertAsync(Guid id, ConvertQuoteRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
