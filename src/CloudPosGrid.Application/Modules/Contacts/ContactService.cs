using System.Linq.Expressions;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Contacts;

public sealed class ContactService : IContactService
{
    private readonly IApplicationDbContext _db;

    public ContactService(IApplicationDbContext db) => _db = db;

    /// <summary>EF Select içinde çevrilebilir projeksiyon.</summary>
    private static readonly Expression<Func<Contact, ContactDto>> Projection = c => new ContactDto(
        c.Id, c.Type, c.Name, c.TaxOffice, c.TaxNo, c.Phone, c.Email, c.Address, c.Balance, c.DiscountRate, c.PointsBalance, c.IsActive,
        c.Notes, c.Tags, c.Birthday, c.CreditLimit);

    public async Task<PagedResult<ContactDto>> GetAsync(ContactQuery query, CancellationToken ct = default)
    {
        var q = _db.Contacts.Where(c => c.IsActive);
        if (query.Type is ContactType t) q = q.Where(c => c.Type == t || c.Type == ContactType.Both);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlLike.Contains(query.Search);
            q = q.Where(c => EF.Functions.ILike(c.Name, pattern) || (c.Phone != null && EF.Functions.ILike(c.Phone, pattern)));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(c => c.Name)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(Projection).ToListAsync(ct);

        return new PagedResult<ContactDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<ContactDto> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Contacts.Where(c => c.Id == id).Select(Projection).FirstOrDefaultAsync(ct)
            ?? throw NotFoundException.For("Cari", id);

    public async Task<ContactDto> CreateAsync(CreateContactRequest req, CancellationToken ct = default)
    {
        var contact = new Contact
        {
            Type = req.Type,
            Name = req.Name.Trim(),
            TaxOffice = req.TaxOffice?.Trim(),
            TaxNo = req.TaxNo?.Trim(),
            Phone = req.Phone?.Trim(),
            Email = req.Email?.Trim(),
            Address = req.Address?.Trim(),
            Balance = 0,
            DiscountRate = Math.Clamp(req.DiscountRate, 0m, 100m),
            IsActive = true,
            Notes = req.Notes?.Trim(),
            Tags = req.Tags?.Trim(),
            Birthday = req.Birthday,
            CreditLimit = req.CreditLimit,
        };
        _db.Contacts.Add(contact);

        if (req.OpeningBalance != 0)
        {
            var dir = req.OpeningBalance > 0 ? TransactionDirection.Debit : TransactionDirection.Credit;
            contact.Balance = req.OpeningBalance;
            _db.AccountTransactions.Add(new AccountTransaction
            {
                Contact = contact,
                ContactId = contact.Id,
                Direction = dir,
                Amount = Math.Abs(req.OpeningBalance),
                BalanceAfter = contact.Balance,
                Description = "Açılış bakiyesi",
                Date = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync(ct);
        return Map(contact);
    }

    public async Task<ContactDto> UpdateAsync(Guid id, UpdateContactRequest req, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.FindAsync([id], ct) ?? throw NotFoundException.For("Cari", id);

        contact.Type = req.Type;
        contact.Name = req.Name.Trim();
        contact.TaxOffice = req.TaxOffice?.Trim();
        contact.TaxNo = req.TaxNo?.Trim();
        contact.Phone = req.Phone?.Trim();
        contact.Email = req.Email?.Trim();
        contact.Address = req.Address?.Trim();
        contact.DiscountRate = Math.Clamp(req.DiscountRate, 0m, 100m);
        contact.IsActive = req.IsActive;
        contact.Notes = req.Notes?.Trim();
        contact.Tags = req.Tags?.Trim();
        contact.Birthday = req.Birthday;
        contact.CreditLimit = req.CreditLimit;

        await _db.SaveChangesAsync(ct);
        return Map(contact);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.FindAsync([id], ct) ?? throw NotFoundException.For("Cari", id);
        contact.IsActive = false; // soft delete (ekstre korunur)
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ContactLedgerDto> GetLedgerAsync(Guid id, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.Where(c => c.Id == id).Select(Projection).FirstOrDefaultAsync(ct)
            ?? throw NotFoundException.For("Cari", id);

        var txns = await _db.AccountTransactions
            .Where(t => t.ContactId == id)
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt)
            .Select(t => new AccountTransactionDto(t.Id, t.ContactId, t.Direction, t.Amount, t.BalanceAfter, t.Description, t.DocRef, t.Date))
            .ToListAsync(ct);

        return new ContactLedgerDto(contact, txns);
    }

    public async Task<AccountTransactionDto> AddTransactionAsync(Guid id, CreateAccountTransactionRequest req, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.FindAsync([id], ct) ?? throw NotFoundException.For("Cari", id);

        var delta = req.Direction == TransactionDirection.Debit ? req.Amount : -req.Amount;
        contact.Balance += delta;

        var txn = new AccountTransaction
        {
            ContactId = id,
            Direction = req.Direction,
            Amount = req.Amount,
            BalanceAfter = contact.Balance,
            Description = req.Description,
            Date = req.Date ?? DateTime.UtcNow,
        };
        _db.AccountTransactions.Add(txn);
        await _db.SaveChangesAsync(ct);

        return new AccountTransactionDto(txn.Id, id, txn.Direction, txn.Amount, txn.BalanceAfter, txn.Description, txn.DocRef, txn.Date);
    }

    public async Task<Contact360Dto> GetOverviewAsync(Guid id, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.Where(c => c.Id == id).Select(Projection).FirstOrDefaultAsync(ct)
            ?? throw NotFoundException.For("Cari", id);

        var sales = _db.Invoices.Where(i => i.ContactId == id && i.Type == InvoiceType.Sales);
        var invCount = await sales.CountAsync(ct);
        var totalPurchases = Math.Round(await sales.SumAsync(i => (decimal?)i.GrandTotal, ct) ?? 0m, 2);
        var lastPurchase = await sales.OrderByDescending(i => i.Date).Select(i => (DateTime?)i.Date).FirstOrDefaultAsync(ct);
        var avgBasket = invCount > 0 ? Math.Round(totalPurchases / invCount, 2) : 0m;

        var recent = await sales.OrderByDescending(i => i.Date).Take(10)
            .Select(i => new ContactInvoiceBriefDto(i.Id, i.Number, i.Date, i.GrandTotal, i.PaidAmount, i.Status.ToString()))
            .ToListAsync(ct);

        // Sık alınan ürünler (bu cariye kesilen satış faturalarının satırları).
        var grouped = await _db.InvoiceLines
            .Where(l => l.Invoice.ContactId == id && l.Invoice.Type == InvoiceType.Sales)
            .GroupBy(l => new { l.ProductId, l.ProductName })
            .Select(g => new ContactPurchaseDto(g.Key.ProductId, g.Key.ProductName, g.Sum(x => x.Quantity), g.Sum(x => x.LineTotal)))
            .ToListAsync(ct);
        var topProducts = grouped.OrderByDescending(p => p.Total).Take(10).ToList();

        var apptCount = await _db.Appointments.CountAsync(a => a.ContactId == id, ct);
        var lastAppt = await _db.Appointments.Where(a => a.ContactId == id)
            .OrderByDescending(a => a.StartsAt).Select(a => (DateTime?)a.StartsAt).FirstOrDefaultAsync(ct);
        var quoteCount = await _db.Quotes.CountAsync(qq => qq.ContactId == id, ct);

        return new Contact360Dto(contact, totalPurchases, invCount, avgBasket, lastPurchase,
            contact.Balance, apptCount, lastAppt, quoteCount, topProducts, recent);
    }

    private static ContactDto Map(Contact c) => new(
        c.Id, c.Type, c.Name, c.TaxOffice, c.TaxNo, c.Phone, c.Email, c.Address, c.Balance, c.DiscountRate, c.PointsBalance, c.IsActive,
        c.Notes, c.Tags, c.Birthday, c.CreditLimit);
}
