// MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.WindowsAPICodePack.Dialogs;
using Muavin.Xml.Data;
using Muavin.Xml.Parsing;
using Muavin.Xml.Util;
using Npgsql;
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

        // ========= (Toolbar için) MANUEL FİŞ TÜRÜ (UI seçimi) =========
        private string? _selectedFisTuru;
        public string? SelectedFisTuru
        {
            get => _selectedFisTuru;
            set => SetProperty(ref _selectedFisTuru, value);
        }

        public IReadOnlyList<string> FisTuruOptions { get; } =
            new[] { "Normal", "Açılış", "Kapanış" };

        // ✅ Eski XAML/ComboBox binding’leri için alias (yaygın binding adı)
        public IReadOnlyList<string> AvailableFisTypes => FisTuruOptions;

        // Kullanıcı tarafından manuel değiştirilen groupKey seti (Kaydet için)
        private readonly HashSet<string> _dirtyFisTypeGroupKeys = new(StringComparer.Ordinal);

        // ========= UNDO (tek adım) =========
        private sealed class UndoFisTypeChange
        {
            public string EntryNo { get; init; } = "";
            public string GroupKey { get; init; } = "";
            public string? NewFisTuru { get; init; }
            public List<(MuavinRow row, string? oldFisTuru, string? oldGroupKey)> Snapshots { get; init; } = new();
            public HashSet<string> DirtyBefore { get; init; } = new(StringComparer.Ordinal);
        }

        private UndoFisTypeChange? _lastUndo;

        // ===================== ✅ UNDO LAST IMPORT UX STATE (MANUAL - generator bağımsız) =====================
        private bool _hasUndoableImport;
        public bool HasUndoableImport
        {
            get => _hasUndoableImport;
            set
            {
                if (SetProperty(ref _hasUndoableImport, value))
                {
                    OnPropertyChanged(nameof(UndoLastImportToolTip));
                    UndoLastImportCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string UndoLastImportToolTip =>
            IsBusy ? "İşlem sürüyor..." :
            !_context.HasContext ? "Önce bağlam seçin." :
            HasUndoableImport ? "En son veritabanı yüklemesini geri alır." :
            "Geri alınacak bir import bulunamadı.";

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

            // Context değişince UI + komut aktifliği + tooltip güncellensin
            _context.PropertyChanged += async (_, __) =>
            {
                OnPropertyChanged(nameof(ContextDisplay));
                OnPropertyChanged(nameof(UndoLastImportToolTip));

                await RefreshUndoLastImportStateAsync();
                UndoLastImportCommand.NotifyCanExecuteChanged();
            };

            // başlangıçta state’i bir kere hesaplayalım
            _ = RefreshUndoLastImportStateAsync();
        }

        private void Rows_CollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
        {
            RecomputeTotals();

            // ✅ Rows.Count değişince badge text otomatik güncellenmez.
            // Focus açıkken UI göstergelerini zorla yenile.
            if (IsFisFocusActive)
            {
                _focusedFisRowCount = Rows.Count;
                RaiseFisFocusUiChanged();
            }
        }

        // IsBusy değişince Undo butonunun CanExecute'i + tooltip güncellensin
        partial void OnIsBusyChanged(bool value)
        {
            UndoLastImportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(UndoLastImportToolTip));
        }

        // ================== ✅ SATIR BAZINDA BAKİYE (RUNNING BALANCE) =====================
        private void ComputeRunningBalances(IList<MuavinRow> rows)
        {
            if (rows == null || rows.Count == 0) return;

            foreach (var g in rows
                .Where(r => !string.IsNullOrWhiteSpace(r.HesapKodu))
                .GroupBy(r => r.HesapKodu!, StringComparer.OrdinalIgnoreCase))
            {
                decimal running = 0m;

                foreach (var r in g
                    .OrderBy(r => r.PostingDate ?? DateTime.MinValue)
                    .ThenBy(r => r.EntryNumber ?? "")
                    .ThenBy(r => r.EntryCounter ?? 0))
                {
                    running += (r.Borc - r.Alacak);
                    r.RunningBalance = running;
                }
            }
        }

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

            await RefreshUndoLastImportStateAsync();
            UndoLastImportCommand.NotifyCanExecuteChanged();

            await LoadFromDatabaseAsync();
        }

        // ================== ✅ IMPORT TO DB (XAML binding hatasını bitirir) =====================
        // XAML'de Command="{Binding ImportToDatabaseCommand}" kullan.
        [RelayCommand]
        private async Task ImportToDatabaseAsync()
        {
            // 0) Context zorunlu
            if (!_context.HasContext)
            {
                StatusText = "Önce bağlam seçin (Şirket/Yıl).";
                return;
            }

            try
            {
                IsBusy = true;

                // 1) Eğer henüz preview yoksa → dosya seç + preview yükle
                if (_allRows.Count == 0)
                {
                    StatusText = "Dosya seçiliyor…";
                    var ok = await PickAndPreviewInternalAsync();
                    if (!ok)
                    {
                        StatusText = "İşlem iptal edildi.";
                        return;
                    }
                }

                if (_allRows.Count == 0)
                {
                    StatusText = "Aktarılacak veri yok (preview boş).";
                    return;
                }

                // 2) Preview sonrası onay
                var srcText = BuildSourceDisplayName();
                var msg =
                    $"Seçilen kaynak: {srcText}\n" +
                    $"Önizleme satır sayısı: {_allRows.Count:N0}\n\n" +
                    "Bu veriler veritabanına eklensin mi?";

                var confirm = MessageBox.Show(msg, "Veritabanına Veri Ekle", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    StatusText = "DB ekleme iptal edildi (preview kaldı).";
                    return;
                }

                // 3) DB import
                StatusText = "DB’ye import ediliyor…";

                var sourceFileForBatch = BuildSourceFileForBatch(); // batch için tek string
                await _dbRepo.BulkInsertRowsAsync(
                    companyCode: _context.CompanyCode!,
                    rows: _allRows,
                    sourceFile: sourceFileForBatch,
                    replaceExistingForSameSource: true,
                    companyName: _context.CompanyName,
                    ct: default,
                    replaceMode: DbMuavinRepository.ImportReplaceMode.MonthsInPayload
                );

                StatusText = "Import tamamlandı. DB’den yeniden yükleniyor…";
                await LoadFromDatabaseAsync();

                await RefreshUndoLastImportStateAsync();
                UndoLastImportCommand.NotifyCanExecuteChanged();

                StatusText = "DB import tamamlandı.";
            }
            catch (Exception ex)
            {
                StatusText = "Import hatası: " + ex.Message;
                Logger.WriteLine("[IMPORT ERROR] " + ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ✅ Import butonunun içinde kullandığımız: Dosya seç + Preview yükle
        private async Task<bool> PickAndPreviewInternalAsync()
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

            if (dlg.ShowDialog() != CommonFileDialogResult.Ok)
                return false;

            _selectedInputs.Clear();
            _selectedInputs.AddRange(dlg.FileNames);

            StatusText = $"{_selectedInputs.Count} seçim yapıldı. Önizleme yükleniyor…";

            // Preview
            await ParsePreviewAsync();
            return true;
        }

        // UI’da güzel gözüksün diye
        private string BuildSourceDisplayName()
        {
            if (_selectedInputs.Count == 0) return "-";
            if (_selectedInputs.Count == 1) return System.IO.Path.GetFileName(_selectedInputs[0]);
            return $"{_selectedInputs.Count} dosya";
        }

        // DB batch.source_file alanına tek değer veriyoruz
        private string BuildSourceFileForBatch()
        {
            if (_selectedInputs.Count == 0) return "_manual_import_";
            if (_selectedInputs.Count == 1) return _selectedInputs[0]; // repo zaten GetFileName’e çeviriyor
            return "_multi_select_";
        }

        // ================== DB'den yükle (SEÇİLİ BAĞLAM) =====================
        [RelayCommand]
        private async Task LoadFromDatabaseAsync()
        {
            if (!_context.HasContext)
            {
                StatusText = "Bağlam seçilmedi.";
                HasUndoableImport = false;
                return;
            }

            try
            {
                IsBusy = true;
                StatusText = $"{_context.Display} verileri yükleniyor…";

                // 1) satırları çek
                var rows = await _dbRepo.GetRowsAsync(_context.CompanyCode!, _context.Year);

                // 2) override'ları çek ve uygula (fis türü)
                var overrides = await _dbRepo.GetFisTypeOverridesAsync(_context.CompanyCode!, _context.Year);
                if (overrides.Count > 0)
                {
                    foreach (var r in rows)
                    {
                        if (!string.IsNullOrWhiteSpace(r.GroupKey) &&
                            overrides.TryGetValue(r.GroupKey!, out var ft))
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

                ComputeRunningBalances(_allRows);

                _dirtyFisTypeGroupKeys.Clear();
                _userChangedSort = false;

                // Undo reset
                _lastUndo = null;
                RaiseUndoStateChanged();

                // Fiş odağı reset (DB load "fresh state")
                ClearFisFocusCore(resetStatus: false);

                ApplyCurrentFilterToView(_allRows);
                await ComputeContraForVisibleAsync();

                await RefreshUndoLastImportStateAsync();

                StatusText = $"DB'den {Rows.Count} satır yüklendi. ({_context.Display})";
            }
            catch (Exception ex)
            {
                StatusText = "Hata: " + ex.Message;

                if (ex is PostgresException pg)
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

        [RelayCommand]
        private async Task ReloadFromDatabaseAsync()
        {
            if (!_context.HasContext)
            {
                StatusText = "Bağlam seçilmedi.";
                MessageBox.Show(
                    "Önce bağlam seçmelisiniz (Şirket / Dönem).",
                    "Veriyi Yeniden Yükle",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            await RefreshUndoLastImportStateAsync();
            UndoLastImportCommand.NotifyCanExecuteChanged();

            await LoadFromDatabaseAsync();
        }

        // =====================================================================
        //  ✅ MANUEL FİŞ TÜRÜ OVERRIDE — "Grid/ComboBox" tarzı çağrılar için de destek
        // =====================================================================
        public async Task ApplyFisTypeOverrideAsync(string? groupKey, string? newFisType)
        {
            if (!_context.HasContext)
            {
                StatusText = "Önce bağlam seçin.";
                return;
            }

            var gk = (groupKey ?? "").Trim();
            var ft = (newFisType ?? "").Trim();

            if (string.IsNullOrWhiteSpace(gk))
            {
                StatusText = "GroupKey boş (fiş grubu bulunamadı).";
                return;
            }
            if (string.IsNullOrWhiteSpace(ft))
            {
                StatusText = "Fiş türü boş olamaz.";
                return;
            }

            var targets = _allRows
                .Where(r => string.Equals((r.GroupKey ?? "").Trim(), gk, StringComparison.Ordinal))
                .ToList();

            if (targets.Count == 0)
            {
                StatusText = "Uygulanacak satır bulunamadı.";
                return;
            }

            var entryNo = (targets[0].EntryNumber ?? "").Trim();
            if (string.IsNullOrWhiteSpace(entryNo))
                entryNo = gk;

            var alreadySame = targets.All(r => string.Equals((r.FisTuru ?? "").Trim(), ft, StringComparison.OrdinalIgnoreCase));
            if (alreadySame)
            {
                StatusText = $"Boş işlem: fiş zaten '{ft}'.";
                return;
            }

            if (targets.Count >= 500)
            {
                var big = MessageBox.Show(
                    $"Dikkat: {targets.Count:N0} satır etkilenecek.\n\n" +
                    "Bu genellikle yanlış bir seçim olabilir.\n\n" +
                    "Devam etmek istiyor musunuz?",
                    "Büyük Değişiklik Uyarısı",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (big != MessageBoxResult.Yes)
                {
                    StatusText = "Fiş türü değişikliği iptal edildi.";
                    return;
                }
            }

            var applied = ApplyFisTuruToTargetsCore(entryNo, targets, ft);
            if (!applied) return;


            var impact = BuildFisTypeImpact(entryNo, targets, ft);
            var msg = BuildFisTypeConfirmationMessage(impact);

            var res = MessageBox.Show(
                msg,
                "Fiş Türü Değişikliği",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Cancel)
            {
                UndoLastFisTuruChangeCore();
                return;
            }

            if (res == MessageBoxResult.Yes)
            {
                await SaveFisTuruOverridesToDbAsync();
                return;
            }

            StatusText = $"Değişiklik uygulandı; şimdilik kaydedilmedi. (Kaydedilmemiş fiş/grup: {_dirtyFisTypeGroupKeys.Count})";
        }

        // =====================================================================
        //  CONTEXT MENU: Fiş Türü — Komutlar (eski davranış)
        // =====================================================================

        [RelayCommand]
        private async Task SetFisTuruForSelectedFisWithPromptAsync(string? fisTuru)
        {
            if (!_context.HasContext)
            {
                StatusText = "Önce bağlam seçin.";
                return;
            }
            if (SelectedRow is null)
            {
                StatusText = "Önce bir satır seçin.";
                return;
            }

            var ft = (fisTuru ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ft))
            {
                StatusText = "Fiş türü boş olamaz.";
                return;
            }

            var entryNo = (SelectedRow.EntryNumber ?? "").Trim();
            if (string.IsNullOrWhiteSpace(entryNo))
            {
                StatusText = "Seçili satırda Fiş Numarası yok.";
                return;
            }

            var targets = _allRows
                .Where(r => string.Equals((r.EntryNumber ?? "").Trim(), entryNo, StringComparison.Ordinal))
                .ToList();

            if (targets.Count == 0)
            {
                StatusText = "Uygulanacak satır bulunamadı.";
                return;
            }

            foreach (var r in targets)
                if (string.IsNullOrWhiteSpace(r.GroupKey))
                    r.GroupKey = BuildGroupKey(r);

            var alreadySame = targets.All(r => string.Equals((r.FisTuru ?? "").Trim(), ft, StringComparison.OrdinalIgnoreCase));
            if (alreadySame)
            {
                StatusText = $"Boş işlem: {entryNo} fişi zaten '{ft}'.";
                return;
            }

            if (targets.Count >= 500)
            {
                var big = MessageBox.Show(
                    $"Dikkat: {targets.Count:N0} satır etkilenecek.\n\n" +
                    "Bu genellikle yanlış bir seçim olabilir.\n\n" +
                    "Devam etmek istiyor musunuz?",
                    "Büyük Değişiklik Uyarısı",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (big != MessageBoxResult.Yes)
                {
                    StatusText = "Fiş türü değişikliği iptal edildi.";
                    return;
                }
            }

            var applied = ApplyFisTuruToTargetsCore(entryNo, targets, ft);
            if (!applied) return;

            var impact = BuildFisTypeImpact(entryNo, targets, ft);
            var msg = BuildFisTypeConfirmationMessage(impact);

            var res = MessageBox.Show(
                msg,
                "Fiş Türü Değişikliği",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Cancel)
            {
                UndoLastFisTuruChangeCore();
                return;
            }

            if (res == MessageBoxResult.Yes)
            {
                await SaveFisTuruOverridesToDbAsync();
                return;
            }

            StatusText = $"Değişiklik uygulandı; şimdilik kaydedilmedi. (Kaydedilmemiş fiş/grup: {_dirtyFisTypeGroupKeys.Count})";
        }

        // ✅ Code-behind / grid gibi yerlerden çağırmak için public köprü
        public Task SetFisTuruForSelectedFisWithPromptFromUiAsync(string? fisTuru)
            => SetFisTuruForSelectedFisWithPromptAsync(fisTuru);


        [RelayCommand]
        private void UndoLastFisTuruChange() => UndoLastFisTuruChangeCore();

        private void UndoLastFisTuruChangeCore()
        {
            if (_lastUndo == null || _lastUndo.Snapshots.Count == 0)
            {
                StatusText = "Geri alınacak bir değişiklik yok.";
                return;
            }

            foreach (var (row, oldFisTuru, oldGroupKey) in _lastUndo.Snapshots)
            {
                row.FisTuru = oldFisTuru;
                row.GroupKey = oldGroupKey;
            }

            _dirtyFisTypeGroupKeys.Clear();
            foreach (var k in _lastUndo.DirtyBefore)
                _dirtyFisTypeGroupKeys.Add(k);

            var filtered = ApplyFilterLogic(_allRows);
            ApplyCurrentFilterToView(filtered);

            StatusText = $"Geri alındı: {_lastUndo.EntryNo} fişindeki son değişiklik geri çevrildi.";
            _lastUndo = null;
            RaiseUndoStateChanged();
        }

        [RelayCommand]
        private async Task ApplySelectedFisTuruToSelectionAsync()
        {
            await SetFisTuruForSelectedFisWithPromptAsync(SelectedFisTuru);
        }

        private bool ApplyFisTuruToTargetsCore(string entryNo, List<MuavinRow> targets, string newFisTuru)
        {
            if (targets == null || targets.Count == 0) return false;

            var undo = new UndoFisTypeChange
            {
                EntryNo = entryNo,
                GroupKey = targets.FirstOrDefault()?.GroupKey ?? "",
                NewFisTuru = newFisTuru,
                DirtyBefore = new HashSet<string>(_dirtyFisTypeGroupKeys, StringComparer.Ordinal)
            };

            foreach (var r in targets)
                undo.Snapshots.Add((r, r.FisTuru, r.GroupKey));

            foreach (var r in targets)
            {
                r.FisTuru = newFisTuru;

                if (string.IsNullOrWhiteSpace(r.GroupKey))
                    r.GroupKey = BuildGroupKey(r);

                _dirtyFisTypeGroupKeys.Add(r.GroupKey!);
            }

            _lastUndo = undo;
            RaiseUndoStateChanged();

            var filtered = ApplyFilterLogic(_allRows);
            ApplyCurrentFilterToView(filtered);

            StatusText = $"Uygulandı: {entryNo} fişinde {targets.Count} satır güncellendi. (Kaydedilmemiş: {_dirtyFisTypeGroupKeys.Count})";
            return true;
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

            var msg =
                $"Bu işlem veritabanına {_dirtyFisTypeGroupKeys.Count} adet fiş/grup için fiş türü değişikliği kaydedecek.\n\n" +
                $"Devam edilsin mi?";

            var ok = MessageBox.Show(msg, "DB'ye Kaydet Onayı", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes)
            {
                StatusText = "Kaydetme işlemi iptal edildi.";
                return;
            }

            try
            {
                IsBusy = true;
                StatusText = "Manuel fiş türü değişiklikleri DB’ye kaydediliyor…";

                // 1) upsert listesi + delete listesi hazırlansın
                var upserts = new List<(string groupKey, string fisTuru)>();
                var deletes = new List<string>();

                foreach (var gk in _dirtyFisTypeGroupKeys)
                {
                    var anyRow = _allRows.FirstOrDefault(r => string.Equals(r.GroupKey, gk, StringComparison.Ordinal));
                    var ft = (anyRow?.FisTuru ?? "").Trim();

                    if (string.IsNullOrWhiteSpace(ft))
                        continue;

                    if (string.Equals(ft, "Normal", StringComparison.OrdinalIgnoreCase))
                        deletes.Add(gk);
                    else
                        upserts.Add((gk, ft));
                }

                // 2) Önce upsert (tek transaction içinde senin repo zaten tx açıyor)
                if (upserts.Count > 0)
                {
                    await _dbRepo.UpsertFisTypeOverridesAsync(
                        companyCode: _context.CompanyCode!,
                        year: _context.Year,
                        items: upserts,
                        updatedBy: Environment.UserName);
                }

                // 3) Delete’ler (istersen repo’ya batch delete ekleriz; şimdilik loop ok)
                foreach (var gk in deletes)
                {
                    await _dbRepo.DeleteFisTypeOverrideAsync(_context.CompanyCode!, _context.Year, gk);
                }

                _dirtyFisTypeGroupKeys.Clear();
                _lastUndo = null;
                RaiseUndoStateChanged();

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

        private sealed class FisTypeImpact
        {
            public string EntryNo { get; init; } = "";
            public string GroupKey { get; init; } = "";
            public string NewType { get; init; } = "";
            public int TargetRowCount { get; init; }
            public int DistinctFisCount { get; init; }
            public int DistinctGroupKeyCount { get; init; }
            public Dictionary<string, int> OldTypeCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormFisType(string? t)
        {
            var s = (t ?? "").Trim();
            return string.IsNullOrWhiteSpace(s) ? "Normal" : s;
        }

        private static string FormatTypeCounts(Dictionary<string, int> map)
        {
            if (map.Count == 0) return "-";
            // örn: Normal: 12, Açılış: 3
            return string.Join(", ",
                map.OrderByDescending(kv => kv.Value)
                   .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                   .Select(kv => $"{kv.Key}: {kv.Value}"));
        }

        private FisTypeImpact BuildFisTypeImpact(string entryNo, List<MuavinRow> targets, string newType)
        {
            var gk = (targets.FirstOrDefault()?.GroupKey ?? "").Trim();

            // distinct fis = entry no sayısı (data kirliliğine karşı)
            var fisCount = targets
                .Select(r => (r.EntryNumber ?? "").Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count();

            var groupKeyCount = targets
                .Select(r => (r.GroupKey ?? "").Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count();

            var oldCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in targets)
            {
                var old = NormFisType(r.FisTuru);
                oldCounts.TryGetValue(old, out var c);
                oldCounts[old] = c + 1;
            }

            return new FisTypeImpact
            {
                EntryNo = entryNo,
                GroupKey = gk,
                NewType = newType,
                TargetRowCount = targets.Count,
                DistinctFisCount = fisCount == 0 ? 1 : fisCount,
                DistinctGroupKeyCount = groupKeyCount == 0 ? 1 : groupKeyCount,
                OldTypeCounts = oldCounts
            };
        }

        private string BuildFisTypeConfirmationMessage(FisTypeImpact impact)
        {
            var oldDist = FormatTypeCounts(impact.OldTypeCounts);

            // "Normal" özel davranış
            var normalNote =
                string.Equals(impact.NewType, "Normal", StringComparison.OrdinalIgnoreCase)
                ? "• Bu seçim 'override' kaydını DB'den SİLER (yani varsayılana döner).\n"
                : "• Bu seçim DB'de bir 'override' olarak saklanır.\n";

            // Kaydet derse kaç kayıt yazılacak? = distinct groupKey
            // (senin SaveFisTuruOverridesToDbAsync zaten groupKey bazlı dönüyor)
            var dbWriteCountLine = $"• Kaydet dersen DB'ye yazılacak kayıt: {impact.DistinctGroupKeyCount} (groupKey)\n";

            return
        $@"Fiş Türü Değişikliği Ön İzleme

Fiş No: {impact.EntryNo}
Yeni Tür: {impact.NewType}

Etkilenecek:
• Satır: {impact.TargetRowCount}
• Fiş: {impact.DistinctFisCount}
{dbWriteCountLine}

Önceki tür dağılımı:
• {oldDist}

Ne yapmak istiyorsunuz?
• Evet  : Değişikliği KAYDET (veritabanına yaz)
• Hayır : Şimdilik kaydetme (sadece ekranda kalsın)
• İptal : Geri al (az önceki değişikliği iptal et)

Notlar:
{normalNote}• Kaydetmezseniz bu değişiklik uygulama kapanınca kaybolur.";
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

                FieldMap.Load();

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

                // Focus reset
                ClearFisFocusCore(resetStatus: false);

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

                ComputeRunningBalances(_allRows);

                _dirtyFisTypeGroupKeys.Clear();
                _userChangedSort = false;
                _lastUndo = null;
                RaiseUndoStateChanged();

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

        // ================== ✅ SON IMPORTU GERİ AL (UNDO BATCH) =====================
        private bool CanUndoLastImport() => _context.HasContext && !IsBusy && HasUndoableImport;

        private async Task RefreshUndoLastImportStateAsync()
        {
            if (!_context.HasContext)
            {
                HasUndoableImport = false;
                return;
            }

            try
            {
                var summary = await _dbRepo.GetLastBatchSummaryAsync(_context.CompanyCode!, _context.Year);
                HasUndoableImport = summary is not null;
            }
            catch
            {
                HasUndoableImport = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanUndoLastImport))]
        private async Task UndoLastImportAsync()
        {
            if (!_context.HasContext)
            {
                StatusText = "Önce bağlam seçin.";
                HasUndoableImport = false;
                return;
            }

            try
            {
                IsBusy = true;

                StatusText = "Son import bilgisi alınıyor…";
                var summary = await _dbRepo.GetLastBatchSummaryAsync(_context.CompanyCode!, _context.Year);

                if (summary is null)
                {
                    HasUndoableImport = false;
                    StatusText = "Geri alınacak import bulunamadı.";
                    MessageBox.Show("Geri alınacak import bulunamadı.", "Yüklemeyi Geri Al", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var monthsText = summary.Months.Count == 0
                    ? "-"
                    : string.Join(", ", summary.Months.Select(m => m.ToString("00")));

                var msg =
                    $"Son yükleme geri alınacak.\n\n" +
                    $"Şirket : {summary.CompanyCode}\n" +
                    $"Yıl    : {summary.Year}\n" +
                    $"Dosya  : {summary.SourceFile}\n" +
                    $"Tarih  : {summary.LoadedAt:dd.MM.yyyy HH:mm}\n" +
                    $"Aylar  : {monthsText}\n" +
                    $"Satır  : {summary.RowCount:N0}\n\n" +
                    "Devam edilsin mi?";

                var ok = MessageBox.Show(msg, "Yüklemeyi Geri Al", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ok != MessageBoxResult.Yes)
                {
                    StatusText = "Geri alma iptal edildi.";
                    return;
                }

                StatusText = "Geri alma yapılıyor…";
                var res = await _dbRepo.UndoLastImportBatchAsync(_context.CompanyCode!, _context.Year);

                if (!res.ok)
                {
                    StatusText = res.message;
                    MessageBox.Show(res.message, "Geri Alma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                StatusText = res.message + " Yeniden yükleniyor…";
                await LoadFromDatabaseAsync();

                await RefreshUndoLastImportStateAsync();

                MessageBox.Show(res.message, "Geri Alma Tamamlandı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText = "Geri alma hatası: " + ex.Message;
                Logger.WriteLine("[UNDO ERROR] " + ex);
                MessageBox.Show(ex.Message, "Geri Alma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ================== Filtre / Temizle =====================
        [RelayCommand]
        private async Task ApplyFilters()
        {
            if (IsFisFocusActive) ClearFisFocusCore(resetStatus: false);

            var filtered = ApplyFilterLogic(_allRows);
            ApplyCurrentFilterToView(filtered);
            await ComputeContraForVisibleAsync();
            StatusText = $"Filtre uygulandı. Görünen {Rows.Count} satır.";
        }

        [RelayCommand]
        private async Task ResetFilters()
        {
            if (IsFisFocusActive) ClearFisFocusCore(resetStatus: false);

            Filters.Reset();
            ApplyCurrentFilterToView(_allRows);
            await ComputeContraForVisibleAsync();
            StatusText = "Filtreler sıfırlandı.";
        }

        [RelayCommand]
        private void ClearAll()
        {
            if (IsBusy)
            {
                MessageBox.Show(
                    "Veri yüklenirken ekran boşaltılamaz. Lütfen yükleme bitince tekrar deneyin.",
                    "Veriyi Boşalt",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Sayı bilgisi (null-safe)
            var visibleCount = Rows?.Count ?? 0;
            var allCount = _allRows?.Count ?? 0;

            var details = (visibleCount == allCount || allCount == 0)
                ? $"{visibleCount:n0} satır ekrandan kaldırılacak."
                : $"{visibleCount:n0} (ekranda) / {allCount:n0} (toplam) satır ekrandan kaldırılacak.";

            var res = MessageBox.Show(
                details +
                "\n\nBu işlem yalnızca ekrandaki veriyi kaldırır; veritabanını etkilemez.\n" +
                "Geri yüklemek için 'Veriyi Yeniden Yükle' butonunu kullanabilirsiniz.\n\n" +
                "Devam etmek istiyor musunuz?",
                "Veriyi Boşalt",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
                return;

            // ---- Asıl temizleme ----
            ClearFisFocusCore(resetStatus: false);

            _selectedInputs?.Clear();

            _allRows?.Clear();
            Rows?.Clear();

            Filters?.Reset();
            Totals?.Reset();
            UpdateTotalsText();

            _dirtyFisTypeGroupKeys?.Clear();
            _lastUndo = null;
            RaiseUndoStateChanged();

            HasUndoableImport = false;

            StatusText = "Ekrandaki veri boşaltıldı. Geri yüklemek için 'Veriyi Yeniden Yükle' kullanın.";
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
                catch { }
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

            if (IsFisFocusActive)
                _focusedFisRowCount = Rows.Count;

            RaiseFisFocusUiChanged();
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

        // ================== CONTEXT MENU UI HELPERS =====================

        public string ContextMenuFisHeader
        {
            get
            {
                var r = SelectedRow;
                if (r is null) return "Seçili satır yok";

                var entry = (r.EntryNumber ?? "").Trim();
                var date = r.PostingDate?.ToString("dd.MM.yyyy") ?? "-";
                var ft = (r.FisTuru ?? "Normal").Trim();

                return $"Fiş: {entry} | Tarih: {date} | Tür: {ft}";
            }
        }

        public bool CanUndoFisTuruChange => _lastUndo is not null && _lastUndo.Snapshots.Count > 0;

        partial void OnSelectedRowChanged(MuavinRow? value)
        {
            OnPropertyChanged(nameof(ContextMenuFisHeader));
        }

        private void RaiseUndoStateChanged()
        {
            OnPropertyChanged(nameof(CanUndoFisTuruChange));
            OnPropertyChanged(nameof(ContextMenuFisHeader));
        }

        // ================== FİŞ ODAK (B-1 UI STATE) ==================
        private string? _focusedFisNo;
        private int _focusedFisRowCount;

        public bool IsFisFocusActive => !string.IsNullOrWhiteSpace(_focusedFisNo);
        public string? FocusFisNo => _focusedFisNo;
        public int FocusFisRowCount => _focusedFisRowCount;

        public string FisFocusStatusText =>
            IsFisFocusActive
                ? $"Fiş odak: {FocusFisNo} — {FocusFisRowCount} satır (tekrar tıkla: geri dön)"
                : "";

        public string FisFocusBadgeText
            => IsFisFocusActive
                ? $"FİŞ ODAĞI: {_focusedFisNo} ({Rows.Count} satır) — tekrar tıklayınca kapanır"
                : "";

        public string? FocusedFisNo => _focusedFisNo;

        private List<MuavinRow>? _rowsBeforeFisFocus;

        private List<SortDescription>? _sortBeforeFisFocus;
        private bool _userChangedSortBeforeFisFocus;

        private void RaiseFisFocusUiChanged()
        {
            OnPropertyChanged(nameof(IsFisFocusActive));
            OnPropertyChanged(nameof(FocusFisNo));
            OnPropertyChanged(nameof(FocusFisRowCount));
            OnPropertyChanged(nameof(FisFocusStatusText));
            OnPropertyChanged(nameof(FisFocusBadgeText));
            OnPropertyChanged(nameof(FocusedFisNo));
        }

        private List<SortDescription> SnapshotCurrentSort()
        {
            var view = CollectionViewSource.GetDefaultView(Rows);
            if (view == null) return new List<SortDescription>();
            return view.SortDescriptions.ToList();
        }

        private void RestoreSortSnapshot(List<SortDescription>? sorts)
        {
            if (sorts == null) return;

            var view = CollectionViewSource.GetDefaultView(Rows);
            if (view == null) return;

            view.SortDescriptions.Clear();
            foreach (var sd in sorts)
                view.SortDescriptions.Add(sd);

            view.Refresh();
        }

        private void ApplyFixedSortForFisFocus()
        {
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

        private void UpdateFisFocusFlags(string? focusedFisNo)
        {
            var f = (focusedFisNo ?? "").Trim();

            foreach (var r in _allRows)
            {
                var en = (r.EntryNumber ?? "").Trim();
                r.IsFocusedFis = !string.IsNullOrWhiteSpace(f) && string.Equals(en, f, StringComparison.Ordinal);
            }
        }

        // UI’dan çağrılır (code-behind)
        public void ToggleFocusByFisNo(string entryNo)
        {
            entryNo = (entryNo ?? "").Trim();
            if (string.IsNullOrWhiteSpace(entryNo)) return;

            if (string.IsNullOrWhiteSpace(_focusedFisNo))
            {
                _rowsBeforeFisFocus = Rows.ToList();
                _sortBeforeFisFocus = SnapshotCurrentSort();
                _userChangedSortBeforeFisFocus = _userChangedSort;

                _focusedFisNo = entryNo;
                UpdateFisFocusFlags(_focusedFisNo);

                var list = _allRows
                    .Where(r => string.Equals((r.EntryNumber ?? "").Trim(), entryNo, StringComparison.Ordinal))
                    .OrderBy(r => r.PostingDate ?? DateTime.MinValue)
                    .ThenBy(r => r.Kebir)
                    .ThenBy(r => r.HesapKodu)
                    .ThenBy(r => r.EntryNumber)
                    .ThenBy(r => r.EntryCounter ?? 0)
                    .ToList();

                _focusedFisRowCount = list.Count;

                ApplyCurrentFilterToView(list);
                ApplyFixedSortForFisFocus();

                RaiseFisFocusUiChanged();
                StatusText = $"Fiş odak: {entryNo} — {list.Count} satır (tekrar tıkla: geri dön)";
                return;
            }

            if (string.Equals(_focusedFisNo, entryNo, StringComparison.Ordinal))
            {
                ClearFisFocusCore(resetStatus: true);
                return;
            }

            _focusedFisNo = entryNo;

            var next = _allRows
                .Where(r => string.Equals((r.EntryNumber ?? "").Trim(), entryNo, StringComparison.Ordinal))
                .OrderBy(r => r.PostingDate ?? DateTime.MinValue)
                .ThenBy(r => r.Kebir)
                .ThenBy(r => r.HesapKodu)
                .ThenBy(r => r.EntryNumber)
                .ThenBy(r => r.EntryCounter ?? 0)
                .ToList();

            _focusedFisRowCount = next.Count;

            ApplyCurrentFilterToView(next);
            ApplyFixedSortForFisFocus();

            RaiseFisFocusUiChanged();
            StatusText = $"Fiş odak: {entryNo} — {next.Count} satır (tekrar tıkla: geri dön)";
        }

        [RelayCommand]
        private void ClearFisFocus()
        {
            ClearFisFocusCore(resetStatus: true);
        }

        private void ClearFisFocusCore(bool resetStatus)
        {
            if (string.IsNullOrWhiteSpace(_focusedFisNo))
                return;

            _focusedFisNo = null;
            UpdateFisFocusFlags(null);

            _focusedFisRowCount = 0;

            if (_rowsBeforeFisFocus != null)
            {
                ApplyCurrentFilterToView(_rowsBeforeFisFocus);
            }
            else
            {
                var filtered = ApplyFilterLogic(_allRows);
                ApplyCurrentFilterToView(filtered);
            }

            RestoreSortSnapshot(_sortBeforeFisFocus);
            _userChangedSort = _userChangedSortBeforeFisFocus;

            _rowsBeforeFisFocus = null;
            _sortBeforeFisFocus = null;
            _userChangedSortBeforeFisFocus = false;

            RaiseFisFocusUiChanged();

            if (resetStatus)
                StatusText = "Fiş odağı kapatıldı.";
        }

        private static string BuildGroupKey(MuavinRow r)
        {
            var d = r.PostingDate?.ToString("yyyy-MM-dd") ?? "";
            var no = r.EntryNumber ?? "";
            if (r.FisTuru is "Açılış" or "Kapanış") return $"{no}|{d}";
            var doc = r.DocumentNumber ?? "";
            return string.IsNullOrWhiteSpace(doc) ? $"{no}|{d}" : $"{no}|{d}|DOC:{doc}";
        }

        public void ApplyFisTuruFromGrid(MuavinRow row, string? newFisTuru)
        {
            if (!_context.HasContext)
            {
                StatusText = "Önce bağlam seçin.";
                return;
            }

            if (row is null) return;

            var ft = (newFisTuru ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ft)) return;

            var entryNo = (row.EntryNumber ?? "").Trim();
            if (string.IsNullOrWhiteSpace(entryNo))
            {
                StatusText = "Seçili satırda Fiş Numarası yok.";
                return;
            }

            // Bu değişiklik context-menu ile aynı mantıkta: aynı EntryNumber olan tüm satırlara uygula
            var targets = _allRows
                .Where(r => string.Equals((r.EntryNumber ?? "").Trim(), entryNo, StringComparison.Ordinal))
                .ToList();

            if (targets.Count == 0)
            {
                StatusText = "Uygulanacak satır bulunamadı.";
                return;
            }

            // GroupKey yoksa üret (DB override için şart)
            foreach (var r in targets)
                if (string.IsNullOrWhiteSpace(r.GroupKey))
                    r.GroupKey = BuildGroupKey(r);

            var alreadySame = targets.All(r => string.Equals((r.FisTuru ?? "").Trim(), ft, StringComparison.OrdinalIgnoreCase));
            if (alreadySame) return;

            // ✅ Sessiz uygula: pop-up yok, ama Undo + dirty set + ekran refresh var
            ApplyFisTuruToTargetsCore(entryNo, targets, ft);

            // Not: ApplyFisTuruToTargetsCore zaten StatusText basıyor.
            // İstersen burada daha net bir mesaj verebilirsin.
        }



    }

    internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
