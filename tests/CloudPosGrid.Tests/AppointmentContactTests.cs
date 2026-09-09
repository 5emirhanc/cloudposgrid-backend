using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Randevu → cari bağı: tahsilatta fatura cariye kesilir (ekstre + bakiye + müşteri indirimi + puan).</summary>
[Collection("api")]
public class AppointmentContactTests
{
    private readonly ApiFixture _fx;
    public AppointmentContactTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"apc{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), $"POST {url}");
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
            companyName = "Güzellik Salonu", fullName = "Sahip", email, password = "test1234", businessType = "Beauty", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        return c;
    }

    private static async Task<string> CashAccount(HttpClient c)
    {
        var a = await Get(c, "/api/cash-accounts");
        return a.GetArrayLength() > 0 ? Id(a[0]) : Id(await Post(c, "/api/cash-accounts", new { name = "Kasa", type = "Cash", openingBalance = 0m }));
    }

    private static async Task<string> Service(HttpClient c, string name, decimal price, decimal vat = 20m)
        => Id(await Post(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = 0m, salePrice = price, vatRate = vat, openingStock = 0m, minStock = 0m, isService = true,
        }));

    [Fact]
    public async Task Appointment_linked_to_contact_bills_that_contact()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var svc = await Service(c, "Saç Kesimi", 500m);
        var cid = Id(await Post(c, "/api/contacts", new { type = "Customer", name = "Ayşe Yılmaz", openingBalance = 0m, discountRate = 0m }));

        var appt = await Post(c, "/api/appointments", new
        {
            customerName = "Ayşe Yılmaz", phone = (string?)null, serviceName = "Saç Kesimi",
            startsAt = DateTime.UtcNow.AddHours(2), durationMinutes = 60, price = 600m, note = (string?)null,
            productIds = svc, staffId = (string?)null, contactId = cid,
        });
        Assert.Equal(cid, appt.GetProperty("contactId").GetString());
        Assert.Equal("Ayşe Yılmaz", appt.GetProperty("contactName").GetString());

        await Post(c, $"/api/appointments/{Id(appt)}/collect", new { cashAccountId = cash, method = "Cash" });

        // Fatura CARİYE kesildi (carisiz değil) → ekstrede görünür.
        var invoices = await Get(c, "/api/invoices?pageSize=10");
        var inv = invoices.GetProperty("items").EnumerateArray().First();
        Assert.Equal("Ayşe Yılmaz", inv.GetProperty("contactName").GetString());
        Assert.Equal(600m, D(inv, "grandTotal")); // 500 + %20 KDV

        // Peşin tahsil edildiği için cari bakiyesi sıfır kalır (borç+ödeme netleşir).
        Assert.Equal(0m, D(await Get(c, $"/api/contacts/{cid}"), "balance"));
    }

    [Fact]
    public async Task Contact_discount_applies_and_payment_is_not_rejected()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var svc = await Service(c, "Manikür", 200m);
        // %10 indirimli müşteri: tahsilat tutarı da indirimli olmalı, yoksa "ödeme faturayı aşamaz" hatası gelirdi.
        var cid = Id(await Post(c, "/api/contacts", new { type = "Customer", name = "VIP Müşteri", openingBalance = 0m, discountRate = 10m }));

        var appt = await Post(c, "/api/appointments", new
        {
            customerName = "VIP Müşteri", phone = (string?)null, serviceName = "Manikür",
            startsAt = DateTime.UtcNow.AddHours(3), durationMinutes = 45, price = 240m, note = (string?)null,
            productIds = svc, staffId = (string?)null, contactId = cid,
        });
        await Post(c, $"/api/appointments/{Id(appt)}/collect", new { cashAccountId = cash, method = "Cash" });

        // 200 → %10 indirim → 180 net + %20 KDV = 216.
        var inv = (await Get(c, "/api/invoices?pageSize=10")).GetProperty("items").EnumerateArray().First();
        Assert.Equal(216m, D(inv, "grandTotal"));
        Assert.Equal(0m, D(await Get(c, $"/api/contacts/{cid}"), "balance"));
    }

    [Fact]
    public async Task Collected_appointment_sale_is_attributed_to_assigned_staff_not_collector()
    {
        // Kurumsal: personel yönetimi + personel bazlı satış raporu gerekir.
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var reg = await Post(c, "/api/auth/register", new
        {
            companyName = "Kuaför", fullName = "Sahip", email, password = "test1234", businessType = "Beauty", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg.GetProperty("accessToken").GetString());
        await _fx.ActivateEnterpriseAsync(Guid.Parse(reg.GetProperty("user").GetProperty("tenantId").GetString()!));

        var cash = await CashAccount(c);
        var svc = await Service(c, "Saç Boyama", 1000m);

        // Hizmeti yapan personel — tahsil eden Sahip'ten FARKLI bir kullanıcı.
        var staffId = Id(await Post(c, "/api/staff", new
        {
            fullName = "Elif Usta", email = NewEmail(), password = "staff1234", role = "Staff", pin = (string?)null, branchIds = (Guid[]?)null,
        }));

        // Randevu bu personele atanır.
        var appt = await Post(c, "/api/appointments", new
        {
            customerName = "Müşteri", phone = (string?)null, serviceName = "Saç Boyama",
            startsAt = DateTime.UtcNow.AddHours(1), durationMinutes = 60, price = 1200m, note = (string?)null,
            productIds = svc, staffId, contactId = (string?)null,
        });

        // Ana hesap (Sahip) tahsil eder — ama satış ATANAN personele yazılmalı.
        await Post(c, $"/api/appointments/{Id(appt)}/collect", new { cashAccountId = cash, method = "Cash" });

        var from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var rows = (await Get(c, $"/api/reports/staff-sales?from={from}&to={to}")).EnumerateArray().ToList();

        // Satış atanan personele (Elif) yazıldı — tahsil eden Sahip'e DEĞİL.
        var staffRow = rows.FirstOrDefault(r => r.GetProperty("sellerUserId").ValueKind == JsonValueKind.String
                                                && r.GetProperty("sellerUserId").GetString() == staffId);
        Assert.True(staffRow.ValueKind != JsonValueKind.Undefined, "Satış atanan personele yazılmadı (muhtemelen tahsil edene yazıldı).");
        Assert.True(staffRow.GetProperty("revenue").GetDecimal() > 0);
    }

    [Fact]
    public async Task Appointment_without_contact_still_works()
    {
        var c = await RegisterAsync();
        var cash = await CashAccount(c);
        var svc = await Service(c, "Fön", 150m);

        // Cari seçilmemiş randevu (geriye dönük uyum): serbest metin müşteri adı, carisiz fatura.
        var appt = await Post(c, "/api/appointments", new
        {
            customerName = "Geçici Müşteri", phone = (string?)null, serviceName = "Fön",
            startsAt = DateTime.UtcNow.AddHours(1), durationMinutes = 30, price = 180m, note = (string?)null,
            productIds = svc, staffId = (string?)null, contactId = (string?)null,
        });
        Assert.True(appt.GetProperty("contactId").ValueKind == JsonValueKind.Null);

        await Post(c, $"/api/appointments/{Id(appt)}/collect", new { cashAccountId = cash, method = "Cash" });
        var inv = (await Get(c, "/api/invoices?pageSize=10")).GetProperty("items").EnumerateArray().First();
        Assert.True(inv.GetProperty("contactName").ValueKind == JsonValueKind.Null);
        Assert.Equal(180m, D(inv, "grandTotal"));
    }
}
