using System.Globalization;
using System.Text;

namespace CloudPosGrid.Application.Modules.Assistant;

/// <summary>Desteklenen niyet kodları (asistanın "anlayabildiği" soru türleri).</summary>
public static class Intents
{
    public const string TopProducts = "top_products";       // "en çok ne sattım"
    public const string PeriodCompare = "period_compare";   // "neden geçen aya göre az/çok"
    public const string RevenueForecast = "revenue_forecast"; // "yarın tahmini ciro"
    public const string DailySummary = "daily_summary";     // "bugün nasıl geçti"
    // --- Genişletilmiş konular ---
    public const string TopProfit = "top_profit";           // "en çok kâr getiren"
    public const string Receivables = "receivables";        // "kim bana borçlu"
    public const string Waste = "waste";                    // "ne kadar fire verdim"
    public const string PaymentBreakdown = "payment_breakdown"; // "ne kadar nakit/kart"
    public const string CategorySales = "category_sales";   // "hangi kategori en çok"
    public const string StockAlert = "stock_alert";         // "hangi ürünüm tükeniyor"
    public const string WhatIf = "what_if";                 // "X eklesem ne satar" (hipotetik)
    public const string Help = "help";                      // "CloudPosGrid/stok kartı nedir" (bilgi bankası)
    public const string Insights = "insights";              // "bana öneri ver / neye dikkat etmeliyim"
    public const string Chitchat = "chitchat";              // "selam / naber / teşekkürler" (günlük sohbet)
}

/// <summary>Eğitim örneği: bir örnek soru + ait olduğu niyet. Seed'de gömülüdür; Faz 3'te DB'den
/// öğrenilenler EKLENİR (motor değişmeden akıllanır — "sora sora eğit").</summary>
public record TrainingExample(string Intent, string Text);

/// <summary>Güçlü ipucu ifadesi: bu ifade soruda geçiyorsa ilgili niyet ağırlıklı puan alır.
/// Örneklerden farkı: küratörlü, yüksek ağırlıklı ve çok-kelimeli kalıpları (substring) yakalar.</summary>
public record IntentAnchor(string Intent, string Phrase, double Weight = 3.0, bool Prefix = false);

/// <summary>Sorudan çıkarılan zaman dönemi türü. Weekend = içinde bulunulan haftanın Cumartesi+Pazar'ı;
/// "bu hafta"dan AYRI bir ufuktur (tahmin niyeti iki günü ayrı ayrı projekte eder).</summary>
public enum PeriodKind { None, Today, Yesterday, ThisWeek, LastWeek, ThisMonth, LastMonth, ThisYear, Last7, Last30, Tomorrow, Weekend }

/// <summary>Çözümlenen dönem: yarı-açık [From, To) aralık + Türkçe etiket (cevapta kullanılır).
/// Tomorrow geleceğe aittir (veri yok) → From/To o günü işaret eder ama tahmin için kullanılır.</summary>
public record PeriodResult(PeriodKind Kind, DateTime From, DateTime To, string Label);

/// <summary>Sınıflandırma sonucu: niyet (yoksa null) + güven skoru + çözümlenen dönem.</summary>
public record IntentResult(string? Intent, double Score, PeriodResult Period);

/// <summary>
/// CloudPosGrid'in KENDİ niyet motoru (NLU çekirdeği). Harici LLM/API YOKTUR.
/// Soruyu normalize eder, gömülü + öğrenilmiş eğitim setiyle skorlar, eşiği geçen en yüksek
/// niyeti döndürür (yoksa "anlamadım"). Saf/statik → tam birim testli.
/// </summary>
public static class IntentEngine
{
    /// <summary>Eşiği geçmek için gereken minimum skor. Tek bir anchor eşleşmesi (≈3) ya da
    /// yüksek örnek örtüşmesi (jaccard·4) bunu geçer.</summary>
    public const double Threshold = 3.0;

    // Çok kısa/anlamsız token'lar örtüşmede gürültü yapmasın diye elenen Türkçe durak kelimeleri.
    // DİKKAT: niyet ayırt eden kelimeler (neden, en, çok, bugün, yarın…) BURADA OLMAMALI.
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "bir", "ve", "ile", "için", "mi", "mu", "mı", "acaba", "ki", "de", "da",
        "benim", "ben", "bizim", "biz", "the", "my", "kadar", "gibi",
    };

    // Güçlü, küratörlü ipuçları. Substring (kelime-sınırlı) eşleşir; çok-kelimeli kalıpları yakalar.
    private static readonly IntentAnchor[] Anchors =
    [
        // top_products — hangi ürün/en çok satan
        new(Intents.TopProducts, "en çok"), new(Intents.TopProducts, "en fazla"),
        new(Intents.TopProducts, "çok satan"), new(Intents.TopProducts, "en çok satan"),
        new(Intents.TopProducts, "hangi ürün"), new(Intents.TopProducts, "hangi ürünü"),
        new(Intents.TopProducts, "en çok satılan"), new(Intents.TopProducts, "popüler"),
        new(Intents.TopProducts, "best seller"),
        // period_compare — neden değişti / kıyas. "neden/niçin" yaygın günlük kelimeler → düşük
        // ağırlık: tek başına eşiği geçmez, ancak başka bir sinyalle birleşince niyeti belirler.
        new(Intents.PeriodCompare, "neden", 2.0), new(Intents.PeriodCompare, "niçin", 2.0),
        new(Intents.PeriodCompare, "geçen aya göre"), new(Intents.PeriodCompare, "karşılaştır"),
        new(Intents.PeriodCompare, "kıyasla"), new(Intents.PeriodCompare, "düştü"),
        new(Intents.PeriodCompare, "azaldı"), new(Intents.PeriodCompare, "arttı"),
        new(Intents.PeriodCompare, "daha az"), new(Intents.PeriodCompare, "daha çok"),
        new(Intents.PeriodCompare, "fark"),
        // revenue_forecast — tahmin/gelecek
        new(Intents.RevenueForecast, "tahmin"), new(Intents.RevenueForecast, "tahmini"),
        new(Intents.RevenueForecast, "ne olur"), new(Intents.RevenueForecast, "ne kadar olur"),
        new(Intents.RevenueForecast, "beklenen"), new(Intents.RevenueForecast, "öngörü"),
        new(Intents.RevenueForecast, "gelecek"),
        // Dönem ufuklu tahmin ("bu hafta sonu / ay bitiminde ne kadar"): kalıplar GELECEK zamanlı FİİLLE
        // bitmeli. Çıplak "hafta sonu ne kadar" / "ay sonunda" bırakılırsa "geçen hafta sonu ne kadar SATTIM"
        // gibi apaçık geçmiş sorular da tahmine düşer (ForecastAsync'te ayrıca geçmiş-dönem guard'ı var,
        // ama niyeti baştan doğru seçmek daha iyi).
        new(Intents.RevenueForecast, "ne kadar satarım"), new(Intents.RevenueForecast, "hafta sonu ne kadar satarım"),
        new(Intents.RevenueForecast, "hafta sonu ne kadar olur"), new(Intents.RevenueForecast, "hafta sonu ne bekleyebilirim"),
        new(Intents.RevenueForecast, "ay sonunda ne olur"), new(Intents.RevenueForecast, "ay sonunda ne kadar olur"),
        new(Intents.RevenueForecast, "ay bitiminde"), new(Intents.RevenueForecast, "hafta bitiminde"),
        // daily_summary — bugün/dönem özeti. "ciro" (tam kelime, 2.5) → başka güçlü sinyal yoksa günlük ciro
        // özeti; "geçen aya göre ciro" gibi durumlarda period_compare daha güçlü anchor'la kazanır.
        new(Intents.DailySummary, "bugün nasıl"), new(Intents.DailySummary, "gün sonu"),
        new(Intents.DailySummary, "bugünkü"), new(Intents.DailySummary, "bugünki"),
        new(Intents.DailySummary, "günün özeti"), new(Intents.DailySummary, "nasıl geçti"),
        new(Intents.DailySummary, "bugün ne kadar"),
        // "ciro" PREFIX → cirom / ciromu / cironuz / cirosu hepsini yakalar (tam-kelime "cirom"u kaçırıyordu).
        new(Intents.DailySummary, "ciro", 2.5, Prefix: true),
        new(Intents.DailySummary, "günlük ciro"), new(Intents.DailySummary, "günün cirosu"),
        // top_profit — kâr/kazanç odaklı. Tekil "kar/kârım/kârlı" (weight 4 → dönem kelimesi "bugünkü"yü
        // yener) TAM KELİME (prefix değil; "kart"/"karışık" gibi kelimelere bulaşmasın). â→a normalize
        // sayesinde "kâr" ve "kar" ikisi de eşleşir.
        new(Intents.TopProfit, "kar", 4.0), new(Intents.TopProfit, "kârım", 4.0),
        new(Intents.TopProfit, "kârı", 4.0), new(Intents.TopProfit, "kârlı", 4.0),
        new(Intents.TopProfit, "kârlılık", 4.0), new(Intents.TopProfit, "kazanç", 4.0),
        new(Intents.TopProfit, "en çok kâr"), new(Intents.TopProfit, "en kârlı"),
        new(Intents.TopProfit, "kâr getiren"), new(Intents.TopProfit, "kâr marjı"),
        new(Intents.TopProfit, "en fazla kâr"), new(Intents.TopProfit, "kârım ne"),
        new(Intents.TopProfit, "kaç para kâr"), new(Intents.TopProfit, "en çok kazandıran"),
        // receivables — borç/alacak/tahsilat
        new(Intents.Receivables, "kim borçlu"), new(Intents.Receivables, "bana borçlu"),
        new(Intents.Receivables, "alacağım"), new(Intents.Receivables, "alacak"),
        new(Intents.Receivables, "borç durumu"), new(Intents.Receivables, "en çok borçlu"),
        new(Intents.Receivables, "veresiye"), new(Intents.Receivables, "kimden alacağım"),
        // waste — fire/zayi
        new(Intents.Waste, "fire"), new(Intents.Waste, "zayi"),
        new(Intents.Waste, "ne kadar fire"), new(Intents.Waste, "fire maliyeti"),
        new(Intents.Waste, "çöpe giden"),
        // payment_breakdown — nakit/kart kırılımı. Tekil "nakit"/"kart" (weight 2 → tek başına eşiği
        // geçmez ama bir örnek/dönem sinyaliyle geçer) sıra-bağımsız kısa soruları ("nakit ne kadar") kurtarır.
        new(Intents.PaymentBreakdown, "nakit", 2.0), new(Intents.PaymentBreakdown, "kart", 2.0),
        new(Intents.PaymentBreakdown, "ne kadar nakit"), new(Intents.PaymentBreakdown, "ne kadar kart"),
        new(Intents.PaymentBreakdown, "nakit mi kart"), new(Intents.PaymentBreakdown, "ödeme yöntemi"),
        new(Intents.PaymentBreakdown, "nakit oranı"), new(Intents.PaymentBreakdown, "nakit girdi"),
        // category_sales — kategori kırılımı. "kategori" PREFIX (uzun kök, güvenli) → "kategorim/kategoriye/kategorisi"
        // hepsini yakalar; yoksa ekli biçimler top_products'a kayıyordu.
        new(Intents.CategorySales, "kategori", 3.0, Prefix: true),
        new(Intents.CategorySales, "hangi kategori"), new(Intents.CategorySales, "kategori bazında"),
        new(Intents.CategorySales, "en çok satan kategori"), new(Intents.CategorySales, "kategori satış"),
        new(Intents.CategorySales, "kategorilere göre"),
        // stock_alert — tükenen/azalan stok. Geçmiş-zaman biçimleri de ("azaldı/tükendi/bitti") — bunlar
        // olmadan "stok azaldı" period_compare'in "azaldı" anchor'ına kayıyordu.
        new(Intents.StockAlert, "tükeniyor"), new(Intents.StockAlert, "tükenen"),
        new(Intents.StockAlert, "biten ürün"), new(Intents.StockAlert, "azalan stok"),
        new(Intents.StockAlert, "stok uyarı"), new(Intents.StockAlert, "sipariş vermem gereken"),
        new(Intents.StockAlert, "hangi ürün bitiyor"), new(Intents.StockAlert, "yakında bitecek"),
        new(Intents.StockAlert, "stok azaldı"), new(Intents.StockAlert, "stok tükendi"),
        new(Intents.StockAlert, "stok bitti"), new(Intents.StockAlert, "tükendi"),
        // what_if — hipotetik/simülasyon
        new(Intents.WhatIf, "eklesem"), new(Intents.WhatIf, "eklersem"),
        new(Intents.WhatIf, "koysam"), new(Intents.WhatIf, "satar mı"),
        new(Intents.WhatIf, "ne kadar satılır"), new(Intents.WhatIf, "satsam"),
        // insights — proaktif öneri/uyarı isteği.
        new(Intents.Insights, "öneri"), new(Intents.Insights, "öneriler"),
        new(Intents.Insights, "önerin"), new(Intents.Insights, "tavsiye"),
        new(Intents.Insights, "neye dikkat"), new(Intents.Insights, "ne yapmalıyım"),
        new(Intents.Insights, "ne yapmam lazım"), new(Intents.Insights, "genel durum"),
        new(Intents.Insights, "dikkat etmem gereken"), new(Intents.Insights, "uyarı var mı"),
        // help — bilgi bankası (sistem/uygulama soruları). "nedir/ne demek" düşük ağırlık (çok genel);
        // "cloudposgrid/ne işe yarar/nasıl yaparım" güçlü. help AllIntents'te EN SON denenir (düşük öncelik).
        new(Intents.Help, "nedir", 2.0), new(Intents.Help, "ne demek", 2.5),
        new(Intents.Help, "ne işe yarar", 3.0), new(Intents.Help, "ne işe yarıyor", 3.0),
        new(Intents.Help, "nasıl yaparım", 3.0), new(Intents.Help, "nasıl yapılır", 3.0),
        new(Intents.Help, "nasıl çalışır", 3.0), new(Intents.Help, "cloudposgrid", 4.0),
        new(Intents.Help, "ne yapar", 2.5), new(Intents.Help, "anlat", 2.0),
        new(Intents.Help, "açıkla", 2.0), new(Intents.Help, "sen kimsin", 4.0),
        // chitchat — günlük sohbet/selamlaşma. Düşük ağırlık (2.0) + AllIntents'te EN SON denenir → "naber
        // bugünki ciro" gibi karışık soruda iş niyeti (ciro) kazanır, yalnız "naber" ise chitchat olur.
        new(Intents.Chitchat, "selam", 2.0), new(Intents.Chitchat, "merhaba", 2.0),
        new(Intents.Chitchat, "naber", 2.0), new(Intents.Chitchat, "nasılsın", 2.0),
        new(Intents.Chitchat, "ne haber", 2.0), new(Intents.Chitchat, "günaydın", 2.0),
        new(Intents.Chitchat, "iyi akşamlar", 2.0), new(Intents.Chitchat, "teşekkür", 2.0),
        new(Intents.Chitchat, "teşekkürler", 2.0), new(Intents.Chitchat, "sağ ol", 2.0),
        new(Intents.Chitchat, "sağol", 2.0), new(Intents.Chitchat, "eyvallah", 2.0),
        new(Intents.Chitchat, "görüşürüz", 2.0), new(Intents.Chitchat, "hoşça kal", 2.0),
    ];

    /// <summary>Gömülü eğitim seti (seed). Faz 3'te öğrenilenler bunun ÜSTÜNE eklenir.</summary>
    public static readonly IReadOnlyList<TrainingExample> Seed =
    [
        new(Intents.TopProducts, "Bu ay en çok hangi ürünü sattım?"),
        new(Intents.TopProducts, "En çok satan ürünüm ne?"),
        new(Intents.TopProducts, "Hangi ürün en çok satıldı bu hafta?"),
        new(Intents.TopProducts, "En fazla satılan ürünler neler?"),
        new(Intents.TopProducts, "Bugün en çok ne sattım?"),
        new(Intents.TopProducts, "Geçen ay en popüler ürün hangisiydi?"),
        new(Intents.TopProducts, "En çok ciro yapan ürünüm ne?"),

        new(Intents.PeriodCompare, "Bu ay neden geçen aya göre daha az gelirim oldu?"),
        new(Intents.PeriodCompare, "Neden gelirim düştü?"),
        new(Intents.PeriodCompare, "Bu ayki cirom geçen aya göre nasıl?"),
        new(Intents.PeriodCompare, "Geçen aya göre satışlarım arttı mı?"),
        new(Intents.PeriodCompare, "Bu ay geçen aydan farkım ne?"),
        new(Intents.PeriodCompare, "Gelirim neden azaldı?"),
        new(Intents.PeriodCompare, "Bu hafta geçen haftaya göre daha mı iyi?"),

        new(Intents.RevenueForecast, "Yarın tahmini cirom ne olur?"),
        new(Intents.RevenueForecast, "Yarın ne kadar satış beklenir?"),
        new(Intents.RevenueForecast, "Gelecek günlerde cirom ne olur?"),
        new(Intents.RevenueForecast, "Yarınki tahmini gelirim nedir?"),
        new(Intents.RevenueForecast, "Önümüzdeki gün ne kadar kazanırım?"),
        new(Intents.RevenueForecast, "Tahmini satışım ne olur?"),
        // Dönem ufuklu tahmin örnekleri (yarın DIŞINDA bir ufuk isteyenler).
        new(Intents.RevenueForecast, "Bu hafta sonu ne kadar satarım?"),
        new(Intents.RevenueForecast, "Hafta sonu ne kadar ciro beklerim?"),
        new(Intents.RevenueForecast, "Bu ay sonunda cirom ne olur?"),
        new(Intents.RevenueForecast, "Bu hafta sonunda toplam ne kadar satarım?"),

        new(Intents.DailySummary, "Bugün nasıl geçti?"),
        new(Intents.DailySummary, "Bugünkü satışlarım nasıl?"),
        new(Intents.DailySummary, "Gün sonu özeti nedir?"),
        new(Intents.DailySummary, "Bugün ne kadar sattım?"),
        new(Intents.DailySummary, "Bugünün özetini ver."),
        new(Intents.DailySummary, "Bugünkü satışlarım iyi mi?"),
        new(Intents.DailySummary, "Bugünkü ciro ne kadar?"),
        new(Intents.DailySummary, "Cirom ne kadar?"),
        new(Intents.DailySummary, "Günün cirosu ne?"),

        new(Intents.TopProfit, "En çok kâr getiren ürünüm ne?"),
        new(Intents.TopProfit, "En kârlı ürünüm hangisi?"),
        new(Intents.TopProfit, "Bu ay kârım ne kadar?"),
        new(Intents.TopProfit, "Hangi üründen en çok kazandım?"),
        new(Intents.TopProfit, "Kâr marjım nedir?"),
        new(Intents.TopProfit, "En çok kazandıran ürün hangisi?"),

        new(Intents.Receivables, "Kim bana ne kadar borçlu?"),
        new(Intents.Receivables, "En çok borçlu müşterim kim?"),
        new(Intents.Receivables, "Toplam alacağım ne kadar?"),
        new(Intents.Receivables, "Veresiye defterinde ne var?"),
        new(Intents.Receivables, "Borç durumu nedir?"),
        new(Intents.Receivables, "Kimden ne kadar alacağım var?"),

        new(Intents.Waste, "Bu ay ne kadar fire verdim?"),
        new(Intents.Waste, "Ne kadar zayi oldu?"),
        new(Intents.Waste, "Fire maliyetim ne kadar?"),
        new(Intents.Waste, "En çok hangi üründe fire var?"),

        new(Intents.PaymentBreakdown, "Ne kadar nakit ne kadar kart aldım?"),
        new(Intents.PaymentBreakdown, "Nakit oranım ne?"),
        new(Intents.PaymentBreakdown, "Ödeme yöntemi kırılımı nedir?"),
        new(Intents.PaymentBreakdown, "Bugün ne kadar nakit girdi?"),

        new(Intents.CategorySales, "Hangi kategori en çok sattı?"),
        new(Intents.CategorySales, "Kategori bazında satışlarım ne?"),
        new(Intents.CategorySales, "En çok satan kategorim hangisi?"),
        new(Intents.CategorySales, "Kategorilere göre ciro nedir?"),

        new(Intents.StockAlert, "Hangi ürünüm tükeniyor?"),
        new(Intents.StockAlert, "Stoğu azalan ürünler neler?"),
        new(Intents.StockAlert, "Hangi ürüne sipariş vermeliyim?"),
        new(Intents.StockAlert, "Yakında bitecek ürünler hangileri?"),
        new(Intents.StockAlert, "Stok uyarısı var mı?"),

        new(Intents.WhatIf, "Karışık tost eklesem ne kadar satılır?"),
        new(Intents.WhatIf, "Yeni ürün koysam satar mı?"),
        new(Intents.WhatIf, "Menüye şunu eklesem ne olur?"),

        new(Intents.Insights, "Bana öneri ver"),
        new(Intents.Insights, "Neye dikkat etmeliyim?"),
        new(Intents.Insights, "Önerilerin neler?"),
        new(Intents.Insights, "İşletmem için tavsiyen ne?"),
        new(Intents.Insights, "Ne yapmalıyım?"),
        new(Intents.Insights, "Dikkat etmem gereken bir şey var mı?"),

        new(Intents.Help, "CloudPosGrid nedir?"),
        new(Intents.Help, "Bu uygulama ne işe yarar?"),
        new(Intents.Help, "Sistem neler yapabiliyor?"),
        new(Intents.Help, "Stok kartı nedir?"),
        new(Intents.Help, "Cari ne demek?"),
        new(Intents.Help, "Adisyon nedir?"),
        new(Intents.Help, "Nasıl fatura keserim?"),
        new(Intents.Help, "Sadakat puanı nasıl çalışır?"),
        new(Intents.Help, "Sen kimsin?"),

        new(Intents.Chitchat, "Selam"),
        new(Intents.Chitchat, "Merhaba"),
        new(Intents.Chitchat, "Naber?"),
        new(Intents.Chitchat, "Nasılsın?"),
        new(Intents.Chitchat, "Günaydın"),
        new(Intents.Chitchat, "Teşekkürler"),
        new(Intents.Chitchat, "Sağ ol"),
        new(Intents.Chitchat, "Görüşürüz"),
    ];

    /// <summary>Soruyu sınıflandırır: en yüksek skorlu niyet eşiği geçerse döner, yoksa Intent=null.
    /// <paramref name="learned"/>: Faz 3'te DB'den gelen öğrenilmiş örnekler (seed'e eklenir).</summary>
    public static IntentResult Classify(string? question, DateTime today, IEnumerable<TrainingExample>? learned = null)
    {
        var period = ExtractPeriod(question, today);
        var norm = Normalize(question);
        if (norm.Length == 0)
            return new IntentResult(null, 0, period);

        var padded = " " + norm + " ";
        var qTokens = Tokenize(norm);
        if (qTokens.Count == 0)
            return new IntentResult(null, 0, period);

        // Örnek havuzu = seed + öğrenilenler, niyete göre gruplanır.
        var examples = learned is null ? (IEnumerable<TrainingExample>)Seed : Seed.Concat(learned);
        var byIntent = examples.GroupBy(e => e.Intent)
            .ToDictionary(g => g.Key, g => g.Select(e => Tokenize(Normalize(e.Text))).ToList());

        string? best = null;
        double bestScore = 0;

        foreach (var intent in AllIntents())
        {
            double score = 0;

            // 1) Anchor ifadeleri (güçlü sinyal): niyet başına EN YÜKSEK TEK anchor puanı (toplama YOK —
            //    yoksa çok-anchor'lı bir niyet, ör. top_products'ın "en çok"/"çok satan"/"en çok satan"
            //    örtüşen kalıpları yığılıp başka niyetleri ezer). Prefix=true olan anchor Türkçe ekleri
            //    yakalar (uzun kök: "kategori" → "kategorim/kategoriye"); kısa köklerde kullanılmaz.
            double anchorScore = 0;
            foreach (var a in Anchors)
            {
                if (a.Intent != intent) continue;
                var np = Normalize(a.Phrase);
                bool hit = a.Prefix
                    ? qTokens.Any(t => t.StartsWith(np, StringComparison.Ordinal))
                    : padded.Contains(" " + np + " ", StringComparison.Ordinal);
                if (hit && a.Weight > anchorScore) anchorScore = a.Weight;
            }
            score += anchorScore;

            // 2) Örnek örtüşmesi (destekleyici): en iyi eşleşen örnekle Jaccard benzerliği · 4.
            if (byIntent.TryGetValue(intent, out var exTokenLists))
            {
                double bestOverlap = 0;
                foreach (var exTokens in exTokenLists)
                {
                    var j = Jaccard(qTokens, exTokens);
                    if (j > bestOverlap) bestOverlap = j;
                }
                score += bestOverlap * 4.0;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = intent;
            }
        }

        return new IntentResult(bestScore >= Threshold ? best : null, bestScore, period);
    }

    private static IEnumerable<string> AllIntents()
    {
        yield return Intents.TopProfit;      // "kâr" içerenler top_products'tan önce denensin (daha spesifik)
        yield return Intents.CategorySales;  // "kategori" içerenler top_products'tan önce
        yield return Intents.StockAlert;
        yield return Intents.Receivables;
        yield return Intents.Waste;
        yield return Intents.PaymentBreakdown;
        yield return Intents.Insights;   // "öneri/tavsiye/dikkat" — spesifik, önce dene
        yield return Intents.WhatIf;
        yield return Intents.TopProducts;
        yield return Intents.PeriodCompare;
        yield return Intents.RevenueForecast;
        yield return Intents.DailySummary;
        yield return Intents.Help;    // en genel "nedir/nasıl" — iş niyetleri eşleşmezse
        yield return Intents.Chitchat; // EN SON: yalnız hiçbir iş/bilgi niyeti tutmazsa selamlaşma
    }

    /// <summary>Sorudan zaman dönemini çıkarır. Anahtar ifadeler yoksa Kind=None (varsayılan dönem
    /// çağıran servise bırakılır). <paramref name="today"/> = referans "bugün" (UTC gün başı).</summary>
    public static PeriodResult ExtractPeriod(string? question, DateTime today)
    {
        today = today.Date;
        var n = " " + Normalize(question) + " ";

        // Sıra ÖNEMLİ: "geçen ay" kontrolü "bu ay"dan, "dün"den önce spesifik kalıplar.
        // Türkçe ekli biçimler (datif/-ki/ablatif: "geçen haftaya", "dünkü", "bu ayki") ELLE listelenir;
        // Has() tam-token eşleştiği ve "ay" gibi kısa köklerde stemming çakışacağı için kalıp genişletme
        // en güvenli yoldur.
        if (HasAny(n, "yarin", "yarinki", "yarina", "onumuzdeki gun", "gelecek gun", "ertesi gun"))
            return new(PeriodKind.Tomorrow, today.AddDays(1), today.AddDays(2), "yarın");
        if (HasAny(n, "gecen ay", "gecen aya", "gecen ayki", "gecen aydan", "onceki ay", "onceki aya", "onceki ayki"))
        {
            var f = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
            return new(PeriodKind.LastMonth, f, f.AddMonths(1), "geçen ay");
        }
        if (HasAny(n, "bu ay", "bu aya", "bu ayki", "aylik"))
        {
            var f = new DateTime(today.Year, today.Month, 1);
            return new(PeriodKind.ThisMonth, f, f.AddMonths(1), "bu ay");
        }
        if (HasAny(n, "gecen hafta", "gecen haftaya", "gecen haftaki", "gecen haftadan", "onceki hafta", "onceki haftaya", "onceki haftaki"))
        {
            var thisWeek = StartOfWeek(today);
            return new(PeriodKind.LastWeek, thisWeek.AddDays(-7), thisWeek, "geçen hafta");
        }
        // "bu hafta sonu" MUTLAKA "bu hafta"dan ÖNCE denenmeli: " bu hafta " alt-dizgesi burada da geçtiği
        // için aşağıdaki dal eşleşir ve ufuk yanlışlıkla 7 güne genişlerdi. Cumartesi = ISO haftanın 6. günü.
        if (HasAny(n, "hafta sonu", "haftasonu", "hafta sonunda", "haftasonunda", "hafta sonuna", "haftasonuna"))
        {
            var sat = StartOfWeek(today).AddDays(5);
            return new(PeriodKind.Weekend, sat, sat.AddDays(2), "bu hafta sonu");
        }
        if (HasAny(n, "bu hafta", "bu haftaya", "bu haftaki", "haftalik"))
        {
            var f = StartOfWeek(today);
            return new(PeriodKind.ThisWeek, f, f.AddDays(7), "bu hafta");
        }
        if (HasAny(n, "dun", "dunku", "dunki", "dune", "dunden"))
            return new(PeriodKind.Yesterday, today.AddDays(-1), today, "dün");
        // "bugünki" = "bugünkü"nün çok yaygın (kü↔ki) yazımı → ikisini de tanı.
        if (HasAny(n, "bugun", "bugune", "bugunku", "bugunki", "bugunden", "gun sonu"))
            return new(PeriodKind.Today, today, today.AddDays(1), "bugün");
        if (HasAny(n, "son 7 gun", "son yedi gun"))
            return new(PeriodKind.Last7, today.AddDays(-6), today.AddDays(1), "son 7 gün");
        if (HasAny(n, "son 30 gun", "son otuz gun", "son bir ay"))
            return new(PeriodKind.Last30, today.AddDays(-29), today.AddDays(1), "son 30 gün");
        if (HasAny(n, "bu yil", "bu yila", "bu yilki", "yillik"))
        {
            var f = new DateTime(today.Year, 1, 1);
            return new(PeriodKind.ThisYear, f, f.AddYears(1), "bu yıl");
        }

        return new(PeriodKind.None, default, default, "");
    }

    /// <summary>TAKİP SORUSU çözücü (konuşma hafızası). Yalnız asıl sınıflama başarısızken çağrılır: soru
    /// tek başına anlamsız ama önceki bağlama ("ürün olarak", "peki geçen ay", "neden", "detay") bağlanır.
    /// Döner: (çözülen niyet, dönem) ya da null (takip değil → gerçekten anlaşılmadı).</summary>
    public static (string Intent, PeriodResult Period)? ResolveFollowUp(string? question, string prevIntent, DateTime today)
    {
        var norm = Normalize(question);
        if (norm.Length == 0) return null;
        var n = " " + norm + " ";
        var period = ExtractPeriod(question, today);

        // "ürün olarak / ürün bazında" → ürün detayına in (kategori niyetiyse ürün-kâra çevir).
        if (Has(n, "urun olarak") || Has(n, "urun bazinda") || (Has(n, "urun") && HasAny(n, "olarak", "bazinda", "bazli")))
            return (prevIntent == Intents.CategorySales ? Intents.TopProfit : prevIntent, period);
        // "kategori olarak / kategori bazında" → kategori kırılımına çık.
        if (Has(n, "kategori olarak") || Has(n, "kategori bazinda") || (Has(n, "kategori") && HasAny(n, "olarak", "bazinda", "bazli")))
            return (Intents.CategorySales, period);
        // "neden / niye" → dönem karşılaştırma (açıklama iste).
        if (HasAny(n, "neden", "niye", "nicin"))
            return (Intents.PeriodCompare, period);
        // Kısa "sadece dönem" takibi ("peki geçen ay", "ya bu hafta") → önceki niyet + yeni dönem.
        if (period.Kind != PeriodKind.None && norm.Split(' ').Length <= 4)
            return (prevIntent, period);
        // "detay / daha / başka / devam / peki / göster" → önceki niyeti tekrar (dönem verildiyse uygula).
        if (HasAny(n, "detay", "daha", "baska", "devam", "peki", "goster"))
            return (prevIntent, period);
        return null;
    }

    private static bool Has(string paddedNorm, string phrase)
        => paddedNorm.Contains(" " + phrase + " ", StringComparison.Ordinal);

    /// <summary>Kalıplardan herhangi biri (tam-token) metinde geçiyor mu?</summary>
    private static bool HasAny(string paddedNorm, params string[] phrases)
    {
        foreach (var p in phrases)
            if (paddedNorm.Contains(" " + p + " ", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Haftanın başı (Pazartesi, gün başı).</summary>
    private static DateTime StartOfWeek(DateTime d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // Pazartesi=0
        return d.AddDays(-diff).Date;
    }

    /// <summary>Metni sınıflandırmaya hazır hale getirir: Türkçe büyük-harf düzeltmesi + küçük harf +
    /// noktalama→boşluk + tek boşluk. Türkçe karakterler ASCII'ye indirgenir (i/ı, ş/s…) ki
    /// aksan/yazım farkları eşleşmeyi bozmasın.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // Türkçe büyük harfleri doğru küçült: İ→i, I→ı (InvariantLower yanlış yapar).
        s = s.Replace('İ', 'i').Replace('I', 'ı');
        s = s.ToLowerInvariant();

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            // Türkçe aksanları ASCII'ye indirger. 'ı'→'i' KRİTİK: kullanıcı klavyede i/ı'yı sürekli
            // karıştırır ("yarin"/"yarın") — eşleşmeyi bozmasın diye ikisini tek 'i'de birleştiriyoruz.
            char c = ch switch
            {
                'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u',
                // Şapkalı (düzeltme imli) harfler → ASCII. KRİTİK: "kâr" ile "kar" aynı token olsun
                // (kullanıcı standart klavyede şapkasız yazar; "kâr" anchor'ları yoksa eşleşmez).
                'â' => 'a', 'î' => 'i', 'û' => 'u',
                _ => ch,
            };
            // Harf/rakam dışını (noktalama, sembol) boşluğa çevir.
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else sb.Append(' ');
        }
        // Çoklu boşluğu teke indir.
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Normalize edilmiş metni anlamlı token kümesine böler (durak kelimeler ve tek harfler elenir).</summary>
    private static HashSet<string> Tokenize(string norm)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in norm.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length < 2) continue;
            if (Stop.Contains(t)) continue;
            set.Add(t);
        }
        return set;
    }

    /// <summary>İki token kümesinin (YAZIM-HATASI TOLERANSLI) Jaccard benzerliği, 0..1. Bir token diğer
    /// kümede birebir yoksa, Levenshtein mesafesi eşik içindeyse yine eşleşmiş sayılır ("olarka"≈"olarak",
    /// "ürü"≈"ürün"). Böylece kullanıcı yazım hatası yapsa da niyet tanınır.</summary>
    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = 0;
        foreach (var t in a) if (FuzzyContains(b, t)) inter++;
        int union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }

    /// <summary>b kümesinde t'ye birebir ya da yazım-hatası-yakın (Levenshtein ≤ eşik) bir token var mı?
    /// Eşik: kısa kelimede 1, uzun kelimede (≥5 harf) 2 — kısa kelimelerde gevşek eşleşme gürültü yapmasın.</summary>
    private static bool FuzzyContains(HashSet<string> b, string t)
    {
        if (b.Contains(t)) return true;
        int max = t.Length <= 4 ? 1 : 2;
        foreach (var x in b)
        {
            if (Math.Abs(x.Length - t.Length) > max) continue; // uzunluk farkı fazlaysa boşuna hesaplama
            if (Levenshtein(t, x, max) <= max) return true;
        }
        return false;
    }

    /// <summary>İki dizi arası Levenshtein (düzenleme) mesafesi; <paramref name="max"/> aşılırsa erken çıkar.</summary>
    private static int Levenshtein(string s, string t, int max)
    {
        int n = s.Length, m = t.Length;
        if (Math.Abs(n - m) > max) return max + 1;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            int rowMin = curr[0];
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                if (curr[j] < rowMin) rowMin = curr[j];
            }
            if (rowMin > max) return max + 1; // bu satırın tamamı eşiği aştı → daha da artar
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }
}
