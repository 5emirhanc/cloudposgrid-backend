using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Contacts;

public record ContactDto(
    Guid Id, ContactType Type, string Name, string? TaxOffice, string? TaxNo,
    string? Phone, string? Email, string? Address, decimal Balance, decimal DiscountRate, decimal PointsBalance, bool IsActive,
    string? Notes = null, string? Tags = null, DateTime? Birthday = null, decimal? CreditLimit = null);

public record CreateContactRequest(
    ContactType Type, string Name, string? TaxOffice, string? TaxNo,
    string? Phone, string? Email, string? Address, decimal OpeningBalance, decimal DiscountRate = 0,
    string? Notes = null, string? Tags = null, DateTime? Birthday = null, decimal? CreditLimit = null);

public record UpdateContactRequest(
    ContactType Type, string Name, string? TaxOffice, string? TaxNo,
    string? Phone, string? Email, string? Address, bool IsActive, decimal DiscountRate = 0,
    string? Notes = null, string? Tags = null, DateTime? Birthday = null, decimal? CreditLimit = null);

public class ContactQuery : PagedQuery
{
    public ContactType? Type { get; set; }
}

public record AccountTransactionDto(
    Guid Id, Guid ContactId, TransactionDirection Direction, decimal Amount,
    decimal BalanceAfter, string? Description, string? DocRef, DateTime Date);

public record CreateAccountTransactionRequest(
    TransactionDirection Direction, decimal Amount, string? Description, DateTime? Date);

public record ContactLedgerDto(ContactDto Contact, IReadOnlyList<AccountTransactionDto> Transactions);

// ---- Müşteri 360 ----
public record ContactPurchaseDto(Guid ProductId, string ProductName, decimal Quantity, decimal Total);
public record ContactInvoiceBriefDto(Guid Id, string Number, DateTime Date, decimal GrandTotal, decimal PaidAmount, string Status);

/// <summary>Müşteri 360: cari + yaşam boyu değer, sık alınan ürünler, son faturalar, randevu/teklif özeti.</summary>
public record Contact360Dto(
    ContactDto Contact,
    decimal TotalPurchases,   // LTV — satış faturaları toplamı
    int InvoiceCount,
    decimal AvgBasket,
    DateTime? LastPurchaseAt,
    decimal OpenBalance,      // = Contact.Balance
    int AppointmentCount,
    DateTime? LastAppointmentAt,
    int QuoteCount,
    IReadOnlyList<ContactPurchaseDto> TopProducts,
    IReadOnlyList<ContactInvoiceBriefDto> RecentInvoices);

public interface IContactService
{
    Task<PagedResult<ContactDto>> GetAsync(ContactQuery query, CancellationToken ct = default);
    Task<ContactDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ContactDto> CreateAsync(CreateContactRequest req, CancellationToken ct = default);
    Task<ContactDto> UpdateAsync(Guid id, UpdateContactRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<ContactLedgerDto> GetLedgerAsync(Guid id, CancellationToken ct = default);
    Task<AccountTransactionDto> AddTransactionAsync(Guid id, CreateAccountTransactionRequest req, CancellationToken ct = default);
    /// <summary>Müşteri 360: LTV + sık alınan ürünler + son faturalar + randevu/teklif özeti (veri carie bağlı).</summary>
    Task<Contact360Dto> GetOverviewAsync(Guid id, CancellationToken ct = default);
}
