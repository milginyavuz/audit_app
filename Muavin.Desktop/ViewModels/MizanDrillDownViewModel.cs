// MizanDrillDownViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using Muavin.Xml.Parsing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Muavin.Desktop.ViewModels
{
    public partial class MizanDrillDownViewModel : ObservableObject
    {
        public ObservableCollection<MuavinRow> PeriodRows { get; } = new();
        public IReadOnlyList<MuavinRow> SourceRows { get; }

        private int _imbalancedFisCount;
        public int ImbalancedFisCount
        {
            get => _imbalancedFisCount;
            set => SetProperty(ref _imbalancedFisCount, value);
        }

        private string _titleText = "";
        public string TitleText
        {
            get => _titleText;
            set => SetProperty(ref _titleText, value);
        }

        private string _subTitleText = "";
        public string SubTitleText
        {
            get => _subTitleText;
            set => SetProperty(ref _subTitleText, value);
        }

        private string _rowsHeaderText = "";
        public string RowsHeaderText
        {
            get => _rowsHeaderText;
            set => SetProperty(ref _rowsHeaderText, value);
        }

        private decimal _totalBorc;
        public decimal TotalBorc
        {
            get => _totalBorc;
            set => SetProperty(ref _totalBorc, value);
        }

        private decimal _totalAlacak;
        public decimal TotalAlacak
        {
            get => _totalAlacak;
            set => SetProperty(ref _totalAlacak, value);
        }

        public MizanDrillDownViewModel(
            IEnumerable<MuavinRow> source,
            MizanRow selected,
            DateTime startDate,
            DateTime endDate)
        {
            var src = source?.ToList() ?? new List<MuavinRow>();
            SourceRows = src;

            var kebir = (selected.Kebir ?? "").Trim();
            var hesap = (selected.HesapKodu ?? "").Trim();
            var hesapAdi = (selected.HesapAdi ?? "").Trim();

            TitleText = selected.IsKebirRow
                ? $"Kebir Hareketleri: {kebir}"
                : $"Hesap Hareketleri: {hesap} — {hesapAdi}";

            SubTitleText = $"Dönem: {startDate:dd.MM.yyyy} - {endDate:dd.MM.yyyy}";

            var q = src.Where(r => r.PostingDate.HasValue)
                       .Where(r => r.PostingDate!.Value.Date >= startDate.Date &&
                                   r.PostingDate!.Value.Date <= endDate.Date);

            if (selected.IsKebirRow)
                q = q.Where(r => string.Equals((r.Kebir ?? "").Trim(), kebir, StringComparison.OrdinalIgnoreCase));
            else
                q = q.Where(r => string.Equals((r.HesapKodu ?? "").Trim(), hesap, StringComparison.OrdinalIgnoreCase));

            var list = q.OrderBy(r => r.PostingDate)
                        .ThenBy(r => (r.EntryNumber ?? "").Trim())
                        .ThenBy(r => r.EntryCounter ?? 0)
                        .ToList();

            foreach (var r in list)
                PeriodRows.Add(r);

            // ✅ Dengesizliği tüm kaynak üzerinden hesapla ama sadece periodRows'a uygula + toleranslı
            ApplyFisImbalanceFlags(
                periodRows: list,
                allRows: src,
                startDate: startDate,
                endDate: endDate,
                epsilon: 0.005m // N2 formatı için güvenli tolerans
            );

            TotalBorc = list.Sum(x => x.Borc);
            TotalAlacak = list.Sum(x => x.Alacak);

            // ✅ sadece bu drilldown'da görünen fişlerin dengesiz sayısı (distinct)
            ImbalancedFisCount = list
                .Where(r => r.IsFisImbalanced)
                .Select(r => new { D = r.PostingDate!.Value.Date, No = (r.EntryNumber ?? "").Trim() })
                .Distinct()
                .Count();

            var imbalanceText = ImbalancedFisCount > 0 ? $" | ⚠ Dengesiz Fiş: {ImbalancedFisCount}" : "";
            RowsHeaderText = $"Satır: {PeriodRows.Count:N0} | Borç: {TotalBorc:N2} | Alacak: {TotalAlacak:N2}{imbalanceText}";
        }

        private static void ApplyFisImbalanceFlags(
            IEnumerable<MuavinRow> periodRows,
            IEnumerable<MuavinRow> allRows,
            DateTime startDate,
            DateTime endDate,
            decimal epsilon)
        {
            if (periodRows == null) return;

            // 1) Reset (sadece drilldown satırları)
            foreach (var r in periodRows)
            {
                r.IsFisImbalanced = false;
                r.FisDiff = 0m;
            }

            if (allRows == null) return;

            // 2) Tüm kaynakta (date aralığında) fiş toplamları
            var fisTotals = allRows
                .Where(r => r.PostingDate.HasValue && !string.IsNullOrWhiteSpace(r.EntryNumber))
                .Where(r => r.PostingDate!.Value.Date >= startDate.Date &&
                            r.PostingDate!.Value.Date <= endDate.Date)
                .GroupBy(r => (Date: r.PostingDate!.Value.Date, No: (r.EntryNumber ?? "").Trim()))
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(x => x.Borc) - g.Sum(x => x.Alacak)
                );

            // 3) Drilldown satırlarına diff uygula (toleranslı)
            foreach (var r in periodRows)
            {
                if (!r.PostingDate.HasValue) continue;

                var entryNo = (r.EntryNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(entryNo)) continue;

                var key = (r.PostingDate.Value.Date, entryNo);

                if (!fisTotals.TryGetValue(key, out var diff))
                    continue;

                // ✅ N2 formatında 0,00 görünecek seviyeyi "0" say
                if (Math.Abs(diff) < epsilon)
                    diff = 0m;

                r.FisDiff = diff;
                r.IsFisImbalanced = diff != 0m;
            }
        }
    }
}
