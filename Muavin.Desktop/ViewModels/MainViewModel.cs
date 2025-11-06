// Muavin.Desktop/ViewModels/MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using Muavin.Xml.Parsing;
using Muavin.Xml.Util;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;

namespace Muavin.Desktop.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly EdefterParser _parser = new();

        [ObservableProperty] private string? _statusText;
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private int _progressValue;
        [ObservableProperty] private int _progressMax = 100;
        [ObservableProperty] private string? _totalsText;

        public FilterViewModel Filters { get; } = new();
        public TotalsViewModel Totals { get; } = new();

        // Grid kaynağı
        public ObservableCollection<MuavinRow> Rows { get; } = new();

        // Seçimden gelen yollar
        private readonly List<string> _selectedInputs = new();

        public MainViewModel()
        {
            Rows.CollectionChanged += Rows_CollectionChanged;
            Totals.PropertyChanged += (_, __) => UpdateTotalsText();
        }

        private void Rows_CollectionChanged(object? s, NotifyCollectionChangedEventArgs e) => RecomputeTotals();

        // ================== Dosya/Klasör Seç ==================
        [RelayCommand]
        private async Task Pick()
        {
            var dlg = new CommonOpenFileDialog
            {
                Title = "XML dosyası veya klasör seç",
                EnsurePathExists = true,
                Multiselect = true,
                IsFolderPicker = false
            };
            dlg.Filters.Add(new CommonFileDialogFilter("XML", "*.xml"));
            dlg.Filters.Add(new CommonFileDialogFilter("ZIP", "*.zip"));
            dlg.Filters.Add(new CommonFileDialogFilter("Tümü", "*.*"));

            if (dlg.ShowDialog() != CommonFileDialogResult.Ok)
                return;

            _selectedInputs.Clear();
            _selectedInputs.AddRange(dlg.FileNames);

            StatusText = $"{_selectedInputs.Count} seçim yapıldı. Yükleniyor…";

            await ParseAsync();    // seçer seçmez başlat
        }

        // ================== XML’leri Oku (sade, karşı hesapsız) ==================
        [RelayCommand]
        private async Task ParseAsync()
        {
            if (_selectedInputs.Count == 0)
            {
                StatusText = "Önce dosya/klasör seçin.";
                return;
            }

            try
            {
                IsBusy = true;
                ProgressValue = 0;
                StatusText = "Hazırlanıyor…";

                var logPath = Path.Combine(AppContext.BaseDirectory, "debug.txt");
                Logger.Init(logPath, overwrite: true);
                FieldMap.Load(); // ./config/fieldmap.json

                var xmls = ExpandToXmlFiles(_selectedInputs);
                if (xmls.Count == 0)
                {
                    StatusText = "Seçimlerde XML bulunamadı.";
                    return;
                }

                // temizle
                Rows.Clear();
                Totals.Reset();

                ProgressMax = xmls.Count;
                int parsedCount = 0;

                foreach (var file in xmls)
                {
                    // Her dosyayı parse et ve doğrudan Rows’a ekle
                    var parsed = _parser.Parse(file);
                    foreach (var r in parsed)
                        Rows.Add(r);

                    parsedCount += parsed.Count();

                    ProgressValue++;
                    StatusText = $"Yükleniyor… ({ProgressValue}/{ProgressMax})";
                    await Task.Yield();
                }

                // NOT: Karşı Hesap hesaplanmıyor, filtre de otomatik çalışmıyor
                RecomputeTotals();

                StatusText = $"Tamam — {parsedCount} satır yüklendi, gridde {Rows.Count} satır.";
                Logger.WriteLine($"[OK] Parsed: {parsedCount}, visible: {Rows.Count}");
            }
            catch (Exception ex)
            {
                StatusText = "Hata: " + ex.Message;
                Logger.WriteLine("[FATAL] " + ex);
            }
            finally
            {
                Logger.Close();
                IsBusy = false;
            }
        }

        // ================== Filtre / Temizle ==================
        [RelayCommand]
        private void ApplyFilters()
        {
            // Basit yol: mevcut Rows üzerinde çalış (yeni liste üretip geri yaz)
            var filtered = Rows.ToList();

            // Kebir aralığı
            if (int.TryParse(Filters.KebirBas, out var kb) || int.TryParse(Filters.KebirBit, out var ke))
            {
                int? start = int.TryParse(Filters.KebirBas, out kb) ? kb : null;
                int? end = int.TryParse(Filters.KebirBit, out ke) ? ke : null;

                filtered = filtered.Where(r =>
                {
                    if (!int.TryParse((r.Kebir ?? "0"), out var k)) return false;
                    if (start.HasValue && k < start.Value) return false;
                    if (end.HasValue && k > end.Value) return false;
                    return true;
                }).ToList();
            }

            // Tarih aralığı
            if (Filters.TarihBas.HasValue)
                filtered = filtered.Where(r => r.PostingDate.HasValue && r.PostingDate.Value.Date >= Filters.TarihBas.Value.Date).ToList();
            if (Filters.TarihBit.HasValue)
                filtered = filtered.Where(r => r.PostingDate.HasValue && r.PostingDate.Value.Date <= Filters.TarihBit.Value.Date).ToList();

            // Hesap kodu
            if (!string.IsNullOrWhiteSpace(Filters.HesapKodu))
            {
                var wanted = Filters.HesapKodu
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                filtered = filtered.Where(r => r.HesapKodu != null && wanted.Contains(r.HesapKodu)).ToList();
            }

            // Açıklama
            if (!string.IsNullOrWhiteSpace(Filters.Aciklama))
                filtered = filtered.Where(r => (r.Aciklama ?? "").Contains(Filters.Aciklama!, StringComparison.OrdinalIgnoreCase)).ToList();

            Rows.Clear();
            foreach (var r in filtered) Rows.Add(r);

            RecomputeTotals();
            StatusText = $"Filtre uygulandı. Görünen {Rows.Count} satır.";
        }

        [RelayCommand]
        private void ClearAll()
        {
            _selectedInputs.Clear();
            Rows.Clear();
            Filters.Reset();
            Totals.Reset();
            UpdateTotalsText();
            StatusText = "Temizlendi.";
        }

        // ================== Excel ==================
        [RelayCommand]
        private void ExportExcel()
        {
            if (Rows.Count == 0) { StatusText = "Dışa aktarılacak satır yok."; return; }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = "muavin.xlsx"
            };
            if (sfd.ShowDialog() != true) return;

            // Karşı hesap yok; doğrudan aktar
            PostProcessors.ExportExcel(Rows.ToList(), sfd.FileName, perAccountBalance: true);
            StatusText = "Excel yazıldı: " + sfd.FileName;
        }

        // ================== Detay Penceresi ==================
        [RelayCommand]
        private void OpenEntryDetails()
        {
            var row = Rows.LastOrDefault();
            if (row is null) return;

            var sameEntry = Rows.Where(x => x.EntryNumber == row.EntryNumber).ToList();
            var win = new EntryDetailsWindow(sameEntry) { Owner = App.Current.MainWindow };
            win.ShowDialog();
        }

        // ================== Hesap Özeti ==================
        [RelayCommand]
        private void OpenAccountSummary()
        {
            var win = new AccountSummaryWindow(Rows.ToList()) { Owner = App.Current.MainWindow };
            win.ShowDialog();
        }

        // ================== Sıralama (kolon başlığı çift tık) ==================
        public void ToggleSort(string sortMember)
        {
            var cvs = CollectionViewSource.GetDefaultView(Rows);
            if (cvs is null) return;

            var current = cvs.SortDescriptions.FirstOrDefault();
            cvs.SortDescriptions.Clear();

            var dir = System.ComponentModel.ListSortDirection.Ascending;
            if (current.PropertyName == sortMember && current.Direction == dir)
                dir = System.ComponentModel.ListSortDirection.Descending;

            cvs.SortDescriptions.Add(new System.ComponentModel.SortDescription(sortMember, dir));
            cvs.Refresh();
        }

        // ================== Yardımcılar ==================
        private static List<string> ExpandToXmlFiles(IEnumerable<string> inputs)
        {
            var result = new List<string>();
            foreach (var path in inputs)
            {
                if (Directory.Exists(path))
                {
                    result.AddRange(Directory.EnumerateFiles(path, "*.xml", SearchOption.AllDirectories));
                }
                else if (File.Exists(path))
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == ".xml") result.Add(path);
                    else if (ext == ".zip")
                    {
                        var temp = Path.Combine(Path.GetTempPath(), "Muavin_Unzip_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temp);
                        System.IO.Compression.ZipFile.ExtractToDirectory(path, temp);
                        result.AddRange(Directory.EnumerateFiles(temp, "*.xml", SearchOption.AllDirectories));
                    }
                }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void RecomputeTotals()
        {
            Totals.Reset();
            foreach (var r in Rows)
            {
                Totals.Borc += r.Borc;
                Totals.Alacak += r.Alacak;
            }
            Totals.Bakiye = Totals.Borc - Totals.Alacak;
            UpdateTotalsText();
        }

        private void UpdateTotalsText()
        {
            var tutar = Totals.Borc + Totals.Alacak;
            TotalsText = $"Toplamlar — Borç: {Totals.Borc:N2} | Alacak: {Totals.Alacak:N2} | Tutar: {tutar:N2} | Bakiye: {Totals.Bakiye:N2}";
        }
    }
}
