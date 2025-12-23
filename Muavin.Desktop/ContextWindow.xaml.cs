using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using Muavin.Xml.Data;
using Muavin.Xml.Parsing;

namespace Muavin.Desktop
{
    public partial class ContextWindow : Window
    {
        private readonly DbMuavinRepository _repo;
        private readonly ContextState _state;

        private readonly EdefterParser _edefterParser = new();

        // KeyBinding hatası için: XAML Command binding’leri buraya bakacak
        public ICommand OkCommand { get; }
        public ICommand ExitCommand { get; }

        public string ErrorText
        {
            get => (string)GetValue(ErrorTextProperty);
            set => SetValue(ErrorTextProperty, value);
        }

        public static readonly DependencyProperty ErrorTextProperty =
            DependencyProperty.Register(nameof(ErrorText), typeof(string), typeof(ContextWindow), new PropertyMetadata(""));

        public ContextWindow(ContextState state, DbMuavinRepository repo)
        {
            InitializeComponent();

            _state = state;
            _repo = repo;

            OkCommand = new RelayCommand(() => OkClicked(this, new RoutedEventArgs()));
            ExitCommand = new RelayCommand(() => ExitClicked(this, new RoutedEventArgs()));

            DataContext = this;
            Loaded += async (_, __) => await LoadCompaniesAsync();
        }

        public string? SelectedCompanyCode { get; private set; }
        public string? SelectedCompanyName { get; private set; }
        public int SelectedYear { get; private set; }

        private async Task LoadCompaniesAsync()
        {
            try
            {
                ErrorText = "";
                var companies = await _repo.GetCompaniesAsync();
                cbCompany.ItemsSource = companies;

                // daha önce seçili bağlam varsa onu seçmeye çalış
                if (!string.IsNullOrWhiteSpace(_state.CompanyCode))
                {
                    cbCompany.SelectedValue = _state.CompanyCode;
                    SelectedCompanyCode = _state.CompanyCode;
                    SelectedCompanyName = _state.CompanyName;
                    if (_state.Year > 0) tbYear.Text = _state.Year.ToString();
                }
                else if (companies.Count > 0)
                {
                    cbCompany.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                ErrorText = "Şirket listesi alınamadı: " + ex.Message;
            }
        }

        private void Year_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // sadece rakam
            e.Handled = e.Text.Any(ch => ch < '0' || ch > '9');
        }

        private void CompanyChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cbCompany.SelectedItem is DbMuavinRepository.CompanyItem ci)
            {
                SelectedCompanyCode = ci.CompanyCode;
                SelectedCompanyName = ci.CompanyName;
            }
        }

        private void OkClicked(object sender, RoutedEventArgs e)
        {
            ErrorText = "";

            if (cbCompany.SelectedItem is not DbMuavinRepository.CompanyItem company)
            {
                ErrorText = "Lütfen bir şirket seçin.";
                return;
            }

            if (!int.TryParse(tbYear.Text?.Trim(), out var year) || year <= 0)
            {
                ErrorText = "Yıl geçersiz.";
                return;
            }

            SelectedCompanyCode = company.CompanyCode;
            SelectedCompanyName = company.CompanyName;
            SelectedYear = year;

            // state güncelle
            _state.CompanyCode = SelectedCompanyCode;
            _state.CompanyName = SelectedCompanyName;
            _state.Year = SelectedYear;

            DialogResult = true;
            Close();
        }

        private void ExitClicked(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ===================== YENİ ŞİRKET (XML/ZIP/TXT) =====================
        private async void AddCompanyFromEdefterClicked(object sender, RoutedEventArgs e)
        {
            ErrorText = "";

            try
            {
                var dlg = new CommonOpenFileDialog
                {
                    Title = "Şirket eklemek için XML / ZIP / TXT seç",
                    EnsurePathExists = true,
                    Multiselect = false,
                    IsFolderPicker = false
                };
                dlg.Filters.Add(new CommonFileDialogFilter("XML", "*.xml"));
                dlg.Filters.Add(new CommonFileDialogFilter("ZIP", "*.zip"));
                dlg.Filters.Add(new CommonFileDialogFilter("TXT", "*.txt"));
                dlg.Filters.Add(new CommonFileDialogFilter("Tümü", "*.*"));

                if (dlg.ShowDialog() != CommonFileDialogResult.Ok)
                    return;

                var picked = dlg.FileName;
                var ext = Path.GetExtension(picked).ToLowerInvariant();

                string? taxId = null;
                string? title = null;

                if (ext == ".txt")
                {
                    // TXT’den çıkar (VKN + Ünvan)
                    var info = TryParseCompanyFromTxt(picked);
                    taxId = info.taxId;
                    title = info.title;
                }
                else
                {
                    // XML veya ZIP -> XML çöz, sonra ParseCompanyInfo
                    var xmlPath = ResolveAnyXml(picked);
                    if (xmlPath == null)
                    {
                        ErrorText = "Seçimde XML bulunamadı.";
                        return;
                    }

                    var info = _edefterParser.ParseCompanyInfo(xmlPath);

                    // ParseCompanyInfo record alanları: TaxId + EntityName (senin kullanımına göre)
                    taxId = (info.TaxId ?? "").Trim();
                    title = (info.EntityName ?? "").Trim();
                }

                if (string.IsNullOrWhiteSpace(taxId))
                {
                    ErrorText = "Vergi No (VKN/taxID) bulunamadı.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(title))
                    title = taxId; // fallback

                // DB’ye yaz (muavin.company)
                await _repo.EnsureCompanyAsync(taxId, title);

                // Listeyi yenile ve yeni şirketi seç
                await LoadCompaniesAsync();
                cbCompany.SelectedValue = taxId;

                // state’i de güncelle
                _state.CompanyCode = taxId;
                _state.CompanyName = title;

                SelectedCompanyCode = taxId;
                SelectedCompanyName = title;

                tbYear.Focus();
                tbYear.SelectAll();
            }
            catch (Exception ex)
            {
                ErrorText = "Yeni şirket eklenemedi: " + ex.Message;
            }
        }

        private static string? ResolveAnyXml(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (File.Exists(path) && Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                return path;

            if (File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var temp = Path.Combine(Path.GetTempPath(), "Muavin_CompanyZip_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);

                System.IO.Compression.ZipFile.ExtractToDirectory(path, temp, overwriteFiles: true);

                return Directory.EnumerateFiles(temp, "*.xml", SearchOption.AllDirectories).FirstOrDefault();
            }

            return null;
        }

        // ---------------- TXT -> Company (heuristic) ----------------

        // VKN: 10 haneli sayı (TR VKN)
        private static readonly Regex RxVkn =
            new Regex(@"\b(\d{10})\b", RegexOptions.Compiled);

        // Ünvan yakalamaya çalış: "UNVAN: ..." / "ÜNVAN: ..." / "FIRMA: ..." gibi
        private static readonly Regex RxTitleLine =
            new Regex(@"(?im)^(?:\s*(?:unvan|ünvan|firma|şirket|company)\s*[:\-]\s*)(.+)\s*$",
                      RegexOptions.Compiled);

        private static (string? taxId, string? title) TryParseCompanyFromTxt(string txtPath)
        {
            if (string.IsNullOrWhiteSpace(txtPath) || !File.Exists(txtPath))
                return (null, null);

            string text;
            try
            {
                text = File.ReadAllText(txtPath);
            }
            catch
            {
                return (null, null);
            }

            // title
            string? title = null;
            var mTitle = RxTitleLine.Match(text);
            if (mTitle.Success)
                title = mTitle.Groups[1].Value.Trim();

            // tax id (VKN)
            string? taxId = null;
            var mVkn = RxVkn.Match(text);
            if (mVkn.Success)
                taxId = mVkn.Groups[1].Value.Trim();

            // Aşırı “rastgele” 10 hane yakalama riskini azaltmak için:
            // Eğer metinde "VKN" / "Vergi" / "Tax" vb. varsa ilk eşleşmeyi alıyoruz,
            // yoksa yine de eşleşme varsa alıyoruz (sende format değişebilir).
            if (!string.IsNullOrWhiteSpace(taxId))
            {
                // 0000... gibi gelirse temizle
                taxId = taxId.TrimStart('0');
                if (taxId.Length == 0) taxId = null;
            }

            return (taxId, title);
        }

        // Mini ICommand helper (MVVM toolkit kullanmadan)
        private sealed class RelayCommand : ICommand
        {
            private readonly Action _execute;
            private readonly Func<bool>? _canExecute;

            public RelayCommand(Action execute, Func<bool>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
            public void Execute(object? parameter) => _execute();
            public event EventHandler? CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
