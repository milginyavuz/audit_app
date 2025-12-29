// MuavinRow.cs
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Muavin.Xml.Parsing
{
    public sealed class MuavinRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Header
        public string? EntryNumberRaw { get; set; }
        public string? EntryNumber { get; set; }
        public int? EntryCounter { get; set; }
        public DateTime? PostingDate { get; set; }
        public string? DocumentNumber { get; set; }

        // Account
        public string? AccountMainID { get; set; }
        public string? AccountMainDescription { get; set; }
        public string? AccountSubID { get; set; }
        public string? AccountSubDescription { get; set; }
        public string? Kebir { get; set; }
        public string? HesapKodu { get; set; }
        public string? HesapAdi { get; set; }

        // Amount
        public string? DebitCreditCode { get; set; }
        public decimal Amount { get; set; }
        public decimal Borc { get; set; }
        public decimal Alacak { get; set; }
        public decimal Tutar { get; set; }

        // Running balance
        public decimal RunningBalance { get; set; }
        public decimal Bakiye => RunningBalance;

        // UI/Comment & derived
        public string? Aciklama { get; set; }

        private string? _fisTuru;
        public string? FisTuru
        {
            get => _fisTuru;
            set { if (_fisTuru != value) { _fisTuru = value; OnPropertyChanged(); } }
        }

        private string? _fisTipi;
        public string? FisTipi
        {
            get => _fisTipi;
            set { if (_fisTipi != value) { _fisTipi = value; OnPropertyChanged(); } }
        }

        // Contra / UI
        public string? GroupKey { get; set; }
        public string Side { get; set; } = "";
        public string? ContraKebirCsv { get; set; }
        public string? ContraHesapCsv { get; set; }

        private string? _karsiHesap;
        public string? KarsiHesap
        {
            get => _karsiHesap;
            set { if (_karsiHesap != value) { _karsiHesap = value; OnPropertyChanged(); } }
        }

        // helper
        public string PostingDateText => PostingDate?.ToString("dd.MM.yyyy") ?? "";
    }
}
