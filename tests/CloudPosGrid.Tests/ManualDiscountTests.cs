using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Kasiyerin elle indirimi: satır fiyatına işlenir (KDV matrahı da düşer), yetki sınırına tabidir,
/// iade/void'de doğru terslenir ve raporda BİR KEZ sayılır. Ayrıca puanla ödenen kısmın iadesi (nakit değil puan).</summary>
[Collection("api")]
public class ManualDiscountTests
{
    private readonly ApiFixture _fx;
    public ManualDiscountTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"md{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

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

    private async Task<HttpClient> RegisterAsync(string businessType = "Retail")
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new
        {
            companyName = "İndirim İşletme", fullName = "Sahip", email, password = "test1234", businessType, code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        await _fx.ActivateEnterpriseAsync(Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!));
        return c;
    }

    /// <summary>Elle indirimi açar; maxPercent kasiyer sınırıdır (Owner/Admin sınırsız).</summary>
    private static Task EnableDiscount(HttpClient c, decimal maxPercent, bool loyalty = false, decimal earn = 0m)
        => Put(c, "/api/settings", new
        {
            companyName = "İndirim İşletme", currency = "TRY", defaultVatRate = 20m,
            loyaltyEnabled = loyalty, loyaltyEarnPercent = earn,
            manualDiscountEnabled = true, maxManualDiscountPercent = maxPercent,
        });

    private static async Task<string> Product(HttpClient c, string name, decimal sale, decimal purchase, decimal stock, decimal vat = 10m)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = purchase, salePrice = sale, vatRate = vat, openingStock = stock, minStock = 0m, isService = false,
        }));

    private static async Task<string> CashAccount(HttpClient c)
    {
        var a = await Get(c, "/api/cash-accounts");
        return a.GetArrayLength() > 0 ? Id(a[0]) : Id(await Post(c, "/api/cash-accounts", new { name = "Kasa", type = "Cash", openingBalance = 0m }));
    }

    /// <summary>Kasa bakiyesi — tekil GET yok, listeden okunur.</summary>
    private static async Task<decimal> CashBalance(HttpClient c, string id)
        => (await Get(c, "/api/cash-accounts")).EnumerateArray().First(a => Id(a) == id).GetProperty("balance").GetDecimal();

    private static async Task<string> Contact(HttpClient c, string name, decimal discountRate = 0m)
        => Id(await Post(c, "/api/contacts", new { type = "Customer", name, openingBalance = 0m, discountRate }));

    private static object SaleBody(string pid, decimal qty, decimal unitPrice, decimal vat,
        string? contactId = null, object? payment = null, decimal? pct = null, decimal? amt = null, string? reason = null, decimal? redeem = null)
        => new
        {
            type = "Sales", contactId, date = (string?)null, note = "satış",
            lines = new[] { new { productId = pid, quantity = qty, unitPrice, vatRate = vat } },
            payment,
            redeemPoints = redeem,
            manualDiscountPercent = pct,
            manualDiscountAmount = amt,
            manualDiscountReason = reason,
        };

    // ---- Hesaplama ----

    [Fact]
    public async Task Percent_discount_reduces_total_AND_vat_proportionally()
    {
        var c = await RegisterAsync();
        await EnableDiscount(c, 100m);
        var pid = await Product(c, "Ürün", sale: 100m, purchase: 40m, stock: 50m);

        // 2 × 100 = 200 net, %10 KDV → 220. %10 elle indirim → net 180, KDV 18, toplam 198.
        var inv = await Post(c, "/api/invoices", SaleBody(pid, 2m, 100m, 10m, pct: 10m, reason: "Negotiation"));

        Assert.Equal(180m, D(inv, "subtotal"));
        Assert.Equal(18m, D(inv, "vatTotal"));   // KDV de düştü — sadakat indiriminden farkı tam olarak bu
        Assert.Equal(198m, D(inv, "grandTotal"));
        Assert.Equal(22m, D(inv, "manualDiscount"));
        Assert.Equal("Pazarlık", inv.GetProperty("discountReason").GetString());
        Assert.Equal(90m, D(inv.GetProperty("lines")[0], "unitPrice")); // satır fiyatına işlendi
    }

    [Fact]
    public async Task Amount_discount_hits_the_requested_total_exactly()
    {
        var c = await RegisterAsync();
        await EnableDiscount(c, 100m);
        var pid = await Product(c, "Ürün", sale: 33.33m, purchase: 10m, stock: 100m);

        // 3 × 33,33 = 99,99 net + %10 KDV = 109,99. 9,99 ₺ indir → tam 100,00 olmalı (kuruş düzeltmesi).
        var inv = await Post(c, "/api/invoices", SaleBody(pid, 3m, 33.33m, 10m, amt: 9.99m, reason: "Rounding"));
        Assert.Equal(100m, D(inv, "grandTotal"));
        Assert.Equal(9.99m, D(inv, "manualDiscount"));
    }

    [Fact]
    public async Task Customer_rate_and_manual_discount_stack_multiplicatively()
    {
        var c = await RegisterAsync();
        await EnableDiscount(c, 100m);
        var pid = await Product(c, "Ürün", sale: 100m, purchase: 40m, stock: 50m);
        var cid = await Contact(c, "İndirimli Müşteri", discountRate: 10m);

        // 100 → müşteri %10 → 90 → elle %10 → 81. KDV %10 → 89,10. (Toplamsal olsaydı 80 olurdu.)
        var inv = await Post(c, "/api/invoices", SaleBody(pid, 1m, 100m, 10m, contactId: cid, pct: 10m, reason: "Negotiation"));
        Assert.Equal(81m, D(inv, "subtotal"));
        Assert.Equal(89.10m, D(inv, "grandTotal"));
    }

    // ---- Yetki ----

    [Fact]
    public async Task Cashier_is_capped_but_owner_is_not()
    {
        var owner = await RegisterAsync();
        await EnableDiscount(owner, 10m); // kasiyer en fazla %10
        var pid = await Product(owner, "Ürün", sale: 100m, purchase: 40m, stock: 100m);
        await Post(owner, "/api/staff", new { fullName = "Kasiyer", email = NewEmail(), password = "pass1234", role = "Cashier", pin = "4242" });

        var pin = await Post(owner, "/api/auth/pin-login", new { pin = "4242" });
        var cashier = _fx.Factory.CreateClient();
        cashier.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pin.GetProperty("accessToken").GetString());

        // Kasiyer sınır içinde indirim yapabilir...
        var ok = await Post(cashier, "/api/invoices", SaleBody(pid, 1m, 100m, 10m, pct: 10m, reason: "Rounding"));
        Assert.Equal(99m, D(ok, "grandTotal"));

        // ...sınırı aşamaz (sessizce kırpılmaz, açıkça reddedilir).
        var over = await cashier.PostAsJsonAsync("/api/invoices", SaleBody(pid, 1m, 100m, 10m, pct: 30m, reason: "Rounding"));
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Contains("%10", await over.Content.ReadAsStringAsync());

        // Sahip aynı indirimi yapabilir.
        var byOwner = await Post(owner, "/api/invoices", SaleBody(pid, 1m, 100m, 10m, pct: 30m, reason: "Negotiation"));
        Assert.Equal(77m, D(byOwner, "grandTotal"));
    }

    [Fact]
    public async Task Discount_is_rejected_when_disabled_or_invalid()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Ürün", sale: 100m, purchase: 40m, stock: 100m);

        // Ayar kapalı (varsayılan) → reddedilir.
        var off = await c.PostAsJsonAsync("/api/invoices", SaleBody(pid, 1m, 100m, 10m, pct: 5m));
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);

        await EnableDiscount(c, 100m);

        // Yüzde + tutar birlikte verilemez.
        var both = await c.PostAsJsonAsync("/api/invoices", SaleBody(pid, 1m, 100m, 10m, pct: 5m, amt: 5m));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        // İndirim satış tutarını aşamaz.
        var over = await c.PostAsJsonAsync("/api/invoices", SaleBody(pid, 1m, 100m, 10m, amt: 500m));
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);

        // Alış faturasında elle indirim yok.
        var purchase = await c.PostAsJsonAsync("/api/invoices", new
        {
            type = "Purchase", contactId = (string?)null, date = (string?)null, note = "alış",
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 50m, vatRate = 10m } },
            payment = (object?)null, manualDiscountPercent = 10m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, purchase.StatusCode);
    }

    // ---- İade / void / rapor ----

    [Fact]
    public async Task Refund_of_discounted_line_returns_the_discounted_amount()
    {
        var c = await RegisterAsync();
        await EnableDiscount(c, 100m);
        var cash = await CashAccount(c);
        var pid = await Product(c, "Ürün", sale: 100m, purchase: 40m, stock: 50m);

        // %20 indirimli peşin satış: 100 → 80 net, KDV 8 → 88 ödendi.
        var inv = await Post(c, "/api/invoices", SaleBody(pid, 1m, 100m, 10m,
            payment: new { cashAccountId = cash, amount = 88m, method = "Cash" }, pct: 20m, reason: "Damaged"));
        Assert.Equal(88m, D(inv, "grandTotal"));

        var before = await CashBalance(c, cash);
        var lineId = Id(inv.GetProperty("lines")[0]);
        await Post(c, $"/api/invoices/{Id(inv)}/refund", new
        {
            lines = new[] { new { invoiceLineId = lineId, quantity = 1m } }, cashAccountId = cash, note = (string?)null,
        });

        // İndirimli tutar (88) geri ödenir — indirimsiz 110 DEĞİL.
        var after = await CashBalance(c, cash);
        Assert.Equal(88m, before - after);
    }

    [Fact]
    public async Task Discounted_sale_is_counted_once_in_reports()
    {
        var c = await RegisterAsync();
        await EnableDiscount(c, 100m);
        var cash = await CashAccount(c);
        var pid = await Product(c, "Ürün", sale: 100m, purchase: 40m, stock: 50m);

        // %10 indirim → 90 net + 9 KDV = 99.
        await Post(c, "/api/invoices", SaleBody(pid, 1m, 100m, 10m,
            payment: new { cashAccountId = cash, amount = 99m, method = "Cash" }, pct: 10m, reason: "Rounding"));

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sales = await Get(c, $"/api/reports/sales?from={today}&to={today}");
        Assert.Equal(99m, D(sales, "salesTotal"));       // indirim İKİ kez düşülmedi
        Assert.Equal(90m, D(sales, "salesSubtotal"));

        // Kâr: ciro 90 (net), maliyet 40 → 50.
        var profit = await Get(c, $"/api/reports/profit?from={today}&to={today}");
        Assert.Equal(90m, D(profit, "totalRevenue"));
        Assert.Equal(50m, D(profit, "grossProfit"));
    }

    [Fact]
    public async Task Voiding_discounted_sale_restores_everything()
    {
        var c = await RegisterAsync();
        await EnableDiscount(c, 100m);
        var cash = await CashAccount(c);
        var pid = await Product(c, "Ürün", sale: 100m, purchase: 40m, stock: 50m);
        var openingCash = await CashBalance(c, cash);

        var inv = await Post(c, "/api/invoices", SaleBody(pid, 2m, 100m, 10m,
            payment: new { cashAccountId = cash, amount = 176m, method = "Cash" }, pct: 20m, reason: "Complimentary"));
        Assert.Equal(176m, D(inv, "grandTotal"));
        Assert.Equal(48m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));

        await Post(c, $"/api/invoices/{Id(inv)}/void", new { });
        Assert.Equal(openingCash, await CashBalance(c, cash));
        Assert.Equal(50m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));
    }

    // ---- Adisyon yolu ----

    [Fact]
    public async Task Order_close_with_discount_is_not_rejected_as_underpayment()
    {
        var c = await RegisterAsync("Hospitality");
        await EnableDiscount(c, 100m);
        var cash = await CashAccount(c);
        var pid = await Product(c, "Çay", sale: 100m, purchase: 30m, stock: 100m, vat: 10m);
        var tid = Id(await Post(c, "/api/tables", new { name = "M1", areaId = (string?)null, sortOrder = 0 }));

        var oid = Id(await Post(c, "/api/orders", new { type = "DineIn", tableId = tid, contactId = (string?)null, label = (string?)null, note = (string?)null }));
        await Post(c, $"/api/orders/{oid}/lines", new { productId = pid, quantity = 2m, note = (string?)null });

        // 2 × 100 + %10 KDV = 220. %10 indirimle 198 tahsil edilir — "Eksik ödeme" hatası VERMEMELİ.
        var closed = await Post(c, $"/api/orders/{oid}/close", new
        {
            payment = new { cashAccountId = cash, amount = 198m, method = "Cash" },
            contactId = (string?)null, tip = 0m,
            manualDiscountPercent = 10m, manualDiscountReason = "Negotiation",
        });
        var invId = closed.GetProperty("invoiceId").GetString()!;
        Assert.Equal(198m, D(await Get(c, $"/api/invoices/{invId}"), "grandTotal"));
    }

    // ---- Puanla ödenen kısmın iadesi (mevcut hatanın regresyon kalkanı) ----

    [Fact]
    public async Task Refund_returns_redeemed_points_as_points_not_cash()
    {
        var c = await RegisterAsync();
        await EnableDiscount(c, 100m, loyalty: true, earn: 0m); // kazanım kapalı: yalnız KULLANIM izlensin
        var cash = await CashAccount(c);
        var pid = await Product(c, "Ürün", sale: 100m, purchase: 40m, stock: 50m);
        var cid = await Contact(c, "Puanlı Müşteri");

        // Puan bakiyesi kur: %10 kazanımla bir satış yap, sonra kazanımı kapat.
        await EnableDiscount(c, 100m, loyalty: true, earn: 10m);
        await Post(c, "/api/invoices", SaleBody(pid, 1m, 100m, 10m, contactId: cid,
            payment: new { cashAccountId = cash, amount = 110m, method = "Cash" }));
        Assert.Equal(11m, D(await Get(c, $"/api/contacts/{cid}"), "pointsBalance"));
        await EnableDiscount(c, 100m, loyalty: true, earn: 0m);

        // 110 ₺ satış, 11 ₺ puanla ödendi → 99 ₺ nakit.
        var inv = await Post(c, "/api/invoices", SaleBody(pid, 1m, 100m, 10m, contactId: cid,
            payment: new { cashAccountId = cash, amount = 99m, method = "Cash" }, redeem: 11m));
        Assert.Equal(99m, D(inv, "grandTotal"));
        Assert.Equal(11m, D(inv, "discount"));
        Assert.Equal(0m, D(await Get(c, $"/api/contacts/{cid}"), "pointsBalance"));

        var before = await CashBalance(c, cash);
        await Post(c, $"/api/invoices/{Id(inv)}/refund", new
        {
            lines = new[] { new { invoiceLineId = Id(inv.GetProperty("lines")[0]), quantity = 1m } },
            cashAccountId = cash, note = (string?)null,
        });

        // Nakit yalnız ÖDENEN kadar geri verilir (99), 110 değil — aksi halde işletme 11 ₺ kaybeder.
        var after = await CashBalance(c, cash);
        Assert.Equal(99m, before - after);
        // Kullanılan puan müşteriye geri döner.
        Assert.Equal(11m, D(await Get(c, $"/api/contacts/{cid}"), "pointsBalance"));
    }
}
