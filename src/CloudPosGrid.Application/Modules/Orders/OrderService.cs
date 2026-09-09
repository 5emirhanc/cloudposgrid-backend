using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Invoices;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Orders;

public sealed class OrderService : IOrderService
{
    private readonly IApplicationDbContext _db;
    private readonly IInvoiceService _invoices;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentBranch _branch;
    private readonly IAuditTrail _audit;

    public OrderService(IApplicationDbContext db, IInvoiceService invoices, ICurrentUser currentUser, ICurrentBranch branch, IAuditTrail audit)
    {
        _db = db;
        _invoices = invoices;
        _currentUser = currentUser;
        _branch = branch;
        _audit = audit;
    }

    public async Task<List<OrderListItemDto>> GetOpenAsync(CancellationToken ct = default) =>
        await _db.Orders.Where(o => o.Status == OrderStatus.Open)
            .Where(o => _branch.HeaderBranchId == null || o.BranchId == _branch.HeaderBranchId)
            .OrderBy(o => o.OpenedAt)
            .Select(o => new OrderListItemDto(
                o.Id, o.Type, o.Status, o.Source, o.TableId, o.Table != null ? o.Table.Name : null, o.Label,
                o.AssetInfo, o.WorkStatus, o.OpenedAt,
                o.Lines.Sum(l => l.LineTotal + l.LineTotal * l.VatRate / 100m), o.Lines.Count))
            .ToListAsync(ct);

    public async Task<OrderDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var order = await _db.Orders.Include(o => o.Lines).Include(o => o.Table)
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw NotFoundException.For("Adisyon", id);
        return ToDto(order);
    }

    public async Task<OrderDto> OpenAsync(OpenOrderRequest req, CancellationToken ct = default)
    {
        if (req.Type == OrderType.DineIn)
        {
            if (req.TableId is not Guid tid)
                throw new BusinessRuleException("Masa siparişi için masa seçilmeli.");
            if (!await _db.DiningTables.AnyAsync(t => t.Id == tid, ct))
                throw NotFoundException.For("Masa", tid);
            if (await _db.Orders.AnyAsync(o => o.TableId == tid && o.Status == OrderStatus.Open, ct))
                throw new ConflictException("Bu masada zaten açık bir adisyon var.");
        }

        var order = new Order
        {
            Type = req.Type,
            Status = OrderStatus.Open,
            BranchId = await _branch.ResolveWriteBranchAsync(ct),
            TableId = req.Type == OrderType.DineIn ? req.TableId : null,
            ContactId = req.ContactId,
            Label = req.Label?.Trim(),
            Note = req.Note?.Trim(),
            AssetInfo = req.AssetInfo?.Trim(),
            WorkStatus = req.Type == OrderType.Service ? Domain.Enums.WorkStatus.Received : null,
            OpenedAt = DateTime.UtcNow,
            OpenedByUserId = _currentUser.UserId,
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(order.Id, ct);
    }

    public async Task<OrderDto> MoveAsync(Guid id, MoveOrderRequest req, CancellationToken ct = default)
    {
        var order = await OpenOrderWithLines(id, ct);
        if (order.Type != OrderType.DineIn)
            throw new BusinessRuleException("Yalnız masa adisyonu taşınabilir.");

        var target = await _db.DiningTables.FirstOrDefaultAsync(t => t.Id == req.TargetTableId, ct)
            ?? throw NotFoundException.For("Masa", req.TargetTableId);
        if (order.TableId == target.Id)
            throw new BusinessRuleException("Adisyon zaten bu masada.");

        var existing = await _db.Orders.Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.TableId == target.Id && o.Status == OrderStatus.Open, ct);

        if (existing is null)
        {
            // Boş masaya TAŞI
            order.TableId = target.Id;
            _audit.Record("OrderMoved", "Order", order.Id, $"Adisyon taşındı → {target.Name}");
            await _db.SaveChangesAsync(ct);
            return await GetByIdAsync(order.Id, ct);
        }

        if (!req.Merge)
            throw new ConflictException($"{target.Name} masasında açık adisyon var. Birleştirmek için onaylayın.");

        // BİRLEŞTİR: kaynak satırları hedefe taşı, kaynağı iptal et (stok hareketi YOK —
        // stok satır eklenirken değil, adisyon kapanırken faturada düşülüyor).
        foreach (var line in order.Lines.ToList())
        {
            line.OrderId = existing.Id;
            existing.Lines.Add(line);
            order.Lines.Remove(line);
        }
        Recalc(existing);
        order.Status = OrderStatus.Cancelled;
        order.ClosedAt = DateTime.UtcNow;
        order.Note = string.IsNullOrWhiteSpace(order.Note)
            ? $"{target.Name} masasıyla birleştirildi"
            : $"{order.Note} · {target.Name} masasıyla birleştirildi";
        Recalc(order);

        _audit.Record("OrderMerged", "Order", existing.Id,
            $"Adisyon birleştirildi → {target.Name} ({existing.Lines.Count} satır)");
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(existing.Id, ct);
    }

    public async Task<OrderDto> AddLineAsync(Guid orderId, AddOrderLineRequest req, CancellationToken ct = default)
    {
        var order = await OpenOrderWithLines(orderId, ct);
        if (req.Quantity <= 0) throw new BusinessRuleException("Miktar 0'dan büyük olmalı.");

        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == req.ProductId, ct)
            ?? throw NotFoundException.For("Ürün", req.ProductId);

        var note = req.Note?.Trim();
        // Notsuz aynı ürün varsa miktarı birleştir; aksi halde yeni satır.
        var existing = note is null
            ? order.Lines.FirstOrDefault(l => l.ProductId == product.Id && l.Note == null)
            : null;

        if (existing is not null)
        {
            existing.Quantity += req.Quantity;
            existing.LineTotal = Math.Round(existing.Quantity * existing.UnitPrice, 2);
        }
        else
        {
            var line = new OrderLine
            {
                OrderId = order.Id,
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = req.Quantity,
                UnitPrice = product.SalePrice,
                VatRate = product.VatRate,
                LineTotal = Math.Round(req.Quantity * product.SalePrice, 2),
                Note = note,
            };
            // Var olan (tracked) adisyona koleksiyondan eklersek EF, Guid Id dolu olduğu için
            // yeni satırı "mevcut kayıt" sanıp UPDATE üretir (0 satır -> concurrency hatası).
            // Açıkça Add ile INSERT'i garanti ediyoruz; EF ilişki fixup'ı satırı order.Lines'a
            // otomatik ekler (elle eklersek çift sayılır -> yanlış toplam).
            _db.OrderLines.Add(line);
        }

        Recalc(order);
        await _db.SaveChangesAsync(ct);
        return ToDto(order);
    }

    public async Task<OrderDto> UpdateLineAsync(Guid orderId, Guid lineId, UpdateOrderLineRequest req, CancellationToken ct = default)
    {
        var order = await OpenOrderWithLines(orderId, ct);
        var line = order.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw NotFoundException.For("Adisyon satırı", lineId);

        if (req.Quantity <= 0)
            _db.OrderLines.Remove(line);
        else
        {
            line.Quantity = req.Quantity;
            line.LineTotal = Math.Round(req.Quantity * line.UnitPrice, 2);
        }

        Recalc(order, removed: req.Quantity <= 0 ? line : null);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(order.Id, ct);
    }

    public async Task<OrderDto> RemoveLineAsync(Guid orderId, Guid lineId, CancellationToken ct = default)
    {
        var order = await OpenOrderWithLines(orderId, ct);
        var line = order.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw NotFoundException.For("Adisyon satırı", lineId);

        _db.OrderLines.Remove(line);
        Recalc(order, removed: line);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(order.Id, ct);
    }

    public async Task<OrderDto> SetWorkStatusAsync(Guid id, Domain.Enums.WorkStatus status, CancellationToken ct = default)
    {
        var order = await _db.Orders.Include(o => o.Lines).Include(o => o.Table)
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw NotFoundException.For("İş emri", id);
        if (order.Status != OrderStatus.Open)
            throw new BusinessRuleException("Kapalı/iptal iş emrinin durumu değiştirilemez.");

        order.WorkStatus = status;
        await _db.SaveChangesAsync(ct);
        return ToDto(order);
    }

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw NotFoundException.For("Adisyon", id);
        if (order.Status != OrderStatus.Open)
            throw new BusinessRuleException("Yalnızca açık adisyon iptal edilebilir.");

        order.Status = OrderStatus.Cancelled;
        order.ClosedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<OrderDto> CloseAsync(Guid id, CloseOrderRequest req, CancellationToken ct = default)
    {
        var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw NotFoundException.For("Adisyon", id);
        if (order.Status != OrderStatus.Open)
            throw new BusinessRuleException("Adisyon zaten kapalı ya da iptal edilmiş.");
        if (order.Lines.Count == 0)
            throw new BusinessRuleException("Boş adisyon kapatılamaz.");

        var now = DateTime.UtcNow;

        // Veresiye/kısmi ödeme: borç bu cariye yazılır.
        if (req.ContactId is Guid cid)
        {
            if (!await _db.Contacts.AnyAsync(c => c.Id == cid, ct))
                throw NotFoundException.For("Cari", cid);
            order.ContactId = cid;
        }

        // Carisiz kapanışta ödeme, tutarın TAMAMINI karşılamalı. Aksi halde kalan borç
        // hiçbir cariye yazılamayacağı için "buharlaşır" (kasada satış-tahsilat açığı oluşur,
        // gün sonu tutmaz). Eksik ödeme için cari/veresiye zorunlu.
        if (order.ContactId is null)
        {
            var expectedTotal = ExpectedGrandTotal(order.Lines, req.ManualDiscountPercent, req.ManualDiscountAmount);
            // Çoklu/karma ödeme: tüm parçaların toplamı tahsilatı karşılamalı.
            var paid = (req.Payments is { Count: > 0 })
                ? req.Payments.Sum(p => p.Amount)
                : (req.Payment?.Amount ?? 0m);
            if (paid < expectedTotal)
                throw new BusinessRuleException(
                    "Eksik ödeme: kalan tutar hiçbir yere yazılamaz. Tam tahsilat yapın ya da veresiye için cari seçin.");
        }

        // Durumu kapanmış olarak işaretle: InvoiceService.CreateAsync'in SaveChanges'i bunu da
        // faturayla AYNI işlemde flush eder (aynı DbContext). Çift kapatma/çift fatura engellenir.
        order.Status = OrderStatus.Closed;
        order.ClosedAt = now;

        var invoiceReq = new CreateInvoiceRequest(
            InvoiceType.Sales,
            order.ContactId,
            now,
            $"Adisyon: {order.Label ?? order.Id.ToString()[..8]}",
            order.Lines.Select(l => new CreateInvoiceLineRequest(l.ProductId, l.Quantity, l.UnitPrice, l.VatRate)).ToList(),
            req.Payment is { Amount: > 0 } p ? new InvoicePaymentRequest(p.CashAccountId, p.Amount, p.Method) : null,
            ManualDiscountPercent: req.ManualDiscountPercent,
            ManualDiscountAmount: req.ManualDiscountAmount,
            ManualDiscountReason: req.ManualDiscountReason,
            // Çoklu/karma ödeme (split tender): doluysa faturada tek Payment yerine bu liste işlenir.
            Payments: req.Payments?.Select(x => new InvoicePaymentRequest(x.CashAccountId, x.Amount, x.Method)).ToList());

        var invoice = await _invoices.CreateAsync(invoiceReq, ct);

        // Bahşiş: faturaya dahil değildir; kasaya ayrı gelir olarak işlenir.
        if (req.Tip > 0 && req.Payment is { } pay)
        {
            var cash = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == pay.CashAccountId, ct);
            if (cash is not null)
            {
                cash.Balance += req.Tip;
                _db.FinanceTransactions.Add(new FinanceTransaction
                {
                    CashAccountId = cash.Id,
                    Type = FinanceType.Income,
                    Category = "Bahşiş",
                    Amount = req.Tip,
                    Description = $"Bahşiş ({invoice.Number})",
                    PaymentMethod = pay.Method,
                    RefId = invoice.Id,
                    BranchId = order.BranchId, // adisyonun şubesi — yoksa şube filtresi bu geliri gizlerdi
                    Date = now,
                });
            }
        }

        // Fatura kimliğini ve kesin tutarları bağla (faturadan).
        order.InvoiceId = invoice.Id;
        order.Subtotal = invoice.Subtotal;
        order.VatTotal = invoice.VatTotal;
        order.GrandTotal = invoice.GrandTotal;
        await _db.SaveChangesAsync(ct);

        return await GetByIdAsync(order.Id, ct);
    }

    public async Task<OrderDto> SplitCloseAsync(Guid id, SplitCloseRequest req, CancellationToken ct = default)
    {
        var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw NotFoundException.For("Adisyon", id);
        if (order.Status != OrderStatus.Open)
            throw new BusinessRuleException("Adisyon zaten kapalı ya da iptal edilmiş.");
        if (req.Items is null || req.Items.Count == 0)
            throw new BusinessRuleException("Bölmek için en az bir ürün seçin.");

        var now = DateTime.UtcNow;

        if (req.ContactId is Guid cid)
        {
            if (!await _db.Contacts.AnyAsync(c => c.Id == cid, ct))
                throw NotFoundException.For("Cari", cid);
            order.ContactId = cid;
        }
        if (req.Payment is null && order.ContactId is null)
            throw new BusinessRuleException("Ödeme alınmayan (veresiye) bölme için cari seçilmeli.");

        // Seçilen porsiyonlardan fatura satırları kur, adisyon satırlarını azalt/sil.
        var invLines = new List<CreateInvoiceLineRequest>();
        var removed = new List<OrderLine>();
        foreach (var sel in req.Items)
        {
            if (sel.Quantity <= 0) continue;
            var line = order.Lines.FirstOrDefault(l => l.Id == sel.LineId)
                ?? throw NotFoundException.For("Adisyon satırı", sel.LineId);
            if (sel.Quantity > line.Quantity)
                throw new BusinessRuleException($"'{line.ProductName}' için seçilen miktar adisyondakinden fazla.");

            invLines.Add(new CreateInvoiceLineRequest(line.ProductId, sel.Quantity, line.UnitPrice, line.VatRate));

            if (sel.Quantity >= line.Quantity)
            {
                _db.OrderLines.Remove(line);
                removed.Add(line);
            }
            else
            {
                line.Quantity -= sel.Quantity;
                line.LineTotal = Math.Round(line.Quantity * line.UnitPrice, 2);
            }
        }
        if (invLines.Count == 0)
            throw new BusinessRuleException("Bölmek için en az bir ürün seçin.");

        // Carisiz kısmi bölmede de ödeme, bölünen porsiyonun tamamını karşılamalı;
        // aksi halde eksik tutar hiçbir yere yazılamaz (bkz. CloseAsync).
        if (order.ContactId is null)
        {
            decimal esub = 0, evat = 0;
            foreach (var il in invLines)
            {
                var lt = Math.Round(il.Quantity * il.UnitPrice, 2);
                esub += lt;
                evat += Math.Round(lt * il.VatRate / 100m, 2);
            }
            var expected = Math.Round(esub, 2) + Math.Round(evat, 2);
            // Elle indirim yalnız bölünen porsiyona uygulanır → beklenen tutar da o kadar düşer.
            if (req.ManualDiscountAmount is > 0m) expected -= req.ManualDiscountAmount.Value;
            else if (req.ManualDiscountPercent is > 0m) expected = Math.Round(expected * (1 - req.ManualDiscountPercent.Value / 100m), 2);
            expected = Math.Max(0m, expected);
            var paid = (req.Payments is { Count: > 0 })
                ? req.Payments.Sum(p => p.Amount)
                : (req.Payment?.Amount ?? 0m);
            if (paid < expected)
                throw new BusinessRuleException(
                    "Eksik ödeme: bölünen tutarın tamamını tahsil edin ya da veresiye için cari seçin.");
        }

        var invoiceReq = new CreateInvoiceRequest(
            InvoiceType.Sales, order.ContactId, now, $"Adisyon (kısmi): {order.Label ?? order.Id.ToString()[..8]}",
            invLines,
            req.Payment is { Amount: > 0 } p ? new InvoicePaymentRequest(p.CashAccountId, p.Amount, p.Method) : null,
            ManualDiscountPercent: req.ManualDiscountPercent,
            ManualDiscountAmount: req.ManualDiscountAmount,
            ManualDiscountReason: req.ManualDiscountReason,
            Payments: req.Payments?.Select(x => new InvoicePaymentRequest(x.CashAccountId, x.Amount, x.Method)).ToList());

        var invoice = await _invoices.CreateAsync(invoiceReq, ct);

        // Kalanları yeniden hesapla; hiç kalmadıysa adisyonu kapat.
        decimal sub = 0, vat = 0;
        foreach (var l in order.Lines)
        {
            if (removed.Contains(l)) continue;
            sub += l.LineTotal;
            vat += Math.Round(l.LineTotal * l.VatRate / 100m, 2);
        }
        order.Subtotal = Math.Round(sub, 2);
        order.VatTotal = Math.Round(vat, 2);
        order.GrandTotal = order.Subtotal + order.VatTotal;

        // Kalan satır sayısını DOĞRUDAN say. EF Core, Remove edilen satırı order.Lines
        // navigation koleksiyonundan zaten çıkarır; ayrıca removed.Count çıkarmak çift
        // sayıma yol açıp kısmi bölmede adisyonu yanlışlıkla kapatıyordu (kalan tahsil
        // edilmeden kayboluyordu). removed.Contains ile filtrelemek her iki durumda da doğru.
        var remainingLines = order.Lines.Count(l => !removed.Contains(l));
        if (remainingLines == 0)
        {
            order.Status = OrderStatus.Closed;
            order.ClosedAt = now;
            order.InvoiceId = invoice.Id;
            // Tam kapanışta adisyon toplamlarını faturadan bağla; yoksa yukarıdaki
            // "kalan satır" hesabı 0 bıraktığından adisyon 0 TL görünür (rapor/geçmiş yanlış).
            order.Subtotal = invoice.Subtotal;
            order.VatTotal = invoice.VatTotal;
            order.GrandTotal = invoice.GrandTotal;
        }

        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(order.Id, ct);
    }

    public async Task<OrderDto> PlacePublicAsync(PlacePublicOrderRequest req, CancellationToken ct = default)
    {
        if (req.Items is null || req.Items.Count == 0)
            throw new BusinessRuleException("Sipariş en az bir ürün içermeli.");
        // Anonim uçtan gelen istekleri sınırla: aşırı kalem/adet ile şişirme (abuse) engellenir.
        if (req.Items.Count > 50)
            throw new BusinessRuleException("Tek siparişte en fazla 50 kalem gönderilebilir.");
        if (req.Items.Any(i => i.Quantity > 99))
            throw new BusinessRuleException("Bir kalemde en fazla 99 adet sipariş verilebilir.");

        Order? order = null;
        Guid? tableBranchId = null;
        // Masaya QR siparişi: o masada açık adisyon varsa ona EKLENİR, yoksa yeni açılır.
        if (req.Type == OrderType.DineIn && req.TableId is Guid tid)
        {
            var table = await _db.DiningTables.AsNoTracking()
                .Where(t => t.Id == tid).Select(t => new { t.Id, t.BranchId }).FirstOrDefaultAsync(ct)
                ?? throw NotFoundException.For("Masa", tid);
            tableBranchId = table.BranchId; // şube masadan gelir; anonim başlığa güvenilmez
            order = await _db.Orders.Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.TableId == tid && o.Status == OrderStatus.Open, ct);
        }

        if (order is null)
        {
            // Anonim (QR) sipariş: X-Branch-Id başlığına GÜVENME (istemci taklit edebilir).
            // DineIn'de şubeyi masadan al; aksi halde varsayılan şubeye bağla.
            var branchId = tableBranchId ?? await DefaultBranchIdAsync(ct);
            order = new Order
            {
                Type = req.Type,
                Status = OrderStatus.Open,
                Source = OrderSource.Qr,
                BranchId = branchId,
                TableId = req.Type == OrderType.DineIn ? req.TableId : null,
                Label = Truncate(req.Label?.Trim(), 120), // kolon sınırı: 500 hatası yerine kibarca kırp
                OpenedAt = DateTime.UtcNow,
            };
            _db.Orders.Add(order);
        }

        var productIds = req.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _db.Products
            .Where(p => productIds.Contains(p.Id) && p.IsActive && p.IsVisibleOnMenu)
            .ToListAsync(ct);

        var addedLines = 0;
        foreach (var item in req.Items)
        {
            if (item.Quantity <= 0) continue;
            var product = products.FirstOrDefault(p => p.Id == item.ProductId)
                ?? throw new BusinessRuleException("Menüde olmayan ürün seçildi.");

            var line = new OrderLine
            {
                OrderId = order.Id,
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = item.Quantity,
                UnitPrice = product.SalePrice, // fiyat sunucudan alınır; istemciye güvenilmez
                VatRate = product.VatRate,
                LineTotal = Math.Round(item.Quantity * product.SalePrice, 2),
                Note = Truncate(item.Note?.Trim(), 300), // kolon sınırı
            };
            // Mevcut açık adisyona eklerken INSERT'i garanti et (bkz. AddLineAsync notu).
            // EF fixup satırı order.Lines'a ekler; elle eklemiyoruz (çift sayımı önler).
            _db.OrderLines.Add(line);
            addedLines++;
        }

        // Tüm kalemler geçersizse (ör. hepsi 0/negatif adet) boş adisyon açma.
        if (addedLines == 0)
            throw new BusinessRuleException("Sipariş en az bir geçerli ürün içermeli.");

        Recalc(order);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(order.Id, ct);
    }

    public async Task<OrderDto> PlaceMarketplaceAsync(PlaceMarketplaceOrderRequest req, CancellationToken ct = default)
    {
        if (req.Lines is null || req.Lines.Count == 0)
            throw new BusinessRuleException("Pazaryeri siparişi en az bir eşleşmiş ürün içermeli.");

        // Kanal carisi ("Trendyol (Pazaryeri)") — pazaryeri, ödeme yapılana kadar bize borçlu (alacak).
        // Veresiye satış olarak kapatıldığından carisiz-kısmi-ödeme guard'ına takılmaz.
        var contact = await GetOrCreateMarketplaceContactAsync(req.Channel, ct);

        var now = DateTime.UtcNow;
        var order = new Order
        {
            Type = OrderType.Delivery,
            Status = OrderStatus.Closed,
            Source = OrderSource.Marketplace,
            ContactId = contact.Id,
            Label = Truncate($"{req.Channel} #{req.OrderNumber}", 120),
            BranchId = await _branch.ResolveWriteBranchAsync(ct),
            OpenedAt = now,
            ClosedAt = now,
        };
        _db.Orders.Add(order);
        foreach (var l in req.Lines)
        {
            _db.OrderLines.Add(new OrderLine
            {
                OrderId = order.Id,
                ProductId = l.ProductId,
                ProductName = l.ProductName,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
                LineTotal = Math.Round(l.Quantity * l.UnitPrice, 2),
            });
        }

        // Satış faturası: kanal=Channel, ödeme yok → cari borçlanır + stok düşer (InvoiceService atomik).
        // AllowOversell: pazaryeri satışı zaten gerçekleşti; stok yetmese bile kaydedilir (stok negatife düşer).
        var invoiceReq = new CreateInvoiceRequest(
            InvoiceType.Sales, contact.Id, now, $"{req.Channel}: {req.OrderNumber}",
            req.Lines.Select(l => new CreateInvoiceLineRequest(l.ProductId, l.Quantity, l.UnitPrice, l.VatRate)).ToList(),
            Payment: null, Channel: req.Channel, AllowOversell: true);
        var invoice = await _invoices.CreateAsync(invoiceReq, ct);

        order.InvoiceId = invoice.Id;
        order.Subtotal = invoice.Subtotal;
        order.VatTotal = invoice.VatTotal;
        order.GrandTotal = invoice.GrandTotal;
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(order.Id, ct);
    }

    /// <summary>Kanal carisini bulur/oluşturur (ör. "Trendyol (Pazaryeri)"); pazaryeri alacakları burada birikir.</summary>
    private async Task<Contact> GetOrCreateMarketplaceContactAsync(string channel, CancellationToken ct)
    {
        var name = $"{channel} (Pazaryeri)";
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Name == name, ct);
        if (contact is null)
        {
            contact = new Contact { Type = ContactType.Customer, Name = name, IsActive = true };
            _db.Contacts.Add(contact);
            await _db.SaveChangesAsync(ct); // InvoiceService cariyi sorguyla bulabilsin diye önce kaydet
        }
        // Not: Contact.Name'de tekil kısıt yok (normal carilerde aynı ad olabilir); eşzamanlı ilk-içe-alma
        // kuramsal olarak iki kanal carisi oluşturabilir (düşük risk, yalnız rapor). Sipariş bazında
        // mükerrer içe-alma, MarketplaceSyncService'te claim-first (Pending) ile önlenir.
        return contact;
    }

    private async Task<Order> OpenOrderWithLines(Guid orderId, CancellationToken ct)
    {
        var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw NotFoundException.For("Adisyon", orderId);
        if (order.Status != OrderStatus.Open)
            throw new BusinessRuleException("Kapalı adisyon değiştirilemez.");
        return order;
    }

    /// <summary>Anonim (public) girdiyi kolon sınırına kırpar — 500 yerine veri güvenle kısalır.</summary>
    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    /// <summary>Varsayılan (yoksa herhangi bir) şube — anonim QR siparişinde başlığa güvenmeden kullanılır.</summary>
    private async Task<Guid> DefaultBranchIdAsync(CancellationToken ct)
    {
        var def = await _db.Branches.AsNoTracking()
            .OrderByDescending(b => b.IsDefault)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(ct);
        return def ?? Guid.Empty;
    }

    private static void Recalc(Order o, OrderLine? removed = null)
    {
        decimal sub = 0, vat = 0;
        foreach (var l in o.Lines)
        {
            if (l == removed) continue; // henüz koleksiyondan çıkmamış olabilir
            sub += l.LineTotal;
            vat += Math.Round(l.LineTotal * l.VatRate / 100m, 2);
        }
        o.Subtotal = Math.Round(sub, 2);
        o.VatTotal = Math.Round(vat, 2);
        o.GrandTotal = o.Subtotal + o.VatTotal;
    }

    /// <summary>Verilen satırlardan (indirimsiz) beklenen genel toplam — carisiz kapanışta
    /// eksik ödeme kontrolü için. Müşteri indirimi yalnızca cari seçiliyken uygulandığından,
    /// carisiz senaryoda bu değer fatura genel toplamıyla birebir örtüşür.</summary>
    private static decimal ExpectedGrandTotal(IEnumerable<OrderLine> lines, decimal? discountPct = null, decimal? discountAmt = null)
    {
        decimal sub = 0, vat = 0;
        foreach (var l in lines)
        {
            sub += l.LineTotal;
            vat += Math.Round(l.LineTotal * l.VatRate / 100m, 2);
        }
        var gross = Math.Round(sub, 2) + Math.Round(vat, 2);
        // Elle indirim beklenen tutarı da düşürür — yoksa indirimli tam tahsilat "eksik ödeme" sanılır.
        // Kuruş kayması olabileceğinden karşılaştırma bu değere göre yapılır (fatura kesin tutarı üretir).
        if (discountAmt is > 0m) gross -= discountAmt.Value;
        else if (discountPct is > 0m) gross = Math.Round(gross * (1 - discountPct.Value / 100m), 2);
        return Math.Max(0m, gross);
    }

    private static OrderDto ToDto(Order o) => new(
        o.Id, o.Type, o.Status, o.Source, o.TableId, o.Table?.Name, o.ContactId, o.Label, o.Note,
        o.AssetInfo, o.WorkStatus, o.OpenedAt, o.Subtotal, o.VatTotal, o.GrandTotal, o.InvoiceId,
        o.Lines.OrderBy(l => l.CreatedAt).Select(l => new OrderLineDto(
            l.Id, l.ProductId, l.ProductName, l.Quantity, l.UnitPrice, l.VatRate, l.LineTotal, l.Note)).ToList());
}
