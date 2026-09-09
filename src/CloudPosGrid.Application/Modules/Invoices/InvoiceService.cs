using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloudPosGrid.Application.Modules.Invoices;

public sealed class InvoiceService : IInvoiceService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IPaymentProvider _payment;
    private readonly ICurrentBranch _branch;
    private readonly IPlanEntitlementProvider _entitlements;
    private readonly IAuditTrail _audit;

    public InvoiceService(IApplicationDbContext db, ICurrentUser currentUser, IPaymentProvider payment, ICurrentBranch branch, IPlanEntitlementProvider entitlements, IAuditTrail audit)
    {
        _db = db;
        _currentUser = currentUser;
        _payment = payment;
        _branch = branch;
        _entitlements = entitlements;
        _audit = audit;
    }

    public async Task<PagedResult<InvoiceListItemDto>> GetAsync(InvoiceQuery query, CancellationToken ct = default)
    {
        var q = _db.Invoices.AsQueryable();
        if (_branch.HeaderBranchId is Guid br) q = q.Where(i => i.BranchId == br);
        if (query.Type is InvoiceType t) q = q.Where(i => i.Type == t);
        if (query.Status is InvoiceStatus st) q = q.Where(i => i.Status == st);
        if (query.ContactId is Guid cid) q = q.Where(i => i.ContactId == cid);
        if (query.From is DateTime f) q = q.Where(i => i.Date >= f);
        if (query.To is DateTime to) q = q.Where(i => i.Date <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlLike.Contains(query.Search);
            q = q.Where(i => EF.Functions.ILike(i.Number, pattern) || (i.Contact != null && EF.Functions.ILike(i.Contact.Name, pattern)));
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(i => i.Date).ThenByDescending(i => i.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(i => new InvoiceListItemDto(i.Id, i.Type, i.Number,
                i.Contact != null ? i.Contact.Name : null, i.Date, i.GrandTotal, i.PaidAmount, i.Status))
            .ToListAsync(ct);

        return new PagedResult<InvoiceListItemDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<InvoiceDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var inv = await _db.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Contact)
            .FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw NotFoundException.For("Fatura", id);

        return ToDto(inv);
    }

    public async Task<InvoiceDto> CreateAsync(CreateInvoiceRequest req, CancellationToken ct = default, Guid? sellerUserId = null)
    {
        if (req.Lines is null || req.Lines.Count == 0)
            throw new BusinessRuleException("Fatura en az bir satır içermeli.");

        // Çevrimdışı POS kuyruğu yeniden gönderiminde ÇİFT satışı önle (idempotency): aynı istemci-satış
        // kimliğiyle zaten bir fatura kesildiyse yenisini oluşturma, mevcut faturayı döndür.
        var clientSaleId = string.IsNullOrWhiteSpace(req.ClientSaleId) ? null : req.ClientSaleId.Trim();
        if (clientSaleId is not null)
        {
            // IgnoreQueryFilters ŞART: ClientSaleId unique index'i TENANT geneli (şube ayrımsız). Şube
            // query filter'ıyla arasaydık, çok-şubeli kullanıcı aktif şubesini değiştirip aynı satışı
            // yeniden gönderince mevcut faturayı göremez, yeniden kurmaya kalkar ve 23505'e çarpardı.
            var duplicate = await _db.Invoices.AsNoTracking().IgnoreQueryFilters()
                .Include(i => i.Lines).Include(i => i.Contact)
                .FirstOrDefaultAsync(i => i.ClientSaleId == clientSaleId, ct);
            if (duplicate is not null) return ToDto(duplicate);
        }

        var productIds = req.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _db.Products.Where(p => productIds.Contains(p.Id)).ToListAsync(ct);
        if (products.Count != productIds.Count)
            throw new BusinessRuleException("Faturada geçersiz ürün var.");

        // Reçete/BOM (yalnız SATIŞTA): bileşik ürün satılınca KENDİ stoğu değil bileşenleri düşer.
        var recipesByProduct = new Dictionary<Guid, List<RecipeComponent>>();
        var componentProducts = new Dictionary<Guid, Product>();
        if (req.Type == InvoiceType.Sales)
        {
            var recipeRows = await _db.RecipeComponents.Where(r => productIds.Contains(r.ProductId)).ToListAsync(ct);
            if (recipeRows.Count > 0)
            {
                recipesByProduct = recipeRows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.ToList());
                var compIds = recipeRows.Select(r => r.ComponentProductId).Distinct().ToList();
                componentProducts = await _db.Products.Where(p => compIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, ct);
            }
        }

        Contact? contact = null;
        if (req.ContactId is Guid contactId)
            contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId, ct)
                ?? throw NotFoundException.For("Cari", contactId);

        // Müşteriye özel indirim: yalnızca satış faturasında ve cari seçiliyse uygulanır.
        var discountRate = req.Type == InvoiceType.Sales && contact is not null && !req.SkipContactDiscount
            ? Math.Clamp(contact.DiscountRate, 0m, 100m) : 0m;

        // Elle indirim (kasiyer): oran olarak çözülür — tutar modu da orana çevrilir ki yetki sınırı
        // tek bir kuralla her iki modu birden kapsasın. Müşteri indiriminden SONRAKİ tutara uygulanır.
        var preGross = req.Lines.Sum(l =>
        {
            var up = discountRate > 0 ? Math.Round(l.UnitPrice * (1 - discountRate / 100m), 2) : l.UnitPrice;
            var lt = Math.Round(l.Quantity * up, 2);
            return lt + Math.Round(lt * l.VatRate / 100m, 2);
        });
        var manualRate = await ResolveManualDiscountRateAsync(req, preGross, ct);

        var date = req.Date ?? DateTime.UtcNow;
        var number = await GenerateNumberAsync(req.Type, date, ct);
        var branchId = await _branch.ResolveWriteBranchAsync(ct);

        var invoice = new Invoice
        {
            Type = req.Type,
            Number = number,
            ContactId = contact?.Id,
            Date = date,
            DueDate = req.DueDate,
            BranchId = branchId,
            Channel = req.Channel,
            SellerUserId = sellerUserId ?? _currentUser.UserId, // satan personel — randevu tahsilatında atanan personel; yoksa mevcut kullanıcı
            Status = InvoiceStatus.Issued,
            ClientSaleId = clientSaleId,
            PurchaseOrderId = req.PurchaseOrderId,
            Note = discountRate > 0
                ? $"{req.Note?.Trim()} (%{discountRate:0.##} müşteri indirimi)".Trim()
                : req.Note?.Trim(),
        };
        if (manualRate > 0m)
        {
            invoice.DiscountReason = ManualDiscountLabel(req.ManualDiscountReason);
            invoice.Note = $"{invoice.Note} (%{manualRate:0.##} elle indirim · {invoice.DiscountReason})".Trim();
        }

        decimal subtotal = 0, vatTotal = 0;

        foreach (var line in req.Lines)
        {
            var product = products.First(p => p.Id == line.ProductId);
            // Müşteri indirimi ve elle indirim ÇARPIMSAL birleşir — toplamsal olsaydı (%60 + %60)
            // negatif fiyat üretirdi. Elle indirim satır fiyatına işlenir → KDV matrahı da doğru düşer.
            var effFactor = (1 - discountRate / 100m) * (1 - manualRate / 100m);
            var unitPrice = effFactor < 1m
                ? Math.Round(line.UnitPrice * effFactor, 2)
                : line.UnitPrice;
            var lineTotal = Math.Round(line.Quantity * unitPrice, 2);
            var vatAmount = Math.Round(lineTotal * line.VatRate / 100m, 2);
            subtotal += lineTotal;
            vatTotal += vatAmount;

            // Reçete/BOM: bileşik ürünün maliyeti bileşenlerinin toplam maliyetidir (kâr raporu doğru olsun).
            var hasRecipe = recipesByProduct.TryGetValue(product.Id, out var recipe) && recipe!.Count > 0;
            var lineUnitCost = hasRecipe
                ? recipe!.Sum(rc => (componentProducts.TryGetValue(rc.ComponentProductId, out var cp) ? cp.PurchasePrice : 0m) * rc.Quantity)
                : product.PurchasePrice;

            invoice.Lines.Add(new InvoiceLine
            {
                Invoice = invoice,
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = line.Quantity,
                UnitPrice = unitPrice,
                VatRate = line.VatRate,
                LineTotal = lineTotal,
                VatAmount = vatAmount,
                // Kesin kâr için satış anındaki alış maliyetini sabitle (alış faturasında PurchasePrice aşağıda
                // güncellenmeden ÖNCE okunur → satışta o anki maliyet, alışta eski maliyet — rapor yalnız satışta kullanır).
                UnitCost = lineUnitCost,
            });

            if (hasRecipe)
            {
                // BİLEŞİK ÜRÜN (kafe/restoran): kendi stoğu değil, BİLEŞENLERİ düşer. Her bileşen için ayrı stok
                // hareketi (RefId=fatura) → void/iade bunları otomatik ters çevirir. Yalnız satışta çalışır.
                foreach (var rc in recipe!)
                {
                    if (!componentProducts.TryGetValue(rc.ComponentProductId, out var comp) || comp.IsService) continue;
                    var need = rc.Quantity * line.Quantity;
                    if (need <= 0m) continue;
                    var compRow = await BranchStockLedger.GetRowAsync(_db, comp.Id, branchId, ct);
                    if (!req.AllowOversell && need > compRow.Quantity)
                        throw new BusinessRuleException(
                            $"'{product.Name}' için '{comp.Name}' bileşeni yetersiz (mevcut: {compRow.Quantity}, gereken: {need}).");
                    BranchStockLedger.ApplyDelta(compRow, comp, -need);
                    _db.StockMovements.Add(new StockMovement
                    {
                        ProductId = comp.Id,
                        Type = StockMovementType.Out,
                        Quantity = need,
                        UnitCost = comp.PurchasePrice,
                        Reference = StockMovementReference.Sale,
                        RefId = invoice.Id,
                        Note = $"{number} · {product.Name} reçetesi",
                        StockAfter = compRow.Quantity,
                        BranchId = branchId,
                        CreatedBy = _currentUser.UserId,
                    });
                }
            }
            // Stok hareketi — hizmet kalemleri ve bileşik ürünler kendi stoklarını düşmez.
            else if (!product.IsService)
            {
                // Çok-şube TAM stok: mutasyon FATURANIN şubesinin bakiyesine uygulanır (oversell o şubeye bakar).
                var stockRow = await BranchStockLedger.GetRowAsync(_db, product.Id, branchId, ct);
                if (req.Type == InvoiceType.Sales)
                {
                    // AllowOversell (pazaryeri): sipariş zaten gerçekleşti → engelleme yok, stok negatife düşer (restok sinyali).
                    if (!req.AllowOversell && line.Quantity > stockRow.Quantity)
                        throw new BusinessRuleException(
                            $"'{product.Name}' için bu şubede yeterli stok yok (mevcut: {stockRow.Quantity}, istenen: {line.Quantity}).");
                    BranchStockLedger.ApplyDelta(stockRow, product, -line.Quantity);
                }
                else
                {
                    BranchStockLedger.ApplyDelta(stockRow, product, line.Quantity);
                    product.PurchasePrice = line.UnitPrice; // son alış fiyatı
                }

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = product.Id,
                    Type = req.Type == InvoiceType.Sales ? StockMovementType.Out : StockMovementType.In,
                    Quantity = line.Quantity,
                    UnitCost = unitPrice,
                    Reference = req.Type == InvoiceType.Sales ? StockMovementReference.Sale : StockMovementReference.Purchase,
                    RefId = invoice.Id,
                    Note = number,
                    StockAfter = stockRow.Quantity, // şube bakiyesi (per-şube anlamı)
                    BranchId = branchId,
                    CreatedBy = _currentUser.UserId,
                });
            }
        }

        invoice.Subtotal = Math.Round(subtotal, 2);
        invoice.VatTotal = Math.Round(vatTotal, 2);
        invoice.GrandTotal = invoice.Subtotal + invoice.VatTotal;

        if (manualRate > 0m)
        {
            // Tutar modu ("hesabı 500₺'ye indir"): satır satır yuvarlama toplamı birkaç kuruş kaydırabilir.
            // Kalan farkı EN BÜYÜK satıra yaz (negatife düşme riski en düşük satır) ve KDV'sini tazele.
            if (req.ManualDiscountAmount is decimal target && target > 0m)
            {
                var wanted = Math.Round(preGross - target, 2);
                var drift = Math.Round(wanted - invoice.GrandTotal, 2);
                if (drift != 0m)
                {
                    var big = invoice.Lines.OrderByDescending(l => l.LineTotal).First();
                    var newTotal = Math.Round(big.LineTotal + drift / (1 + big.VatRate / 100m), 2);
                    if (newTotal > 0m)
                    {
                        subtotal += newTotal - big.LineTotal;
                        big.LineTotal = newTotal;
                        big.UnitPrice = big.Quantity > 0m ? Math.Round(newTotal / big.Quantity, 4) : big.UnitPrice;
                        vatTotal += Math.Round(newTotal * big.VatRate / 100m, 2) - big.VatAmount;
                        big.VatAmount = Math.Round(newTotal * big.VatRate / 100m, 2);
                        invoice.Subtotal = Math.Round(subtotal, 2);
                        invoice.VatTotal = Math.Round(vatTotal, 2);
                        invoice.GrandTotal = invoice.Subtotal + invoice.VatTotal;
                    }
                }
            }

            // BİLGİ amaçlı: satır fiyatlarına zaten işlendi → GrandTotal'dan tekrar düşme, RAPORDA CİRODAN ÇIKARMA.
            invoice.ManualDiscount = Math.Round(preGross - invoice.GrandTotal, 2);
            _audit.Record("InvoiceManualDiscount", "Invoice", invoice.Id,
                $"{number} · {invoice.ManualDiscount:0.00} ₺ (%{manualRate:0.##}) · {invoice.DiscountReason}");
        }

        // Sadakat/puan: yalnız cariye kesilen satışta ve sistem açıksa. Önce KULLAN (indirim → GrandTotal düşer),
        // sonra KAZAN (ödenen tutar üzerinden). 1 puan = ₺1.
        // AllowOversell=pazaryeri (Trendyol) siparişi → sözde-cari (gerçek son-müşteri değil) puan biriktirmesin.
        if (req.Type == InvoiceType.Sales && contact is not null && !req.AllowOversell)
        {
            var settings = await _db.Settings.FirstOrDefaultAsync(ct);
            // Sadakat/puan yalnız yetkili planda (Kurumsal+Zincir) uygulanır — plan düşse bile para yolunda sızmaz.
            if (settings is { LoyaltyEnabled: true } && (await _entitlements.GetAsync(ct)).LoyaltyProgram)
            {
                var cap = Math.Max(0m, Math.Min(contact.PointsBalance, invoice.GrandTotal));
                var redeem = Math.Round(Math.Clamp(req.RedeemPoints ?? 0m, 0m, cap), 2);
                if (redeem > 0m)
                {
                    invoice.Discount = redeem;
                    invoice.GrandTotal -= redeem;
                    contact.PointsBalance -= redeem;
                }
                // KAZAN satış tutarı (indirimli GrandTotal) üzerinden — ödeme peşin/veresiye ayrımı YAPILMAZ
                // (tasarım kararı): veresiye de gerçek bir satış/borçtur; ödenmezse fatura void edilir ve
                // VoidAsync kazanılan puanı geri alır. (Bkz. loyalty-points-system hafızası.)
                if (settings.LoyaltyEarnPercent > 0m)
                {
                    var earn = Math.Round(invoice.GrandTotal * settings.LoyaltyEarnPercent / 100m, 2);
                    invoice.PointsEarned = earn;
                    contact.PointsBalance += earn;
                }
            }
        }

        _db.Invoices.Add(invoice);

        // Cari hareketi (fatura tutarı)
        if (contact is not null)
        {
            contact.Balance += req.Type == InvoiceType.Sales ? invoice.GrandTotal : -invoice.GrandTotal;
            _db.AccountTransactions.Add(new AccountTransaction
            {
                ContactId = contact.Id,
                Direction = req.Type == InvoiceType.Sales ? TransactionDirection.Debit : TransactionDirection.Credit,
                Amount = invoice.GrandTotal,
                BalanceAfter = contact.Balance,
                Description = $"{number} faturası",
                DocRef = number,
                RefId = invoice.Id,
                Date = date,
                DueDate = req.DueDate,
            });
        }

        // Ödeme(ler): tek ödeme (geri uyumlu req.Payment) VEYA çoklu/karma ödeme (split tender: parça nakit,
        // parça kart/havale). Her ikisi de aynı normalize listeden işlenir; toplam fatura tutarını aşamaz.
        var reqPayments = (req.Payments is { Count: > 0 })
            ? req.Payments.Where(p => p.Amount > 0).ToList()
            : (req.Payment is { Amount: > 0 } single ? [single] : new List<InvoicePaymentRequest>());

        if (reqPayments.Count > 0)
        {
            // Fazla ödeme koruması: toplam, fatura tutarını aşamaz (kasayı/cariyi eksiye sürükler).
            // Para üstü kasada verilir, kayda geçmez → istemci, her yöntemin faturaya işlenen payını gönderir.
            var totalRequested = reqPayments.Sum(p => p.Amount);
            if (totalRequested > invoice.GrandTotal + 0.01m)
                throw new BusinessRuleException(
                    $"Toplam ödeme fatura tutarını ({invoice.GrandTotal:0.00} ₺) aşamaz.");

            var isIncome = req.Type == InvoiceType.Sales;
            decimal cumulative = 0m;
            foreach (var pay in reqPayments)
            {
                // Tolerans (≤0,01 ₺) dahilindeki fazlalık kayda GEÇMEZ (cari eksiye kaymasın / yanlış "Ödendi").
                var applied = Math.Min(pay.Amount, invoice.GrandTotal - cumulative);
                if (applied <= 0m) break;
                cumulative += applied;

                var cash = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == pay.CashAccountId, ct)
                    ?? throw NotFoundException.For("Kasa", pay.CashAccountId);

                // Gerçek kart ağ geçidi yapılandırılmışsa çekimi burada yap (manuel sağlayıcıda atlanır).
                if (_payment.IsGateway && pay.Method == PaymentMethod.Card)
                {
                    var charge = await _payment.ChargeAsync(new PaymentChargeRequest(applied, "TRY", pay.Method, number), ct);
                    if (!charge.Success)
                        throw new BusinessRuleException($"Kart tahsilatı başarısız: {charge.Error ?? "bilinmeyen hata"}");
                }

                cash.Balance += isIncome ? applied : -applied;

                _db.FinanceTransactions.Add(new FinanceTransaction
                {
                    CashAccountId = cash.Id,
                    Type = isIncome ? FinanceType.Income : FinanceType.Expense,
                    Category = isIncome ? "Satış tahsilatı" : "Alış ödemesi",
                    Amount = applied,
                    Description = $"{number} fatura ödemesi",
                    PaymentMethod = pay.Method,
                    ContactId = contact?.Id,
                    RefId = invoice.Id,
                    BranchId = branchId, // faturanın yazma şubesi (kasa şubesinden bağımsız — tutarlı raporlama)
                    Date = date,
                });

                _db.Payments.Add(new Payment
                {
                    InvoiceId = invoice.Id,
                    ContactId = contact?.Id,
                    CashAccountId = cash.Id,
                    Amount = applied,
                    Direction = isIncome ? PaymentDirection.In : PaymentDirection.Out,
                    Method = pay.Method,
                    Date = date,
                });

                // Ödeme cari bakiyeyi de düşürür
                if (contact is not null)
                {
                    contact.Balance += isIncome ? -applied : applied;
                    _db.AccountTransactions.Add(new AccountTransaction
                    {
                        ContactId = contact.Id,
                        Direction = isIncome ? TransactionDirection.Credit : TransactionDirection.Debit,
                        Amount = applied,
                        BalanceAfter = contact.Balance,
                        Description = isIncome ? "Tahsilat" : "Ödeme",
                        DocRef = number,
                        RefId = invoice.Id,
                        Date = date,
                    });
                }
            }

            invoice.PaidAmount = cumulative;
        }

        // Cari risk/kredi limiti (#40): veresiye satışta yeni bakiye limiti aşarsa engelle (batık alacağı önler).
        // Pazaryeri (AllowOversell) hariç — o sipariş zaten gerçekleşti.
        if (req.Type == InvoiceType.Sales && !req.AllowOversell
            && contact?.CreditLimit is decimal creditLimit && creditLimit > 0m && contact.Balance > creditLimit)
            throw new BusinessRuleException(
                $"'{contact.Name}' risk limiti aşılıyor (limit: {creditLimit:0.##} ₺, yeni bakiye: {contact.Balance:0.##} ₺). Tahsilat yapın ya da limiti artırın.");

        invoice.Status = invoice.PaidAmount >= invoice.GrandTotal ? InvoiceStatus.Paid : InvoiceStatus.Issued;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (clientSaleId is not null
            && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Yarış: aynı client-sale-id neredeyse eşzamanlı iki kez gönderildi; DB unique kısıtı çifte
            // satışı engelledi. Kazanan faturayı bulup idempotent şekilde döndür (satış tek kez kaydedildi).
            // IgnoreQueryFilters: kazanan başka bir şubede kesilmiş olabilir (yukarıdaki gerekçe).
            var winner = await _db.Invoices.AsNoTracking().IgnoreQueryFilters()
                .Include(i => i.Lines).Include(i => i.Contact)
                .FirstOrDefaultAsync(i => i.ClientSaleId == clientSaleId, ct);
            if (winner is not null) return ToDto(winner);
            throw new ConflictException("Bu satış zaten kaydedilmiş.");
        }

        return await GetByIdAsync(invoice.Id, ct);
    }

    public async Task<InvoiceDto> VoidAsync(Guid id, CancellationToken ct = default)
    {
        var invoice = await _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw NotFoundException.For("Fatura", id);
        if (invoice.Status == InvoiceStatus.Cancelled)
            throw new BusinessRuleException("Fatura zaten iptal edilmiş.");
        // Kısmen iade edilmiş fatura tam iptal edilemez: Void tüm RefId hareketlerini terslerdi ve iade
        // ters kayıtlarını da kapsayarak çifte düzeltme yapardı. Kalan miktar da iade edilerek kapatılmalı.
        if (invoice.Lines.Any(l => l.RefundedQuantity > 0m))
            throw new BusinessRuleException("Bu fatura kısmen iade edilmiş; tam iptal yapılamaz. Kalan miktarı da iade edin.");

        var now = DateTime.UtcNow;

        // 1) Stok hareketlerini ters çevir (In↔Out) ve ürün stoğunu düzelt.
        var movements = await _db.StockMovements.Where(m => m.RefId == id).ToListAsync(ct);
        if (movements.Count > 0)
        {
            var pids = movements.Select(m => m.ProductId).Distinct().ToList();
            var products = await _db.Products.Where(p => pids.Contains(p.Id)).ToListAsync(ct);
            foreach (var m in movements)
            {
                var product = products.FirstOrDefault(p => p.Id == m.ProductId);
                if (product is null) continue;
                var reverseType = m.Type == StockMovementType.In ? StockMovementType.Out : StockMovementType.In;
                var delta = reverseType == StockMovementType.In ? m.Quantity : -m.Quantity;
                // Ters kayıt HAREKETİN şubesine döner (yoksa faturanın şubesi) → doğru şubenin stoğu düzelir.
                var revBranch = m.BranchId ?? invoice.BranchId ?? await _branch.ResolveWriteBranchAsync(ct);
                var branchAfter = await BranchStockLedger.AdjustAsync(_db, product, revBranch, delta, ct);
                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = product.Id,
                    Type = reverseType,
                    Quantity = m.Quantity,
                    UnitCost = m.UnitCost,
                    Reference = StockMovementReference.Adjustment,
                    RefId = invoice.Id,
                    Note = $"İptal: {invoice.Number}",
                    StockAfter = branchAfter,
                    BranchId = revBranch,
                    CreatedBy = _currentUser.UserId,
                });
            }
        }

        // 2) Kasa (finans) hareketlerini ters çevir (Income↔Expense) — ödeme tahsilatı + bahşiş dahil.
        var fins = await _db.FinanceTransactions.Where(f => f.RefId == id).ToListAsync(ct);
        if (fins.Count > 0)
        {
            var accIds = fins.Select(f => f.CashAccountId).Distinct().ToList();
            var accounts = await _db.CashAccounts.Where(a => accIds.Contains(a.Id)).ToListAsync(ct);
            foreach (var f in fins)
            {
                var acc = accounts.FirstOrDefault(a => a.Id == f.CashAccountId);
                if (acc is null) continue;
                var reverseType = f.Type == FinanceType.Income ? FinanceType.Expense : FinanceType.Income;
                acc.Balance += reverseType == FinanceType.Income ? f.Amount : -f.Amount;
                _db.FinanceTransactions.Add(new FinanceTransaction
                {
                    CashAccountId = acc.Id,
                    Type = reverseType,
                    Category = "Fatura iptali",
                    Amount = f.Amount,
                    Description = $"İptal: {invoice.Number}",
                    PaymentMethod = f.PaymentMethod,
                    ContactId = f.ContactId,
                    RefId = invoice.Id,
                    BranchId = f.BranchId,
                    Date = now,
                });
            }
        }

        // 3) Cari hareketlerini ters çevir (Debit↔Credit) ve bakiyeyi düzelt.
        var accTxns = await _db.AccountTransactions.Where(a => a.RefId == id).ToListAsync(ct);
        if (accTxns.Count > 0)
        {
            var contactIds = accTxns.Select(a => a.ContactId).Distinct().ToList();
            var contacts = await _db.Contacts.Where(c => contactIds.Contains(c.Id)).ToListAsync(ct);
            foreach (var a in accTxns)
            {
                var contact = contacts.FirstOrDefault(c => c.Id == a.ContactId);
                if (contact is null) continue;
                // Debit bakiyeyi +Amount, Credit -Amount değiştirmişti; ters kayıt bunu geri alır.
                contact.Balance += a.Direction == TransactionDirection.Debit ? -a.Amount : a.Amount;
                _db.AccountTransactions.Add(new AccountTransaction
                {
                    ContactId = contact.Id,
                    Direction = a.Direction == TransactionDirection.Debit ? TransactionDirection.Credit : TransactionDirection.Debit,
                    Amount = a.Amount,
                    BalanceAfter = contact.Balance,
                    Description = $"İptal: {invoice.Number}",
                    DocRef = invoice.Number,
                    RefId = invoice.Id,
                    Date = now,
                });
            }
        }

        // 3.5) Sadakat puanını geri al: bu satışta kazanılan puanı sil, kullanılan puanı cariye iade et.
        if (invoice.ContactId is Guid loyaltyCid && (invoice.PointsEarned != 0m || invoice.Discount != 0m))
        {
            var lc = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == loyaltyCid, ct);
            if (lc is not null)
            {
                lc.PointsBalance -= invoice.PointsEarned; // kazanılan geri alınır
                lc.PointsBalance += invoice.Discount;      // kullanılan iade edilir
                // Clamp YOK: kazanılan puan zaten harcanmışsa bakiye negatife düşer — bu gerçek "fazla-kullanım"
                // borcudur (mevcut meşru puanı yok etmez), gelecekteki kazançla telafi olur; negatif bakiye
                // redeem'de 0'a floor'lanır ve UI'da gizlenir (availablePoints/pointsBalance > 0 kapıları).
            }
        }

        // 4) Ödemeleri ters kayıtla dengele (In↔Out) — sub-ledger bütünlüğü.
        var payments = await _db.Payments.Where(p => p.InvoiceId == id).ToListAsync(ct);
        foreach (var p in payments)
        {
            _db.Payments.Add(new Payment
            {
                InvoiceId = invoice.Id,
                ContactId = p.ContactId,
                CashAccountId = p.CashAccountId,
                Amount = p.Amount,
                Direction = p.Direction == PaymentDirection.In ? PaymentDirection.Out : PaymentDirection.In,
                Method = p.Method,
                Date = now,
                Note = $"İptal: {invoice.Number}",
            });
        }

        // 5) Faturayı iptalli işaretle.
        invoice.Status = InvoiceStatus.Cancelled;
        invoice.PaidAmount = 0;

        // 6) Bu fatura bir adisyondan doğduysa adisyonu da iptalli yap (satış geri alındı).
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.InvoiceId == id, ct);
        if (order is not null && order.Status == OrderStatus.Closed)
        {
            order.Status = OrderStatus.Cancelled;
            order.ClosedAt = now;
        }

        // Mal kabul faturasıysa siparişin "gelen miktar"ı da geri düşer — yoksa sipariş yalancı-kapalı kalır
        // ve aynı mal bir daha kabul edilemez.
        if (invoice.PurchaseOrderId is Guid poId)
        {
            var po = await _db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == poId, ct);
            if (po is not null)
            {
                foreach (var il in invoice.Lines)
                {
                    var pol = po.Lines.FirstOrDefault(x => x.ProductId == il.ProductId);
                    if (pol is not null) pol.ReceivedQuantity = Math.Max(0m, pol.ReceivedQuantity - il.Quantity);
                }
                po.Status = po.Lines.All(l => l.ReceivedQuantity >= l.OrderedQuantity)
                    ? PurchaseOrderStatus.Received
                    : po.Lines.Any(l => l.ReceivedQuantity > 0m)
                        ? PurchaseOrderStatus.PartiallyReceived
                        : PurchaseOrderStatus.Sent;
            }
        }

        // Sorumluluk izi: para geri alan eylem — kim iptal etti? (aynı SaveChanges ile atomik)
        _audit.Record("InvoiceVoided", "Invoice", invoice.Id, $"{invoice.Number} · {invoice.GrandTotal:0.00} ₺");

        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(invoice.Id, ct);
    }

    public async Task<InvoiceDto> RefundAsync(Guid id, RefundInvoiceRequest req, CancellationToken ct = default)
    {
        if (req.Lines is null || req.Lines.Count == 0)
            throw new BusinessRuleException("İade için en az bir satır seçin.");

        var invoice = await _db.Invoices.Include(i => i.Lines).Include(i => i.Contact)
            .FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw NotFoundException.For("Fatura", id);
        if (invoice.Type != InvoiceType.Sales)
            throw new BusinessRuleException("Yalnızca satış faturaları iade edilebilir.");
        if (invoice.Status == InvoiceStatus.Cancelled)
            throw new BusinessRuleException("İptal edilmiş fatura iade edilemez.");

        var now = DateTime.UtcNow;

        // İade satırlarını doğrula (kalanı aşamaz) + iade tutarını (net + KDV) hesapla.
        var refunds = new List<(InvoiceLine Line, decimal Qty)>();
        decimal refundNet = 0m, refundVat = 0m;
        foreach (var r in req.Lines)
        {
            if (r.Quantity <= 0m) continue;
            var line = invoice.Lines.FirstOrDefault(l => l.Id == r.InvoiceLineId)
                ?? throw new BusinessRuleException("İade satırı bu faturada bulunamadı.");
            var remaining = line.Quantity - line.RefundedQuantity;
            if (r.Quantity > remaining + 0.0001m)
                throw new BusinessRuleException($"'{line.ProductName}' için iade miktarı kalan ({remaining:0.##}) adetten fazla olamaz.");
            var qty = Math.Min(r.Quantity, remaining);
            var lineNet = Math.Round(qty * line.UnitPrice, 2);
            refundNet += lineNet;
            refundVat += Math.Round(lineNet * line.VatRate / 100m, 2);
            refunds.Add((line, qty));
        }
        if (refunds.Count == 0) throw new BusinessRuleException("Geçerli iade satırı yok.");

        var refundGross = Math.Round(refundNet + refundVat, 2);
        if (refundGross <= 0m) throw new BusinessRuleException("İade tutarı sıfır olamaz.");

        // Sadakat puanı indirimi satır fiyatlarına YANSIMAZ (fatura seviyesinde durur) → iade edilecek PARA,
        // mal değerinin indirim payı kadar azıdır; o pay müşteriye PUAN olarak geri verilir. Aksi halde
        // müşteri ödediğinden fazlasını nakit alır (100₺ mal, 10₺ puanla ödenmiş → 90₺ ödendi, 100₺ geri).
        // Elle indirim burada YOKTUR: o zaten UnitPrice'a işlendiği için refundNet kendiliğinden nettir.
        var goodsValue = invoice.Subtotal + invoice.VatTotal;
        var pointsShare = goodsValue > 0m && invoice.Discount > 0m
            ? Math.Round(invoice.Discount * refundGross / goodsValue, 2)
            : 0m;
        var refundMoney = Math.Round(refundGross - pointsShare, 2);
        if (invoice.ContactId is null && req.CashAccountId is null)
            throw new BusinessRuleException("Peşin (carisiz) satış iadesinde geri ödeme kasası seçin.");

        // 1) Stok geri gir + satır iade miktarını işaretle (hizmet kalemleri stok tutmaz).
        var pids = refunds.Select(x => x.Line.ProductId).Distinct().ToList();
        var products = await _db.Products.Where(p => pids.Contains(p.Id)).ToListAsync(ct);
        // Reçete/BOM: bileşik ürün iadesinde KENDİ stoğu değil BİLEŞENLERİ geri girer (satıştaki düşümün tersi).
        var refundRecipes = (await _db.RecipeComponents.Where(r => pids.Contains(r.ProductId)).ToListAsync(ct))
            .GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var refundCompIds = refundRecipes.Values.SelectMany(l => l.Select(r => r.ComponentProductId)).Distinct().ToList();
        var refundComps = refundCompIds.Count > 0
            ? await _db.Products.Where(p => refundCompIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct)
            : new Dictionary<Guid, Product>();
        var refBranch0 = invoice.BranchId ?? await _branch.ResolveWriteBranchAsync(ct);
        foreach (var (line, qty) in refunds)
        {
            var product = products.FirstOrDefault(p => p.Id == line.ProductId);
            if (product is not null && refundRecipes.TryGetValue(product.Id, out var rcpe))
            {
                // Bileşik ürün: her bileşeni (miktar × iade adedi) kadar geri koy.
                foreach (var rc in rcpe)
                {
                    if (!refundComps.TryGetValue(rc.ComponentProductId, out var comp) || comp.IsService) continue;
                    var back = rc.Quantity * qty;
                    if (back <= 0m) continue;
                    var after = await BranchStockLedger.AdjustAsync(_db, comp, refBranch0, back, ct);
                    _db.StockMovements.Add(new StockMovement
                    {
                        ProductId = comp.Id,
                        Type = StockMovementType.In,
                        Quantity = back,
                        UnitCost = comp.PurchasePrice,
                        Reference = StockMovementReference.Adjustment,
                        RefId = invoice.Id,
                        Note = $"İade: {invoice.Number} · {product.Name} reçetesi",
                        StockAfter = after,
                        BranchId = refBranch0,
                        CreatedBy = _currentUser.UserId,
                    });
                }
            }
            else if (product is not null && !product.IsService)
            {
                // İade stoğu, satışın yapıldığı faturanın şubesine geri girer.
                var branchAfter = await BranchStockLedger.AdjustAsync(_db, product, refBranch0, qty, ct);
                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = product.Id,
                    Type = StockMovementType.In,
                    Quantity = qty,
                    UnitCost = line.UnitCost ?? product.PurchasePrice,
                    Reference = StockMovementReference.Adjustment,
                    RefId = invoice.Id,
                    Note = $"İade: {invoice.Number}",
                    StockAfter = branchAfter,
                    BranchId = refBranch0,
                    CreatedBy = _currentUser.UserId,
                });
            }
            line.RefundedQuantity += qty;
        }

        // 2) Cari alacağı ters çevir. Satış cariye İNDİRİMLİ GrandTotal'ı borç yazmıştı → iade de
        //    indirimli (refundMoney) tutarı geri almalı; simetri bozulursa bakiye kayar.
        if (invoice.Contact is Contact contact)
        {
            contact.Balance -= refundMoney;
            _db.AccountTransactions.Add(new AccountTransaction
            {
                ContactId = contact.Id,
                Direction = TransactionDirection.Credit,
                Amount = refundMoney,
                BalanceAfter = contact.Balance,
                Description = $"İade: {invoice.Number}",
                DocRef = invoice.Number,
                RefId = invoice.Id,
                Date = now,
            });
        }

        // 3) Nakit geri ödeme (kasa seçiliyse): kasadan çıkış + Payment(Out) + (cari varsa) alacağı tüket.
        //    Kasa yoksa → cariye alacak yazılır (credit note; adım 2 yeterli).
        if (req.CashAccountId is Guid caid)
        {
            var cash = await _db.CashAccounts.FirstOrDefaultAsync(a => a.Id == caid, ct)
                ?? throw NotFoundException.For("Kasa", caid);
            cash.Balance -= refundMoney;
            _db.FinanceTransactions.Add(new FinanceTransaction
            {
                CashAccountId = cash.Id,
                Type = FinanceType.Expense,
                Category = "İade",
                Amount = refundMoney,
                Description = $"İade: {invoice.Number}",
                PaymentMethod = PaymentMethod.Cash,
                ContactId = invoice.ContactId,
                RefId = invoice.Id,
                BranchId = invoice.BranchId,
                Date = now,
            });
            _db.Payments.Add(new Payment
            {
                InvoiceId = invoice.Id,
                ContactId = invoice.ContactId,
                CashAccountId = cash.Id,
                Amount = refundMoney,
                Direction = PaymentDirection.Out,
                Method = PaymentMethod.Cash,
                Date = now,
                Note = $"İade: {invoice.Number}",
            });
            if (invoice.Contact is Contact paid)
            {
                paid.Balance += refundMoney; // nakit geri ödendi → alacak kapandı (adım 2 ile net 0)
                _db.AccountTransactions.Add(new AccountTransaction
                {
                    ContactId = paid.Id,
                    Direction = TransactionDirection.Debit,
                    Amount = refundMoney,
                    BalanceAfter = paid.Balance,
                    Description = $"İade ödemesi: {invoice.Number}",
                    DocRef = invoice.Number,
                    RefId = invoice.Id,
                    Date = now,
                });
            }
            invoice.PaidAmount = Math.Max(0m, invoice.PaidAmount - refundMoney);
        }

        // 4) Sadakat: (a) bu satışta KAZANILAN puanı iade oranınca geri al,
        //    (b) bu satışta KULLANILAN puanın iade payını müşteriye geri ver (nakit yerine puan iadesi).
        //    Clamp yok (void ile tutarlı): fazla-kullanım negatife düşer, redeem'de 0'a floor'lanır.
        if (invoice.Contact is Contact loyalty)
        {
            if (invoice.PointsEarned > 0m && goodsValue > 0m)
                loyalty.PointsBalance -= Math.Round(invoice.PointsEarned * refundGross / goodsValue, 2);
            if (pointsShare > 0m)
                loyalty.PointsBalance += pointsShare;
        }

        // Sorumluluk izi: para geri veren eylem — kim, hangi faturadan ne kadar iade etti?
        _audit.Record("InvoiceRefunded", "Invoice", invoice.Id, pointsShare > 0m
            ? $"{invoice.Number} · {refundMoney:0.00} ₺ + {pointsShare:0.00} puan iade"
            : $"{invoice.Number} · {refundMoney:0.00} ₺ iade");

        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(invoice.Id, ct);
    }

    /// <summary>Elle indirim isteğini doğrulayıp UYGULANACAK ORANA çevirir (tutar modu da orana çevrilir,
    /// böylece yetki sınırı tek kuralla her iki modu kapsar). İndirim istenmemişse 0 döner.</summary>
    private async Task<decimal> ResolveManualDiscountRateAsync(CreateInvoiceRequest req, decimal preGross, CancellationToken ct)
    {
        var pct = req.ManualDiscountPercent;
        var amt = req.ManualDiscountAmount;
        if (pct is null or <= 0m && amt is null or <= 0m) return 0m;
        if (pct is > 0m && amt is > 0m)
            throw new BusinessRuleException("Elle indirimde yüzde ve tutar birlikte verilemez.");

        if (req.Type != InvoiceType.Sales)
            throw new BusinessRuleException("Elle indirim yalnızca satış faturasında uygulanabilir.");
        // Pazaryeri: fiyat pazaryerinden gelir; indirim uygulanırsa mutabakat bozulur → sessizce yok sayma, reddet.
        if (req.AllowOversell)
            throw new BusinessRuleException("Pazaryeri siparişinde elle indirim uygulanamaz.");
        if (preGross <= 0m)
            throw new BusinessRuleException("Tutarsız satışta indirim uygulanamaz.");

        var settings = await _db.Settings.FirstOrDefaultAsync(ct);
        if (settings is not { ManualDiscountEnabled: true })
            throw new BusinessRuleException("Elle indirim kapalı. Ayarlar'dan açabilirsiniz.");

        var rate = pct is decimal p ? p : amt!.Value / preGross * 100m;
        if (rate > 100m)
            throw new BusinessRuleException("İndirim, satış tutarını aşamaz.");

        // Kasiyer sınırı: sessizce kırpma — ekranda %30 yazıp %10 uygulanması kasiyeri yanıltır.
        var isManager = _currentUser.Role is UserRole.Owner or UserRole.Admin;
        if (!isManager)
        {
            var max = Math.Clamp(settings.MaxManualDiscountPercent, 0m, 100m);
            if (rate > max + 0.0001m)
                throw new BusinessRuleException(max <= 0m
                    ? "İndirim yetkiniz yok. Yöneticinizden isteyin."
                    : $"En fazla %{max:0.##} indirim yetkiniz var.");
        }
        return Math.Round(rate, 4);
    }

    private static string ManualDiscountLabel(DiscountReason? r) => r switch
    {
        DiscountReason.Complimentary => "İkram",
        DiscountReason.Damaged => "Hasarlı ürün",
        DiscountReason.Rounding => "Yuvarlama",
        DiscountReason.Negotiation => "Pazarlık",
        DiscountReason.Other => "Diğer",
        _ => "Belirtilmemiş",
    };

    private async Task<string> GenerateNumberAsync(InvoiceType type, DateTime date, CancellationToken ct)
    {
        var prefix = type == InvoiceType.Sales ? "SLS" : "PRC";
        // Sayaç ilk kez oluşturulurken mevcut fatura sayısından devam etsin (eski tenant'lar için).
        var seed = await _db.Invoices.CountAsync(i => i.Type == type, ct) + 1;
        var next = await _db.NextDocumentNumberAsync($"invoice:{type}", seed, ct);
        return $"{prefix}-{date:yyyy}-{next:D5}";
    }

    private static InvoiceDto ToDto(Invoice i) => new(
        i.Id, i.Type, i.Number, i.ContactId, i.Contact?.Name, i.Date,
        i.Subtotal, i.VatTotal, i.GrandTotal, i.PaidAmount, i.Discount, i.PointsEarned,
        i.ManualDiscount, i.DiscountReason, i.Status, i.Note,
        i.Lines.OrderBy(l => l.CreatedAt).Select(l => new InvoiceLineDto(
            l.Id, l.ProductId, l.ProductName, l.Quantity, l.UnitPrice, l.VatRate, l.LineTotal, l.VatAmount, l.RefundedQuantity)).ToList(),
        i.DueDate);
}
