namespace CloudPosGrid.Application.Common;

/// <summary>ILIKE aramaları için kullanıcı girdisini güvenli desene çevirir.</summary>
public static class SqlLike
{
    /// <summary>"%term%" deseni üretir; LIKE özel karakterlerini (\ % _) kaçışlar.</summary>
    public static string Contains(string term)
    {
        var escaped = term.Trim()
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        return $"%{escaped}%";
    }
}
