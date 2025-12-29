// MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using Muavin.Xml.Data;
using Muavin.Xml.Parsing;
using Muavin.Xml.Util;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace Muavin.Desktop.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly EdefterParser _parser = new();
        private readonly TxtMuavinParser _txtParser = new();

        private readonly DbMuavinRepository _dbRepo;
        private readonly ContextState _context;

        [ObservableProperty] private string? _statusText;
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private int _progressValue;
        [ObservableProperty] private int _progressMax = 100;
        [ObservableProperty] private string? _totalsText;


        [ObservableProperty]
        private MuavinRow? _selectedRow;

        // ========= MANUEL FİŞ TÜRÜ (UI seçimi) =========
        // UI'dan bind edeceksin (ComboBox SelectedItem vs)
        [ObservableProperty]
        private string? _selectedFisTuru;

        // Kullanıcı tarafından manuel değiştirilen groupKey seti (Kaydet için)
        private readonly HashSet<string> _dirtyFisTypeGroupKeys = new(StringComparer.Ordinal);

        public FilterViewModel Filters { get; } = new();
        public TotalsViewModel Totals { get; } = new();

        public ObservableCollection<MuavinRow> Rows { get; } = new();

        private readonly List<MuavinRow> _allRows = new();
        public IReadOnlyList<MuavinRow> AllRows => _allRows;

        private readonly List<string> _selectedInputs = new();
        private bool _userChangedSort = false;

        public string ContextDisplay => _context.Display;

        public MainViewModel(ContextState context, DbMuavinRepository repo)
        {
            _context = context;
            _dbRepo = repo;

            Rows.CollectionChanged += Rows_CollectionChanged;
            Totals.PropertyChanged += (_, __) => UpdateTotalsText();

            _context.PropertyChanged += (_, __) => OnPropertyChanged(nameof(ContextDisplay));
        }

        private void Rows_CollectionChanged(object? s, NotifyCollectionChangedEventArgs e) => RecomputeTotals();

        // ================== BAĞLAM DEĞİŞTİR =====================
        [RelayCommand]
        private async Task ChangeContextAsync()
        {
            var win = new ContextWindow(_context, _dbRepo)
            {
                Owner = Application.Current?.MainWindow
            };

            if (win.ShowDialog() != true || !_context.HasContext)
                return;

            OnPropertyChanged(nameof(ContextDisplay));

            await LoadFromDatabaseAsync();
        }

        // ================== DB'den yükle (SEÇİLİ BAĞLAM) =====================
        [RelayCommand]
        private async Task LoadFromDatabaseAsync()
        {
            if (!_context.HasContext)
            {
                StatusText = "Bağlam seçilmedi.";
                return;
            }

            try
            {
                IsBusy = true;
                StatusText = $"{_context.Display} verileri yükleniyor…";

                // 1) satırları çek
                var rows = await _dbRepo.GetRowsAsync(_context.CompanyCode!, _context.Year);

                // 2) override'ları çek ve uygula (fis türü)
                //    override tablosu: company+year+group_key -> fis_turu
                var overrides = await _dbRepo.GetFisTypeOverridesAsync(_context.CompanyCode!, _context.Year);
                if (overrides.Count > 0)
                {
                    foreach (var r in rows)
                    {
                        if (!string.IsNullOrWhiteSpace(r.GroupKey) && overrides.TryGetValue(r.GroupKey!, out var ft))
                            r.FisTuru = ft;
                    }
                }

                var ordered = rows
                    .OrderBy(r => r.PostingDate ?? DateTime.MinValue)
                    .ThenBy(r => r.Kebir)
                    .ThenBy(r => r.HesapKodu)
                    .ThenBy(r => r.EntryNumber)
                    .ThenBy(r => r.EntryCounter ?? 0)
                    .ToList();

                _allRows.Clear();
                _allRows.AddRange(ordered);

                _dirtyFisTypeGroupKeys.Clear(); // reload ile dirty reset
                _userChangedSort = false;

                ApplyCurrentFilterToView(_allRows);
                await ComputeContraForVisibleAsync();

                StatusText = $"DB'den {Rows.Count} satır yüklendi. ({_context.Display})";
            }
            catch (Exception ex)
            {
                StatusText = "Hata: " + ex.Message;

                if (ex is Npgsql.PostgresException pg)
                {
                    Logger.WriteLine("[PG ERROR] " + pg.MessageText);
                    Logger.WriteLine("[PG] SqlState=" + pg.SqlState);
                    Logger.WriteLine("[PG] Detail=" + pg.Detail);
                    Logger.WriteLine("[PG] Where=" + pg.Where);
                    Logger.WriteLine("[PG] Position=" + pg.Position);
                    Logger.WriteLine("[PG] Routine=" + pg.Routine);
                    Logger.WriteLine("[PG] Schema=" + pg.SchemaName + " Table=" + pg.TableName + " Column=" + pg.ColumnName);
                    StatusText = $"DB Hata ({pg.SqlState}): {pg.MessageText}";
                }
                else
                {
                    Logger.WriteLine("[ERROR] " + ex);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ================== MANUEL FİŞ TÜRÜ: UYGULA (EKRAN) =====================
        // UI tarafında: seçili satır(lar) üzerinden çalışmak istersen
        // şimdilik SelectedRow üzerinden yapıyoruz.
        [RelayCommand]
        private void ApplySelectedFisTuruToSelection()
        {
            if (!_context.HasContext)
            {
                StatusText = "Önce bağlam seçin.";
                return;
            }

            var ft = (SelectedFisTuru ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ft))
            {
                StatusText = "Fiş türü seçin.";
                return;
            }

            var row = SelectedRow;
            if (row == null)
            {
                StatusText = "Önce bir satır seçin.";
                return;
            }

            // groupKey yoksa üret (DB/KeyFor ile uyumlu)
            if (string.IsNullOrWhiteSpace(row.GroupKey))
                row.GroupKey = BuildGroupKey(row);

            // Aynı fişe ait tüm satırlara uygula (groupKey bazlı)
            var gk = row.GroupKey!;
            foreach (var r in _allRows.Where(x => string.Equals(x.GroupKey, gk, StringComparison.Ordinal)))
                r.FisTuru = ft;

            // dirty set'e ekle
            _dirtyFisTypeGroupKeys.Add(gk);

            // ekrandaki filtre görünümü etkilenmiş olabilir (Exclude Açılış/Kapanış)
            var filtered = ApplyFilterLogic(_allRows);
            ApplyCurrentFilterToView(filtered);

            StatusText = $"Manuel fiş türü uygulandı: {ft} (Kaydetmek için 'Kaydet' butonuna bas)";
        }

        // ================== MANUEL FİŞ TÜRÜ: KAYDET (DB) =====================
        [RelayCommand]
        private async Task SaveFisTuruOverridesToDbAsync()
        {
            if (!_context.HasContext)
            {
                StatusText = "Önce bağlam seçin.";
                return;
            }

            if (_dirtyFisTypeGroupKeys.Count == 0)
            {
                StatusText = "Kaydedilecek manuel fiş türü değişikliği yok.";
                return;
            }

            try
            {
                IsBusy = true;
                StatusText = "Manuel fiş türü değişiklikleri DB’ye kaydediliyor…";

                // groupKey -> fisTuru (tekil)
                var items = _dirtyFisTypeGroupKeys
                    .Select(gk =>
                    {
                        var anyRow = _allRows.FirstOrDefault(r => string.Equals(r.GroupKey, gk, StringComparison.Ordinal));
                        var ft = (anyRow?.FisTuru ?? "").Trim();
                        return (groupKey: gk, fisTuru: ft);
                    })
                    .Where(x => x.groupKey.Length > 0 && x.fisTuru.Length > 0)
                    .ToList();

                await _dbRepo.UpsertFisTypeOverridesAsync(
                    companyCode: _context.CompanyCode!,
                    year: _context.Year,
                    items: items,
                    updatedBy: Environment.UserName);

                _dirtyFisTypeGroupKeys.Clear();

                StatusText = "Kaydedildi. DB’den yeniden yükleniyor…";
                await LoadFromDatabaseAsync();
            }
            catch (Exception ex)
            {
                StatusText = "Kaydetme hatası: " + ex.Message;
                Logger.WriteLine("[FIS SAVE ERROR] " + ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ================== Dosya/Klasör Seç (PREVIEW) =====================
        [RelayCommand]
        private async Task Pick()
        {
            var dlg = new CommonOpenFileDialog
            {
                Title = "XML/TXT dosyası veya ZIP seç",
                EnsurePathExists = true,
                Multiselect = true,
                IsFolderPicker = false
            };

            dlg.Filters.Add(new CommonFileDialogFilter("XML", "*.xml"));
            dlg.Filters.Add(new CommonFileDialogFilter("TXT", "*.txt"));
            dlg.Filters.Add(new CommonFileDialogFilter("ZIP", "*.zip"));
            dlg.Filters.Add(new CommonFileDialogFilter("Tümü", "*.*"));

            if (dlg.ShowDialog() != CommonFileDialogResult.Ok) return;

            _selectedInputs.Clear();
            _selectedInputs.AddRange(dlg.FileNames);

            StatusText = $"{_selectedInputs.Count} seçim yapıldı. (Önizleme)";
            await ParsePreviewAsync();
        }

        private async Task ParsePreviewAsync()
        {
            if (_selectedInputs.Count == 0)
            {
                StatusText = "Önce dosya/klasör seçin.";
                return;
            }

            List<string>? tempDirs = null;

            try
            {
                IsBusy = true;
                ProgressValue = 0;
                StatusText = "Hazırlanıyor…";

                var logPath = Path.Combine(AppContext.BaseDirectory, "debug.txt");
                Logger.Init(logPath, overwrite: true);

                FieldMap.Load(); // XML için

                var (files, temps) = ExpandToDataFilesWithTemps(_selectedInputs);
                tempDirs = temps;

                if (files.Count == 0)
                {
                    StatusText = "Seçimlerde XML/TXT bulunamadı.";
                    return;
                }

                _allRows.Clear();
                Rows.Clear();
                Totals.Reset();

                ProgressMax = files.Count;
                int parsedCount = 0;

                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    List<MuavinRow> list;

                    if (ext == ".txt")
                    {
                        int py, pm;
                        list = _txtParser.Parse(file, _context.CompanyCode ?? "", out py, out pm);

                        var meta = _txtParser.LastMeta;
                        if (meta.DistinctYearMonthCount > 1)
                            Logger.WriteLine($"[TXT META] {Path.GetFileName(file)} => {meta.DistinctYearMonthCount} ay (min={meta.MinDate:yyyy-MM-dd}, max={meta.MaxDate:yyyy-MM-dd})");
                    }
                    else
                    {
                        var parsed = _parser.Parse(file);
                        list = parsed?.ToList() ?? new List<MuavinRow>();
                    }

                    // groupKey üret (manuel işlemler için)
                    foreach (var r in list)
                        if (string.IsNullOrWhiteSpace(r.GroupKey))
                            r.GroupKey = BuildGroupKey(r);

                    _allRows.AddRange(list);
                    parsedCount += list.Count;

                    ProgressValue++;
                    StatusText = $"Önizleme yükleniyor… ({ProgressValue}/{ProgressMax})";
                    await Task.Yield();
                }

                var ordered = _allRows
                    .OrderBy(r => r.PostingDate ?? DateTime.MinValue)
                    .ThenBy(r => r.Kebir)
                    .ThenBy(r => r.HesapKodu)
                    .ThenBy(r => r.EntryNumber)
                    .ThenBy(r => r.EntryCounter ?? 0)
                    .ToList();

                _allRows.Clear();
                _allRows.AddRange(ordered);

                _dirtyFisTypeGroupKeys.Clear();
                _userChangedSort = false;

                ApplyCurrentFilterToView(_allRows);
                await ComputeContraForVisibleAsync();

                StatusText = $"Önizleme tamam — {parsedCount} satır. Görünen {Rows.Count} satır.";
            }
            catch (Exception ex)
            {
                StatusText = "Hata: " + ex.Message;
                Logger.WriteLine("[PREVIEW ERROR] " + ex);
            }
            finally
            {
                Logger.Close();
                IsBusy = false;

                CleanupTempDirs(tempDirs);
            }
        }

        // ================== Veritabanına Veri Ekle (SEÇİLİ BAĞLAM) =====================
        [RelayCommand]
        private async Task ImportToDatabaseAsync()
        {
            if (!_context.HasContext)
            {
                StatusText = "Önce bağlam seçin.";
                return;
            }

            var dlg = new CommonOpenFileDialog
            {
                Title = "DB’ye eklenecek XML/TXT/ZIP seç",
                EnsurePathExists = true,
                Multiselect = true,
                IsFolderPicker = false
            };

            dlg.Filters.Add(new CommonFileDialogFilter("XML", "*.xml"));
            dlg.Filters.Add(new CommonFileDialogFilter("TXT", "*.txt"));
            dlg.Filters.Add(new CommonFileDialogFilter("ZIP", "*.zip"));
            dlg.Filters.Add(new CommonFileDialogFilter("Tümü", "*.*"));

            if (dlg.ShowDialog() != CommonFileDialogResult.Ok) return;

            _selectedInputs.Clear();
            _selectedInputs.AddRange(dlg.FileNames);

            await ParseAndSaveToDbAsync();
        }

        private async Task ParseAndSaveToDbAsync()
        {
            List<string>? tempDirs = null;

            try
            {
                IsBusy = true;
                ProgressValue = 0;
                StatusText = "Hazırlanıyor…";

                var logPath = Path.Combine(AppContext.BaseDirectory, "debug.txt");
                Logger.Init(logPath, overwrite: true);

                FieldMap.Load();

                var (files, temps) = ExpandToDataFilesWithTemps(_selectedInputs);
                tempDirs = temps;

                if (files.Count == 0)
                {
                    StatusText = "Seçimlerde XML/TXT bulunamadı.";
                    return;
                }

                ProgressMax = files.Count;

                int parsedCount = 0;

                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();

                    List<MuavinRow> list;
                    if (ext == ".txt")
                    {
                        int py, pm;
                        list = _txtParser.Parse(file, _context.CompanyCode!, out py, out pm);

                        var meta = _txtParser.LastMeta;
                        if (meta.DistinctYearMonthCount > 1)
                        {
                            Logger.WriteLine($"[TXT META] {Path.GetFileName(file)} => {meta.DistinctYearMonthCount} ay (min={meta.MinDate:yyyy-MM-dd}, max={meta.MaxDate:yyyy-MM-dd})");
                        }
                    }
                    else
                    {
                        var parsed = _parser.Parse(file);
                        list = parsed?.ToList() ?? new List<MuavinRow>();
                    }

                    // groupKey üret
                    foreach (var r in list)
                        if (string.IsNullOrWhiteSpace(r.GroupKey))
                            r.GroupKey = BuildGroupKey(r);

                    parsedCount += list.Count;

                    StatusText = $"DB’ye yazılıyor… {Path.GetFileName(file)}";

                    await _dbRepo.BulkInsertRowsAsync(
                        companyCode: _context.CompanyCode!,
                        rows: list,
                        sourceFile: file,
                        replaceExistingForSameSource: true,
                        companyName: _context.CompanyName);

                    ProgressValue++;
                    await Task.Yield();
                }

                StatusText = $"Tamam — {parsedCount} satır DB’ye eklendi. Şimdi DB’den yükleniyor…";
                await LoadFromDatabaseAsync();
            }
            catch (Exception ex)
            {
                StatusText = "Hata: " + ex.Message;

                if (ex is Npgsql.PostgresException pg)
                {
                    Logger.WriteLine("[PG ERROR] " + pg.MessageText);
                    Logger.WriteLine("[PG] SqlState=" + pg.SqlState);
                    Logger.WriteLine("[PG] Detail=" + pg.Detail);
                    Logger.WriteLine("[PG] Where=" + pg.Where);
                    Logger.WriteLine("[PG] Position=" + pg.Position);
                    Logger.WriteLine("[PG] Routine=" + pg.Routine);
                    Logger.WriteLine("[PG] Schema=" + pg.SchemaName + " Table=" + pg.TableName + " Column=" + pg.ColumnName);
                    StatusText = $"DB Hata ({pg.SqlState}): {pg.MessageText}";
                }
                else
                {
                    Logger.WriteLine("[ERROR] " + ex);
                }
            }
            finally
            {
                Logger.Close();
                IsBusy = false;

                CleanupTempDirs(tempDirs);
            }
        }

        // ================== Filtre / Temizle =====================
        [RelayCommand]
        private async Task ApplyFilters()
        {
            var filtered = ApplyFilterLogic(_allRows);
            ApplyCurrentFilterToView(filtered);
            await ComputeContraForVisibleAsync();
            StatusText = $"Filtre uygulandı. Görünen {Rows.Count} satır.";
        }

        [RelayCommand]
        private async Task ResetFilters()
        {
            Filters.Reset();
            ApplyCurrentFilterToView(_allRows);
            await ComputeContraForVisibleAsync();
            StatusText = "Filtreler sıfırlandı.";
        }

        [RelayCommand]
        private void ClearAll()
        {
            _selectedInputs.Clear();
            _allRows.Clear();
            Rows.Clear();
            Filters.Reset();
            Totals.Reset();
            UpdateTotalsText();
            StatusText = "Temizlendi.";
        }

        // ================ Detay Penceresi ================
        [RelayCommand]
        private void OpenEntryDetails()
        {
            var row = SelectedRow ?? Rows.LastOrDefault();
            if (row is null) return;

            IEnumerable<MuavinRow> source = _allRows.Count > 0 ? _allRows : (IEnumerable<MuavinRow>)Rows;
            var key = KeyFor(row);
            var list = source.Where(x => KeyFor(x) == key)
                             .OrderBy(x => x.HesapKodu).ThenBy(x => x.Kebir)
                             .ToList();
            if (list.Count == 0) list = new List<MuavinRow> { row };

            var win = new EntryDetailsWindow(list) { Owner = App.Current.MainWindow };
            win.ShowDialog();
        }

        // ================ Hesap Özeti ================
        [RelayCommand]
        private void OpenAccountSummary()
        {
            var source = Rows.Count > 0 ? Rows.ToList() : _allRows;
            if (source.Count == 0) { StatusText = "Özet için veri yok."; return; }

            var win = new AccountSummaryWindow(source) { Owner = App.Current.MainWindow };
            win.ShowDialog();
        }

        // =============== Yaşlandırma ============================
        [RelayCommand]
        private void OpenAging()
        {
            var source = Rows.Count > 0 ? Rows.ToList() : _allRows.ToList();
            if (source.Count == 0)
            {
                StatusText = "Yaşlandırma için veri yok.";
                return;
            }

            var win = new AgingWindow(source)
            {
                Owner = App.Current.MainWindow
            };
            win.ShowDialog();
        }

        // ================ Sıralama ==============================
        public void ToggleSort(string sortMember)
        {
            var cvs = CollectionViewSource.GetDefaultView(Rows);
            if (cvs is null) return;

            _userChangedSort = true;

            var current = cvs.SortDescriptions.FirstOrDefault(sd => sd.PropertyName == sortMember);
            var dir = ListSortDirection.Ascending;
            if (current.PropertyName == sortMember && current.Direction == dir)
                dir = ListSortDirection.Descending;

            cvs.SortDescriptions.Clear();
            cvs.SortDescriptions.Add(new SortDescription(sortMember, dir));
            cvs.Refresh();
        }

        // ================ Karşı Hesap ==========================
        private async Task ComputeContraForVisibleAsync()
        {
            if (Rows.Count == 0) return;

            var snapshot = Rows.ToList();
            var result = await Task.Run(() =>
            {
                var map = new Dictionary<string, (HashSet<string> deb, HashSet<string> crd)>(StringComparer.Ordinal);

                foreach (var g in snapshot.GroupBy(r => KeyFor(r)))
                {
                    var deb = new HashSet<string>(StringComparer.Ordinal);
                    var crd = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var r in g)
                    {
                        var kebir = (r.Kebir ?? "").Trim();
                        if (string.IsNullOrEmpty(kebir)) continue;

                        if (r.Borc > 0m) deb.Add(kebir);
                        else if (r.Alacak > 0m) crd.Add(kebir);
                    }

                    map[g.Key] = (deb, crd);
                }

                var karsiByRow = new Dictionary<MuavinRow, string>(ReferenceEqualityComparer<MuavinRow>.Instance);

                foreach (var r in snapshot)
                {
                    var key = KeyFor(r);
                    if (!map.TryGetValue(key, out var sets))
                    {
                        karsiByRow[r] = "";
                        continue;
                    }

                    var my = (r.Kebir ?? "").Trim();
                    IEnumerable<string> other =
                        r.Borc > 0m ? sets.crd :
                        r.Alacak > 0m ? sets.deb :
                        Enumerable.Empty<string>();

                    var list = other
                        .Where(k => !string.Equals(k, my, StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(k => k, StringComparer.Ordinal)
                        .ToList();

                    karsiByRow[r] = string.Join(" | ", list);
                }

                return karsiByRow;
            });

            foreach (var r in snapshot)
                if (result.TryGetValue(r, out var val))
                    r.KarsiHesap = val;
        }

        private static string KeyFor(MuavinRow r)
        {
            var d = r.PostingDate?.ToString("yyyy-MM-dd") ?? "";
            var no = r.EntryNumber ?? "";
            if (r.FisTuru is "Açılış" or "Kapanış") return $"{no}|{d}";
            var doc = r.DocumentNumber ?? "";
            return string.IsNullOrWhiteSpace(doc) ? $"{no}|{d}" : $"{no}|{d}|DOC:{doc}";
        }

        private void ApplyDefaultSort()
        {
            if (_userChangedSort) return;

            var view = CollectionViewSource.GetDefaultView(Rows);
            if (view == null) return;

            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(MuavinRow.PostingDate), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(MuavinRow.Kebir), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(MuavinRow.HesapKodu), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(MuavinRow.EntryNumber), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(MuavinRow.EntryCounter), ListSortDirection.Ascending));
            view.Refresh();
        }

        private static (List<string> Files, List<string> TempDirs) ExpandToDataFilesWithTemps(IEnumerable<string> inputs)
        {
            var result = new List<string>();
            var temps = new List<string>();

            foreach (var path in inputs)
            {
                if (Directory.Exists(path))
                {
                    result.AddRange(Directory.EnumerateFiles(path, "*.xml", SearchOption.AllDirectories));
                    result.AddRange(Directory.EnumerateFiles(path, "*.txt", SearchOption.AllDirectories));
                    continue;
                }

                if (!File.Exists(path))
                    continue;

                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".xml" or ".txt")
                {
                    result.Add(path);
                }
                else if (ext == ".zip")
                {
                    var temp = Path.Combine(Path.GetTempPath(), "Muavin_Unzip_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    System.IO.Compression.ZipFile.ExtractToDirectory(path, temp, overwriteFiles: true);

                    temps.Add(temp);

                    result.AddRange(Directory.EnumerateFiles(temp, "*.xml", SearchOption.AllDirectories));
                    result.AddRange(Directory.EnumerateFiles(temp, "*.txt", SearchOption.AllDirectories));
                }
            }

            return (result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), temps);
        }

        private static void CleanupTempDirs(List<string>? tempDirs)
        {
            if (tempDirs == null || tempDirs.Count == 0) return;

            foreach (var d in tempDirs)
            {
                try
                {
                    if (Directory.Exists(d))
                        Directory.Delete(d, recursive: true);
                }
                catch
                {
                    // production: temp cleanup failure should not crash app
                }
            }
        }

        private List<MuavinRow> ApplyFilterLogic(IEnumerable<MuavinRow> source)
        {
            var q = source;

            int kb, ke;
            int? start = int.TryParse(Filters.KebirBas, out kb) ? kb : null;
            int? end = int.TryParse(Filters.KebirBit, out ke) ? ke : null;

            if (start.HasValue || end.HasValue)
            {
                q = q.Where(r =>
                {
                    if (!int.TryParse(r.Kebir ?? "0", out var k)) return false;
                    if (start.HasValue && k < start.Value) return false;
                    if (end.HasValue && k > end.Value) return false;
                    return true;
                });
            }

            if (Filters.TarihBas.HasValue)
                q = q.Where(r => r.PostingDate.HasValue && r.PostingDate.Value.Date >= Filters.TarihBas.Value.Date);
            if (Filters.TarihBit.HasValue)
                q = q.Where(r => r.PostingDate.HasValue && r.PostingDate.Value.Date <= Filters.TarihBit.Value.Date);

            if (!string.IsNullOrWhiteSpace(Filters.HesapKodu))
            {
                var wanted = Filters.HesapKodu.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
                q = q.Where(r => r.HesapKodu != null && wanted.Contains(r.HesapKodu));
            }

            if (!string.IsNullOrWhiteSpace(Filters.Aciklama))
                q = q.Where(r => (r.Aciklama ?? "").Contains(Filters.Aciklama!, StringComparison.OrdinalIgnoreCase));

            // ✅ NEW: Açılış / Kapanış hariç tut
            if (Filters.ExcludeAcilis)
                q = q.Where(r => !string.Equals((r.FisTuru ?? "").Trim(), "Açılış", StringComparison.OrdinalIgnoreCase));

            if (Filters.ExcludeKapanis)
                q = q.Where(r => !string.Equals((r.FisTuru ?? "").Trim(), "Kapanış", StringComparison.OrdinalIgnoreCase));

            return q
                .OrderBy(r => r.PostingDate ?? DateTime.MinValue)
                .ThenBy(r => r.Kebir)
                .ThenBy(r => r.HesapKodu)
                .ThenBy(r => r.EntryNumber)
                .ThenBy(r => r.EntryCounter ?? 0)
                .ToList();
        }

        private void ApplyCurrentFilterToView(IList<MuavinRow> data)
        {
            Rows.CollectionChanged -= Rows_CollectionChanged;

            Rows.Clear();
            foreach (var r in data)
                Rows.Add(r);

            Rows.CollectionChanged += Rows_CollectionChanged;

            RecomputeTotals();
            ApplyDefaultSort();
        }

        private void RecomputeTotals()
        {
            Totals.Reset();
            foreach (var r in Rows)
            {
                Totals.Borc += r.Borc;
                Totals.Alacak += r.Alacak;
            }
            Totals.Bakiye = Totals.Borc - Totals.Alacak;
            UpdateTotalsText();
        }

        private void UpdateTotalsText()
        {
            TotalsText = $"Toplam Borç: {Totals.Borc:N2} | Toplam Alacak: {Totals.Alacak:N2}";
        }

        // MainViewModel’deki KeyFor ile uyumlu olacak şekilde groupKey üret
        private static string BuildGroupKey(MuavinRow r)
        {
            var d = r.PostingDate?.ToString("yyyy-MM-dd") ?? "";
            var no = r.EntryNumber ?? "";
            var doc = r.DocumentNumber ?? "";

            if (r.FisTuru is "Açılış" or "Kapanış")
                return $"{no}|{d}";

            if (!string.IsNullOrWhiteSpace(doc))
                return $"{no}|{d}|DOC:{doc}";

            return $"{no}|{d}";
        }
    }

    internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
