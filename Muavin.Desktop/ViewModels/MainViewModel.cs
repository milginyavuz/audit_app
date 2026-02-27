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
using System.Globalization;
using System.Reflection;


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

        [ObservableProperty] private string? _aciklamaAra;

        // ========= (Toolbar için) MANUEL FİŞ TÜRÜ (UI seçimi) =========
        private string? _selectedFisTuru;
        public string? SelectedFisTuru
        {
            get => _selectedFisTuru;
            set => SetProperty(ref _selectedFisTuru, value);
        }

        // ================== ✅ DATA ORIGIN (DB vs PREVIEW) ==================
        private enum DataOrigin
        {
            None = 0,
            Preview = 1,
            Database = 2
        }


        // ===============================
        // ✅ PREVIEW SNAPSHOT CACHE (IMPORT GARANTİ)
        // DB yükleme / filtre / ekran temizleme gibi işlemler _allRows'u etkileyebilir.
        // Import için güvenli kaynak: "preview anında alınan snapshot".
        // ===============================
        private readonly List<MuavinRow> _previewRowsCache = new();
        private readonly List<string> _previewInputsCache = new();

        private bool HasPreviewCache => _previewRowsCache.Count > 0;

        private void CacheCurrentPreviewSnapshot()
        {
            try
            {
                _previewRowsCache.Clear();
                _previewRowsCache.AddRange(_allRows);

                _previewInputsCache.Clear();
                if (_selectedInputs is { Count: > 0 })
                    _previewInputsCache.AddRange(_selectedInputs);

                Logger.WriteLine($"[PREVIEW] cache_saved has={HasPreviewCache} rows={_previewRowsCache.Count} inputs={_previewInputsCache.Count}");
            }
            catch (Exception ex)
            {
                _previewRowsCache.Clear();
                _previewInputsCache.Clear();
                Logger.WriteLine("[PREVIEW] cache_saved ERROR: " + ex);
            }
        }

        private void ClearPreviewCache()
        {
            _previewRowsCache.Clear();
            _previewInputsCache.Clear();
        }



        private DataOrigin _dataOrigin = DataOrigin.None;

        // ================== ✅ QUICK FILTER HELPERS (GENEL) ==================
        private static readonly CultureInfo TR = CultureInfo.GetCultureInfo("tr-TR");
        private static readonly Dictionary<string, string> QuickFilterHeaderMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Kayıt No"] = nameof(MuavinRow.EntryCounter),
                ["Kebir"] = nameof(MuavinRow.Kebir),
                ["Hesap Kodu"] = nameof(MuavinRow.HesapKodu),
                ["Hesap Adı"] = nameof(MuavinRow.HesapAdi),
                ["Fiş Tarihi"] = nameof(MuavinRow.PostingDate),
                ["Fiş Numarası"] = nameof(MuavinRow.EntryNumber),
                ["Fiş Türü"] = nameof(MuavinRow.FisTuru),
                ["Açıklama"] = nameof(MuavinRow.Aciklama),
                ["Borç"] = nameof(MuavinRow.Borc),
                ["Alacak"] = nameof(MuavinRow.Alacak),
                ["Tutar"] = nameof(MuavinRow.Tutar),
                ["Bakiye"] = nameof(MuavinRow.RunningBalance),
                ["Karşı Hesap"] = nameof(MuavinRow.KarsiHesap),
            };


        private static string BuildInputsListTextCore(IReadOnlyList<string> inputs, int maxItems = 25)
        {
            if (inputs == null || inputs.Count == 0)
                return "• (dosya seçilmedi)";

            var files = inputs
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    try { return Path.GetFileName(p); }
                    catch { return p; }
                })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            if (files.Count == 0)
                return "• (dosya seçilmedi)";

            if (files.Count <= maxItems)
                return "• " + string.Join("\n• ", files);

            var head = files.Take(maxItems).ToList();
            var rest = files.Count - maxItems;

            return "• " + string.Join("\n• ", head) + $"\n• … (+{rest} dosya daha)";
        }

        private static string BuildInputsSummaryLineCore(IReadOnlyList<string> inputs)
        {
            if (inputs == null || inputs.Count == 0)
                return "Seçim: 0 dosya";

            int xml = 0, txt = 0, zip = 0, other = 0;

            foreach (var p in inputs.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var ext = "";
                try { ext = Path.GetExtension(p).ToLowerInvariant(); } catch { }

                if (ext == ".xml") xml++;
                else if (ext == ".txt") txt++;
                else if (ext == ".zip") zip++;
                else other++;
            }

            var total = inputs.Count;
            var parts = new List<string> { $"Toplam {total} dosya" };
            if (xml > 0) parts.Add($"XML: {xml}");
            if (txt > 0) parts.Add($"TXT: {txt}");
            if (zip > 0) parts.Add($"ZIP: {zip}");
            if (other > 0) parts.Add($"Diğer: {other}");

            return "Seçim: " + string.Join(" | ", parts);
        }

        private static string BuildSourceDisplayNameFrom(IReadOnlyList<string> inputs)
        {
            if (inputs == null || inputs.Count == 0) return "-";
            if (inputs.Count == 1)
            {
                try { return Path.GetFileName(inputs[0]); }
                catch { return inputs[0]; }
            }
            return $"{inputs.Count} dosya";
        }

        private static string BuildSourceFileForBatchFrom(IReadOnlyList<string> inputs)
        {
            if (inputs == null || inputs.Count == 0) return "_manual_import_";
            if (inputs.Count == 1) return inputs[0];
            return "_multi_select_";
        }



        private string BuildSelectedInputsSummaryLine()
        {
            if (_selectedInputs == null || _selectedInputs.Count == 0)
                return "Seçim: 0 dosya";

            int xml = 0, txt = 0, zip = 0, other = 0;

            foreach (var p in _selectedInputs.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var ext = "";
                try { ext = Path.GetExtension(p).ToLowerInvariant(); } catch { }

                if (ext == ".xml") xml++;
                else if (ext == ".txt") txt++;
                else if (ext == ".zip") zip++;
                else other++;
            }

            var total = _selectedInputs.Count;
            var parts = new List<string> { $"Toplam {total} dosya" };
            if (xml > 0) parts.Add($"XML: {xml}");
            if (txt > 0) parts.Add($"TXT: {txt}");
            if (zip > 0) parts.Add($"ZIP: {zip}");
            if (other > 0) parts.Add($"Diğer: {other}");

            return "Seçim: " + string.Join(" | ", parts);
        }

        private static string NormalizeQuickFilterField(string field)
        {
            field = (field ?? "").Trim();
            if (field.Length == 0) return field;

            if (QuickFilterHeaderMap.TryGetValue(field, out var mapped))
                return mapped;

            return field;
        }

        private static string GetCellTextForQuickFilter(object row, string propertyPath)
        {
            if (row == null) return "";
            propertyPath = (propertyPath ?? "").Trim();
            if (propertyPath.Length == 0) return "";

            object? current = row;

            foreach (var part in propertyPath.Split('.'))
            {
                if (current == null) return "";

                var t = current.GetType();
                var p = t.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) return "";

                current = p.GetValue(current);
            }

            if (current == null) return "";

            var type = current.GetType();
            var underlying = Nullable.GetUnderlyingType(type);

            if (underlying == typeof(DateTime))
                return current.ToString() ?? "";

            return current switch
            {
                DateTime dt => dt.ToString("dd.MM.yyyy", TR),
                DateTimeOffset dto => dto.ToString("dd.MM.yyyy", TR),

                decimal dec => dec.ToString("N2", TR),
                double dbl => dbl.ToString("N2", TR),
                float flt => ((double)flt).ToString("N2", TR),

                int i => i.ToString(TR),
                long l => l.ToString(TR),
                short s => s.ToString(TR),

                bool b => b ? "Evet" : "Hayır",

                _ => current.ToString() ?? ""
            };
        }

        public IReadOnlyList<string> FisTuruOptions { get; } =
            new[] { "Normal", "Açılış", "Kapanış", "Gelir Tablosu Kapanış", "Yansıtma Kapama" };

        public IReadOnlyList<string> AvailableFisTypes => FisTuruOptions;

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

        // ===================== ✅ UNDO LAST IMPORT UX STATE =====================
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

            _context.PropertyChanged += async (_, __) =>
            {
                OnPropertyChanged(nameof(ContextDisplay));
                OnPropertyChanged(nameof(UndoLastImportToolTip));

                await RefreshUndoLastImportStateAsync();
                UndoLastImportCommand.NotifyCanExecuteChanged();
            };

            _ = RefreshUndoLastImportStateAsync();
        }

        private void Rows_CollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
        {
            RecomputeTotals();

            if (IsFisFocusActive)
            {
                _focusedFisRowCount = Rows.Count;
                RaiseFisFocusUiChanged();
            }
        }

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



        // ================== ✅ IMPORT TO DB =====================
        [RelayCommand]
        private async Task ImportToDatabaseAsync()
        {
            void LogState(string stage)
            {
                Logger.WriteLine($"[IMPORT][{stage}] vm_hash={this.GetHashCode()} thread={Environment.CurrentManagedThreadId}");
                Logger.WriteLine($"[IMPORT][{stage}] context_has={_context.HasContext} context={_context.CompanyCode}/{_context.Year} companyName={_context.CompanyName}");
                Logger.WriteLine($"[IMPORT][{stage}] selectedInputs.count={_selectedInputs?.Count ?? -1}");
                Logger.WriteLine($"[IMPORT][{stage}] allRows.count={_allRows?.Count ?? -1}");
                Logger.WriteLine($"[IMPORT][{stage}] rowsView.count={Rows?.Count ?? -1}");
                Logger.WriteLine($"[IMPORT][{stage}] isBusy={IsBusy} dataOrigin={_dataOrigin}");
                Logger.WriteLine($"[IMPORT][{stage}] hasPreviewCache={HasPreviewCache}");
            }

            if (!_context.HasContext)
            {
                StatusText = "Önce bağlam seçin (Şirket/Yıl).";
                return;
            }

            string? importLogPath = null;

            try
            {
                IsBusy = true;

                importLogPath = LogPaths.NewLogFilePath("debug_import");
                Logger.Init(importLogPath, overwrite: true, forceReinit: true);

                Logger.WriteLine("[IMPORT] LOG PATH=" + importLogPath);
                Logger.WriteLine($"[IMPORT] Started. user={Environment.UserName}");
                LogState("START");

                // ============================================================
                // ✅ IMPORT SOURCE SNAPSHOT (PREVIEW CACHE > LIVE PREVIEW > PICK)
                // Amaç: Import sırasında _allRows/_selectedInputs değişse bile
                // aynı snapshot ile importu tamamlamak.
                // ============================================================
                List<MuavinRow> rowsForImport;
                IReadOnlyList<string> inputsForImport;

                bool haveCachePreview = HasPreviewCache;

                bool haveLivePreview =
                    !haveCachePreview &&
                    (_dataOrigin == DataOrigin.Preview) &&
                    _allRows is { Count: > 0 };

                if (haveCachePreview)
                {
                    rowsForImport = _previewRowsCache.ToList();     // snapshot of cache
                    inputsForImport = _previewInputsCache.ToList();

                    Logger.WriteLine($"[IMPORT] using PREVIEW CACHE rows={rowsForImport.Count:n0} inputs={inputsForImport.Count}");
                    LogState("USING_PREVIEW_CACHE");
                }
                else if (haveLivePreview)
                {
                    rowsForImport = _allRows.ToList();              // snapshot
                    inputsForImport = _selectedInputs?.ToList() ?? new List<string>();

                    Logger.WriteLine($"[IMPORT] using LIVE preview snapshot rows={rowsForImport.Count:n0} inputs={inputsForImport.Count}");
                    LogState("USING_LIVE_PREVIEW_SNAPSHOT");
                }
                else
                {
                    StatusText = "Dosya seçiliyor…";
                    Logger.WriteLine("[IMPORT] no preview -> PickAndPreviewInternalAsync()");
                    var pickedOk = await PickAndPreviewInternalAsync();

                    // ⚠️ Pick&Preview içinde Logger.Init(preview) yapılmış olabilir.
                    // Import loguna geri dön.
                    if (!string.IsNullOrWhiteSpace(importLogPath))
                        Logger.Init(importLogPath, overwrite: false, forceReinit: true);

                    Logger.WriteLine("[IMPORT] PickAndPreviewInternalAsync returned: " + pickedOk);
                    LogState("AFTER_PICK_PREVIEW");

                    if (!pickedOk)
                    {
                        StatusText = "İşlem iptal edildi.";
                        Logger.WriteLine("[IMPORT] canceled at file picker");
                        return;
                    }

                    // Preview bittiğinde cache zaten alınmış olmalı; yine de garanti:
                    if (!HasPreviewCache && _allRows.Count > 0)
                        CacheCurrentPreviewSnapshot();

                    if (_allRows.Count == 0 && !HasPreviewCache)
                    {
                        StatusText = "Aktarılacak veri yok (preview boş).";
                        Logger.WriteLine("[IMPORT] abort: preview empty after pick");
                        LogState("ABORT_EMPTY_AFTER_PICK");
                        return;
                    }

                    // Cache varsa cache'i kullan
                    if (HasPreviewCache)
                    {
                        rowsForImport = _previewRowsCache.ToList();
                        inputsForImport = _previewInputsCache.ToList();
                        Logger.WriteLine($"[IMPORT] using PICKED PREVIEW CACHE rows={rowsForImport.Count:n0} inputs={inputsForImport.Count}");
                        LogState("USING_PICKED_PREVIEW_CACHE");
                    }
                    else
                    {
                        rowsForImport = _allRows.ToList();
                        inputsForImport = _selectedInputs?.ToList() ?? new List<string>();
                        Logger.WriteLine($"[IMPORT] using PICKED LIVE snapshot rows={rowsForImport.Count:n0} inputs={inputsForImport.Count}");
                        LogState("USING_PICKED_PREVIEW_SNAPSHOT");
                    }
                }

                if (rowsForImport.Count == 0)
                {
                    StatusText = "Aktarılacak veri yok.";
                    Logger.WriteLine("[IMPORT] abort: rowsForImport=0");
                    LogState("ABORT_EMPTY_SNAPSHOT");
                    return;
                }


                // ============================================================
                // ✅ CONTEXT YEAR FILTER (ONLY IMPORT SELECTED YEAR)
                // Amaç: ContextWindow'da seçilen yıl dışındaki satırlar DB'ye yazılmasın.
                // ============================================================
                var targetYear = _context.Year;

                // rowsForImport snapshot -> filtered snapshot
                var beforeCount = rowsForImport.Count;
                var filtered = rowsForImport
                    .Where(r => r.PostingDate.HasValue && r.PostingDate.Value.Year == targetYear)
                    .ToList();

                var skipped = beforeCount - filtered.Count;

                Logger.WriteLine($"[IMPORT] ContextYearFilter targetYear={targetYear} before={beforeCount:n0} after={filtered.Count:n0} skipped={skipped:n0}");

                if (filtered.Count == 0)
                {
                    StatusText = $"Aktarılacak veri yok. (Bağlam yılı: {targetYear})";
                    Logger.WriteLine("[IMPORT] abort: after ContextYearFilter = 0");
                    LogState("ABORT_EMPTY_AFTER_CONTEXT_YEAR_FILTER");
                    return;
                }

                // IReadOnlyList olduğu için referansı yeni listeye çeviriyoruz
                rowsForImport = filtered;

                // UI'ye uyarı (istersen kapatırız ama faydalı)
                if (skipped > 0)
                {
                    StatusText = $"Uyarı: {skipped:n0} satır {targetYear} dışı olduğu için import edilmedi.";
                }


                // UI mesajı için kaynak adı/özet (snapshot üzerinden)
                string srcText;
                if (inputsForImport.Count == 0) srcText = "-";
                else if (inputsForImport.Count == 1) srcText = Path.GetFileName(inputsForImport[0]);
                else srcText = $"{inputsForImport.Count} dosya";

                int xml = 0, txt = 0, zip = 0, other = 0;
                foreach (var p in inputsForImport.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    string ext;
                    try { ext = Path.GetExtension(p).ToLowerInvariant(); }
                    catch { ext = ""; }

                    if (ext == ".xml") xml++;
                    else if (ext == ".txt") txt++;
                    else if (ext == ".zip") zip++;
                    else other++;
                }

                var parts = new List<string> { $"Toplam {inputsForImport.Count} dosya" };
                if (xml > 0) parts.Add($"XML: {xml}");
                if (txt > 0) parts.Add($"TXT: {txt}");
                if (zip > 0) parts.Add($"ZIP: {zip}");
                if (other > 0) parts.Add($"Diğer: {other}");
                var filesSummary = "Seçim: " + string.Join(" | ", parts);

                string filesList;
                {
                    var names = inputsForImport
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(p =>
                        {
                            try { return Path.GetFileName(p); }
                            catch { return p; }
                        })
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();

                    if (names.Count == 0) filesList = "• (dosya seçilmedi)";
                    else if (names.Count <= 25) filesList = "• " + string.Join("\n• ", names);
                    else
                    {
                        var head = names.Take(25).ToList();
                        var rest = names.Count - 25;
                        filesList = "• " + string.Join("\n• ", head) + $"\n• … (+{rest} dosya daha)";
                    }
                }

                var msg =
                    $"Seçilen kaynak: {srcText}\n" +
                    $"{filesSummary}\n\n" +
                    $"Seçilen dosyalar:\n{filesList}\n\n" +
                    $"Önizleme satır sayısı: {rowsForImport.Count:N0}\n\n" +
                    "Bu veriler veritabanına eklensin mi?";

                var confirm = MessageBox.Show(msg, "Veritabanına Veri Ekle", MessageBoxButton.YesNo, MessageBoxImage.Question);
                Logger.WriteLine("[IMPORT] confirmation result: " + confirm);

                if (confirm != MessageBoxResult.Yes)
                {
                    StatusText = "DB ekleme iptal edildi (preview kaldı).";
                    Logger.WriteLine("[IMPORT] user canceled at confirmation");
                    LogState("CANCEL_CONFIRM");
                    return;
                }

                StatusText = "DB’ye import ediliyor…";
                Logger.WriteLine("[IMPORT] confirmed by user");
                LogState("BEFORE_VALIDATION");

                WriteQuickValidationLog(
                    rows: rowsForImport,
                    selectedInputs: inputsForImport,
                    contextCompany: _context.CompanyCode,
                    contextYear: _context.Year
                );

                // ✅ Context year dışına taşan satırları yakala (kafa karışmasın)
                var yearsInPayload = rowsForImport
                    .Where(r => r.PostingDate.HasValue)
                    .Select(r => r.PostingDate!.Value.Year)
                    .Distinct()
                    .OrderBy(y => y)
                    .ToList();

                if (yearsInPayload.Count > 0 && yearsInPayload.Any(y => y != _context.Year))
                {
                    var msgYear =
                        $"Seçili bağlam yılı: {_context.Year}\n" +
                        $"Dosyadaki yıl(lar): {string.Join(", ", yearsInPayload)}\n\n" +
                        "Bu dosyada bağlam yılı dışındaki satırlar var.\n\n" +
                        "Evet = Sadece bağlam yılı satırlarını import et\n" +
                        "Hayır = Tüm yılları olduğu gibi import et\n" +
                        "İptal = Import’u durdur";

                    var ans = MessageBox.Show(msgYear, "Yıl Uyuşmazlığı", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                    if (ans == MessageBoxResult.Cancel)
                    {
                        StatusText = "Import iptal edildi (yıl uyuşmazlığı).";
                        return;
                    }

                    if (ans == MessageBoxResult.Yes)
                    {
                        var filteredRows = rowsForImport
                            .Where(r => r.PostingDate?.Year == _context.Year)
                            .ToList();

                        Logger.WriteLine($"[IMPORT] year-mismatch -> FILTERED to contextYear={_context.Year} rows={filteredRows.Count:n0} (was {rowsForImport.Count:n0})");
                        rowsForImport = filteredRows;
                    }
                    else
                    {
                        Logger.WriteLine("[IMPORT] year-mismatch -> user chose to import ALL years in payload");
                    }
                }


                string sourceFileForBatch;
                if (inputsForImport.Count == 0) sourceFileForBatch = "_manual_import_";
                else if (inputsForImport.Count == 1) sourceFileForBatch = inputsForImport[0];
                else sourceFileForBatch = "_multi_select_";

                Logger.WriteLine("[IMPORT] sourceFileForBatch=" + sourceFileForBatch);
                Logger.WriteLine("[IMPORT] BulkInsertRowsAsync begin...");
                Logger.WriteLine($"[IMPORT] snapshot rows={rowsForImport.Count:n0} inputs={inputsForImport.Count}");
                LogState("BEFORE_BULK");

                await _dbRepo.BulkInsertRowsAsync(
                    companyCode: _context.CompanyCode!,
                    rows: rowsForImport,
                    sourceFile: sourceFileForBatch,
                    replaceExistingForSameSource: true,
                    companyName: _context.CompanyName,
                    ct: default,
                    replaceMode: DbMuavinRepository.ImportReplaceMode.MonthsInPayload
                );

                Logger.WriteLine("[IMPORT] BulkInsertRowsAsync ok");
                LogState("AFTER_BULK");

                StatusText = "Import tamamlandı. DB’den yeniden yükleniyor…";
                await LoadFromDatabaseAsync();

                await RefreshUndoLastImportStateAsync();
                UndoLastImportCommand.NotifyCanExecuteChanged();

                StatusText = "DB import tamamlandı.";
                Logger.WriteLine("[IMPORT] done ok");
                LogState("DONE");
            }
            catch (Exception ex)
            {
                StatusText = "Import hatası: " + ex.Message;
                Logger.WriteLine("[IMPORT ERROR] " + ex);
                try { LogState("ERROR"); } catch { }
            }
            finally
            {
                try { Logger.Close(); } catch { }
                IsBusy = false;

                if (!string.IsNullOrWhiteSpace(importLogPath))
                    StatusText = (StatusText ?? "") + $" (Log: {importLogPath})";
            }
        }

        private void WriteQuickValidationLog(
             IReadOnlyList<MuavinRow> rows,
             IEnumerable<string>? selectedInputs,
             string? contextCompany,
             int contextYear)
        {
            try
            {
                if (rows == null || rows.Count == 0)
                {
                    Logger.WriteLine("[QV] rows=0");
                    return;
                }

                var safeInputs = (selectedInputs ?? Array.Empty<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

                var total = rows.Count;
                var postingNull = rows.Count(r => !r.PostingDate.HasValue);
                var entryNull = rows.Count(r => string.IsNullOrWhiteSpace(r.EntryNumber));
                var gkNull = rows.Count(r => string.IsNullOrWhiteSpace(r.GroupKey));
                var docNull = rows.Count(r => string.IsNullOrWhiteSpace(r.DocumentNumber));

                DateTime? minDate = rows.Where(r => r.PostingDate.HasValue).Min(r => r.PostingDate) ?? null;
                DateTime? maxDate = rows.Where(r => r.PostingDate.HasValue).Max(r => r.PostingDate) ?? null;

                var ym = rows.Where(r => r.PostingDate.HasValue)
                             .Select(r => r.PostingDate!.Value.Year * 100 + r.PostingDate!.Value.Month)
                             .Distinct()
                             .OrderBy(x => x)
                             .ToList();

                Logger.WriteLine("====================================================");
                Logger.WriteLine("[QV] QUICK VALIDATION (BEFORE DB IMPORT)");
                Logger.WriteLine($"[QV] Context   : company={contextCompany ?? "-"} year={contextYear}");
                Logger.WriteLine($"[QV] Inputs    : {safeInputs.Length} file(s) => {string.Join(" | ", safeInputs.Select(Path.GetFileName))}");
                Logger.WriteLine($"[QV] Rows      : total={total:n0}");
                Logger.WriteLine($"[QV] Nulls     : posting_date_null={postingNull:n0} entry_number_null={entryNull:n0} group_key_null={gkNull:n0} doc_no_null={docNull:n0}");
                Logger.WriteLine($"[QV] DateRange : {minDate:yyyy-MM-dd} .. {maxDate:yyyy-MM-dd}");
                Logger.WriteLine($"[QV] YM        : count={ym.Count} => {string.Join(", ", ym.Select(x => (x % 100).ToString("00") + "/" + (x / 100)))}");

                // Kaynağa göre kabaca dağılım (XML/TXT/ZIP)
                int xml = safeInputs.Count(p => Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase));
                int txt = safeInputs.Count(p => Path.GetExtension(p).Equals(".txt", StringComparison.OrdinalIgnoreCase));
                int zip = safeInputs.Count(p => Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase));
                Logger.WriteLine($"[QV] FileTypes : xml={xml} txt={txt} zip={zip}");

                // Fiş türü dağılımı (ilk 10)
                var ftMap = rows.GroupBy(r => (r.FisTuru ?? "Normal").Trim(), StringComparer.OrdinalIgnoreCase)
                                .OrderByDescending(g => g.Count())
                                .Take(10)
                                .Select(g => $"{g.Key}:{g.Count():n0}");
                Logger.WriteLine($"[QV] FisTuru   : {string.Join(", ", ftMap)}");

                Logger.WriteLine("====================================================");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[QV ERROR] " + ex);
            }
        }

        private async Task<bool> PickAndPreviewInternalAsync()
        {
            var dlg = new CommonOpenFileDialog
            {
                Title = "XML/TXT dosyası veya ZIP seç (tek/çoklu)",
                EnsurePathExists = true,
                Multiselect = true,
                IsFolderPicker = false
            };

            dlg.Filters.Add(new CommonFileDialogFilter("e-Defter / Muavin (XML, TXT, ZIP)", "*.xml;*.txt;*.zip"));
            dlg.Filters.Add(new CommonFileDialogFilter("XML", "*.xml"));
            dlg.Filters.Add(new CommonFileDialogFilter("TXT", "*.txt"));
            dlg.Filters.Add(new CommonFileDialogFilter("ZIP", "*.zip"));
            dlg.Filters.Add(new CommonFileDialogFilter("Tümü", "*.*"));

            if (dlg.ShowDialog() != CommonFileDialogResult.Ok)
                return false;

            _selectedInputs.Clear();
            _selectedInputs.AddRange(dlg.FileNames);

            StatusText = $"{_selectedInputs.Count} seçim yapıldı. Önizleme yükleniyor…";

            await ParsePreviewAsync();
            return true;
        }

        private string BuildSourceDisplayName()
        {
            if (_selectedInputs.Count == 0) return "-";
            if (_selectedInputs.Count == 1) return System.IO.Path.GetFileName(_selectedInputs[0]);
            return $"{_selectedInputs.Count} dosya";
        }

        private string BuildSourceFileForBatch()
        {
            if (_selectedInputs.Count == 0) return "_manual_import_";
            if (_selectedInputs.Count == 1) return _selectedInputs[0];
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
                _dataOrigin = DataOrigin.None;
                return;
            }

            try
            {
                IsBusy = true;
                StatusText = $"{_context.Display} verileri yükleniyor…";

                _dataOrigin = DataOrigin.Database;

                // 1) çek
                var rows = await _dbRepo.GetRowsAsync(_context.CompanyCode!, _context.Year);

                // 2) override'ları uygula (fis türü)
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

                // 3) DB-side karsi_hesap eksikse: UI-side hesaplama YOK.
                //    Onun yerine DB'ye backfill + tekrar çek.
                if (rows.Any(r => string.IsNullOrWhiteSpace(r.KarsiHesap)))
                {
                    StatusText = "DB'de eksik Karşı Hesap var — düzeltiliyor…";

                    await _dbRepo.RecomputeKarsiHesapForCompanyYearAsync(_context.CompanyCode!, _context.Year);

                    // tekrar çek
                    rows = await _dbRepo.GetRowsAsync(_context.CompanyCode!, _context.Year);

                    // override tekrar (çünkü rows yenilendi)
                    if (overrides.Count > 0)
                    {
                        foreach (var r in rows)
                        {
                            if (!string.IsNullOrWhiteSpace(r.GroupKey) &&
                                overrides.TryGetValue(r.GroupKey!, out var ft))
                                r.FisTuru = ft;
                        }
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
                ComputeFisBalanceFlags(_allRows);

                _dirtyFisTypeGroupKeys.Clear();
                _userChangedSort = false;

                _lastUndo = null;
                RaiseUndoStateChanged();

                ClearFisFocusCore(resetStatus: false);

                ApplyCurrentFilterToView(_allRows);

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
        //  ✅ MANUEL FİŞ TÜRÜ OVERRIDE
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
        //  CONTEXT MENU: Fiş Türü — Komutlar
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

            var key = KeyFor(SelectedRow);

            var targets = _allRows
                .Where(r => string.Equals(KeyFor(r), key, StringComparison.Ordinal))
                .ToList();

            if (targets.Count == 0)
            {
                StatusText = "Uygulanacak satır bulunamadı.";
                return;
            }

            // ✅ DB load’da group_key zaten var; preview’da yoksa üret.
            foreach (var r in targets)
                if (string.IsNullOrWhiteSpace(r.GroupKey))
                    r.GroupKey = BuildGroupKeyFallback(r);

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

            entryNo = (entryNo ?? "").Trim();
            newFisTuru = (newFisTuru ?? "").Trim();
            if (string.IsNullOrWhiteSpace(newFisTuru)) return false;

            var undo = new UndoFisTypeChange
            {
                EntryNo = entryNo,
                GroupKey = (targets.FirstOrDefault()?.GroupKey ?? "").Trim(),
                NewFisTuru = newFisTuru,
                DirtyBefore = new HashSet<string>(_dirtyFisTypeGroupKeys, StringComparer.Ordinal)
            };

            foreach (var r in targets)
                undo.Snapshots.Add((r, r.FisTuru, r.GroupKey));

            foreach (var r in targets)
            {
                r.FisTuru = newFisTuru;

                if (string.IsNullOrWhiteSpace(r.GroupKey))
                    r.GroupKey = BuildGroupKeyFallback(r);

                _dirtyFisTypeGroupKeys.Add(r.GroupKey);
            }

            _lastUndo = undo;
            RaiseUndoStateChanged();

            ComputeFisBalanceFlags(_allRows);

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

                if (upserts.Count > 0)
                {
                    await _dbRepo.UpsertFisTypeOverridesAsync(
                        companyCode: _context.CompanyCode!,
                        year: _context.Year,
                        items: upserts,
                        updatedBy: Environment.UserName);
                }

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
            return string.Join(", ",
                map.OrderByDescending(kv => kv.Value)
                   .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                   .Select(kv => $"{kv.Key}: {kv.Value}"));
        }

        private FisTypeImpact BuildFisTypeImpact(string entryNo, List<MuavinRow> targets, string newType)
        {
            var gk = (targets.FirstOrDefault()?.GroupKey ?? "").Trim();

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

            var normalNote =
                string.Equals(impact.NewType, "Normal", StringComparison.OrdinalIgnoreCase)
                ? "• Bu seçim 'override' kaydını DB'den SİLER (yani varsayılana döner).\n"
                : "• Bu seçim DB'de bir 'override' olarak saklanır.\n";

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
            string? previewLogPath = null;

            try
            {
                IsBusy = true;
                ProgressValue = 0;
                StatusText = "Hazırlanıyor…";

                _dataOrigin = DataOrigin.Preview;

                previewLogPath = LogPaths.NewLogFilePath("debug_preview");
                Logger.Init(previewLogPath, overwrite: true, forceReinit: true);

                Logger.WriteLine("[PREVIEW] LOG PATH=" + previewLogPath);
                Logger.WriteLine($"[PREVIEW] Started. context={_context.CompanyCode}/{_context.Year} user={Environment.UserName}");
                Logger.WriteLine($"[PREVIEW] selectedInputs={_selectedInputs.Count}");
                Logger.WriteLine($"[PREVIEW] vm_hash={this.GetHashCode()} thread={Environment.CurrentManagedThreadId}");

                StatusText = $"Hazırlanıyor… (Log: {previewLogPath})";

                FieldMap.Load();

                var (files, temps) = ExpandToDataFilesWithTemps(_selectedInputs);
                tempDirs = temps;

                Logger.WriteLine($"[PREVIEW] files.count={files.Count}");

                if (files.Count == 0)
                {
                    StatusText = "Seçimlerde XML/TXT bulunamadı.";
                    Logger.WriteLine("[PREVIEW] abort: no xml/txt found in selection");
                    return;
                }

                _allRows.Clear();
                Rows.Clear();
                Totals.Reset();

                ClearFisFocusCore(resetStatus: false);

                ProgressMax = files.Count;
                int parsedCount = 0;

                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    List<MuavinRow> list;

                    Logger.WriteLine("[PREVIEW] parsing: " + file);

                    if (ext == ".txt")
                    {
                        int py, pm;
                        list = _txtParser.Parse(file, _context.CompanyCode ?? "", out py, out pm);

                        var meta = _txtParser.LastMeta;
                        Logger.WriteLine($"[PREVIEW][TXT] delimiter={meta.Delimiter} parsed={meta.ParsedRowCount} skipped={meta.SkippedRowCount} ymCount={meta.DistinctYearMonthCount} min={meta.MinDate:yyyy-MM-dd} max={meta.MaxDate:yyyy-MM-dd}");

                        if (meta.DistinctYearMonthCount > 1)
                            Logger.WriteLine($"[TXT META] {Path.GetFileName(file)} => {meta.DistinctYearMonthCount} ay (min={meta.MinDate:yyyy-MM-dd}, max={meta.MaxDate:yyyy-MM-dd})");
                    }
                    else
                    {
                        var parsed = _parser.Parse(file);
                        list = parsed?.ToList() ?? new List<MuavinRow>();
                        Logger.WriteLine($"[PREVIEW][XML] rows={list.Count}");
                    }

                    // ✅ Preview’da group_key üretimi DB ile uyumlu olsun:
                    var normalizedSource = NormalizeSourceFileForUi(file);

                    foreach (var r in list)
                        if (string.IsNullOrWhiteSpace(r.GroupKey))
                            r.GroupKey = BuildGroupKeyForUi(r, normalizedSource);

                    _allRows.AddRange(list);
                    parsedCount += list.Count;

                    ProgressValue++;
                    StatusText = $"Önizleme yükleniyor… ({ProgressValue}/{ProgressMax})";
                    await Task.Yield();
                }

                Logger.WriteLine($"[PREVIEW] totalRows(parsed)={parsedCount}");

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
                ComputeFisBalanceFlags(_allRows);

                _dirtyFisTypeGroupKeys.Clear();
                _userChangedSort = false;
                _lastUndo = null;
                RaiseUndoStateChanged();

                ApplyCurrentFilterToView(_allRows);

                // ✅ Preview’da DB yok → UI-side karşı hesap hesaplanır.
                await EnsureKarsiHesapForVisibleIfMissingAsync(forceComputeEvenIfSomePresent: false);
                // ✅ PREVIEW SNAPSHOT CACHE (IMPORT GARANTİ)
                CacheCurrentPreviewSnapshot();

                StatusText = $"Önizleme tamam — {parsedCount} satır. Görünen {Rows.Count} satır.";

                // ✅ kritik: Preview sonunda VM/state snapshot
                Logger.WriteLine($"[PREVIEW] vm_hash={this.GetHashCode()} allRows.count={_allRows.Count} selectedInputs.count={_selectedInputs.Count} visibleRows.count={Rows.Count}");
                Logger.WriteLine($"[PREVIEW] done. visibleRows={Rows.Count}");
            }
            catch (Exception ex)
            {
                StatusText = "Hata: " + ex.Message;
                Logger.WriteLine("[PREVIEW ERROR] " + ex);
            }
            finally
            {
                try { Logger.Close(); } catch { }
                IsBusy = false;
                CleanupTempDirs(tempDirs);

                if (!string.IsNullOrWhiteSpace(previewLogPath))
                    StatusText = (StatusText ?? "") + $" (Log: {previewLogPath})";
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
            await RefreshViewAsync(
                baseSource: _allRows,
                keepFisFocus: true,
                recomputeContra: (_dataOrigin == DataOrigin.Preview));

            StatusText = IsFisFocusActive
                ? $"Fiş odağı açık ({_focusedFisNo}) — filtre uygulandı. Görünen {Rows.Count} satır."
                : $"Filtre uygulandı. Görünen {Rows.Count} satır.";
        }

        [RelayCommand]
        private async Task ResetFilters()
        {
            Filters.Reset();
            Filters.Aciklama = null;

            await RefreshViewAsync(
                baseSource: _allRows,
                keepFisFocus: true,
                recomputeContra: (_dataOrigin == DataOrigin.Preview));

            StatusText = IsFisFocusActive
                ? $"Fiş odağı açık ({_focusedFisNo}) — filtreler sıfırlandı. Görünen {Rows.Count} satır."
                : "Filtreler sıfırlandı.";
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

            ClearFisFocusCore(resetStatus: false);

            _selectedInputs?.Clear();

            _allRows?.Clear();
            Rows?.Clear();
            ClearPreviewCache();


            Filters?.Reset();
            Totals?.Reset();
            UpdateTotalsText();

            _dirtyFisTypeGroupKeys?.Clear();
            _lastUndo = null;
            RaiseUndoStateChanged();

            HasUndoableImport = false;
            _dataOrigin = DataOrigin.None;

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

        // ================ ✅ Karşı Hesap (PREVIEW ONLY) ==========================
        // DB'de KarsiHesap, repo tarafında hesaplanır. UI-side hesaplama SADECE Preview'da kullanılır.
        private async Task EnsureKarsiHesapForVisibleIfMissingAsync(bool forceComputeEvenIfSomePresent = false)
        {
            if (_dataOrigin != DataOrigin.Preview)
                return;

            if (Rows.Count == 0) return;

            var visible = Rows.ToList();
            bool anyMissing = visible.Any(r => string.IsNullOrWhiteSpace(r.KarsiHesap));

            if (!anyMissing && !forceComputeEvenIfSomePresent)
                return;

            var all = _allRows.Count > 0 ? _allRows : visible;

            var result = await Task.Run(() =>
            {
                // 1) group_key bazında deb/crd setleri (TÜM satırlar üzerinden)
                var map = new Dictionary<string, (HashSet<string> deb, HashSet<string> crd)>(StringComparer.Ordinal);

                foreach (var g in all.GroupBy(r => KeyFor(r)))
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

                // 2) sadece visible için string üret
                var karsiByRow = new Dictionary<MuavinRow, string>(ReferenceEqualityComparer<MuavinRow>.Instance);

                foreach (var r in visible)
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

            // 3) UI satırlarına bas (SADECE boş olanlara)
            foreach (var r in visible)
            {
                if (!string.IsNullOrWhiteSpace(r.KarsiHesap) && !forceComputeEvenIfSomePresent)
                    continue;

                if (result.TryGetValue(r, out var val))
                    r.KarsiHesap = val;
            }
        }

        // ✅ Tek doğru anahtar: GroupKey varsa onu kullan.
        private static string KeyFor(MuavinRow r)
        {
            var gk = (r.GroupKey ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(gk))
                return gk;

            return BuildGroupKeyFallback(r);
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
                var wanted = Filters.HesapKodu
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                q = q.Where(r => r.HesapKodu != null && wanted.Contains(r.HesapKodu));
            }

            if (!string.IsNullOrWhiteSpace(Filters.Aciklama))
            {
                var term = Filters.Aciklama.Trim();
                q = q.Where(r => (r.Aciklama ?? "").Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            if (Filters.ExcludeAcilis)
                q = q.Where(r => !string.Equals((r.FisTuru ?? "").Trim(), "Açılış", StringComparison.OrdinalIgnoreCase));

            if (Filters.ExcludeKapanis)
                q = q.Where(r => !string.Equals((r.FisTuru ?? "").Trim(), "Kapanış", StringComparison.OrdinalIgnoreCase));

            if (Filters.HasQuickFilter)
            {
                var col = NormalizeQuickFilterField(Filters.QuickColumn ?? "");
                var val = (Filters.QuickValue ?? "").Trim();
                var op = Filters.QuickOp;

                if (!string.IsNullOrWhiteSpace(col) && !string.IsNullOrWhiteSpace(val))
                {
                    static string S(string? x) => (x ?? "").Trim();

                    bool Match(string? cellRaw)
                    {
                        var cell = S(cellRaw);

                        return op switch
                        {
                            QuickFilterOp.Equals => string.Equals(cell, val, StringComparison.OrdinalIgnoreCase),
                            QuickFilterOp.NotEquals => !string.Equals(cell, val, StringComparison.OrdinalIgnoreCase),
                            QuickFilterOp.Contains => cell.Contains(val, StringComparison.OrdinalIgnoreCase),
                            QuickFilterOp.NotContains => !cell.Contains(val, StringComparison.OrdinalIgnoreCase),
                            _ => true
                        };
                    }

                    q = q.Where(r =>
                    {
                        var cellText = GetCellTextForQuickFilter(r, col);
                        return Match(cellText);
                    });
                }
            }

            return q.ToList();
        }

        private async Task RefreshViewAsync(
            IEnumerable<MuavinRow>? baseSource = null,
            bool keepFisFocus = false,
            bool recomputeContra = true,
            string? status = null)
        {
            IEnumerable<MuavinRow> src = baseSource ?? _allRows;

            if (keepFisFocus && IsFisFocusActive && !string.IsNullOrWhiteSpace(_focusedFisNo))
            {
                var fno = _focusedFisNo.Trim();
                src = src.Where(r => string.Equals((r.EntryNumber ?? "").Trim(), fno, StringComparison.Ordinal));
            }

            var filtered = ApplyFilterLogic(src);
            ApplyCurrentFilterToView(filtered);

            if (recomputeContra && _dataOrigin == DataOrigin.Preview)
                await EnsureKarsiHesapForVisibleIfMissingAsync();

            if (!string.IsNullOrWhiteSpace(status))
                StatusText = status;
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

        public async void ToggleFocusByFisNo(string entryNo)
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

                await RefreshViewAsync(baseSource: _allRows, keepFisFocus: true, recomputeContra: (_dataOrigin == DataOrigin.Preview));
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

            await RefreshViewAsync(baseSource: _allRows, keepFisFocus: true, recomputeContra: (_dataOrigin == DataOrigin.Preview));
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

            var filtered = ApplyFilterLogic(_allRows);
            ApplyCurrentFilterToView(filtered);

            // ✅ DB modunda UI-side karşı hesap hesaplanmaz.
            if (_dataOrigin == DataOrigin.Preview)
                _ = EnsureKarsiHesapForVisibleIfMissingAsync();

            RestoreSortSnapshot(_sortBeforeFisFocus);
            _userChangedSort = _userChangedSortBeforeFisFocus;

            _rowsBeforeFisFocus = null;
            _sortBeforeFisFocus = null;
            _userChangedSortBeforeFisFocus = false;

            RaiseFisFocusUiChanged();

            if (resetStatus)
                StatusText = "Fiş odağı kapatıldı.";
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

            // ✅ Tek doğru anahtar: GroupKey (yoksa fallback üret)
            var key = KeyFor(row);

            var targets = _allRows
                .Where(r => string.Equals(KeyFor(r), key, StringComparison.Ordinal))
                .ToList();

            if (targets.Count == 0)
            {
                StatusText = "Uygulanacak satır bulunamadı.";
                return;
            }

            // DB load'da GroupKey zaten var; preview'da yoksa üret.
            foreach (var r in targets)
                if (string.IsNullOrWhiteSpace(r.GroupKey))
                    r.GroupKey = BuildGroupKeyFallback(r);

            var alreadySame = targets.All(r => string.Equals((r.FisTuru ?? "").Trim(), ft, StringComparison.OrdinalIgnoreCase));
            if (alreadySame) return;

            ApplyFisTuruToTargetsCore(entryNo, targets, ft);
        }

        private string _uiStatusMessage = "";
        public string UiStatusMessage
        {
            get => _uiStatusMessage;
            set { _uiStatusMessage = value; OnPropertyChanged(); }
        }

        private void ComputeFisBalanceFlags(IList<MuavinRow> rows)
        {
            if (rows == null || rows.Count == 0) return;

            foreach (var r in rows)
            {
                r.FisTotalBorc = 0m;
                r.FisTotalAlacak = 0m;
                r.FisDiff = 0m;
                r.IsFisImbalanced = false;
            }

            foreach (var g in rows.GroupBy(r => KeyFor(r)))
            {
                decimal tb = 0m, ta = 0m;

                foreach (var r in g)
                {
                    tb += r.Borc;
                    ta += r.Alacak;
                }

                var diff = tb - ta;
                var imbalanced = diff != 0m;

                foreach (var r in g)
                {
                    r.FisTotalBorc = tb;
                    r.FisTotalAlacak = ta;
                    r.FisDiff = diff;
                    r.IsFisImbalanced = imbalanced;
                }
            }
        }

        public static bool IsSpecialClosingType(string? fisTuru)
        {
            var ft = (fisTuru ?? "").Trim();

            return ft.Equals("Açılış", StringComparison.OrdinalIgnoreCase)
                || ft.Equals("Kapanış", StringComparison.OrdinalIgnoreCase)
                || ft.Equals("Gelir Tablosu Kapanış", StringComparison.OrdinalIgnoreCase)
                || ft.Equals("Yansıtma Kapama", StringComparison.OrdinalIgnoreCase);
        }

        public async Task ApplyQuickFilterFromUiAsync(string field, string value, string mode)
        {
            field = (field ?? "").Trim();
            value = (value ?? "").Trim();
            mode = (mode ?? "").Trim();

            if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value))
                return;

            field = NormalizeQuickFilterField(field);

            Filters.QuickColumn = field;
            Filters.QuickValue = value;

            Filters.QuickOp = mode switch
            {
                "Equals" => QuickFilterOp.Equals,
                "NotEquals" => QuickFilterOp.NotEquals,
                "Contains" => QuickFilterOp.Contains,
                "NotContains" => QuickFilterOp.NotContains,
                _ => QuickFilterOp.Equals
            };

            await ApplyFilters();
        }

        // ================== ✅ GroupKey üretimi (UI tarafı, DB ile uyumlu) ==================
        private static string NormalizeSourceFileForUi(string? sourceFile)
        {
            if (string.IsNullOrWhiteSpace(sourceFile)) return "_unknown_";
            try { return Path.GetFileName(sourceFile.Trim()); }
            catch { return sourceFile.Trim(); }
        }

        private static bool IsTxtLikeSource(string? normalizedSourceFile)
        {
            var s = (normalizedSourceFile ?? "").Trim();
            if (s.Length == 0) return false;

            var ext = Path.GetExtension(s);
            return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase);
        }

        // Preview/import öncesi üretimde: txt/csv ise DOC ekleme
        private static string BuildGroupKeyForUi(MuavinRow r, string normalizedSourceFile)
        {
            var d = r.PostingDate?.ToString("yyyy-MM-dd") ?? "";
            var no = r.EntryNumber ?? "";

            if (IsSpecialClosingType(r.FisTuru))
                return $"{no}|{d}";

            if (IsTxtLikeSource(normalizedSourceFile))
                return $"{no}|{d}";

            var doc = r.DocumentNumber ?? "";
            return string.IsNullOrWhiteSpace(doc) ? $"{no}|{d}" : $"{no}|{d}|DOC:{doc}";
        }

        [RelayCommand]
        private void OpenLogsFolder()
        {
            LogPaths.OpenLogFolderInExplorer();
            StatusText = "Log klasörü açıldı.";
        }

        // Source bilinmiyorsa: eski davranış (fallback)
        private static string BuildGroupKeyFallback(MuavinRow r)
        {
            var d = r.PostingDate?.ToString("yyyy-MM-dd") ?? "";
            var no = r.EntryNumber ?? "";
            if (IsSpecialClosingType(r.FisTuru)) return $"{no}|{d}";
            var doc = r.DocumentNumber ?? "";
            return string.IsNullOrWhiteSpace(doc) ? $"{no}|{d}" : $"{no}|{d}|DOC:{doc}";
        }

        
    }

    internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}