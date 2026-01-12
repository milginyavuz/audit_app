// TxtMuavinParser.cs (UPDATED)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Muavin.Xml.Parsing
{
    public sealed class TxtMuavinParser
    {
        private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

        public sealed record ParseMeta(
            DateTime? MinDate,
            DateTime? MaxDate,
            int DistinctYearMonthCount,
            int ParsedRowCount,
            int SkippedRowCount,
            bool UsedFallbackEncoding,
            string Delimiter
        );

        public ParseMeta LastMeta { get; private set; } =
            new(null, null, 0, 0, 0, false, "");

        private static readonly Regex[] _rxDocNo =
        {
            new(@"\[\s*no\s*[:\-]?\s*(?<n>[A-Z0-9\/\-\.]{2,})\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"(?:fatura|belge|irsaliye)\s*no[:\-]?\s*(?<n>[A-Z0-9\/\-\.]{2,})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"\b(?:inv|ftr|fat|ft)\s*[:\-]?\s*(?<n>[A-Z0-9\/\-\.]{4,})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"\bno[:\-]?\s*(?<n>[A-Z0-9\/\-\.]{4,})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        private static string? TryExtractDocNo(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var rx in _rxDocNo)
            {
                var m = rx.Match(text);
                if (m.Success)
                {
                    var s = m.Groups["n"].Value.Trim();
                    if (s.Length >= 2) return s;
                }
            }
            return null;
        }

        private enum Delim { Unknown, Semicolon, Tab, Comma }

        // ---------- NEW: opening/closing detection helpers (AND rule) ----------
        private static bool ContainsAcilis(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.ToLowerInvariant();
            return t.Contains("açılış") || t.Contains("acilis") || t.Contains("açilis");
        }

        private static bool ContainsKapanis(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.ToLowerInvariant();
            return t.Contains("kapanış") || t.Contains("kapanis") || t.Contains("kapanis");
        }

        private static (string tur, string? tip) InferFisTxt(string? fisTuruField, string? aciklama)
        {
            // Normalize field
            string ft = (fisTuruField ?? "").Trim().ToLowerInvariant();

            bool fieldAcilis = ft.Contains("açılış") || ft.Contains("acilis") || ft.Contains("açilis") || ft.Contains("opening");
            bool fieldKapanis = ft.Contains("kapanış") || ft.Contains("kapanis") || ft.Contains("closing");

            bool descAcilis = ContainsAcilis(aciklama);
            bool descKapanis = ContainsKapanis(aciklama);

            // AND rule:
            if (fieldAcilis && descAcilis) return ("Açılış", "Açılış");
            if (fieldKapanis && descKapanis) return ("Kapanış", "Kapanış");

            // If field empty, allow description-only inference (optional but useful)
            if (string.IsNullOrWhiteSpace(ft))
            {
                if (descAcilis) return ("Açılış", "Açılış");
                if (descKapanis) return ("Kapanış", "Kapanış");
            }

            return ("Mahsup", null);
        }
        // ---------------------------------------------------------------------

        public List<MuavinRow> Parse(string filePath, string companyCode, out int periodYear, out int periodMonth)
        {
            periodYear = 0;
            periodMonth = 0;

            LastMeta = new ParseMeta(null, null, 0, 0, 0, false, "");

            var rows = new List<MuavinRow>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return rows;

            var (reader, usedFallback) = OpenSmartWithMojibakeFallback(filePath);

            using (reader)
            {
                Dictionary<string, int>? headerMap = null;
                Delim delim = Delim.Unknown;

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var trimmed = line.Trim();

                    if (trimmed.Equals("\"Muavin Defter\"", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Equals("Muavin Defter", StringComparison.OrdinalIgnoreCase))
                        continue;

                    delim = DetectDelimiter(trimmed);
                    var headers = SplitSmart(trimmed, delim);

                    bool looksHeader =
                        headers.Any(h => CanonicalHeader(NormalizeKey(h)) == "tarih") &&
                        (headers.Any(h => CanonicalHeader(NormalizeKey(h)) == "hesapkodu") ||
                         headers.Any(h => CanonicalHeader(NormalizeKey(h)) == "hesapadi"));

                    if (!looksHeader) continue;

                    headerMap = headers
                        .Select((h, i) => new { Key = CanonicalHeader(NormalizeKey(h)), Index = i })
                        .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                        .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.OrdinalIgnoreCase);

                    if (!headerMap.ContainsKey("hesapkodu") && headerMap.ContainsKey("hesapadi"))
                        headerMap["hesapkodu"] = 0;

                    break;
                }

                if (headerMap == null || headerMap.Count == 0)
                {
                    LastMeta = new ParseMeta(null, null, 0, 0, 0, usedFallback, delim.ToString());
                    return rows;
                }

                int lineNo = 0;
                int skipped = 0;

                DateTime? min = null;
                DateTime? max = null;
                var ymSet = new HashSet<int>(); // y*100+m

                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = SplitSmart(line, delim);
                    if (parts.Count == 0) continue;

                    string Get(string key)
                    {
                        if (!headerMap.TryGetValue(key, out var ix)) return "";
                        if (ix < 0 || ix >= parts.Count) return "";
                        return parts[ix];
                    }

                    var postingDate = ParseDateFlexible(Get("tarih"));
                    if (postingDate == null)
                    {
                        skipped++;
                        continue;
                    }

                    var d = postingDate.Value.Date;
                    min = min == null || d < min.Value ? d : min;
                    max = max == null || d > max.Value ? d : max;
                    ymSet.Add(d.Year * 100 + d.Month);

                    var hesapKoduRaw = Get("hesapkodu");
                    var hesapAdiRaw = Get("hesapadi");

                    var hesapKodu = string.IsNullOrWhiteSpace(hesapKoduRaw) ? null : hesapKoduRaw.Trim();
                    var hesapAdi = string.IsNullOrWhiteSpace(hesapAdiRaw) ? null : hesapAdiRaw.Trim();

                    var fisTuruField = NullIfWhite(Get("fisturu"));
                    var fisNo = NullIfWhite(Get("fisno")) ?? NullIfWhite(Get("fisnumarasi")) ?? NullIfWhite(Get("fisnumarasi2"));
                    var aciklama = NullIfWhite(Get("aciklama"));

                    decimal borc = 0m, alacak = 0m;

                    bool hasBorc = headerMap.ContainsKey("borc");
                    bool hasAlacak = headerMap.ContainsKey("alacak");

                    if (hasBorc) borc = ParseDecimalFlexible(Get("borc"));
                    if (hasAlacak) alacak = ParseDecimalFlexible(Get("alacak"));

                    if (!hasBorc && !hasAlacak)
                    {
                        var tutar = ParseDecimalFlexible(Get("tutar"));
                        var dc = NormalizeDC(NullIfWhite(Get("debitcredit")) ?? NullIfWhite(Get("borcalacak")) ?? NullIfWhite(Get("dc")));
                        if (dc == "D") borc = tutar;
                        else if (dc == "C") alacak = tutar;
                        else
                        {
                            if (tutar == 0m) { skipped++; continue; }
                            borc = tutar;
                        }
                    }

                    var side = borc > 0m ? "D" : (alacak > 0m ? "C" : "");

                    string entryNumber = (fisNo ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(entryNumber))
                    {
                        entryNumber = BuildStableEntryNumber(
                            postingDate: d,
                            hesapKodu: hesapKodu,
                            borc: borc,
                            alacak: alacak,
                            aciklama: aciklama,
                            fisTuru: fisTuruField,
                            sourceFileName: Path.GetFileName(filePath)
                        );
                    }

                    var docNo = TryExtractDocNo(aciklama);

                    //  AND-rule inference for FisTuru/FisTipi
                    var (fisTuru, fisTipi) = InferFisTxt(fisTuruField, aciklama);

                    rows.Add(new MuavinRow
                    {
                        PostingDate = d,
                        EntryNumber = entryNumber,
                        EntryCounter = ++lineNo,

                        // txt için stabil fiş bazlı GroupKey:
                        // override kalıcılığı için DOC vb. oynak alanlar kesinlikle burada olmamalı.
                        GroupKey = $"{entryNumber}|{d:yyyy-MM-dd}",

                        FisTuru = fisTuru,
                        FisTipi = fisTipi,
                        Aciklama = aciklama,

                        HesapKodu = hesapKodu,
                        HesapAdi = hesapAdi,

                        Borc = borc,
                        Alacak = alacak,
                        Tutar = borc != 0m ? borc : alacak,
                        Side = side,

                        Kebir = GuessKebirFromAccount(hesapKodu),
                        DocumentNumber = docNo
                    });
                }

                if (min.HasValue)
                {
                    periodYear = min.Value.Year;
                    periodMonth = min.Value.Month;
                }

                LastMeta = new ParseMeta(
                    MinDate: min,
                    MaxDate: max,
                    DistinctYearMonthCount: ymSet.Count,
                    ParsedRowCount: rows.Count,
                    SkippedRowCount: skipped,
                    UsedFallbackEncoding: usedFallback,
                    Delimiter: delim.ToString()
                );
            }

            return rows;
        }

        // ---------------- Encoding / Reader ----------------

        private static (StreamReader reader, bool usedFallback) OpenSmartWithMojibakeFallback(string filePath)
        {
            var utf = new StreamReader(
                filePath,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: true
            );

            var peek = new List<string>();
            for (int i = 0; i < 5 && !utf.EndOfStream; i++)
            {
                var l = utf.ReadLine() ?? "";
                peek.Add(l);
            }

            bool mojibake = peek.Any(l => l.Contains('�'));
            utf.Dispose();

            if (!mojibake)
            {
                return (new StreamReader(
                    filePath,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: true
                ), false);
            }

            return (new StreamReader(filePath, Encoding.GetEncoding(1254)), true);
        }

        private static Delim DetectDelimiter(string line)
        {
            if (line.Contains('\t')) return Delim.Tab;
            if (line.Contains(';')) return Delim.Semicolon;
            if (line.Contains(',')) return Delim.Comma;
            return Delim.Semicolon;
        }

        private static List<string> SplitSmart(string line, Delim delim)
        {
            return delim switch
            {
                Delim.Tab => line.Split('\t').Select(Unquote).ToList(),
                Delim.Comma => SplitCsvLike(line, ','),
                _ => SplitCsvLike(line, ';'),
            };
        }

        private static List<string> SplitCsvLike(string line, char sep)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"'); i++; continue;
                    }
                    inQuotes = !inQuotes;
                    continue;
                }

                if (c == sep && !inQuotes)
                {
                    list.Add(Unquote(sb.ToString()));
                    sb.Clear();
                    continue;
                }

                sb.Append(c);
            }

            list.Add(Unquote(sb.ToString()));
            return list;
        }

        private static string Unquote(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
                s = s.Substring(1, s.Length - 2);
            return s.Trim();
        }

        private static string NormalizeKey(string s)
        {
            s = Unquote(s).Trim().ToLowerInvariant();

            s = s.Replace('ı', 'i')
                 .Replace('ğ', 'g')
                 .Replace('ü', 'u')
                 .Replace('ş', 's')
                 .Replace('ö', 'o')
                 .Replace('ç', 'c');

            var filtered = new StringBuilder(s.Length);
            foreach (var ch in s)
                if (char.IsLetterOrDigit(ch))
                    filtered.Append(ch);

            return filtered.ToString();
        }

        private static string CanonicalHeader(string normalized)
        {
            if (normalized.Contains("hesapkodu") || normalized == "kodu") return "hesapkodu";
            if (normalized.Contains("hesapadi") || normalized.Contains("unvan")) return "hesapadi";

            if (normalized == "tarih" || normalized.Contains("fistarihi") || normalized.Contains("islemtarihi"))
                return "tarih";

            if (normalized.Contains("fisturu") || normalized.Contains("fistipi"))
                return "fisturu";

            if (normalized == "fisno" || normalized.Contains("fisno") || normalized.Contains("yevmiye") || normalized.Contains("belgeno"))
                return "fisno";

            if (normalized.Contains("fisnumarasi") || normalized.Contains("fisnumara"))
                return "fisnumarasi";

            if (normalized.Contains("aciklama") || normalized.Contains("detaycomment") || normalized.Contains("comment"))
                return "aciklama";

            if (normalized == "borc" || normalized.Contains("borc")) return "borc";
            if (normalized == "alacak" || normalized.Contains("alacak")) return "alacak";

            if (normalized.Contains("tutar") || normalized.Contains("amount")) return "tutar";

            if (normalized.Contains("debitcredit") || normalized == "dc" || normalized.Contains("borcalacak"))
                return "debitcredit";

            if (normalized.Contains("fisnumarasi2")) return "fisnumarasi2";

            return normalized;
        }

        private static DateTime? ParseDateFlexible(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = Unquote(s);

            string[] formats =
            {
                "dd.MM.yyyy","d.MM.yyyy","dd.M.yyyy","d.M.yyyy",
                "dd/MM/yyyy","d/M/yyyy","d/MM/yyyy","dd/M/yyyy",
                "yyyy-MM-dd","yyyy/M/d","yyyy/MM/dd"
            };

            if (DateTime.TryParseExact(s, formats, Tr, DateTimeStyles.None, out var dt))
                return dt.Date;

            if (DateTime.TryParse(s, Tr, DateTimeStyles.None, out dt))
                return dt.Date;

            return null;
        }

        private static decimal ParseDecimalFlexible(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0m;
            s = Unquote(s);

            if (decimal.TryParse(s, NumberStyles.Any, Tr, out var d))
                return d;

            var normalized = s.Replace(".", "").Replace(",", ".");
            if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                return d;

            return 0m;
        }

        private static string? GuessKebirFromAccount(string? account)
        {
            if (string.IsNullOrWhiteSpace(account)) return null;
            var s = account.Trim();

            var dot = s.IndexOf('.');
            if (dot > 0) return s.Substring(0, dot);

            var dash = s.IndexOf('-');
            if (dash > 0) return s.Substring(0, dash);

            if (s.Length >= 3) return s.Substring(0, 3);
            return s;
        }

        private static string? NullIfWhite(string s)
            => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string NormalizeDC(string? dc)
        {
            if (string.IsNullOrWhiteSpace(dc)) return "";
            dc = dc.Trim().ToUpperInvariant();
            if (dc.StartsWith("D") || dc.StartsWith("B")) return "D";
            if (dc.StartsWith("C") || dc.StartsWith("A")) return "C";
            return dc.Length == 1 ? dc : "";
        }

        private static string BuildStableEntryNumber(
            DateTime postingDate,
            string? hesapKodu,
            decimal borc,
            decimal alacak,
            string? aciklama,
            string? fisTuru,
            string? sourceFileName)
        {
            string normDesc = NormalizeForKey(aciklama);
            if (normDesc.Length > 80) normDesc = normDesc.Substring(0, 80);

            string normAcc = NormalizeForKey(hesapKodu);
            string normFis = NormalizeForKey(fisTuru);
            string normSrc = NormalizeForKey(sourceFileName);

            string payload =
                $"{postingDate:yyyy-MM-dd}|{normAcc}|B:{borc:0.00}|A:{alacak:0.00}|{normFis}|{normDesc}|{normSrc}";

            string h = ShortSha256Hex(payload, 12);
            return $"TXT-{postingDate:yyyyMMdd}-{h}";
        }

        private static string NormalizeForKey(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim().ToLowerInvariant();

            s = s.Replace('ı', 'i')
                 .Replace('ğ', 'g')
                 .Replace('ü', 'u')
                 .Replace('ş', 's')
                 .Replace('ö', 'o')
                 .Replace('ç', 'c');

            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (char.IsWhiteSpace(ch)) sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        private static string ShortSha256Hex(string input, int hexLen)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var hex = Convert.ToHexString(bytes).ToLowerInvariant();
            if (hexLen <= 0 || hexLen >= hex.Length) return hex;
            return hex.Substring(0, hexLen);
        }
    }
}
