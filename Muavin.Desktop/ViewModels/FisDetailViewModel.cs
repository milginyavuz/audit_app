// FisDetailViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using Muavin.Xml.Parsing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Muavin.Desktop.ViewModels
{
    public class FisDetailViewModel : ObservableObject
    {
        public ObservableCollection<MuavinRow> Rows { get; } = new();

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

        // ✅ YENİ: Fark
        private decimal _totalFark;
        public decimal TotalFark
        {
            get => _totalFark;
            set => SetProperty(ref _totalFark, value);
        }

        // ✅ YENİ: Dengesizlik var mı?
        private bool _hasImbalance;
        public bool HasImbalance
        {
            get => _hasImbalance;
            set => SetProperty(ref _hasImbalance, value);
        }

        private string _warningText = "";
        public string WarningText
        {
            get => _warningText;
            set => SetProperty(ref _warningText, value);
        }

        public FisDetailViewModel(IEnumerable<MuavinRow> source, string entryNumber, DateTime postingDate)
        {
            var src = source?.ToList() ?? new List<MuavinRow>();
            entryNumber = (entryNumber ?? "").Trim();

            var list = src
                .Where(r => r.PostingDate.HasValue)
                .Where(r => string.Equals((r.EntryNumber ?? "").Trim(), entryNumber, StringComparison.OrdinalIgnoreCase))
                .Where(r => r.PostingDate!.Value.Date == postingDate.Date)
                .OrderBy(r => r.EntryCounter ?? 0)
                .ThenBy(r => (r.HesapKodu ?? "").Trim())
                .ToList();

            foreach (var r in list)
                Rows.Add(r);

            TitleText = $"Fiş Detayı: {entryNumber} | Tarih: {postingDate:dd.MM.yyyy}";
            SubTitleText = $"Satır: {Rows.Count:N0}";

            TotalBorc = list.Sum(r => r.Borc);
            TotalAlacak = list.Sum(r => r.Alacak);

            TotalFark = TotalBorc - TotalAlacak;
            HasImbalance = TotalFark != 0m;

            WarningText = HasImbalance
                ? $"⚠ Fiş dengesiz! Borç - Alacak = {TotalFark:N2}"
                : "";
        }
    }
}
