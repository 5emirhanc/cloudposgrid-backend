using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Invoices;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Quotes;

/// <summary>
/// Teklif / proforma. MODÜLÜN TEK EN ÖNEMLİ KURALI: teklif BAĞLAYICI DEĞİLDİR —
/// burada stok hareketi, cari hareketi, kasa hareketi ve sadakat puanı OLUŞMAZ.
/// Tüm mali etki yalnızca <see cref="ConvertAsync"/> içinde, faturayla bir kez oluşur.
/// </summary>
public sealed class QuoteService : IQuoteService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;
    private readonly IInvoiceService _invoices;
    private readonly IAuditTrail _audit;

    public QuoteService(IApplicationDbContext db, ICurrentBranch branch, IInvoiceService invoices, IAuditTrail audit)
    {
        _db = db;
        _branch = branch;
        _invoices = invoices;
        _audit = audit;
    }

    public async Task<PagedResult<QuoteListItemDto>> GetAsync(QuoteQuery query, CancellationToken ct = default)
    {
        var q = _db.Quotes.AsQueryable();
        if (query.Status is QuoteStatus st) q = q.Where(x => x.Status == st);
        if (query.ContactId is Guid cid) q = q.Where(x => x.ContactId == cid);
        if (query.From is DateTime f) q = q.Where(x => x.Date >= f);
        if (query.To is DateTime to) q = q.Where(x => x.Date <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlLike.Contains(query.Search);
            q = q.Where(x => EF.Functions.ILike(x.Number, pattern)
                || (x.Contact != null && EF.Functions.ILike(x.Contact.Name, pattern))
                || (x.CustomerName != null && EF.Functions.ILike(x.CustomerName, pattern)));
        }

        var today = DateTime.UtcNow.Date;
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.Date).ThenByDescending(x => x.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(x => new QuoteListItemDto(
                x.Id, x.Number, x.Status,
                (x.Status == QuoteStatus.Draft || x.Status == QuoteStatus.Sent) && x.ValidUntil < today,
                x.Contact != null ? x.Contact.Name : null, x.CustomerName,
                x.Date, x.ValidUntil, x.GrandTotal, x.InvoiceId))
            .ToListAsync(ct);

        return new PagedResult<QuoteListItemDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<QuoteDto> GetByIdAsync(Guid id, CancellationToken ct = default)
        => ToDto(await LoadAsync(id, ct));

    public async Task<QuoteDto> CreateAsync(SaveQuoteRequest req, CancellationToken ct = default)
    {
        var (date, validUntil) = ResolveDates(req);
        var quote = new Quote
        {
            Number = await GenerateNumberAsync(date, ct),
            Status = QuoteStatus.Draft,
            ContactId = await ResolveContactAsync(req.ContactId, ct),
            CustomerName = Clean(req.CustomerName),
            Date = date,
            ValidUntil = validUntil,
            Note = Clean(req.Note),
            BranchId = await _branch.ResolveWriteBranchAsync(ct),
        };
        await FillLinesAsync(quote, req.Lines, ct);

        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync(ct);
        return ToDto(await LoadAsync(quote.Id, ct));
    }

    public async Task<QuoteDto> UpdateAsync(Guid id, SaveQuoteRequest req, CancellationToken ct = default)
    {
        // Satırları YÜKLEME: QuoteLine hem Quote hem Product required FK'sına sahip; Include edilmiş eski
        // satırları change-tracker üzerinden silmek EF fixup'ını orphan-Modified'a çeker (DELETE yerine
        // UPDATE dener → satır yok → 409). Bunun yerine eski satırlar ExecuteDelete ile doğrudan silinir.
        var quote = await _db.Quotes.FirstOrDefaultAsync(q => q.Id == id, ct)
            ?? throw NotFoundException.For("Teklif", id);
        if (quote.Status == QuoteStatus.Converted)
            throw new BusinessRuleException("Faturaya dönüştürülmüş teklif değiştirilemez.");

        var (date, validUntil) = ResolveDates(req);
        quote.ContactId = await ResolveContactAsync(req.ContactId, ct);
        quote.CustomerName = Clean(req.CustomerName);
        quote.Date = date;
        quote.ValidUntil = validUntil;
        quote.Note = Clean(req.Note);

        // Önce yeni satırları valide et + kur (geçersiz üründe burada patlar, DB'ye dokunulmaz);
        // sonra eski DB satırlarını change-tracker'ı bypass ederek sil (henüz yeni satırlar INSERT edilmedi,
        // sadece eskiler DB'de); en son tek SaveChanges yeni satırları yazar.
        await FillLinesAsync(quote, req.Lines, ct);
        await _db.QuoteLines.Where(l => l.QuoteId == id).ExecuteDeleteAsync(ct);
        await _db.SaveChangesAsync(ct);
        return ToDto(await LoadAsync(id, ct));
    }

    public async Task<QuoteDto> SetStatusAsync(Guid id, QuoteStatus status, CancellationToken ct = default)
    {
        var quote = await LoadAsync(id, ct);
        // Converted YALNIZ ConvertAsync tarafından yazılır — aksi halde faturasız "dönüştürülmüş" teklif oluşur.
        if (status == QuoteStatus.Converted)
            throw new BusinessRuleException("Teklif ancak faturaya dönüştürülerek bu duruma geçer.");
        if (quote.Status == QuoteStatus.Converted)
            throw new BusinessRuleException("Faturaya dönüştürülmüş teklifin durumu değiştirilemez.");

        quote.Status = status;
        if (status is QuoteStatus.Accepted or QuoteStatus.Rejected) quote.DecidedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(quote);
    }

    public async Task<QuoteDto> ConvertAsync(Guid id, ConvertQuoteRequest req, CancellationToken ct = default)
    {
        var quote = await LoadAsync(id, ct);
        if (quote.InvoiceId is not null)
            throw new BusinessRuleException("Bu teklif zaten faturaya dönüştürüldü.");
        if (quote.Status == QuoteStatus.Rejected)
            throw new BusinessRuleException("Reddedilmiş teklif faturaya dönüştürülemez.");
        if (quote.Lines.Count == 0)
            throw new BusinessRuleException("Boş teklif faturaya dönüştürülemez.");
        // Carisiz + ödemesiz dönüşümde kalan tutar hiçbir yere yazılamaz (OrderService.CloseAsync ile aynı kural).
        if (quote.ContactId is null && req.Payment is not { Amount: > 0 })
            throw new BusinessRuleException(
                "Kalan tutar hiçbir yere yazılamaz. Tam tahsilat yapın ya da veresiye için cari seçin.");

        var now = DateTime.UtcNow;
        // Durum faturadan ÖNCE yazılır: InvoiceService'in SaveChanges'i (aynı DbContext) ikisini
        // birlikte flush eder → çift dönüşüm penceresi kapanır. Fatura hata verirse hiçbiri yazılmaz.
        quote.Status = QuoteStatus.Converted;
        quote.DecidedAt ??= now;

        var invoice = await _invoices.CreateAsync(new CreateInvoiceRequest(
            InvoiceType.Sales, quote.ContactId, now, $"Teklif: {quote.Number}",
            quote.Lines.Select(l => new CreateInvoiceLineRequest(l.ProductId, l.Quantity, l.UnitPrice, l.VatRate)).ToList(),
            req.Payment,
            // Teklifteki fiyatlar zaten nihai/pazarlıklı → cari indirimi İKİNCİ kez uygulanmamalı.
            SkipContactDiscount: true), ct);

        quote.InvoiceId = invoice.Id;
        _audit.Record("QuoteConverted", "Quote", quote.Id, $"{quote.Number} → {invoice.Number} · {invoice.GrandTotal:0.00} ₺");
        await _db.SaveChangesAsync(ct);
        return ToDto(await LoadAsync(id, ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var quote = await LoadAsync(id, ct);
        if (quote.InvoiceId is not null)
            throw new BusinessRuleException("Faturaya dönüştürülmüş teklif silinemez.");
        _db.Quotes.Remove(quote);
        await _db.SaveChangesAsync(ct);
    }

    // ---- yardımcılar ----

    private async Task<Quote> LoadAsync(Guid id, CancellationToken ct)
        => await _db.Quotes.Include(q => q.Lines).Include(q => q.Contact)
            .FirstOrDefaultAsync(q => q.Id == id, ct)
            ?? throw NotFoundException.For("Teklif", id);

    private async Task<Guid?> ResolveContactAsync(Guid? contactId, CancellationToken ct)
    {
        if (contactId is not Guid cid) return null;
        if (!await _db.Contacts.AnyAsync(c => c.Id == cid, ct)) throw NotFoundException.For("Cari", cid);
        return cid;
    }

    private static (DateTime Date, DateTime ValidUntil) ResolveDates(SaveQuoteRequest req)
    {
        var date = req.Date ?? DateTime.UtcNow;
        var validUntil = req.ValidUntil ?? date.AddDays(15); // sektör alışkanlığı: 15 gün
        if (validUntil.Date < date.Date)
            throw new BusinessRuleException("Geçerlilik tarihi teklif tarihinden önce olamaz.");
        return (date, validUntil);
    }

    /// <summary>Satırları kurar ve toplamları hesaplar. Yuvarlama InvoiceService ile BİREBİR aynı olmalı,
    /// yoksa dönüşümde teklif tutarı ile fatura tutarı tutmaz.</summary>
    private async Task FillLinesAsync(Quote quote, List<SaveQuoteLineRequest>? lines, CancellationToken ct)
    {
        var rows = (lines ?? []).Where(l => l.Quantity > 0m).ToList();
        if (rows.Count == 0) throw new BusinessRuleException("Teklif en az bir kalem içermeli.");

        var ids = rows.Select(l => l.ProductId).Distinct().ToList();
        var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        if (products.Count != ids.Count) throw new BusinessRuleException("Teklifte geçersiz ürün var.");

        decimal subtotal = 0m, vatTotal = 0m;
        var newLines = new List<QuoteLine>();
        foreach (var l in rows)
        {
            var product = products.First(p => p.Id == l.ProductId);
            var unitPrice = Math.Max(0m, l.UnitPrice);
            var lineTotal = Math.Round(l.Quantity * unitPrice, 2);
            var vatAmount = Math.Round(lineTotal * l.VatRate / 100m, 2);
            subtotal += lineTotal;
            vatTotal += vatAmount;

            newLines.Add(new QuoteLine
            {
                // Quote NAVIGATION set edilmez — sadece QuoteId. Navigation set etmek quote.Lines fixup'ını
                // tetikleyip sil-yeniden kur'da eski satırı orphan-Modified'a çeker → 409.
                QuoteId = quote.Id,
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = l.Quantity,
                UnitPrice = unitPrice,
                VatRate = l.VatRate,
                LineTotal = lineTotal,
                VatAmount = vatAmount,
            });
        }
        _db.QuoteLines.AddRange(newLines); // DbSet'e ekle; quote.Lines navigation'ına DOKUNMA

        quote.Subtotal = Math.Round(subtotal, 2);
        quote.VatTotal = Math.Round(vatTotal, 2);
        quote.GrandTotal = quote.Subtotal + quote.VatTotal;
    }

    private async Task<string> GenerateNumberAsync(DateTime date, CancellationToken ct)
    {
        var seed = await _db.Quotes.CountAsync(ct) + 1;
        var next = await _db.NextDocumentNumberAsync("quote", seed, ct);
        return $"TKL-{date:yyyy}-{next:D5}";
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static QuoteDto ToDto(Quote q) => new(
        q.Id, q.Number, q.Status,
        q.Status is QuoteStatus.Draft or QuoteStatus.Sent && q.ValidUntil.Date < DateTime.UtcNow.Date,
        q.ContactId, q.Contact?.Name, q.CustomerName, q.Date, q.ValidUntil,
        q.Subtotal, q.VatTotal, q.GrandTotal, q.Note, q.InvoiceId,
        q.Lines.OrderBy(l => l.CreatedAt).Select(l => new QuoteLineDto(
            l.Id, l.ProductId, l.ProductName, l.Quantity, l.UnitPrice, l.VatRate, l.LineTotal, l.VatAmount)).ToList());
}
