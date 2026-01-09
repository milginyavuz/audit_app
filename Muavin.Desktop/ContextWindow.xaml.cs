using System;
using System.IO;
using System.Linq;
using System.Reflection;
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

        // ===================== YENİ DÖNEM (YIL) =====================
        private async void AddPeriodClicked(object sender, RoutedEventArgs e)
        {
            ErrorText = "";

            try
            {
                if (cbCompany.SelectedItem is not DbMuavinRepository.CompanyItem company)
                {
                    ErrorText = "Önce bir şirket seçin (dönem eklemek için).";
                    return;
                }

                var companyCode = (company.CompanyCode ?? "").Trim();
                if (string.IsNullOrWhiteSpace(companyCode))
                {
                    ErrorText = "Şirket kodu boş.";
                    return;
                }

                // Yıl textbox doluysa onu kullan, değilse kullanıcıdan al
                int year;
                if (!int.TryParse(tbYear.Text?.Trim(), out year) || year <= 0)
                {
                    var y = Microsoft.VisualBasic.Interaction.InputBox(
                        $"Şirket: {company.CompanyName}\nYeni dönem yılını girin (örn: 2024):",
                        "Yeni Dönem (Yıl) Ekle",
                        DateTime.Today.Year.ToString());

                    if (string.IsNullOrWhiteSpace(y))
                        return; // iptal

                    if (!int.TryParse(y.Trim(), out year) || year <= 0)
                    {
                        ErrorText = "Yıl geçersiz.";
                        return;
                    }
                }

                // Kalıcı yaz: muavin.company_year
                await _repo.EnsureCompanyYearAsync(companyCode, year, Environment.UserName);

                // UI + state güncelle
                tbYear.Text = year.ToString();
                SelectedCompanyCode = companyCode;
                SelectedCompanyName = company.CompanyName;
                SelectedYear = year;

                _state.CompanyCode = SelectedCompanyCode;
                _state.CompanyName = SelectedCompanyName;
                _state.Year = SelectedYear;

                ErrorText = "";
                tbYear.Focus();
                tbYear.SelectAll();
            }
            catch (Exception ex)
            {
                ErrorText = "Yeni dönem eklenemedi: " + ex.Message;
            }
        }


        /// <summary>
        /// DbMuavinRepository içinde dönem/context ekleyen bir metot varsa çağırır.
        /// Metot yoksa sessizce geçer (DB’de ayrı dönem tablosu yoksa zaten sorun değil).
        /// Beklenen olası imzalar:
        /// - Task EnsureCompanyYearAsync(string companyCode, int year)
        /// - Task EnsurePeriodAsync(string companyCode, int year)
        /// - Task EnsureContextAsync(string companyCode, int year)
        /// (opsiyonel 3. param: createdBy/updatedBy string)
        /// </summary>
        private async Task TryEnsurePeriodInRepoAsync(string companyCode, int year)
        {
            // olası isimler
            var names = new[]
            {
                "EnsureCompanyYearAsync",
                "EnsurePeriodAsync",
                "EnsureContextAsync",
                "AddCompanyYearAsync",
                "AddPeriodAsync"
            };

            var t = _repo.GetType();

            MethodInfo? picked = null;
            foreach (var n in names)
            {
                picked = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                          .FirstOrDefault(m =>
                          {
                              if (!string.Equals(m.Name, n, StringComparison.Ordinal)) return false;
                              var ps = m.GetParameters();
                              if (ps.Length < 2) return false;
                              if (ps[0].ParameterType != typeof(string)) return false;
                              if (ps[1].ParameterType != typeof(int)) return false;
                              // 2 param veya 3 param (string updatedBy) veya daha fazlası istemiyoruz
                              if (ps.Length == 2) return true;
                              if (ps.Length == 3 && ps[2].ParameterType == typeof(string)) return true;
                              return false;
                          });

                if (picked != null) break;
            }

            if (picked == null)
                return; // repo'da böyle bir method yok -> sorun değil

            var psPicked = picked.GetParameters();
            object? result;

            if (psPicked.Length == 2)
            {
                result = picked.Invoke(_repo, new object?[] { companyCode, year });
            }
            else
            {
                // 3 param: updatedBy/createdBy gibi
                result = picked.Invoke(_repo, new object?[] { companyCode, year, Environment.UserName });
            }

            if (result is Task task)
                await task;
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
                    // TXT’den sadece VKN çıkar
                    var info = TryParseCompanyFromTxt(picked);
                    taxId = info.taxId;

                    if (string.IsNullOrWhiteSpace(taxId))
                    {
                        ErrorText = "TXT içinde VKN (10 haneli) bulunamadı.";
                        return;
                    }

                    // Ünvan manuel girilecek
                    var manualName = Microsoft.VisualBasic.Interaction.InputBox(
                        $"VKN: {taxId}\nŞirket adını girin:",
                        "Şirket Adı",
                        "");

                    title = string.IsNullOrWhiteSpace(manualName) ? taxId : manualName.Trim();
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

                    // ParseCompanyInfo record alanları: TaxId + EntityName
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


        private async void PickYearClicked(object sender, RoutedEventArgs e)
        {
            ErrorText = "";

            try
            {
                if (cbCompany.SelectedItem is not DbMuavinRepository.CompanyItem company)
                {
                    ErrorText = "Önce şirket seçin.";
                    return;
                }

                var code = (company.CompanyCode ?? "").Trim();
                if (string.IsNullOrWhiteSpace(code))
                {
                    ErrorText = "Şirket kodu boş.";
                    return;
                }

                // company_year + muavin_row union (senin güncel GetYearsAsync)
                var years = await _repo.GetYearsAsync(code);
                if (years == null || years.Count == 0)
                {
                    ErrorText = "Bu şirket için kayıtlı yıl bulunamadı.";
                    return;
                }

                // Basit seçim dialog’u (minimal)
                var picked = SelectFromList("Yıl Seç", "Bu şirkete ait yıllar:", years.Select(y => y.ToString()).ToList());
                if (picked == null) return;

                tbYear.Text = picked;
                tbYear.Focus();
                tbYear.SelectAll();
            }
            catch (Exception ex)
            {
                ErrorText = "Yıllar alınamadı: " + ex.Message;
            }
        }

        /// <summary>
        /// Minimal liste seçim dialog'u (harici pencere dosyası yok, ContextWindow içinde çalışır).
        /// </summary>
        private static string? SelectFromList(string title, string caption, List<string> items)
        {
            var w = new Window
            {
                Title = title,
                Width = 320,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var text = new System.Windows.Controls.TextBlock
            {
                Text = caption,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };

            var list = new System.Windows.Controls.ListBox
            {
                ItemsSource = items,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var ok = new System.Windows.Controls.Button
            {
                Content = "Seç",
                Width = 90,
                Height = 30,
                IsDefault = true,
                Margin = new Thickness(0, 0, 10, 0)
            };

            var cancel = new System.Windows.Controls.Button
            {
                Content = "İptal",
                Width = 90,
                Height = 30,
                IsCancel = true
            };

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            System.Windows.Controls.Grid.SetRow(text, 0);
            System.Windows.Controls.Grid.SetRow(list, 1);
            System.Windows.Controls.Grid.SetRow(buttons, 2);

            grid.Children.Add(text);
            grid.Children.Add(list);
            grid.Children.Add(buttons);

            w.Content = grid;

            string? result = null;

            ok.Click += (_, __) =>
            {
                if (list.SelectedItem is string s && !string.IsNullOrWhiteSpace(s))
                {
                    result = s.Trim();
                    w.DialogResult = true;
                    w.Close();
                }
            };

            list.MouseDoubleClick += (_, __) =>
            {
                if (list.SelectedItem is string s && !string.IsNullOrWhiteSpace(s))
                {
                    result = s.Trim();
                    w.DialogResult = true;
                    w.Close();
                }
            };

            // liste boşsa güvenlik
            if (items.Count > 0) list.SelectedIndex = items.Count - 1;

            var dialogResult = w.ShowDialog();
            return dialogResult == true ? result : null;
        }


    }
}
