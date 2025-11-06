// Muavin.Desktop/ViewModels/FilterViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Muavin.Desktop.ViewModels
{
    public partial class FilterViewModel : ObservableObject
    {
        [ObservableProperty] private string? _kebirBas;
        [ObservableProperty] private string? _kebirBit;

        [ObservableProperty] private DateTime? _tarihBas;
        [ObservableProperty] private DateTime? _tarihBit;

        [ObservableProperty] private string? _hesapKodu;
        [ObservableProperty] private string? _aciklama;

        public void Reset()
        {
            KebirBas = KebirBit = HesapKodu = Aciklama = null;
            TarihBas = TarihBit = null;
        }
    }
}
