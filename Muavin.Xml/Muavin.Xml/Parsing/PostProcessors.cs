// PostProcessors.cs
using ClosedXML.Excel;
using Muavin.Xml.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Muavin.Xml.Parsing
{
    public static class PostProcessors
    {
        /// <summary>
        /// aynı fişte (EntryNumber+Tarih [+Belge]) bulunan hareketler için Karşı Hesap doldurur
        /// hızlı sürüm: grup bazında kebir setlerini 1 kez hesaplar stringleri cacheler
        /// alsoAccountCodes=true ise ayrıca ContraHesapCsv (hesap kodları) ve ContraKebirCsv de doldurur
        /// </summary>
        public static void FillContraAccounts(
            IList<MuavinRow> rows,
            string? normalizedSourceFile = null,
            bool alsoAccountCodes = false)
        {
            if (rows is null || rows.Count == 0) return;

            // Tek doğru GroupKey üretimi: normalizedSourceFile normalize edilmiş olmalı
            // (örn: GroupKeyUtil.NormalizeSourceFile(file))
            var normalizedSrc = GroupKeyUtil.NormalizeSourceFile(normalizedSourceFile);

            // önce group key ve taraf (D/C)
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];

                r.Side = (r.Borc > 0m) ? "D" : (r.Alacak > 0m) ? "C" : "";

                // DB’den gelen satırlarda GroupKey zaten dolu olabilir; boşsa üret.
                if (string.IsNullOrWhiteSpace(r.GroupKey))
                    r.GroupKey = GroupKeyUtil.Build(r.EntryNumber, r.PostingDate, r.DocumentNumber, r.FisTuru, normalizedSrc);
            }


            // grup (fiş) bazında 
            // Dictionary<groupKey, list indices> – index listesi kopya oluşturmayı azaltır
            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                var key = rows[i].GroupKey ?? "";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<int>(8);
                    groups[key] = list;
                }
                list.Add(i);
            }

            // her grupta kebir setlerini 1 kez çıkarıp cacheli stringlerden yazar
            foreach (var kv in groups)
            {
                var idxs = kv.Value;

                // D ve C tarafları
                var debKebirSet = new HashSet<string>(StringComparer.Ordinal);
                var crdKebirSet = new HashSet<string>(StringComparer.Ordinal);

                // hesap kodu setleri
                HashSet<string>? debCodeSet = alsoAccountCodes ? new HashSet<string>(StringComparer.Ordinal) : null;
                HashSet<string>? crdCodeSet = alsoAccountCodes ? new HashSet<string>(StringComparer.Ordinal) : null;

                // setleri doldur
                foreach (var i in idxs)
                {
                    var r = rows[i];
                    var kebir = (r.Kebir ?? "").Trim();
                    var code = (r.HesapKodu ?? "").Trim();

                    if (r.Side == "D")
                    {
                        if (kebir.Length > 0) debKebirSet.Add(kebir);
                        if (alsoAccountCodes && code.Length > 0) debCodeSet!.Add(code);
                    }
                    else if (r.Side == "C")
                    {
                        if (kebir.Length > 0) crdKebirSet.Add(kebir);
                        if (alsoAccountCodes && code.Length > 0) crdCodeSet!.Add(code);
                    }
                }

                // sıralı listeler
                static string JoinSorted(HashSet<string> hs, string sep)
                {
                    if (hs.Count == 0) return string.Empty;
                    var arr = hs.ToArray();
                    Array.Sort(arr, StringComparer.Ordinal);
                    return string.Join(sep, arr);
                }

                // taban stringler (tam set)
                var crdKebirAll = JoinSorted(crdKebirSet, " | ");
                var debKebirAll = JoinSorted(debKebirSet, " | ");

                // kebir çıkarılmış durumlar için küçük cache
                // grupta hem borçta hem alacakta aynı kebir varsa kendi kebirini hariç bırakmak gerekiyor
                var crdMinusCache = new Dictionary<string, string>(StringComparer.Ordinal);
                var debMinusCache = new Dictionary<string, string>(StringComparer.Ordinal);

                string BuildMinusString(HashSet<string> src, string mine)
                {
                    // mine sette yoksa doğrudan tam string dönebiliriz
                    if (!src.Contains(mine)) return JoinSorted(src, " | ");
                    // varsa 1 kez hesaplayıp cachele
                    var key = mine;
                    var dict = ReferenceEquals(src, crdKebirSet) ? crdMinusCache : debMinusCache;
                    if (dict.TryGetValue(key, out var cached)) return cached;

                    if (src.Count == 1)
                    {
                        dict[key] = string.Empty;
                        return string.Empty;
                    }

                    // hızlı kopya ve çıkarma
                    var tmp = new List<string>(src.Count - 1);
                    foreach (var s in src) if (!string.Equals(s, mine, StringComparison.Ordinal)) tmp.Add(s);
                    tmp.Sort(StringComparer.Ordinal);
                    var result = string.Join(" | ", tmp);
                    dict[key] = result;
                    return result;
                }

                //  hesap kodları için de taban/join cache
                string? crdCodeAll = null, debCodeAll = null;
                Dictionary<string, string>? crdCodeMinusCache = null, debCodeMinusCache = null;
                if (alsoAccountCodes)
                {
                    crdCodeAll = JoinSorted(crdCodeSet!, ",");
                    debCodeAll = JoinSorted(debCodeSet!, ",");

                    crdCodeMinusCache = new Dictionary<string, string>(StringComparer.Ordinal);
                    debCodeMinusCache = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                string BuildMinusCodes(HashSet<string> src, string mine, bool isCreditSide)
                {
                    if (!alsoAccountCodes || src == null) return string.Empty;
                    if (!src.Contains(mine)) return JoinSorted(src, ",");

                    var dict = isCreditSide ? crdCodeMinusCache! : debCodeMinusCache!;
                    if (dict.TryGetValue(mine, out var cached)) return cached;

                    if (src.Count == 1) { dict[mine] = string.Empty; return string.Empty; }

                    var tmp = new List<string>(src.Count - 1);
                    foreach (var s in src) if (!string.Equals(s, mine, StringComparison.Ordinal)) tmp.Add(s);
                    tmp.Sort(StringComparer.Ordinal);
                    var result = string.Join(",", tmp);
                    dict[mine] = result;
                    return result;
                }

                // grup üyelerine yazılır
                foreach (var i in idxs)
                {
                    var r = rows[i];
                    var myKebir = (r.Kebir ?? "").Trim();
                    var myCode = (r.HesapKodu ?? "").Trim();

                    if (r.Side == "D")
                    {
                        // borç satırları için karşı taraf alacak kebir seti
                        r.KarsiHesap = BuildMinusString(crdKebirSet, myKebir); // kebir bazlı
                        r.ContraKebirCsv = r.KarsiHesap?.Replace(" | ", ","); // csv için
                        if (alsoAccountCodes)
                            r.ContraHesapCsv = BuildMinusCodes(crdCodeSet!, myCode, isCreditSide: true);
                    }
                    else if (r.Side == "C")
                    {
                        // alacak satırları için karşı taraf borç kebir seti
                        r.KarsiHesap = BuildMinusString(debKebirSet, myKebir);
                        r.ContraKebirCsv = r.KarsiHesap?.Replace(" | ", ",");
                        if (alsoAccountCodes)
                            r.ContraHesapCsv = BuildMinusCodes(debCodeSet!, myCode, isCreditSide: false);
                    }
                    else
                    {
                        // yönsüz satırlar için iki tarafın birleşimini kebirini göster
                        if (debKebirSet.Count == 0 && crdKebirSet.Count == 0)
                        {
                            r.KarsiHesap = string.Empty;
                            r.ContraKebirCsv = string.Empty;
                            if (alsoAccountCodes) r.ContraHesapCsv = string.Empty;
                        }
                        else
                        {
                            // birleşim kendi kebirini yine hariç
                            var union = new HashSet<string>(debKebirSet, StringComparer.Ordinal);
                            foreach (var s in crdKebirSet) union.Add(s);
                            r.KarsiHesap = BuildMinusString(union, myKebir);
                            r.ContraKebirCsv = r.KarsiHesap?.Replace(" | ", ",");
                            if (alsoAccountCodes)
                            {
                                var unionCodes = new HashSet<string>(debCodeSet ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
                                if (crdCodeSet != null) foreach (var s in crdCodeSet) unionCodes.Add(s);
                                r.ContraHesapCsv = BuildMinusCodes(unionCodes, myCode, isCreditSide: false);
                            }
                        }
                    }
                }
            }
        }

        // ---------------- helpers ----------------

        private static string DetectSide(MuavinRow r)
        {
            if (r.Borc > 0m) return "D";
            if (r.Alacak > 0m) return "C";
            return "";
        }

        /// <summary>
        /// aynı fişe ait hareketleri aynı anahtar altında toplar
        /// açılış/kapanış fişlerinde belge no katılmaz diğerlerinde no+tarih+belge ile ayırmaya çalışırız
        /// </summary>
        private static string BuildGroupKey(MuavinRow r)
        {
            var d = r.PostingDate?.ToString("yyyy-MM-dd") ?? "";
            var no = r.EntryNumber ?? "";
            var doc = r.DocumentNumber ?? "";

            if (r.FisTuru is "Açılış" or "Kapanış")
                return $"{no}|{d}";

            if (!string.IsNullOrWhiteSpace(doc))
                return $"{no}|{d}|DOC:{doc}";

            return $"{no}|{d}";
        }

        public static void ComputeRunningBalance(IList<MuavinRow> rows)
        {
            if (rows is null) return;
            decimal bal = 0m;
            foreach (var r in rows.OrderBy(x => x.PostingDate)
                                  .ThenBy(x => x.EntryNumber)
                                  .ThenBy(x => x.EntryCounter))
            {
                bal += r.Borc - r.Alacak;
                r.RunningBalance = bal;
            }
        }

        public static void ComputeRunningBalancePerAccount(IList<MuavinRow> rows)
        {
            if (rows is null) return;

            foreach (var g in rows.Where(r => !string.IsNullOrWhiteSpace(r.HesapKodu))
                                  .GroupBy(r => r.HesapKodu, StringComparer.Ordinal))
            {
                decimal bal = 0m;
                foreach (var r in g.OrderBy(x => x.PostingDate)
                                   .ThenBy(x => x.EntryNumber)
                                   .ThenBy(x => x.EntryCounter))
                {
                    bal += r.Borc - r.Alacak;
                    r.RunningBalance = bal;
                }
            }
        }

        public static void ExportCsv(IEnumerable<MuavinRow> rows, string csvPath, bool perAccountBalance = false)
        {
            var ordered = rows.OrderBy(x => x.PostingDate)
                              .ThenBy(x => x.EntryNumber)
                              .ThenBy(x => x.EntryCounter)
                              .ToList();

            if (perAccountBalance) ComputeRunningBalancePerAccount(ordered);
            else ComputeRunningBalance(ordered);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
            using var sw = new StreamWriter(csvPath, false, new UTF8Encoding(true));

            sw.WriteLine(string.Join(",",
                "Kayıt No", "Kebir", "Hesap Kodu", "Hesap Adı",
                "Fiş Tarihi", "Fiş Numarası", "Fiş Türü", "Açıklama",
                "Borç", "Alacak", "Bakiye", "Tutar", "Fiş Tipi", "Fatura No", "Karşı Hesap"
            ));

            var tr = CultureInfo.GetCultureInfo("tr-TR");
            string Csv(string? v) => string.IsNullOrEmpty(v) ? "" :
                (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
                    ? $"\"{v.Replace("\"", "\"\"")}\""
                    : v;

            foreach (var r in ordered)
            {
                sw.WriteLine(string.Join(",",
                    Csv(r.EntryCounter?.ToString(CultureInfo.InvariantCulture) ?? ""),
                    Csv(r.Kebir),
                    Csv(r.HesapKodu),
                    Csv(r.HesapAdi),
                    Csv(r.PostingDate?.ToString("dd.MM.yyyy")),
                    Csv(r.EntryNumber),
                    Csv(r.FisTuru),
                    Csv(r.Aciklama),
                    r.Borc.ToString("0.##", tr),
                    r.Alacak.ToString("0.##", tr),
                    r.RunningBalance.ToString("0.##", tr),
                    r.Tutar.ToString("0.##", tr),
                    Csv(r.FisTipi),
                    Csv(r.DocumentNumber),
                    Csv(r.KarsiHesap)   // kebir bazlı tek satır
                ));
            }
        }

        public static void ExportExcel(IEnumerable<MuavinRow> rows, string xlsxPath, bool perAccountBalance = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(xlsxPath))!);

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Muavin");

            string[] headers =
            {
               "Kayıt No","Kebir","Hesap Kodu","Hesap Adı","Fiş Tarihi","Fiş Numarası",
               "Fiş Türü","Açıklama","Borç","Alacak","Bakiye","Tutar",
               "Fiş Tipi","Fatura No","Karşı Hesap"
            };

            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];

            var headerRange = ws.Range(1, 1, 1, headers.Length);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0F0F0");

            var ordered = rows
                .OrderBy(x => x.PostingDate)
                .ThenBy(x => x.EntryNumber)
                .ThenBy(x => x.EntryCounter)
                .ToList();

            var balances = new Dictionary<string, decimal>(StringComparer.Ordinal);
            string BalanceKey(MuavinRow r) => perAccountBalance ? (r.HesapKodu ?? r.Kebir ?? "_") : "_GLOBAL";

            int rowIndex = 2;
            foreach (var r in ordered)
            {
                var key = BalanceKey(r);
                if (!balances.TryGetValue(key, out var bal)) bal = 0m;
                bal += r.Borc - r.Alacak;
                balances[key] = bal;

                ws.Cell(rowIndex, 1).Value = r.EntryCounter;
                ws.Cell(rowIndex, 2).Value = r.Kebir;
                ws.Cell(rowIndex, 3).Value = r.HesapKodu;
                ws.Cell(rowIndex, 4).Value = r.HesapAdi;
                ws.Cell(rowIndex, 5).Value = r.PostingDate?.ToString("dd.MM.yyyy");
                ws.Cell(rowIndex, 6).Value = r.EntryNumber;
                ws.Cell(rowIndex, 7).Value = r.FisTuru;
                ws.Cell(rowIndex, 8).Value = r.Aciklama;
                ws.Cell(rowIndex, 9).Value = r.Borc;
                ws.Cell(rowIndex, 10).Value = r.Alacak;
                ws.Cell(rowIndex, 11).Value = bal;               // Bakiye
                ws.Cell(rowIndex, 12).Value = r.Tutar;
                ws.Cell(rowIndex, 13).Value = r.FisTipi;
                ws.Cell(rowIndex, 14).Value = r.DocumentNumber;  // Fatura No
                ws.Cell(rowIndex, 15).Value = r.KarsiHesap;      // Karşı Hesap (kebir bazlı)

                ws.Cell(rowIndex, 9).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(rowIndex, 10).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(rowIndex, 11).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(rowIndex, 12).Style.NumberFormat.Format = "#,##0.00";

                rowIndex++;
            }

            var dataRange = ws.Range(1, 1, rowIndex - 1, headers.Length);
            dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            ws.Columns().AdjustToContents();

            wb.SaveAs(xlsxPath);
        }
    }
}
