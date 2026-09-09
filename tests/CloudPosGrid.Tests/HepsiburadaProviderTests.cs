using System.Net.Http;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Infrastructure.Marketplace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudPosGrid.Tests;

/// <summary>
/// Hepsiburada (#30) adapter'ının SEAM bağlantısını doğrular: kanal adı, factory yönlendirmesi ve
/// saklanan sipariş JSON'unun normalize modele geri çevrilmesi (ParseOrder). HTTP çağrıları (canlı API)
/// gerçek satıcı hesabı gerektirdiğinden burada test edilmez — Trendyol adapter'ında da aynı yaklaşım.
/// </summary>
public class HepsiburadaProviderTests
{
    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeProvider(string channel) : IMarketplaceProvider
    {
        public string Channel { get; } = channel;
        public Task<MarketplacePushResult> PushStockAsync(MarketplaceCredentials c, IReadOnlyList<MarketplaceStockItem> i, CancellationToken ct = default) => Task.FromResult(new MarketplacePushResult(true, 0, null));
        public Task<IReadOnlyList<MarketplaceOrderData>> FetchOrdersAsync(MarketplaceCredentials c, DateTime s, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MarketplaceOrderData>>(Array.Empty<MarketplaceOrderData>());
        public MarketplaceOrderData? ParseOrder(string rawJson) => null;
        public Task<MarketplacePushResult> TestConnectionAsync(MarketplaceCredentials c, CancellationToken ct = default) => Task.FromResult(new MarketplacePushResult(true, 0, null));
        public Task<IReadOnlyList<MarketplaceCategory>> GetCategoriesAsync(MarketplaceCredentials c, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MarketplaceCategory>>(Array.Empty<MarketplaceCategory>());
        public Task<IReadOnlyList<MarketplaceCategoryAttribute>> GetCategoryAttributesAsync(MarketplaceCredentials c, int id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MarketplaceCategoryAttribute>>(Array.Empty<MarketplaceCategoryAttribute>());
        public Task<IReadOnlyList<MarketplaceBrand>> SearchBrandsAsync(MarketplaceCredentials c, string q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MarketplaceBrand>>(Array.Empty<MarketplaceBrand>());
        public Task<IReadOnlyList<MarketplaceCargoProvider>> GetCargoProvidersAsync(MarketplaceCredentials c, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MarketplaceCargoProvider>>(Array.Empty<MarketplaceCargoProvider>());
        public Task<MarketplaceCreateResult> CreateListingAsync(MarketplaceCredentials c, MarketplaceListingData d, CancellationToken ct = default) => Task.FromResult(new MarketplaceCreateResult(true, "b", null));
        public Task<MarketplaceBatchResult> GetBatchResultAsync(MarketplaceCredentials c, string b, CancellationToken ct = default) => Task.FromResult(new MarketplaceBatchResult(true, "Completed", Array.Empty<MarketplaceBatchItemResult>()));
    }

    private static HepsiburadaProvider Build()
    {
        var config = new ConfigurationBuilder().Build();
        return new HepsiburadaProvider(new StubHttpFactory(), config, NullLogger<HepsiburadaProvider>.Instance);
    }

    [Fact]
    public void Channel_is_Hepsiburada()
    {
        Assert.Equal("Hepsiburada", Build().Channel);
    }

    [Fact]
    public void Factory_routes_channel_case_insensitively()
    {
        var factory = new MarketplaceProviderFactory(new IMarketplaceProvider[] { Build(), new FakeProvider("Trendyol") });

        Assert.Equal("Hepsiburada", factory.Get("Hepsiburada")?.Channel);
        Assert.Equal("Hepsiburada", factory.Get("hepsiburada")?.Channel); // büyük/küçük harf duyarsız
        Assert.Equal("Trendyol", factory.Get("Trendyol")?.Channel);
        Assert.Null(factory.Get("N11")); // bilinmeyen kanal → null
    }

    [Fact]
    public void ParseOrder_roundtrips_stored_order_json()
    {
        // FetchOrders'ın sakladığı normalize biçim.
        var raw = """
        {
          "orderNumber": "HB-1001",
          "buyerName": "Ayşe Yılmaz",
          "orderDate": "2026-07-20T10:30:00",
          "status": "Open",
          "grandTotal": 250.0,
          "lines": [
            { "barcode": "869000000001", "quantity": 2, "unitPrice": 100.0 },
            { "barcode": "869000000002", "quantity": 1, "unitPrice": 50.0 }
          ]
        }
        """;

        var order = Build().ParseOrder(raw);

        Assert.NotNull(order);
        Assert.Equal("HB-1001", order!.OrderNumber);
        Assert.Equal("Ayşe Yılmaz", order.BuyerName);
        Assert.Equal("Open", order.Status);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal("869000000001", order.Lines[0].Barcode);
        Assert.Equal(2m, order.Lines[0].Quantity);
        Assert.Equal(100m, order.Lines[0].UnitPrice);
    }

    [Fact]
    public void ParseOrder_returns_null_for_garbage()
    {
        Assert.Null(Build().ParseOrder("not-json"));
        Assert.Null(Build().ParseOrder("{}")); // orderNumber yok → null
    }
}
