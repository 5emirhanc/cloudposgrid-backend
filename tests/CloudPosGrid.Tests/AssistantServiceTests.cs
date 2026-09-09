using CloudPosGrid.Application.Modules.Assistant;
using CloudPosGrid.Application.Modules.Reports;
using CloudPosGrid.Application.Modules.Stock;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Tests;

/// <summary>Asistan cevap üretimi: niyet motoru + GERÇEK rapor verisi → Türkçe cevap.
/// IReportService fake'lenir → deterministik, dış bağımlılıksız. Karşılaştırma matematiği burada kanıtlanır.</summary>
public class AssistantServiceTests
{
    /// <summary>Testin verdiği satış raporlarını dönem [from,to) başlangıcına göre döndüren sahte rapor servisi.</summary>
    private sealed class FakeReports : IReportService
    {
        // from.Date -> rapor. Eşleşme yoksa boş rapor.
        public Dictionary<DateTime, SalesReportDto> Sales { get; } = new();
        public DailyCloseDto? DailyClose { get; set; }

        public Task<SalesReportDto> GetSalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(Sales.TryGetValue(from.Date, out var r) ? r : Empty());

        public Task<DailyCloseDto> GetDailyCloseAsync(DateTime date, CancellationToken ct = default)
            => Task.FromResult(DailyClose ?? new DailyCloseDto(date, 0, 0, 0, 0, [], 0, 0, 0, [], 0, 0));

        // Ayarlanabilir (verilmezse boş) — yeni konu testleri bunları doldurur.
        public ProfitReportDto Profit { get; set; } = new(0, 0, 0, 0, [], [], []);
        public AgingReportDto Aging { get; set; } = new(default, 0, 0, 0, 0, 0, 0, [], []);
        public WasteReportDto Waste { get; set; } = new(0, 0, [], []);
        public List<ProductProfitDto> ProductProfit { get; set; } = [];

        public Task<FinancialReportDto> GetFinancialAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new FinancialReportDto(0, 0, 0, [], []));
        public Task<ProfitReportDto> GetProfitAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(Profit);
        public Task<List<ProductProfitDto>> GetProductProfitAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(ProductProfit);
        public Task<AgingReportDto> GetAgingAsync(CancellationToken ct = default)
            => Task.FromResult(Aging);
        public Task<WasteReportDto> GetWasteAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(Waste);
        // Analitik hub uçları — asistan testlerinde kullanılmaz, arayüzü karşılamak için boş stub.
        public Task<List<HourlySalesCellDto>> GetHourlySalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new List<HourlySalesCellDto>());
        public Task<List<StaffSalesDto>> GetStaffSalesAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new List<StaffSalesDto>());

        private static SalesReportDto Empty() => new(0, 0, 0, 0, 0, 0, [], [], [], []);
    }

    /// <summary>Sahte sipariş-önerisi servisi (stok uyarısı testleri için).</summary>
    private sealed class FakeReplenishment : IReplenishmentService
    {
        public List<ReplenishmentItemDto> Items { get; set; } = [];
        public Task<List<ReplenishmentItemDto>> GetAsync(int windowDays = 30, int horizonDays = 7, int coverDays = 30, CancellationToken ct = default)
            => Task.FromResult(Items);
    }

    private static SalesReportDto SalesWith(
        int count, decimal total, decimal profit = 0, decimal? subtotal = null,
        List<TopProductReportDto>? top = null, List<CategoryBreakdownDto>? cats = null,
        List<NamedAmountDto>? pay = null, List<DailySalesDto>? trend = null)
        => new(count, total, subtotal ?? total, total - (subtotal ?? total), count > 0 ? total / count : 0, profit,
               pay ?? [], cats ?? [], top ?? [], trend ?? []);

    private static readonly DateTime FirstOfMonth = new(2026, 7, 1);
    private static readonly DateTime Today = new(2026, 7, 15);

    // ---- top_products ----

    [Fact]
    public async Task Top_products_names_the_leader_with_real_numbers()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(
            count: 120, total: 30000,
            top:
            [
                new(Guid.NewGuid(), "Latte", 340, 23800),
                new(Guid.NewGuid(), "Çay", 200, 5000),
            ]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay en çok hangi ürünü sattım?", Today);

        Assert.True(r.Understood);
        Assert.Equal(Intents.TopProducts, r.Intent);
        Assert.Contains("Latte", r.Answer);
        Assert.Contains("340", r.Answer);
        Assert.Contains("23.800", r.Answer); // tr-TR binlik ayracı
    }

    [Fact]
    public async Task Top_products_handles_no_sales()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("Bu ay en çok hangi ürünü sattım?", Today);
        Assert.True(r.Understood);
        Assert.Contains("satış", r.Answer.ToLower());
    }

    // ---- period_compare: matematik ----

    [Fact]
    public async Task Period_compare_computes_drop_percentage()
    {
        var reports = new FakeReports();
        // Bu ay 8.800; geçen ay 10.000 → %12 düşüş.
        reports.Sales[FirstOfMonth] = SalesWith(count: 50, total: 8800,
            cats: [new("Tatlılar", 10, 2000)]);
        reports.Sales[FirstOfMonth.AddMonths(-1)] = SalesWith(count: 60, total: 10000,
            cats: [new("Tatlılar", 20, 6200)]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay neden geçen aya göre daha az gelirim oldu?", Today);

        Assert.True(r.Understood);
        Assert.Equal(Intents.PeriodCompare, r.Intent);
        Assert.Contains("düştü", r.Answer);
        Assert.Contains("%12", r.Answer);
        Assert.Contains("Tatlılar", r.Answer); // en çok değişen kategori (6200→2000)
    }

    [Fact]
    public async Task Period_compare_reports_increase()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 70, total: 12000);
        reports.Sales[FirstOfMonth.AddMonths(-1)] = SalesWith(count: 60, total: 10000);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay geçen aya göre satışlarım nasıl?", Today);
        Assert.Contains("arttı", r.Answer);
        Assert.Contains("%20", r.Answer);
    }

    [Fact]
    public async Task Period_compare_when_previous_empty()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 70, total: 12000);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay neden geçen aya göre farklı?", Today);
        Assert.True(r.Understood);
        Assert.Contains("kıyas", r.Answer.ToLower());
    }

    // ---- daily_summary ----

    [Fact]
    public async Task Daily_summary_reports_today()
    {
        var reports = new FakeReports();
        reports.Sales[new DateTime(2026, 7, 15)] = SalesWith(
            count: 47, total: 6200, profit: 2100,
            pay: [new("Nakit", 4000, 30), new("Kart", 2200, 17)]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bugün nasıl geçti?", Today);

        Assert.True(r.Understood);
        Assert.Equal(Intents.DailySummary, r.Intent);
        Assert.Contains("47", r.Answer);
        Assert.Contains("6.200", r.Answer);
        Assert.Contains("Nakit", r.Answer);
    }

    // ---- revenue_forecast ----

    [Fact]
    public async Task Forecast_uses_history_average()
    {
        var reports = new FakeReports();
        // 30 günlük trend: hepsi 1.000 → ortalama 1.000, tahmin ~1.000.
        // Tahmin NET ciroyu (KDV hariç) kullanır → 4. alan (NetTotal) doldurulmalı; boş kalırsa
        // asistan haklı olarak "yeterli veri yok" der.
        var trend = Enumerable.Range(0, 30)
            .Select(i => new DailySalesDto(new DateTime(2026, 6, 16).AddDays(i), 1000, 5, 1000))
            .ToList();
        reports.Sales[new DateTime(2026, 6, 16)] = SalesWith(count: 150, total: 30000, trend: trend);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Yarın tahmini cirom ne olur?", Today);

        Assert.True(r.Understood);
        Assert.Equal(Intents.RevenueForecast, r.Intent);
        Assert.Contains("1.000", r.Answer);
        Assert.Contains("tahmin", r.Answer.ToLower());
    }

    [Fact]
    public async Task Forecast_without_data()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("Yarın tahmini cirom ne olur?", Today);
        Assert.True(r.Understood);
        Assert.Contains("yeterli", r.Answer.ToLower());
    }

    // ---- anlaşılmayan ----

    [Fact]
    public async Task Unknown_question_is_not_understood()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("Bugün hava nasıl olacak?", Today);
        Assert.False(r.Understood);
        Assert.Null(r.Intent);
    }

    // ---- İnceleme düzeltmeleri: KDV taban tutarlılığı, dönem-farkında özet, eşit gelir ----

    [Fact]
    public async Task Top_products_uses_net_revenue_consistently()
    {
        // KDV'li: net (subtotal) 10.000, gross (total) 12.000. Asistan hep NET kullanmalı → toplam 10.000, 12.000 GÖRÜNMEMELİ.
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 20, total: 12000, subtotal: 10000,
            top: [new(Guid.NewGuid(), "Ürün", 50, 10000)]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay en çok hangi ürünü sattım?", Today);
        Assert.Contains("10.000", r.Answer);
        Assert.DoesNotContain("12.000", r.Answer); // gross sızmamalı
    }

    [Fact]
    public async Task Daily_summary_honors_month_period_not_just_today()
    {
        // "Bu ay nasıl geçti?" daily_summary'ye düşer ama ThisMonth dönemi taşır → ay verisi, "bugün" DEĞİL.
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 200, total: 50000);        // ay geneli
        reports.Sales[Today] = SalesWith(count: 3, total: 600);                   // bugünün küçük verisi
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay nasıl geçti?", Today);
        Assert.Equal(Intents.DailySummary, r.Intent);
        Assert.Contains("Bu ay", r.Answer);   // dönem etiketi doğru
        Assert.Contains("200", r.Answer);      // ay verisi
        Assert.DoesNotContain("Bugün", r.Answer);
    }

    [Fact]
    public async Task Period_compare_equal_revenue_says_unchanged()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 10, total: 5000);
        reports.Sales[FirstOfMonth.AddMonths(-1)] = SalesWith(count: 10, total: 5000);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay geçen aya göre nasıl?", Today);
        Assert.Contains("aynı kaldı", r.Answer);
        Assert.DoesNotContain("arttı", r.Answer);
    }

    // ---- Genişletilmiş konular ----

    [Fact]
    public async Task Top_profit_reports_gross_profit_and_margin()
    {
        var reports = new FakeReports
        {
            Profit = new ProfitReportDto(
                TotalRevenue: 50000, TotalCost: 42000, GrossProfit: 8000, GrossMarginPercent: 16,
                ByChannel: [], ByCategory: [new("Kahve", 20000, 12000, 8000, 40)], Trend: []),
        };
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay en çok kâr getiren ürünüm ne?", Today);
        Assert.True(r.Understood);
        Assert.Equal(Intents.TopProfit, r.Intent);
        Assert.Contains("8.000", r.Answer);       // brüt kâr
        Assert.Contains("Kahve", r.Answer);       // en kârlı kategori
        Assert.Contains("%20'nin altında", r.Answer); // düşük marj danışman uyarısı (16 < 20)
    }

    [Fact]
    public async Task Receivables_reports_total_and_top_debtor()
    {
        var reports = new FakeReports
        {
            Aging = new AgingReportDto(
                AsOf: default, TotalReceivable: 15000, TotalPayable: 0,
                Current: 5000, D31_60: 4000, D61_90: 2000, Over90: 4000,
                Receivables:
                [
                    new(Guid.NewGuid(), "Ahmet Ticaret", "Customer", null, 9000, 0, 0, 0, 4000, 120),
                    new(Guid.NewGuid(), "Mehmet Bakkal", "Customer", null, 6000, 0, 0, 0, 0, 20),
                ],
                Payables: []),
        };
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Kim bana ne kadar borçlu?", Today);
        Assert.Equal(Intents.Receivables, r.Intent);
        Assert.Contains("15.000", r.Answer);          // toplam alacak
        Assert.Contains("Ahmet Ticaret", r.Answer);   // en borçlu
        Assert.Contains("90 günü aşmış", r.Answer);   // gecikme uyarısı (Over90>0)
    }

    [Fact]
    public async Task Waste_reports_total_and_top_item()
    {
        var reports = new FakeReports
        {
            Waste = new WasteReportDto(TotalCost: 1200, TotalQuantity: 15,
                Items: [new(Guid.NewGuid(), "Süt", 10, 800), new(Guid.NewGuid(), "Ekmek", 5, 400)],
                ByReason: []),
        };
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay ne kadar fire verdim?", Today);
        Assert.Equal(Intents.Waste, r.Intent);
        Assert.Contains("1.200", r.Answer);
        Assert.Contains("Süt", r.Answer);
    }

    [Fact]
    public async Task Payment_breakdown_lists_methods()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 100, total: 10000,
            pay: [new("Nakit", 6000, 60), new("Kart", 4000, 40)]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Ne kadar nakit ne kadar kart aldım?", Today);
        Assert.Equal(Intents.PaymentBreakdown, r.Intent);
        Assert.Contains("Nakit", r.Answer);
        Assert.Contains("Kart", r.Answer);
        Assert.Contains("6.000", r.Answer);
    }

    [Fact]
    public async Task Category_sales_reports_top_category()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 100, total: 20000,
            cats: [new("İçecek", 100, 12000), new("Tatlı", 50, 8000)]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Hangi kategori en çok sattı?", Today);
        Assert.Equal(Intents.CategorySales, r.Intent);
        Assert.Contains("İçecek", r.Answer);
        Assert.Contains("12.000", r.Answer);
    }

    [Fact]
    public async Task Stock_alert_lists_running_out_products()
    {
        var repl = new FakeReplenishment
        {
            Items =
            [
                new(Guid.NewGuid(), "Türk Kahvesi", "İçecek", "paket", 5, 10, 1.5m, 3, 40, 60, 30, 20, 0),
                new(Guid.NewGuid(), "Şeker", "Bakliyat", "kg", 8, 5, 0.5m, 12, 20, 30, 15, 20, 0),
            ],
        };
        var svc = new AssistantService(new FakeReports(), replenishment: repl, db: null!, user: null!);

        var r = await svc.GenerateAsync("Hangi ürünüm tükeniyor?", Today);
        Assert.Equal(Intents.StockAlert, r.Intent);
        Assert.Contains("Türk Kahvesi", r.Answer);   // en acil (3 gün)
        Assert.Contains("3 gün", r.Answer);
    }

    [Fact]
    public async Task Stock_alert_when_none()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("Stok uyarısı var mı?", Today);
        Assert.Equal(Intents.StockAlert, r.Intent);
        Assert.Contains("risk", r.Answer.ToLower());
    }

    [Fact]
    public async Task What_if_is_honest_no_fabrication()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("Karışık tost eklesem ne kadar satılır?", Today);
        Assert.Equal(Intents.WhatIf, r.Intent);
        Assert.Contains("tahmin yapamam", r.Answer.ToLower());
    }

    // ---- Bilgi bankası + ürün bazlı kâr ----

    [Fact]
    public async Task Help_answers_from_knowledge_base()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("Stok kartı nedir?", Today);
        Assert.True(r.Understood);
        Assert.Equal(Intents.Help, r.Intent);
        Assert.Contains("alış fiyatı", r.Answer);   // stok kartı makalesinden
    }

    [Fact]
    public async Task Help_overview_for_generic_question()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("CloudPosGrid ne işe yarar?", Today);
        Assert.Equal(Intents.Help, r.Intent);
        Assert.Contains("POS", r.Answer);
    }

    [Fact]
    public async Task Top_profit_lists_most_profitable_products()
    {
        var reports = new FakeReports
        {
            Profit = new ProfitReportDto(50000, 30000, 20000, 40, [], [], []),
            ProductProfit =
            [
                new(Guid.NewGuid(), "Latte", 300, 15000, 6000, 9000, 60),
                new(Guid.NewGuid(), "Cheesecake", 100, 8000, 3000, 5000, 62),
            ],
        };
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("En çok kâr getiren ürünüm ne?", Today);
        Assert.Equal(Intents.TopProfit, r.Intent);
        Assert.Contains("Latte", r.Answer);           // ürün bazlı
        Assert.Contains("En kârlı", r.Answer);
        Assert.Contains("20.000", r.Answer);          // toplam brüt kâr
    }

    // ---- Günlük sohbet + dönem şeffaflığı ----

    [Fact]
    public async Task Chitchat_greets_warmly()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("selam", Today);
        Assert.True(r.Understood);
        Assert.Equal(Intents.Chitchat, r.Intent);
        Assert.Contains("Merhaba", r.Answer);
    }

    [Fact]
    public async Task Chitchat_thanks()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("çok teşekkürler", Today);
        Assert.Equal(Intents.Chitchat, r.Intent);
        Assert.Contains("Rica ederim", r.Answer);
    }

    [Fact]
    public async Task Period_hint_added_when_no_period_given()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 20, total: 10000,
            top: [new(Guid.NewGuid(), "Ürün", 50, 10000)]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        // Dönem verilmedi → varsayım şeffaf bildirilmeli.
        var r = await svc.GenerateAsync("En çok hangi ürünü sattım?", Today);
        Assert.Contains("bu ayı", r.Answer.ToLower());
    }

    [Fact]
    public async Task No_period_hint_when_period_given()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 20, total: 10000,
            top: [new(Guid.NewGuid(), "Ürün", 50, 10000)]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        // Dönem açıkça verildi → ipucu EKLENMEMELİ.
        var r = await svc.GenerateAsync("Bu ay en çok hangi ürünü sattım?", Today);
        Assert.DoesNotContain("baz aldım", r.Answer);
    }

    // ---- Proaktif içgörüler (InsightService) ----

    [Fact]
    public async Task Insights_lists_proactive_warnings()
    {
        var reports = new FakeReports
        {
            Aging = new AgingReportDto(default, 5000, 0, 0, 0, 0, 5000,
                [new(Guid.NewGuid(), "Ahmet Ticaret", "Customer", null, 5000, 0, 0, 0, 5000, 120)], []),
        };
        var repl = new FakeReplenishment
        {
            Items = [new(Guid.NewGuid(), "Türk Kahvesi", "İçecek", "paket", 3, 10, 1.5m, 3, 40, 60, 30, 20, 0)],
        };
        var svc = new AssistantService(reports, replenishment: repl, db: null!, user: null!);

        var r = await svc.GenerateAsync("Bana öneri ver, neye dikkat etmeliyim?", Today);
        Assert.True(r.Understood);
        Assert.Equal(Intents.Insights, r.Intent);
        Assert.Contains("Türk Kahvesi", r.Answer);   // düşük stok içgörüsü
        Assert.Contains("90 günü aşmış", r.Answer);  // geciken alacak içgörüsü
    }

    [Fact]
    public async Task Insights_says_all_good_when_nothing_wrong()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("Bana öneri ver", Today);
        Assert.Equal(Intents.Insights, r.Intent);
        Assert.Contains("yolunda", r.Answer.ToLower());
    }

    // ---- Konuşma hafızası (diyalog motoru) ----

    [Fact]
    public async Task Followup_product_after_category_uses_context()
    {
        var reports = new FakeReports
        {
            Profit = new ProfitReportDto(50000, 30000, 20000, 40, [], [], []),
            ProductProfit = [new(Guid.NewGuid(), "Latte", 300, 15000, 6000, 9000, 60)],
        };
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        // Önceki konu kategori satışıydı; "ürün olarak" → ürün bazlı kâra iner.
        var ctx = new AssistantContext("category_sales", "ThisMonth");
        var r = await svc.GenerateAsync("ürün olarak", Today, BusinessType.General, null, ctx);
        Assert.True(r.Understood);
        Assert.Equal(Intents.TopProfit, r.Intent);
        Assert.Contains("Latte", r.Answer);
    }

    [Fact]
    public async Task Followup_without_context_stays_unknown()
    {
        var svc = new AssistantService(new FakeReports(), replenishment: new FakeReplenishment(), db: null!, user: null!);
        var r = await svc.GenerateAsync("ürün olarak", Today); // bağlam yok → anlaşılmaz
        Assert.False(r.Understood);
    }

    [Fact]
    public async Task Reply_carries_context_for_next_turn()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 10, total: 5000,
            top: [new(Guid.NewGuid(), "Çay", 5, 5000)]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        var r = await svc.GenerateAsync("Bu ay en çok ne sattım?", Today);
        Assert.NotNull(r.Context);
        Assert.Equal(Intents.TopProducts, r.Context!.LastIntent);
    }

    // ---- Sektörelleştirme ----

    [Fact]
    public async Task Uses_sector_terminology_for_beauty()
    {
        var reports = new FakeReports();
        reports.Sales[FirstOfMonth] = SalesWith(count: 30, total: 9000,
            top: [new(Guid.NewGuid(), "Saç Kesimi", 60, 9000)]);
        var svc = new AssistantService(reports, replenishment: new FakeReplenishment(), db: null!, user: null!);

        // Beauty sektörü → "ürün" değil "hizmet" demeli.
        var r = await svc.GenerateAsync("Bu ay en çok ne sattım?", Today, BusinessType.Beauty);
        Assert.Equal(Intents.TopProducts, r.Intent);
        Assert.Contains("hizmet", r.Answer);
        Assert.DoesNotContain("ürün", r.Answer);
    }
}
