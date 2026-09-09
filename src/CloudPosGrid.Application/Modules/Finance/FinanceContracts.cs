using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Finance;

// ---- Kasa / Banka ----
public record CashAccountDto(Guid Id, string Name, CashAccountType Type, decimal Balance, bool IsActive);
public record CreateCashAccountRequest(string Name, CashAccountType Type, decimal OpeningBalance);
public record UpdateCashAccountRequest(string Name, CashAccountType Type, bool IsActive);

// ---- Gelir / Gider ----
public record FinanceTransactionDto(
    Guid Id, Guid CashAccountId, string CashAccountName, FinanceType Type,
    string? Category, decimal Amount, string? Description, PaymentMethod PaymentMethod, DateTime Date);

public record CreateFinanceTransactionRequest(
    Guid CashAccountId, FinanceType Type, string? Category, decimal Amount,
    string? Description, PaymentMethod PaymentMethod, DateTime? Date);

public class FinanceQuery : PagedQuery
{
    public FinanceType? Type { get; set; }
    public Guid? CashAccountId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public record FinanceSummaryDto(decimal TotalIncome, decimal TotalExpense, decimal Net, DateTime From, DateTime To);

// ---- Tekrarlayan giderler ----
public record RecurringExpenseDto(
    Guid Id, string Name, decimal Amount, string? Category,
    Guid CashAccountId, string CashAccountName, int DueDay, string? Description,
    bool IsActive, string? LastPostedPeriod, bool PostedThisPeriod);

public record CreateRecurringExpenseRequest(
    string Name, decimal Amount, string? Category, Guid CashAccountId, int DueDay, string? Description);

public record UpdateRecurringExpenseRequest(
    string Name, decimal Amount, string? Category, Guid CashAccountId, int DueDay, string? Description, bool IsActive);

/// <summary>Vadesi gelen tekrarlayan giderleri işleme sonucu (kaç adet gider üretildi + toplam tutar).</summary>
public record ProcessRecurringResultDto(int PostedCount, decimal PostedAmount);

// ---- Kasa vardiyası (açılış/kapanış + kör sayım) ----
public record CashShiftDto(
    Guid Id, Guid CashAccountId, string CashAccountName, string Status,
    DateTime OpenedAt, string? OpenedByName, decimal OpeningFloat,
    DateTime? ClosedAt, string? ClosedByName,
    decimal? CountedAmount, decimal? ExpectedAmount, decimal? Difference, string? Note);

public record OpenCashShiftRequest(Guid CashAccountId, decimal OpeningFloat, string? Note);
public record CloseCashShiftRequest(decimal CountedAmount, string? Note);

public interface ICashAccountService
{
    Task<List<CashAccountDto>> GetAllAsync(CancellationToken ct = default);
    Task<CashAccountDto> CreateAsync(CreateCashAccountRequest req, CancellationToken ct = default);
    Task<CashAccountDto> UpdateAsync(Guid id, UpdateCashAccountRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IFinanceService
{
    Task<PagedResult<FinanceTransactionDto>> GetAsync(FinanceQuery query, CancellationToken ct = default);
    Task<FinanceTransactionDto> CreateAsync(CreateFinanceTransactionRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<FinanceSummaryDto> GetSummaryAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    // Tekrarlayan giderler (şablon + otomatik işleme)
    Task<List<RecurringExpenseDto>> GetRecurringAsync(CancellationToken ct = default);
    Task<RecurringExpenseDto> CreateRecurringAsync(CreateRecurringExpenseRequest req, CancellationToken ct = default);
    Task<RecurringExpenseDto> UpdateRecurringAsync(Guid id, UpdateRecurringExpenseRequest req, CancellationToken ct = default);
    Task DeleteRecurringAsync(Guid id, CancellationToken ct = default);
    /// <summary>Vadesi gelmiş (DueDay ≤ bugün) ve bu ay işlenmemiş şablonları gider olarak yazar. İdempotent.</summary>
    Task<ProcessRecurringResultDto> ProcessDueRecurringAsync(CancellationToken ct = default);

    // Kasa vardiyası
    Task<CashShiftDto?> GetOpenShiftAsync(Guid cashAccountId, CancellationToken ct = default);
    Task<List<CashShiftDto>> GetShiftsAsync(CancellationToken ct = default);
    Task<CashShiftDto> OpenShiftAsync(OpenCashShiftRequest req, CancellationToken ct = default);
    Task<CashShiftDto> CloseShiftAsync(Guid id, CloseCashShiftRequest req, CancellationToken ct = default);
}
