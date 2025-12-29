// EdefterParser.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Muavin.Xml.Util;

namespace Muavin.Xml.Parsing
{
    public sealed class EdefterParser
    {
        private static readonly CultureInfo TR = CultureInfo.GetCultureInfo("tr-TR");

        // ===================== COMPANY INFO =====================
        public sealed record CompanyInfo(string? TaxId, string? EntityName);

        /// <summary>
        /// e-Defter XML içinden şirket bilgisi okur (prefix/namespace bağımsız).
        /// - Ünvan:   <gl-bus:entityName>
        /// - VergiNo: <gl-bus:taxID>
        /// </summary>
        public CompanyInfo ParseCompanyInfo(string xmlPath)
        {
            if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath))
                return new CompanyInfo(null, null);

            string? entityName = null;
            string? taxId = null;

            using var fs = File.OpenRead(xmlPath);
            using var xr = XmlReader.Create(fs, SecureReaderSettings());

            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element)
                    continue;

                if (entityName == null && xr.LocalName.Equals("entityName", StringComparison.OrdinalIgnoreCase))
                {
                    var v = xr.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(v))
                        entityName = v.Trim();
                    continue;
                }

                if (taxId == null && xr.LocalName.Equals("taxID", StringComparison.OrdinalIgnoreCase))
                {
                    var v = xr.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(v))
                        taxId = v.Trim();
                    continue;
                }

                if (entityName != null && taxId != null)
                    break;
            }

            if (string.IsNullOrWhiteSpace(taxId)) taxId = null;
            if (string.IsNullOrWhiteSpace(entityName)) entityName = null;

            return new CompanyInfo(taxId, entityName);
        }

        // Backward compatibility
        public CompanyInfo ReadCompanyInfo(string xmlPath) => ParseCompanyInfo(xmlPath);

        private static XmlReaderSettings SecureReaderSettings()
            => new()
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };

        // --- küçük yardımcılar ---
        private static string? TrimZeros(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            var s = raw.TrimStart('0');
            return string.IsNullOrEmpty(s) ? "0" : s;
        }

        private static readonly System.Text.RegularExpressions.Regex[] _rxDocNo =
        {
            new System.Text.RegularExpressions.Regex(@"(?:fatura|belge|irsaliye)\s*no[:\-]?\s*([A-Z0-9\/\-\.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            new System.Text.RegularExpressions.Regex(@"\b(?:inv|ftr|fat|ft)\s*[:\-]?\s*([A-Z0-9\/\-\.]{4,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            new System.Text.RegularExpressions.Regex(@"\bno[:\-]?\s*([A-Z0-9\/\-\.]{4,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        };

        private static string? TryExtractDocNo(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var rx in _rxDocNo)
            {
                var m = rx.Match(text);
                if (m.Success)
                {
                    var s = m.Groups[1].Value.Trim();
                    if (s.Length >= 4) return s;
                }
            }
            return null;
        }

        private static string Combine(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a)) return b?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(b)) return a.Trim();
            var left = a.Trim();
            return b!.Contains(left, StringComparison.OrdinalIgnoreCase) ? b.Trim() : $"{left} {b!.Trim()}";
        }

        private static string NormalizeDC(string? dc)
        {
            if (string.IsNullOrWhiteSpace(dc)) return "";
            dc = dc.Trim().ToUpperInvariant();
            if (dc.StartsWith("D")) return "D";
            if (dc.StartsWith("C") || dc.StartsWith("A")) return "C";
            return dc.Length == 1 ? dc : "";
        }

        private static string? BuildAccountCode(string? mainId, string? subId)
        {
            if (string.IsNullOrWhiteSpace(mainId) && string.IsNullOrWhiteSpace(subId))
                return null;

            mainId = mainId?.Trim();
            subId = subId?.Trim();

            if (!string.IsNullOrEmpty(mainId))
                mainId = mainId.Replace(".", "-");
            if (!string.IsNullOrEmpty(subId))
                subId = subId.Replace(".", "-");

            string? full;

            if (!string.IsNullOrWhiteSpace(subId))
            {
                if (!string.IsNullOrEmpty(mainId) &&
                    subId.StartsWith(mainId, StringComparison.Ordinal))
                {
                    full = subId;
                }
                else
                {
                    full = string.IsNullOrEmpty(mainId) ? subId : $"{mainId}-{subId}";
                }
            }
            else
            {
                full = mainId;
            }

            if (string.IsNullOrWhiteSpace(full))
                return null;

            full = System.Text.RegularExpressions.Regex.Replace(
                full,
                @"^(\d{3})-\1-(.+)$",
                "$1-$2"
            );

            return full;
        }

        private static string? GuessKebir(string? account)
        {
            if (string.IsNullOrWhiteSpace(account)) return null;
            var s = account.Trim();

            var idx = s.IndexOfAny(new[] { '-', '.' });
            if (idx > 0) return s.Substring(0, idx);
            if (s.Length >= 3) return s.Substring(0, 3);
            return s;
        }

        private static (string? tur, string? tip) InferFis(string? aciklama)
        {
            if (string.IsNullOrWhiteSpace(aciklama)) return ("Mahsup", null);
            var t = aciklama.ToLowerInvariant();
            if (t.Contains("açılış") || t.Contains("acilis")) return ("Açılış", "Açılış");
            if (t.Contains("kapanış") || t.Contains("kapanis")) return ("Kapanış", "Kapanış");
            return ("Mahsup", null);
        }

        private static DateTime? ParseDate(string? txt)
        {
            if (string.IsNullOrWhiteSpace(txt)) return null;
            var t = txt.Trim();
            if (DateTime.TryParse(t, TR, DateTimeStyles.None, out var d)) return d;
            if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        private static decimal ParseDecimal(string? txt)
        {
            if (string.IsNullOrWhiteSpace(txt)) return 0m;
            var t = txt.Trim();
            if (decimal.TryParse(t, NumberStyles.Number, TR, out var v)) return v;
            if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out v)) return v;
            return 0m;
        }

        private static ISet<string> NormSet(IEnumerable<string> paths)
            => new HashSet<string>(paths.Select(PathNormalizer.Normalize).Where(s => s.Length > 0), StringComparer.Ordinal);

        // ===================== NEW: stable EntryNumber for XML if missing =====================
        private static string BuildStableEntryNumber(DateTime postingDate, string? hesapKodu, decimal borc, decimal alacak, string? aciklama, int entryCounter)
        {
            string normDesc = NormalizeForKey(aciklama);
            if (normDesc.Length > 80) normDesc = normDesc.Substring(0, 80);

            string normAcc = NormalizeForKey(hesapKodu);
            string payload = $"{postingDate:yyyy-MM-dd}|{normAcc}|B:{borc:0.00}|A:{alacak:0.00}|{entryCounter}|{normDesc}";

            var h = ShortSha256Hex(payload, 12);
            return $"XML-{postingDate:yyyyMMdd}-{h}";
        }

        private static string NormalizeForKey(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim().ToLowerInvariant()
                 .Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u')
                 .Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c');

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

        private static (string tur, string? tip) InferFisXml(string? aciklama, DateTime postingDate)
        {
            bool descAcilis = !string.IsNullOrWhiteSpace(aciklama) &&
                              (aciklama.Contains("açılış", StringComparison.OrdinalIgnoreCase) ||
                               aciklama.Contains("acilis", StringComparison.OrdinalIgnoreCase));

            bool descKapanis = !string.IsNullOrWhiteSpace(aciklama) &&
                               (aciklama.Contains("kapanış", StringComparison.OrdinalIgnoreCase) ||
                                aciklama.Contains("kapanis", StringComparison.OrdinalIgnoreCase));

            // AND kuralı: açıklama + tarih sinyali
            if (descAcilis && postingDate.Month == 1 && postingDate.Day == 1)
                return ("Açılış", "Açılış");

            if (descKapanis && postingDate.Month == 12 && postingDate.Day == 31)
                return ("Kapanış", "Kapanış");

            return ("Mahsup", null);
        }

        public IEnumerable<MuavinRow> Parse(string xmlPath)
        {
            var fmap = FieldMap.Current;

            var P_HEADER_ENTRYNO = NormSet(fmap.Get("Header.EntryNumber"));
            var P_HEADER_ENTRYCOUNTER = NormSet(fmap.Get("Header.EntryCounter"));
            var P_HEADER_POSTINGDATE = NormSet(fmap.Get("Header.PostingDate"));
            var P_HEADER_DESCRIPTION = NormSet(fmap.Get("Header.Description"));
            var P_HEADER_DOCUMENTNUMBER = NormSet(fmap.Get("Header.DocumentNumber"));

            var P_DETAIL_MAINID = NormSet(fmap.Get("Detail.AccountMainID"));
            var P_DETAIL_MAINDESC = NormSet(fmap.Get("Detail.AccountMainDescription"));
            var P_DETAIL_SUBID = NormSet(fmap.Get("Detail.AccountSubID"));
            var P_DETAIL_SUBDESC = NormSet(fmap.Get("Detail.AccountSubDescription"));
            var P_DETAIL_DEBITCREDIT = NormSet(fmap.Get("Detail.DebitCreditCode"));
            var P_DETAIL_AMOUNT = NormSet(fmap.Get("Detail.Amount"));
            var P_DETAIL_DOCNUMBER = NormSet(fmap.Get("Detail.DocumentNumber"));
            var P_DETAIL_DESCRIPTION = NormSet(fmap.Get("Detail.Description"));
            var P_DETAIL_ENTRYCOUNTER = NormSet(fmap.Get("Detail.EntryCounter"));
            var P_DETAIL_POSTINGDATE = NormSet(fmap.Get("Detail.PostingDate"));

            Logger.WriteLine("[FMAP] loaded and normalized.");

            var rows = new List<MuavinRow>();

            bool inDetail = false;

            string? h_entryNo = null, h_headerNote = null, h_docNo = null;
            DateTime? h_date = null;
            int h_detailIndex = 0;

            string? d_mainId = null, d_mainDesc = null, d_subId = null, d_subDesc = null, d_dc = null, d_note = null;
            decimal d_amount = 0m;
            int? d_lineNo = null;
            DateTime? d_postingDate = null;

            void ResetHeader()
            {
                h_entryNo = null;
                h_headerNote = null;
                h_docNo = null;
                h_date = null;
                h_detailIndex = 0;
            }

            void ResetDetail()
            {
                d_mainId = d_mainDesc = d_subId = d_subDesc = d_dc = d_note = null;
                d_amount = 0m;
                d_lineNo = null;
                d_postingDate = null;
            }

            void FlushDetail()
            {
                // hesap bilgisi yoksa satır oluşturma
                if (string.IsNullOrWhiteSpace(d_mainId) && string.IsNullOrWhiteSpace(d_subId) && string.IsNullOrWhiteSpace(d_dc))
                {
                    ResetDetail();
                    return;
                }

                var posting = (h_date ?? d_postingDate)?.Date;
                if (posting == null)
                {
                    ResetDetail();
                    return;
                }

                var dc = NormalizeDC(d_dc);

                var row = new MuavinRow
                {
                    EntryNumberRaw = h_entryNo,
                    EntryNumber = TrimZeros(h_entryNo),
                    PostingDate = posting,
                    DocumentNumber = h_docNo,
                    Aciklama = Combine(h_headerNote, d_note),

                    AccountMainID = d_mainId,
                    AccountMainDescription = d_mainDesc,
                    AccountSubID = d_subId,
                    AccountSubDescription = d_subDesc,

                    DebitCreditCode = dc,
                    Amount = d_amount,
                    Tutar = d_amount,

                    EntryCounter = d_lineNo ?? (h_detailIndex > 0 ? h_detailIndex : 1)
                };

                // Document no fallback
                if (!string.IsNullOrWhiteSpace(row.Aciklama) && string.IsNullOrWhiteSpace(row.DocumentNumber))
                {
                    var doc = TryExtractDocNo(row.Aciklama);
                    if (!string.IsNullOrWhiteSpace(doc)) row.DocumentNumber = doc;
                }

                // hesap/kebir
                row.HesapKodu = BuildAccountCode(d_mainId, d_subId);
                row.HesapAdi = Combine(d_mainDesc, d_subDesc);
                row.Kebir = GuessKebir(row.HesapKodu);

                // Borç / Alacak
                if (row.DebitCreditCode == "D") { row.Borc = d_amount; row.Alacak = 0m; }
                else if (row.DebitCreditCode == "C") { row.Borc = 0m; row.Alacak = d_amount; }
                else { row.Borc = 0m; row.Alacak = 0m; }

                // EntryNumber yoksa stabil üret
                if (string.IsNullOrWhiteSpace(row.EntryNumber))
                {
                    row.EntryNumber = BuildStableEntryNumber(
                        postingDate: posting.Value,
                        hesapKodu: row.HesapKodu,
                        borc: row.Borc,
                        alacak: row.Alacak,
                        aciklama: row.Aciklama,
                        entryCounter: row.EntryCounter ?? 1
                    );
                }

                (row.FisTuru, row.FisTipi) = InferFisXml(row.Aciklama, posting.Value);


                rows.Add(row);
                ResetDetail();
            }

            using var fs = File.OpenRead(xmlPath);
            using var xr = XmlReader.Create(fs, SecureReaderSettings());

            var nsb = new NamespacePathBuilder();
            var missCache = new HashSet<string>(StringComparer.Ordinal);
            int hit = 0, miss = 0;

            while (xr.Read())
            {
                switch (xr.NodeType)
                {
                    case XmlNodeType.Element:
                        nsb.Push(xr.LocalName);

                        if (string.Equals(xr.LocalName, "entryheader", StringComparison.OrdinalIgnoreCase))
                        {
                            inDetail = false;
                            ResetHeader();
                        }
                        else if (string.Equals(xr.LocalName, "entrydetail", StringComparison.OrdinalIgnoreCase))
                        {
                            inDetail = true;
                            h_detailIndex++;
                        }
                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        {
                            var rawPath = nsb.Path;
                            var path = PathNormalizer.Normalize(rawPath);
                            var val = xr.Value?.Trim();
                            if (string.IsNullOrEmpty(val)) break;

                            bool matched = false;

                            // HEADER
                            if (!matched && P_HEADER_ENTRYNO.Contains(path)) { h_entryNo = val; matched = true; }
                            if (!matched && P_HEADER_POSTINGDATE.Contains(path)) { h_date = ParseDate(val); matched = true; }
                            if (!matched && P_HEADER_DESCRIPTION.Contains(path)) { h_headerNote = Combine(h_headerNote, val); matched = true; }
                            if (!matched && P_HEADER_ENTRYCOUNTER.Contains(path)) { matched = true; }
                            if (!matched && P_HEADER_DOCUMENTNUMBER.Contains(path)) { h_docNo = val; matched = true; }

                            // DETAIL
                            if (!matched && P_DETAIL_MAINID.Contains(path)) { d_mainId = val; matched = true; }
                            if (!matched && P_DETAIL_MAINDESC.Contains(path)) { d_mainDesc = val; matched = true; }
                            if (!matched && P_DETAIL_SUBID.Contains(path)) { d_subId = val; matched = true; }
                            if (!matched && P_DETAIL_SUBDESC.Contains(path)) { d_subDesc = val; matched = true; }
                            if (!matched && P_DETAIL_DEBITCREDIT.Contains(path)) { d_dc = val; matched = true; }
                            if (!matched && P_DETAIL_AMOUNT.Contains(path)) { d_amount = ParseDecimal(val); matched = true; }
                            if (!matched && P_DETAIL_DOCNUMBER.Contains(path)) { h_docNo = val; matched = true; }
                            if (!matched && P_DETAIL_DESCRIPTION.Contains(path)) { d_note = Combine(d_note, val); matched = true; }

                            if (!matched && P_DETAIL_ENTRYCOUNTER.Contains(path))
                            {
                                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) d_lineNo = n;
                                matched = true;
                            }

                            if (!matched && P_DETAIL_POSTINGDATE.Contains(path)) { d_postingDate = ParseDate(val); matched = true; }

                            // fallback (zayıf eşleştirme)
                            if (!matched && inDetail)
                            {
                                if (path.EndsWith("/amount", StringComparison.Ordinal)) { d_amount = ParseDecimal(val); matched = true; }
                                else if (path.EndsWith("/debitcreditcode", StringComparison.Ordinal)) { d_dc = val; matched = true; }
                                else if (path.EndsWith("/accountmainid", StringComparison.Ordinal)) { d_mainId = val; matched = true; }
                                else if (path.EndsWith("/accountsubid", StringComparison.Ordinal)) { d_subId = val; matched = true; }
                                else if (path.EndsWith("/documentnumber", StringComparison.Ordinal) ||
                                         path.EndsWith("/documentreference", StringComparison.Ordinal)) { h_docNo = val; matched = true; }
                                else if (path.EndsWith("/postingdate", StringComparison.Ordinal) ||
                                         path.EndsWith("/documentdate", StringComparison.Ordinal)) { d_postingDate = ParseDate(val); matched = true; }
                                else if (path.EndsWith("/detailcomment", StringComparison.Ordinal) ||
                                         path.EndsWith("/documenttypedescription", StringComparison.Ordinal)) { d_note = Combine(d_note, val); matched = true; }
                            }

                            if (matched) hit++;
                            else
                            {
                                if (missCache.Add(path))
                                    Logger.WriteLine("MISS " + path);
                                miss++;
                            }
                            break;
                        }

                    case XmlNodeType.EndElement:
                        if (string.Equals(xr.LocalName, "entrydetail", StringComparison.OrdinalIgnoreCase))
                        {
                            FlushDetail();
                            inDetail = false;
                        }
                        nsb.Pop();
                        break;
                }
            }

            FlushDetail();

            Logger.WriteLine($"[STATS] hits={hit}, misses={miss}, rows={rows.Count}");
            return rows;
        }
    }
}
