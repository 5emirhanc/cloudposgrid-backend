using System.Globalization;
using System.Linq.Expressions;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Finance;

public sealed class FinanceService : IFinanceService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;
    private readonly IAuditTrail _audit;
    private readonly ICurrentUser _currentUser;

    public FinanceService(IApplicationDbContext db, ICurrentBranch branch, IAuditTrail audit, ICurrentUser currentUser)
    {
        _db = db;
        _branch = branch;
        _audit = audit;
        _currentUser = currentUser;
    }

    private static readonly Expression<Func<FinanceTransaction, FinanceTransactionDto>> Projection = t =>
        new FinanceTransactionDto(t.Id, t.CashAccountId, t.CashAccount.Name, t.Type,
            t.Category, t.Amount, t.Description, t.PaymentMethod, t.Date);

    public async Task<PagedResult<FinanceTransactionDto>> GetAsync(FinanceQuery query, CancellationToken ct = default)
    {
        var q = _db.FinanceTransactions.AsQueryable();
        if (_branch.HeaderBranchId is Guid br) q = q.Where(x => x.BranchId == br);
        if (query.Type is FinanceType t) q = q.Where(x => x.Type == t);
        if (query.CashAccountId is Guid acc) q = q.Where(x => x.CashAccountId == acc);
        if (query.From is DateTime f) q = q.Where(x => x.Date >= f);
        if (query.To is DateTime to) q = q.Where(x => x.Date <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim().ToLower();
            q = q.Where(x => (x.Description != null && x.Description.ToLower().Contains(s)) ||
                             (x.Category != null && x.Category.ToLower().Contains(s)));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.Date).ThenByDescending(x => x.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(Projection).ToListAsync(ct);

        return new PagedResult<FinanceTransactionDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<FinanceTransactionDto> CreateAsync(CreateFinanceTransactionRequest req, CancellationToken ct = default)
    {
        var account = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == req.CashAccountId, ct)
            ?? throw NotFoundException.For("Kasa", req.CashAccountId);

        // Gelir kasayı artırır, gider azaltır.
        account.Balance += req.Type == FinanceType.Income ? req.Amount : -req.Amount;

        var txn = new FinanceTransaction
        {
            CashAccountId = account.Id,
            Type = req.Type,
            Category = req.Category?.Trim(),
            Amount = req.Amount,
            Description = req.Description?.Trim(),
            PaymentMethod = req.PaymentMethod,
            Date = req.Date ?? DateTime.UtcNow,
            BranchId = account.BranchId ?? await _branch.ResolveWriteBranchAsync(ct),
        };
        _db.FinanceTransactions.Add(txn);

        // Sorumluluk izi: elle kasa giriş/çıkışı — kim, hangi kasadan ne kadar?
        _audit.Record(
            req.Type == FinanceType.Income ? "CashIncomeCreated" : "CashExpenseCreated",
            "FinanceTransaction", txn.Id, $"{account.Name} · {txn.Amount:0.00} ₺ · {txn.Category ?? "-"}");

        await _db.SaveChangesAsync(ct);

        return new FinanceTransactionDto(txn.Id, account.Id, account.Name, txn.Type,
            txn.Category, txn.Amount, txn.Description, txn.PaymentMethod, txn.Date);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var txn = await _db.FinanceTransactions.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw NotFoundException.For("Hareket", id);

        // Fatura/ödeme/bahşiş kaynaklı hareketler (RefId dolu) doğrudan silinemez: yalnız kasa
        // bakiyesi geri alınır, bağlı fatura.PaidAmount / cari bakiye düzeltilmez → tutarsızlık.
        // Bu kayıtlar ancak ilgili fatura iptal/iade edilerek geri alınmalıdır.
        if (txn.RefId is not null)
            throw new BusinessRuleException(
                "Bu hareket bir fatura/ödeme kaydına bağlı; doğrudan silinemez. İlgili faturayı iptal edin.");

        var account = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == txn.CashAccountId, ct);
        if (account is not null)
            account.Balance -= txn.Type == FinanceType.Income ? txn.Amount : -txn.Amount; // ters çevir

        _db.FinanceTransactions.Remove(txn);

        // Sorumluluk izi: kasa hareketi SİLİNDİ — kim sildi? (silinen kayıt kaybolur, iz kalır)
        _audit.Record("CashTransactionDeleted", "FinanceTransaction", txn.Id,
            $"{txn.Type} · {txn.Amount:0.00} ₺ · {txn.Category ?? "-"}");

        await _db.SaveChangesAsync(ct);
    }

    public async Task<FinanceSummaryDto> GetSummaryAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var fromDate = from ?? DateTime.UtcNow.Date.AddDays(-30);
        var toDate = to ?? DateTime.UtcNow.Date.AddDays(1);

        var q = _db.FinanceTransactions.Where(x => x.Date >= fromDate && x.Date <= toDate);
        if (_branch.HeaderBranchId is Guid br) q = q.Where(x => x.BranchId == br);
        var income = await q.Where(x => x.Type == FinanceType.Income).SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var expense = await q.Where(x => x.Type == FinanceType.Expense).SumAsync(x => (decimal?)x.Amount, ct) ?? 0;

        return new FinanceSummaryDto(income, expense, income - expense, fromDate, toDate);
    }

    // ---- Tekrarlayan giderler ----
    private static string CurrentPeriod() => DateTime.UtcNow.ToString("yyyy-MM");

    private static RecurringExpenseDto MapRecurring(RecurringExpense r, string acctName, string period) =>
        new(r.Id, r.Name, r.Amount, r.Category, r.CashAccountId, acctName, r.DueDay,
            r.Description, r.IsActive, r.LastPostedPeriod, r.LastPostedPeriod == period);

    public async Task<List<RecurringExpenseDto>> GetRecurringAsync(CancellationToken ct = default)
    {
        var period = CurrentPeriod();
        // ŞUBE İZOLASYONU: RecurringExpense'in kendi BranchId'si yoktur; şubesi bağlı olduğu KASA üzerinden
        // gelir. CashAccounts global sorgu filtresi (şube) zaten uygulanır → IgnoreQueryFilters KULLANMAYIZ,
        // aksi halde kullanıcı erişemediği şubenin kasa ADINI ve o kasaya bağlı gider şablonlarını görürdü.
        var names = await _db.CashAccounts.ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var rows = await _db.RecurringExpenses
            .Where(r => _db.CashAccounts.Any(a => a.Id == r.CashAccountId))
            .OrderByDescending(r => r.IsActive).ThenBy(r => r.DueDay).ThenBy(r => r.Name)
            .ToListAsync(ct);
        return rows.Select(r => MapRecurring(r, names.GetValueOrDefault(r.CashAccountId, ""), period)).ToList();
    }

    public async Task<RecurringExpenseDto> CreateRecurringAsync(CreateRecurringExpenseRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new BusinessRuleException("Gider adı gerekli.");
        if (req.Amount <= 0) throw new BusinessRuleException("Tutar 0'dan büyük olmalı.");
        var account = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == req.CashAccountId, ct)
            ?? throw NotFoundException.For("Kasa", req.CashAccountId);

        var r = new RecurringExpense
        {
            Name = req.Name.Trim(),
            Amount = req.Amount,
            Category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim(),
            CashAccountId = account.Id,
            DueDay = Math.Clamp(req.DueDay, 1, 28),
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            IsActive = true,
        };
        _db.RecurringExpenses.Add(r);
        await _db.SaveChangesAsync(ct);
        return MapRecurring(r, account.Name, CurrentPeriod());
    }

    public async Task<RecurringExpenseDto> UpdateRecurringAsync(Guid id, UpdateRecurringExpenseRequest req, CancellationToken ct = default)
    {
        var r = await _db.RecurringExpenses
            .Where(x => _db.CashAccounts.Any(a => a.Id == x.CashAccountId)) // şube izolasyonu: yalnız erişilebilir kasanın şablonu
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw NotFoundException.For("Tekrarlayan gider", id);
        if (string.IsNullOrWhiteSpace(req.Name)) throw new BusinessRuleException("Gider adı gerekli.");
        if (req.Amount <= 0) throw new BusinessRuleException("Tutar 0'dan büyük olmalı.");
        var account = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == req.CashAccountId, ct)
            ?? throw NotFoundException.For("Kasa", req.CashAccountId);

        r.Name = req.Name.Trim();
        r.Amount = req.Amount;
        r.Category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim();
        r.CashAccountId = account.Id;
        r.DueDay = Math.Clamp(req.DueDay, 1, 28);
        r.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        r.IsActive = req.IsActive;
        await _db.SaveChangesAsync(ct);
        return MapRecurring(r, account.Name, CurrentPeriod());
    }

    public async Task DeleteRecurringAsync(Guid id, CancellationToken ct = default)
    {
        var r = await _db.RecurringExpenses
            .Where(x => _db.CashAccounts.Any(a => a.Id == x.CashAccountId)) // şube izolasyonu: yalnız erişilebilir kasanın şablonu
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw NotFoundException.For("Tekrarlayan gider", id);
        // Şablon silinir; daha önce üretilmiş gider hareketleri bağımsızdır, korunur.
        _db.RecurringExpenses.Remove(r);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ProcessRecurringResultDto> ProcessDueRecurringAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var currentPeriod = now.ToString("yyyy-MM");
        var currentMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var floor = currentMonth.AddMonths(-24); // en fazla 24 ay geriye telafi (runaway koruması)

        // Aktif ve bu ayı henüz işlememiş şablonlar. Arka planda zamanlanmış iş yok → sayfa geç açılırsa
        // ATLANAN AYLAR KAYBOLMASIN diye her şablon için son işlenen ay ile bugün arası TELAFİ (catch-up) edilir.
        var templates = await _db.RecurringExpenses
            .Where(r => r.IsActive && (r.LastPostedPeriod == null || r.LastPostedPeriod != currentPeriod))
            .Where(r => _db.CashAccounts.Any(a => a.Id == r.CashAccountId)) // şube izolasyonu (kasa üzerinden)
            .ToListAsync(ct);
        if (templates.Count == 0) return new ProcessRecurringResultDto(0, 0m);

        var acctIds = templates.Select(d => d.CashAccountId).Distinct().ToList();
        var accounts = await _db.CashAccounts.IgnoreQueryFilters()
            .Where(a => acctIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        Guid? fallbackBranch = null;
        var count = 0;
        decimal total = 0m;
        foreach (var r in templates)
        {
            if (!accounts.TryGetValue(r.CashAccountId, out var account)) continue; // kasa yoksa atla (FK korur)

            // İşlenecek ilk ay: son işlenen aydan sonrası; hiç işlenmemişse şablonun oluşturulduğu ay
            // (oluşturulmadan önceki aylar için gider üretilmez). 24 ay tabanıyla sınırlı.
            DateTime firstMonth;
            if (r.LastPostedPeriod is { Length: 7 } lp && DateTime.TryParseExact(
                    lp + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var lastM))
                firstMonth = new DateTime(lastM.Year, lastM.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            else
                firstMonth = new DateTime(r.CreatedAt.Year, r.CreatedAt.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            if (firstMonth < floor) firstMonth = floor;

            for (var m = firstMonth; m <= currentMonth; m = m.AddMonths(1))
            {
                var period = m.ToString("yyyy-MM");
                if (r.LastPostedPeriod == period) continue; // idempotent güvenlik
                var day = Math.Min(r.DueDay, DateTime.DaysInMonth(m.Year, m.Month));
                var dueDate = new DateTime(m.Year, m.Month, day, 0, 0, 0, DateTimeKind.Utc);
                if (dueDate > now) continue; // vade henüz gelmedi (yalnız cari ay için etkili)

                account.Balance -= r.Amount; // gider kasayı azaltır
                _db.FinanceTransactions.Add(new FinanceTransaction
                {
                    CashAccountId = account.Id,
                    Type = FinanceType.Expense,
                    Category = r.Category,
                    Amount = r.Amount,
                    Description = string.IsNullOrWhiteSpace(r.Description) ? $"Otomatik: {r.Name}" : $"{r.Description} (Otomatik: {r.Name})",
                    PaymentMethod = PaymentMethod.Cash,
                    Date = dueDate,
                    BranchId = account.BranchId ?? (fallbackBranch ??= await _branch.ResolveWriteBranchAsync(ct)),
                });
                r.LastPostedPeriod = period; // idempotent: her ay tek kez
                count++;
                total += r.Amount;
            }
        }

        if (count > 0)
            _audit.Record("RecurringExpensesPosted", "FinanceTransaction", null, $"{count} tekrarlayan gider işlendi · {total:0.00} ₺");
        await _db.SaveChangesAsync(ct);
        return new ProcessRecurringResultDto(count, total);
    }

    // ---- Kasa vardiyası ----
    private static CashShiftDto MapShift(CashShift s, string acctName) =>
        new(s.Id, s.CashAccountId, acctName, s.Status.ToString(), s.OpenedAt, s.OpenedByName, s.OpeningFloat,
            s.ClosedAt, s.ClosedByName, s.CountedAmount, s.ExpectedAmount, s.Difference, s.Note);

    public async Task<CashShiftDto?> GetOpenShiftAsync(Guid cashAccountId, CancellationToken ct = default)
    {
        var s = await _db.CashShifts
            .Where(x => x.CashAccountId == cashAccountId && x.Status == CashShiftStatus.Open)
            .OrderByDescending(x => x.OpenedAt).FirstOrDefaultAsync(ct);
        if (s is null) return null;
        var name = await _db.CashAccounts.IgnoreQueryFilters()
            .Where(a => a.Id == cashAccountId).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? "";
        return MapShift(s, name);
    }

    public async Task<List<CashShiftDto>> GetShiftsAsync(CancellationToken ct = default)
    {
        var names = await _db.CashAccounts.IgnoreQueryFilters().ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var rows = await _db.CashShifts.OrderByDescending(x => x.OpenedAt).Take(50).ToListAsync(ct);
        return rows.Select(s => MapShift(s, names.GetValueOrDefault(s.CashAccountId, ""))).ToList();
    }

    public async Task<CashShiftDto> OpenShiftAsync(OpenCashShiftRequest req, CancellationToken ct = default)
    {
        var account = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == req.CashAccountId, ct)
            ?? throw NotFoundException.For("Kasa", req.CashAccountId);
        if (req.OpeningFloat < 0) throw new BusinessRuleException("Açılış nakdi negatif olamaz.");

        // Fiziksel kasada aynı anda tek açık vardiya (filtreyi yok say → başka şube bağlamı gizlemesin).
        var exists = await _db.CashShifts.IgnoreQueryFilters()
            .AnyAsync(x => x.CashAccountId == account.Id && x.Status == CashShiftStatus.Open, ct);
        if (exists) throw new BusinessRuleException("Bu kasada zaten açık bir vardiya var. Önce onu kapatın.");

        var shift = new CashShift
        {
            CashAccountId = account.Id,
            Status = CashShiftStatus.Open,
            OpenedAt = DateTime.UtcNow,
            OpenedByUserId = _currentUser.UserId,
            OpenedByName = _currentUser.Email,
            OpeningFloat = req.OpeningFloat,
            BranchId = account.BranchId ?? await _branch.ResolveWriteBranchAsync(ct),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
        };
        _db.CashShifts.Add(shift);
        _audit.Record("CashShiftOpened", "CashShift", shift.Id, $"{account.Name} · açılış {req.OpeningFloat:0.00} ₺");
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // partial unique index (0031): eşzamanlı ikinci açılış → yarış DB'de reddedilir
        {
            throw new BusinessRuleException("Bu kasada zaten açık bir vardiya var. Önce onu kapatın.");
        }
        return MapShift(shift, account.Name);
    }

    public async Task<CashShiftDto> CloseShiftAsync(Guid id, CloseCashShiftRequest req, CancellationToken ct = default)
    {
        var shift = await _db.CashShifts.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw NotFoundException.For("Vardiya", id);
        if (shift.Status == CashShiftStatus.Closed)
            throw new BusinessRuleException("Vardiya zaten kapatılmış.");
        if (req.CountedAmount < 0) throw new BusinessRuleException("Sayılan tutar negatif olamaz.");

        var closeTime = DateTime.UtcNow;
        // Vardiya süresince (fiziksel akış = CreatedAt) bu kasadaki net NAKİT hareketi: nakit gelir − nakit gider.
        // Kör sayım fiziksel ÇEKMECEYİ mutabık kılar → yalnız PaymentMethod=Cash sayılır; kart/havale çekmeceye
        // girmez (aynı kasaya Income yazılsa bile), yoksa kart cirosu sahte "kasa açığı" üretir.
        // Tüm hesap hareketlerini gör (şube filtresi kapalı) → doğru mutabakat.
        var q = _db.FinanceTransactions.IgnoreQueryFilters()
            .Where(f => f.CashAccountId == shift.CashAccountId && f.PaymentMethod == PaymentMethod.Cash
                && f.CreatedAt >= shift.OpenedAt && f.CreatedAt <= closeTime);
        var income = await q.Where(f => f.Type == FinanceType.Income).SumAsync(f => (decimal?)f.Amount, ct) ?? 0m;
        var expense = await q.Where(f => f.Type == FinanceType.Expense).SumAsync(f => (decimal?)f.Amount, ct) ?? 0m;
        var expected = shift.OpeningFloat + income - expense;

        shift.ExpectedAmount = expected;
        shift.CountedAmount = req.CountedAmount;
        shift.Difference = req.CountedAmount - expected;
        shift.Status = CashShiftStatus.Closed;
        shift.ClosedAt = closeTime;
        shift.ClosedByUserId = _currentUser.UserId;
        shift.ClosedByName = _currentUser.Email;
        if (!string.IsNullOrWhiteSpace(req.Note)) shift.Note = req.Note.Trim();

        var acctName = await _db.CashAccounts.IgnoreQueryFilters()
            .Where(a => a.Id == shift.CashAccountId).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? "";
        // Sorumluluk izi: kasa mutabakatı — fark (açık/fazla) kim tarafından, ne kadar?
        _audit.Record("CashShiftClosed", "CashShift", shift.Id,
            $"{acctName} · beklenen {expected:0.00} / sayılan {req.CountedAmount:0.00} / fark {shift.Difference:0.00} ₺");
        await _db.SaveChangesAsync(ct);
        return MapShift(shift, acctName);
    }
}
