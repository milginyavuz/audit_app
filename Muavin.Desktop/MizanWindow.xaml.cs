// MizanWindow.xaml.cs
using ClosedXML.Excel;
using Microsoft.Win32;
using Muavin.Desktop.ViewModels;
using Muavin.Xml.Parsing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Muavin.Desktop
{
    public partial class MizanWindow : Window
    {
        private readonly MizanViewModel _vm;

        public MizanWindow(IEnumerable<MuavinRow> rows)
        {
            InitializeComponent();

            var list = rows?.ToList() ?? new List<MuavinRow>();
            _vm = new MizanViewModel(list);
            DataContext = _vm;
        }

        // ✅ Drill-down: çift tıkla detay penceresi
        private void MizanGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MizanViewModel vm) return;

            if (sender is not DataGrid dg) return;
            if (dg.SelectedItem is not MizanRow selected) return;

            // boş satırsa çık
            if (string.IsNullOrWhiteSpace(selected.Kebir)) return;

            // drilldown vm oluştur
            var ddVm = new MizanDrillDownViewModel(
                source: vm.SourceRows,      // ✅ kaynağı VM üzerinden al
                selected: selected,
                startDate: vm.StartDate,
                endDate: vm.EndDate
            );

            var win = new MizanDrillDownWindow
            {
                Owner = this,
                DataContext = ddVm
            };

            

            win.ShowDialog();
        }

        private void BtnExportExcel_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as MizanViewModel;
            if (vm == null || vm.Rows == null || vm.Rows.Count == 0)
            {
                MessageBox.Show("Aktarılacak mizan satırı bulunamadı.",
                                "Excel'e Aktar",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Mizanı Excel'e Aktar",
                Filter = "Excel Çalışma Kitabı|*.xlsx",
                FileName = $"Mizan_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("Mizan");

                    int row = 1;

                    // Başlık
                    ws.Cell(row, 1).Value = "Kebir";
                    ws.Cell(row, 2).Value = "Hesap Kodu";
                    ws.Cell(row, 3).Value = "Hesap Adı";
                    ws.Cell(row, 4).Value = "Borç";
                    ws.Cell(row, 5).Value = "Alacak";
                    ws.Cell(row, 6).Value = "Borç Bakiye";
                    ws.Cell(row, 7).Value = "Alacak Bakiye";

                    var headerRange = ws.Range(row, 1, row, 7);
                    headerRange.Style.Font.Bold = true;
                    headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

                    row++;

                    foreach (var item in vm.Rows)
                    {
                        ws.Cell(row, 1).Value = item.Kebir;
                        ws.Cell(row, 2).Value = item.HesapKodu;
                        ws.Cell(row, 3).Value = item.HesapAdi ?? string.Empty;

                        ws.Cell(row, 4).Value = item.Borc;
                        ws.Cell(row, 5).Value = item.Alacak;
                        ws.Cell(row, 6).Value = item.BorcBakiye;
                        ws.Cell(row, 7).Value = item.AlacakBakiye;

                        // Sayı formatı
                        for (int c = 4; c <= 7; c++)
                        {
                            ws.Cell(row, c).Style.NumberFormat.Format = "#,##0.00";
                            ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        }

                        if (item.IsKebirRow)
                        {
                            ws.Row(row).Style.Font.Bold = true;
                            ws.Row(row).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF0F0F0");
                        }

                        row++;
                    }

                    ws.Columns().AdjustToContents();
                    wb.SaveAs(dlg.FileName);
                }

                var result = MessageBox.Show(
                    "Mizan Excel dosyası başarıyla kaydedildi.\nŞimdi açmak ister misiniz?",
                    "Excel'e Aktar",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Excel'e aktarılırken hata oluştu:\n" + ex.Message,
                                "Excel'e Aktar",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }



    }
}
