namespace CloudPosGrid.Application.Modules.Assistant;

/// <summary>
/// CloudPosGrid'i asistana "öğreten" statik bilgi bankası (API'siz). "X nedir / ne işe yarar / nasıl
/// yaparım" gibi sistem sorularına buradan yanıt verilir. Anahtarlar NORMALIZE edilmiş (aksansız/küçük)
/// yazılır ki IntentEngine.Normalize'dan geçmiş soruyla eşleşsin.
/// </summary>
public static class KnowledgeBase
{
    public record Article(string[] Keys, string Body);

    /// <summary>Genel tanıtım — belirli bir konu eşleşmezse kullanılır.</summary>
    public const string Overview =
        "CloudPosGrid, işletmeni tek yerden yöneten bulut tabanlı POS + ön muhasebe sistemidir. Başlıca " +
        "özellikler: hızlı satış (POS) ve barkod, stok takibi, cari (müşteri/tedarikçi) hesapları, fatura ve " +
        "kasa, adisyon & masa yönetimi (kafe/restoran), raporlar ve satış analizi, personel takibi, sipariş " +
        "önerisi, sadakat puanı ve pazaryeri (Trendyol) entegrasyonu. Ekranlar sektörüne göre uyarlanır.";

    public static readonly Article[] Articles =
    [
        new(["stok karti","stok kart","urun karti","urun kart"],
            "Stok kartı bir ürünün kimliğidir: adı, alış fiyatı, satış fiyatı, stok miktarı, minimum stok, " +
            "kategori, barkod ve KDV oranı bilgilerini tutar. 'Ürünler' ekranından eklenir/düzenlenir."),
        new(["cari","musteri hesabi","tedarikci hesabi","veresiye","cari hesap"],
            "Cari, bir müşteri ya da tedarikçidir. Her cari için borç, alacak ve bakiye takibi yapılır; " +
            "veresiye satış ve tahsilatlar buradan izlenir."),
        new(["fatura","faturalama","fatura kesme"],
            "Fatura, bir satış ya da alışın kaydıdır; ürün satırları, KDV ve genel toplamı içerir. Satış " +
            "faturası stoğu düşer, kasaya/cariye yansır. 'Faturalar' ekranından oluşturulur."),
        new(["barkod","barkod okuma","etiket"],
            "Barkod, ürünü hızlı bulmak içindir. POS'ta okutunca ürün sepete eklenir. Barkodsuz ürünlere " +
            "dahili barkod üretebilir, 80mm etiket yazdırabilirsin; terazi barkodu da desteklenir."),
        new(["kdv","vergi","vergiler"],
            "KDV (Katma Değer Vergisi) fiyata eklenen vergidir. Her ürünün bir KDV oranı vardır; fatura ve " +
            "raporlarda KDV hariç (net) ve KDV dahil (brüt) tutarlar ayrı gösterilir."),
        new(["adisyon","masa","masa yonetimi","masalar"],
            "Adisyon, bir masanın açık hesabıdır (kafe/restoran). 'Masalar' ekranından masa açar, ürün " +
            "eklersin; hesap kapanınca satışa/faturaya döner. Masa taşıma ve birleştirme yapılabilir."),
        new(["kasa","gelir gider","nakit akisi","z raporu","gun sonu"],
            "Kasa, nakit ve banka hareketlerini izler. Satış tahsilatları, giderler ve gün sonu (Z) raporu " +
            "buradan yönetilir; vardiya açma/kapama ve kör sayım desteklenir."),
        new(["rapor","raporlama","analiz","satis analizi","kar zarar"],
            "Raporlar; satış, kâr-zarar, finansal özet, kategori/ürün kırılımı ve cari yaşlandırma gibi " +
            "analizleri sunar. Dönemsel karşılaştırma ve Excel/CSV dışa aktarma yapabilirsin."),
        new(["personel","calisan","kullanici","rol","yetki"],
            "Personel, yetkiyle çalışan kullanıcılardır (Sahip, Yönetici, Muhasebe, Kasiyer). Her role göre " +
            "ekran/yetki verilir; kimin ne yaptığı denetim kaydında tutulur."),
        new(["siparis onerisi","stok tahmin","tukenme","replenishment"],
            "Sipariş önerisi, satış hızına göre hangi ürünün ne zaman tükeneceğini tahmin eder ve önerilen " +
            "sipariş miktarını gösterir. Zincir pakete özeldir."),
        new(["sadakat","puan","sadakat puani"],
            "Sadakat puanı, müşterine alışverişinin bir yüzdesi kadar puan kazandırır (1 puan = 1 ₺); puan " +
            "sonraki alışverişte indirim olarak kullanılır."),
        new(["pazaryeri","trendyol","e ticaret"],
            "Pazaryeri entegrasyonu (Trendyol) stok ve siparişleri çift yönlü senkronlar. Zincir pakete özeldir."),
        new(["teklif","proforma"],
            "Teklif (proforma), müşteriye bağlayıcı olmayan fiyat önerisidir; kabul edilirse faturaya dönüşür."),
        new(["tedarikci siparisi","satin alma","mal kabul","alis"],
            "Tedarikçi siparişi, satın alacağın malları önceden ısmarlamandır; mal gelince 'mal kabul' ile " +
            "stoğa ve alış faturasına döner. Yoldaki mal, sipariş önerisinden düşülür."),
        new(["kategori","kategoriler"],
            "Kategori, ürünleri gruplamana yarar (ör. İçecek, Tatlı). Raporlarda kategori bazlı satış/kâr görürsün."),
        new(["randevu","rezervasyon","takvim"],
            "Randevu, kuaför/güzellik gibi işletmeler için müşteri randevularını takvimde yönetir."),
        new(["qr menu","qr","dijital menu"],
            "QR menü, müşterinin telefonuyla okutup menünü görmesini (ve sipariş vermesini) sağlar."),
        new(["fire","zayi","zaiat"],
            "Fire/zayi, bozulan/kırılan/israf olan ürünlerin kaydıdır. Kâr raporunda görünmeyen gizli " +
            "kayıptır; asistana 'bu ay ne kadar fire verdim' diye sorabilirsin."),
        new(["stok sayim","sayim","envanter"],
            "Stok sayımı, fiziki stoğu sistemdekiyle karşılaştırıp farkları düzeltmeni sağlar (kör sayım destekli)."),
        new(["paket","abonelik","zincir","kurumsal","yukselt","fiyat"],
            "CloudPosGrid paketlerle sunulur. Gelişmiş özellikler (sipariş önerisi, pazaryeri, AI Asistan) " +
            "Zincir pakete özeldir; 'Yükselt' ekranından paketini değiştirebilirsin."),
        new(["ai asistan","asistan","yapay zeka","sen kimsin","sen nesin"],
            "Ben CloudPosGrid AI Asistanıyım. Satış, kâr, stok, cari ve fire gibi konularda GERÇEK verilerinle " +
            "Türkçe yanıt veririm; anlamadığım soruları 'Eğitim' bölümünden bana öğretebilirsin. Zincir pakete özelim."),
    ];

    /// <summary>Normalize edilmiş soruya en uygun makaleyi bulur (anahtar örtüşmesi). Yoksa null → Overview.</summary>
    public static Article? Find(string normalizedQuestion)
    {
        Article? best = null;
        int bestScore = 0;
        foreach (var a in Articles)
        {
            int score = 0;
            foreach (var k in a.Keys)
            {
                // Çok kelimeli anahtar: alt dize; tek kelimeli: kelime-sınırlı (yanlış eşleşme olmasın).
                bool hit = k.Contains(' ')
                    ? normalizedQuestion.Contains(k, StringComparison.Ordinal)
                    : (" " + normalizedQuestion + " ").Contains(" " + k + " ", StringComparison.Ordinal);
                if (hit) score += k.Contains(' ') ? 2 : 1; // çok kelimeli eşleşme daha güçlü
            }
            if (score > bestScore) { bestScore = score; best = a; }
        }
        return best;
    }
}
