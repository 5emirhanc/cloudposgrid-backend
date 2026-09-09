using System.Text.Json;
using CloudPosGrid.Application.Abstractions;

namespace CloudPosGrid.Tests;

/// <summary>
/// Testler için sahte pazaryeri sağlayıcısı (kanal "TestMarket"). Gerçek Trendyol'a gitmeden
/// sipariş çekme + stok gönderme akışını test etmeyi sağlar. Koleksiyon testleri sıralı çalıştığından
/// paylaşımlı (singleton) durum güvenlidir; her test NextOrders'ı kendisi ayarlar.
/// </summary>
public sealed class FakeMarketplaceProvider : IMarketplaceProvider
{
    public string Channel => "TestMarket";

    /// <summary>FetchOrdersAsync bunları döndürür (test kurar). Tüketmez — dedupe testini mümkün kılar.</summary>
    public List<MarketplaceOrderData> NextOrders { get; set; } = new();

    /// <summary>PushStockAsync'e gelen son kalemler (test okur).</summary>
    public List<MarketplaceStockItem> LastPushed { get; } = new();

    public bool TestOk { get; set; } = true;

    // ---- İlan açma (createProducts) ----
    /// <summary>CreateListingAsync'e gelen son ilan verisi (test okur).</summary>
    public MarketplaceListingData? LastListing { get; private set; }
    /// <summary>true ise batch sonucu FAILED döner (ilan reddi testi).</summary>
    public bool NextBatchFailed { get; set; }
    /// <summary>true ise batch hâlâ PROCESSING döner (henüz sonuçlanmadı).</summary>
    public bool BatchProcessing { get; set; }

    // ---- Stok gönderim batch (asenkron) ----
    /// <summary>PushStockAsync'in döndürdüğü batch kimliği. null → senkron kabul (eski davranış).</summary>
    public string? StockBatchId { get; set; } = "stock-batch-1";
    /// <summary>true ise stok batch sonucu FAILED döner (gönderim reddi → tekrar dene testi).</summary>
    public bool NextStockBatchFailed { get; set; }

    public void Reset()
    {
        NextOrders = new();
        LastPushed.Clear();
        TestOk = true;
        LastListing = null;
        NextBatchFailed = false;
        BatchProcessing = false;
        StockBatchId = "stock-batch-1";
        NextStockBatchFailed = false;
    }

    public Task<MarketplacePushResult> PushStockAsync(MarketplaceCredentials c, IReadOnlyList<MarketplaceStockItem> items, CancellationToken ct = default)
    {
        LastPushed.Clear();
        LastPushed.AddRange(items);
        return Task.FromResult(new MarketplacePushResult(true, items.Count, null, StockBatchId));
    }

    // Her siparişin RawJson'unu kendi verisiyle damgalar ki reconcile (ParseOrder) geri çözebilsin — gerçek adapter de RawJson saklar.
    public Task<IReadOnlyList<MarketplaceOrderData>> FetchOrdersAsync(MarketplaceCredentials c, DateTime since, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MarketplaceOrderData>>(NextOrders.Select(o => o with { RawJson = Serialize(o) }).ToList());

    public MarketplaceOrderData? ParseOrder(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<RawOrder>(rawJson);
            if (r is null || string.IsNullOrWhiteSpace(r.OrderNumber)) return null;
            var lines = (r.Lines ?? new()).Select(l => new MarketplaceOrderLineData(l.Barcode, l.Quantity, l.UnitPrice)).ToList();
            return new MarketplaceOrderData(r.OrderNumber, r.BuyerName, r.OrderDate, r.Status, r.GrandTotal, lines, rawJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<MarketplacePushResult> TestConnectionAsync(MarketplaceCredentials c, CancellationToken ct = default)
        => Task.FromResult(TestOk ? new MarketplacePushResult(true, 0, null) : new MarketplacePushResult(false, 0, "test fail"));

    // ---- İlan açma referans verileri (sabit) ----
    public Task<IReadOnlyList<MarketplaceCategory>> GetCategoriesAsync(MarketplaceCredentials c, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MarketplaceCategory>>(new List<MarketplaceCategory>
        {
            new(1, "Giyim", null, false),
            new(2, "Kadın Tişört", 1, true),
        });

    public Task<IReadOnlyList<MarketplaceCategoryAttribute>> GetCategoryAttributesAsync(MarketplaceCredentials c, int categoryId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MarketplaceCategoryAttribute>>(new List<MarketplaceCategoryAttribute>
        {
            new(100, "Renk", true, false, new List<MarketplaceAttributeValue> { new(1000, "Kırmızı"), new(1001, "Mavi") }),
        });

    public Task<IReadOnlyList<MarketplaceBrand>> SearchBrandsAsync(MarketplaceCredentials c, string query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MarketplaceBrand>>(new List<MarketplaceBrand> { new(500, string.IsNullOrWhiteSpace(query) ? "TestMarka" : query) });

    public Task<IReadOnlyList<MarketplaceCargoProvider>> GetCargoProvidersAsync(MarketplaceCredentials c, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MarketplaceCargoProvider>>(new List<MarketplaceCargoProvider> { new(10, "Test Kargo") });

    public Task<MarketplaceCreateResult> CreateListingAsync(MarketplaceCredentials c, MarketplaceListingData data, CancellationToken ct = default)
    {
        LastListing = data;
        return Task.FromResult(new MarketplaceCreateResult(true, "batch-1", null));
    }

    public Task<MarketplaceBatchResult> GetBatchResultAsync(MarketplaceCredentials c, string batchRequestId, CancellationToken ct = default)
    {
        if (BatchProcessing)
            return Task.FromResult(new MarketplaceBatchResult(true, "PROCESSING", Array.Empty<MarketplaceBatchItemResult>()));

        // Stok gönderim batch'i: gönderilen her barkod için kalem sonucu (asenkron doğrulama testi).
        if (StockBatchId is not null && string.Equals(batchRequestId, StockBatchId, StringComparison.Ordinal))
        {
            var stockItems = LastPushed
                .Select(p => NextStockBatchFailed
                    ? new MarketplaceBatchItemResult(p.Barcode, "FAILED", "Barkod bulunamadı")
                    : new MarketplaceBatchItemResult(p.Barcode, "SUCCESS", null))
                .ToArray();
            return Task.FromResult(new MarketplaceBatchResult(true, "COMPLETED", stockItems));
        }

        // İlan (createProducts) batch'i.
        var barcode = LastListing?.Barcode;
        var item = NextBatchFailed
            ? new MarketplaceBatchItemResult(barcode, "FAILED", "Zorunlu öznitelik eksik")
            : new MarketplaceBatchItemResult(barcode, "SUCCESS", null);
        return Task.FromResult(new MarketplaceBatchResult(true, "COMPLETED", new[] { item }));
    }

    private static string Serialize(MarketplaceOrderData o) => JsonSerializer.Serialize(new RawOrder(
        o.OrderNumber, o.BuyerName, o.OrderDate, o.Status, o.GrandTotal,
        o.Lines.Select(l => new RawLine(l.Barcode, l.Quantity, l.UnitPrice)).ToList()));

    private sealed record RawOrder(string OrderNumber, string? BuyerName, DateTime OrderDate, string? Status, decimal GrandTotal, List<RawLine> Lines);
    private sealed record RawLine(string Barcode, decimal Quantity, decimal UnitPrice);
}
