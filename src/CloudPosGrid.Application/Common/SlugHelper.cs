using System.Globalization;
using System.Text;

namespace CloudPosGrid.Application.Common;

public static class SlugHelper
{
    /// <summary>Türkçe karakterleri sadeleştirip URL-dostu slug üretir.</summary>
    public static string Slugify(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "isletme";

        input = input.Trim().ToLowerInvariant()
            .Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u')
            .Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c');

        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (char.IsWhiteSpace(c) || c is '-' or '_') sb.Append('-');
        }

        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "isletme" : slug;
    }
}
