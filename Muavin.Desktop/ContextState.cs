//ContextState.cs
using CommunityToolkit.Mvvm.ComponentModel;

namespace Muavin.Desktop
{
    // Uygulama genel bağlamı (şirket + yıl)
    public sealed partial class ContextState : ObservableObject
    {
        [ObservableProperty] private string? _companyCode;
        [ObservableProperty] private string? _companyName;
        [ObservableProperty] private int _year;

        public bool HasContext => !string.IsNullOrWhiteSpace(CompanyCode) && Year > 0;

        public string Display =>
            HasContext
                ? $"{CompanyName ?? CompanyCode} / {Year}"
                : "Bağlam seçilmedi";

        partial void OnCompanyCodeChanged(string? value)
        {
            OnPropertyChanged(nameof(HasContext));
            OnPropertyChanged(nameof(Display));
        }

        partial void OnCompanyNameChanged(string? value)
        {
            OnPropertyChanged(nameof(Display));
        }

        partial void OnYearChanged(int value)
        {
            OnPropertyChanged(nameof(HasContext));
            OnPropertyChanged(nameof(Display));
        }
    }
}
