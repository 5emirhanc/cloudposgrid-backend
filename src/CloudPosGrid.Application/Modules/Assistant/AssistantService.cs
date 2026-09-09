using System.Globalization;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Reports;
using CloudPosGrid.Application.Modules.Stock;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Assistant;

/// <summary>İşletme tipine (sektör) göre asistanın kullanacağı terimler. Frontend business-profile'ın
/// backend karşılığı — asistan cevapları uygulamanın diliyle tutarlı olsun ("ürün"/"hizmet"/"parça").</summary>
public readonly record struct SectorTerms(string Product, string ProductPlural, string Sale)
{
    public static SectorTerms For(BusinessType t) => t switch
    {
        BusinessType.Beauty => new("hizmet", "hizmetler", "satış"),
        BusinessType.Service => new("kalem", "parça/hizmetler", "iş emri"),
        BusinessType.Hospitality => new("ürün", "ürünler", "adisyon"),
        _ => new("ürün", "ürünler", "satış"), // General, Retail
    };
}

/// <summary>
/// CloudPosGrid AI Asistanı. Harici LLM API KULLANMAZ — kendi niyet motorumuzla (IntentEngine)
/// soruyu sınıflar, cevabı GERÇEK rapor verilerinden (IReportService) üretir. Rakamları biz
/// verdiğimiz için "halüsinasyon" imkânsızdır. Anlaşılamayan sorular Understood=false döner
/// (Faz 3'te öğrenmeye alınacak).
/// </summary>
public sealed class AssistantService : IAssistantService
{
    private static readonly CultureInfo Tr = new("tr-TR");
    private readonly IReportService _reports;
    private readonly IReplenishmentService _replenishment;
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _user;

    /// <summary>"Şimdi" (UTC) sağlayıcısı. Prod'da DI varsayılanı UtcNow kullanır; testte sabitlenir.
    /// init-only → DI'ı etkilemez (yalnız constructor'la enjekte edilir).</summary>
    public Func<DateTime> NowUtc { get; init; } = () => DateTime.UtcNow;

    public AssistantService(IReportService reports, IReplenishmentService replenishment, IApplicationDbContext db, ICurrentUser user)
    {
        _reports = reports;
        _replenishment = replenishment;
        _db = db;
        _user = user;
    }

    /// <summary>Hazır soru butonları — işletme tipine göre terminoloji uyarlanır (kuaför→hizmet, tamirci→kalem).
    /// Sorular "en çok" gibi güçlü ipuçları taşıdığından motor sektör kelimesinden bağımsız doğru niyeti bulur.</summary>
    public IReadOnlyList<AssistantSuggestionDto> Suggestions()
    {
        var terms = SectorTerms.For(_user.BusinessType ?? BusinessType.General);
        return
        [
            new("💡 Bana öneri ver", "Bana öneri ver, neye dikkat etmeliyim?"),
            new($"En çok satan {terms.Product}", $"Bu ay en çok hangi {terms.Product} sattım?"),
            new("En çok kâr getiren", "Bu ay en çok kâr getiren ürünüm ne?"),
            new("Neden gelirim değişti?", "Bu ay neden geçen aya göre daha az gelirim oldu?"),
            new("Kim bana borçlu?", "Kim bana ne kadar borçlu?"),
            new("Bugün nasıl geçti?", "Bugün nasıl geçti?"),
        ];
    }

    /// <summary>Sınıflandırmaya girecek en uzun soru — bunun ötesi kesilir (ağır Normalize/Tokenize'ı DoS'tan korur).</summary>
    private const int MaxQuestionLen = 500;

    public async Task<AssistantReplyDto> AskAsync(string question, AssistantContext? context = null, CancellationToken ct = default)
    {
        // Aşırı uzun girdiyi sınıflandırmadan ÖNCE kırp (CPU/bellek koruması).
        question ??= string.Empty;
        if (question.Length > MaxQuestionLen) question = question[..MaxQuestionLen];

        var today = NowUtc().Date;
        // Öğrenilmiş örnekleri (admin etiketlemeleri) motora yükle → seed'e EKlenir ("sora sora eğit").
        var learned = await _db.AssistantTrainings
            .Select(t => new TrainingExample(t.Intent, t.Text))
            .ToListAsync(ct);

        var sector = _user.BusinessType ?? BusinessType.General;
        var reply = await GenerateAsync(question, today, sector, learned, context, ct);

        // Anlaşılamayan soruyu öğrenme kuyruğuna al (admin sonra bir niyete atayacak).
        if (!reply.Understood)
            await LogUnresolvedAsync(question, ct);

        return reply;
    }

    /// <summary>SAF cevap üretimi: niyet motoru + rapor OKUMA. DB'ye YAZMAZ (öğrenme AskAsync'te).
    /// Bu ayrım sayesinde cevap mantığı DB'siz birim test edilebilir. <paramref name="sector"/> = işletme tipi
    /// (terminoloji için); <paramref name="prevContext"/> = önceki konuşma bağlamı (takip sorularını çözer).</summary>
    public async Task<AssistantReplyDto> GenerateAsync(
        string question, DateTime today, BusinessType sector = BusinessType.General,
        IReadOnlyList<TrainingExample>? learned = null, AssistantContext? prevContext = null, CancellationToken ct = default)
    {
        var result = IntentEngine.Classify(question, today, learned);
        var terms = SectorTerms.For(sector);
        var intent = result.Intent;
        var period = result.Period;

        // KONUŞMA HAFIZASI: asıl niyet anlaşılmadıysa ama önceki bağlam varsa, takip sorusu olabilir
        // ("ürün olarak", "peki geçen ay", "neden") → önceki niyete/döneme bağla.
        if (intent is null && !string.IsNullOrEmpty(prevContext?.LastIntent))
        {
            var follow = IntentEngine.ResolveFollowUp(question, prevContext!.LastIntent!, today);
            if (follow is not null)
            {
                intent = follow.Value.Intent;
                period = follow.Value.Period;
            }
        }

        if (intent is null)
        {
            const string msg =
                "Bunu tam anlayamadım 🤔 Şunları sorabilirsin: en çok satan/kâr getiren ürün, gelirinin neden " +
                "değiştiği, tahmini ciro, günün özeti, kim sana borçlu, fire/stok durumu ya da nakit-kart kırılımı. " +
                "Farklı kelimelerle tekrar dener misin?";
            return new AssistantReplyDto(msg, Intent: null, Understood: false, Context: prevContext);
        }

        var answer = intent switch
        {
            Intents.TopProducts => await TopProductsAsync(period, today, terms, ct),
            Intents.PeriodCompare => await PeriodCompareAsync(period, today, terms, ct),
            Intents.RevenueForecast => await ForecastAsync(period, today, ct),
            Intents.DailySummary => await DailySummaryAsync(period, today, terms, ct),
            Intents.TopProfit => await TopProfitAsync(period, today, terms, ct),
            Intents.Receivables => await ReceivablesAsync(ct),
            Intents.Waste => await WasteAsync(period, today, terms, ct),
            Intents.PaymentBreakdown => await PaymentBreakdownAsync(period, today, ct),
            Intents.CategorySales => await CategorySalesAsync(period, today, ct),
            Intents.StockAlert => await StockAlertAsync(terms, ct),
            Intents.WhatIf => WhatIf(terms),
            Intents.Help => Help(question),
            Intents.Insights => await InsightsAsync(today, ct),
            Intents.Chitchat => Chitchat(question),
            _ => "Bu konuda henüz yardımcı olamıyorum.",
        };

        // Dönem belirtilmemiş dönem-duyarlı niyetlerde varsayımı ŞEFFAF yap (Reasoning Engine: tahmin
        // ediyorsan açıkça belirt) → kullanıcı sessiz varsayıma takılmasın, dönemi kolayca değiştirebilsin.
        if (period.Kind == PeriodKind.None && IsPeriodSensitive(intent))
            answer += "\n\n📅 Dönem belirtmediğin için **bu ayı** baz aldım — \"geçen ay\", \"bu hafta\" ya da \"bugün\" diyerek değiştirebilirsin.";

        // Güncel bağlamı döndür → frontend saklar, bir sonraki takip sorusunda geri gönderir.
        var newContext = new AssistantContext(intent, period.Kind.ToString());
        return new AssistantReplyDto(answer, intent, Understood: true, Context: newContext);
    }

    // ---- Öğrenme döngüsü (Faz 3) ----

    public async Task<IReadOnlyList<AssistantUnresolvedDto>> UnresolvedAsync(int take = 50, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 200) take = 200;
        return await _db.AssistantUnresolveds
            .Where(u => !u.IsResolved)
            .OrderByDescending(u => u.Count).ThenByDescending(u => u.CreatedAt)
            .Take(take)
            .Select(u => new AssistantUnresolvedDto(u.Id, u.Question, u.Count, u.CreatedAt, u.AskedByEmail))
            .ToListAsync(ct);
    }

    public async Task ResolveAsync(Guid unresolvedId, string intent, CancellationToken ct = default)
    {
        if (!IntentCatalog().Any(i => i.Code == intent))
            throw new ArgumentException($"Geçersiz niyet kodu: {intent}", nameof(intent));

        var u = await _db.AssistantUnresolveds.FirstOrDefaultAsync(x => x.Id == unresolvedId, ct);
        if (u is null || u.IsResolved) return; // idempotent: yoksa/çözülmüşse sessiz geç

        // Soruyu bir eğitim örneğine dönüştür → motor bir sonraki soruda tanır.
        _db.AssistantTrainings.Add(new AssistantTraining { Intent = intent, Text = u.Question, Source = "learned" });
        u.IsResolved = true;
        u.ResolvedIntent = intent;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DismissUnresolvedAsync(Guid unresolvedId, CancellationToken ct = default)
    {
        var u = await _db.AssistantUnresolveds.FirstOrDefaultAsync(x => x.Id == unresolvedId, ct);
        if (u is null || u.IsResolved) return;
        u.IsResolved = true; // eğitime eklemeden gizle (ResolvedIntent null kalır)
        await _db.SaveChangesAsync(ct);
    }

    public IReadOnlyList<AssistantIntentDto> IntentCatalog() =>
    [
        new(Intents.TopProducts, "En çok satan ürün"),
        new(Intents.TopProfit, "En çok kâr getiren"),
        new(Intents.PeriodCompare, "Dönem kıyası / neden değişti"),
        new(Intents.RevenueForecast, "Ciro tahmini"),
        new(Intents.DailySummary, "Günlük / dönem özeti"),
        new(Intents.Receivables, "Borç / alacak durumu"),
        new(Intents.Waste, "Fire / zayi"),
        new(Intents.PaymentBreakdown, "Ödeme kırılımı (nakit/kart)"),
        new(Intents.CategorySales, "Kategori satışları"),
        new(Intents.StockAlert, "Stok uyarısı / tükenecekler"),
        new(Intents.WhatIf, "Hipotetik (\"X eklesem ne satar\")"),
        new(Intents.Help, "Bilgi / sistem sorusu (\"X nedir\")"),
        new(Intents.Insights, "Öneriler / uyarılar"),
    ];

    /// <summary>Anlaşılamayan soruyu kuyruğa alır. Aynı soru tekrar gelirse yeni satır açmaz, sayacı artırır.
    /// Dedup, DB'de LOWER() yerine önceden hesaplanmış normalize anahtar (QuestionKey) üzerinden yapılır →
    /// .NET/SQL küçük-harf uyuşmazlığı (Türkçe i/İ) ortadan kalkar. Eşzamanlı çift-ekleme, QuestionKey
    /// üzerindeki kısmi tekil indeks + DbUpdateException yakalama ile sayacı-artırmaya çevrilir.</summary>
    private async Task LogUnresolvedAsync(string? question, CancellationToken ct)
    {
        var q = (question ?? "").Trim();
        if (q.Length < 3) return;          // boş/çok kısa gürültüyü kaydetme
        if (q.Length > MaxQuestionLen) q = q[..MaxQuestionLen];
        var key = IntentEngine.Normalize(q);
        if (key.Length == 0) return;       // yalnız noktalama/simge → anlamsız

        var existing = await _db.AssistantUnresolveds
            .FirstOrDefaultAsync(u => !u.IsResolved && u.QuestionKey == key, ct);
        if (existing is not null)
        {
            existing.Count++;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var row = new AssistantUnresolved { Question = q, QuestionKey = key, AskedByEmail = _user.Email };
        _db.AssistantUnresolveds.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Yarış: başka bir istek aynı anahtarı ekledi (kısmi tekil indeks ihlali). Eklemeyi geri al
            // (Added → Detached), mevcut satırı bul ve sayacı artır → "aynı soru = tek satır" değişmezi korunur.
            _db.AssistantUnresolveds.Remove(row);
            var again = await _db.AssistantUnresolveds
                .FirstOrDefaultAsync(u => !u.IsResolved && u.QuestionKey == key, ct);
            if (again is not null)
            {
                again.Count++;
                await _db.SaveChangesAsync(ct);
            }
        }
    }

    // ---- Niyet cevapları (GERÇEK veriden) ----

    private async Task<string> TopProductsAsync(PeriodResult p, DateTime today, SectorTerms terms, CancellationToken ct)
    {
        var (from, to, label) = ResolvePeriod(p, today, defaultKind: PeriodKind.ThisMonth);
        var s = await _reports.GetSalesAsync(from, to, ct);

        if (s.TopProducts.Count == 0)
            return $"{Cap(label)} henüz {terms.Sale} kaydın görünmüyor. Satış girdikçe en çok satanları buradan gösterebilirim.";

        // TopProducts CİRO'ya (KDV hariç net tutar) göre sıralıdır → "en çok ciro getiren" de.
        // "en çok adet satan" olduğunu ima etmemek için ciro dilini kullan (adedi ek bilgi olarak ver).
        var top = s.TopProducts[0];
        var sb = new System.Text.StringBuilder();
        sb.Append($"{Cap(label)} en çok ciro getiren {terms.Product} **{top.Name}**: {Qty(top.Quantity)} adet, {Money(top.Total)}.");

        if (s.TopProducts.Count > 1)
        {
            var rest = s.TopProducts.Skip(1).Take(2)
                .Select((t, i) => $"{i + 2}. {t.Name} ({Money(t.Total)})");
            sb.Append(" Ardından " + string.Join(", ", rest) + ".");
        }
        // Tutarlılık: ürün tutarları KDV hariç olduğundan toplam ciroyu da KDV hariç (SalesSubtotal) ver.
        sb.Append($" Toplam ciron {Money(s.SalesSubtotal)} ({s.SalesCount} {terms.Sale}, KDV hariç).");
        return sb.ToString();
    }

    private async Task<string> PeriodCompareAsync(PeriodResult p, DateTime today, SectorTerms terms, CancellationToken ct)
    {
        var (cur, prev, unit) = ComparePeriods(p, today);
        var c = await _reports.GetSalesAsync(cur.from, cur.to, ct);
        var q = await _reports.GetSalesAsync(prev.from, prev.to, ct);

        // KDV hariç ciro (SalesSubtotal) — kategori kırılımları (ByCategory.Total) da net olduğundan tutarlı.
        var curRev = c.SalesSubtotal;
        var prevRev = q.SalesSubtotal;

        if (prevRev == 0 && curRev == 0)
            return $"Ne {cur.label} ne de {prev.label} için {terms.Sale} kaydın var; kıyaslayacak veri yok.";
        if (prevRev == 0)
            return $"{Cap(prev.label)} {terms.Sale} kaydın yok, o yüzden kıyas yapamıyorum. {Cap(cur.label)} ciron {Money(curRev)} (KDV hariç).";

        var diff = curRev - prevRev;

        var sb = new System.Text.StringBuilder();
        sb.Append($"{Cap(cur.label)} ciron {Money(curRev)}, {prev.label} {Money(prevRev)} idi (KDV hariç). ");
        if (diff == 0)
        {
            sb.Append("Gelirin **aynı kaldı**.");
        }
        else
        {
            var pct = Math.Round(diff / prevRev * 100, 1);
            var dir = diff > 0 ? "arttı" : "düştü";
            var word = diff > 0 ? "fazla" : "az";
            sb.Append($"Gelirin **%{Math.Abs(pct).ToString("0.#", Tr)} {dir}** ({Money(Math.Abs(diff))} {word}).");
        }

        // En çok değişen kategoriyi bul (mevcut ↔ önceki eşleştir).
        var biggest = BiggestCategoryChange(c.ByCategory, q.ByCategory);
        if (biggest is not null)
        {
            var (name, cdiff) = biggest.Value;
            var cw = cdiff >= 0 ? "artış" : "düşüş";
            sb.Append($" En büyük {cw} **{name}** kategorisinde ({Money(Math.Abs(cdiff))}).");
        }
        // Danışman dokunuşu: düşüşte kısa bir öneri.
        if (diff < 0)
            sb.Append($" 💡 Düşüşü telafi için {(biggest is { } b2 && b2.diff < 0 ? $"**{b2.name}** kaleminde" : "en çok düşen kalemde")} kampanya/promosyon düşünebilirsin.");
        return sb.ToString();
    }

    /// <summary>Ciro tahmini — DÖNEM FARKINDA. Soruda dönem yoksa/"yarın" ise tek gün; "hafta sonu" ise
    /// Cumartesi+Pazar; "bu hafta"/"bu ay" ise dönem SONU (gerçekleşen + kalan günlerin projeksiyonu).
    /// Bütün rakamlar gerçek günlük trendden türetilir — uydurma yok, dil hep tahmin olduğunu söyler.</summary>
    private async Task<string> ForecastAsync(PeriodResult p, DateTime today, CancellationToken ct)
    {
        // GEÇMİŞ DÖNEM TAHMİN EDİLMEZ. "geçen hafta sonu ne kadar sattım" gibi sorular tahmin çapalarına
        // takılıp buraya düşebiliyor; olmuş bitmiş bir dönem için projeksiyon uydurmak yerine GERÇEKLEŞEN
        // rakamı veririz (soru zaten onu soruyor).
        if (p.Kind is PeriodKind.Yesterday or PeriodKind.LastWeek or PeriodKind.LastMonth)
        {
            var pastReport = await _reports.GetSalesAsync(p.From, p.To, ct);
            var realized = pastReport.DailyTrend.Sum(d => d.NetTotal);
            return $"{Cap(p.Label)} geçmişte kaldı — tahmin değil GERÇEKLEŞEN rakamı vereyim: " +
                   $"**{Money(realized)}** ciro (KDV hariç), {pastReport.SalesCount} satış. " +
                   "İleriye dönük beklenti istersen \"yarın\", \"bu hafta sonu\" ya da \"bu ay bitiminde\" diye sorabilirsin.";
        }

        // Son 30 günün günlük trendi (satışsız günler 0 dahil) → basit ortalama + aynı hafta-günü inceliği.
        // Dönem-sonu projeksiyonunda GERÇEKLEŞEN kısmı da bu trendden okuduğumuz için, dönem başı 30 günden
        // eskiyse (ör. ayın 31'i) pencereyi geriye genişletiriz; yoksa ayın ilk günleri toplamdan düşerdi.
        var histFrom = today.AddDays(-29);
        var isPeriodEnd = p.Kind is PeriodKind.ThisWeek or PeriodKind.ThisMonth;
        var from = isPeriodEnd && p.From != default && p.From < histFrom ? p.From : histFrom;
        var s = await _reports.GetSalesAsync(from, today.AddDays(1), ct);
        var days = s.DailyTrend;
        if (days.Count == 0 || days.All(d => d.NetTotal == 0))
            return "Tahmin için yeterli geçmiş satış verin yok. Birkaç günlük satıştan sonra yarın için beklenti üretebilirim.";

        // Ortalamalar YALNIZ son 30 TAMAMLANMIŞ günden hesaplanır: bugün henüz bitmediği için ortalamaya
        // katılırsa (yarım gün, çoğu zaman ₺0) beklentiyi sistematik olarak aşağı çeker.
        var hist = days.Where(d => d.Date >= histFrom && d.Date < today.Date).ToList();
        if (hist.Count == 0) hist = days;
        var overallAvg = Math.Round(hist.Average(d => d.NetTotal), 2);

        // HAFTA SONU: iki günü AYRI ayrı ele al (Cumartesi ile Pazar genelde çok farklıdır). Gün GEÇTİYSE
        // tahmin değil GERÇEKLEŞEN rakamı söyleriz — Pazar akşamı "hafta sonu ne satarım" diyene olmuş
        // Cumartesi'yi tahmin gibi sunmak yanlış olurdu.
        if (p.Kind == PeriodKind.Weekend)
        {
            var sat = p.From;              // ExtractPeriod dönemin ilk gününü Cumartesi verir
            var sun = sat.AddDays(1);
            var (satVal, satDone) = DayValue(days, hist, sat, today, overallAvg);
            var (sunVal, sunDone) = DayValue(days, hist, sun, today, overallAvg);
            var satTxt = $"Cumartesi {(satDone ? "gerçekleşen" : "~")}{Money(satVal)}";
            var sunTxt = $"Pazar {(sunDone ? "gerçekleşen" : "~")}{Money(sunVal)}";
            var head = satDone && sunDone
                ? $"Bu hafta sonu ({sat.ToString("d MMMM", Tr)}–{sun.ToString("d MMMM", Tr)}) tamamlandı: **{Money(satVal + sunVal)}** ciro (KDV hariç)"
                : $"Bu hafta sonu ({sat.ToString("d MMMM", Tr)} Cumartesi – {sun.ToString("d MMMM", Tr)} Pazar) için kaba beklenti **~{Money(satVal + sunVal)}** (KDV hariç)";
            return $"{head} ({satTxt}, {sunTxt}). " +
                   $"Geçmiş Cumartesi/Pazar ortalamalarına dayanır; günlük genel ortalaman {Money(overallAvg)}." +
                   (satDone && sunDone ? "" : " Bu bir tahmindir, kesin değildir.");
        }

        // DÖNEM SONU (bu hafta / bu ay): gerçekleşen (DÜNE kadar) + BUGÜN DAHİL kalan günlerin projeksiyonu.
        // Bugünü "gerçekleşen" sayarsak günün henüz yaşanmamış kısmı toplamdan düşer (sabah sorulunca
        // dönem sonu beklentisi bir günlük ciro kadar eksik çıkardı).
        if (isPeriodEnd)
        {
            var actual = days.Where(d => d.Date >= p.From && d.Date < today.Date).Sum(d => d.NetTotal);
            var remaining = (int)(p.To - today.Date).TotalDays; // bugün DAHİL kalan gün sayısı
            if (remaining <= 1)
            {
                var soFar = actual + days.Where(d => d.Date == today.Date).Sum(d => d.NetTotal);
                return $"{Cap(p.Label)} şu ana kadar **{Money(soFar)}** ciro oldu (KDV hariç). Dönemin son günündesin, " +
                       "eklenecek gün kalmadı — bu rakam pratikte dönem sonu beklentin.";
            }

            var projected = Math.Round(overallAvg * remaining, 2);
            // "…sonunda" DEĞİL "…bitiminde": "bu hafta sonunda" Türkçede hafta sonuyla karışırdı.
            return $"{Cap(p.Label)} düne kadar **{Money(actual)}** ciro gerçekleşti (KDV hariç). Bugün dahil kalan " +
                   $"{remaining} güne günlük ortalaman {Money(overallAvg)} üzerinden ~{Money(projected)} eklenirse, " +
                   $"{p.Label} bitiminde toplam **~{Money(actual + projected)}** bekleyebilirsin. (Bu bir tahmindir, kesin değildir.)";
        }

        // Varsayılan (dönem yok / "yarın"): tek gün.
        var tomorrow = today.AddDays(1);
        var sameDow = hist.Where(d => d.Date.DayOfWeek == tomorrow.DayOfWeek).ToList();
        var useDow = sameDow.Count >= 2;
        var forecast = useDow ? Math.Round(sameDow.Average(d => d.NetTotal), 2) : overallAvg;

        var basis = useDow
            ? $"geçmiş {TrDay(tomorrow.DayOfWeek)} günlerinin ortalamasına"
            : "son 30 günlük ortalamana";

        return $"Yarın ({TrDay(tomorrow.DayOfWeek)}) için {basis} göre kaba beklenti **~{Money(forecast)}** (KDV hariç). " +
               $"(Son 30 günde günlük ortalaman {Money(overallAvg)}. Bu bir tahmindir, kesin değildir.)";
    }

    /// <summary>Bir hafta-gününün beklentisi: o günün geçmiş ortalaması (en az 2 örnek varsa), yoksa
    /// genel günlük ortalama. Yalnız gerçek trend verisinden türetilir — veri yoksa rakam UYDURMAZ.</summary>
    private static decimal DowAvg(List<DailySalesDto> hist, DayOfWeek dow, decimal overallAvg)
    {
        var same = hist.Where(d => d.Date.DayOfWeek == dow).ToList();
        return same.Count >= 2 ? Math.Round(same.Average(d => d.NetTotal), 2) : overallAvg;
    }

    /// <summary>Bir günün rakamı: gün GEÇTİYSE (bugünden önce) gerçekleşen net ciro, aksi halde o hafta-gününün
    /// geçmiş ortalamasından projeksiyon. İkinci değer "gerçekleşen mi" bilgisidir — cevabın dili buna göre
    /// kurulur (olmuş bir günü "tahmin" diye sunmayalım). Bugün henüz bitmediği için projeksiyon sayılır.</summary>
    private static (decimal Value, bool Realized) DayValue(
        List<DailySalesDto> days, List<DailySalesDto> hist, DateTime day, DateTime today, decimal overallAvg)
    {
        if (day.Date < today.Date)
            return (days.Where(d => d.Date.Date == day.Date).Sum(d => d.NetTotal), true);
        return (DowAvg(hist, day.DayOfWeek, overallAvg), false);
    }

    private async Task<string> DailySummaryAsync(PeriodResult p, DateTime today, SectorTerms terms, CancellationToken ct)
    {
        // Dönem farkında ol: "bu ay/bu hafta nasıl geçti" gibi geniş dönemleri tek güne indirgeme.
        // Yalnız Today/Yesterday/None/Tomorrow tek-gün moduna düşer; diğerleri kendi aralığını kullanır.
        DateTime from, to;
        string label;
        switch (p.Kind)
        {
            case PeriodKind.Yesterday:
                from = today.AddDays(-1); to = today; label = "dün"; break;
            case PeriodKind.None:
            case PeriodKind.Today:
            case PeriodKind.Tomorrow:
                from = today; to = today.AddDays(1); label = "bugün"; break;
            default: // ThisWeek/ThisMonth/LastWeek/LastMonth/Last7/Last30/ThisYear
                from = p.From; to = p.To; label = p.Label; break;
        }
        var isSingleDay = (to - from).TotalDays <= 1.0;

        var s = await _reports.GetSalesAsync(from, to, ct);
        if (s.SalesCount == 0)
            return $"{Cap(label)} henüz {terms.Sale} yok.";

        var sb = new System.Text.StringBuilder();
        sb.Append($"{Cap(label)} **{s.SalesCount} {terms.Sale}**, {Money(s.SalesSubtotal)} ciro (KDV hariç), ~{Money(s.EstimatedProfit)} tahmini kâr.");
        if (s.ByPaymentMethod.Count > 0)
        {
            var top = s.ByPaymentMethod[0];
            sb.Append($" En çok {top.Name} ({Money(top.Amount)}).");
        }
        if (s.AvgBasket > 0)
            sb.Append($" Ortalama sepet {Money(s.AvgBasket)}.");

        // Açık adisyon uyarısı yalnız bugünün tek-gün özeti için anlamlı.
        if (isSingleDay && label == "bugün")
        {
            var close = await _reports.GetDailyCloseAsync(from, ct);
            if (close.OpenOrdersCount > 0)
                sb.Append($" ⚠️ {close.OpenOrdersCount} açık adisyon ({Money(close.OpenOrdersTotal)}) kapanmayı bekliyor.");
        }
        return sb.ToString();
    }

    // ---- Genişletilmiş konular (kâr, borç/alacak, fire, ödeme, kategori, stok, what-if) ----

    private async Task<string> TopProfitAsync(PeriodResult p, DateTime today, SectorTerms terms, CancellationToken ct)
    {
        var (from, to, label) = ResolvePeriod(p, today, defaultKind: PeriodKind.ThisMonth);
        var pr = await _reports.GetProfitAsync(from, to, ct);

        if (pr.TotalRevenue == 0)
            return $"{Cap(label)} kâr hesaplayacak {terms.Sale} kaydın yok.";

        var sb = new System.Text.StringBuilder();
        sb.Append($"{Cap(label)} brüt kârın **{Money(pr.GrossProfit)}** (marj %{pr.GrossMarginPercent.ToString("0.#", Tr)}). ");

        // ÜRÜN bazında en kârlılar → "hangi ürünler kârlı / neye odaklanmalıyım" sorusuna doğru cevap + öneri.
        // Ürün kârı kesinti öncesi YAKLAŞIKTIR (puan/pazaryeri ürün seviyesine dağıtılamaz) → "yaklaşık" işaretle.
        var products = await _reports.GetProductProfitAsync(from, to, ct);
        var winners = products.Where(x => x.Profit > 0).Take(3).ToList();
        if (winners.Count > 0)
        {
            sb.Append($"En kârlı {terms.ProductPlural} (yaklaşık): ");
            sb.Append(string.Join(", ", winners.Select(x => $"**{x.Name}** ({Money(x.Profit)})")));
            sb.Append($". 💡 Kârını artırmak için bu {terms.ProductPlural}e ağırlık vermen mantıklı olur.");
        }
        else
        {
            var topCat = pr.ByCategory.OrderByDescending(c => c.Profit).FirstOrDefault();
            if (topCat is not null && topCat.Profit > 0)
                sb.Append($"Kâr dağılımında en önde **{topCat.Name}** kategorisi geliyor.");
        }
        // Danışman: düşük marj uyarısı (iş kuralı — marj %20 altı).
        if (pr.GrossMarginPercent < 20)
            sb.Append(" ⚠️ Genel marjın %20'nin altında — alış maliyetlerini ve satış fiyatlarını gözden geçirmen iyi olur.");
        return sb.ToString();
    }

    private async Task<string> ReceivablesAsync(CancellationToken ct)
    {
        var aging = await _reports.GetAgingAsync(ct);
        if (aging.TotalReceivable == 0)
            return "Şu an sana borçlu görünen kimse yok — tahsil edilecek açık alacağın bulunmuyor. 👍";

        var sb = new System.Text.StringBuilder();
        sb.Append($"Toplam alacağın **{Money(aging.TotalReceivable)}**. ");
        var top = aging.Receivables.OrderByDescending(r => r.Balance).FirstOrDefault();
        if (top is not null)
            sb.Append($"En çok **{top.Name}** borçlu: {Money(top.Balance)}{(top.OldestDays is int d ? $" ({d} gün)" : "")}. ");
        // Danışman: 90 günü aşan alacak riski.
        if (aging.Over90 > 0)
            sb.Append($"⚠️ {Money(aging.Over90)} tutarında 90 günü aşmış alacağın var — tahsilatını öne alman iyi olur.");
        return sb.ToString();
    }

    private async Task<string> WasteAsync(PeriodResult p, DateTime today, SectorTerms terms, CancellationToken ct)
    {
        var (from, to, label) = ResolvePeriod(p, today, defaultKind: PeriodKind.ThisMonth);
        var w = await _reports.GetWasteAsync(from, to, ct);
        if (w.TotalCost == 0)
            return $"{Cap(label)} fire/zayi kaydın yok. 👍";

        var sb = new System.Text.StringBuilder();
        sb.Append($"{Cap(label)} toplam **{Money(w.TotalCost)}** fire/zayi verdin. ");
        var top = w.Items.OrderByDescending(i => i.Cost).FirstOrDefault();
        if (top is not null)
            sb.Append($"En çok **{top.ProductName}** ({Money(top.Cost)}). ");
        sb.Append("💡 Fire kâr raporunda görünmeyen gizli kayıptır — sık tekrar eden kalemlerde sipariş/porsiyon miktarını gözden geçir.");
        return sb.ToString();
    }

    private async Task<string> PaymentBreakdownAsync(PeriodResult p, DateTime today, CancellationToken ct)
    {
        var (from, to, label) = ResolvePeriod(p, today, defaultKind: PeriodKind.ThisMonth);
        var s = await _reports.GetSalesAsync(from, to, ct);
        if (s.ByPaymentMethod.Count == 0)
            return $"{Cap(label)} tahsilat kaydın yok.";

        var total = s.ByPaymentMethod.Sum(m => m.Amount);
        var parts = s.ByPaymentMethod.Select(m =>
            $"{m.Name} {Money(m.Amount)} (%{(total > 0 ? Math.Round(m.Amount / total * 100, 1) : 0).ToString("0.#", Tr)})");
        return $"{Cap(label)} tahsilat kırılımı: " + string.Join(", ", parts) + $". Toplam {Money(total)}.";
    }

    private async Task<string> CategorySalesAsync(PeriodResult p, DateTime today, CancellationToken ct)
    {
        var (from, to, label) = ResolvePeriod(p, today, defaultKind: PeriodKind.ThisMonth);
        var s = await _reports.GetSalesAsync(from, to, ct);
        if (s.ByCategory.Count == 0)
            return $"{Cap(label)} kategori bazında satış kaydın yok.";

        var top = s.ByCategory[0]; // ciro (net) sıralı
        var sb = new System.Text.StringBuilder();
        sb.Append($"{Cap(label)} en çok satan kategori **{top.Category}**: {Money(top.Total)}. ");
        if (s.ByCategory.Count > 1)
        {
            var rest = s.ByCategory.Skip(1).Take(2).Select((c, i) => $"{i + 2}. {c.Category} ({Money(c.Total)})");
            sb.Append("Ardından " + string.Join(", ", rest) + ".");
        }
        return sb.ToString();
    }

    private async Task<string> StockAlertAsync(SectorTerms terms, CancellationToken ct)
    {
        var items = await _replenishment.GetAsync(30, 7, 30, ct);
        if (items.Count == 0)
            return $"Şu an tükenme riski olan {terms.Product} yok. 👍 Stok seviyelerin güvenli.";

        var top = items.OrderBy(i => i.DaysUntilStockout).Take(3).ToList();
        var lines = top.Select(i =>
            $"**{i.ProductName}** ~{i.DaysUntilStockout} gün (öneri: {Qty(i.SuggestedReorderQty)} {i.Unit})");
        var sb = new System.Text.StringBuilder();
        sb.Append($"{items.Count} {terms.Product} tükenme riski taşıyor. En acilleri: ");
        sb.Append(string.Join("; ", lines) + ". ");
        sb.Append("💡 Bunları Sipariş Önerisi ekranından tedarikçine tek tıkla ısmarlayabilirsin.");
        return sb.ToString();
    }

    /// <summary>Proaktif öneriler/uyarılar — InsightService'ten (düşük stok, geciken alacak, ciro düşüşü…).
    /// InsightService burada new'lenir (aynı rapor/replenishment + saat) → constructor'ı büyütmeden test-dostu.</summary>
    private async Task<string> InsightsAsync(DateTime today, CancellationToken ct)
    {
        var insights = await new InsightService(_reports, _replenishment) { NowUtc = () => today }.GetInsightsAsync(5, ct);
        if (insights.Count == 0)
            return "Şu an acil dikkat gerektiren bir şey görünmüyor. 👍 İşler yolunda görünüyor!";

        var sb = new System.Text.StringBuilder();
        sb.Append("İşte şu an dikkatini çekebilecek konular:");
        foreach (var i in insights)
            sb.Append($"\n\n{i.Icon} **{i.Title}** — {i.Detail}");
        return sb.ToString();
    }

    /// <summary>Dönem belirtilmemişse "bu ay" varsayımını kullanıcıya bildirmemiz gereken (döneme çok
    /// duyarlı) niyetler. daily_summary/receivables/stock_alert/forecast kendi mantıklı varsayımını taşır.</summary>
    private static bool IsPeriodSensitive(string intent) => intent is
        Intents.TopProducts or Intents.TopProfit or Intents.CategorySales or Intents.PaymentBreakdown or Intents.Waste;

    /// <summary>Günlük sohbet/selamlaşma — iş sorusu değil; sıcak, kısa yanıt (Identity: doğal, samimi).</summary>
    private static string Chitchat(string question)
    {
        var n = IntentEngine.Normalize(question);
        if (n.Contains("tesekkur") || n.Contains("sagol") || n.Contains("sag ol") || n.Contains("eyvallah"))
            return "Rica ederim! 🙌 Başka bir şey lazım olursa buradayım.";
        if (n.Contains("gorusuruz") || n.Contains("hosca") || n.Contains("hoscakal") || n.Contains("bay bay"))
            return "Görüşürüz! 👋 İyi işler.";
        if (n.Contains("naber") || n.Contains("nasilsin") || n.Contains("ne haber"))
            return "İyiyim, sorduğun için sağ ol! 😊 Senin için işletmene bakayım mı — satış, kâr, stok, cari?";
        return "Merhaba! 👋 Ben CloudPosGrid asistanınım. Satış, kâr, stok, cari gibi konuları sorabilir " +
               "ya da \"bana öneri ver\" diyebilirsin.";
    }

    /// <summary>Bilgi bankası ("X nedir / ne işe yarar") — statik dokümantasyondan (KnowledgeBase), API'siz.</summary>
    private static string Help(string question)
    {
        var norm = IntentEngine.Normalize(question);
        var article = KnowledgeBase.Find(norm);
        return article?.Body ?? KnowledgeBase.Overview;
    }

    /// <summary>Hipotetik ("X eklesem ne satar") — GERÇEK veri olmadığından dürüst yanıt; uydurma yapmaz.</summary>
    private static string WhatIf(SectorTerms terms) =>
        $"Henüz satmadığın bir {terms.Product} için kesin tahmin yapamam — geçmiş {terms.Sale} verisi olmadan rakam " +
        $"uydurmak yanıltıcı olur. 💡 Bunun yerine mevcut bir {terms.Product}ünü sorabilirsin (ör. \"X bu ay ne kadar sattı?\") " +
        $"ya da benzer {terms.ProductPlural}inin ortalamasına bakıp karar verebilirsin.";

    // ---- Yardımcılar ----

    /// <summary>Tek dönemli niyetler için: dönem yoksa varsayılanı uygula.</summary>
    private static (DateTime from, DateTime to, string label) ResolvePeriod(PeriodResult p, DateTime today, PeriodKind defaultKind)
    {
        if (p.Kind != PeriodKind.None && p.Kind != PeriodKind.Tomorrow)
            return (p.From, p.To, p.Label);
        // Varsayılan: bu ay.
        var f = new DateTime(today.Year, today.Month, 1);
        return (f, f.AddMonths(1), "bu ay");
    }

    /// <summary>Kıyas niyeti için mevcut + önceki eş dönemi granülariteye göre seçer.</summary>
    private static ((DateTime from, DateTime to, string label) cur, (DateTime from, DateTime to, string label) prev, string unit)
        ComparePeriods(PeriodResult p, DateTime today)
    {
        switch (p.Kind)
        {
            case PeriodKind.Today:
            case PeriodKind.Yesterday:
                return ((today, today.AddDays(1), "bugün"),
                        (today.AddDays(-1), today, "dün"), "gün");
            case PeriodKind.ThisWeek:
            case PeriodKind.LastWeek:
                var mon = StartOfWeek(today);
                return ((mon, mon.AddDays(7), "bu hafta"),
                        (mon.AddDays(-7), mon, "geçen hafta"), "hafta");
            default:
                var m1 = new DateTime(today.Year, today.Month, 1);
                return ((m1, m1.AddMonths(1), "bu ay"),
                        (m1.AddMonths(-1), m1, "geçen ay"), "ay");
        }
    }

    private static DateTime StartOfWeek(DateTime d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // Pazartesi=0
        return d.AddDays(-diff).Date;
    }

    /// <summary>İki dönemin kategori cirolarını eşleştirip mutlak değişimi en büyük olanı döndürür.</summary>
    private static (string name, decimal diff)? BiggestCategoryChange(
        List<CategoryBreakdownDto> cur, List<CategoryBreakdownDto> prev)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cur) map[c.Category] = c.Total;
        foreach (var q in prev) map[q.Category] = (map.TryGetValue(q.Category, out var v) ? v : 0) - q.Total;

        (string name, decimal diff)? best = null;
        foreach (var kv in map)
            if (best is null || Math.Abs(kv.Value) > Math.Abs(best.Value.diff))
                best = (kv.Key, kv.Value);
        return best is not null && best.Value.diff != 0 ? best : null;
    }

    private static string Money(decimal v) => "₺" + v.ToString("N0", Tr);

    private static string Qty(decimal q)
        => q == Math.Truncate(q) ? q.ToString("N0", Tr) : q.ToString("0.##", Tr);

    private static string Cap(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], Tr) + s[1..];

    private static string TrDay(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "Pazartesi",
        DayOfWeek.Tuesday => "Salı",
        DayOfWeek.Wednesday => "Çarşamba",
        DayOfWeek.Thursday => "Perşembe",
        DayOfWeek.Friday => "Cuma",
        DayOfWeek.Saturday => "Cumartesi",
        _ => "Pazar",
    };
}
