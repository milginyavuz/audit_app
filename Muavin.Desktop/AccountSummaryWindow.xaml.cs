// Muavin.Desktop/AccountSummaryWindow.xaml.cs
using Muavin.Xml.Parsing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Muavin.Desktop
{
    public partial class AccountSummaryWindow : Window
    {
        private readonly List<MuavinRow> _source;
        private List<AccountSummaryRow> _current = new();

        public AccountSummaryWindow(List<MuavinRow> rows)
        {
            InitializeComponent();
            _source = rows;
            DataContext = _current;
        }

        private void Summarize_Click(object sender, RoutedEventArgs e)
        {
            var items = _source.AsEnumerable();

            // Kebir filtresi
            if (int.TryParse(tbKBas.Text, out var kb) || int.TryParse(tbKBit.Text, out var ke))
            {
                int? start = int.TryParse(tbKBas.Text, out kb) ? kb : null;
                int? end = int.TryParse(tbKBit.Text, out ke) ? ke : null;
                items = items.Where(r =>
                {
                    if (!int.TryParse((r.Kebir ?? "0"), out var k)) return false;
                    if (start.HasValue && k < start.Value) return false;
                    if (end.HasValue && k > end.Value) return false;
                    return true;
                });
            }

            // Tarih filtresi
            if (dpBas.SelectedDate is DateTime d1)
                items = items.Where(r => r.PostingDate.HasValue && r.PostingDate.Value.Date >= d1.Date);
            if (dpBit.SelectedDate is DateTime d2)
                items = items.Where(r => r.PostingDate.HasValue && r.PostingDate.Value.Date <= d2.Date);

            // Hesap kodları
            if (!string.IsNullOrWhiteSpace(tbHesap.Text))
            {
                var wanted = tbHesap.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                         .ToHashSet(StringComparer.OrdinalIgnoreCase);
                items = items.Where(r => r.HesapKodu != null && wanted.Contains(r.HesapKodu));
            }

            _current = items.GroupBy(r => new { r.HesapKodu, r.HesapAdi })
                            .Select(g => new AccountSummaryRow
                            {
                                HesapKodu = g.Key.HesapKodu!,
                                HesapAdi = g.Key.HesapAdi!,
                                Borc = g.Sum(x => x.Borc),
                                Alacak = g.Sum(x => x.Alacak),
                                Bakiye = g.Sum(x => x.Borc) - g.Sum(x => x.Alacak)
                            })
                            .OrderBy(r => r.HesapKodu)
                            .ToList();

            grid.ItemsSource = _current;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_current.Count == 0)
            {
                MessageBox.Show("Önce özetleyin.");
                return;
            }

            var sfd = new Microsoft.Win32.SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "hesap_ozeti.xlsx" };
            if (sfd.ShowDialog() != true) return;

            // Row’ları MuavinRow benzeri geçici tipe dönüştürelim
            var temp = _current.Select(x => new MuavinRow
            {
                HesapKodu = x.HesapKodu,
                HesapAdi = x.HesapAdi,
                Borc = x.Borc,
                Alacak = x.Alacak,
                Tutar = x.Borc + x.Alacak
            }).ToList();

            Muavin.Xml.Parsing.PostProcessors.ExportExcel(temp, sfd.FileName, perAccountBalance: false);
            MessageBox.Show("Excel yazıldı.");
        }

        private sealed class AccountSummaryRow
        {
            public string HesapKodu { get; set; } = "";
            public string HesapAdi { get; set; } = "";
            public decimal Borc { get; set; }
            public decimal Alacak { get; set; }
            public decimal Bakiye { get; set; }
        }
    }
}
