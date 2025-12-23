// CompanyPeriodWindow.xaml.cs 
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Muavin.Xml.Data;

namespace Muavin.Desktop
{
    public partial class CompanyPeriodWindow : Window
    {
        private readonly DbMuavinRepository _repo;

        public string? SelectedCompanyCode { get; private set; }
        public int? SelectedYear { get; private set; }
        public int? SelectedMonth { get; private set; } // artık kullanılmıyor, null kalacak

        public CompanyPeriodWindow(DbMuavinRepository repo)
        {
            InitializeComponent();
            _repo = repo;
            Loaded += async (_, __) => await LoadCompaniesAsync();
        }

        private async Task LoadCompaniesAsync()
        {
            var companies = await _repo.GetCompaniesAsync();
            cbCompany.ItemsSource = companies;
            if (companies.Count > 0) cbCompany.SelectedIndex = 0;
        }

        private async void CompanyChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cbCompany.SelectedValue is not string code || string.IsNullOrWhiteSpace(code))
                return;

            // ✅ Ay yerine YIL listesi
            var years = await _repo.GetYearsAsync(code);

            var items = years
                .Select(y => new
                {
                    Key = y,
                    Display = y.ToString()
                })
                .ToList();

            cbPeriod.ItemsSource = items;
            if (items.Count > 0) cbPeriod.SelectedIndex = 0;
        }

        private void OkClicked(object sender, RoutedEventArgs e)
        {
            if (cbCompany.SelectedValue is not string code || string.IsNullOrWhiteSpace(code))
                return;

            // ✅ Yıl seçimi
            if (cbPeriod.SelectedValue is not int y)
                return;

            SelectedCompanyCode = code;
            SelectedYear = y;
            SelectedMonth = null; // yıl bazlı; ay yok

            DialogResult = true;
            Close();
        }
    }
}