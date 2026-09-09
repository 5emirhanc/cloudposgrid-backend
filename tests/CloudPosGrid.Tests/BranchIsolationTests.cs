using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudPosGrid.Tests;

/// <summary>Şube izolasyonu (#8) — global query filter'ın ATLANDIĞI okuma yolları: stok hareket geçmişi,
/// sayım geçmişi ve çok-şube konsolide analitik. Şubeye KİLİTLİ kullanıcı (AllowedBranchIds dolu) başka şubenin
/// hareketini/maliyetini, sayım oturumunu veya ciro/gider/net rakamını GÖREMEZ — X-Branch-Id taklidi de işe yaramaz.
/// Kısıtsız Owner ise tüm şubeleri görmeye DEVAM eder (konsolide panel bozulmadı).</summary>
[Collection("api")]
public class BranchIsolationTests
{
    private readonly ApiFixture _fx;
    public BranchIsolationTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"bi{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string ctx)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{ctx} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;
    private static string? Note(JsonElement e) => e.GetProperty("note").GetString();

    /// <summary>X-Branch-Id başlığıyla istek (aktif şubeyi belirler; kilitli kullanıcıda YOK SAYILIR).</summary>
    private static async Task<JsonElement> Send(HttpClient c, HttpMethod m, string url, object? body, string? branch)
    {
        var req = new HttpRequestMessage(m, url);
        if (branch is not null) req.Headers.Add("X-Branch-Id", branch);
        if (body is not null) req.Content = JsonContent.Create(body);
        return await ReadAsync(await c.SendAsync(req), $"{m} {url}");
    }
    private static Task<JsonElement> GetB(HttpClient c, string url, string? branch) => Send(c, HttpMethod.Get, url, null, branch);
    private static Task<JsonElement> PostB(HttpClient c, string url, object body, string? branch) => Send(c, HttpMethod.Post, url, body, branch);

    private async Task<HttpClient> RegisterOwnerAsync(bool chain = false)
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await ReadAsync(await c.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = "İzolasyon İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        }), "register");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        // Personel + çok şube + gelişmiş analitik Kurumsal pakete özel; akıllı sipariş önerisi ise ZİNCİR'e özel.
        var tenantId = Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!);
        if (chain) await _fx.ActivateChainAsync(tenantId);
        else await _fx.ActivateEnterpriseAsync(tenantId);
        return c;
    }

    /// <summary>Owner + iki şube (A = varsayılan Merkez, B = yeni) + A şubesine KİLİTLİ Admin istemcisi.</summary>
    private async Task<(HttpClient Owner, string BranchA, string BranchB, HttpClient Locked)> SetupAsync(bool chain = false)
    {
        var owner = await RegisterOwnerAsync(chain);
        var branches = await GetB(owner, "/api/branches", null);
        var branchA = Id(branches[0]);
        var branchB = Id(await PostB(owner, "/api/branches", new { name = "Şube B", address = (string?)null, phone = (string?)null }, null));

        // Şubeye kilitli Admin — desteklenen bir kurulum: rol yüksek, görüş alanı tek şube.
        var adminEmail = NewEmail();
        await PostB(owner, "/api/staff", new
        {
            fullName = "Şube A Yöneticisi", email = adminEmail, password = "pass1234",
            role = "Admin", pin = (string?)null, branchIds = new[] { branchA },
        }, null);

        var locked = _fx.Factory.CreateClient();
        var login = await ReadAsync(await locked.PostAsJsonAsync("/api/auth/login", new { email = adminEmail, password = "pass1234" }), "login");
        locked.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());

        return (owner, branchA, branchB, locked);
    }

    private static async Task<string> ProductAsync(HttpClient c, string name, decimal opening)
        => Id(await PostB(c, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name, categoryId = (string?)null, unit = "adet",
            purchasePrice = 10m, salePrice = 100m, vatRate = 0m, openingStock = opening, minStock = 0m, isService = false,
        }, null));

    [Fact]
    public async Task Branch_locked_user_sees_only_own_branch_stock_movements_and_count_history()
    {
        var (owner, branchA, branchB, locked) = await SetupAsync();

        // Ürünler başlıksız açılır → varsayılan (A) şubeye yazılır.
        var p1 = await ProductAsync(owner, "Kutu", opening: 100m);
        var p2 = await ProductAsync(owner, "Şişe", opening: 0m);

        // Her şubeye ayırt edilebilir notlu birer giriş hareketi.
        await PostB(owner, "/api/stock/movements", new { productId = p1, type = "In", quantity = 20m, unitCost = (decimal?)null, note = "A-giris" }, branchA);
        await PostB(owner, "/api/stock/movements", new { productId = p1, type = "In", quantity = 50m, unitCost = (decimal?)null, note = "B-giris" }, branchB);

        // Sayım: A'da 1 düzeltme, B'de 2 düzeltme → geçmiş kayıtları adjustedCount ile ayırt edilir.
        await PostB(owner, "/api/stock/count", new
        {
            items = new[] { new { productId = p1, countedQuantity = 5m } }, note = "A-sayim",
        }, branchA);
        await PostB(owner, "/api/stock/count", new
        {
            items = new[] { new { productId = p1, countedQuantity = 7m }, new { productId = p2, countedQuantity = 9m } },
            note = "B-sayim",
        }, branchB);

        // KİLİTLİ admin B başlığını TAKLİT etse bile yalnız A'nın hareketlerini görür (maliyet/transfer notu sızmaz).
        var moves = (await GetB(locked, "/api/stock/movements?pageSize=200", branchB)).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(moves, m => Note(m) == "A-giris");
        Assert.Contains(moves, m => Note(m) == "A-sayim");
        Assert.DoesNotContain(moves, m => Note(m) == "B-giris");
        Assert.DoesNotContain(moves, m => Note(m) == "B-sayim");

        // Ürün bazlı hareket geçmişi de sızdırmaz — AYNI ürünün B şubesindeki hareketleri gizli kalır.
        var p1Moves = (await GetB(locked, $"/api/products/{p1}/movements", branchB)).EnumerateArray().ToList();
        Assert.Contains(p1Moves, m => Note(m) == "A-giris");
        Assert.DoesNotContain(p1Moves, m => Note(m) == "B-giris");

        // Sayım geçmişi: yalnız A'nın oturumu (1 düzeltme); B'ninki (2 düzeltme) hiç görünmez.
        var history = (await GetB(locked, "/api/stock/count/history?limit=50", branchB)).EnumerateArray().ToList();
        Assert.Single(history);
        Assert.Equal(1, history[0].GetProperty("adjustedCount").GetInt32());

        // Regresyon yok: kısıtsız Owner (başlık yok) her iki şubenin geçmişini birleşik görmeye devam eder.
        var ownerHistory = (await GetB(owner, "/api/stock/count/history?limit=50", null)).EnumerateArray().ToList();
        Assert.Equal(2, ownerHistory.Count);
        Assert.Contains(ownerHistory, h => h.GetProperty("adjustedCount").GetInt32() == 2);
        var ownerMoves = (await GetB(owner, "/api/stock/movements?pageSize=200", null)).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(ownerMoves, m => Note(m) == "B-giris");
    }

    [Fact]
    public async Task Branch_analytics_hides_other_branch_figures_from_locked_admin_but_not_from_owner()
    {
        var (owner, branchA, branchB, locked) = await SetupAsync();
        var contact = Id(await PostB(owner, "/api/contacts", new { name = "Müşteri", type = "Customer" }, null));
        var pid = await ProductAsync(owner, "Palto", opening: 10m);
        // B şubesine de stok koy → oradaki satış şube-bazlı oversell kontrolüne takılmasın.
        await PostB(owner, "/api/stock/movements", new { productId = pid, type = "In", quantity = 10m, unitCost = (decimal?)null, note = (string?)null }, branchB);

        // A'da 100 ₺, B'de 500 ₺ ciro (veresiye satış — kasa hareketi gerekmez).
        await PostB(owner, "/api/invoices", new
        {
            type = "Sales", contactId = contact, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 100m, vatRate = 0m } }, payment = (object?)null,
        }, branchA);
        await PostB(owner, "/api/invoices", new
        {
            type = "Sales", contactId = contact, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 500m, vatRate = 0m } }, payment = (object?)null,
        }, branchB);

        // Aralık geniş tutulur (yerel/UTC gün sınırı testi bozmasın).
        var today = DateTime.UtcNow.Date;
        var url = $"/api/branch-analytics?from={today.AddDays(-2):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}";

        // KİLİTLİ admin B başlığını taklit etse de: yalnız A satırı, yalnız A cirosu. B'nin adı/rakamı hiçbir yerde yok.
        var lockedView = await GetB(locked, url, branchB);
        var lockedRows = lockedView.GetProperty("branches").EnumerateArray().ToList();
        Assert.Single(lockedRows);
        Assert.Equal(branchA, lockedRows[0].GetProperty("branchId").GetString());
        Assert.Equal(100m, lockedRows[0].GetProperty("revenue").GetDecimal());
        Assert.DoesNotContain(lockedRows, r => r.GetProperty("branchName").GetString() == "Şube B");
        Assert.DoesNotContain(lockedRows, r => r.GetProperty("revenue").GetDecimal() == 500m);
        Assert.Equal(100m, lockedView.GetProperty("totalRevenue").GetDecimal());

        // Regresyon yok: kısıtsız Owner konsolide paneli tam görür (iki şube + toplam ciro).
        var ownerView = await GetB(owner, url, null);
        var ownerRows = ownerView.GetProperty("branches").EnumerateArray().ToList();
        Assert.Equal(2, ownerRows.Count);
        Assert.Equal(100m, ownerRows.First(r => r.GetProperty("branchId").GetString() == branchA).GetProperty("revenue").GetDecimal());
        Assert.Equal(500m, ownerRows.First(r => r.GetProperty("branchId").GetString() == branchB).GetProperty("revenue").GetDecimal());
        Assert.Equal(600m, ownerView.GetProperty("totalRevenue").GetDecimal());
    }

    /// <summary>Envanter analitiği (#8 devamı): şubeye kilitli kullanıcı yalnız KENDİ şubesinin stok değerini,
    /// dönem COGS'unu ve ABC cirosunu görmeli — kısıtsız Owner'da tenant geneli toplam korunur.</summary>
    [Fact]
    public async Task Branch_locked_user_sees_only_own_branch_inventory_analytics()
    {
        var (owner, branchA, branchB, locked) = await SetupAsync();

        // Aynı üründen A'ya 10, B'ye 40 adet giriş (alış 10 ₺) → A değeri 100, tenant geneli 500.
        var pid = await ProductAsync(owner, "Analiz Ürünü", opening: 0m);
        await PostB(owner, "/api/stock/movements", new { productId = pid, type = "In", quantity = 10m, unitCost = (decimal?)null, note = "A-stok" }, branchA);
        await PostB(owner, "/api/stock/movements", new { productId = pid, type = "In", quantity = 40m, unitCost = (decimal?)null, note = "B-stok" }, branchB);

        // KİLİTLİ admin B başlığını taklit etse de yalnız A'nın stok değerini görür.
        var lockedInv = await GetB(locked, "/api/inventory-analytics?days=90", branchB);
        Assert.Equal(100m, lockedInv.GetProperty("valuation").GetProperty("totalValue").GetDecimal());
        Assert.Equal(10m, lockedInv.GetProperty("valuation").GetProperty("totalUnits").GetDecimal());

        // Regresyon yok: kısıtsız Owner "tüm şubeler"de (başlıksız) toplamı görmeye devam eder.
        var ownerInv = await GetB(owner, "/api/inventory-analytics?days=90", null);
        Assert.Equal(500m, ownerInv.GetProperty("valuation").GetProperty("totalValue").GetDecimal());
    }

    /// <summary>Sipariş önerisi (#8 devamı): hız ve stok SEÇİLİ ŞUBEDEN hesaplanmalı. Aksi halde şube
    /// kullanıcısına başka şubenin satış hızıyla üretilmiş yanlış öneri çıkar (ve o hacim sızar).</summary>
    [Fact]
    public async Task Replenishment_velocity_and_stock_are_scoped_to_selected_branch()
    {
        var (owner, branchA, branchB, locked) = await SetupAsync(chain: true); // öneri Zincir pakete özel
        var contact = Id(await PostB(owner, "/api/contacts", new { type = "Customer", name = "Müşteri", openingBalance = 0m, discountRate = 0m }, null));

        // Her iki şubeye stok; satış YALNIZ B şubesinde → A'da hız 0 olmalı, B'de hız var.
        var pid = await ProductAsync(owner, "Hızlı Ürün", opening: 0m);
        await PostB(owner, "/api/stock/movements", new { productId = pid, type = "In", quantity = 5m, unitCost = (decimal?)null, note = "A-stok" }, branchA);
        await PostB(owner, "/api/stock/movements", new { productId = pid, type = "In", quantity = 5m, unitCost = (decimal?)null, note = "B-stok" }, branchB);
        await PostB(owner, "/api/invoices", new
        {
            type = "Sales", contactId = contact, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 4m, unitPrice = 100m, vatRate = 0m } }, payment = (object?)null,
        }, branchB);

        // A'ya kilitli kullanıcı: A'da hiç satış yok → bu ürün için öneri ÇIKMAMALI (B'nin hızı sızmaz).
        var lockedRows = (await GetB(locked, "/api/products/replenishment?windowDays=30&horizonDays=60", branchB)).EnumerateArray().ToList();
        Assert.DoesNotContain(lockedRows, r => r.GetProperty("productId").GetString() == pid);

        // Owner B şubesini seçerse aynı ürün risk listesinde görünür (satış orada oldu).
        var ownerRows = (await GetB(owner, "/api/products/replenishment?windowDays=30&horizonDays=60", branchB)).EnumerateArray().ToList();
        Assert.Contains(ownerRows, r => r.GetProperty("productId").GetString() == pid);
    }

    /// <summary>Tekrarlayan giderler (#8 devamı): şablonun kendi BranchId'si yok — erişim bağlı olduğu KASA
    /// üzerinden sınırlanır. Kilitli kullanıcı başka şubenin kasasına bağlı şablonu ve o kasanın ADINI görmemeli.</summary>
    [Fact]
    public async Task Recurring_expenses_are_scoped_through_their_cash_account_branch()
    {
        var (owner, branchA, branchB, locked) = await SetupAsync();

        // Her şubeye birer kasa + o kasaya bağlı birer tekrarlayan gider şablonu.
        var accA = Id(await PostB(owner, "/api/cash-accounts", new { name = "A Kasa", type = "Cash", openingBalance = 0m }, branchA));
        var accB = Id(await PostB(owner, "/api/cash-accounts", new { name = "B Kasa GIZLI", type = "Cash", openingBalance = 0m }, branchB));
        await PostB(owner, "/api/finance/recurring", new { name = "A Kira", amount = 100m, category = "Kira", cashAccountId = accA, dueDay = 1, description = (string?)null }, branchA);
        await PostB(owner, "/api/finance/recurring", new { name = "B Kira GIZLI", amount = 900m, category = "Kira", cashAccountId = accB, dueDay = 1, description = (string?)null }, branchB);

        // KİLİTLİ admin B başlığını taklit etse de yalnız A'nın şablonunu görür; B kasasının adı da sızmaz.
        var lockedRows = (await GetB(locked, "/api/finance/recurring", branchB)).EnumerateArray().ToList();
        Assert.Contains(lockedRows, r => r.GetProperty("name").GetString() == "A Kira");
        Assert.DoesNotContain(lockedRows, r => r.GetProperty("name").GetString() == "B Kira GIZLI");
        Assert.DoesNotContain(lockedRows, r => r.GetProperty("cashAccountName").GetString() == "B Kasa GIZLI");

        // Regresyon yok: kısıtsız Owner "tüm şubeler"de her ikisini de görür.
        var ownerRows = (await GetB(owner, "/api/finance/recurring", null)).EnumerateArray().ToList();
        Assert.Contains(ownerRows, r => r.GetProperty("name").GetString() == "A Kira");
        Assert.Contains(ownerRows, r => r.GetProperty("name").GetString() == "B Kira GIZLI");
    }

    /// <summary>YEDEK İNDİRME (#8 devamı): JSON yedeği, kullanıcının uygulamada göremediği şubelerin
    /// faturalarını/kasa bakiyelerini/finans hareketlerini İÇERMEMELİ. Kısıtsız Owner'da yedek tam kalır.</summary>
    [Fact]
    public async Task Backup_export_excludes_other_branches_for_locked_user()
    {
        var (owner, branchA, branchB, locked) = await SetupAsync();
        var contact = Id(await PostB(owner, "/api/contacts", new { type = "Customer", name = "Müşteri", openingBalance = 0m, discountRate = 0m }, null));
        var pid = await ProductAsync(owner, "Yedek Ürünü", opening: 50m);
        // Satışlar için B şubesine de stok gerekir (açılış stoğu varsayılan A şubesine yazılır).
        await PostB(owner, "/api/stock/movements", new { productId = pid, type = "In", quantity = 50m, unitCost = (decimal?)null, note = "B-stok" }, branchB);

        // Her şubeye ayırt edilebilir tutarlı birer satış + birer kasa.
        await PostB(owner, "/api/cash-accounts", new { name = "A Kasa", type = "Cash", openingBalance = 111m }, branchA);
        await PostB(owner, "/api/cash-accounts", new { name = "B Kasa GIZLI", type = "Cash", openingBalance = 999m }, branchB);
        await PostB(owner, "/api/invoices", new
        {
            type = "Sales", contactId = contact, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 123m, vatRate = 0m } }, payment = (object?)null,
        }, branchA);
        await PostB(owner, "/api/invoices", new
        {
            type = "Sales", contactId = contact, date = (string?)null, note = (string?)null,
            lines = new[] { new { productId = pid, quantity = 1m, unitPrice = 777m, vatRate = 0m } }, payment = (object?)null,
        }, branchB);

        // KİLİTLİ admin: B başlığını taklit etse de yedekte B'nin cirosu/kasası GÖRÜNMEZ.
        var lockedReq = new HttpRequestMessage(HttpMethod.Get, "/api/backup/export");
        lockedReq.Headers.Add("X-Branch-Id", branchB);
        var lockedRes = await locked.SendAsync(lockedReq);
        Assert.True(lockedRes.IsSuccessStatusCode, $"backup export -> {(int)lockedRes.StatusCode}");
        var lockedJson = await lockedRes.Content.ReadAsStringAsync();
        Assert.Contains("123", lockedJson);                  // kendi şubesinin satışı yedekte
        Assert.DoesNotContain(branchB, lockedJson);          // B ŞUBESİNE ait HİÇBİR kayıt yok (fatura/kasa/finans/stok)
        Assert.DoesNotContain("B Kasa GIZLI", lockedJson);   // diğer şubenin kasası + bakiyesi yok
        Assert.Contains(branchA, lockedJson);                // kendi şubesinin kayıtları duruyor
        // NOT: cari hareketler (AccountTransactions) BİLEREK tenant genelidir — Contact/AccountTransaction
        // şube boyutu taşımaz ve cari ekstre uygulamada da (ContactService.GetLedgerAsync) şube süzmez;
        // bakiye zincir genelinde tektir. Bu yüzden yedekte tutar bazlı arama yapılmaz, ŞUBE KİMLİĞİ aranır.

        // Regresyon yok: kısıtsız Owner'ın yedeği TÜM şubeleri kapsamaya devam eder.
        var ownerJson = await (await owner.GetAsync("/api/backup/export")).Content.ReadAsStringAsync();
        Assert.Contains("123", ownerJson);
        Assert.Contains(branchB, ownerJson);                 // kısıtsız Owner'ın yedeği B şubesini de kapsar
        Assert.Contains("B Kasa GIZLI", ownerJson);
    }

    /// <summary>NAKİT AKIŞI TAHMİNİ (#8 devamı): açılış bakiyesi şubeye süzülüyorsa tekrarlayan giderler de
    /// aynı kapsamdan gelmeli — yoksa tek şubenin kasasından tüm şubelerin gideri düşülüp sahte alarm üretir.</summary>
    [Fact]
    public async Task Cashflow_forecast_scopes_recurring_expenses_like_opening_balance()
    {
        var (owner, branchA, branchB, locked) = await SetupAsync();

        var accA = Id(await PostB(owner, "/api/cash-accounts", new { name = "A Kasa", type = "Cash", openingBalance = 1000m }, branchA));
        var accB = Id(await PostB(owner, "/api/cash-accounts", new { name = "B Kasa", type = "Cash", openingBalance = 1000m }, branchB));
        await PostB(owner, "/api/finance/recurring", new { name = "A Kira", amount = 100m, category = "Kira", cashAccountId = accA, dueDay = 1, description = (string?)null }, branchA);
        await PostB(owner, "/api/finance/recurring", new { name = "B Dev Kira", amount = 50000m, category = "Kira", cashAccountId = accB, dueDay = 1, description = (string?)null }, branchB);

        // KİLİTLİ admin: yalnız A'nın gideri hesaba katılmalı → B'nin 50.000'i toplam çıkışa GİRMEMELİ.
        var lockedFc = await GetB(locked, "/api/cashflow-forecast?days=90", branchB);
        Assert.True(lockedFc.GetProperty("totalOutflow").GetDecimal() < 50000m,
            "Başka şubenin tekrarlayan gideri tahmine karışmış (sahte nakit-krizi alarmı).");

        // Regresyon yok: Owner "tüm şubeler"de her iki gideri de görür.
        var ownerFc = await GetB(owner, "/api/cashflow-forecast?days=90", null);
        Assert.True(ownerFc.GetProperty("totalOutflow").GetDecimal() >= 50000m);
    }

    /// <summary>PANEL + KRİTİK STOK (#8 devamı): panelin stok rakamları ve kritik-stok listesi seçili şubenin
    /// bakiyesine göre olmalı — zincir toplamına bakarsa şubede tükenmiş ürün gözden kaçar.</summary>
    [Fact]
    public async Task Dashboard_and_low_stock_use_selected_branch_balance()
    {
        var (owner, branchA, branchB, locked) = await SetupAsync();

        // minStock=5. A'da 0 adet (kritik), B'de 40 adet (zincir toplamı 40 → tenant-geneli bakışta kritik DEĞİL).
        var pid = Id(await PostB(owner, "/api/products", new
        {
            sku = (string?)null, barcode = (string?)null, name = "Şube Kritik", categoryId = (string?)null, unit = "adet",
            purchasePrice = 10m, salePrice = 100m, vatRate = 0m, openingStock = 0m, minStock = 5m, isService = false,
        }, branchA));
        await PostB(owner, "/api/stock/movements", new { productId = pid, type = "In", quantity = 40m, unitCost = (decimal?)null, note = "B-stok" }, branchB);

        // A'ya kilitli kullanıcı: ürün A'da tükenmiş → kritik listede GÖRÜNMELİ (zincirde 40 olsa bile).
        var lowList = (await GetB(locked, "/api/products/low-stock", branchB)).EnumerateArray().ToList();
        Assert.Contains(lowList, p => p.GetProperty("id").GetString() == pid);

        // Panel stok değeri de A'nın bakiyesinden: A'da 0 adet → bu üründen değer gelmez.
        var summary = await GetB(locked, "/api/dashboard/summary", branchB);
        Assert.True(summary.GetProperty("totalStockValue").GetDecimal() < 400m,
            "Panel stok değeri zincir toplamından hesaplanmış (şube kapsamı değil).");
    }
}
