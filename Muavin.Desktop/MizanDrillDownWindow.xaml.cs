// MizanDrillDownWindow.xaml.cs
using ClosedXML.Excel;
using Microsoft.Win32;
using Muavin.Desktop.ViewModels;
using Muavin.Xml.Parsing;
using System;
using System.Diagnostics;
using System.Windows;

namespace Muavin.Desktop
{
    public partial class MizanDrillDownWindow : Window
    {
        public MizanDrillDownWindow()
        {
            InitializeComponent();
        }

        private void BtnExportExcel_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MizanDrillDownViewModel vm)
            {
                MessageBox.Show("Veri bulunamadı.", "Excel'e Aktar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (vm.PeriodRows == null || vm.PeriodRows.Count == 0)
            {
                MessageBox.Show("Aktarılacak detay satırı bulunamadı.", "Excel'e Aktar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Hesap Detaylarını Excel'e Aktar",
                Filter = "Excel Çalışma Kitabı|*.xlsx",
                FileName = $"HesapDetay_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                using var wb = new XLWorkbook();
                var ws = wb.Worksheets.Add("Detay");

                int row = 1;

                ws.Cell(row, 1).Value = "Tarih";
                ws.Cell(row, 2).Value = "Fiş No";
                ws.Cell(row, 3).Value = "Kebir";
                ws.Cell(row, 4).Value = "Hesap";
                ws.Cell(row, 5).Value = "Hesap Adı";
                ws.Cell(row, 6).Value = "Açıklama";
                ws.Cell(row, 7).Value = "Borç";
                ws.Cell(row, 8).Value = "Alacak";

                var headerRange = ws.Range(row, 1, row, 8);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

                row++;

                foreach (var item in vm.PeriodRows)
                {
                    ws.Cell(row, 1).Value = item.PostingDate?.ToString("dd.MM.yyyy") ?? "";
                    ws.Cell(row, 2).Value = item.EntryNumber;
                    ws.Cell(row, 3).Value = item.Kebir;
                    ws.Cell(row, 4).Value = item.HesapKodu;
                    ws.Cell(row, 5).Value = item.HesapAdi;
                    ws.Cell(row, 6).Value = item.Aciklama;
                    ws.Cell(row, 7).Value = item.Borc;
                    ws.Cell(row, 8).Value = item.Alacak;

                    ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                    row++;
                }

                row++;
                ws.Cell(row, 6).Value = "TOPLAM:";
                ws.Cell(row, 6).Style.Font.Bold = true;

                ws.Cell(row, 7).Value = vm.TotalBorc;
                ws.Cell(row, 8).Value = vm.TotalAlacak;

                ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 7).Style.Font.Bold = true;
                ws.Cell(row, 8).Style.Font.Bold = true;

                ws.Columns().AdjustToContents();
                wb.SaveAs(dlg.FileName);

                var result = MessageBox.Show(
                    "Excel dosyası kaydedildi. Açmak ister misiniz?",
                    "Excel'e Aktar",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Excel'e aktarılırken hata:\n" + ex.Message, "Excel'e Aktar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnFisDetay_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MizanDrillDownViewModel ddVm)
                return;

            // Buton satır context'i MuavinRow
            if (sender is not FrameworkElement fe || fe.DataContext is not MuavinRow row)
                return;

            if (row.PostingDate is null || string.IsNullOrWhiteSpace(row.EntryNumber))
                return;

            // SourceRows null/empty kontrolü (aksi halde fiş detayı boş çıkar)
            if (ddVm.SourceRows == null || ddVm.SourceRows.Count == 0)
            {
                MessageBox.Show("Fiş detayı için kaynak satırlar bulunamadı (SourceRows boş).",
                    "Fiş Detayı",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var win = new FisDetailWindow(
                sourceRows: ddVm.SourceRows,
                entryNumber: row.EntryNumber!,
                postingDate: row.PostingDate.Value
            )
            {
                Owner = this
            };

            win.ShowDialog();
        }
    }
}
