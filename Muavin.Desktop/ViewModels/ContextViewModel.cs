using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using Muavin.Xml.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

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

        // yeni şirket ekleme (opsiyon)
        [ObservableProperty] private bool _isNewCompany;
        [ObservableProperty] private string? _newCompanyCode;
        [ObservableProperty] private string? _newCompanyName;

        [ObservableProperty] private int _manualYear;
        [ObservableProperty] private string? _errorText;

        [ObservableProperty] private ContextMode _mode = ContextMode.LoadFromDatabase;

        public ObservableCollection<string> SelectedInputs { get; } = new();

        public bool HasValidContext
        {
            get
            {
                if (IsNewCompany)
                    return !string.IsNullOrWhiteSpace(NewCompanyCode) && ManualYear > 0;

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
            // varsayılan
            ManualYear = DateTime.Today.Year;
            _ = LoadCompaniesAsync();
        }

        partial void OnSelectedCompanyChanged(DbMuavinRepository.CompanyItem? value)
        {
            _ = LoadYearsAsync();
            ErrorText = "";
        }

        partial void OnIsNewCompanyChanged(bool value)
        {
            ErrorText = "";
            if (value)
            {
                SelectedCompany = null;
                Years.Clear();
                SelectedYearItem = null;
            }
        }

        partial void OnSelectedYearItemChanged(int? value) => ErrorText = "";
        partial void OnManualYearChanged(int value) => ErrorText = "";

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

            var code = (NewCompanyCode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) return;

            try
            {
                await _repo.EnsureCompanyAsync(code, NewCompanyName);
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
                if (string.IsNullOrWhiteSpace(NewCompanyCode))
                {
                    error = "Yeni şirket için Şirket Kodu zorunlu.";
                    return false;
                }

                if (ManualYear <= 0)
                {
                    error = "Yıl geçersiz.";
                    return false;
                }

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
    }
}
