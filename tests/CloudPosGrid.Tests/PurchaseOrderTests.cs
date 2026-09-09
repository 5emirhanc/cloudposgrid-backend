using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Tedarikçi siparişi + mal kabul: sipariş mali hareket üretmez; mal kabulde alış faturası
/// kesilir (stok girer, tedarikçiye borç yazılır). Kısmi teslimat ve fatura iptali doğru geri alınır.</summary>
[Collection("api")]
public class PurchaseOrderTests
{
    private readonly ApiFixture _fx;
    public PurchaseOrderTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"po{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), $"POST {url}");
    private static async Task<JsonElement> Put(HttpClient c, string url, object body) => await ReadAsync(await c.PutAsJsonAsync(url, body), $"PUT {url}");
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), $"GET {url}");
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;
    private static decimal D(JsonElement e, string p) => e.GetProperty(p).GetDecimal();

    private async Task<HttpClient> RegisterAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new
        {
            companyName = "Market", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return c;
    }

    /// <summary>Zincir paket gerektiren testler için (sipariş önerisi).</summary>
    private async Task<HttpClient> RegisterChainAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new
        {
            companyName = "Market", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        await _fx.ActivateChainAsync(Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!));
        return c;
    }

    private static async Task<string> CashAccount(HttpClient c)
    {
        var a = await Get(c, "/api/cash-accounts");
        return a.GetArrayLength() > 0 ? Id(a[0]) : Id(await Post(c, "/api/cash-accounts", new { name = "Kasa", type = "Cash", openingBalance = 0m }));
    }
    private static async Task<decimal> CashBalance(HttpClient c, string id)
        => (await Get(c, "/api/cash-accounts")).EnumerateArray().First(a => Id(a) == id).GetProperty("balance").GetDecimal();

    private static async Task<string> Supplier(HttpClient c, string name = "Tedarikçi A.Ş.")
        => Id(await Post(c, "/api/contacts", new { type = "Supplier", name, openingBalance = 0m, discountRate = 0m }));

    private static async Task<string> Product(HttpClient c, string name, decimal stock = 0m, decimal purchase = 10m, bool isService = false)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = purchase, salePrice = purchase * 2m, vatRate = 20m, openingStock = stock, minStock = 0m, isService,
        }));

    private static Task<JsonElement> Order(HttpClient c, string supplierId, string productId, decimal qty, decimal unitPrice)
        => Post(c, "/api/purchase-orders", new
        {
            contactId = supplierId, orderDate = (string?)null, expectedDate = (string?)null, note = "aylık sipariş",
            lines = new[] { new { productId, quantity = qty, unitPrice, vatRate = 20m } },
        });

    [Fact]
    public async Task Order_itself_does_not_touch_stock_or_supplier_balance()
    {
        var c = await RegisterAsync();
        var sup = await Supplier(c);
        var pid = await Product(c, "Un", stock: 5m);

        var po = await Order(c, sup, pid, 100m, 10m);
        Assert.StartsWith("SIP-", po.GetProperty("number").GetString());
        Assert.Equal("Draft", po.GetProperty("status").GetString());
        Assert.Equal(1200m, D(po, "grandTotal")); // 1000 + %20 KDV

        // Sipariş bağlayıcı değil: stok ve tedarikçi bakiyesi değişmedi.
        Assert.Equal(5m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));
        Assert.Equal(0m, D(await Get(c, $"/api/contacts/{sup}"), "balance"));
    }

    [Fact]
    public async Task Receive_creates_purchase_invoice_and_moves_stock()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var sup = await Supplier(c);
        var pid = await Product(c, "Şeker", stock: 0m);
        var cashBefore = await CashBalance(c, cash);

        var po = await Order(c, sup, pid, 50m, 20m);
        var poId = Id(po);
        await Post(c, $"/api/purchase-orders/{poId}/send", new { });

        var lineId = Id((await Get(c, $"/api/purchase-orders/{poId}")).GetProperty("lines")[0]);
        var res = await Post(c, $"/api/purchase-orders/{poId}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = lineId, quantity = 50m, unitPrice = (decimal?)null } },
            date = (string?)null, note = (string?)null,
            payment = new { cashAccountId = cash, amount = 1200m, method = "Cash" },
        });

        Assert.Equal("Received", res.GetProperty("order").GetProperty("status").GetString());
        Assert.StartsWith("PRC-", res.GetProperty("invoiceNumber").GetString());
        Assert.Equal(1200m, D(res, "invoiceGrandTotal"));

        // Stok girdi, kasa azaldı (alış ödemesi).
        Assert.Equal(50m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));
        Assert.Equal(cashBefore - 1200m, await CashBalance(c, cash));

        // Mal kabul geçmişi siparişte görünür.
        var full = await Get(c, $"/api/purchase-orders/{poId}");
        Assert.Equal(1, full.GetProperty("receipts").GetArrayLength());
        Assert.Equal(1m, D(full, "receivedRatio"));
    }

    [Fact]
    public async Task Partial_receipt_keeps_order_open_and_tracks_remaining()
    {
        var c = await RegisterAsync();
        var sup = await Supplier(c);
        var pid = await Product(c, "Yağ", stock: 0m);

        var poId = Id(await Order(c, sup, pid, 100m, 10m));
        await Post(c, $"/api/purchase-orders/{poId}/send", new { });
        var lineId = Id((await Get(c, $"/api/purchase-orders/{poId}")).GetProperty("lines")[0]);

        // İlk parti: 40 adet geldi.
        await Post(c, $"/api/purchase-orders/{poId}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = lineId, quantity = 40m, unitPrice = (decimal?)null } },
            date = (string?)null, note = (string?)null, payment = (object?)null,
        });

        var mid = await Get(c, $"/api/purchase-orders/{poId}");
        Assert.Equal("PartiallyReceived", mid.GetProperty("status").GetString());
        Assert.Equal(40m, D(mid.GetProperty("lines")[0], "receivedQuantity"));
        Assert.Equal(60m, D(mid.GetProperty("lines")[0], "remainingQuantity"));
        Assert.Equal(40m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));

        // Kalandan fazlası reddedilir.
        var over = await c.PostAsJsonAsync($"/api/purchase-orders/{poId}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = lineId, quantity = 70m, unitPrice = (decimal?)null } },
            date = (string?)null, note = (string?)null, payment = (object?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);

        // İkinci parti: kalan 60 → tamamlandı.
        await Post(c, $"/api/purchase-orders/{poId}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = lineId, quantity = 60m, unitPrice = (decimal?)null } },
            date = (string?)null, note = (string?)null, payment = (object?)null,
        });
        var done = await Get(c, $"/api/purchase-orders/{poId}");
        Assert.Equal("Received", done.GetProperty("status").GetString());
        Assert.Equal(2, done.GetProperty("receipts").GetArrayLength()); // her parti bir fatura
        Assert.Equal(100m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));
    }

    [Fact]
    public async Task Voiding_receipt_invoice_rolls_back_received_quantity()
    {
        var c = await RegisterAsync();
        var sup = await Supplier(c);
        var pid = await Product(c, "Pirinç", stock: 0m);

        var poId = Id(await Order(c, sup, pid, 30m, 15m));
        await Post(c, $"/api/purchase-orders/{poId}/send", new { });
        var lineId = Id((await Get(c, $"/api/purchase-orders/{poId}")).GetProperty("lines")[0]);

        var res = await Post(c, $"/api/purchase-orders/{poId}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = lineId, quantity = 30m, unitPrice = (decimal?)null } },
            date = (string?)null, note = (string?)null, payment = (object?)null,
        });
        Assert.Equal("Received", res.GetProperty("order").GetProperty("status").GetString());

        // Yanlış mal kabulü → fatura iptal edilir; sipariş yalancı-kapalı KALMAMALI.
        await Post(c, $"/api/invoices/{res.GetProperty("invoiceId").GetString()}/void", new { });

        var after = await Get(c, $"/api/purchase-orders/{poId}");
        Assert.Equal("Sent", after.GetProperty("status").GetString());
        Assert.Equal(0m, D(after.GetProperty("lines")[0], "receivedQuantity"));
        Assert.Equal(0m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));

        // Tekrar mal kabul edilebilir.
        var again = await Post(c, $"/api/purchase-orders/{poId}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = lineId, quantity = 30m, unitPrice = (decimal?)null } },
            date = (string?)null, note = (string?)null, payment = (object?)null,
        });
        Assert.Equal("Received", again.GetProperty("order").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Receive_with_different_price_updates_product_purchase_price()
    {
        var c = await RegisterAsync();
        var sup = await Supplier(c);
        var pid = await Product(c, "Tuz", stock: 0m, purchase: 10m);

        var poId = Id(await Order(c, sup, pid, 20m, 10m));
        await Post(c, $"/api/purchase-orders/{poId}/send", new { });
        var lineId = Id((await Get(c, $"/api/purchase-orders/{poId}")).GetProperty("lines")[0]);

        // Fatura zamlı geldi: 10 yerine 13.
        var res = await Post(c, $"/api/purchase-orders/{poId}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = lineId, quantity = 20m, unitPrice = (decimal?)13m } },
            date = (string?)null, note = (string?)null, payment = (object?)null,
        });
        Assert.Equal(312m, D(res, "invoiceGrandTotal")); // 20×13 = 260 + %20 = 312
        Assert.Equal(13m, D(await Get(c, $"/api/products/{pid}"), "purchasePrice")); // son alış fiyatı güncellendi
    }

    [Fact]
    public async Task Draft_must_be_sent_before_receiving_and_can_be_edited()
    {
        var c = await RegisterAsync();
        var sup = await Supplier(c);
        var pid = await Product(c, "Makarna", stock: 0m);

        var poId = Id(await Order(c, sup, pid, 10m, 5m));
        var lineId = Id((await Get(c, $"/api/purchase-orders/{poId}")).GetProperty("lines")[0]);

        // Taslağa mal kabul yapılamaz.
        var early = await c.PostAsJsonAsync($"/api/purchase-orders/{poId}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = lineId, quantity = 10m, unitPrice = (decimal?)null } },
            date = (string?)null, note = (string?)null, payment = (object?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);

        // Taslak düzenlenebilir.
        var edited = await Put(c, $"/api/purchase-orders/{poId}", new
        {
            contactId = sup, orderDate = (string?)null, expectedDate = (string?)null, note = "güncellendi",
            lines = new[] { new { productId = pid, quantity = 25m, unitPrice = 5m, vatRate = 20m } },
        });
        Assert.Equal(25m, D(edited.GetProperty("lines")[0], "orderedQuantity"));

        // Gönderildikten sonra düzenlenemez.
        await Post(c, $"/api/purchase-orders/{poId}/send", new { });
        var late = await c.PutAsJsonAsync($"/api/purchase-orders/{poId}", new
        {
            contactId = sup, orderDate = (string?)null, expectedDate = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 5m, unitPrice = 5m, vatRate = 20m } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
    }

    [Fact]
    public async Task Service_products_cannot_be_ordered_and_duplicate_lines_merge()
    {
        var c = await RegisterAsync();
        var sup = await Supplier(c);
        var svc = await Product(c, "Nakliye Hizmeti", isService: true);
        var pid = await Product(c, "Bulgur", stock: 0m);

        // Hizmet kalemi sipariş edilemez (stok girişi yok).
        var withService = await c.PostAsJsonAsync("/api/purchase-orders", new
        {
            contactId = sup, orderDate = (string?)null, expectedDate = (string?)null, note = (string?)null,
            lines = new[] { new { productId = svc, quantity = 1m, unitPrice = 100m, vatRate = 20m } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, withService.StatusCode);

        // Aynı ürün iki satırda gelirse TOPLANIR (void geri-alması ProductId eşleşmesine dayanıyor).
        var merged = await Post(c, "/api/purchase-orders", new
        {
            contactId = sup, orderDate = (string?)null, expectedDate = (string?)null, note = (string?)null,
            lines = new[]
            {
                new { productId = pid, quantity = 10m, unitPrice = 8m, vatRate = 20m },
                new { productId = pid, quantity = 15m, unitPrice = 8m, vatRate = 20m },
            },
        });
        Assert.Equal(1, merged.GetProperty("lines").GetArrayLength());
        Assert.Equal(25m, D(merged.GetProperty("lines")[0], "orderedQuantity"));
    }

    [Fact]
    public async Task Cancel_blocks_receiving_but_fully_received_cannot_be_cancelled()
    {
        var c = await RegisterAsync();
        var sup = await Supplier(c);
        var pid = await Product(c, "Çay", stock: 0m);

        var poId = Id(await Order(c, sup, pid, 10m, 5m));
        await Post(c, $"/api/purchase-orders/{poId}/send", new { });
        var cancelled = await Post(c, $"/api/purchase-orders/{poId}/cancel", new { });
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());

        var lineId = Id(cancelled.GetProperty("lines")[0]);
        var res = await c.PostAsJsonAsync($"/api/purchase-orders/{poId}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = lineId, quantity = 5m, unitPrice = (decimal?)null } },
            date = (string?)null, note = (string?)null, payment = (object?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // Tamamı gelmiş sipariş iptal edilemez.
        var po2 = Id(await Order(c, sup, pid, 4m, 5m));
        await Post(c, $"/api/purchase-orders/{po2}/send", new { });
        var l2 = Id((await Get(c, $"/api/purchase-orders/{po2}")).GetProperty("lines")[0]);
        await Post(c, $"/api/purchase-orders/{po2}/receive", new
        {
            lines = new[] { new { purchaseOrderLineId = l2, quantity = 4m, unitPrice = (decimal?)null } },
            date = (string?)null, note = (string?)null, payment = (object?)null,
        });
        var badCancel = await c.PostAsJsonAsync($"/api/purchase-orders/{po2}/cancel", new { });
        Assert.Equal(HttpStatusCode.BadRequest, badCancel.StatusCode);
    }

    [Fact]
    public async Task Open_orders_reduce_replenishment_suggestion()
    {
        var c = await RegisterChainAsync(); // sipariş önerisi Zincir pakete özel
        var cash = await CashAccount(c);
        var sup = await Supplier(c);
        var pid = await Product(c, "Su", stock: 100m, purchase: 2m);

        // Satış geçmişi oluştur ki öneri üretilsin.
        await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = "satış",
            lines = new[] { new { productId = pid, quantity = 90m, unitPrice = 4m, vatRate = 20m } },
            payment = new { cashAccountId = cash, amount = 432m, method = "Cash" },
        });

        var before = await Get(c, "/api/products/replenishment");
        var b = before.EnumerateArray().FirstOrDefault(x => x.GetProperty("productId").GetString() == pid);
        Assert.True(b.ValueKind != JsonValueKind.Undefined, "öneri üretilmedi");
        var suggestedBefore = D(b, "suggestedReorderQty");
        Assert.True(suggestedBefore > 0m);
        Assert.Equal(0m, D(b, "onOrderQuantity"));

        // Yolda mal olsun: sipariş ver + gönder.
        var poId = Id(await Order(c, sup, pid, suggestedBefore, 2m));
        await Post(c, $"/api/purchase-orders/{poId}/send", new { });

        var after = await Get(c, "/api/products/replenishment");
        var a = after.EnumerateArray().FirstOrDefault(x => x.GetProperty("productId").GetString() == pid);
        if (a.ValueKind != JsonValueKind.Undefined)
        {
            // Yoldaki mal öneriden düşüldü → aynı mal ikinci kez ısmarlanmaz.
            Assert.Equal(suggestedBefore, D(a, "onOrderQuantity"));
            Assert.Equal(0m, D(a, "suggestedReorderQty"));
        }
    }
}
