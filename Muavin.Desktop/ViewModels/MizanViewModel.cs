// MizanViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muavin.Desktop.Enums;
using Muavin.Xml.Parsing;

namespace Muavin.Desktop.ViewModels
{
    public partial class MizanViewModel : ObservableObject
    {
        private readonly List<MuavinRow> _source;

        [ObservableProperty]
        private DateTime startDate;

        [ObservableProperty]
        private DateTime endDate;

        [ObservableProperty]
        private MizanType mizanTipi = MizanType.Genel;

        [ObservableProperty]
        private HareketDurumu hareketDurumu = HareketDurumu.Hepsi;

        [ObservableProperty]
        private GorunumModu gorunum = GorunumModu.AcikDetayli;

        public ObservableCollection<MizanRow> Rows { get; } = new();

        public MizanViewModel(IEnumerable<MuavinRow> source)
        {
            _source = source?.ToList() ?? new List<MuavinRow>();

            var dated = _source
                .Where(r => r.PostingDate.HasValue)
                .Select(r => r.PostingDate!.Value)
                .ToList();

            if (dated.Any())
            {
                var min = dated.Min();
                var max = dated.Max();

                StartDate = new DateTime(min.Year, 1, 1);
                EndDate = new DateTime(max.Year, 12, 31);
            }
            else
            {
                StartDate = new DateTime(DateTime.Today.Year, 1, 1);
                EndDate = new DateTime(DateTime.Today.Year, 12, 31);
            }

            // pencere açılınca otomatik hesapla
            Hesapla();
        }

        [RelayCommand]
        private void Hesapla()
        {
            Rows.Clear();

            if (!_source.Any())
                return;

            var datedRows = _source
                .Where(r => r.PostingDate.HasValue)
                .Select(r => r.PostingDate!.Value)
                .ToList();

            if (!datedRows.Any())
                return;

            var start = StartDate.Date;
            var end = EndDate.Date;

            var hesapSonucu = MizanCalculator.Calculate(
                _source,
                start,
                end,
                MizanTipi,
                HareketDurumu,
                Gorunum
            );

            foreach (var row in hesapSonucu)
                Rows.Add(row);
        }

        partial void OnMizanTipiChanged(MizanType value) => Hesapla();
        partial void OnHareketDurumuChanged(HareketDurumu value) => Hesapla();
        partial void OnGorunumChanged(GorunumModu value) => Hesapla();
    }
}
