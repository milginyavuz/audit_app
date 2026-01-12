using System;
using System.Text.RegularExpressions;

namespace Muavin.Xml.Util
{
    /// <summary>
    /// xml yollarını köke duyarsız ve şema farklarına (schemaref) dayanıklı hale getirir
    /// </summary>
    public static class PathNormalizer
    {
        // birden fazla slashı teke indirmek için
        private static readonly Regex _multiSlash = new Regex("/{2,}", RegexOptions.Compiled);

        public static string Normalize(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return string.Empty;
            p = p.Trim().ToLowerInvariant();

            // sondaki /#text i at
            if (p.EndsWith("/#text", StringComparison.Ordinal))
                p = p[..^6];

            // /schemaref/ segmentini yok say
            p = p.Replace("/schemaref/", "/", StringComparison.Ordinal);

            // defter/xbrl öncesi kökleri at
            var idxDefter = p.IndexOf("/defter/", StringComparison.Ordinal);
            if (idxDefter > 0) p = p.Substring(idxDefter);

            // çift / -> /
            p = _multiSlash.Replace(p, "/");

            // sondaki / varsa at
            if (p.Length > 1 && p[^1] == '/') p = p[..^1];

            return p;
        }
    }
}
