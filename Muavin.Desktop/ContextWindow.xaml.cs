// ContextWindow.xaml.cs
using System;
using System.Collections.Generic;
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
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
            List<string>? tempDirs = null;

            try
            {
                var dlg = new CommonOpenFileDialog
                {
                    Title = "Şirket eklemek için XML / ZIP / TXT seç (çoklu seçilebilir)",
                    EnsurePathExists = true,
                    Multiselect = true,
                    IsFolderPicker = false
                };

                dlg.Filters.Add(new CommonFileDialogFilter("XML", "*.xml"));
                dlg.Filters.Add(new CommonFileDialogFilter("ZIP", "*.zip"));
                dlg.Filters.Add(new CommonFileDialogFilter("TXT", "*.txt"));
                dlg.Filters.Add(new CommonFileDialogFilter("Tümü", "*.*"));

                if (dlg.ShowDialog() != CommonFileDialogResult.Ok)
                    return;

                var picks = dlg.FileNames?.Where(File.Exists).Distinct().ToList() ?? new List<string>();
                if (picks.Count == 0)
                {
                    ErrorText = "Dosya seçilmedi.";
                    return;
                }

                // Şirket adı önerisi için “en uygun kaynak” (XML veya TXT veya ZIP->XML)
                var resolved = ResolveCompanySourceFromSelection(picks, out tempDirs);
                if (resolved == null)
                {
                    ErrorText = "Seçimde şirket bilgisi okunabilecek XML/TXT bulunamadı.";
                    return;
                }

                string? taxId = null;     // varsa VKN/taxId -> company_code olarak kullanacağız
                string? autoTitle = null; // varsa InputBox default

                if (resolved.Kind == CompanySourceKind.Txt)
                {
                    var info = TryParseCompanyFromTxt(resolved.Path);
                    taxId = (info.taxId ?? "").Trim();
                    autoTitle = (info.title ?? "").Trim();
                    // ✅ VKN yoksa sorun değil
                    if (string.IsNullOrWhiteSpace(taxId)) taxId = null;
                }
                else
                {
                    var info = _edefterParser.ParseCompanyInfo(resolved.Path);
                    taxId = (info.TaxId ?? "").Trim();
                    autoTitle = (info.EntityName ?? "").Trim();
                    // ✅ taxId yoksa sorun değil
                    if (string.IsNullOrWhiteSpace(taxId)) taxId = null;
                }

                // ✅ Her durumda şirket adını sor (auto-fill varsa doldur)
                var suggested = string.IsNullOrWhiteSpace(autoTitle) ? "" : autoTitle.Trim();

                var manualName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Şirket adını kontrol edin / gerekirse düzeltin:",
                    "Şirket Adı",
                    suggested
                );

                if (string.IsNullOrWhiteSpace(manualName))
                {
                    ErrorText = "Şirket adı zorunlu. İsterseniz yeniden deneyin.";
                    return;
                }

                var title = manualName.Trim().Replace("\r", " ").Replace("\n", " ").Trim();
                if (title.Length > 200) title = title.Substring(0, 200).Trim();

                // ✅ Duplicate kontrol: benzer isimli şirket var mı?
                var existing = await CheckDuplicateAndPickExistingAsync(title);
                if (existing != null)
                {
                    // kullanıcı mevcut şirketi seçti -> yeni ekleme yapma, onu seç
                    await LoadCompaniesAsync();
                    cbCompany.SelectedValue = existing.CompanyCode;

                    _state.CompanyCode = existing.CompanyCode;
                    _state.CompanyName = existing.CompanyName;

                    SelectedCompanyCode = existing.CompanyCode;
                    SelectedCompanyName = existing.CompanyName;

                    tbYear.Focus();
                    tbYear.SelectAll();
                    return;
                }


                // ✅ company_code: VKN varsa onu kullan, yoksa hash code üret
                var companyCode = taxId ?? BuildCompanyCodeFromName(title);

                await _repo.EnsureCompanyAsync(companyCode, title);

                // Listeyi yenile ve yeni şirketi seç
                await LoadCompaniesAsync();
                cbCompany.SelectedValue = companyCode;

                // state güncelle
                _state.CompanyCode = companyCode;
                _state.CompanyName = title;

                SelectedCompanyCode = companyCode;
                SelectedCompanyName = title;

                tbYear.Focus();
                tbYear.SelectAll();
            }
            catch (Exception ex)
            {
                ErrorText = "Yeni şirket eklenemedi: " + ex.Message;
            }
            finally
            {
                CleanupTempDirs(tempDirs);
            }
        }

        // TXT parser (aynı namespace: Muavin.Xml.Parsing)
        private readonly TxtMuavinParser _txtParser = new();

        private async void ImportTxtClicked(object sender, RoutedEventArgs e)
        {
            ErrorText = "";

            try
            {
                if (cbCompany.SelectedItem is not DbMuavinRepository.CompanyItem company)
                {
                    ErrorText = "Önce şirket seçin.";
                    return;
                }

                if (!int.TryParse(tbYear.Text?.Trim(), out var selectedYear) || selectedYear <= 0)
                {
                    ErrorText = "Yıl geçersiz.";
                    return;
                }

                var dlg = new CommonOpenFileDialog
                {
                    Title = "TXT seç (muavin import)",
                    EnsurePathExists = true,
                    Multiselect = true,
                    IsFolderPicker = false
                };
                dlg.Filters.Add(new CommonFileDialogFilter("TXT", "*.txt"));

                if (dlg.ShowDialog() != CommonFileDialogResult.Ok)
                    return;

                var files = dlg.FileNames?.Where(File.Exists).Distinct().ToList() ?? new List<string>();
                if (files.Count == 0)
                {
                    ErrorText = "Dosya seçilmedi.";
                    return;
                }

                foreach (var f in files)
                    await ImportTxtOneAsync(company.CompanyCode, company.CompanyName, selectedYear, f);

                MessageBox.Show("TXT import tamamlandı.", "Muavin", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ErrorText = "TXT import hatası: " + ex.Message;
            }
        }

        private async Task ImportTxtOneAsync(string companyCode, string companyName, int selectedYear, string txtPath)
        {
            int py, pm;
            var rows = _txtParser.Parse(txtPath, companyCode, out py, out pm);
            var meta = _txtParser.LastMeta;

            if (rows.Count == 0)
            {
                var ask = MessageBox.Show(
                    $"TXT satır çıkmadı.\nDosya: {Path.GetFileName(txtPath)}\nDelimiter: {meta.Delimiter}\nParsed:{meta.ParsedRowCount} Skipped:{meta.SkippedRowCount}\nAtlayalım mı?",
                    "TXT Kontrol",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (ask == MessageBoxResult.Yes) return;
                throw new InvalidOperationException("TXT parse sonucu boş.");
            }

            if (meta.DistinctYearMonthCount >= 2)
            {
                var ask = MessageBox.Show(
                    $"TXT birden fazla ay içeriyor.\nDosya: {Path.GetFileName(txtPath)}\nMin:{meta.MinDate:yyyy-MM-dd} Max:{meta.MaxDate:yyyy-MM-dd}\nDevam?",
                    "TXT Kontrol",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (ask != MessageBoxResult.Yes) return;
            }

            var total = meta.ParsedRowCount + meta.SkippedRowCount;
            if (total > 0)
            {
                var skipRate = (double)meta.SkippedRowCount / total;
                if (skipRate >= 0.20)
                {
                    var ask = MessageBox.Show(
                        $"TXT atlanan satır oranı yüksek (~{skipRate:P0}).\nDosya: {Path.GetFileName(txtPath)}\nParsed:{meta.ParsedRowCount} Skipped:{meta.SkippedRowCount}\nDevam?",
                        "TXT Kontrol",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (ask != MessageBoxResult.Yes) return;
                }
            }

            if (meta.MinDate.HasValue && meta.MinDate.Value.Year != selectedYear)
            {
                var ask = MessageBox.Show(
                    $"Seçili yıl ({selectedYear}) ile TXT yılı ({meta.MinDate.Value.Year}) farklı.\nDosya: {Path.GetFileName(txtPath)}\nDevam?",
                    "TXT Kontrol",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (ask != MessageBoxResult.Yes) return;
            }

            await _repo.BulkInsertRowsAsync(
                companyCode: companyCode,
                rows: rows,
                sourceFile: txtPath,
                replaceExistingForSameSource: true,
                companyName: companyName,
                replaceMode: DbMuavinRepository.ImportReplaceMode.MonthsInPayload
            );
        }




        // ✅ Ünvanı “sadece şirket adı” olarak güvenli almak için: kullanıcıya onay + düzeltme
        // - default: autoTitle (varsa) yoksa taxId
        // - kullanıcı boş bırakırsa default kullanılır
        // Not: InputBox Cancel'da çoğunlukla "" döndürüyor; burada "" -> default'a düşer.
        // Gerçek "iptal" akışı istersen ayrıca custom WPF dialog yazabiliriz.
        private static string? PromptCompanyName(string taxId, string? autoTitle)
        {
            var def = string.IsNullOrWhiteSpace(autoTitle) ? taxId : autoTitle.Trim();

            var input = Microsoft.VisualBasic.Interaction.InputBox(
                $"VKN: {taxId}\nŞirket adı (ünvan). Otomatik yakalandıysa kontrol edin, değilse yazın:",
                "Şirket Ünvanı Onayı",
                def
            );

            if (input == null) return null;

            var s = input.Trim();

            // kullanıcı boş geçtiyse default'a düş
            if (string.IsNullOrWhiteSpace(s))
                return def;

            return s;
        }

        // temp klasör cleanup (ZIP açarken oluşturulan klasörler için)
        private static void CleanupTempDirs(List<string>? tempDirs)
        {
            if (tempDirs == null || tempDirs.Count == 0) return;

            foreach (var d in tempDirs)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                        Directory.Delete(d, recursive: true);
                }
                catch
                {
                    // ignore (temp cleanup fail should not crash)
                }
            }
        }

        private enum CompanySourceKind { Xml, Txt }

        private sealed record CompanySource(CompanySourceKind Kind, string Path);

        private CompanySource? ResolveCompanySourceFromSelection(List<string> picks, out List<string>? tempDirs)
        {
            tempDirs = new List<string>();

            // 1) Önce direkt XML'ler: TaxId bulunanı yakala
            var xmls = picks
                .Where(p => Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var x in xmls)
            {
                try
                {
                    var info = _edefterParser.ParseCompanyInfo(x);
                    if (!string.IsNullOrWhiteSpace(info.TaxId))
                        return new CompanySource(CompanySourceKind.Xml, x);
                }
                catch { /* ignore */ }
            }

            // 2) ZIP: içinden XML çıkar, TaxId bulunanı yakala
            var zips = picks
                .Where(p => Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var z in zips)
            {
                try
                {
                    var temp = Path.Combine(Path.GetTempPath(), "Muavin_CompanyZip_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    System.IO.Compression.ZipFile.ExtractToDirectory(z, temp, overwriteFiles: true);
                    tempDirs.Add(temp);

                    var xmlInZipAll = Directory.EnumerateFiles(temp, "*.xml", SearchOption.AllDirectories).ToList();

                    foreach (var x in xmlInZipAll)
                    {
                        try
                        {
                            var info = _edefterParser.ParseCompanyInfo(x);
                            if (!string.IsNullOrWhiteSpace(info.TaxId))
                                return new CompanySource(CompanySourceKind.Xml, x);
                        }
                        catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }
            }

            // 3) TXT fallback
            var txt = picks.FirstOrDefault(p => Path.GetExtension(p).Equals(".txt", StringComparison.OrdinalIgnoreCase));
            if (txt != null) return new CompanySource(CompanySourceKind.Txt, txt);

            return null;
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
            new Regex(@"(?im)^(?:\s*(?:ticaret\s*unvani|unvan|ünvan|firma|şirket|sirket|company|organizationidentifier|organization\s*identifier)\s*[:\-]\s*)(.+)\s*$",
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

        private async void ImportInputsClicked(object sender, RoutedEventArgs e)
        {
            ErrorText = "";
            List<string>? tempDirs = null;

            try
            {
                if (cbCompany.SelectedItem is not DbMuavinRepository.CompanyItem company)
                {
                    ErrorText = "Önce şirket seçin.";
                    return;
                }

                if (!int.TryParse(tbYear.Text?.Trim(), out var selectedYear) || selectedYear <= 0)
                {
                    ErrorText = "Yıl geçersiz.";
                    return;
                }

                var dlg = new CommonOpenFileDialog
                {
                    Title = "İçe Aktar: XML / ZIP / TXT seç (çoklu seçilebilir)",
                    EnsurePathExists = true,
                    Multiselect = true,
                    IsFolderPicker = false
                };

                dlg.Filters.Add(new CommonFileDialogFilter("XML", "*.xml"));
                dlg.Filters.Add(new CommonFileDialogFilter("ZIP", "*.zip"));
                dlg.Filters.Add(new CommonFileDialogFilter("TXT", "*.txt"));
                dlg.Filters.Add(new CommonFileDialogFilter("Tümü", "*.*"));

                if (dlg.ShowDialog() != CommonFileDialogResult.Ok)
                    return;

                var picks = dlg.FileNames?.Where(File.Exists).Distinct().ToList() ?? new List<string>();
                if (picks.Count == 0)
                {
                    ErrorText = "Dosya seçilmedi.";
                    return;
                }

                var companyCode = (company.CompanyCode ?? "").Trim();
                var companyName = (company.CompanyName ?? "").Trim();

                if (string.IsNullOrWhiteSpace(companyCode))
                {
                    ErrorText = "Şirket kodu boş.";
                    return;
                }

                // TXT import
                var txtFiles = picks.Where(p => Path.GetExtension(p).Equals(".txt", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var f in txtFiles)
                    await ImportTxtOneAsync(companyCode, companyName, selectedYear, f);

                // TODO: XML/ZIP import’u senin importerına bağlayacağız
                // var xmlFiles = ...
                // await ImportXmlZipAsync(companyCode, companyName, selectedYear, xmlFiles);

                if (txtFiles.Count > 0)
                    MessageBox.Show("İçe aktarma tamamlandı.", "Muavin", MessageBoxButton.OK, MessageBoxImage.Information);

                tbYear.Focus();
                tbYear.SelectAll();
            }
            catch (Exception ex)
            {
                ErrorText = "İçe aktarma hatası: " + ex.Message;
            }
            finally
            {
                CleanupTempDirs(tempDirs);
            }
        }


        private static string? PromptCompanyNameStrict(string companyCode, string? autoTitle)
        {
            var suggested = string.IsNullOrWhiteSpace(autoTitle) ? "" : autoTitle.Trim();

            var input = Microsoft.VisualBasic.Interaction.InputBox(
                "Şirket adını kontrol edin / gerekirse düzeltin:",
                "Şirket Adı",
                suggested
            );

            if (input == null) return null; // pratikte gelmeyebilir ama kalsın

            var name = input.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return null; // ✅ zorunlu

            // ekstra güvenlik: sadece tek satır, aşırı uzun değil
            name = name.Replace("\r", " ").Replace("\n", " ").Trim();
            if (name.Length > 200) name = name.Substring(0, 200).Trim();

            return name;
        }

        // VKN yoksa stabil company_code üret (C- + 12 hex)
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

        private async Task<DbMuavinRepository.CompanyItem?> CheckDuplicateAndPickExistingAsync(string companyName)
        {
            var candidates = await _repo.FindCompaniesByNameAsync(companyName, limit: 10);

            if (candidates == null || candidates.Count == 0)
                return null;

            // Liste: "NAME  [CODE]" formatı
            var items = candidates
                .Select(c => $"{c.CompanyName}  [{c.CompanyCode}]")
                .ToList();

            // En üste “Yeni oluştur” seçeneği koy
            items.Insert(0, "➕ Yeni şirket olarak ekle");

            var picked = SelectFromList("Benzer şirket bulundu", "Mevcut kaydı mı kullanmak istersiniz?", items);
            if (picked == null) return null;

            if (picked.StartsWith("➕"))
                return null; // yeni oluştur

            // seçilen satırdan code parse et: "... [CODE]"
            var ix1 = picked.LastIndexOf('[');
            var ix2 = picked.LastIndexOf(']');
            if (ix1 < 0 || ix2 <= ix1) return null;

            var code = picked.Substring(ix1 + 1, ix2 - ix1 - 1).Trim();
            return candidates.FirstOrDefault(c => string.Equals(c.CompanyCode, code, StringComparison.Ordinal));
        }


    }
}
