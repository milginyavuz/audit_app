// Muavin.Desktop/ViewModels/TotalsViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;

namespace Muavin.Desktop.ViewModels
{
    public partial class TotalsViewModel : ObservableObject
    {
        [ObservableProperty] private decimal _borc;
        [ObservableProperty] private decimal _alacak;
        [ObservableProperty] private decimal _bakiye;

        public void Reset() => (Borc, Alacak, Bakiye) = (0m, 0m, 0m);
    }
}
