using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Reports;

/// <summary>Satış ve finansal raporlar. Tarih aralığı [from, to) yarı-açıktır (to hariç).</summary>
public sealed class ReportService : IReportService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public ReportService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    public async Task<SalesReportDto> GetSalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;

        // Satış faturaları — özet + günlük trend
        var invoices = await _db.Invoices
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled && i.Date >= from && i.Date < to)
            .Where(i => br == null || i.BranchId == br)
            .Select(i => new { i.Date, i.Subtotal, i.VatTotal, i.GrandTotal, i.Discount })
            .ToListAsync(ct);

        var salesCount = invoices.Count;
        var subtotal = invoices.Sum(i => i.Subtotal);
        var vat = invoices.Sum(i => i.VatTotal);
        var total = invoices.Sum(i => i.GrandTotal);
        var avg = salesCount > 0 ? Math.Round(total / salesCount, 2) : 0m;

        // Satır bazlı: kategori, en çok satan, tahmini kâr
        var lines = await _db.InvoiceLines
            .Where(l => l.Invoice.Type == InvoiceType.Sales && l.Invoice.Status != InvoiceStatus.Cancelled && l.Invoice.Date >= from && l.Invoice.Date < to)
            .Where(l => br == null || l.Invoice.BranchId == br)
            .Select(l => new
            {
                l.ProductId,
                l.ProductName,
                l.Quantity,
                l.RefundedQuantity,
                l.LineTotal,
                l.UnitCost,
                Date = l.Invoice.Date, // günlük NET trend için (KDV hariç seri)
                Category = l.Product != null && l.Product.Category != null ? l.Product.Category.Name : null,
                PurchasePrice = l.Product != null ? l.Product.PurchasePrice : 0m,
                IsService = l.Product != null && l.Product.IsService,
            })
            .ToListAsync(ct);

        // Kısmi iade: iade edilen miktar ciro/COGS'ten düşülür → net miktar (Quantity − RefundedQuantity) üzerinden.
        var eff = lines.Select(l =>
        {
            var netQty = l.Quantity - l.RefundedQuantity;
            var frac = l.Quantity > 0 ? netQty / l.Quantity : 0m;
            return new
            {
                l.ProductId,
                l.ProductName,
                Quantity = netQty,
                Revenue = Math.Round(l.LineTotal * frac, 2),
                Cost = LineCost(l.IsService, l.UnitCost, l.PurchasePrice, netQty),
                l.Category,
                l.Date,
            };
        }).ToList();

        // Tahmini kâr: hizmet maliyeti 0; maliyet satış anında sabitlenen UnitCost, yoksa güncel alış fiyatı (eski satır).
        // Sadakat puanı indirimi (Invoice.Discount) satır LineTotal'larına yansımaz → dönem toplamını ciroyu düşürmek için çıkar (GetProfitAsync ile tutarlı).
        var pointsDiscount = invoices.Sum(i => i.Discount);
        var estProfit = Math.Round(eff.Sum(l => l.Revenue - l.Cost) - pointsDiscount, 2);

        var byCategory = eff
            .GroupBy(l => l.Category ?? "Kategorisiz")
            .Select(g => new CategoryBreakdownDto(g.Key, g.Sum(x => x.Quantity), Math.Round(g.Sum(x => x.Revenue), 2)))
            .OrderByDescending(x => x.Total)
            .ToList();

        var topProducts = eff
            .GroupBy(l => new { l.ProductId, l.ProductName })
            .Select(g => new TopProductReportDto(g.Key.ProductId, g.Key.ProductName, g.Sum(x => x.Quantity), Math.Round(g.Sum(x => x.Revenue), 2)))
            .OrderByDescending(x => x.Total)
            .Take(10)
            .ToList();

        // Ödeme yöntemi kırılımı — gerçekleşen tahsilatlar (veresiye hariç).
        // İptal (void) edilen faturanın tahsilatı sayılmaz: void orijinal "In" ödemeyi silmez, ters "Out"
        // kaydı ekler; yalnız "In" saydığımız için iptalli faturanın In'ini de dışlarız (yoksa ciro
        // Cancelled'ı hariç tutarken ödeme kırılımı iptalli tahsilatı gösterir → Z raporu tutmaz).
        var payments = await _db.Payments
            .Where(p => p.Direction == PaymentDirection.In && p.Date >= from && p.Date < to)
            .Where(p => !_db.Invoices.Any(i => i.Id == p.InvoiceId && i.Status == InvoiceStatus.Cancelled))
            .Where(p => br == null || _db.Invoices.Any(i => i.Id == p.InvoiceId && i.BranchId == br))
            .Select(p => new { p.Method, p.Amount })
            .ToListAsync(ct);

        var byPayment = payments
            .GroupBy(p => p.Method)
            .Select(g => new NamedAmountDto(PaymentLabel(g.Key), Math.Round(g.Sum(x => x.Amount), 2), g.Count()))
            .OrderByDescending(x => x.Amount)
            .ToList();

        // Günlük NET ciro (KDV hariç, iade düşülmüş) — brüt trendin yanına. Puan indirimi de günü bazında
        // çıkarılır ki asistan tahmini ile özet/kâr raporu aynı "ciro" tanımını konuşsun (bkz. DailySalesDto).
        var netByDay = eff
            .GroupBy(l => l.Date.Date)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(x => x.Revenue), 2));
        foreach (var g in invoices.GroupBy(i => i.Date.Date))
        {
            var pd = g.Sum(x => x.Discount);
            if (pd > 0 && netByDay.TryGetValue(g.Key, out var net)) netByDay[g.Key] = Math.Round(net - pd, 2);
        }

        var daily = invoices
            .GroupBy(i => i.Date.Date)
            .Select(g => new DailySalesDto(g.Key, Math.Round(g.Sum(x => x.GrandTotal), 2), g.Count(),
                netByDay.TryGetValue(g.Key, out var n) ? n : 0m))
            .ToList();

        var trend = FillDays(from.Date, to.Date, daily);

        return new SalesReportDto(
            salesCount, Math.Round(total, 2), Math.Round(subtotal, 2), Math.Round(vat, 2),
            avg, estProfit, byPayment, byCategory, topProducts, trend);
    }

    public async Task<ProfitReportDto> GetProfitAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;

        var lines = await _db.InvoiceLines
            .Where(l => l.Invoice.Type == InvoiceType.Sales && l.Invoice.Status != InvoiceStatus.Cancelled && l.Invoice.Date >= from && l.Invoice.Date < to)
            .Where(l => br == null || l.Invoice.BranchId == br)
            .Select(l => new
            {
                l.InvoiceId,
                l.LineTotal,
                l.Quantity,
                l.RefundedQuantity,
                l.UnitCost,
                Channel = l.Invoice.Channel,
                Category = l.Product != null && l.Product.Category != null ? l.Product.Category.Name : null,
                PurchasePrice = l.Product != null ? l.Product.PurchasePrice : 0m,
                IsService = l.Product != null && l.Product.IsService,
                Date = l.Invoice.Date,
            })
            .ToListAsync(ct);

        // Kısmi iade: net miktar (Quantity − RefundedQuantity) üzerinden ciro/COGS.
        var rows = lines.Select(l =>
        {
            var netQty = l.Quantity - l.RefundedQuantity;
            var frac = l.Quantity > 0 ? netQty / l.Quantity : 0m;
            return new
            {
                l.InvoiceId,
                Revenue = Math.Round(l.LineTotal * frac, 2), // net (KDV hariç)
                Cost = LineCost(l.IsService, l.UnitCost, l.PurchasePrice, netQty),
                Channel = string.IsNullOrWhiteSpace(l.Channel) ? "Store" : l.Channel,
                Category = l.Category ?? "Kategorisiz",
                Day = l.Date.Date,
            };
        }).ToList();

        // Pazaryeri kesintileri (komisyon + kargo): satıcının bağlantı ayarında girdiği değerler.
        // Trendyol finans API'si GEREKMEZ. Fatura başına hesaplanır → komisyon ciro-oransal, kargo sipariş başına.
        // Bu sayede toplam / kanal kırılımı / günlük trend hepsi NET kârı gösterir (aksi halde pazaryeri kârı şişer).
        var feeByChannel = (await _db.MarketplaceConnections
                .Select(c => new { c.Channel, c.CommissionRate, c.ShippingCost })
                .ToListAsync(ct))
            .GroupBy(c => c.Channel)
            .ToDictionary(
                g => g.Key,
                g => (Rate: Math.Clamp(g.First().CommissionRate, 0m, 100m), Shipping: Math.Max(0m, g.First().ShippingCost)));

        var invoiceFees = rows
            .GroupBy(r => r.InvoiceId)
            .Select(g =>
            {
                var head = g.First();
                if (!feeByChannel.TryGetValue(head.Channel, out var f)) return new { head.Channel, head.Day, Fee = 0m };
                var commission = Math.Round(g.Sum(x => x.Revenue) * f.Rate / 100m, 2);
                return new { head.Channel, head.Day, Fee = commission + f.Shipping };
            })
            .Where(x => x.Fee > 0m)
            .ToList();

        var feesTotal = Math.Round(invoiceFees.Sum(x => x.Fee), 2);
        var feesByChannel = invoiceFees.GroupBy(x => x.Channel).ToDictionary(g => g.Key, g => g.Sum(x => x.Fee));
        var feesByDay = invoiceFees.GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Sum(x => x.Fee));

        // Sadakat puanı indirimi gerçek ciroyu düşürür; satır-bazlı LineTotal bunu içermez.
        // Fatura BAŞINA çekilir (kanal + gün ile) — yalnız dönem toplamı çekilseydi başlıktaki ciro/kâr
        // düşer ama kanal ve günlük kırılımlar düşmez → kırılımlar toplamla UZLAŞMAZDI.
        var discountRows = await _db.Invoices
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled
                        && i.Date >= from && i.Date < to && i.Discount > 0m)
            .Where(i => br == null || i.BranchId == br)
            .Select(i => new { i.Channel, i.Date, i.Discount })
            .ToListAsync(ct);

        var pointsDiscount = discountRows.Sum(x => x.Discount);
        var discountByChannel = discountRows
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Channel) ? "Store" : x.Channel)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Discount));
        var discountByDay = discountRows
            .GroupBy(x => x.Date.Date)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Discount));

        var revenue = Math.Round(rows.Sum(r => r.Revenue) - pointsDiscount, 2);
        // Maliyet = COGS + pazaryeri kesintileri (komisyon + kargo) → kâr NET.
        var cost = Math.Round(rows.Sum(r => r.Cost) + feesTotal, 2);
        var profit = Math.Round(revenue - cost, 2);

        var byChannel = rows
            .GroupBy(r => r.Channel)
            .Select(g => Breakdown(
                ChannelLabel(g.Key),
                g.Sum(x => x.Revenue) - (discountByChannel.TryGetValue(g.Key, out var d) ? d : 0m),
                g.Sum(x => x.Cost) + (feesByChannel.TryGetValue(g.Key, out var f) ? f : 0m)))
            .OrderByDescending(x => x.Profit)
            .ToList();

        var byCategory = rows
            .GroupBy(r => r.Category)
            .Select(g => Breakdown(g.Key, g.Sum(x => x.Revenue), g.Sum(x => x.Cost)))
            .OrderByDescending(x => x.Profit)
            .Take(10)
            .ToList();

        var dailyProfit = rows
            .GroupBy(r => r.Day)
            .Select(g => new ProfitTrendPointDto(
                g.Key,
                Math.Round(g.Sum(x => x.Revenue - x.Cost)
                           - (discountByDay.TryGetValue(g.Key, out var d) ? d : 0m)
                           - (feesByDay.TryGetValue(g.Key, out var f) ? f : 0m), 2)))
            .ToList();
        var trend = FillProfitDays(from.Date, to.Date, dailyProfit);

        return new ProfitReportDto(revenue, cost, profit, Pct(profit, revenue), byChannel, byCategory, trend);
    }

    /// <summary>Ürün bazında brüt kâr (ciro − COGS), kâra göre azalan (ilk 20). Puan indirimi/pazaryeri
    /// kesintisi ürün seviyesine dağıtılamadığından YAKLAŞIK sıralamadır — "hangi ürün daha kârlı" içindir.</summary>
    public async Task<List<ProductProfitDto>> GetProductProfitAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;
        var lines = await _db.InvoiceLines
            .Where(l => l.Invoice.Type == InvoiceType.Sales && l.Invoice.Status != InvoiceStatus.Cancelled && l.Invoice.Date >= from && l.Invoice.Date < to)
            .Where(l => br == null || l.Invoice.BranchId == br)
            .Select(l => new
            {
                l.ProductId,
                l.ProductName,
                l.LineTotal,
                l.Quantity,
                l.RefundedQuantity,
                l.UnitCost,
                PurchasePrice = l.Product != null ? l.Product.PurchasePrice : 0m,
                IsService = l.Product != null && l.Product.IsService,
            })
            .ToListAsync(ct);

        // Kısmi iade: net miktar üzerinden ciro/COGS (GetSalesAsync ile aynı mantık).
        var eff = lines.Select(l =>
        {
            var netQty = l.Quantity - l.RefundedQuantity;
            var frac = l.Quantity > 0 ? netQty / l.Quantity : 0m;
            return new
            {
                l.ProductId,
                l.ProductName,
                Quantity = netQty,
                Revenue = Math.Round(l.LineTotal * frac, 2),
                Cost = LineCost(l.IsService, l.UnitCost, l.PurchasePrice, netQty),
            };
        });

        return eff
            .GroupBy(x => new { x.ProductId, x.ProductName })
            .Select(g =>
            {
                var rev = Math.Round(g.Sum(x => x.Revenue), 2);
                var cost = Math.Round(g.Sum(x => x.Cost), 2);
                var profit = Math.Round(rev - cost, 2);
                return new ProductProfitDto(g.Key.ProductId, g.Key.ProductName, g.Sum(x => x.Quantity), rev, cost, profit, Pct(profit, rev));
            })
            .OrderByDescending(x => x.Profit)
            .Take(20)
            .ToList();
    }

    /// <summary>Satır maliyeti: hizmet=0; maliyet satış anında sabitlenen UnitCost (null değilse, 0 dahil),
    /// yoksa (eski/legacy satır → null) güncel alış fiyatına düşülür.</summary>
    private static decimal LineCost(bool isService, decimal? unitCost, decimal purchasePrice, decimal qty)
        => isService ? 0m : (unitCost ?? purchasePrice) * qty;

    private static ProfitBreakdownDto Breakdown(string name, decimal revenue, decimal cost)
    {
        var rev = Math.Round(revenue, 2);
        var cst = Math.Round(cost, 2);
        var pr = Math.Round(rev - cst, 2);
        return new ProfitBreakdownDto(name, rev, cst, pr, Pct(pr, rev));
    }

    private static decimal Pct(decimal profit, decimal revenue)
        => revenue > 0 ? Math.Round(profit / revenue * 100m, 1) : 0m;

    private static string ChannelLabel(string channel) => channel switch
    {
        "Store" => "Mağaza",
        "QR" => "QR Menü",
        _ => channel, // Trendyol vb. olduğu gibi
    };

    private static List<ProfitTrendPointDto> FillProfitDays(DateTime fromDate, DateTime toDate, List<ProfitTrendPointDto> present)
    {
        var map = present.ToDictionary(p => p.Date.Date, p => p.Profit);
        var list = new List<ProfitTrendPointDto>();
        for (var d = fromDate.Date; d < toDate.Date; d = d.AddDays(1))
            list.Add(new ProfitTrendPointDto(d, map.TryGetValue(d, out var v) ? v : 0m));
        return list;
    }

    public async Task<FinancialReportDto> GetFinancialAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;
        var txns = await _db.FinanceTransactions
            .Where(t => t.Date >= from && t.Date < to)
            .Where(t => br == null || t.BranchId == br)
            .Select(t => new { t.Type, t.Category, t.Amount })
            .ToListAsync(ct);

        var income = txns.Where(t => t.Type == FinanceType.Income).Sum(t => t.Amount);
        var expense = txns.Where(t => t.Type == FinanceType.Expense).Sum(t => t.Amount);

        var incomeByCat = txns.Where(t => t.Type == FinanceType.Income)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Diğer" : t.Category!)
            .Select(g => new NamedAmountDto(g.Key, Math.Round(g.Sum(x => x.Amount), 2), g.Count()))
            .OrderByDescending(x => x.Amount).ToList();

        var expenseByCat = txns.Where(t => t.Type == FinanceType.Expense)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Diğer" : t.Category!)
            .Select(g => new NamedAmountDto(g.Key, Math.Round(g.Sum(x => x.Amount), 2), g.Count()))
            .OrderByDescending(x => x.Amount).ToList();

        return new FinancialReportDto(
            Math.Round(income, 2), Math.Round(expense, 2), Math.Round(income - expense, 2),
            incomeByCat, expenseByCat);
    }

    public async Task<DailyCloseDto> GetDailyCloseAsync(DateTime date, CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;
        var from = date.Date;
        var to = from.AddDays(1);

        var invoices = await _db.Invoices
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled && i.Date >= from && i.Date < to)
            .Where(i => br == null || i.BranchId == br)
            .Select(i => new { i.Subtotal, i.VatTotal, i.GrandTotal })
            .ToListAsync(ct);

        // Ödeme yöntemi kırılımı — gün içi gerçekleşen tahsilatlar (iptal edilen faturanın tahsilatı hariç)
        var payments = await _db.Payments
            .Where(p => p.Direction == PaymentDirection.In && p.Date >= from && p.Date < to)
            .Where(p => !_db.Invoices.Any(i => i.Id == p.InvoiceId && i.Status == InvoiceStatus.Cancelled))
            .Where(p => br == null || _db.Invoices.Any(i => i.Id == p.InvoiceId && i.BranchId == br))
            .Select(p => new { p.Method, p.Amount })
            .ToListAsync(ct);
        var byPayment = payments
            .GroupBy(p => p.Method)
            .Select(g => new NamedAmountDto(PaymentLabel(g.Key), Math.Round(g.Sum(x => x.Amount), 2), g.Count()))
            .OrderByDescending(x => x.Amount)
            .ToList();

        var txns = await _db.FinanceTransactions
            .Where(t => t.Date >= from && t.Date < to)
            .Where(t => br == null || t.BranchId == br)
            .Select(t => new { t.Type, t.Amount, t.CashAccountId })
            .ToListAsync(ct);
        var income = txns.Where(t => t.Type == FinanceType.Income).Sum(t => t.Amount);
        var expense = txns.Where(t => t.Type == FinanceType.Expense).Sum(t => t.Amount);

        // Kasa bazında gün içi hareket + güncel bakiye (fiziki sayımla karşılaştırma için)
        var accounts = await _db.CashAccounts.Where(a => a.IsActive)
            .Where(a => br == null || a.BranchId == br)
            .Select(a => new { a.Id, a.Name, a.Balance })
            .ToListAsync(ct);
        var cashRows = accounts.Select(a =>
        {
            var mine = txns.Where(t => t.CashAccountId == a.Id).ToList();
            return new CashAccountCloseDto(
                a.Name,
                Math.Round(mine.Where(t => t.Type == FinanceType.Income).Sum(t => t.Amount), 2),
                Math.Round(mine.Where(t => t.Type == FinanceType.Expense).Sum(t => t.Amount), 2),
                a.Balance);
        }).ToList();

        // Gün sonunda hâlâ açık (kapatılmamış) adisyonlar
        var openOrders = await _db.Orders
            .Where(o => o.Status == OrderStatus.Open)
            .Where(o => br == null || o.BranchId == br)
            .Select(o => o.GrandTotal)
            .ToListAsync(ct);

        return new DailyCloseDto(
            from,
            invoices.Count,
            Math.Round(invoices.Sum(i => i.GrandTotal), 2),
            Math.Round(invoices.Sum(i => i.Subtotal), 2),
            Math.Round(invoices.Sum(i => i.VatTotal), 2),
            byPayment,
            Math.Round(income, 2), Math.Round(expense, 2), Math.Round(income - expense, 2),
            cashRows,
            openOrders.Count,
            Math.Round(openOrders.Sum(), 2));
    }

    public async Task<WasteReportDto> GetWasteAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;
        var rows = await _db.StockMovements
            .Where(m => m.Reference == StockMovementReference.Waste && m.CreatedAt >= from && m.CreatedAt < to)
            .Where(m => br == null || m.BranchId == br)
            .Select(m => new { m.ProductId, Name = m.Product.Name, m.Quantity, m.UnitCost, m.WasteReason })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return new WasteReportDto(0m, 0m, [], []);

        var items = rows.GroupBy(r => new { r.ProductId, r.Name })
            .Select(g => new WasteItemDto(g.Key.ProductId, g.Key.Name,
                Math.Round(g.Sum(x => x.Quantity), 2),
                Math.Round(g.Sum(x => x.Quantity * x.UnitCost), 2)))
            .OrderByDescending(i => i.Cost).ToList();

        var byReason = rows.GroupBy(r => r.WasteReason)
            .Select(g => new NamedAmountDto(ReasonLabel(g.Key), Math.Round(g.Sum(x => x.Quantity * x.UnitCost), 2), g.Count()))
            .OrderByDescending(x => x.Amount).ToList();

        return new WasteReportDto(
            Math.Round(rows.Sum(r => r.Quantity * r.UnitCost), 2),
            Math.Round(rows.Sum(r => r.Quantity), 2),
            items, byReason);
    }

    public async Task<List<HourlySalesCellDto>> GetHourlySalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;
        var rows = await _db.Invoices
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled && i.Date >= from && i.Date < to)
            .Where(i => br == null || i.BranchId == br)
            .Select(i => new { i.Date, i.GrandTotal })
            .ToListAsync(ct);

        // Gün/saat işletme yerel saatine göre (TR) — yoğun saat tespiti doğru olsun. Hafta günü 0=Pzt..6=Paz.
        return rows
            .Select(r =>
            {
                var local = AppTime.ToLocal(r.Date);
                return new { Weekday = ((int)local.DayOfWeek + 6) % 7, local.Hour, r.GrandTotal };
            })
            .GroupBy(x => new { x.Weekday, x.Hour })
            .Select(g => new HourlySalesCellDto(g.Key.Weekday, g.Key.Hour, g.Count(), Math.Round(g.Sum(x => x.GrandTotal), 2)))
            .OrderBy(c => c.Weekday).ThenBy(c => c.Hour)
            .ToList();
    }

    public async Task<List<StaffSalesDto>> GetStaffSalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;
        var rows = await _db.Invoices
            .Where(i => i.Type == InvoiceType.Sales && i.Status != InvoiceStatus.Cancelled && i.Date >= from && i.Date < to)
            .Where(i => br == null || i.BranchId == br)
            .GroupBy(i => i.SellerUserId)
            .Select(g => new { g.Key, Count = g.Count(), Revenue = g.Sum(x => x.GrandTotal) })
            .ToListAsync(ct);

        return rows
            .Select(r => new StaffSalesDto(r.Key, r.Count, Math.Round(r.Revenue, 2),
                r.Count > 0 ? Math.Round(r.Revenue / r.Count, 2) : 0m))
            .OrderByDescending(x => x.Revenue).ToList();
    }

    private static string ReasonLabel(WasteReason? r) => r switch
    {
        WasteReason.Spoiled => "Bozuldu",
        WasteReason.Broken => "Kırıldı/hasar",
        WasteReason.Expired => "Son kullanma geçti",
        WasteReason.Complimentary => "İkram",
        WasteReason.Other => "Diğer",
        _ => "Belirtilmemiş",
    };

    public async Task<AgingReportDto> GetAgingAsync(CancellationToken ct = default)
    {
        // Cari bakiyeleri tenant geneli (şube yok) → yaşlandırma tenant genelinde.
        var asOf = DateTime.UtcNow;
        var contacts = await _db.Contacts
            .Where(c => c.IsActive && c.Balance != 0m)
            .Select(c => new { c.Id, c.Name, c.Type, c.Phone, c.Balance })
            .ToListAsync(ct);
        if (contacts.Count == 0)
            return new AgingReportDto(asOf, 0m, 0m, 0m, 0m, 0m, 0m, [], []);

        var ids = contacts.Select(c => c.Id).ToList();
        var txns = await _db.AccountTransactions
            .Where(t => ids.Contains(t.ContactId))
            .Select(t => new { t.ContactId, t.Direction, t.Amount, t.Date, t.DueDate })
            .ToListAsync(ct);
        // Yaşlandırma referans tarihi = vade (varsa), yoksa hareket tarihi. Vadeli satış gerçek vadesinden yaşlanır.
        var byContact = txns.GroupBy(t => t.ContactId)
            .ToDictionary(g => g.Key,
                g => g.Select(t => new { t.Direction, t.Amount, RefDate = t.DueDate ?? t.Date })
                      .OrderByDescending(t => t.RefDate).ToList());

        var receivables = new List<ContactAgingDto>();
        var payables = new List<ContactAgingDto>();
        foreach (var c in contacts)
        {
            var receivable = c.Balance > 0m;
            // Alacak (bize borçlu) → borcu artıran hareket Debit; borç (biz borçlu) → Credit.
            var chargeDir = receivable ? TransactionDirection.Debit : TransactionDirection.Credit;
            var remaining = Math.Abs(c.Balance);
            decimal cur = 0m, d30 = 0m, d60 = 0m, over = 0m;
            int? oldestDays = null;

            // FIFO: ödemeler en eski borcu kapatır → kalan bakiye en YENİ borç hareketlerinden oluşur.
            // Hareketleri yeniden eskiye tara, bakiyeyi karşılayana kadar kovala.
            foreach (var t in byContact.GetValueOrDefault(c.Id) ?? [])
            {
                if (remaining <= 0m) break;
                if (t.Direction != chargeDir) continue;
                var apply = Math.Min(remaining, t.Amount);
                var age = (int)(asOf.Date - t.RefDate.Date).TotalDays;
                if (age <= 30) cur += apply;
                else if (age <= 60) d30 += apply;
                else if (age <= 90) d60 += apply;
                else over += apply;
                oldestDays = age; // en son dokunulan = en eski katkı → "en eski borç yaşı"
                remaining -= apply;
            }
            if (remaining > 0m) cur += remaining; // dated hareketle atfedilemeyen kalan → güncel say (savunmacı)

            var row = new ContactAgingDto(c.Id, c.Name, c.Type.ToString(), c.Phone,
                c.Balance, cur, d30, d60, over, oldestDays);
            if (receivable) receivables.Add(row); else payables.Add(row);
        }

        // En riskli üstte: en eskimişi (Over90) sonra bakiyesi büyük olan.
        receivables = receivables.OrderByDescending(r => r.Over90).ThenByDescending(r => r.Balance).ToList();
        payables = payables.OrderByDescending(r => r.Over90).ThenByDescending(r => Math.Abs(r.Balance)).ToList();

        return new AgingReportDto(
            asOf,
            Math.Round(receivables.Sum(r => r.Balance), 2),
            Math.Round(payables.Sum(r => Math.Abs(r.Balance)), 2),
            Math.Round(receivables.Sum(r => r.Current), 2),
            Math.Round(receivables.Sum(r => r.D31_60), 2),
            Math.Round(receivables.Sum(r => r.D61_90), 2),
            Math.Round(receivables.Sum(r => r.Over90), 2),
            receivables, payables);
    }

    private static List<DailySalesDto> FillDays(DateTime from, DateTime toExclusive, List<DailySalesDto> data)
    {
        var map = data.ToDictionary(d => d.Date.Date);
        var result = new List<DailySalesDto>();
        // Çok uzun aralıklarda günlük dizi şişmesin (>366 gün) — trend yine de kaba kalır.
        if ((toExclusive - from).TotalDays > 366) return data.OrderBy(d => d.Date).ToList();
        for (var d = from.Date; d < toExclusive; d = d.AddDays(1))
            result.Add(map.TryGetValue(d, out var v) ? v : new DailySalesDto(d, 0, 0, 0m));
        return result;
    }

    private static string PaymentLabel(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "Nakit",
        PaymentMethod.Card => "Kart",
        PaymentMethod.Transfer => "Havale",
        PaymentMethod.Credit => "Veresiye",
        _ => m.ToString(),
    };
}
