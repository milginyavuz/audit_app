using Muavin.Xml.Parsing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Muavin.Desktop
{
    public partial class EntryDetailsWindow : Window
    {
        public EntryDetailsWindow(List<MuavinRow> rows)
        {
            InitializeComponent();
            DataContext = new EntryDetailsVm(rows ?? new List<MuavinRow>());
        }

        private sealed class EntryDetailsVm
        {
            public List<MuavinRow> Rows { get; }
            public string TitleText { get; }
            public decimal TotalBorc { get; }
            public decimal TotalAlacak { get; }
            public string DiffText { get; }
            public string WarningText { get; }

            public EntryDetailsVm(List<MuavinRow> rows)
            {
                Rows = rows;

                // Başlık bilgisi (varsa ilk satırdan fiş bilgisi türet)
                var first = rows.FirstOrDefault();
                var fisNo = (first?.EntryNumber ?? "").Trim();
                var date = first?.PostingDate?.ToString("dd.MM.yyyy") ?? "-";
                TitleText = string.IsNullOrWhiteSpace(fisNo)
                    ? "Fiş Detayı"
                    : $"Fiş Detayı: {fisNo} | Tarih: {date}";

                TotalBorc = rows.Sum(r => r.Borc);
                TotalAlacak = rows.Sum(r => r.Alacak);

                var diff = TotalBorc - TotalAlacak;
                DiffText = diff.ToString("N2");

                WarningText = diff != 0m
                    ? $"⚠ Fiş dengesiz! Borç - Alacak = {diff:N2}"
                    : "";
            }
        }
    }
}
