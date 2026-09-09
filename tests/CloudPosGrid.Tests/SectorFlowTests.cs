using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

[Collection("api")]
public class SectorFlowTests
{
    private readonly ApiFixture _fx;
    public SectorFlowTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"t{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    // ---- HTTP/JSON yardımcıları ----
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string method, string url)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"{method} {url} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), "POST", url);
    private static async Task<JsonElement> Put(HttpClient c, string url, object body) => await ReadAsync(await c.PutAsJsonAsync(url, body), "PUT", url);
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), "GET", url);

    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    // ---- Ortak kurulum yardımcıları ----
    private async Task<HttpClient> RegisterAsync(string businessType)
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var res = await Post(client, "/api/auth/register", new
        {
            companyName = "Test İşletme",
            fullName = "Sahip",
            email,
            password = "test1234",
            businessType,
            code = "111111",
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>Personel yönetimi gerektiren testler için tenant'ı Kurumsal pakete yükseltir.</summary>
    private async Task UpgradeToEnterpriseAsync(HttpClient c)
    {
        var me = await Get(c, "/api/auth/me");
        var tenantId = Guid.Parse(me.GetProperty("tenantId").GetString()!);
        await _fx.ActivateEnterpriseAsync(tenantId);
    }

    private static async Task<string> CashAccount(HttpClient c)
    {
        var accts = await Get(c, "/api/cash-accounts");
        if (accts.ValueKind == JsonValueKind.Array && accts.GetArrayLength() > 0)
            return Id(accts[0]);
        return Id(await Post(c, "/api/cash-accounts", new { name = "Kasa", type = "Cash", openingBalance = 0m }));
    }

    private static async Task<string> Category(HttpClient c, string name = "Genel")
        => Id(await Post(c, "/api/categories", new { name }));

    private static async Task<JsonElement> Product(HttpClient c, string name, decimal sale, decimal purchase, decimal stock, bool isService = false, string? categoryId = null)
        => await Post(c, "/api/products", new
        {
            sku = (string?)null,
            barcode = (string?)null,
            name,
            categoryId,
            unit = "adet",
            purchasePrice = purchase,
            salePrice = sale,
            vatRate = 10m,
            openingStock = stock,
            minStock = 0m,
            isService,
        });

    // ---- Testler ----

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        var c = _fx.Factory.CreateClient();
        var r = await c.GetAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Healthy", (await r.Content.ReadAsStringAsync()).Trim());
    }

    [Theory]
    [InlineData("General")]
    [InlineData("Hospitality")]
    [InlineData("Retail")]
    [InlineData("Service")]
    [InlineData("Beauty")]
    public async Task Register_and_me_returns_owner_and_sector(string sector)
    {
        var c = await RegisterAsync(sector);
        var me = await Get(c, "/api/auth/me");
        Assert.Equal(sector, me.GetProperty("businessType").GetString());
        Assert.Equal("Owner", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task QuickSale_decrements_stock_and_shows_in_dashboard_and_reports()
    {
        var c = await RegisterAsync("Retail");
        var cash = await CashAccount(c);
        var cat = await Category(c, "İçecek");
        var pid = Id(await Product(c, "Kola", sale: 20, purchase: 12, stock: 100, categoryId: cat));

        var inv = await Post(c, "/api/invoices", new
        {
            type = "Sales",
            contactId = (string?)null,
            date = (string?)null,
            note = "Hızlı satış",
            lines = new[] { new { productId = pid, quantity = 2m, unitPrice = 20m, vatRate = 10m } },
            payment = new { cashAccountId = cash, amount = 44m, method = "Cash" },
        });
        Assert.Equal("Paid", inv.GetProperty("status").GetString());

        var prod = await Get(c, $"/api/products/{pid}");
        Assert.Equal(98m, prod.GetProperty("currentStock").GetDecimal());

        var dash = await Get(c, "/api/dashboard/summary");
        Assert.Equal(44m, dash.GetProperty("todayIncome").GetDecimal());

        var rep = await Get(c, "/api/reports/sales");
        Assert.Equal(1, rep.GetProperty("salesCount").GetInt32());
        Assert.Equal(44m, rep.GetProperty("salesTotal").GetDecimal());
        Assert.True(rep.GetProperty("estimatedProfit").GetDecimal() > 0);
    }

    [Fact]
    public async Task Sale_with_same_clientSaleId_is_idempotent_no_double_sale()
    {
        // Çevrimdışı POS kuyruğu: ağ kesilip aynı satış yeniden gönderilirse ÇİFT satış OLMAMALI.
        var c = await RegisterAsync("Retail");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Kola", sale: 20, purchase: 12, stock: 100));

        var saleId = Guid.NewGuid().ToString("N");
        var body = new
        {
            type = "Sales",
            contactId = (string?)null,
            date = (string?)null,
            note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 3m, unitPrice = 20m, vatRate = 10m } },
            payment = new { cashAccountId = cash, amount = 66m, method = "Cash" },
            clientSaleId = saleId,
        };

        var first = await Post(c, "/api/invoices", body);
        var second = await Post(c, "/api/invoices", body); // aynı clientSaleId → idempotent

        // Aynı fatura döner, yenisi kesilmez
        Assert.Equal(Id(first), Id(second));
        Assert.Equal(first.GetProperty("number").GetString(), second.GetProperty("number").GetString());

        // Stok yalnız BİR kez düştü (100 − 3 = 97)
        var prod = await Get(c, $"/api/products/{pid}");
        Assert.Equal(97m, prod.GetProperty("currentStock").GetDecimal());

        // Tek satış faturası + rapor tek satış
        var rep = await Get(c, "/api/reports/sales");
        Assert.Equal(1, rep.GetProperty("salesCount").GetInt32());
    }

    [Fact]
    public async Task Void_and_cash_expense_write_tenant_audit_events()
    {
        var c = await RegisterAsync("Retail");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Kola", sale: 20, purchase: 12, stock: 10));

        var inv = await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 20m, vatRate = 10m } },
            payment = new { cashAccountId = cash, amount = 22m, method = "Cash" },
        });
        await Post(c, $"/api/invoices/{Id(inv)}/void", new { });

        // Elle kasa gideri (sorumluluk izi gerektiren ikinci eylem)
        await Post(c, "/api/finance/transactions", new
        {
            cashAccountId = cash, type = "Expense", category = "Kira", amount = 500m,
            description = "Ocak kirası", paymentMethod = "Cash", date = (string?)null,
        });

        var audit = await Get(c, "/api/audit?limit=50");
        var events = audit.EnumerateArray().ToList();

        var voided = events.First(x => x.GetProperty("action").GetString() == "InvoiceVoided");
        Assert.Equal("Invoice", voided.GetProperty("targetType").GetString());
        Assert.Equal(Id(inv), voided.GetProperty("targetId").GetString());
        Assert.Contains(inv.GetProperty("number").GetString()!, voided.GetProperty("details").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(voided.GetProperty("actorEmail").GetString())); // KİM yaptı

        Assert.Contains(events, x => x.GetProperty("action").GetString() == "CashExpenseCreated");
    }

    [Fact]
    public async Task Appointment_carries_assigned_staff()
    {
        var c = await RegisterAsync("Beauty");
        var staffId = Guid.NewGuid().ToString();
        await Post(c, "/api/appointments", new
        {
            customerName = "Ayşe",
            phone = (string?)null,
            serviceName = "Saç Kesimi",
            startsAt = "2026-07-15T10:00:00",
            durationMinutes = 30,
            price = 150m,
            note = (string?)null,
            productIds = (string?)null,
            staffId,
        });

        var list = await Get(c, "/api/appointments?from=2026-07-15T00:00:00&to=2026-07-16T00:00:00");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(staffId, list[0].GetProperty("staffId").GetString());
    }

    [Fact]
    public async Task Service_product_can_be_sold_without_stock()
    {
        var c = await RegisterAsync("Beauty");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Saç Kesimi", sale: 200, purchase: 0, stock: 0, isService: true));

        var inv = await Post(c, "/api/invoices", new
        {
            type = "Sales",
            contactId = (string?)null,
            date = (string?)null,
            note = "Hizmet",
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 200m, vatRate = 0m } },
            payment = new { cashAccountId = cash, amount = 200m, method = "Card" },
        });
        Assert.Equal("Paid", inv.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Takeaway_order_add_merge_and_close()
    {
        var c = await RegisterAsync("Hospitality");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Latte", sale: 50, purchase: 20, stock: 100));

        var oid = Id(await Post(c, "/api/orders", new { type = "Takeaway", tableId = (string?)null, contactId = (string?)null, label = "Paket 1", note = (string?)null }));

        var afterAdd = await Post(c, $"/api/orders/{oid}/lines", new { productId = pid, quantity = 2m, note = (string?)null });
        Assert.Equal(1, afterAdd.GetProperty("lines").GetArrayLength());
        Assert.Equal(2m, afterAdd.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(110m, afterAdd.GetProperty("grandTotal").GetDecimal());

        var afterMerge = await Post(c, $"/api/orders/{oid}/lines", new { productId = pid, quantity = 1m, note = (string?)null });
        Assert.Equal(1, afterMerge.GetProperty("lines").GetArrayLength());
        Assert.Equal(3m, afterMerge.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        var closed = await Post(c, $"/api/orders/{oid}/close", new { payment = new { cashAccountId = cash, amount = 165m, method = "Cash" }, contactId = (string?)null, tip = 0m });
        Assert.False(string.IsNullOrEmpty(closed.GetProperty("invoiceId").GetString()));

        var prod = await Get(c, $"/api/products/{pid}");
        Assert.Equal(97m, prod.GetProperty("currentStock").GetDecimal());
    }

    [Fact]
    public async Task DineIn_table_open_add_and_close()
    {
        var c = await RegisterAsync("Hospitality");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Çay", sale: 15, purchase: 5, stock: 200));
        var tid = Id(await Post(c, "/api/tables", new { name = "M1", areaId = (string?)null, sortOrder = 0 }));

        var oid = Id(await Post(c, "/api/orders", new { type = "DineIn", tableId = tid, contactId = (string?)null, label = (string?)null, note = (string?)null }));
        await Post(c, $"/api/orders/{oid}/lines", new { productId = pid, quantity = 4m, note = (string?)null });

        var closed = await Post(c, $"/api/orders/{oid}/close", new { payment = new { cashAccountId = cash, amount = 66m, method = "Cash" }, contactId = (string?)null, tip = 0m });
        Assert.False(string.IsNullOrEmpty(closed.GetProperty("invoiceId").GetString()));

        // Masa tekrar boşalmalı: aynı masaya yeni adisyon açılabilmeli
        var reopened = await Post(c, "/api/orders", new { type = "DineIn", tableId = tid, contactId = (string?)null, label = (string?)null, note = (string?)null });
        Assert.Equal("Open", reopened.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Service_work_order_flow()
    {
        var c = await RegisterAsync("Service");
        var cash = await CashAccount(c);
        var part = Id(await Product(c, "Fren Balatası", sale: 300, purchase: 150, stock: 20));
        var labor = Id(await Product(c, "İşçilik", sale: 250, purchase: 0, stock: 0, isService: true));

        var oid = Id(await Post(c, "/api/orders", new { type = "Service", tableId = (string?)null, contactId = (string?)null, label = "Ahmet", note = (string?)null, assetInfo = "34 ABC 123" }));
        await Post(c, $"/api/orders/{oid}/lines", new { productId = part, quantity = 2m, note = (string?)null });
        await Post(c, $"/api/orders/{oid}/lines", new { productId = labor, quantity = 1m, note = (string?)null });

        var st = await Put(c, $"/api/orders/{oid}/work-status", new { status = "InProgress" });
        Assert.Equal("InProgress", st.GetProperty("workStatus").GetString());

        var closed = await Post(c, $"/api/orders/{oid}/close", new { payment = new { cashAccountId = cash, amount = 935m, method = "Card" }, contactId = (string?)null, tip = 0m });
        Assert.False(string.IsNullOrEmpty(closed.GetProperty("invoiceId").GetString()));
    }

    [Fact]
    public async Task Appointment_create_and_list_shows_pending_price()
    {
        var c = await RegisterAsync("Beauty");
        var day = DateTime.UtcNow.Date;
        await Post(c, "/api/appointments", new
        {
            customerName = "Ayşe",
            phone = "0555",
            serviceName = "Fön",
            startsAt = day.AddHours(10).ToString("s"),
            durationMinutes = 30,
            price = 150m,
            note = (string?)null,
        });

        var list = await Get(c, $"/api/appointments?from={day:s}&to={day.AddDays(1):s}");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Scheduled", list[0].GetProperty("status").GetString());
        Assert.Equal(150m, list[0].GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Appointment_collect_records_sale_in_dashboard_and_reports()
    {
        var c = await RegisterAsync("Beauty");
        var cash = await CashAccount(c);
        var svcId = Id(await Product(c, "Saç Boyası", sale: 300, purchase: 0, stock: 0, isService: true));
        var day = DateTime.UtcNow.Date;

        var appt = await Post(c, "/api/appointments", new
        {
            customerName = "Zeynep",
            phone = (string?)null,
            serviceName = "Saç Boyası",
            startsAt = day.AddHours(11).ToString("s"),
            durationMinutes = 45,
            price = 300m,
            note = (string?)null,
            productIds = svcId,
        });
        var aid = Id(appt);

        var before = (await Get(c, "/api/dashboard/summary")).GetProperty("todayIncome").GetDecimal();
        var collected = await Post(c, $"/api/appointments/{aid}/collect", new { cashAccountId = cash, method = "Cash" });
        Assert.Equal("Done", collected.GetProperty("status").GetString());

        var after = (await Get(c, "/api/dashboard/summary")).GetProperty("todayIncome").GetDecimal();
        Assert.True(after > before);

        var rep = await Get(c, "/api/reports/sales");
        Assert.Equal(1, rep.GetProperty("salesCount").GetInt32());
    }

    [Fact]
    public async Task Staff_create_and_pin_login_switches_user()
    {
        var c = await RegisterAsync("Retail");
        await UpgradeToEnterpriseAsync(c); // personel yönetimi Kurumsal pakete özel
        var staff = await Post(c, "/api/staff", new { fullName = "Kasiyer Ali", email = NewEmail(), password = "staff123", role = "Cashier", pin = "4321" });
        Assert.Equal("Cashier", staff.GetProperty("role").GetString());
        Assert.True(staff.GetProperty("hasPin").GetBoolean());

        var pinRes = await Post(c, "/api/auth/pin-login", new { pin = "4321" });
        var c2 = _fx.Factory.CreateClient();
        c2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pinRes.GetProperty("accessToken").GetString());
        var me = await Get(c2, "/api/auth/me");
        Assert.Equal("Cashier", me.GetProperty("role").GetString());
        Assert.Equal("Kasiyer Ali", me.GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Cashier_can_sell_and_view_receipt_but_not_finance()
    {
        var owner = await RegisterAsync("Retail");
        await UpgradeToEnterpriseAsync(owner); // personel yönetimi Kurumsal pakete özel
        var cash = await CashAccount(owner);
        var pid = Id(await Product(owner, "Ürün", sale: 50, purchase: 20, stock: 100));
        await Post(owner, "/api/staff", new { fullName = "Kasiyer", email = NewEmail(), password = "pass1234", role = "Cashier", pin = "9999" });

        var pin = await Post(owner, "/api/auth/pin-login", new { pin = "9999" });
        var cashier = _fx.Factory.CreateClient();
        cashier.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pin.GetProperty("accessToken").GetString());

        // Kasiyer satış yapabilmeli ve fişi (GET {id}) görebilmeli
        var inv = await Post(cashier, "/api/invoices", new
        {
            type = "Sales",
            contactId = (string?)null,
            date = (string?)null,
            note = "POS",
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 50m, vatRate = 10m } },
            payment = new { cashAccountId = cash, amount = 55m, method = "Cash" },
        });
        await Get(cashier, $"/api/invoices/{Id(inv)}");

        // Ama geri ofis uçlarına erişememeli (403)
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/finance/transactions")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/invoices")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_sales_of_last_unit_do_not_oversell()
    {
        var c = await RegisterAsync("Retail");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Son Ürün", sale: 100, purchase: 50, stock: 1));

        async Task<bool> TrySell()
        {
            var r = await c.PostAsJsonAsync("/api/invoices", new
            {
                type = "Sales",
                contactId = (string?)null,
                date = (string?)null,
                note = "race",
                lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 0m } },
                payment = new { cashAccountId = cash, amount = 100m, method = "Cash" },
            });
            return r.IsSuccessStatusCode;
        }

        // İki eşzamanlı satış: değişmez kural -> aşırı satış olmamalı (tam olarak biri başarılı, stok 0).
        var results = await Task.WhenAll(TrySell(), TrySell());
        Assert.Equal(1, results.Count(x => x));

        var prod = await Get(c, $"/api/products/{pid}");
        Assert.Equal(0m, prod.GetProperty("currentStock").GetDecimal());
    }

    [Fact]
    public async Task Customer_discount_is_applied_to_sale()
    {
        var c = await RegisterAsync("Retail");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Ürün", sale: 100, purchase: 60, stock: 50));
        var cid = Id(await Post(c, "/api/contacts", new
        {
            type = "Customer",
            name = "Sadık Müşteri",
            taxOffice = (string?)null,
            taxNo = (string?)null,
            phone = (string?)null,
            email = (string?)null,
            address = (string?)null,
            openingBalance = 0m,
            discountRate = 10m,
        }));

        // %10 indirim -> birim 90, KDV %10 -> toplam 99
        var inv = await Post(c, "/api/invoices", new
        {
            type = "Sales",
            contactId = cid,
            date = (string?)null,
            note = "satış",
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 10m } },
            payment = new { cashAccountId = cash, amount = 99m, method = "Cash" },
        });
        Assert.Equal(90m, inv.GetProperty("subtotal").GetDecimal());
        Assert.Equal(99m, inv.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(90m, inv.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal("Paid", inv.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Credit_sale_increases_contact_balance()
    {
        var c = await RegisterAsync("General");
        var cid = Id(await Post(c, "/api/contacts", new
        {
            type = "Customer",
            name = "Veresiye Müşteri",
            taxOffice = (string?)null,
            taxNo = (string?)null,
            phone = (string?)null,
            email = (string?)null,
            address = (string?)null,
            openingBalance = 0m,
        }));
        var pid = Id(await Product(c, "Ürün", sale: 100, purchase: 60, stock: 50));

        await Post(c, "/api/invoices", new
        {
            type = "Sales",
            contactId = cid,
            date = (string?)null,
            note = "Veresiye",
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 10m } },
            payment = (object?)null,
        });

        var refreshed = await Get(c, $"/api/contacts/{cid}");
        Assert.Equal(110m, refreshed.GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task SplitClose_partial_keeps_order_open_and_remaining_is_collectible()
    {
        var c = await RegisterAsync("Hospitality");
        var cash = await CashAccount(c);
        var su = Id(await Product(c, "Su", sale: 15, purchase: 5, stock: 100));
        var espresso = Id(await Product(c, "Espresso", sale: 40, purchase: 12, stock: 100));
        var tid = Id(await Post(c, "/api/tables", new { name = "M1", areaId = (string?)null, sortOrder = 0 }));

        var oid = Id(await Post(c, "/api/orders", new { type = "DineIn", tableId = tid, contactId = (string?)null, label = (string?)null, note = (string?)null }));
        await Post(c, $"/api/orders/{oid}/lines", new { productId = su, quantity = 2m, note = (string?)null });
        var withBoth = await Post(c, $"/api/orders/{oid}/lines", new { productId = espresso, quantity = 2m, note = (string?)null });
        Assert.Equal(121m, withBoth.GetProperty("grandTotal").GetDecimal()); // (30+80) + %10 KDV

        var espressoLineId = Id(withBoth.GetProperty("lines").EnumerateArray()
            .First(l => l.GetProperty("productId").GetString() == espresso));

        // Espresso x2'yi böl ve tahsil et (88). Su x2 (33) adisyonda AÇIK kalmalı.
        var afterSplit = await Post(c, $"/api/orders/{oid}/split-close", new
        {
            items = new[] { new { lineId = espressoLineId, quantity = 2m } },
            payment = new { cashAccountId = cash, amount = 88m, method = "Card" },
            contactId = (string?)null,
        });
        Assert.Equal("Open", afterSplit.GetProperty("status").GetString());   // regresyon: kapanmamalı
        Assert.Equal(33m, afterSplit.GetProperty("grandTotal").GetDecimal());  // kalan = Su x2 + KDV
        Assert.Equal(1, afterSplit.GetProperty("lines").GetArrayLength());
        Assert.True(string.IsNullOrEmpty(afterSplit.GetProperty("invoiceId").GetString()));

        // Kalan tahsil edilebilmeli
        var closed = await Post(c, $"/api/orders/{oid}/close", new
        {
            payment = new { cashAccountId = cash, amount = 33m, method = "Cash" },
            contactId = (string?)null,
            tip = 0m,
        });
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(closed.GetProperty("invoiceId").GetString()));

        // Her iki üründen de stok düşmüş olmalı (bölünen + kalan)
        Assert.Equal(98m, (await Get(c, $"/api/products/{espresso}")).GetProperty("currentStock").GetDecimal());
        Assert.Equal(98m, (await Get(c, $"/api/products/{su}")).GetProperty("currentStock").GetDecimal());
    }

    [Fact]
    public async Task Invoice_void_reverses_stock_and_contact_balance()
    {
        var c = await RegisterAsync("Retail");
        var pid = Id(await Product(c, "Ürün", sale: 100, purchase: 60, stock: 50));
        var cid = Id(await Post(c, "/api/contacts", new
        {
            type = "Customer", name = "Müşteri", taxOffice = (string?)null, taxNo = (string?)null,
            phone = (string?)null, email = (string?)null, address = (string?)null, openingBalance = 0m,
        }));

        // Veresiye satış: cari borç 110, stok 49
        var inv = await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = cid, date = (string?)null, note = "satış",
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 10m } },
            payment = (object?)null,
        });
        var invId = Id(inv);
        Assert.Equal(110m, (await Get(c, $"/api/contacts/{cid}")).GetProperty("balance").GetDecimal());
        Assert.Equal(49m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());

        // İptal: stok + cari geri alınmalı, fatura Cancelled
        var voided = await Post(c, $"/api/invoices/{invId}/void", new { });
        Assert.Equal("Cancelled", voided.GetProperty("status").GetString());
        Assert.Equal(0m, (await Get(c, $"/api/contacts/{cid}")).GetProperty("balance").GetDecimal());
        Assert.Equal(50m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());

        // İkinci kez iptal reddedilmeli
        var second = await c.PostAsJsonAsync($"/api/invoices/{invId}/void", new { });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Overpayment_within_tolerance_is_clamped_and_does_not_skew_balances()
    {
        var c = await RegisterAsync("Retail");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Ürün", sale: 100, purchase: 60, stock: 50));
        var cid = Id(await Post(c, "/api/contacts", new
        {
            type = "Customer", name = "Müşteri", taxOffice = (string?)null, taxNo = (string?)null,
            phone = (string?)null, email = (string?)null, address = (string?)null, openingBalance = 0m,
        }));

        // GrandTotal = 100 (KDV yok). 100.01 öde → tolerans içinde kabul edilir ama fazlalık kayda geçmez.
        var inv = await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = cid, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 0m } },
            payment = new { cashAccountId = cash, amount = 100.01m, method = "Cash" },
        });

        Assert.Equal("Paid", inv.GetProperty("status").GetString());
        Assert.Equal(100m, inv.GetProperty("paidAmount").GetDecimal());
        // Cari eksiye kaymamalı (100 borç − 100 tahsilat = 0); 1 kuruş sahte alacak oluşmamalı.
        Assert.Equal(0m, (await Get(c, $"/api/contacts/{cid}")).GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task Voided_sale_is_excluded_from_daily_close_payment_breakdown()
    {
        var c = await RegisterAsync("Retail");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Ürün", sale: 100, purchase: 60, stock: 50));
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // Nakit peşin satış (GrandTotal 100, KDV yok)
        var invId = Id(await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 0m } },
            payment = new { cashAccountId = cash, amount = 100m, method = "Cash" },
        }));

        // Void öncesi: gün sonu satış 100 + Nakit tahsilat 100
        var before = await Get(c, $"/api/reports/daily-close?date={today}");
        Assert.Equal(100m, before.GetProperty("salesTotal").GetDecimal());
        Assert.Equal(100m, PaymentSum(before));

        await Post(c, $"/api/invoices/{invId}/void", new { });

        // Void sonrası: satış 0 VE ödeme kırılımı da 0 (iptalli faturanın tahsilatı sayılmamalı — regresyon)
        var after = await Get(c, $"/api/reports/daily-close?date={today}");
        Assert.Equal(0m, after.GetProperty("salesTotal").GetDecimal());
        Assert.Equal(0m, PaymentSum(after));

        static decimal PaymentSum(JsonElement dc)
            => dc.GetProperty("byPaymentMethod").EnumerateArray().Sum(p => p.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Partial_refund_restores_stock_tracks_quantity_and_nets_profit()
    {
        var c = await RegisterAsync("Retail");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Ürün", sale: 100, purchase: 60, stock: 10));
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // Peşin nakit satış: 3 adet × 100 (KDV yok) = 300, stok 10→7
        var inv = await Post(c, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 3m, unitPrice = 100m, vatRate = 0m } },
            payment = new { cashAccountId = cash, amount = 300m, method = "Cash" },
        });
        var invId = Id(inv);
        var lineId = inv.GetProperty("lines")[0].GetProperty("id").GetString();
        Assert.Equal(7m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());

        // 1 adet kısmi iade (nakit geri) → stok 7→8, satır refundedQuantity=1
        var refunded = await Post(c, $"/api/invoices/{invId}/refund", new
        {
            lines = new[] { new { invoiceLineId = lineId, quantity = 1m } },
            cashAccountId = cash, note = (string?)null,
        });
        Assert.Equal(8m, (await Get(c, $"/api/products/{pid}")).GetProperty("currentStock").GetDecimal());
        Assert.Equal(1m, refunded.GetProperty("lines")[0].GetProperty("refundedQuantity").GetDecimal());

        // Kâr raporu net: kalan 2 adet → ciro 200 (300 değil), kâr 200−120=80
        var profit = await Get(c, $"/api/reports/profit?from={today}&to={today}");
        Assert.Equal(200m, profit.GetProperty("totalRevenue").GetDecimal());
        Assert.Equal(80m, profit.GetProperty("grossProfit").GetDecimal());

        // Over-refund koruması: kalan 2 adet iken 3 istenirse reddedilir
        var over = await c.PostAsJsonAsync($"/api/invoices/{invId}/refund", new
        {
            lines = new[] { new { invoiceLineId = lineId, quantity = 3m } }, cashAccountId = cash, note = (string?)null,
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, over.StatusCode);

        // Kısmen iade edilmiş fatura TAM iptal edilemez (çifte düzeltme önlenir)
        var voided = await c.PostAsJsonAsync($"/api/invoices/{invId}/void", new { });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, voided.StatusCode);
    }

    [Fact]
    public async Task Close_with_partial_payment_and_no_contact_is_rejected()
    {
        var c = await RegisterAsync("Hospitality");
        var cash = await CashAccount(c);
        var pid = Id(await Product(c, "Kola", sale: 100, purchase: 40, stock: 50));
        var oid = Id(await Post(c, "/api/orders", new { type = "Takeaway", tableId = (string?)null, contactId = (string?)null, label = "P", note = (string?)null }));
        await Post(c, $"/api/orders/{oid}/lines", new { productId = pid, quantity = 1m, note = (string?)null });

        // Toplam 110; carisiz yalnızca 50 öde -> reddedilmeli (kalan hiçbir yere yazılamaz)
        var r = await c.PostAsJsonAsync($"/api/orders/{oid}/close", new
        {
            payment = new { cashAccountId = cash, amount = 50m, method = "Cash" }, contactId = (string?)null, tip = 0m,
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);

        // Adisyon hâlâ açık olmalı (kapanmamış)
        Assert.Equal("Open", (await Get(c, $"/api/orders/{oid}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Branch_assigned_staff_is_locked_to_own_branch()
    {
        var owner = await RegisterAsync("Retail");
        await UpgradeToEnterpriseAsync(owner); // personel + çok şube Kurumsal'a özel

        // Varsayılan şube (A) + ikinci şube (B)
        var branches = await Get(owner, "/api/branches");
        var branchA = Id(branches[0]);
        var branchB = Id(await Post(owner, "/api/branches", new { name = "Şube B", address = (string?)null, phone = (string?)null }));

        static void SetBranch(HttpClient c, string b)
        {
            c.DefaultRequestHeaders.Remove("X-Branch-Id");
            c.DefaultRequestHeaders.Add("X-Branch-Id", b);
        }

        // Her şubede birer açık adisyon (owner kısıtsız; X-Branch-Id ile şube seçer)
        SetBranch(owner, branchA);
        await Post(owner, "/api/orders", new { type = "Takeaway", tableId = (string?)null, contactId = (string?)null, label = "A-adisyon", note = (string?)null });
        SetBranch(owner, branchB);
        await Post(owner, "/api/orders", new { type = "Takeaway", tableId = (string?)null, contactId = (string?)null, label = "B-adisyon", note = (string?)null });

        // A şubesine atanmış kasiyer + PIN
        await Post(owner, "/api/staff", new { fullName = "Kasiyer A", email = NewEmail(), password = "pass1234", role = "Cashier", pin = "7788", branchIds = new[] { branchA } });
        var pin = await Post(owner, "/api/auth/pin-login", new { pin = "7788" });
        var staff = _fx.Factory.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pin.GetProperty("accessToken").GetString());

        // Kasiyer B şubesi başlığını TAKLİT etse bile yalnız A şubesini görür (başlık yok sayılır).
        SetBranch(staff, branchB);
        var open = await Get(staff, "/api/orders/open");
        Assert.Equal(1, open.GetArrayLength());
        Assert.Equal("A-adisyon", open[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Multi_branch_member_switches_inside_set_but_never_outside()
    {
        var owner = await RegisterAsync("Retail");
        await UpgradeToEnterpriseAsync(owner);

        var branches = await Get(owner, "/api/branches");
        var branchA = Id(branches[0]);
        var branchB = Id(await Post(owner, "/api/branches", new { name = "Şube B", address = (string?)null, phone = (string?)null }));
        var branchC = Id(await Post(owner, "/api/branches", new { name = "Şube C", address = (string?)null, phone = (string?)null }));

        static void SetBranch(HttpClient c, string b)
        {
            c.DefaultRequestHeaders.Remove("X-Branch-Id");
            c.DefaultRequestHeaders.Add("X-Branch-Id", b);
        }

        // Her şubede birer adisyon
        SetBranch(owner, branchA);
        await Post(owner, "/api/orders", new { type = "Takeaway", tableId = (string?)null, contactId = (string?)null, label = "A-adisyon", note = (string?)null });
        SetBranch(owner, branchB);
        await Post(owner, "/api/orders", new { type = "Takeaway", tableId = (string?)null, contactId = (string?)null, label = "B-adisyon", note = (string?)null });
        SetBranch(owner, branchC);
        await Post(owner, "/api/orders", new { type = "Takeaway", tableId = (string?)null, contactId = (string?)null, label = "C-adisyon", note = (string?)null });

        // A VE B şubelerine üye kasiyer (C'ye üye DEĞİL)
        await Post(owner, "/api/staff", new
        {
            fullName = "Çok Şubeli", email = NewEmail(), password = "pass1234",
            role = "Cashier", pin = "9911", branchIds = new[] { branchA, branchB },
        });
        var pin = await Post(owner, "/api/auth/pin-login", new { pin = "9911" });
        var staff = _fx.Factory.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pin.GetProperty("accessToken").GetString());

        // Küme İÇİNDE geçiş serbest: B başlığı → B'nin adisyonu
        SetBranch(staff, branchB);
        var openB = await Get(staff, "/api/orders/open");
        Assert.Equal(1, openB.GetArrayLength());
        Assert.Equal("B-adisyon", openB[0].GetProperty("label").GetString());

        // A başlığı → A'nın adisyonu
        SetBranch(staff, branchA);
        var openA = await Get(staff, "/api/orders/open");
        Assert.Equal(1, openA.GetArrayLength());
        Assert.Equal("A-adisyon", openA[0].GetProperty("label").GetString());

        // Küme DIŞI (C) istenirse: C'ye DÜŞÜLMEZ, kümenin ilkine (A) geri düşer → C asla görülmez
        SetBranch(staff, branchC);
        var openC = await Get(staff, "/api/orders/open");
        Assert.Equal(1, openC.GetArrayLength());
        Assert.NotEqual("C-adisyon", openC[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Branch_locked_admin_cannot_touch_other_branch_staff()
    {
        var owner = await RegisterAsync("Retail");
        await UpgradeToEnterpriseAsync(owner);

        var branches = await Get(owner, "/api/branches");
        var branchA = Id(branches[0]);
        var branchB = Id(await Post(owner, "/api/branches", new { name = "Şube B", address = (string?)null, phone = (string?)null }));

        // A şubesine kilitli Admin
        var adminEmail = NewEmail();
        await Post(owner, "/api/staff", new
        {
            fullName = "Admin A", email = adminEmail, password = "pass1234",
            role = "Admin", pin = (string?)null, branchIds = new[] { branchA },
        });

        // B şubesine kilitli kasiyer (Admin A'nın DOKUNAMAMASI gereken hedef)
        var victim = Id(await Post(owner, "/api/staff", new
        {
            fullName = "Kasiyer B", email = NewEmail(), password = "pass1234",
            role = "Cashier", pin = (string?)null, branchIds = new[] { branchB },
        }));

        // Admin A olarak giriş yap
        var adminClient = _fx.Factory.CreateClient();
        var login = await Post(adminClient, "/api/auth/login", new { email = adminEmail, password = "pass1234" });
        adminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());

        // SİLEMEZ (varlığı sızdırmamak için 404)
        var del = await adminClient.DeleteAsync($"/api/staff/{victim}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, del.StatusCode);

        // PIN ATAYAMAZ
        var pin = await adminClient.PutAsJsonAsync($"/api/staff/{victim}/pin", new { pin = "4321" });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, pin.StatusCode);

        // KENDİ ŞUBESİNE ÇALAMAZ (rol/şube değiştiremez)
        var upd = await adminClient.PutAsJsonAsync($"/api/staff/{victim}", new
        {
            fullName = "Çalındı", role = "Cashier", isActive = false, branchIds = new[] { branchA },
        });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, upd.StatusCode);

        // Kurban hâlâ B şubesinde, aktif ve adı değişmemiş
        var list = await Get(owner, "/api/staff");
        var still = list.EnumerateArray().First(x => x.GetProperty("id").GetString() == victim);
        Assert.Equal("Kasiyer B", still.GetProperty("fullName").GetString());
        Assert.True(still.GetProperty("isActive").GetBoolean());
        Assert.Equal(branchB, still.GetProperty("branchIds").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task Branch_locked_user_cannot_read_other_branch_records_by_id()
    {
        var owner = await RegisterAsync("Retail");
        await UpgradeToEnterpriseAsync(owner);

        var branches = await Get(owner, "/api/branches");
        var branchA = Id(branches[0]);
        var branchB = Id(await Post(owner, "/api/branches", new { name = "Şube B", address = (string?)null, phone = (string?)null }));

        static void SetBranch(HttpClient c, string b)
        {
            c.DefaultRequestHeaders.Remove("X-Branch-Id");
            c.DefaultRequestHeaders.Add("X-Branch-Id", b);
        }

        // Şube B'de bir açık adisyon + bir satış faturası (owner)
        SetBranch(owner, branchB);
        var cash = await CashAccount(owner);
        var pid = Id(await Product(owner, "Ürün", sale: 100, purchase: 50, stock: 100));
        var orderB = Id(await Post(owner, "/api/orders", new { type = "Takeaway", tableId = (string?)null, contactId = (string?)null, label = "B", note = (string?)null }));
        var invB = Id(await Post(owner, "/api/invoices", new
        {
            type = "Sales", contactId = (string?)null, date = (string?)null, note = "B",
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 0m } },
            payment = new { cashAccountId = cash, amount = 100m, method = "Cash" },
        }));

        // Şube A'ya kilitli kasiyer
        await Post(owner, "/api/staff", new { fullName = "Kasiyer A", email = NewEmail(), password = "pass1234", role = "Cashier", pin = "3344", branchIds = new[] { branchA } });
        var pin = await Post(owner, "/api/auth/pin-login", new { pin = "3344" });
        var staff = _fx.Factory.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pin.GetProperty("accessToken").GetString());

        // B şubesinin adisyon/faturasını ID ile okumak → 404 (global şube filtresi IDOR'u kapatır)
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await staff.GetAsync($"/api/orders/{orderB}")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await staff.GetAsync($"/api/invoices/{invB}")).StatusCode);
    }

    [Fact]
    public async Task Branch_locked_user_cannot_collect_other_branch_appointment()
    {
        var owner = await RegisterAsync("Beauty");
        await UpgradeToEnterpriseAsync(owner);
        var cash = await CashAccount(owner);

        var branches = await Get(owner, "/api/branches");
        var branchA = Id(branches[0]);
        var branchB = Id(await Post(owner, "/api/branches", new { name = "Şube B", address = (string?)null, phone = (string?)null }));

        static void SetBranch(HttpClient c, string b)
        {
            c.DefaultRequestHeaders.Remove("X-Branch-Id");
            c.DefaultRequestHeaders.Add("X-Branch-Id", b);
        }

        // Şube B'de bir randevu (owner)
        SetBranch(owner, branchB);
        var day = DateTime.UtcNow.Date;
        var apptB = Id(await Post(owner, "/api/appointments", new
        {
            customerName = "Zeynep", phone = (string?)null, serviceName = "Fön",
            startsAt = day.AddHours(10).ToString("s"), durationMinutes = 30, price = 150m, note = (string?)null,
        }));

        // Şube A'ya kilitli kasiyer, B'nin randevusunu ID ile tahsil etmeye çalışır → 404 (global şube filtresi)
        await Post(owner, "/api/staff", new { fullName = "Kasiyer A", email = NewEmail(), password = "pass1234", role = "Cashier", pin = "5566", branchIds = new[] { branchA } });
        var pin = await Post(owner, "/api/auth/pin-login", new { pin = "5566" });
        var staff = _fx.Factory.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pin.GetProperty("accessToken").GetString());

        var r = await staff.PostAsJsonAsync($"/api/appointments/{apptB}/collect", new { cashAccountId = cash, method = "Cash" });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Pin_login_cannot_escalate_to_higher_role()
    {
        var owner = await RegisterAsync("Retail");
        await UpgradeToEnterpriseAsync(owner); // personel yönetimi Kurumsal pakete özel
        // Yüksek yetkili (Accountant) ve düşük yetkili (Cashier) personel, ikisinin de PIN'i var.
        await Post(owner, "/api/staff", new { fullName = "Muhasebe", email = NewEmail(), password = "pass1234", role = "Accountant", pin = "1111" });
        await Post(owner, "/api/staff", new { fullName = "Kasiyer", email = NewEmail(), password = "pass1234", role = "Cashier", pin = "2222" });

        // Owner -> Cashier'a geç (meşru, düşük yetkiye iniş)
        var toCashier = await Post(owner, "/api/auth/pin-login", new { pin = "2222" });
        var cashier = _fx.Factory.CreateClient();
        cashier.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", toCashier.GetProperty("accessToken").GetString());

        // Cashier, Accountant PIN'iyle yükselmeye çalışır -> engellenmeli (401, aday listesinden çıkarılır)
        var esc = await cashier.PostAsJsonAsync("/api/auth/pin-login", new { pin = "1111" });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, esc.StatusCode);
    }
}
