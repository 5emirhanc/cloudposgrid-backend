using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Stock;

/// <summary>Terazi barkod şablonu (tenant ayarlarından kurulur).</summary>
public sealed record ScaleBarcodeTemplate(
    bool Enabled, string Prefixes, int ItemDigits, int ValueDigits, int Decimals,
    ScaleEmbedMode Embeds, bool PriceIncludesVat);

/// <summary>Çözülen parçalar: ürün kodu + gömülü değer (ağırlık ya da fiyat).</summary>
public sealed record ScaleBarcodeParts(string ItemCode, decimal Value);

/// <summary>
/// Terazinin bastığı EAN-13 etiketi çözer: [ön ek][ürün kodu][değer][kontrol hanesi].
/// Örn. şablon 28 + 5 + 5, ondalık 3 → "2812345017504" = ürün "12345", 1,750 kg.
/// Saf/statik: HTTP'siz birim testi yazılabilir (SlugHelper/SqlLike deseni).
/// </summary>
public static class ScaleBarcode
{
    /// <summary>Şablona uyuyorsa parçaları döner; uymuyorsa null (çağıran normal barkod aramasına geri düşer).</summary>
    public static ScaleBarcodeParts? TryParse(string? code, ScaleBarcodeTemplate t)
    {
        if (!t.Enabled) return null;
        var c = code?.Trim();
        if (string.IsNullOrEmpty(c) || c.Length != 13) return null;
        foreach (var ch in c) if (ch is < '0' or > '9') return null;

        // Bozuk okuma yanlış ürüne düşmesin: kontrol hanesi tutmayan etiket terazi barkodu sayılmaz.
        if (!IsValidEan13(c)) return null;

        var prefix = MatchPrefix(c, t.Prefixes);
        if (prefix is null) return null;

        var itemDigits = Math.Clamp(t.ItemDigits, 1, 9);
        var valueDigits = Math.Clamp(t.ValueDigits, 1, 9);
        // Ön ek + ürün + değer + kontrol hanesi tam 13'e tamamlanmalı; yoksa şablon bu etikete ait değildir.
        if (prefix.Length + itemDigits + valueDigits + 1 != 13) return null;

        var itemCode = c.Substring(prefix.Length, itemDigits);
        var raw = c.Substring(prefix.Length + itemDigits, valueDigits);
        if (!decimal.TryParse(raw, out var value)) return null;

        var decimals = Math.Clamp(t.Decimals, 0, 4);
        for (var i = 0; i < decimals; i++) value /= 10m;

        return new ScaleBarcodeParts(itemCode, value);
    }

    /// <summary>Kodun başındaki eşleşen ön eki döner (en UZUN eşleşme kazanır: "2" ile "28" karışmasın).</summary>
    private static string? MatchPrefix(string code, string? prefixes)
    {
        if (string.IsNullOrWhiteSpace(prefixes)) return null;
        string? best = null;
        foreach (var p in prefixes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (p.Length == 0 || !code.StartsWith(p, StringComparison.Ordinal)) continue;
            if (best is null || p.Length > best.Length) best = p;
        }
        return best;
    }

    /// <summary>EAN-13 kontrol hanesi doğrulaması (soldan 1-indeks: tek konumlar ×1, çift konumlar ×3).</summary>
    public static bool IsValidEan13(string code)
        => code.Length == 13 && code[12] == CheckDigit(code.AsSpan(0, 12));

    /// <summary>İlk 12 haneden EAN-13 kontrol hanesini hesaplar. Üretici ve doğrulayıcı için TEK kaynak.</summary>
    public static char CheckDigit(ReadOnlySpan<char> first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            var d = first12[i] - '0';
            sum += i % 2 == 0 ? d : d * 3;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }
}
