// ContextViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using Muavin.Xml.Data;
using Muavin.Xml.Parsing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Muavin.Desktop.ViewModels
{
    public enum ContextMode
    {
        LoadFromDatabase = 0,
        ImportToDatabase = 1
    }

    public sealed partial class ContextViewModel : ObservableObject
    {
        private readonly DbMuavinRepository _repo = new();

        public ObservableCollection<DbMuavinRepository.CompanyItem> Companies { get; } = new();
        public ObservableCollection<int> Years { get; } = new();

        [ObservableProperty] private DbMuavinRepository.CompanyItem? _selectedCompany;
        [ObservableProperty] private int? _selectedYearItem;

        // Yeni şirket: code otomatik, isim zorunlu
        [ObservableProperty] private bool _isNewCompany;
        [ObservableProperty] private string? _newCompanyCode; // UI'da göstermene gerek yok
        [ObservableProperty] private string? _newCompanyName; // zorunlu

        [ObservableProperty] private int _manualYear;
        [ObservableProperty] private string? _errorText;

        [ObservableProperty] private ContextMode _mode = ContextMode.LoadFromDatabase;

        public ObservableCollection<string> SelectedInputs { get; } = new();

        public bool HasValidContext
        {
            get
            {
                if (IsNewCompany)
                    return !string.IsNullOrWhiteSpace(NewCompanyName) && ManualYear > 0;

                return SelectedCompany != null && SelectedYear > 0;
            }
        }

        public string SelectedCompanyCode =>
            IsNewCompany ? (NewCompanyCode ?? "").Trim() : (SelectedCompany?.CompanyCode ?? "").Trim();

        public string? SelectedCompanyName =>
            IsNewCompany ? (string.IsNullOrWhiteSpace(NewCompanyName) ? null : NewCompanyName.Trim())
                         : (SelectedCompany?.CompanyName ?? null);

        public int SelectedYear =>
            IsNewCompany ? ManualYear : (SelectedYearItem ?? 0);

        public ContextViewModel()
        {
            ManualYear = DateTime.Today.Year;
            _ = LoadCompaniesAsync();
        }

        partial void OnSelectedCompanyChanged(DbMuavinRepository.CompanyItem? value)
        {
            _ = LoadYearsAsync();
            ErrorText = "";
            OnPropertyChanged(nameof(HasValidContext));
        }

        partial void OnIsNewCompanyChanged(bool value)
        {
            ErrorText = "";
            if (value)
            {
                SelectedCompany = null;
                Years.Clear();
                SelectedYearItem = null;

                NewCompanyName = "";
                NewCompanyCode = "";
                ManualYear = DateTime.Today.Year;
            }

            OnPropertyChanged(nameof(HasValidContext));
        }

        partial void OnSelectedYearItemChanged(int? value)
        {
            ErrorText = "";
            OnPropertyChanged(nameof(HasValidContext));
        }

        partial void OnManualYearChanged(int value)
        {
            ErrorText = "";
            OnPropertyChanged(nameof(HasValidContext));
        }

        partial void OnNewCompanyNameChanged(string? value)
        {
            ErrorText = "";
            var name = (value ?? "").Trim();
            NewCompanyCode = string.IsNullOrWhiteSpace(name) ? "" : BuildCompanyCodeFromName(name);
            OnPropertyChanged(nameof(HasValidContext));
        }

        [RelayCommand]
        private async Task LoadCompaniesAsync()
        {
            try
            {
                Companies.Clear();
                var list = await _repo.GetCompaniesAsync();
                foreach (var c in list)
                    Companies.Add(c);
            }
            catch (Exception ex)
            {
                ErrorText = "Şirket listesi yüklenemedi: " + ex.Message;
            }
        }

        private async Task LoadYearsAsync()
        {
            Years.Clear();
            SelectedYearItem = null;
            if (SelectedCompany == null) return;

            try
            {
                var ys = await _repo.GetYearsAsync(SelectedCompany.CompanyCode);
                foreach (var y in ys)
                    Years.Add(y);

                if (Years.Count > 0)
                    SelectedYearItem = Years.Max();
            }
            catch (Exception ex)
            {
                ErrorText = "Yıl listesi yüklenemedi: " + ex.Message;
            }
        }

        [RelayCommand]
        private void PickFiles()
        {
            var dlg = new CommonOpenFileDialog
            {
                Title = "XML/TXT dosyası veya ZIP seç (veritabanına eklenecek)",
                EnsurePathExists = true,
                Multiselect = true,
                IsFolderPicker = false
            };

            dlg.Filters.Add(new CommonFileDialogFilter("XML", "*.xml"));
            dlg.Filters.Add(new CommonFileDialogFilter("TXT", "*.txt"));
            dlg.Filters.Add(new CommonFileDialogFilter("ZIP", "*.zip"));
            dlg.Filters.Add(new CommonFileDialogFilter("Tümü", "*.*"));

            if (dlg.ShowDialog() != CommonFileDialogResult.Ok) return;

            SelectedInputs.Clear();
            foreach (var f in dlg.FileNames.Distinct(StringComparer.OrdinalIgnoreCase))
                SelectedInputs.Add(f);

            ErrorText = "";
        }

        [RelayCommand]
        private async Task EnsureCompanyIfNeededAsync()
        {
            if (!IsNewCompany) return;

            var name = (NewCompanyName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ErrorText = "Şirket adı zorunlu.";
                return;
            }

            var code = (NewCompanyCode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code))
                code = BuildCompanyCodeFromName(name);

            try
            {
                await _repo.EnsureCompanyAsync(code, name);
                await LoadCompaniesAsync();
                SelectedCompany = Companies.FirstOrDefault(c => string.Equals(c.CompanyCode, code, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                ErrorText = "Şirket eklenemedi: " + ex.Message;
            }
        }

        public bool Validate(out string error)
        {
            error = "";

            if (Mode == ContextMode.ImportToDatabase && SelectedInputs.Count == 0)
            {
                error = "Veritabanına veri eklemek için dosya seçmelisiniz.";
                return false;
            }

            if (IsNewCompany)
            {
                if (string.IsNullOrWhiteSpace(NewCompanyName))
                {
                    error = "Yeni şirket için Şirket Adı zorunlu.";
                    return false;
                }

                if (ManualYear <= 0)
                {
                    error = "Yıl geçersiz.";
                    return false;
                }

                var code = (NewCompanyCode ?? "").Trim();
                if (string.IsNullOrWhiteSpace(code))
                    NewCompanyCode = BuildCompanyCodeFromName(NewCompanyName!.Trim());

                return true;
            }

            if (SelectedCompany == null)
            {
                error = "Şirket seçmelisiniz.";
                return false;
            }

            if (SelectedYear <= 0)
            {
                error = "Yıl seçmelisiniz.";
                return false;
            }

            return true;
        }

        // ===================== ✅ IMPORT COMMAND (TXT kontrol + parse + insert) =====================

        [RelayCommand]
        private async Task ImportSelectedAsync()
        {
            ErrorText = "";

            if (!Validate(out var err))
            {
                ErrorText = err;
                return;
            }

            // Yeni şirket modundaysa önce DB’ye company yaz
            if (IsNewCompany)
            {
                await EnsureCompanyIfNeededAsync();
                if (!string.IsNullOrWhiteSpace(ErrorText)) return;
            }

            var companyCode = SelectedCompanyCode;
            var companyName = SelectedCompanyName;
            if (string.IsNullOrWhiteSpace(companyCode))
            {
                ErrorText = "Şirket kodu üretilemedi.";
                return;
            }

            var inputs = SelectedInputs.Where(File.Exists).ToList();
            if (inputs.Count == 0)
            {
                ErrorText = "Geçerli dosya yok.";
                return;
            }

            try
            {
                // Basit: her dosyayı ayrı import et (source_file ayrı olsun)
                foreach (var file in inputs)
                {
                    var ext = (Path.GetExtension(file) ?? "").ToLowerInvariant();

                    if (ext == ".txt")
                    {
                        await ImportTxtOneAsync(companyCode, companyName, file);
                    }
                    else if (ext == ".xml" || ext == ".zip")
                    {
                        // Senin XML/ZIP import pipeline burada çalışmalı.
                        // Şimdilik sadece “bu kısım mevcut import kodun neredeyse oraya bağla” notu.
                        ErrorText = $"XML/ZIP import bu ViewModel'de bağlı değil. (Dosya: {Path.GetFileName(file)})";
                        return;
                    }
                    else
                    {
                        // ignore
                    }
                }

                MessageBox.Show("Import tamamlandı.", "Muavin", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ErrorText = "Import hatası: " + ex.Message;
            }
        }

        private async Task ImportTxtOneAsync(string companyCode, string? companyName, string txtPath)
        {
            var p = new TxtMuavinParser();
            int py, pm;

            var rows = p.Parse(txtPath, companyCode, out py, out pm);
            var meta = p.LastMeta;

            // ✅ Genel kontrol: header yok / hiç satır yok
            if (rows.Count == 0)
            {
                var msg = $"TXT okunamadı veya satır çıkmadı.\n\nDosya: {Path.GetFileName(txtPath)}\n" +
                          $"Delimiter: {meta.Delimiter}\nParsedRow: {meta.ParsedRowCount}\nSkippedRow: {meta.SkippedRowCount}";
                var r0 = MessageBox.Show(msg + "\n\nBu dosyayı atlayalım mı?", "TXT Kontrol", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r0 == MessageBoxResult.Yes) return;
                throw new InvalidOperationException("TXT parse sonucu boş.");
            }

            // ✅ Etiket analizi sonrası “şüpheli dosya” kontrolleri
            // 1) Çoklu ay / dağınık aralık
            if (meta.DistinctYearMonthCount >= 2)
            {
                var msg = $"TXT dosyası birden fazla ay içeriyor görünüyor.\n\n" +
                          $"Dosya: {Path.GetFileName(txtPath)}\n" +
                          $"Min: {meta.MinDate:yyyy-MM-dd}\nMax: {meta.MaxDate:yyyy-MM-dd}\n" +
                          $"Distinct YM: {meta.DistinctYearMonthCount}\n\n" +
                          $"Devam edelim mi?";
                var r = MessageBox.Show(msg, "TXT Kontrol", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            // 2) Skip oranı aşırı yüksekse
            if (meta.ParsedRowCount > 0)
            {
                var total = meta.ParsedRowCount + meta.SkippedRowCount;
                if (total > 0)
                {
                    var skipRate = (double)meta.SkippedRowCount / total;
                    if (skipRate >= 0.20) // %20+
                    {
                        var msg = $"TXT dosyasında atlanan satır oranı yüksek.\n\n" +
                                  $"Dosya: {Path.GetFileName(txtPath)}\n" +
                                  $"Parsed: {meta.ParsedRowCount}\nSkipped: {meta.SkippedRowCount} (~{skipRate:P0})\n" +
                                  $"Delimiter: {meta.Delimiter}\nEncodingFallback: {meta.UsedFallbackEncoding}\n\n" +
                                  $"Devam edelim mi?";
                        var r = MessageBox.Show(msg, "TXT Kontrol", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (r != MessageBoxResult.Yes) return;
                    }
                }
            }

            // 3) Kullanıcının seçtiği yıl ile TXT içeriği uyuşmuyor olabilir
            // (TXT’te periodYear minDate’den geliyor)
            var selectedYear = SelectedYear;
            if (selectedYear > 0 && meta.MinDate.HasValue && meta.MinDate.Value.Year != selectedYear)
            {
                var msg = $"Seçili yıl ({selectedYear}) ile TXT içindeki yıl ({meta.MinDate.Value.Year}) farklı.\n\n" +
                          $"Dosya: {Path.GetFileName(txtPath)}\nMin: {meta.MinDate:yyyy-MM-dd}\nMax: {meta.MaxDate:yyyy-MM-dd}\n\n" +
                          $"Yine de import edilsin mi?";
                var r = MessageBox.Show(msg, "TXT Kontrol", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }

            // ✅ DB import
            await _repo.BulkInsertRowsAsync(
                companyCode: companyCode,
                rows: rows,
                sourceFile: txtPath,
                replaceExistingForSameSource: true,
                companyName: companyName,
                replaceMode: DbMuavinRepository.ImportReplaceMode.MonthsInPayload
            );
        }

        // ===================== helpers =====================

        private static string BuildCompanyCodeFromName(string name)
        {
            var norm = NormalizeNameForCode(name);

            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));
            var hex = Convert.ToHexString(bytes).ToLowerInvariant();
            return "C-" + hex.Substring(0, 12);
        }

        private static string NormalizeNameForCode(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant()
                .Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u')
                .Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c');

            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (char.IsWhiteSpace(ch)) sb.Append(' ');
            }

            return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
