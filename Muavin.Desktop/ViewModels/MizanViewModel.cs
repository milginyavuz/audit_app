// MizanViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muavin.Desktop.Enums;
using Muavin.Xml.Parsing;

namespace Muavin.Desktop.ViewModels
{
    public partial class MizanViewModel : ObservableObject
    {
        private readonly List<MuavinRow> _source;

        [ObservableProperty] private DateTime startDate;
        [ObservableProperty] private DateTime endDate;

        [ObservableProperty] private MizanType mizanTipi = MizanType.Genel;
        [ObservableProperty] private HareketDurumu hareketDurumu = HareketDurumu.Hepsi;
        [ObservableProperty] private GorunumModu gorunum = GorunumModu.AcikDetayli;

        public ObservableCollection<MizanRow> Rows { get; } = new();

        // ✅ ÜST TOPLAMLAR
        [ObservableProperty] private decimal toplamBorc;
        [ObservableProperty] private decimal toplamAlacak;
        [ObservableProperty] private decimal toplamNetBakiye;

        // ✅ Durum metni
        [ObservableProperty] private string balanceStatusText = "";
        [ObservableProperty] private string netBakiyeText = "";

        // ✅ Net bakiye rengi
        private Brush _netBakiyeBrush = Brushes.Gray;
        public Brush NetBakiyeBrush
        {
            get => _netBakiyeBrush;
            set => SetProperty(ref _netBakiyeBrush, value);
        }
        // ✅ RENK (BUNU AÇIK PROPERTY OLARAK TANIYORUZ)
        private Brush _balanceStatusBrush = Brushes.Gray;
        public Brush BalanceStatusBrush
        {
            get => _balanceStatusBrush;
            set => SetProperty(ref _balanceStatusBrush, value);
        }

        // Drilldown vb. için kaynak satırlar
        public IReadOnlyList<MuavinRow> SourceRows => _source;

        public MizanViewModel(IEnumerable<MuavinRow> source)
        {
            _source = source?.ToList() ?? new List<MuavinRow>();

            var dates = _source
                .Where(r => r.PostingDate.HasValue)
                .Select(r => r.PostingDate!.Value.Date)
                .ToList();

            if (dates.Any())
            {
                var min = dates.Min();
                var max = dates.Max();

                StartDate = new DateTime(min.Year, 1, 1);
                EndDate = new DateTime(min.Year, 12, 31);

                if (min.Year != max.Year)
                    EndDate = new DateTime(max.Year, 12, 31);
            }
            else
            {
                StartDate = new DateTime(DateTime.Today.Year, 1, 1);
                EndDate = new DateTime(DateTime.Today.Year, 12, 31);
            }

            Hesapla();
        }

        [RelayCommand]
        private void Hesapla()
        {
            Rows.Clear();

            // Toplamları sıfırla
            ToplamBorc = 0m;
            ToplamAlacak = 0m;
            ToplamNetBakiye = 0m;
            BalanceStatusText = "";
            BalanceStatusBrush = Brushes.Gray;

            if (_source.Count == 0) return;

            // Date sanity
            var s = StartDate.Date;
            var e = EndDate.Date;
            if (s > e)
            {
                (s, e) = (e, s);
                StartDate = s;
                EndDate = e;
            }

            // ✅ MOD DAVRANIŞI:
            if (MizanTipi == MizanType.Genel)
            {
                var year = s.Year;
                s = new DateTime(year, 1, 1);
                e = new DateTime(year, 12, 31);
            }

            var result = MizanCalculator.Calculate(
                _source,
                s,
                e,
                MizanTipi,
                HareketDurumu,
                Gorunum
            );

            foreach (var row in result)
                Rows.Add(row);

            // ✅ Üst toplam: sadece kebir satırları
            var kebirRows = Rows.Where(r => r.IsKebirRow).ToList();

            ToplamBorc = kebirRows.Sum(r => r.Borc);
            ToplamAlacak = kebirRows.Sum(r => r.Alacak);

            // Net: Borç bakiyeler (+) - Alacak bakiyeler (-)
            ToplamNetBakiye =
                kebirRows.Sum(r => r.BorcBakiye) -
                kebirRows.Sum(r => r.AlacakBakiye);

            // ✅ Durum + renk
            if (ToplamBorc == ToplamAlacak)
            {
                BalanceStatusText = "✔ Mizan dengede";
                BalanceStatusBrush = Brushes.Green;
            }
            else
            {
                var fark = Math.Abs(ToplamBorc - ToplamAlacak);
                BalanceStatusText = $"⚠ Fark var: {fark:N2}";
                BalanceStatusBrush = Brushes.Red;
            }

            // ✅ Durum + renk (dengede mi?)
            if (ToplamBorc == ToplamAlacak)
            {
                BalanceStatusText = "✔ Mizan dengede";
                BalanceStatusBrush = Brushes.Green;

                NetBakiyeText = "Net: 0,00";
                NetBakiyeBrush = Brushes.Green;
            }
            else
            {
                var fark = Math.Abs(ToplamBorc - ToplamAlacak);

                BalanceStatusText = $"⚠ Fark var: {fark:N2}";
                BalanceStatusBrush = Brushes.Red;

                if (ToplamBorc > ToplamAlacak)
                {
                    NetBakiyeText = $"Net: Borç {fark:N2}";
                    NetBakiyeBrush = Brushes.SteelBlue;
                }
                else
                {
                    NetBakiyeText = $"Net: Alacak {fark:N2}";
                    NetBakiyeBrush = Brushes.DarkOrange;
                }
            }

        }

        partial void OnMizanTipiChanged(MizanType value) => Hesapla();
        partial void OnHareketDurumuChanged(HareketDurumu value) => Hesapla();
        partial void OnGorunumChanged(GorunumModu value) => Hesapla();

        partial void OnStartDateChanged(DateTime value)
        {
            if (MizanTipi == MizanType.IkiTarihArasi) Hesapla();
        }

        partial void OnEndDateChanged(DateTime value)
        {
            if (MizanTipi == MizanType.IkiTarihArasi) Hesapla();
        }
    }
}
