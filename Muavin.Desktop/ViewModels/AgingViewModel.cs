using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muavin.Xml.Parsing;

namespace Muavin.Desktop.ViewModels
{
    public partial class AgingViewModel : ObservableObject
    {
        private readonly List<MuavinRow> _source;

        [ObservableProperty] private string? kebirBas;
        [ObservableProperty] private string? kebirBit;
        [ObservableProperty] private string? hesapKodu;
        [ObservableProperty] private DateTime? agingDate;

        public ObservableCollection<AgingDetailRow> DetailRows { get; } = new();
        public ObservableCollection<AgingReportRow> ReportRows { get; } = new();

        public AgingViewModel(IEnumerable<MuavinRow> rows)
        {
            _source = rows
                .Where(r => r.PostingDate.HasValue)
                .ToList();

            if (_source.Any())
            {
                var maxDate = _source.Max(r => r.PostingDate)!.Value;
                AgingDate = new DateTime(maxDate.Year, 12, 31);
            }
            else
            {
                AgingDate = DateTime.Today;
            }
        }

        // ✅ Kapanış: önce Fiş Türü, yoksa açıklama
        private static bool IsClosingEntry(MuavinRow r)
        {
            var ft = (r.FisTuru ?? "").Trim();
            if (ft.Equals("Kapanış", StringComparison.OrdinalIgnoreCase) ||
                ft.Equals("Kapanis", StringComparison.OrdinalIgnoreCase))
                return true;

            var aciklama = r.Aciklama ?? string.Empty;
            return aciklama.Contains("kapanış", StringComparison.OrdinalIgnoreCase)
                || aciklama.Contains("kapanis", StringComparison.OrdinalIgnoreCase);
        }

        // ✅ Açılış: önce Fiş Türü, yoksa açıklama
        private static bool IsOpeningEntry(MuavinRow r)
        {
            var ft = (r.FisTuru ?? "").Trim();
            if (ft.Equals("Açılış", StringComparison.OrdinalIgnoreCase) ||
                ft.Equals("Acilis", StringComparison.OrdinalIgnoreCase))
                return true;

            var aciklama = r.Aciklama ?? string.Empty;
            return aciklama.Contains("açılış", StringComparison.OrdinalIgnoreCase)
                || aciklama.Contains("acilis", StringComparison.OrdinalIgnoreCase);
        }

        [RelayCommand]
        private void Age()
        {
            DetailRows.Clear();
            ReportRows.Clear();

            if (!_source.Any() || AgingDate is null)
                return;

            DateTime curDate = AgingDate.Value.Date;

            var q = _source.Where(r => !IsClosingEntry(r) && r.PostingDate <= curDate);

            if (!string.IsNullOrWhiteSpace(KebirBas) || !string.IsNullOrWhiteSpace(KebirBit))
            {
                int? kb = int.TryParse(KebirBas, out var k1) ? k1 : null;
                int? ke = int.TryParse(KebirBit, out var k2) ? k2 : null;

                q = q.Where(r =>
                {
                    if (!int.TryParse(r.Kebir, out var k)) return false;
                    if (kb.HasValue && k < kb.Value) return false;
                    if (ke.HasValue && k > ke.Value) return false;
                    return true;
                });
            }

            if (!string.IsNullOrWhiteSpace(HesapKodu))
            {
                var parts = HesapKodu!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToArray();

                q = q.Where(r => r.HesapKodu != null &&
                                 parts.Any(p => r.HesapKodu.StartsWith(p, StringComparison.Ordinal)));
            }

            var filtered = q.ToList();
            if (!filtered.Any()) return;

            // Detay: running borç-alacak (pozitif/negatif olabilir)
            var orderedForDetail = filtered
                .OrderBy(r => r.HesapKodu)
                .ThenBy(r => r.PostingDate)
                .ThenBy(r => r.EntryNumber)
                .ThenBy(r => r.EntryCounter ?? 0);

            foreach (var grp in orderedForDetail.GroupBy(r => new { r.HesapKodu, r.HesapAdi }))
            {
                decimal running = 0m;
                foreach (var r in grp)
                {
                    running += (r.Borc - r.Alacak);

                    DetailRows.Add(new AgingDetailRow
                    {
                        HesapKodu = grp.Key.HesapKodu ?? string.Empty,
                        HesapAdi = grp.Key.HesapAdi ?? string.Empty,
                        PostingDate = r.PostingDate!.Value,
                        Borc = r.Borc,
                        Alacak = r.Alacak,
                        Bakiye = running
                    });
                }
            }

            // ✅ Özet: RunningBalance varsa kullan, yoksa kendin hesapla (özetin boş gelmesini engeller)
            var reports = ExcelAgingCalculator.Calculate(filtered, curDate);

            foreach (var row in reports)
                ReportRows.Add(row);
        }

        public class AgingDetailRow
        {
            public string HesapKodu { get; set; } = string.Empty;
            public string HesapAdi { get; set; } = string.Empty;
            public DateTime PostingDate { get; set; }
            public decimal Borc { get; set; }
            public decimal Alacak { get; set; }
            public decimal Bakiye { get; set; }
        }

        public class AgingReportRow
        {
            public string HesapKodu { get; set; } = string.Empty;
            public string HesapAdi { get; set; } = string.Empty;

            public decimal Gun_0_30 { get; set; }
            public decimal Gun_31_60 { get; set; }
            public decimal Gun_61_90 { get; set; }
            public decimal Gun_91_120 { get; set; }
            public decimal Gun_121_150 { get; set; }
            public decimal Gun_151_180 { get; set; }
            public decimal Gun_181_210 { get; set; }
            public decimal Gun_211_240 { get; set; }
            public decimal Gun_241_270 { get; set; }
            public decimal Gun_271_300 { get; set; }
            public decimal Gun_301_330 { get; set; }
            public decimal Gun_331_365 { get; set; }

            public decimal Gun_Acilis { get; set; }
            public decimal Gun_365Ustu { get; set; }

            // Özet satırının toplam bakiyesi (pozitif gösteriyoruz)
            public decimal Bakiye { get; set; }
        }

        private static class ExcelAgingCalculator
        {
            private static readonly int[] Limits =
            {
                30,60,90,120,150,180,210,240,270,300,330,365
            };

            public static List<AgingReportRow> Calculate(IEnumerable<MuavinRow> source, DateTime agingDate)
            {
                var result = new List<AgingReportRow>();

                foreach (var accountGroup in source
                    .Where(r => !string.IsNullOrWhiteSpace(r.HesapKodu))
                    .GroupBy(r => r.HesapKodu!, StringComparer.Ordinal))
                {
                    var rows = accountGroup
                        .OrderBy(r => r.PostingDate ?? DateTime.MinValue)
                        .ThenBy(r => r.EntryNumber)
                        .ThenBy(r => r.EntryCounter ?? 0)
                        .ToList();

                    if (rows.Count == 0) continue;

                    var last = rows[^1];

                    // ✅ 1) Önce RunningBalance dene
                    decimal netBalance = last.RunningBalance;

                    // ✅ 2) RunningBalance yoksa/0 ise: borç-alacak toplamından hesapla (özetin boş gelmesini engeller)
                    if (netBalance == 0m)
                        netBalance = rows.Sum(r => (r.Borc - r.Alacak));

                    if (netBalance == 0m)
                        continue;

                    bool isDebit = netBalance > 0m;
                    decimal remaining = Math.Abs(netBalance);

                    var report = new AgingReportRow
                    {
                        HesapKodu = accountGroup.Key,
                        HesapAdi = last.HesapAdi ?? string.Empty,
                        Bakiye = remaining
                    };

                    foreach (var row in rows.AsEnumerable().Reverse())
                    {
                        if (remaining <= 0m) break;

                        decimal hareketTutar = isDebit ? row.Borc : row.Alacak;
                        if (hareketTutar <= 0m) continue;

                        var pay = Math.Min(hareketTutar, remaining);
                        if (pay <= 0m) continue;

                        if (IsOpeningEntry(row))
                        {
                            report.Gun_Acilis += pay;
                            remaining -= pay;
                            continue;
                        }

                        int days = (agingDate.Date - row.PostingDate!.Value.Date).Days;
                        if (days < 0) days = 0;

                        int bucketIndex = 0;
                        while (bucketIndex < Limits.Length && days > Limits[bucketIndex])
                            bucketIndex++;

                        remaining -= pay;

                        // Tutarı ilgili gün kolonuna ekle – HER ZAMAN POZİTİF
                        switch (bucketIndex)
                        {
                            case 0: report.Gun_0_30 += pay; break;
                            case 1: report.Gun_31_60 += pay; break;
                            case 2: report.Gun_61_90 += pay; break;
                            case 3: report.Gun_91_120 += pay; break;
                            case 4: report.Gun_121_150 += pay; break;
                            case 5: report.Gun_151_180 += pay; break;
                            case 6: report.Gun_181_210 += pay; break;
                            case 7: report.Gun_211_240 += pay; break;
                            case 8: report.Gun_241_270 += pay; break;
                            case 9: report.Gun_271_300 += pay; break;
                            case 10: report.Gun_301_330 += pay; break;
                            case 11: report.Gun_331_365 += pay; break;
                            default: report.Gun_365Ustu += pay; break;
                        }
                    }

                    result.Add(report);
                }

                return result.OrderBy(r => r.HesapKodu).ToList();
            }

            private static bool IsOpeningEntry(MuavinRow r)
            {
                var ft = (r.FisTuru ?? "").Trim();
                if (ft.Equals("Açılış", StringComparison.OrdinalIgnoreCase) ||
                    ft.Equals("Acilis", StringComparison.OrdinalIgnoreCase))
                    return true;

                var aciklama = r.Aciklama ?? string.Empty;
                return aciklama.Contains("açılış", StringComparison.OrdinalIgnoreCase)
                    || aciklama.Contains("acilis", StringComparison.OrdinalIgnoreCase);
            }
        }

        // (Aynı sınıf içinde olduğu için buradaki IsOpeningEntry/IsClosingEntry zaten var,
        // ExcelAgingCalculator içine de güvenli olması için IsOpeningEntry koyduk.)
    }
}
