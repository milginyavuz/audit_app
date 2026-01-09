// AgingWindow.xaml.cs
using ClosedXML.Excel;
using Muavin.Desktop.ViewModels;
using Muavin.Xml.Parsing;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;




namespace Muavin.Desktop
{
    public partial class AgingWindow : Window
    {
        public AgingWindow(IEnumerable<MuavinRow> rows)
        {
            InitializeComponent();
            DataContext = new AgingViewModel(rows);
        }

        private void BtnExportExcel_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as AgingViewModel;
            if (vm == null || vm.ReportRows.Count == 0)
            {
                MessageBox.Show("Özette aktarılacak satır bulunamadı.",
                                "Excel'e Aktar",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Yaşlandırma Raporunu Excel'e Aktar",
                Filter = "Excel Çalışma Kitabı|*.xlsx",
                FileName = $"Yaslandirma_{System.DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                using (var wb = new XLWorkbook())
                {
                    // 1) ÖZET SHEET
                    var wsSummary = wb.Worksheets.Add("Özet");
                    int r = 1;

                    // Başlıklar
                    wsSummary.Cell(r, 1).Value = "Hesap Kodu";
                    wsSummary.Cell(r, 2).Value = "Hesap Adı";
                    wsSummary.Cell(r, 3).Value = "0-30 gün";
                    wsSummary.Cell(r, 4).Value = "31-60 gün";
                    wsSummary.Cell(r, 5).Value = "61-90 gün";
                    wsSummary.Cell(r, 6).Value = "91-120 gün";
                    wsSummary.Cell(r, 7).Value = "121-150 gün";
                    wsSummary.Cell(r, 8).Value = "151-180 gün";
                    wsSummary.Cell(r, 9).Value = "181-210 gün";
                    wsSummary.Cell(r, 10).Value = "211-240 gün";
                    wsSummary.Cell(r, 11).Value = "241-270 gün";
                    wsSummary.Cell(r, 12).Value = "271-300 gün";
                    wsSummary.Cell(r, 13).Value = "301-330 gün";
                    wsSummary.Cell(r, 14).Value = "331-365 gün";
                    wsSummary.Cell(r, 15).Value = "Açılış";
                    wsSummary.Cell(r, 16).Value = ">365 gün";
                    wsSummary.Cell(r, 17).Value = "Bakiye";

                    var header = wsSummary.Range(r, 1, r, 17);
                    header.Style.Font.Bold = true;
                    header.Style.Fill.BackgroundColor = XLColor.LightGray;
                    r++;

                    foreach (var row in vm.ReportRows)
                    {
                        wsSummary.Cell(r, 1).Value = row.HesapKodu;
                        wsSummary.Cell(r, 2).Value = row.HesapAdi ?? string.Empty;

                        wsSummary.Cell(r, 3).Value = row.Gun_0_30;
                        wsSummary.Cell(r, 4).Value = row.Gun_31_60;
                        wsSummary.Cell(r, 5).Value = row.Gun_61_90;
                        wsSummary.Cell(r, 6).Value = row.Gun_91_120;
                        wsSummary.Cell(r, 7).Value = row.Gun_121_150;
                        wsSummary.Cell(r, 8).Value = row.Gun_151_180;
                        wsSummary.Cell(r, 9).Value = row.Gun_181_210;
                        wsSummary.Cell(r, 10).Value = row.Gun_211_240;
                        wsSummary.Cell(r, 11).Value = row.Gun_241_270;
                        wsSummary.Cell(r, 12).Value = row.Gun_271_300;
                        wsSummary.Cell(r, 13).Value = row.Gun_301_330;
                        wsSummary.Cell(r, 14).Value = row.Gun_331_365;
                        wsSummary.Cell(r, 15).Value = row.Gun_Acilis;
                        wsSummary.Cell(r, 16).Value = row.Gun_365Ustu;
                        wsSummary.Cell(r, 17).Value = row.Bakiye;

                        // Sayısal kolonları formatla
                        for (int c = 3; c <= 17; c++)
                        {
                            wsSummary.Cell(r, c).Style.NumberFormat.Format = "#,##0.00";
                            wsSummary.Cell(r, c).Style.Alignment.Horizontal =
                                XLAlignmentHorizontalValues.Right;
                        }

                        r++;
                    }

                    wsSummary.Columns().AdjustToContents();

                    // 2) DETAY SHEET
                    if (vm.DetailRows.Count > 0)
                    {
                        var wsDetail = wb.Worksheets.Add("Detay");
                        int rd = 1;

                        wsDetail.Cell(rd, 1).Value = "Hesap Kodu";
                        wsDetail.Cell(rd, 2).Value = "Hesap Adı";
                        wsDetail.Cell(rd, 3).Value = "Tarih";
                        wsDetail.Cell(rd, 4).Value = "Borç";
                        wsDetail.Cell(rd, 5).Value = "Alacak";
                        wsDetail.Cell(rd, 6).Value = "Bakiye";

                        var h2 = wsDetail.Range(rd, 1, rd, 6);
                        h2.Style.Font.Bold = true;
                        h2.Style.Fill.BackgroundColor = XLColor.LightGray;
                        rd++;

                        foreach (var d in vm.DetailRows)
                        {
                            wsDetail.Cell(rd, 1).Value = d.HesapKodu;
                            wsDetail.Cell(rd, 2).Value = d.HesapAdi ?? string.Empty;
                            wsDetail.Cell(rd, 3).Value = d.PostingDate;
                            wsDetail.Cell(rd, 3).Style.DateFormat.Format = "dd.MM.yyyy";

                            wsDetail.Cell(rd, 4).Value = d.Borc;
                            wsDetail.Cell(rd, 5).Value = d.Alacak;
                            wsDetail.Cell(rd, 6).Value = d.Bakiye;

                            for (int c = 4; c <= 6; c++)
                            {
                                wsDetail.Cell(rd, c).Style.NumberFormat.Format = "#,##0.00";
                                wsDetail.Cell(rd, c).Style.Alignment.Horizontal =
                                    XLAlignmentHorizontalValues.Right;
                            }

                            rd++;
                        }

                        wsDetail.Columns().AdjustToContents();
                    }

                    // Kaydet
                    wb.SaveAs(dlg.FileName);
                }

                var ask = MessageBox.Show(
                    "Yaşlandırma Excel dosyası kaydedildi.\nŞimdi açmak ister misiniz?",
                    "Excel'e Aktar",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (ask == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(dlg.FileName)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Excel'e aktarılırken hata oluştu:\n" + ex.Message,
                                "Excel'e Aktar",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }
    }
}
