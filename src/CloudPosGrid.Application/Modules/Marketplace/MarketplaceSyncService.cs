using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Invoices;
using CloudPosGrid.Application.Modules.Orders;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPosGrid.Application.Modules.Marketplace;

/// <summary>
/// Çift yönlü senkron: (1) pazaryeri siparişlerini çekip satışa+stok düşümüne çevirir,
/// (2) güncel mağaza stoğunu pazaryerine gönderir. Arka plan işi ve "şimdi senkronize et" bunu çağırır.
/// </summary>
public sealed class MarketplaceSyncService : IMarketplaceSyncService
{
    private readonly IApplicationDbContext _db;
    private readonly IMarketplaceProviderFactory _providers;
    private readonly ISecretProtector _protector;
    private readonly IOrderService _orders;
    private readonly IInvoiceService _invoices;
    private readonly ILogger<MarketplaceSyncService> _logger;

    public MarketplaceSyncService(IApplicationDbContext db, IMarketplaceProviderFactory providers,
        ISecretProtector protector, IOrderService orders, IInvoiceService invoices, ILogger<MarketplaceSyncService> logger)
    {
        _db = db;
        _providers = providers;
        _protector = protector;
        _orders = orders;
        _invoices = invoices;
        _logger = logger;
    }

    public async Task<SyncResultDto> SyncTenantAsync(CancellationToken ct = default)
    {
        var conns = await _db.MarketplaceConnections.Where(c => c.IsActive).ToListAsync(ct);
        int orders = 0, stock = 0;
        var errors = new List<string>();
        foreach (var conn in conns)
        {
            try
            {
                var r = await SyncOneAsync(conn, ct);
                orders += r.OrdersImported;
                stock += r.StockPushed;
                if (!r.Success) errors.Add($"{conn.Channel}: {r.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pazaryeri senkron hatası ({Channel})", conn.Channel);
                errors.Add($"{conn.Channel}: {ex.Message}");
            }
        }
        var ok = errors.Count == 0;
        return new SyncResultDto(ok, ok ? "Senkron tamamlandı." : string.Join(" | ", errors), orders, stock, DateTime.UtcNow);
    }

    public async Task<SyncResultDto> SyncConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        var conn = await _db.MarketplaceConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw NotFoundException.For("Bağlantı", connectionId);
        return await SyncOneAsync(conn, ct);
    }

    /// <summary>Overlap: sipariş penceresini biraz geriye alır ki durum değiştiren/gecikmiş siparişler kaçmasın (dedupe güvenli).</summary>
    private const int OrderFetchOverlapMinutes = 30;

    /// <summary>Her turda RawJson'dan yeniden denenecek takılı sipariş üst sınırı (aşırı yüklenme koruması).</summary>
    private const int ReconcileBatchLimit = 200;

    private async Task<SyncResultDto> SyncOneAsync(MarketplaceConnection conn, CancellationToken ct)
    {
        var provider = _providers.Get(conn.Channel);
        if (provider is null) return await FailAsync(conn, $"Desteklenmeyen kanal: {conn.Channel}", ct);

        var creds = new MarketplaceCredentials(conn.SupplierId,
            _protector.Unprotect(conn.ApiKeyEnc), _protector.Unprotect(conn.ApiSecretEnc));

        var imported = 0;

        // 0) RECONCILE: önceki turlarda takılan (Pending/NeedsMapping/Error) siparişleri sakladıkları RawJson'dan
        //    yeniden işle — watermark ilerlemiş ve pazaryeri artık döndürmüyor olsa bile satışa çevrilebilsin
        //    (ör. kullanıcı eşleştirmeyi sonradan ekledi, ya da stok düzeltildi).
        imported += await ReconcileStuckAsync(conn, provider, ct);

        // 1) SİPARİŞ ÇEKME (pazaryeri → biz). Önce stok düşer, sonra güncel stok pazaryerine gider.
        var orderCycle = DateTime.UtcNow;
        var since = conn.LastOrderSyncAt is { } last ? last.AddMinutes(-OrderFetchOverlapMinutes) : orderCycle.AddDays(-7);
        var fetched = await provider.FetchOrdersAsync(creds, since, ct);
        foreach (var mo in fetched)
        {
            if (string.IsNullOrWhiteSpace(mo.OrderNumber)) continue;
            if (await UpsertFetchedOrderAsync(conn, mo, ct)) imported++;
        }
        conn.LastOrderSyncAt = orderCycle;
        await _db.SaveChangesAsync(ct); // sipariş watermark'ını bağımsız kaydet (stok gönderim hatası bunu geri almasın)

        // 3) İLAN DURUMU: uygulamadan açılan (Submitted) ilanların Trendyol batch sonucunu güncelle (Approved/Rejected).
        await PollListingBatchesAsync(conn, provider, creds, ct);

        // 3b) BEKLEYEN STOK BATCH'LERİ: önceki turda gönderilen stok, Trendyol asenkron işler. Sonuç COMPLETED+SUCCESS
        //     olunca LastPushedStock kesinleşir; FAILED/eksik ise bekleyen kayıt temizlenir → aşağıda tekrar gönderilir.
        await PollStockBatchesAsync(conn, provider, creds, ct);

        // 2) STOK GÖNDERME (biz → pazaryeri). Watermark yerine "onaylı gönderilen ≠ güncel stok" ölçütü: batch başarısız
        //    olan kalemler kendiliğinden tekrar gönderilir (kalıcılık watermark'a bağlı değil). Bekleyen batch varsa atlanır.
        var pushed = 0;
        var stockCycle = DateTime.UtcNow;
        var listings = await _db.MarketplaceListings
            .Where(l => l.ConnectionId == conn.Id && l.IsActive && l.StockBatchRequestId == null)
            .Include(l => l.Product)
            .Where(l => l.LastPushedStock == null || l.LastPushedStock != l.Product!.CurrentStock)
            .ToListAsync(ct);
        if (listings.Count > 0)
        {
            var items = listings
                .Select(l => new MarketplaceStockItem(l.MarketplaceBarcode, (int)Math.Max(0, Math.Floor(l.Product!.CurrentStock))))
                .ToList();
            var push = await provider.PushStockAsync(creds, items, ct);
            if (!push.Success) return await FailAsync(conn, push.Error ?? "Stok gönderimi başarısız.", ct, imported);
            if (!string.IsNullOrWhiteSpace(push.BatchRequestId))
            {
                // Asenkron: kesinleştirme, batch COMPLETED+SUCCESS olunca PollStockBatchesAsync'te yapılır.
                foreach (var l in listings) { l.StockBatchRequestId = push.BatchRequestId; l.PendingPushStock = l.Product!.CurrentStock; }
            }
            else
            {
                // Senkron kabul (batch kimliği yok): hemen kesinleşir.
                foreach (var l in listings) { l.LastPushedStock = l.Product!.CurrentStock; l.LastPushedAt = stockCycle; }
            }
            pushed = push.PushedCount;
        }
        conn.LastStockSyncAt = stockCycle; // bilgi amaçlı zaman damgası (artık gönderim ölçütü değil)

        conn.LastStatus = "ok";
        conn.LastMessage = $"{imported} sipariş içe alındı, {pushed} ürün stoğu gönderildi.";
        await _db.SaveChangesAsync(ct);
        return new SyncResultDto(true, conn.LastMessage, imported, pushed, DateTime.UtcNow);
    }

    /// <summary>Önceki turlardan takılı (Pending/NeedsMapping/Error) siparişleri sakladıkları RawJson'dan yeniden işler.</summary>
    private async Task<int> ReconcileStuckAsync(MarketplaceConnection conn, IMarketplaceProvider provider, CancellationToken ct)
    {
        var stuck = await _db.MarketplaceOrders
            .Where(o => o.ConnectionId == conn.Id
                && o.SyncStatus != MarketplaceOrderSyncStatus.Imported
                && o.SyncStatus != MarketplaceOrderSyncStatus.Cancelled
                && o.RawJson != null)
            .OrderBy(o => o.OrderDate)
            .Take(ReconcileBatchLimit)
            .ToListAsync(ct);

        var reimported = 0;
        foreach (var rec in stuck)
        {
            var mo = provider.ParseOrder(rec.RawJson!);
            if (mo is null) continue; // parse edilemiyorsa dokunma (durum korunur)
            if (await ProcessOrderAsync(conn, rec, mo, ct)) reimported++;
        }
        return reimported;
    }

    /// <summary>Çekilen siparişi işler: zaten Imported ise atla; takılıysa mevcut satırı yeniden dener; yeniyse
    /// önce dedupe yuvasını Pending ile iddia eder (eşzamanlı tur çift satış üretmesin), sonra işler.</summary>
    private async Task<bool> UpsertFetchedOrderAsync(MarketplaceConnection conn, MarketplaceOrderData mo, CancellationToken ct)
    {
        var existing = await _db.MarketplaceOrders
            .FirstOrDefaultAsync(x => x.ConnectionId == conn.Id && x.MarketplaceOrderNumber == mo.OrderNumber, ct);

        var cancelled = IsCancelledStatus(mo.Status);
        if (existing is not null)
        {
            // Pazaryeri siparişi iptal/iade edildiyse yerel satışı idempotent geri al (hayalet ciro/stok/alacak önlenir).
            if (cancelled) return await CancelImportedOrderAsync(existing, mo, ct);
            if (existing.SyncStatus == MarketplaceOrderSyncStatus.Imported) return false;   // dedupe
            if (existing.SyncStatus == MarketplaceOrderSyncStatus.Cancelled) return false;  // iptal edilmiş — dokunma
            return await ProcessOrderAsync(conn, existing, mo, ct); // takılıysa yeniden dene
        }

        // Hiç görmediğimiz sipariş zaten iptal/iade gelmişse içe alma (geri alınacak satış yok).
        if (cancelled) return false;

        // Yeni sipariş → dedupe yuvasını Pending ile ATOMİK iddia et. Eşzamanlı tur aynı numarayı eklerse
        // benzersiz index ihlali yakalanır ve o tur atlanır (çift Order/Invoice üretilmez).
        var rec = new MarketplaceOrder
        {
            ConnectionId = conn.Id,
            Channel = conn.Channel,
            MarketplaceOrderNumber = mo.OrderNumber,
            BuyerName = mo.BuyerName,
            GrandTotal = mo.GrandTotal,
            MarketplaceStatus = mo.Status,
            OrderDate = mo.OrderDate,
            RawJson = mo.RawJson,
            SyncStatus = MarketplaceOrderSyncStatus.Pending,
        };
        _db.MarketplaceOrders.Add(rec);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.MarketplaceOrders.Remove(rec); // Added durumundaki kaydı geri al (izlemeden çıkar) → çift işleme yapma
            return false;
        }
        return await ProcessOrderAsync(conn, rec, mo, ct);
    }

    /// <summary>Pazaryeri siparişi terminal-iptal/iade mi (yerel satış geri alınmalı)? Trendyol: Cancelled/Returned
    /// (+ yazım varyantları). Gerekirse başka terminal durumlar (UnSupplied vb.) eklenebilir.</summary>
    private static bool IsCancelledStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var s = status.Trim();
        return s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Returned", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Return", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pazaryeri siparişi iptal/iade edildi: içe alınmış (satış kesilmiş) siparişse faturayı iptal eder
    /// (stok+kasa+cari+puan+adisyon geri alınır) ve kaydı Cancelled yapar. İdempotent: bir kez Cancelled olduktan
    /// sonra tekrar void'lemez (çift iade önlenir).</summary>
    private async Task<bool> CancelImportedOrderAsync(MarketplaceOrder rec, MarketplaceOrderData mo, CancellationToken ct)
    {
        if (rec.SyncStatus == MarketplaceOrderSyncStatus.Cancelled) return false; // zaten işlendi

        if (rec.SyncStatus == MarketplaceOrderSyncStatus.Imported && rec.InvoiceId is Guid invId)
        {
            try
            {
                await _invoices.VoidAsync(invId, ct);
            }
            catch (BusinessRuleException)
            {
                // Fatura zaten iptal edilmiş (elle void ya da yarış) — idempotent, yut.
            }
        }

        rec.SyncStatus = MarketplaceOrderSyncStatus.Cancelled;
        rec.MarketplaceStatus = mo.Status;
        rec.SyncError = null;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Pazaryeri siparişi iptal/iade → yerel satış geri alındı ({Order})", rec.MarketplaceOrderNumber);
        return false; // içe alma (imported) sayısına dahil değil
    }

    /// <summary>Sipariş kaydını işler: barkodları ürünlere eşle → satış (stok düşer). Eşleşmezse NeedsMapping, hata olursa Error.
    /// Hem yeni siparişler hem de reconcile bunu çağırır (mevcut satırı günceller).</summary>
    private async Task<bool> ProcessOrderAsync(MarketplaceConnection conn, MarketplaceOrder rec, MarketplaceOrderData mo, CancellationToken ct)
    {
        // Alanları tazele (reconcile'da eski satırı güncelliyor olabiliriz).
        rec.BuyerName = mo.BuyerName;
        rec.GrandTotal = mo.GrandTotal;
        rec.MarketplaceStatus = mo.Status;
        rec.OrderDate = mo.OrderDate;
        if (!string.IsNullOrWhiteSpace(mo.RawJson)) rec.RawJson = mo.RawJson;

        var barcodes = mo.Lines.Select(l => l.Barcode).Where(b => !string.IsNullOrWhiteSpace(b)).Distinct().ToList();

        // Barkod → ürün: önce bu bağlantının eşleştirmesi, sonra doğrudan ürün barkodu.
        var listingPairs = await _db.MarketplaceListings
            .Where(l => l.ConnectionId == conn.Id && l.IsActive && barcodes.Contains(l.MarketplaceBarcode))
            .Select(l => new { l.MarketplaceBarcode, l.ProductId }).ToListAsync(ct);
        var listingByBarcode = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in listingPairs) listingByBarcode[p.MarketplaceBarcode] = p.ProductId;

        var listingProductIds = listingByBarcode.Values.ToHashSet();
        var products = await _db.Products
            .Where(p => listingProductIds.Contains(p.Id) || (p.Barcode != null && barcodes.Contains(p.Barcode)))
            .ToListAsync(ct);
        var productById = products.ToDictionary(p => p.Id);
        var productByBarcode = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in products) if (p.Barcode is not null) productByBarcode.TryAdd(p.Barcode, p);

        var lines = new List<MarketplaceOrderLine>();
        string? missing = null;
        foreach (var l in mo.Lines)
        {
            Product? prod = null;
            if (listingByBarcode.TryGetValue(l.Barcode, out var pid)) productById.TryGetValue(pid, out prod);
            if (prod is null) productByBarcode.TryGetValue(l.Barcode, out prod);
            if (prod is null || prod.IsService) { missing = l.Barcode; continue; }
            // Pazaryeri birim fiyatı KDV DAHİL (brüt); InvoiceService fiyatı NET alıp üstüne KDV ekler →
            // çift KDV olmasın diye net'e çeviriyoruz (fatura toplamı ≈ pazaryeri brüt toplamı olur).
            var netUnit = prod.VatRate > 0 ? Math.Round(l.UnitPrice / (1 + prod.VatRate / 100m), 2) : l.UnitPrice;
            lines.Add(new MarketplaceOrderLine(prod.Id, prod.Name, l.Quantity, netUnit, prod.VatRate));
        }

        // Herhangi bir satır eşleşmezse SATIŞ YAPMA (kısmi stok düşümü olmasın) — kullanıcı eşleştirip tekrar dener (reconcile toplar).
        if (missing is not null || lines.Count == 0)
        {
            rec.SyncStatus = MarketplaceOrderSyncStatus.NeedsMapping;
            rec.SyncError = missing is not null ? $"Eşleşmeyen barkod: {missing}" : "Sipariş satırı eşleşmedi.";
            await _db.SaveChangesAsync(ct);
            return false;
        }

        try
        {
            var order = await _orders.PlaceMarketplaceAsync(
                new PlaceMarketplaceOrderRequest(conn.Channel, mo.OrderNumber, mo.BuyerName, lines), ct);
            rec.LocalOrderId = order.Id;
            rec.InvoiceId = order.InvoiceId;
            rec.SyncStatus = MarketplaceOrderSyncStatus.Imported;
            rec.SyncError = null;
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            // Pazaryeri satışı zaten gerçekleşti; stok yetmezliği artık AllowOversell ile hata değil. Kalan
            // beklenmedik hatalar Error olur ve sonraki turda reconcile ile tekrar denenir.
            _logger.LogWarning(ex, "Pazaryeri siparişi içe alınamadı ({Order})", mo.OrderNumber);
            rec.SyncStatus = MarketplaceOrderSyncStatus.Error;
            rec.SyncError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            await _db.SaveChangesAsync(ct);
            return false;
        }
    }

    /// <summary>Uygulamadan açılan (Submitted) ilanların Trendyol asenkron batch sonucunu sorgular → Approved/Rejected.
    /// Değişiklikler SyncOneAsync'in kaydıyla birlikte persist olur (tek order/stok/ilan turu).</summary>
    private async Task PollListingBatchesAsync(MarketplaceConnection conn, IMarketplaceProvider provider,
        MarketplaceCredentials creds, CancellationToken ct)
    {
        var pending = await _db.MarketplaceListings
            .Where(l => l.ConnectionId == conn.Id
                && l.ListingStatus == MarketplaceListingStatus.Submitted
                && l.BatchRequestId != null)
            .Take(100)
            .ToListAsync(ct);

        foreach (var l in pending)
        {
            try
            {
                var res = await provider.GetBatchResultAsync(creds, l.BatchRequestId!, ct);
                if (!res.Found || res.Items.Count == 0) continue; // henüz sonuç yok — sonraki tur

                var failed = res.Items.FirstOrDefault(i => i.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase));
                if (failed is not null)
                {
                    l.ListingStatus = MarketplaceListingStatus.Rejected;
                    l.ListingError = failed.Reason is { Length: > 1000 } r ? r[..1000] : failed.Reason;
                }
                else if (res.Items.Any(i => i.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)))
                {
                    l.ListingStatus = MarketplaceListingStatus.Approved;
                    l.ListingError = null;
                    l.ListedAt = DateTime.UtcNow;
                }
                else if (res.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                {
                    // Toplu istek bitti ama SUCCESS/FAILED kalemi yok (beklenmedik terminal durum) → sonsuz poll'u önle.
                    l.ListingStatus = MarketplaceListingStatus.Rejected;
                    l.ListingError = "Trendyol ilanı sonuçlandı ancak onaylanmadı.";
                }
                // aksi halde (henüz COMPLETED değil) → Submitted kalır, sonraki turda tekrar sorulur
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "İlan batch durumu sorgulanamadı ({Batch})", l.BatchRequestId);
            }
        }
    }

    /// <summary>Bekleyen asenkron stok gönderim batch'lerinin sonucunu sorgular. COMPLETED+SUCCESS → LastPushedStock
    /// (o batch'te gönderilen değere) kesinleşir; FAILED/başarısız-terminal → bekleyen kayıt temizlenir (LastPushedStock
    /// eski kalır → sonraki turda tekrar gönderilir). Hâlâ işleniyorsa dokunulmaz (sonraki tur tekrar sorulur).</summary>
    private async Task PollStockBatchesAsync(MarketplaceConnection conn, IMarketplaceProvider provider,
        MarketplaceCredentials creds, CancellationToken ct)
    {
        var pending = await _db.MarketplaceListings
            .Where(l => l.ConnectionId == conn.Id && l.StockBatchRequestId != null)
            .Take(200)
            .ToListAsync(ct);

        // Aynı batchRequestId'yi bir kez sorgula (tek push → çok ilan aynı batch'i paylaşır).
        foreach (var group in pending.GroupBy(l => l.StockBatchRequestId!))
        {
            MarketplaceBatchResult res;
            try
            {
                res = await provider.GetBatchResultAsync(creds, group.Key, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stok batch durumu sorgulanamadı ({Batch})", group.Key);
                continue;
            }
            if (!res.Found) continue; // henüz kayıt yok — sonraki tur
            var completed = res.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase);
            if (!completed && res.Items.Count == 0) continue; // hâlâ işleniyor

            foreach (var l in group)
            {
                var mine = res.Items.FirstOrDefault(i => string.Equals(i.Barcode, l.MarketplaceBarcode, StringComparison.OrdinalIgnoreCase));
                bool success, failed;
                if (mine is not null)
                {
                    success = mine.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);
                    failed = mine.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase);
                    if (!success && !failed && !completed) continue; // bu kalem hâlâ işleniyor
                }
                else
                {
                    // Kalem bazında barkod eşleşmedi: yalnız batch bittiğinde karar ver (tek tip sonuç varsay).
                    if (!completed) continue;
                    failed = res.Items.Any(i => i.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase));
                    success = !failed;
                }

                if (success)
                {
                    l.LastPushedStock = l.PendingPushStock;
                    l.LastPushedAt = DateTime.UtcNow;
                }
                else
                {
                    _logger.LogWarning("Pazaryeri stok gönderimi reddedildi ({Barcode}): {Reason}",
                        l.MarketplaceBarcode, mine?.Reason ?? "bilinmiyor");
                    // LastPushedStock eski bırakılır → güncel stoktan farklı → sonraki turda tekrar gönderilir.
                }
                l.StockBatchRequestId = null;
                l.PendingPushStock = null;
            }
        }
    }

    private async Task<SyncResultDto> FailAsync(MarketplaceConnection conn, string message, CancellationToken ct, int imported = 0)
    {
        conn.LastStatus = "error";
        conn.LastMessage = message.Length > 1000 ? message[..1000] : message;
        await _db.SaveChangesAsync(ct);
        return new SyncResultDto(false, message, imported, 0, DateTime.UtcNow);
    }
}
