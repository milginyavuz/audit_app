// TxtMuavinParser.cs (FINAL - MultiSpace fixed-width kayma çözümü dahil)
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

        // MultiSpace = fixed-width (2+ boşluk ayırıcı) -> header pozisyonundan substring okunur
        private enum Delim { Unknown, Semicolon, Tab, Pipe, MultiSpace }

        // ---------- Açılış/Kapanış ----------
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
            string ft = (fisTuruField ?? "").Trim().ToLowerInvariant();

            bool fieldAcilis = ft.Contains("açılış") || ft.Contains("acilis") || ft.Contains("açilis") || ft.Contains("opening");
            bool fieldKapanis = ft.Contains("kapanış") || ft.Contains("kapanis") || ft.Contains("closing");

            bool descAcilis = ContainsAcilis(aciklama);
            bool descKapanis = ContainsKapanis(aciklama);

            if (fieldAcilis && descAcilis) return ("Açılış", "Açılış");
            if (fieldKapanis && descKapanis) return ("Kapanış", "Kapanış");

            if (string.IsNullOrWhiteSpace(ft))
            {
                if (descAcilis) return ("Açılış", "Açılış");
                if (descKapanis) return ("Kapanış", "Kapanış");
            }

            return ("Mahsup", null);
        }

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

                // MultiSpace/fixed-width için header pozisyonları
                int[]? fwStarts = null;
                int[]? fwEnds = null;
                List<string>? headerCellsOriginal = null;
                string? headerLineOriginal = null;

                string? line;

                // ---------------- header bul ----------------
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var trimmed = line.TrimEnd();

                    if (trimmed.Equals("\"Muavin Defter\"", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Equals("Muavin Defter", StringComparison.OrdinalIgnoreCase))
                        continue;

                    delim = DetectDelimiterSmart(trimmed);

                    // header adaylarını al
                    var headers = SplitSmartForHeader(trimmed, delim);

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

                    // MultiSpace ise fixed-width layout çıkar (kolon kaymasını bitirir)
                    if (delim == Delim.MultiSpace)
                    {
                        headerLineOriginal = trimmed;
                        headerCellsOriginal = headers;

                        BuildFixedWidthLayout(headerLineOriginal, headerCellsOriginal, out fwStarts, out fwEnds);
                    }

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
                var ymSet = new HashSet<int>();

                // ---------------- data oku ----------------
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string Get(string key)
                    {
                        if (!headerMap.TryGetValue(key, out var ix)) return "";

                        // Fixed-width okuma (MultiSpace): header pozisyonlarına göre substring
                        if (delim == Delim.MultiSpace && fwStarts != null && fwEnds != null)
                        {
                            if (ix < 0 || ix >= fwStarts.Length) return "";
                            int s = fwStarts[ix];
                            int e = fwEnds[ix];
                            if (s >= line.Length) return "";
                            if (e > line.Length) e = line.Length;
                            if (e <= s) return "";
                            return line.Substring(s, e - s).Trim();
                        }

                        // Diğer delimiterlar: split ile oku
                        var parts = SplitSmart(line, delim);
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

                    if (string.IsNullOrWhiteSpace(hesapKodu) && string.IsNullOrWhiteSpace(hesapAdi))
                    {
                        skipped++;
                        continue;
                    }

                    var fisTuruField = NullIfWhite(Get("fisturu"));
                    var fisNo = NullIfWhite(Get("fisno")) ?? NullIfWhite(Get("fisnumarasi")) ?? NullIfWhite(Get("fisnumarasi2"));
                    var aciklama = NullIfWhite(Get("aciklama"));

                    decimal borc = 0m, alacak = 0m;

                    bool hasBorc = headerMap.ContainsKey("borc");
                    bool hasAlacak = headerMap.ContainsKey("alacak");

                    if (hasBorc) borc = ParseDecimalFlexible(Get("borc"));
                    if (hasAlacak) alacak = ParseDecimalFlexible(Get("alacak"));

                    // hem borç hem alacak doluysa normalize et
                    if (borc > 0m && alacak > 0m)
                    {
                        if (borc > alacak) alacak = 0m;
                        else borc = 0m;
                    }

                    // Eğer BORC/ALACAK kolonları yoksa: tutar + dc ile türet
                    if (!hasBorc && !hasAlacak)
                    {
                        var tutarRaw = Get("tutar");
                        var tutar = ParseDecimalFlexible(tutarRaw);

                        var dc = NormalizeDC(
                            NullIfWhite(Get("debitcredit")) ??
                            NullIfWhite(Get("borcalacak")) ??
                            NullIfWhite(Get("dc"))
                        );

                        if (dc == "D") borc = Math.Abs(tutar);
                        else if (dc == "C") alacak = Math.Abs(tutar);
                        else
                        {
                            if (tutar == 0m) { skipped++; continue; }
                            if (tutar < 0m) alacak = Math.Abs(tutar);
                            else borc = tutar;
                        }
                    }

                    // Eğer BORC/ALACAK kolonları var ama ikisi de 0 ise:
                    // (TUM_HESAPLAR gibi bazı dosyalarda tutar kolonu da olabilir)
                    if (borc == 0m && alacak == 0m && headerMap.ContainsKey("tutar"))
                    {
                        var t = ParseDecimalFlexible(Get("tutar"));
                        if (t != 0m)
                        {
                            if (t < 0m) alacak = Math.Abs(t);
                            else borc = t;
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
                    var (fisTuru, fisTipi) = InferFisTxt(fisTuruField, aciklama);

                    rows.Add(new MuavinRow
                    {
                        PostingDate = d,
                        EntryNumber = entryNumber,
                        EntryCounter = ++lineNo,

                        // TXT için stabil groupkey (override kalıcılığı)
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
                peek.Add(utf.ReadLine() ?? "");

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

        private static Delim DetectDelimiterSmart(string headerLine)
        {
            if (headerLine.Contains('\t')) return Delim.Tab;
            if (headerLine.TrimStart().StartsWith("|")) return Delim.Pipe;
            if (headerLine.Contains(';')) return Delim.Semicolon;

            // fixed-width: 2+ boşlukla ayrılan başlıklar
            if (Regex.IsMatch(headerLine, @"\S\s{2,}\S")) return Delim.MultiSpace;

            // (Virgül yok: TR ondalık virgül yüzünden kolon bozar)
            return Delim.MultiSpace;
        }

        // Header split: MultiSpace’de split yapıyoruz, ama data satırları substring ile okunacak
        private static List<string> SplitSmartForHeader(string line, Delim delim)
        {
            return delim switch
            {
                Delim.Tab => line.Split('\t').Select(Unquote).ToList(),
                Delim.Pipe => SplitPipeRow(line),
                Delim.MultiSpace => Regex.Split(line.Trim(), @"\s{2,}").Select(x => Unquote(x).Trim()).Where(x => x.Length > 0).ToList(),
                _ => SplitCsvLike(line, ';'),
            };
        }

        private static List<string> SplitSmart(string line, Delim delim)
        {
            return delim switch
            {
                Delim.Tab => line.Split('\t').Select(Unquote).ToList(),
                Delim.Pipe => SplitPipeRow(line),
                Delim.MultiSpace => Regex.Split(line.Trim(), @"\s{2,}").Select(x => Unquote(x).Trim()).ToList(),
                _ => SplitCsvLike(line, ';'),
            };
        }

        private static List<string> SplitPipeRow(string line)
        {
            // | A | B | C | -> ["A","B","C"]
            var t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);

            return t.Split('|')
                    .Select(x => Unquote(x).Trim())
                    .ToList();
        }

        // FIXED-WIDTH: header cell pozisyonlarına göre start/end çıkar
        private static void BuildFixedWidthLayout(string headerLine, List<string> headerCells, out int[] starts, out int[] ends)
        {
            var sList = new List<int>();
            int cursor = 0;

            for (int i = 0; i < headerCells.Count; i++)
            {
                var cell = headerCells[i];
                int ix = headerLine.IndexOf(cell, cursor, StringComparison.Ordinal);
                if (ix < 0)
                {
                    // bulamazsa: cursor'u kullan (fail-safe)
                    ix = cursor;
                }

                sList.Add(ix);
                cursor = ix + cell.Length;
            }

            starts = sList.ToArray();
            ends = new int[starts.Length];
            for (int i = 0; i < starts.Length; i++)
            {
                ends[i] = (i == starts.Length - 1) ? headerLine.Length : starts[i + 1];
            }
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

        // Tek yerde kanonik eşleme (sende önce çok tekrar vardı; temizledim)
        private static string CanonicalHeader(string normalized)
        {
            // Tarih
            if (normalized == "tarih" || normalized.Contains("fistarihi") || normalized.Contains("islemtarihi") ||
                normalized.Contains("belgetarih") || normalized.Contains("belgetrh") ||
                normalized.Contains("kayittarih") || normalized.Contains("kayittrh"))
                return "tarih";

            // Hesap
            if (normalized.Contains("hesapkodu") || normalized == "kodu" ||
                normalized.Contains("anahesap") || (normalized.Contains("ana") && normalized.Contains("hesap")) ||
                normalized.Contains("hesapno"))
                return "hesapkodu";

            if (normalized.Contains("hesapadi") || normalized.Contains("unvan") ||
                normalized.Contains("hesapaciklama") || normalized.Contains("hesapaciklamasi"))
                return "hesapadi";

            // Fiş/Belge no
            if (normalized == "fisno" || normalized.Contains("fisno") ||
                (normalized.Contains("yevmiye") && normalized.Contains("no")) ||
                normalized.Contains("belgeno") || (normalized.Contains("belge") && normalized.Contains("no")))
                return "fisno";

            if (normalized.Contains("fisnumarasi2")) return "fisnumarasi2";
            if (normalized.Contains("fisnumarasi") || normalized.Contains("fisnumara")) return "fisnumarasi";

            // Fiş türü
            if (normalized.Contains("fisturu") || normalized.Contains("fistipi")) return "fisturu";

            // Açıklama
            if (normalized.Contains("aciklama") || normalized.Contains("comment") || normalized.Contains("detaycomment") ||
                normalized.Contains("metin") || normalized.Contains("belgeaciklama") || normalized.Contains("belgeaciklamasi"))
                return "aciklama";

            // Borç/Alacak (özellikle "Borç Tutar", "Alacak Tutar")
            if (normalized.Contains("borctutar") || (normalized.Contains("borc") && normalized.Contains("tutar")))
                return "borc";

            if (normalized.Contains("alacaktutar") || (normalized.Contains("alacak") && normalized.Contains("tutar")))
                return "alacak";

            if (normalized == "borc" || normalized.Contains("borc")) return "borc";
            if (normalized == "alacak" || normalized.Contains("alacak")) return "alacak";

            // Tutar / Amount
            if (normalized.Contains("tutarup")) return "tutar";
            if (normalized.Contains("tutar") || normalized.Contains("amount")) return "tutar";

            // DC
            if (normalized.Contains("debitcredit") || normalized == "dc" || normalized.Contains("borcalacak"))
                return "debitcredit";

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
            s = Unquote(s).Trim();

            bool parenNeg = s.StartsWith("(") && s.EndsWith(")");
            if (parenNeg) s = s.Substring(1, s.Length - 2).Trim();

            bool trailingNeg = s.EndsWith("-");
            if (trailingNeg) s = s.Substring(0, s.Length - 1).Trim();

            s = s.Replace("TL", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("TRY", "", StringComparison.OrdinalIgnoreCase)
                 .Trim();

            if (decimal.TryParse(s, NumberStyles.Any, Tr, out var d))
            {
                if (parenNeg || trailingNeg) d = -d;
                return d;
            }

            var normalized = s.Replace(".", "").Replace(",", ".");
            if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
            {
                if (parenNeg || trailingNeg) d = -d;
                return d;
            }

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