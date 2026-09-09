using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Teklif/proforma: BAĞLAYICI DEĞİL (stok/cari/kasa değişmez), kabul edilirse faturaya
/// TAM BİR KEZ dönüşür; dönüşümde cari indirimi ikinci kez uygulanmaz.</summary>
[Collection("api")]
public class QuoteTests
{
    private readonly ApiFixture _fx;
    public QuoteTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"qte{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

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
            companyName = "Teklif İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Service", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return c;
    }

    private static async Task<string> CashAccount(HttpClient c)
    {
        var a = await Get(c, "/api/cash-accounts");
        return a.GetArrayLength() > 0 ? Id(a[0]) : Id(await Post(c, "/api/cash-accounts", new { name = "Kasa", type = "Cash", openingBalance = 0m }));
    }
    private static async Task<decimal> CashBalance(HttpClient c, string id)
        => (await Get(c, "/api/cash-accounts")).EnumerateArray().First(a => Id(a) == id).GetProperty("balance").GetDecimal();

    private static async Task<string> Product(HttpClient c, string name, decimal sale, decimal stock, decimal vat = 20m)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = 40m, salePrice = sale, vatRate = vat, openingStock = stock, minStock = 0m, isService = false,
        }));

    private static Task<JsonElement> Quote(HttpClient c, string pid, decimal qty, decimal unitPrice, decimal vat = 20m, string? contactId = null)
        => Post(c, "/api/quotes", new
        {
            contactId, customerName = contactId is null ? "Potansiyel Müşteri" : null,
            date = (string?)null, validUntil = (string?)null, note = "30 gün teslim",
            lines = new[] { new { productId = pid, quantity = qty, unitPrice, vatRate = vat } },
        });

    [Fact]
    public async Task Quote_does_not_touch_stock_cash_or_contact()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var pid = await Product(c, "Danışmanlık", sale: 1000m, stock: 10m);
        var cid = Id(await Post(c, "/api/contacts", new { type = "Customer", name = "ACME A.Ş.", openingBalance = 0m, discountRate = 0m }));
        var cashBefore = await CashBalance(c, cash);

        var q = await Quote(c, pid, 2m, 1000m, contactId: cid);
        Assert.StartsWith("TKL-", q.GetProperty("number").GetString());
        Assert.Equal("Draft", q.GetProperty("status").GetString());
        Assert.Equal(2400m, D(q, "grandTotal")); // 2000 + %20 KDV
        Assert.False(q.GetProperty("isExpired").GetBoolean());

        // Teklif bağlayıcı DEĞİL: stok, kasa ve cari bakiyesi hiç değişmedi.
        Assert.Equal(10m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));
        Assert.Equal(cashBefore, await CashBalance(c, cash));
        Assert.Equal(0m, D(await Get(c, $"/api/contacts/{cid}"), "balance"));
        // Fatura da oluşmadı.
        Assert.Equal(0, (await Get(c, "/api/invoices?pageSize=10")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Convert_creates_invoice_once_and_moves_stock_and_cash()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var pid = await Product(c, "Kurulum", sale: 500m, stock: 20m);
        var cashBefore = await CashBalance(c, cash);

        var q = await Quote(c, pid, 3m, 500m);
        var qid = Id(q);
        Assert.Equal(1800m, D(q, "grandTotal")); // 1500 + %20

        var converted = await Post(c, $"/api/quotes/{qid}/convert", new
        {
            payment = new { cashAccountId = cash, amount = 1800m, method = "Cash" },
        });
        Assert.Equal("Converted", converted.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(converted.GetProperty("invoiceId").GetString()));

        // Mali etki TAM BİR KEZ: stok düştü, kasa arttı, fatura oluştu.
        Assert.Equal(17m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));
        Assert.Equal(cashBefore + 1800m, await CashBalance(c, cash));
        var invoices = (await Get(c, "/api/invoices?pageSize=10")).GetProperty("items");
        Assert.Equal(1, invoices.GetArrayLength());
        Assert.Equal(1800m, D(invoices[0], "grandTotal"));

        // İkinci dönüşüm reddedilir (çift fatura olmaz).
        var again = await c.PostAsJsonAsync($"/api/quotes/{qid}/convert", new
        {
            payment = new { cashAccountId = cash, amount = 1800m, method = "Cash" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(1, (await Get(c, "/api/invoices?pageSize=10")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Convert_does_not_apply_contact_discount_twice()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var pid = await Product(c, "Bakım", sale: 1000m, stock: 10m);
        // %20 indirimli cari — teklifte fiyat ZATEN pazarlıklı (800) yazıldı.
        var cid = Id(await Post(c, "/api/contacts", new { type = "Customer", name = "İndirimli", openingBalance = 0m, discountRate = 20m }));

        var q = await Quote(c, pid, 1m, 800m, contactId: cid);
        Assert.Equal(960m, D(q, "grandTotal")); // 800 + %20 KDV

        var converted = await Post(c, $"/api/quotes/{Id(q)}/convert", new
        {
            payment = new { cashAccountId = cash, amount = 960m, method = "Cash" },
        });

        // Fatura teklifle BİREBİR aynı olmalı — cari indirimi tekrar uygulanıp 768'e düşmemeli.
        var inv = await Get(c, $"/api/invoices/{converted.GetProperty("invoiceId").GetString()}");
        Assert.Equal(960m, D(inv, "grandTotal"));
        Assert.Equal(800m, D(inv.GetProperty("lines")[0], "unitPrice"));
    }

    [Fact]
    public async Task Convert_without_contact_and_without_payment_is_rejected()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Servis", sale: 300m, stock: 5m);
        var q = await Quote(c, pid, 1m, 300m); // carisiz

        var res = await c.PostAsJsonAsync($"/api/quotes/{Id(q)}/convert", new { payment = (object?)null });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        // Teklif hâlâ dönüştürülmemiş olmalı.
        Assert.Equal("Draft", (await Get(c, $"/api/quotes/{Id(q)}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Failed_conversion_leaves_quote_unconverted()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var pid = await Product(c, "Kıt Ürün", sale: 100m, stock: 1m);
        var q = await Quote(c, pid, 5m, 100m); // stoktan fazla

        var res = await c.PostAsJsonAsync($"/api/quotes/{Id(q)}/convert", new
        {
            payment = new { cashAccountId = cash, amount = 600m, method = "Cash" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // Fatura oluşmadığı gibi teklif de Converted'a KAYMAMALI (aynı işlemde geri alınır).
        var after = await Get(c, $"/api/quotes/{Id(q)}");
        Assert.Equal("Draft", after.GetProperty("status").GetString());
        Assert.True(after.GetProperty("invoiceId").ValueKind == JsonValueKind.Null);
        Assert.Equal(1m, D(await Get(c, $"/api/products/{pid}"), "currentStock"));
    }

    [Fact]
    public async Task Converted_quote_is_locked()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var pid = await Product(c, "Ürün", sale: 200m, stock: 10m);
        var q = await Quote(c, pid, 1m, 200m);
        await Post(c, $"/api/quotes/{Id(q)}/convert", new { payment = new { cashAccountId = cash, amount = 240m, method = "Cash" } });

        var edit = await c.PutAsJsonAsync($"/api/quotes/{Id(q)}", new
        {
            contactId = (string?)null, customerName = "X", date = (string?)null, validUntil = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 2m, unitPrice = 200m, vatRate = 20m } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);

        var del = await c.DeleteAsync($"/api/quotes/{Id(q)}");
        Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);

        var status = await c.PutAsJsonAsync($"/api/quotes/{Id(q)}/status", new { status = "Rejected" });
        Assert.Equal(HttpStatusCode.BadRequest, status.StatusCode);
    }

    [Fact]
    public async Task Status_flow_and_converted_cannot_be_set_manually()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Ürün", sale: 100m, stock: 10m);
        var q = await Quote(c, pid, 1m, 100m);

        var sent = await Put(c, $"/api/quotes/{Id(q)}/status", new { status = "Sent" });
        Assert.Equal("Sent", sent.GetProperty("status").GetString());

        var accepted = await Put(c, $"/api/quotes/{Id(q)}/status", new { status = "Accepted" });
        Assert.Equal("Accepted", accepted.GetProperty("status").GetString());

        // "Converted" elle yazılamaz — yoksa faturasız "dönüştürülmüş" teklif oluşurdu.
        var fake = await c.PutAsJsonAsync($"/api/quotes/{Id(q)}/status", new { status = "Converted" });
        Assert.Equal(HttpStatusCode.BadRequest, fake.StatusCode);
    }

    [Fact]
    public async Task Expired_is_derived_from_valid_until()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Ürün", sale: 100m, stock: 10m);

        var q = await Post(c, "/api/quotes", new
        {
            contactId = (string?)null, customerName = "Eski Teklif",
            date = DateTime.UtcNow.AddDays(-30), validUntil = DateTime.UtcNow.AddDays(-5), note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 20m } },
        });
        Assert.True(q.GetProperty("isExpired").GetBoolean());

        // Süresi geçmiş teklif yine de dönüştürülebilir (işletme kararı) — sadece uyarı amaçlı bir bayrak.
        var list = await Get(c, "/api/quotes?pageSize=10");
        Assert.True(list.GetProperty("items")[0].GetProperty("isExpired").GetBoolean());
    }

    [Fact]
    public async Task Quote_can_be_edited_and_lines_are_replaced()
    {
        var c = await RegisterAsync();
        var p1 = await Product(c, "Ürün A", sale: 100m, stock: 10m);
        var p2 = await Product(c, "Ürün B", sale: 250m, stock: 10m);
        var q = await Quote(c, p1, 1m, 100m);

        // Satırlar tamamen değiştirilir (sil-yeniden kur yolu gerçekten çalışmalı).
        var updated = await Put(c, $"/api/quotes/{Id(q)}", new
        {
            contactId = (string?)null, customerName = "Güncel Müşteri",
            date = (string?)null, validUntil = (string?)null, note = "revize teklif",
            lines = new[] { new { productId = p2, quantity = 2m, unitPrice = 250m, vatRate = 20m } },
        });
        Assert.Equal(1, updated.GetProperty("lines").GetArrayLength());
        Assert.Equal("Ürün B", updated.GetProperty("lines")[0].GetProperty("productName").GetString());
        Assert.Equal(600m, D(updated, "grandTotal")); // 500 + %20
        Assert.Equal("Güncel Müşteri", updated.GetProperty("customerName").GetString());
    }

    [Fact]
    public async Task Quote_requires_at_least_one_line_and_valid_dates()
    {
        var c = await RegisterAsync();
        var pid = await Product(c, "Ürün", sale: 100m, stock: 10m);

        var empty = await c.PostAsJsonAsync("/api/quotes", new
        {
            contactId = (string?)null, customerName = "X", date = (string?)null, validUntil = (string?)null, note = (string?)null,
            lines = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var badDates = await c.PostAsJsonAsync("/api/quotes", new
        {
            contactId = (string?)null, customerName = "X",
            date = DateTime.UtcNow, validUntil = DateTime.UtcNow.AddDays(-3), note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 20m } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badDates.StatusCode);
    }
}
