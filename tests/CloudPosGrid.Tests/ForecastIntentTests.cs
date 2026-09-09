using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudPosGrid.Application.Modules.Assistant;
using CloudPosGrid.Application.Modules.Reports;
using CloudPosGrid.Application.Modules.Stock;

namespace CloudPosGrid.Tests;

/// <summary>Tahmin niyetinin DÖNEM FARKINDALIĞI (yol haritası #65). Eskiden tek bir revenue_forecast vardı
/// ve dönemi yok sayıp hep "yarın"ı cevaplıyordu; "bu hafta sonu" / "bu ay sonunda" artık kendi ufkuyla
/// cevaplanır. Referans "bugün" = 15 Temmuz 2026 (Çarşamba) — o haftanın Cumartesi'si 18 Temmuz.</summary>
public class ForecastIntentTests
{
    private static readonly DateTime Today = new(2026, 7, 15);

    // ---- Niyet motoru (saf birim): doğru niyet + doğru ufuk ----

    [Fact]
    public void Weekend_question_is_forecast_with_weekend_period()
    {
        var r = IntentEngine.Classify("Bu hafta sonu ne kadar satarım?", Today);
        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Equal(PeriodKind.Weekend, r.Period.Kind);
        // Ufuk = içinde bulunulan haftanın Cumartesi+Pazar'ı (2 gün).
        Assert.Equal(DayOfWeek.Saturday, r.Period.From.DayOfWeek);
        Assert.Equal(new DateTime(2026, 7, 18), r.Period.From);
        Assert.Equal(new DateTime(2026, 7, 20), r.Period.To);
    }

    [Fact]
    public void Month_end_question_is_forecast_with_month_period()
    {
        var r = IntentEngine.Classify("Bu ay sonunda cirom ne olur?", Today);
        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Equal(PeriodKind.ThisMonth, r.Period.Kind);
        Assert.Equal(new DateTime(2026, 7, 1), r.Period.From);
        Assert.Equal(new DateTime(2026, 8, 1), r.Period.To);
    }

    [Fact]
    public void Tomorrow_question_still_uses_tomorrow_period()
    {
        var r = IntentEngine.Classify("Yarın ne kadar satarım?", Today);
        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Equal(PeriodKind.Tomorrow, r.Period.Kind);
        Assert.Equal(new DateTime(2026, 7, 16), r.Period.From);
    }

    // "bu hafta sonu" içinde " bu hafta " alt-dizgesi geçtiği için sıra kritik: hafta sonu ÖNCE denenmeli,
    // ama "bu hafta"/"geçen hafta sonu" eski davranışını korumalı.
    [Theory]
    [InlineData("bu hafta sonu ne kadar satarım", PeriodKind.Weekend)]
    [InlineData("haftasonu ne kadar satarım", PeriodKind.Weekend)]
    [InlineData("bu hafta sonunda toplam ne kadar satarım", PeriodKind.Weekend)]
    [InlineData("bu hafta ne sattım", PeriodKind.ThisWeek)]        // "hafta sonu" YOK → eski dal
    [InlineData("bu haftaki durum ne", PeriodKind.ThisWeek)]
    [InlineData("geçen hafta sonu ne sattım", PeriodKind.LastWeek)] // geçmiş sorusu geleceğe kaymamalı
    public void Weekend_pattern_does_not_swallow_week_periods(string q, PeriodKind expected)
    {
        Assert.Equal(expected, IntentEngine.ExtractPeriod(q, Today).Kind);
    }

    // ---- Cevap üretimi: her ufuk kendi rakamını versin (GERÇEK trend verisinden) ----

    [Fact]
    public async Task Weekend_forecast_projects_saturday_and_sunday_separately()
    {
        // 30 günlük trend: Cumartesi 2.000, Pazar 3.000, diğer günler 1.000 → hafta sonu beklentisi 5.000.
        var reports = new FakeReports();
        reports.Sales[Today.AddDays(-29)] = SalesWith(trend: Trend(Today.AddDays(-29), 30, d => d.DayOfWeek switch
        {
            DayOfWeek.Saturday => 2000m,
            DayOfWeek.Sunday => 3000m,
            _ => 1000m,
        }));
        var svc = new AssistantService(reports, new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu hafta sonu ne kadar satarım?", Today);

        Assert.True(r.Understood);
        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Contains("hafta sonu", r.Answer);      // doğru ufuk
        Assert.Contains("18 Temmuz", r.Answer);        // o haftanın Cumartesi'si
        Assert.Contains("5.000", r.Answer);            // Cumartesi 2.000 + Pazar 3.000
        Assert.Contains("2.000", r.Answer);
        Assert.Contains("3.000", r.Answer);
        Assert.Contains("tahmindir", r.Answer);        // dürüstlük: kesin değil
        Assert.DoesNotContain("Yarın", r.Answer);      // ESKİ HATA: tek günlük yarın cevabı
    }

    [Fact]
    public async Task Month_forecast_adds_elapsed_actual_to_remaining_days()
    {
        // Günde sabit 1.000 → 1-14 Temmuz gerçekleşen 14.000; BUGÜN DAHİL kalan 17 gün × 1.000 = 17.000 →
        // ay sonu 31.000. (Bugün "gerçekleşen" sayılsaydı günün yaşanmamış kısmı toplamdan düşerdi.)
        var reports = new FakeReports();
        reports.Sales[Today.AddDays(-29)] = SalesWith(trend: Trend(Today.AddDays(-29), 30, _ => 1000m));
        var svc = new AssistantService(reports, new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay sonunda cirom ne olur?", Today);

        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Contains("Bu ay", r.Answer);
        Assert.Contains("14.000", r.Answer);   // düne kadar gerçekleşen
        Assert.Contains("17 gün", r.Answer);   // bugün dahil kalan gün sayısı
        Assert.Contains("31.000", r.Answer);   // dönem sonu projeksiyonu
        Assert.Contains("KDV hariç", r.Answer); // asistanın ciro sözleşmesi net
        Assert.DoesNotContain("Yarın", r.Answer);
    }

    [Fact]
    public async Task Week_forecast_reports_elapsed_plus_remaining()
    {
        // Çarşamba: 13-14 Temmuz gerçekleşen 2.000; bugün dahil kalan 5 gün × 1.000 = 5.000 → hafta bitiminde 7.000.
        var reports = new FakeReports();
        reports.Sales[Today.AddDays(-29)] = SalesWith(trend: Trend(Today.AddDays(-29), 30, _ => 1000m));
        var svc = new AssistantService(reports, new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu hafta tahmini cirom ne olur?", Today);

        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Contains("Bu hafta", r.Answer);
        Assert.Contains("2.000", r.Answer);   // düne kadar (Pzt+Sal)
        Assert.Contains("5 gün", r.Answer);   // bugün dahil kalan
        Assert.Contains("7.000", r.Answer);
    }

    // ---- Adversaryal inceleme regresyonları (bulgular: geçmiş dönem tahmini, KDV tabanı, geçmiş hafta sonu) ----

    /// <summary>GEÇMİŞ dönem sorusu tahmine düşerse bile yarını cevaplamamalı. "geçen hafta sonu ne kadar
    /// sattım" tahmin çapalarına takılabiliyordu ve kullanıcıya alakasız bir YARIN projeksiyonu dönüyordu.</summary>
    [Theory]
    [InlineData("geçen hafta sonu ne kadar sattım?")]
    [InlineData("geçen ay sonunda ne sattım?")]
    public async Task Past_period_question_never_answers_with_a_forecast(string question)
    {
        var reports = new FakeReports();
        reports.Sales[Today.AddDays(-29)] = SalesWith(trend: Trend(Today.AddDays(-29), 30, _ => 1000m));
        // Geçmiş dönem sorgusu kendi aralığıyla ayrıca çekilir; hangi aralık gelirse gelsin 0'a düşmesin diye
        // FakeReports bilinmeyen aralıkta boş rapor döndürür — cevabın "yarın tahmini" OLMAMASI esas iddiadır.
        var svc = new AssistantService(reports, new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync(question, Today);

        Assert.DoesNotContain("Yarın", r.Answer);
        Assert.DoesNotContain("beklenti", r.Answer);
        if (r.Intent == Intents.RevenueForecast)
            Assert.Contains("geçmişte kaldı", r.Answer); // tahmin yerine gerçekleşen rakama yönlendirir
    }

    /// <summary>Tahmin, asistanın geri kalanıyla AYNI ciro tabanını (KDV hariç net) kullanmalı. Brüt
    /// GrandTotal kullanılsaydı aynı dönem için iki farklı "ciro" rakamı söylenirdi.</summary>
    [Fact]
    public async Task Forecast_uses_net_vat_excluded_revenue_not_gross()
    {
        // Brüt 1.200 (KDV dahil) / net 1.000 — tahmin NET rakamı konuşmalı.
        var reports = new FakeReports();
        var trend = Enumerable.Range(0, 30).Select(i => Today.AddDays(-29 + i))
            .Select(d => new DailySalesDto(d, 1200m, 1, 1000m)).ToList();
        reports.Sales[Today.AddDays(-29)] = SalesWith(trend: trend);
        var svc = new AssistantService(reports, new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Yarın tahmini cirom ne olur?", Today);

        Assert.Contains("1.000", r.Answer);        // net
        Assert.DoesNotContain("1.200", r.Answer);  // brüt sızmamalı
        Assert.Contains("KDV hariç", r.Answer);
    }

    /// <summary>Hafta sonu sorusu PAZAR günü sorulursa, olmuş bitmiş Cumartesi'yi "tahmin" diye sunmamalı;
    /// gerçekleşen rakamı söylemeli.</summary>
    [Fact]
    public async Task Weekend_forecast_reports_elapsed_saturday_as_realized()
    {
        var sunday = new DateTime(2026, 7, 19);   // o haftanın Pazar'ı (Cumartesi 18'i geçti)
        var reports = new FakeReports();
        reports.Sales[sunday.AddDays(-29)] = SalesWith(trend: Trend(sunday.AddDays(-29), 30, d =>
            d == new DateTime(2026, 7, 18) ? 7777m : d.DayOfWeek == DayOfWeek.Saturday ? 2000m : 1000m));
        var svc = new AssistantService(reports, new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu hafta sonu ne kadar satarım?", sunday);

        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Contains("gerçekleşen", r.Answer);  // geçmiş Cumartesi tahmin değil
        Assert.Contains("7.777", r.Answer);        // o günün GERÇEK rakamı (ortalama 2.000 değil)
    }

    [Fact]
    public async Task Month_forecast_on_last_day_counts_full_month_and_projects_nothing()
    {
        // Ayın 31'inde ay başı 30 günlük pencerenin DIŞINDA kalır → pencere geriye genişletilmeli,
        // yoksa 1 Temmuz toplamdan düşerdi (31.000 değil 30.000 çıkardı).
        var lastDay = new DateTime(2026, 7, 31);
        var reports = new FakeReports();
        reports.Sales[new DateTime(2026, 7, 1)] = SalesWith(trend: Trend(new DateTime(2026, 7, 1), 31, _ => 1000m));
        var svc = new AssistantService(reports, new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay sonunda cirom ne olur?", lastDay);

        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Contains("31.000", r.Answer);       // 31 gün × 1.000, ilk gün dahil
        Assert.Contains("son günündesin", r.Answer);
    }

    [Fact]
    public async Task Tomorrow_forecast_path_is_unchanged()
    {
        var reports = new FakeReports();
        reports.Sales[Today.AddDays(-29)] = SalesWith(trend: Trend(Today.AddDays(-29), 30, _ => 1000m));
        var svc = new AssistantService(reports, new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Yarın tahmini cirom ne olur?", Today);

        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Contains("Yarın (Perşembe)", r.Answer);
        Assert.Contains("1.000", r.Answer);
    }

    [Fact]
    public async Task Weekend_forecast_without_history_is_honest()
    {
        var svc = new AssistantService(new FakeReports(), new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("Bu hafta sonu ne kadar satarım?", Today);
        Assert.True(r.Understood);
        Assert.Contains("yeterli", r.Answer.ToLower());   // veri yoksa rakam UYDURMAZ
    }

    // ---- Yardımcılar (sahte rapor servisi) ----

    /// <summary><paramref name="days"/> günlük ardışık trend; her günün tutarı <paramref name="amount"/> ile belirlenir.</summary>
    // Tahmin NET ciroyu (KDV hariç) kullanır — testte brüt/net aynı tutulur ki beklenen rakamlar okunur kalsın.
    private static List<DailySalesDto> Trend(DateTime from, int days, Func<DateTime, decimal> amount)
        => [.. Enumerable.Range(0, days).Select(i => from.AddDays(i)).Select(d => new DailySalesDto(d, amount(d), 1, amount(d)))];

    private static SalesReportDto SalesWith(List<DailySalesDto>? trend = null)
    {
        var total = trend?.Sum(d => d.Total) ?? 0;
        return new(trend?.Count ?? 0, total, total, 0, 0, 0, [], [], [], trend ?? []);
    }

    /// <summary>Yalnız satış raporu (trend) döndüren sahte rapor servisi; diğer uçlar boş stub.</summary>
    private sealed class FakeReports : IReportService
    {
        public Dictionary<DateTime, SalesReportDto> Sales { get; } = new();

        public Task<SalesReportDto> GetSalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(Sales.TryGetValue(from.Date, out var r) ? r : new SalesReportDto(0, 0, 0, 0, 0, 0, [], [], [], []));

        public Task<DailyCloseDto> GetDailyCloseAsync(DateTime date, CancellationToken ct = default)
            => Task.FromResult(new DailyCloseDto(date, 0, 0, 0, 0, [], 0, 0, 0, [], 0, 0));
        public Task<FinancialReportDto> GetFinancialAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new FinancialReportDto(0, 0, 0, [], []));
        public Task<ProfitReportDto> GetProfitAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new ProfitReportDto(0, 0, 0, 0, [], [], []));
        public Task<List<ProductProfitDto>> GetProductProfitAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new List<ProductProfitDto>());
        public Task<AgingReportDto> GetAgingAsync(CancellationToken ct = default)
            => Task.FromResult(new AgingReportDto(default, 0, 0, 0, 0, 0, 0, [], []));
        public Task<WasteReportDto> GetWasteAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new WasteReportDto(0, 0, [], []));
        public Task<List<HourlySalesCellDto>> GetHourlySalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new List<HourlySalesCellDto>());
        public Task<List<StaffSalesDto>> GetStaffSalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new List<StaffSalesDto>());
    }

    private sealed class FakeReplenishment : IReplenishmentService
    {
        public Task<List<ReplenishmentItemDto>> GetAsync(int windowDays = 30, int horizonDays = 7, int coverDays = 30, CancellationToken ct = default)
            => Task.FromResult(new List<ReplenishmentItemDto>());
    }
}

/// <summary>Uçtan uca (gerçek HTTP + Postgres): dönem ufuklu tahmin sorusu asistan hattından geçince
/// revenue_forecast olarak sınıflanıyor mu? Asistan Zincir-kilitli → ActivateChainAsync şart.</summary>
[Collection("api")]
public class ForecastIntentApiTests
{
    private readonly ApiFixture _fx;
    public ForecastIntentApiTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"fc{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    [Fact]
    public async Task Weekend_and_month_questions_reach_forecast_intent()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var client = _fx.Factory.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = "Tahmin İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        var regBody = await reg.Content.ReadAsStringAsync();
        Assert.True(reg.IsSuccessStatusCode, regBody);
        var res = JsonDocument.Parse(regBody).RootElement;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        await _fx.ActivateChainAsync(Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!));

        foreach (var q in new[] { "Bu hafta sonu ne kadar satarım?", "Bu ay sonunda cirom ne olur?" })
        {
            var resp = await client.PostAsJsonAsync("/api/assistant/ask", new { question = q });
            var txt = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"{q} -> {(int)resp.StatusCode}\n{txt}");
            var body = JsonDocument.Parse(txt).RootElement;
            Assert.True(body.GetProperty("understood").GetBoolean(), $"anlaşılmadı: {q}");
            Assert.Equal("revenue_forecast", body.GetProperty("intent").GetString());
        }
    }
}
