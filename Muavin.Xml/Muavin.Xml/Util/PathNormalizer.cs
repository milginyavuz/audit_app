using System;
using System.Text.RegularExpressions;

namespace Muavin.Xml.Util
{
    /// <summary>
    /// XML yollarını köke duyarsız ve şema farklarına (schemaref) dayanıklı hale getirir.
    /// </summary>
    public static class PathNormalizer
    {
        // birden fazla slash'ı teke indirmek için
        private static readonly Regex _multiSlash = new Regex("/{2,}", RegexOptions.Compiled);

        public static string Normalize(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return string.Empty;
            p = p.Trim().ToLowerInvariant();

            // sondaki /#text'i at
            if (p.EndsWith("/#text", StringComparison.Ordinal))
                p = p[..^6];

            // adım adım sadeleştir
            // 1) /schemaref/ segmentini yok say
            p = p.Replace("/schemaref/", "/", StringComparison.Ordinal);

            // 2) defter/xbrl öncesi kökleri at: örn /root/defter/xbrl/... -> /defter/xbrl/...
            var idxDefter = p.IndexOf("/defter/", StringComparison.Ordinal);
            if (idxDefter > 0) p = p.Substring(idxDefter);

            // 3) çift / -> /
            p = _multiSlash.Replace(p, "/");

            // 4) sondaki / varsa at
            if (p.Length > 1 && p[^1] == '/') p = p[..^1];

            return p;
        }
    }
}
