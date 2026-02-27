// Muavin.Desktop/ViewModels/FilterViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Muavin.Desktop.ViewModels
{
    // ✅ Quick filtre işleci
    public enum QuickFilterOp
    {
        Equals,
        NotEquals,
        Contains,
        NotContains
    }


    public partial class FilterViewModel : ObservableObject
    {
        [ObservableProperty] private string? _kebirBas;
        [ObservableProperty] private string? _kebirBit;

        [ObservableProperty] private DateTime? _tarihBas;
        [ObservableProperty] private DateTime? _tarihBit;

        [ObservableProperty] private string? _hesapKodu;
        [ObservableProperty] private string? _aciklama;

        [ObservableProperty] private bool _excludeAcilis;
        [ObservableProperty] private bool _excludeKapanis;
        // ---- QUICK FILTER STATE (explicit, generator bağımsız) ----
        private string? _quickColumn;
        public string? QuickColumn
        {
            get => _quickColumn;
            set => SetProperty(ref _quickColumn, value);
        }

        private string? _quickValue;
        public string? QuickValue
        {
            get => _quickValue;
            set => SetProperty(ref _quickValue, value);
        }

        private QuickFilterOp _quickOp = QuickFilterOp.Equals;
        public QuickFilterOp QuickOp
        {
            get => _quickOp;
            set => SetProperty(ref _quickOp, value);
        }

        public bool HasQuickFilter =>
            !string.IsNullOrWhiteSpace(QuickColumn) &&
            !string.IsNullOrWhiteSpace(QuickValue);

        public void ClearQuickFilter()
        {
            QuickColumn = null;
            QuickValue = null;
            QuickOp = QuickFilterOp.Equals;
        }

        public void Reset()
        {
            KebirBas = KebirBit = HesapKodu = Aciklama = null;
            TarihBas = TarihBit = null;

            ExcludeAcilis = false;
            ExcludeKapanis = false;

            // ✅ reset ile quick filter da temizlensin
            ClearQuickFilter();
        }
    }
}
