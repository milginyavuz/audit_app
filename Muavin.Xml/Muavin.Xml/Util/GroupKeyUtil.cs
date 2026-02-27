using System;
using System.IO;

namespace Muavin.Xml.Util
{
    public static class GroupKeyUtil
    {
        public static string NormalizeSourceFile(string? sourceFile)
        {
            if (string.IsNullOrWhiteSpace(sourceFile)) return "_unknown_";
            try { return Path.GetFileName(sourceFile).Trim(); }
            catch { return sourceFile.Trim(); }
        }

        private static bool IsTxtLike(string normalizedSourceFile)
        {
            var s = (normalizedSourceFile ?? "").Trim();
            if (s.Length == 0) return false;
            var ext = Path.GetExtension(s);
            return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSpecialClosingType(string? fisTuru)
        {
            var ft = (fisTuru ?? "").Trim();
            return ft.Equals("Açılış", StringComparison.OrdinalIgnoreCase)
                || ft.Equals("Kapanış", StringComparison.OrdinalIgnoreCase)
                || ft.Equals("Gelir Tablosu Kapanış", StringComparison.OrdinalIgnoreCase)
                || ft.Equals("Yansıtma Kapama", StringComparison.OrdinalIgnoreCase);
        }

        // ✅ TEK DOĞRU GROUP KEY
        public static string Build(string? entryNumber, DateTime? postingDate, string? documentNumber, string? fisTuru, string normalizedSourceFile)
        {
            var no = (entryNumber ?? "").Trim();
            var d = postingDate?.ToString("yyyy-MM-dd") ?? "";

            // Closing/Açılış -> DOC ekleme
            if (IsSpecialClosingType(fisTuru))
                return $"{no}|{d}";

            // TXT/CSV -> DOC ekleme (override tetiklememek için)
            if (IsTxtLike(normalizedSourceFile))
                return $"{no}|{d}";

            var doc = (documentNumber ?? "").Trim();
            return string.IsNullOrWhiteSpace(doc)
                ? $"{no}|{d}"
                : $"{no}|{d}|DOC:{doc}";
        }
    }
}