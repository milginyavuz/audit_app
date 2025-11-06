using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muavin.Xml.Parsing; // MuavinRow
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Muavin.Desktop.ViewModels
{
    public sealed partial class AgingRow : ObservableObject
    {
        [ObservableProperty] private string? _hesapKodu;
        [ObservableProperty] private string? _hesapAdi;

        [ObservableProperty] private decimal _b0_30;
        [ObservableProperty] private decimal _b30_60;
        [ObservableProperty] private decimal _b60_90;
        [ObservableProperty] private decimal _b90_120;
        [ObservableProperty] private decimal _b120_150;
        [ObservableProperty] private decimal _b150_180;
        [ObservableProperty] private decimal _b180_210;
        [ObservableProperty] private decimal _b210_240;
        [ObservableProperty] private decimal _b240_270;
        [ObservableProperty] private decimal _b270_300;
        [ObservableProperty] private decimal _b300_330;
        [ObservableProperty] private decimal _b330_365;
        [ObservableProperty] private decimal _bGt365;

        [ObservableProperty] private decimal _bakiye;

        public decimal[] AsArray() => new[]
        {
            B0_30, B30_60, B60_90, B90_120, B120_150, B150_180,
            B180_210, B210_240, B240_270, B270_300, B300_330, B330_365, BGt365
        };
    }

    public sealed partial class AgingViewModel : ObservableObject
    {
        private readonly IReadOnlyList<MuavinRow> _sourceRows;

        // UI
        [ObservableProperty] private DateTime _agingDate = new DateTime(DateTime.Today.Year, 12, 31);

        // Varsayılan (virgüllü) kebir listesi
        [ObservableProperty]
        private string _kebirFilter =
            "120,320,159,340,136,336,131,331,180,280,126,226,236,436,431";

        [ObservableProperty] private bool _onlyNonZero = true;

        public ObservableCollection<AgingRow> Rows { get; } = new();

        // Üst toplamlar
        [ObservableProperty]
        private decimal _t0_30, _t30_60, _t60_90, _t90_120, _t120_150, _t150_180,
                        _t180_210, _t210_240, _t240_270, _t270_300, _t300_330, _t330_365,
                        _tGt365, _tBakiye;

        // Gün sınırları
        private static readonly int[] Buckets = { 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330, 365, 10000 };

        public AgingViewModel(IEnumerable<MuavinRow> rows, DateTime? defaultAgingDate = null)
        {
            _sourceRows = rows.ToList();
            if (defaultAgingDate.HasValue) AgingDate = defaultAgingDate.Value;
            Rebuild();
        }

        [RelayCommand]
        private void Rebuild()
        {
            Rows.Clear();

            // Kebir filtresi
            var kebirSet = new HashSet<string>(
                (KebirFilter ?? "")
                    .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);

            // Hesap bazında grupla — ADLANDIRILMIŞ tuple!
            var q = _sourceRows
                .Where(r =>
                    r.HesapKodu != null &&
                    (kebirSet.Count == 0 || (r.Kebir != null && kebirSet.Contains(r.Kebir))))
                .GroupBy(r => (HesapKodu: r.HesapKodu ?? "", HesapAdi: r.HesapAdi));

            decimal[] totals = new decimal[14]; // 13 kova + bakiye

            foreach (var g in q)
            {
                // İşaretli tutar: borç +, alacak -
                var items = g.Select(r => new
                {
                    Date = r.PostingDate ?? DateTime.MinValue,
                    AmountSigned = r.Borc - r.Alacak
                })
                             .OrderBy(x => x.Date)
                             .ToList();

                if (items.Count == 0) continue;

                var balance = items.Sum(x => x.AmountSigned);
                if (OnlyNonZero && balance == 0m) continue;

                var sign = Math.Sign(balance);
                if (sign == 0) sign = +1;

                decimal remaining = Math.Abs(balance);
                var buckets = new decimal[13];

                foreach (var it in items.Where(x => Math.Sign(x.AmountSigned) == sign))
                {
                    if (remaining <= 0) break;

                    var x = Math.Abs(it.AmountSigned);
                    var take = Math.Min(x, remaining);

                    // Gün farkı → kova
                    var days = (AgingDate.Date - it.Date.Date).TotalDays;
                    if (days < 0) days = 0;

                    int bi = 0;
                    for (; bi < Buckets.Length; bi++)
                        if (days <= Buckets[bi]) break;

                    if (bi >= buckets.Length) bi = buckets.Length - 1;

                    buckets[bi] += take;
                    remaining -= take;
                }

                // Satır
                var row = new AgingRow
                {
                    HesapKodu = g.Key.HesapKodu,
                    HesapAdi = g.Key.HesapAdi,
                    B0_30 = buckets[0],
                    B30_60 = buckets[1],
                    B60_90 = buckets[2],
                    B90_120 = buckets[3],
                    B120_150 = buckets[4],
                    B150_180 = buckets[5],
                    B180_210 = buckets[6],
                    B210_240 = buckets[7],
                    B240_270 = buckets[8],
                    B270_300 = buckets[9],
                    B300_330 = buckets[10],
                    B330_365 = buckets[11],
                    BGt365 = buckets[12],
                    Bakiye = balance
                };

                Rows.Add(row);

                // Toplamlar
                for (int i = 0; i < 13; i++) totals[i] += buckets[i];
                totals[13] += balance;
            }

            // Üst toplam alanları
            T0_30 = totals[0]; T30_60 = totals[1]; T60_90 = totals[2]; T90_120 = totals[3];
            T120_150 = totals[4]; T150_180 = totals[5]; T180_210 = totals[6]; T210_240 = totals[7];
            T240_270 = totals[8]; T270_300 = totals[9]; T300_330 = totals[10]; T330_365 = totals[11];
            TGt365 = totals[12]; TBakiye = totals[13];
        }
    }
}
