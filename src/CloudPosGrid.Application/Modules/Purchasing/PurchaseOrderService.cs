using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Invoices;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Purchasing;

/// <summary>
/// Tedarikçi siparişi + mal kabul. Sipariş kendisi HİÇBİR mali hareket üretmez;
/// mal kabulde siparişe bağlı bir ALIŞ FATURASI kesilir ve stok/cari/kasa etkisi tamamen oradan gelir.
/// Bu sayede tek satır stok kodu yazılmaz ve faturanın iptali her şeyi geri alır.
/// </summary>
public sealed class PurchaseOrderService : IPurchaseOrderService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentBranch _branch;
    private readonly IAuditTrail _audit;
    private readonly IInvoiceService _invoices;

    public PurchaseOrderService(
        IApplicationDbContext db, ICurrentUser currentUser, ICurrentBranch branch,
        IAuditTrail audit, IInvoiceService invoices)
    {
        _db = db;
        _currentUser = currentUser;
        _branch = branch;
        _audit = audit;
        _invoices = invoices;
    }

    public async Task<PagedResult<PurchaseOrderListItemDto>> GetAsync(PurchaseOrderQuery query, CancellationToken ct = default)
    {
        var q = _db.PurchaseOrders.AsQueryable();
        if (query.Status is PurchaseOrderStatus st) q = q.Where(x => x.Status == st);
        if (query.OpenOnly)
            q = q.Where(x => x.Status == PurchaseOrderStatus.Sent || x.Status == PurchaseOrderStatus.PartiallyReceived);
        if (query.ContactId is Guid cid) q = q.Where(x => x.ContactId == cid);
        if (query.From is DateTime f) q = q.Where(x => x.OrderDate >= f);
        if (query.To is DateTime to) q = q.Where(x => x.OrderDate <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlLike.Contains(query.Search);
            q = q.Where(x => EF.Functions.ILike(x.Number, pattern) || EF.Functions.ILike(x.Contact.Name, pattern));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.OrderDate).ThenByDescending(x => x.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(x => new PurchaseOrderListItemDto(
                x.Id, x.Number, x.Contact.Name, x.Status, x.OrderDate, x.ExpectedDate, x.GrandTotal,
                x.Lines.Count,
                x.Lines.Sum(l => l.OrderedQuantity) <= 0m ? 0m
                    : x.Lines.Sum(l => l.ReceivedQuantity) / x.Lines.Sum(l => l.OrderedQuantity)))
            .ToListAsync(ct);

        return new PagedResult<PurchaseOrderListItemDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<PurchaseOrderDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var po = await LoadAsync(id, ct);
        var receipts = await _db.Invoices.Where(i => i.PurchaseOrderId == id)
            .OrderBy(i => i.Date)
            .Select(i => new PurchaseOrderReceiptDto(i.Id, i.Number, i.Date, i.GrandTotal, i.Status == InvoiceStatus.Cancelled))
            .ToListAsync(ct);
        return ToDto(po, receipts);
    }

    public async Task<PurchaseOrderDto> CreateAsync(CreatePurchaseOrderRequest req, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == req.ContactId, ct)
            ?? throw NotFoundException.For("Cari", req.ContactId);

        var date = req.OrderDate ?? DateTime.UtcNow;
        var po = new PurchaseOrder
        {
            Number = await GenerateNumberAsync(date, ct),
            ContactId = contact.Id,
            Status = PurchaseOrderStatus.Draft,
            OrderDate = date,
            ExpectedDate = req.ExpectedDate,
            Note = Clean(req.Note),
            BranchId = await _branch.ResolveWriteBranchAsync(ct),
            CreatedByUserId = _currentUser.UserId,
            CreatedByName = _currentUser.Email,
        };
        await FillLinesAsync(po, req.Lines, ct);

        _db.PurchaseOrders.Add(po);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(po.Id, ct);
    }

    public async Task<PurchaseOrderDto> UpdateAsync(Guid id, CreatePurchaseOrderRequest req, CancellationToken ct = default)
    {
        // Satırları YÜKLEME: PurchaseOrderLine hem PurchaseOrder hem Product required FK'sına sahip; Include
        // edilmiş eski satırları change-tracker üzerinden silmek EF fixup'ını orphan-Modified'a çeker
        // (DELETE yerine UPDATE dener → satır yok → 409). Eski satırlar ExecuteDelete ile doğrudan silinir.
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw NotFoundException.For("Satın alma siparişi", id);
        if (po.Status != PurchaseOrderStatus.Draft)
            throw new BusinessRuleException("Yalnız taslak sipariş değiştirilebilir. Gönderilmiş siparişi iptal edip yenisini oluşturun.");

        if (req.ContactId != po.ContactId)
        {
            _ = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == req.ContactId, ct)
                ?? throw NotFoundException.For("Cari", req.ContactId);
            po.ContactId = req.ContactId;
        }
        po.OrderDate = req.OrderDate ?? po.OrderDate;
        po.ExpectedDate = req.ExpectedDate;
        po.Note = Clean(req.Note);

        // Önce yeni satırları valide et + kur (geçersiz üründe patlar); sonra eski DB satırlarını
        // change-tracker'ı bypass ederek sil; en son tek SaveChanges yeni satırları yazar.
        await FillLinesAsync(po, req.Lines, ct);
        await _db.PurchaseOrderLines.Where(l => l.PurchaseOrderId == id).ExecuteDeleteAsync(ct);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<PurchaseOrderDto> SendAsync(Guid id, CancellationToken ct = default)
    {
        var po = await LoadAsync(id, ct);
        if (po.Status != PurchaseOrderStatus.Draft)
            throw new BusinessRuleException("Bu sipariş zaten gönderilmiş.");

        po.Status = PurchaseOrderStatus.Sent;
        _audit.Record("PurchaseOrderSent", "PurchaseOrder", po.Id, $"{po.Number} · {po.GrandTotal:0.00} ₺");
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<ReceivePurchaseOrderResultDto> ReceiveAsync(Guid id, ReceivePurchaseOrderRequest req, CancellationToken ct = default)
    {
        var po = await LoadAsync(id, ct);
        if (po.Status == PurchaseOrderStatus.Draft)
            throw new BusinessRuleException("Önce siparişi tedarikçiye gönderin.");
        if (po.Status == PurchaseOrderStatus.Cancelled)
            throw new BusinessRuleException("İptal edilmiş siparişe mal kabul yapılamaz.");
        if (po.Status == PurchaseOrderStatus.Received)
            throw new BusinessRuleException("Bu siparişin tamamı zaten teslim alınmış.");

        // Şube kilidi: stok, faturanın kesildiği şubeye girer. Sipariş başka şubeye aitse yanlış depoya girerdi.
        var branchId = await _branch.ResolveWriteBranchAsync(ct);
        if (po.BranchId is Guid pb && pb != branchId)
            throw new BusinessRuleException("Bu sipariş başka bir şubeye ait; mal kabul için o şubeye geçin.");

        var invLines = new List<CreateInvoiceLineRequest>();
        foreach (var r in req.Lines ?? [])
        {
            if (r.Quantity <= 0m) continue;
            var line = po.Lines.FirstOrDefault(l => l.Id == r.PurchaseOrderLineId)
                ?? throw new BusinessRuleException("Mal kabul satırı bu siparişte bulunamadı.");

            var remaining = line.OrderedQuantity - line.ReceivedQuantity;
            if (!req.AllowOverReceipt && r.Quantity > remaining + 0.0001m)
                throw new BusinessRuleException(
                    $"'{line.ProductName}' için kalan ({remaining:0.##}) miktardan fazla mal kabul edilemez.");

            line.ReceivedQuantity += r.Quantity;
            invLines.Add(new CreateInvoiceLineRequest(line.ProductId, r.Quantity, r.UnitPrice ?? line.UnitPrice, line.VatRate));
        }
        if (invLines.Count == 0) throw new BusinessRuleException("Mal kabul için en az bir satır girin.");

        // Durum faturadan ÖNCE güncellenir: aynı DbContext olduğundan InvoiceService'in SaveChanges'i
        // ikisini birlikte flush eder → fatura hata verirse (ör. geçersiz ürün) sipariş de değişmez.
        po.Status = po.Lines.All(l => l.ReceivedQuantity >= l.OrderedQuantity)
            ? PurchaseOrderStatus.Received
            : PurchaseOrderStatus.PartiallyReceived;

        var invoice = await _invoices.CreateAsync(new CreateInvoiceRequest(
            InvoiceType.Purchase, po.ContactId, req.Date ?? DateTime.UtcNow,
            Clean(req.Note) ?? $"{po.Number} mal kabulü",
            invLines, req.Payment,
            PurchaseOrderId: po.Id), ct);

        _audit.Record("PurchaseOrderReceived", "PurchaseOrder", po.Id,
            $"{po.Number} · {invoice.Number} · {invoice.GrandTotal:0.00} ₺");
        await _db.SaveChangesAsync(ct);

        return new ReceivePurchaseOrderResultDto(await GetByIdAsync(po.Id, ct), invoice.Id, invoice.Number, invoice.GrandTotal);
    }

    public async Task<PurchaseOrderDto> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var po = await LoadAsync(id, ct);
        if (po.Status == PurchaseOrderStatus.Received)
            throw new BusinessRuleException("Tamamı teslim alınmış sipariş iptal edilemez.");
        if (po.Status == PurchaseOrderStatus.Cancelled)
            throw new BusinessRuleException("Bu sipariş zaten iptal edilmiş.");

        // Kısmen gelmişse iptal EDİLEBİLİR: gelen mal faturasıyla kayıtlıdır, iptal yalnız kalanı kapatır.
        po.Status = PurchaseOrderStatus.Cancelled;
        _audit.Record("PurchaseOrderCancelled", "PurchaseOrder", po.Id, po.Number);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    // ---- yardımcılar ----

    private async Task<PurchaseOrder> LoadAsync(Guid id, CancellationToken ct)
        => await _db.PurchaseOrders.Include(p => p.Lines).Include(p => p.Contact)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw NotFoundException.For("Satın alma siparişi", id);

    private async Task FillLinesAsync(PurchaseOrder po, List<CreatePurchaseOrderLineRequest>? lines, CancellationToken ct)
    {
        // Aynı ürün iki satırda gelirse TOPLA: void geri-alması ProductId eşleşmesine dayanıyor,
        // mükerrer satır o eşleşmeyi bozar (ilk satır bulunur, ikincisi hiç geri alınmaz).
        var rows = (lines ?? []).Where(l => l.Quantity > 0m)
            .GroupBy(l => l.ProductId)
            .Select(g => new CreatePurchaseOrderLineRequest(
                g.Key, g.Sum(x => x.Quantity), g.Last().UnitPrice, g.Last().VatRate))
            .ToList();
        if (rows.Count == 0) throw new BusinessRuleException("Sipariş en az bir satır içermeli.");

        var ids = rows.Select(l => l.ProductId).ToList();
        // Hizmet kalemi sipariş edilemez (stok girişi yok) ve pasif ürün ısmarlanmaz.
        var products = await _db.Products.Where(p => ids.Contains(p.Id) && p.IsActive && !p.IsService).ToListAsync(ct);
        if (products.Count != ids.Count)
            throw new BusinessRuleException("Siparişte geçersiz ya da hizmet türünde ürün var.");

        decimal subtotal = 0m, vatTotal = 0m;
        var newLines = new List<PurchaseOrderLine>();
        foreach (var l in rows)
        {
            var product = products.First(p => p.Id == l.ProductId);
            var unitPrice = Math.Max(0m, l.UnitPrice);
            var lineTotal = Math.Round(l.Quantity * unitPrice, 2);
            subtotal += lineTotal;
            vatTotal += Math.Round(lineTotal * l.VatRate / 100m, 2);

            newLines.Add(new PurchaseOrderLine
            {
                // PurchaseOrder NAVIGATION set edilmez — sadece PurchaseOrderId. Navigation set etmek
                // po.Lines fixup'ını tetikleyip sil-yeniden kur'da eski satırı orphan-Modified'a çeker → 409.
                PurchaseOrderId = po.Id,
                ProductId = product.Id,
                ProductName = product.Name,
                OrderedQuantity = l.Quantity,
                ReceivedQuantity = 0m,
                UnitPrice = unitPrice,
                VatRate = l.VatRate,
            });
        }
        _db.PurchaseOrderLines.AddRange(newLines); // DbSet'e ekle; po.Lines navigation'ına DOKUNMA

        po.Subtotal = Math.Round(subtotal, 2);
        po.VatTotal = Math.Round(vatTotal, 2);
        po.GrandTotal = po.Subtotal + po.VatTotal;
    }

    private async Task<string> GenerateNumberAsync(DateTime date, CancellationToken ct)
    {
        var seed = await _db.PurchaseOrders.CountAsync(ct) + 1;
        var next = await _db.NextDocumentNumberAsync("purchase_order", seed, ct);
        return $"SIP-{date:yyyy}-{next:D5}";
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static PurchaseOrderDto ToDto(PurchaseOrder po, IReadOnlyList<PurchaseOrderReceiptDto> receipts)
    {
        var ordered = po.Lines.Sum(l => l.OrderedQuantity);
        return new PurchaseOrderDto(
            po.Id, po.Number, po.ContactId, po.Contact?.Name ?? "", po.Contact?.Phone, po.Status,
            po.OrderDate, po.ExpectedDate, po.Note,
            po.Subtotal, po.VatTotal, po.GrandTotal,
            ordered <= 0m ? 0m : Math.Round(po.Lines.Sum(l => l.ReceivedQuantity) / ordered, 4),
            po.Lines.OrderBy(l => l.CreatedAt).Select(l => new PurchaseOrderLineDto(
                l.Id, l.ProductId, l.ProductName, l.OrderedQuantity, l.ReceivedQuantity,
                Math.Max(0m, l.OrderedQuantity - l.ReceivedQuantity),
                l.UnitPrice, l.VatRate, Math.Round(l.OrderedQuantity * l.UnitPrice, 2))).ToList(),
            receipts);
    }
}
