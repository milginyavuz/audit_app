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

        // ---------------- Filtre alanları ----------------

        [ObservableProperty] private string? kebirBas;
        [ObservableProperty] private string? kebirBit;
        [ObservableProperty] private string? hesapKodu;
        [ObservableProperty] private DateTime? agingDate;

        // ---------------- Grid kaynakları ----------------

        public ObservableCollection<AgingDetailRow> DetailRows { get; } = new();
        public ObservableCollection<AgingReportRow> ReportRows { get; } = new();

        public AgingViewModel(IEnumerable<MuavinRow> rows)
        {
            // MUAVİNDEKİ SIRAYI BOZMADAN alıyoruz.
            // XML'den gelen muavin zaten tarih / fiş sırasına göre.
            _source = rows
                .Where(r => r.PostingDate.HasValue)
                .ToList();

            // Varsayılan yaşlandırma tarihi: verideki son fiş tarihinin 31.12'si
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

        // Kapanış fişlerini hariç tut (açılışlar dahil)
        private static bool IsClosingEntry(MuavinRow r)
        {
            var aciklama = r.Aciklama ?? string.Empty;
            return aciklama.Contains("kapanış", StringComparison.OrdinalIgnoreCase)
                || aciklama.Contains("kapanis", StringComparison.OrdinalIgnoreCase);
        }

        // Açılış fişlerini tespit et – 331-365 gün aralığına değil, ayrı "Açılış" kolonuna gidecek
        private static bool IsOpeningEntry(MuavinRow r)
        {
            var aciklama = r.Aciklama ?? string.Empty;
            return aciklama.Contains("açılış", StringComparison.OrdinalIgnoreCase)
                || aciklama.Contains("acilis", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- Komut: Yaşlandır ----------------

        [RelayCommand]
        private void Age()
        {
            DetailRows.Clear();
            ReportRows.Clear();

            if (!_source.Any() || AgingDate is null)
                return;

            DateTime curDate = AgingDate.Value.Date;

            // 1) Temel filtre: kapanış fişleri hariç, yaşlandırma tarihine kadar olan hareketler
            var q = _source
                .Where(r => !IsClosingEntry(r) && r.PostingDate <= curDate);

            // Kebir aralığı filtresi
            if (!string.IsNullOrWhiteSpace(KebirBas) || !string.IsNullOrWhiteSpace(KebirBit))
            {
                int? kb = int.TryParse(KebirBas, out var k1) ? k1 : null;
                int? ke = int.TryParse(KebirBit, out var k2) ? k2 : null;

                q = q.Where(r =>
                {
                    if (!int.TryParse(r.Kebir, out var k))
                        return false;

                    if (kb.HasValue && k < kb.Value) return false;
                    if (ke.HasValue && k > ke.Value) return false;
                    return true;
                });
            }

            // Hesap kodu filtresi (tek kod ya da virgülle çoklu)
            if (!string.IsNullOrWhiteSpace(HesapKodu))
            {
                var parts = HesapKodu!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToArray();

                q = q.Where(r => r.HesapKodu != null &&
                                 parts.Any(p => r.HesapKodu.StartsWith(p, StringComparison.Ordinal)));
            }

            // filtered: MUAVİN SIRASINI KORUYAN liste (summary için bunu kullanacağız)
            var filtered = q.ToList();

            if (!filtered.Any())
                return;

            // 2) Detay grid: ekranda daha düzgün görünmesi için HesapKodu + Tarih sırasıyla
            var orderedForDetail = filtered
                .OrderBy(r => r.HesapKodu)
                .ThenBy(r => r.PostingDate);

            foreach (var grp in orderedForDetail.GroupBy(r => new { r.HesapKodu, r.HesapAdi }))
            {
                decimal running = 0;

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

            // 3) Özet grid: Excel makrosuna göre LIFO yaşlandırma
            // Burada MUAVİN SIRASI ile gelen "filtered" listesine göre çalışıyoruz.
            var reports = ExcelAgingCalculator.Calculate(filtered, curDate);

            foreach (var row in reports)
                ReportRows.Add(row);
        }

        // ---------------- DTO sınıfları ----------------

        public class AgingDetailRow
        {
            public string HesapKodu { get; set; } = string.Empty;
            public string HesapAdi { get; set; } = string.Empty;
            public DateTime PostingDate { get; set; }
            public decimal Borc { get; set; }
            public decimal Alacak { get; set; }
            public decimal Bakiye { get; set; }
        }

        // Excel yaşlandırma raporundaki satırın birebir karşılığı
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

            // Açılış fişinden gelen bakiye
            public decimal Gun_Acilis { get; set; }

            public decimal Gun_365Ustu { get; set; }

            public decimal Bakiye { get; set; }
        }

        // ---------------- Excel makrosu ile aynı LIFO mantığı ----------------

        private static class ExcelAgingCalculator
        {
            // Gün sınırları (0-30, 31-60, ... 331-365)
            private static readonly int[] Limits =
            {
                30,   // 0-30
                60,   // 31-60
                90,   // 61-90
                120,  // 91-120
                150,  // 121-150
                180,  // 151-180
                210,  // 181-210
                240,  // 211-240
                270,  // 241-270
                300,  // 271-300
                330,  // 301-330
                365   // 331-365
            };

            public static List<AgingReportRow> Calculate(IEnumerable<MuavinRow> source, DateTime agingDate)
            {
                var result = new List<AgingReportRow>();

                // Hesap koduna göre grupla, ama HER grubun içindeki sıra muavindeki gibi kalsın
                foreach (var accountGroup in source
                             .GroupBy(r => r.HesapKodu)
                             .Where(g => !string.IsNullOrEmpty(g.Key)))
                {
                    var rows = accountGroup.ToList();
                    if (!rows.Any())
                        continue;

                    var last = rows.Last();

                    // Excel makrosu gibi SON SATIRIN BAKİYE’sini esas al
                    // MuavinRow.RunningBalance = muavindeki "Bakiye" kolonu
                    decimal netBalance = last.RunningBalance;

                    if (netBalance == 0)
                        continue;

                    bool isDebit = netBalance > 0;              // Borç bakiye mi?
                    decimal remaining = Math.Abs(netBalance);   // Dağıtılacak bakiye (her zaman +)

                    var report = new AgingReportRow
                    {
                        HesapKodu = accountGroup.Key ?? string.Empty,
                        HesapAdi = last.HesapAdi ?? string.Empty,
                        // Bakiye her zaman pozitif gösterilsin
                        Bakiye = remaining
                    };

                    // LIFO: muavindeki sıranın TERSİNDEN yürü
                    foreach (var row in rows.AsEnumerable().Reverse())
                    {
                        if (remaining <= 0)
                            break;

                        // Hesabın bakiyesi borç ise sadece BORÇ kolonunu,
                        // alacak ise sadece ALACAK kolonunu kullan.
                        decimal hareketTutar = isDebit ? row.Borc : row.Alacak;
                        if (hareketTutar <= 0)
                            continue;

                        decimal pay = Math.Min(hareketTutar, remaining);
                        if (pay <= 0)
                            continue;

                        // AÇILIŞ FİŞİ ise gün aralığına değil, "Açılış" kolonuna yaz
                        if (IsOpeningEntry(row))
                        {
                            report.Gun_Acilis += pay;
                            remaining -= pay;
                            continue;
                        }

                        int days = (agingDate.Date - row.PostingDate!.Value.Date).Days;
                        if (days < 0) days = 0;

                        // Gün aralığını (bucket) bul
                        int bucketIndex = 0;
                        while (bucketIndex < Limits.Length && days > Limits[bucketIndex])
                            bucketIndex++;

                        remaining -= pay;          // Kalan bakiye azalıyor

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
        }
    }
}
