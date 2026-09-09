using CloudPosGrid.Application.Modules.Assistant;

namespace CloudPosGrid.Tests;

/// <summary>Niyet motoru (kendi NLU çekirdeğimiz) saf birim testleri: sınıflandırma + dönem çıkarımı.
/// Harici API yok — tamamen deterministik. Referans "bugün" = 15 Temmuz 2026 (Çarşamba).</summary>
public class IntentEngineTests
{
    private static readonly DateTime Today = new(2026, 7, 15);

    // ---- Sınıflandırma: doğru niyet ----

    [Theory]
    [InlineData("Bu ay en çok hangi ürünü sattım?", Intents.TopProducts)]
    [InlineData("En çok satan ürünüm ne?", Intents.TopProducts)]
    [InlineData("En fazla satılan ürünler neler?", Intents.TopProducts)]
    [InlineData("Bu ay neden geçen aya göre daha az gelirim oldu?", Intents.PeriodCompare)]
    [InlineData("Neden gelirim düştü?", Intents.PeriodCompare)]
    [InlineData("Geçen aya göre satışlarım arttı mı?", Intents.PeriodCompare)]
    [InlineData("Yarın tahmini cirom ne olur?", Intents.RevenueForecast)]
    [InlineData("Yarın ne kadar satış beklenir?", Intents.RevenueForecast)]
    [InlineData("Bugün nasıl geçti?", Intents.DailySummary)]
    [InlineData("Gün sonu özeti nedir?", Intents.DailySummary)]
    public void Classifies_known_questions(string q, string expected)
    {
        var r = IntentEngine.Classify(q, Today);
        Assert.Equal(expected, r.Intent);
        Assert.True(r.Score >= IntentEngine.Threshold, $"skor {r.Score} eşiğin altında");
    }

    // ---- Aksan/yazım toleransı (Normalize sayesinde) ----

    [Theory]
    [InlineData("en cok satan urunum ne", Intents.TopProducts)]   // aksansız
    [InlineData("BUGÜN NASIL GEÇTİ", Intents.DailySummary)]        // büyük harf + Türkçe İ
    [InlineData("yarinki tahmini cirom", Intents.RevenueForecast)] // aksansız + kısmi
    public void Tolerates_casing_and_missing_diacritics(string q, string expected)
    {
        var r = IntentEngine.Classify(q, Today);
        Assert.Equal(expected, r.Intent);
    }

    // ---- Genişletilmiş niyetler + çakışma düzeltmeleri (adversaryal inceleme bulguları) ----

    [Theory]
    [InlineData("En çok satan kategorim hangisi?", Intents.CategorySales)]  // top_products'a kaymamalı
    [InlineData("Hangi kategori en çok sattı?", Intents.CategorySales)]
    [InlineData("Kategorilere göre ciro nedir?", Intents.CategorySales)]
    [InlineData("stok azaldı", Intents.StockAlert)]                          // period_compare'e kaymamalı
    [InlineData("stok tükendi", Intents.StockAlert)]
    [InlineData("hangi ürünüm tükeniyor", Intents.StockAlert)]
    [InlineData("bugünkü kârım", Intents.TopProfit)]                         // daily_summary'ye kaymamalı
    [InlineData("en çok kâr getiren ürünüm ne", Intents.TopProfit)]
    [InlineData("en cok kar getiren urun", Intents.TopProfit)]              // şapkasız "kar" (â→a normalize)
    [InlineData("kârım ne kadar", Intents.TopProfit)]
    [InlineData("nakit ne kadar", Intents.PaymentBreakdown)]                // ters sıra, eşik-altı kalmamalı
    [InlineData("ne kadar nakit ne kadar kart aldım", Intents.PaymentBreakdown)]
    [InlineData("kim bana borçlu", Intents.Receivables)]
    [InlineData("ne kadar fire verdim", Intents.Waste)]
    public void Classifies_expanded_intents(string q, string expected)
    {
        Assert.Equal(expected, IntentEngine.Classify(q, Today).Intent);
    }

    // ---- Yazım hatası (typo) toleransı — Levenshtein fuzzy eşleşme ----

    [Theory]
    [InlineData("en cok satan urnum ne", Intents.TopProducts)]   // urnum ≈ ürünüm
    [InlineData("kim bana borlcu", Intents.Receivables)]          // borlcu ≈ borçlu
    [InlineData("bugun nasil gecti", Intents.DailySummary)]       // aksansız
    [InlineData("ne kadar fire verdm", Intents.Waste)]            // verdm ≈ verdim
    [InlineData("en cok kar getiren urun", Intents.TopProfit)]
    public void Tolerates_typos(string q, string expected)
    {
        Assert.Equal(expected, IntentEngine.Classify(q, Today).Intent);
    }

    // ---- Konuşma hafızası: takip sorusu çözme (ResolveFollowUp) ----

    [Fact]
    public void FollowUp_product_view_maps_category_to_product_profit()
    {
        var f = IntentEngine.ResolveFollowUp("ürün olarak", Intents.CategorySales, Today);
        Assert.NotNull(f);
        Assert.Equal(Intents.TopProfit, f!.Value.Intent);
    }

    [Fact]
    public void FollowUp_period_override_keeps_intent()
    {
        var f = IntentEngine.ResolveFollowUp("peki geçen ay", Intents.TopProducts, Today);
        Assert.NotNull(f);
        Assert.Equal(Intents.TopProducts, f!.Value.Intent);
        Assert.Equal(PeriodKind.LastMonth, f.Value.Period.Kind);
    }

    [Fact]
    public void FollowUp_why_becomes_period_compare()
    {
        var f = IntentEngine.ResolveFollowUp("neden", Intents.DailySummary, Today);
        Assert.Equal(Intents.PeriodCompare, f!.Value.Intent);
    }

    [Fact]
    public void FollowUp_unrelated_returns_null()
    {
        Assert.Null(IntentEngine.ResolveFollowUp("merhaba nasılsın bugün hava güzel", Intents.TopProducts, Today));
    }

    // ---- Günlük sohbet (chitchat) ----

    [Theory]
    [InlineData("selam", Intents.Chitchat)]
    [InlineData("merhaba", Intents.Chitchat)]
    [InlineData("naber", Intents.Chitchat)]
    [InlineData("nasılsın", Intents.Chitchat)]
    [InlineData("teşekkürler", Intents.Chitchat)]
    [InlineData("görüşürüz", Intents.Chitchat)]
    public void Classifies_chitchat(string q, string expected)
    {
        Assert.Equal(expected, IntentEngine.Classify(q, Today).Intent);
    }

    // ---- "ciro" + "bugünki" + dolgu kelimeler (gerçek kullanıcı sorusu) ----

    [Theory]
    [InlineData("knk naber bugünki ciro mu söylesene", Intents.DailySummary)] // dolgu + bugünki + ciro
    [InlineData("bugünki ciro", Intents.DailySummary)]
    [InlineData("bugünkü ciro ne", Intents.DailySummary)]
    [InlineData("cirom ne kadar", Intents.DailySummary)]
    [InlineData("günün cirosu ne", Intents.DailySummary)]
    [InlineData("geçen ay cirom neydi", Intents.DailySummary)]  // "cirom" ekli — prefix yakalamalı
    [InlineData("cirom neydi", Intents.DailySummary)]
    [InlineData("cironuz ne durumda", Intents.DailySummary)]
    public void Handles_ciro_and_bugunki_with_filler(string q, string expected)
    {
        Assert.Equal(expected, IntentEngine.Classify(q, Today).Intent);
    }

    // "ciro" başka güçlü sinyalle doğru niyete gitmeli (daily'e körü körüne kaymamalı).
    [Theory]
    [InlineData("en çok ciro getiren ürün", Intents.TopProducts)]
    [InlineData("geçen aya göre cirom nasıl", Intents.PeriodCompare)]
    public void Ciro_does_not_override_stronger_intents(string q, string expected)
    {
        Assert.Equal(expected, IntentEngine.Classify(q, Today).Intent);
    }

    [Fact]
    public void Bugunki_extracts_today_period()
    {
        Assert.Equal(PeriodKind.Today, IntentEngine.ExtractPeriod("bugünki ciro", Today).Kind);
    }

    // ---- Proaktif öneri (insights) niyeti ----

    [Theory]
    [InlineData("Bana öneri ver", Intents.Insights)]
    [InlineData("neye dikkat etmeliyim", Intents.Insights)]
    [InlineData("önerilerin neler", Intents.Insights)]
    [InlineData("işletmem için tavsiyen ne", Intents.Insights)]
    public void Classifies_insights_questions(string q, string expected)
    {
        Assert.Equal(expected, IntentEngine.Classify(q, Today).Intent);
    }

    // ---- Bilgi bankası (help) niyeti ----

    [Theory]
    [InlineData("CloudPosGrid nedir", Intents.Help)]
    [InlineData("bu uygulama ne işe yarar", Intents.Help)]
    [InlineData("stok kartı nedir", Intents.Help)]
    [InlineData("cari ne demek", Intents.Help)]
    [InlineData("nasıl fatura keserim", Intents.Help)]
    [InlineData("sen kimsin", Intents.Help)]
    public void Classifies_help_questions(string q, string expected)
    {
        Assert.Equal(expected, IntentEngine.Classify(q, Today).Intent);
    }

    // ---- Anlaşılmayan sorular → null (Faz 3'te öğrenmeye düşecek) ----

    [Theory]
    [InlineData("Hava bugün çok güzel değil mi?")]
    [InlineData("Kediler neden mırlar?")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_for_unknown(string? q)
    {
        var r = IntentEngine.Classify(q, Today);
        Assert.Null(r.Intent);
        Assert.True(r.Score < IntentEngine.Threshold);
    }

    // ---- Öğrenme (Faz 3 önizleme): learned örnek eklenince tanınır ----

    [Fact]
    public void Learns_from_added_examples()
    {
        const string q = "Vitrin performansı dökümü göster";
        // Seed'de hiçbir anchor/örnek yok → anlaşılmaz.
        Assert.Null(IntentEngine.Classify(q, Today).Intent);

        // Admin bu soruyu daily_summary'ye etiketledi → motor artık tanır ("sora sora eğit").
        var learned = new[] { new TrainingExample(Intents.DailySummary, q) };
        var r = IntentEngine.Classify(q, Today, learned);
        Assert.Equal(Intents.DailySummary, r.Intent);
    }

    // ---- Dönem çıkarımı ----

    [Fact]
    public void Extracts_this_month()
    {
        var p = IntentEngine.ExtractPeriod("bu ay ne sattım", Today);
        Assert.Equal(PeriodKind.ThisMonth, p.Kind);
        Assert.Equal(new DateTime(2026, 7, 1), p.From);
        Assert.Equal(new DateTime(2026, 8, 1), p.To);
        Assert.Equal("bu ay", p.Label);
    }

    [Fact]
    public void Extracts_last_month()
    {
        var p = IntentEngine.ExtractPeriod("geçen ay ne oldu", Today);
        Assert.Equal(PeriodKind.LastMonth, p.Kind);
        Assert.Equal(new DateTime(2026, 6, 1), p.From);
        Assert.Equal(new DateTime(2026, 7, 1), p.To);
    }

    [Fact]
    public void Extracts_today_and_yesterday_and_tomorrow()
    {
        var today = IntentEngine.ExtractPeriod("bugün nasıl", Today);
        Assert.Equal(PeriodKind.Today, today.Kind);
        Assert.Equal(new DateTime(2026, 7, 15), today.From);
        Assert.Equal(new DateTime(2026, 7, 16), today.To);

        var yst = IntentEngine.ExtractPeriod("dün ne sattım", Today);
        Assert.Equal(PeriodKind.Yesterday, yst.Kind);
        Assert.Equal(new DateTime(2026, 7, 14), yst.From);
        Assert.Equal(new DateTime(2026, 7, 15), yst.To);

        var tmr = IntentEngine.ExtractPeriod("yarın tahmini ciro", Today);
        Assert.Equal(PeriodKind.Tomorrow, tmr.Kind);
        Assert.Equal(new DateTime(2026, 7, 16), tmr.From);
    }

    [Fact]
    public void Extracts_weeks_starting_monday()
    {
        var tw = IntentEngine.ExtractPeriod("bu hafta ne sattım", Today);
        Assert.Equal(PeriodKind.ThisWeek, tw.Kind);
        Assert.Equal(DayOfWeek.Monday, tw.From.DayOfWeek);
        Assert.True((Today - tw.From).TotalDays is >= 0 and < 7);
        Assert.Equal(7, (tw.To - tw.From).TotalDays);

        var lw = IntentEngine.ExtractPeriod("geçen hafta durum", Today);
        Assert.Equal(PeriodKind.LastWeek, lw.Kind);
        Assert.Equal(tw.From.AddDays(-7), lw.From);
        Assert.Equal(tw.From, lw.To);
    }

    [Fact]
    public void No_period_keyword_yields_none()
    {
        var p = IntentEngine.ExtractPeriod("en çok satan ürün", Today);
        Assert.Equal(PeriodKind.None, p.Kind);
    }

    // Türkçe ekli biçimler (datif/-ki/ablatif) — inceleme bulgusu: tam-token eşleşme bunları kaçırıyordu.
    [Theory]
    [InlineData("geçen haftaya göre satışlarım arttı mı", PeriodKind.LastWeek)]
    [InlineData("bu haftaki durum ne", PeriodKind.ThisWeek)]
    [InlineData("dünkü satışlar nasıldı", PeriodKind.Yesterday)]
    [InlineData("bu ayki cirom ne", PeriodKind.ThisMonth)]
    [InlineData("geçen aya göre nasıl", PeriodKind.LastMonth)]
    [InlineData("bugünkü satış özeti", PeriodKind.Today)]
    public void Extracts_period_from_suffixed_forms(string q, PeriodKind expected)
    {
        Assert.Equal(expected, IntentEngine.ExtractPeriod(q, Today).Kind);
    }

    [Fact]
    public void Classify_also_returns_period()
    {
        var r = IntentEngine.Classify("Bu ay en çok hangi ürünü sattım?", Today);
        Assert.Equal(Intents.TopProducts, r.Intent);
        Assert.Equal(PeriodKind.ThisMonth, r.Period.Kind);
    }
}
