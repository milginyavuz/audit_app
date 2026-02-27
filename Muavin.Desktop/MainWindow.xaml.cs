// Muavin.Desktop/MainWindow.xaml.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ClosedXML.Excel;
using Muavin.Desktop.ViewModels;
using Muavin.Xml.Parsing;
using CommunityToolkit.Mvvm.Input;

namespace Muavin.Desktop
{
    public partial class MainWindow : Window
    {
        // ComboBox init sırasında tetiklenmesin diye küçük koruma
        private bool _fisTuruChangeGuard;
        private System.Threading.CancellationTokenSource? _clipboardStatusCts;

        private static readonly CultureInfo TR = CultureInfo.GetCultureInfo("tr-TR");

        // ===== QUICK FILTER state (sağ tık anında yakalanır) =====
        private DataGridColumn? _quickFilterColumn;
        private string? _quickFilterColumnKey;    // property path (örn: Borc, PostingDate, HesapAdi...)
        private string? _quickFilterCellValue;    // ekranda görünen string (FormatValue ile)

        public MainWindow()
        {
            InitializeComponent();

            // ✅ Ctrl+C: seçili satırları panoya kopyala
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyExecuted, OnCopyCanExecute));
        }

        // =========================
        // ✅ QUICK FILTER MENU CLICK HANDLERS
        // =========================
        private async void QuickFilter_Equals_Click(object sender, RoutedEventArgs e)
            => await ApplyQuickFilterAsync(QuickFilterOp.Equals);

        private async void QuickFilter_NotEquals_Click(object sender, RoutedEventArgs e)
            => await ApplyQuickFilterAsync(QuickFilterOp.NotEquals);

        private async void QuickFilter_Contains_Click(object sender, RoutedEventArgs e)
            => await ApplyQuickFilterAsync(QuickFilterOp.Contains);

        private async void QuickFilter_NotContains_Click(object sender, RoutedEventArgs e)
            => await ApplyQuickFilterAsync(QuickFilterOp.NotContains);

        private async System.Threading.Tasks.Task ApplyQuickFilterAsync(QuickFilterOp op)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var field = (_quickFilterColumnKey ?? "").Trim();
            var value = (_quickFilterCellValue ?? "").Trim();

            if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value))
                return;

            // ✅ KURAL: QuickFilter uygulanırsa fiş odağı kapanır
            if (vm.IsFisFocusActive)
                vm.ClearFisFocusCommand.Execute(null);

            vm.Filters.QuickColumn = field;
            vm.Filters.QuickValue = value;
            vm.Filters.QuickOp = op;

            // ✅ Filtreyi uygula
            await ExecuteCommandAsync(vm.ApplyFiltersCommand, null);
        }

        private static async System.Threading.Tasks.Task ExecuteCommandAsync(ICommand? cmd, object? param = null)
        {
            if (cmd == null) return;

            if (cmd is IAsyncRelayCommand asyncCmd)
            {
                if (asyncCmd.CanExecute(param))
                    await asyncCmd.ExecuteAsync(param);
                return;
            }

            if (cmd.CanExecute(param))
                cmd.Execute(param);

            await System.Threading.Tasks.Task.CompletedTask;
        }

        // =========================
        // ✅ DOUBLE CLICK -> Entry Details
        // =========================
        private void RowsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.OpenEntryDetailsCommand.Execute(null);
        }

        // Kolon başlığı double-click -> sort toggle
        protected override void OnPreviewMouseDoubleClick(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDoubleClick(e);

            if (e.OriginalSource is DependencyObject d)
            {
                var header = FindVisualAncestor<DataGridColumnHeader>(d);
                if (header?.Column?.SortMemberPath is string path && DataContext is MainViewModel vm)
                {
                    vm.ToggleSort(path);
                    e.Handled = true;
                }
            }
        }

        // ✅ TEK helper: Visual Tree'de yukarı çık
        private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t) return t;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ✅ Sadece Fiş Türü edit edilsin: diğer edit girişlerini iptal et
        private void RowsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            var header = (e.Column?.Header?.ToString() ?? "").Trim();

            if (!header.Equals("Fiş Türü", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                return;
            }

            _fisTuruChangeGuard = true;
            Dispatcher.BeginInvoke(new Action(() => _fisTuruChangeGuard = false));
        }

        // ✅ Fiş Numarası hücresine tıklayınca: aynı fiş no focus (toggle)
        private void RowsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var cell = FindVisualAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell == null) return;

            var col = cell.Column;
            if (col == null) return;

            var headerText = (col.Header?.ToString() ?? "").Trim();
            var sortPath = (col.SortMemberPath ?? "").Trim();

            bool isFisNoColumn =
                headerText.Equals("Fiş Numarası", StringComparison.OrdinalIgnoreCase) ||
                sortPath.Equals("EntryNumber", StringComparison.OrdinalIgnoreCase);

            if (!isFisNoColumn) return;

            if (cell.DataContext is not MuavinRow row) return;

            var entryNo = (row.EntryNumber ?? "").Trim();
            if (string.IsNullOrWhiteSpace(entryNo)) return;

            if (DataContext is MainViewModel vm)
            {
                vm.ToggleFocusByFisNo(entryNo);
                e.Handled = true;
            }
        }

        private void RowsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        // Sağ tıkla tıklanan satırı seç (ContextMenu doğru çalışsın)
        private void RowsGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row)
            {
                row.IsSelected = true;
                row.Focus();
                e.Handled = false;
            }
        }

        // ✅ ComboBox seçim değişince: VM prompt'lu akış
        private async void FisTuruCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fisTuruChangeGuard) return;

            if (e.AddedItems == null || e.AddedItems.Count == 0) return;
            if (sender is not ComboBox cb) return;
            if (cb.DataContext is not MuavinRow row) return;

            // Kullanıcı gerçekten değiştirdiyse devam
            if (!cb.IsDropDownOpen && !cb.IsKeyboardFocusWithin) return;

            if (DataContext is not MainViewModel vm) return;

            var newType = (e.AddedItems[0]?.ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(newType)) return;

            var current = (row.FisTuru ?? "").Trim();
            if (string.Equals(current, newType, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                _fisTuruChangeGuard = true;

                vm.SelectedRow = row;
                await vm.SetFisTuruForSelectedFisWithPromptFromUiAsync(newType);

                cb.SelectedItem = row.FisTuru;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fiş türü değişikliği sırasında hata:\n" + ex.Message,
                    "Fiş Türü",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                cb.SelectedItem = row.FisTuru;
            }
            finally
            {
                _ = Dispatcher.BeginInvoke(new Action(() => _fisTuruChangeGuard = false));
            }
        }

        private void MizanButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = RowsGrid.ItemsSource as IEnumerable<MuavinRow>;
            if (rows == null)
            {
                MessageBox.Show("Önce muavin verilerini yükleyin.", "Mizan", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var list = rows.ToList();
            if (!list.Any())
            {
                MessageBox.Show("Muavin listesinde satır yok.", "Mizan", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new MizanWindow(list) { Owner = this };
            win.Show();
        }

        // =========================
        // ✅ EXCEL EXPORT (senin kodun aynen)
        // =========================
        private void ExportExcel_Click(object sender, RoutedEventArgs e)
        {
            if (RowsGrid == null || RowsGrid.ItemsSource == null)
            {
                MessageBox.Show("Aktarılacak muavin tablosu bulunamadı.", "Excel'e Aktar",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var items = new List<object>();
            foreach (var x in (IEnumerable)RowsGrid.ItemsSource)
                if (x != null) items.Add(x);

            if (items.Count == 0)
            {
                MessageBox.Show("Aktarılacak satır bulunamadı.", "Excel'e Aktar",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Muavini Excel'e Aktar",
                Filter = "Excel Çalışma Kitabı|*.xlsx",
                FileName = $"Muavin_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                using var wb = new XLWorkbook();
                var ws = wb.Worksheets.Add("Muavin");

                var cols = RowsGrid.Columns
                    .Where(c => c.Visibility == Visibility.Visible)
                    .OrderBy(c => c.DisplayIndex)
                    .ToList();

                if (cols.Count == 0)
                {
                    MessageBox.Show("Aktarılacak kolon bulunamadı.", "Excel'e Aktar",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int r = 1;

                for (int i = 0; i < cols.Count; i++)
                    ws.Cell(r, i + 1).Value = cols[i].Header?.ToString() ?? "";

                var header = ws.Range(r, 1, r, cols.Count);
                header.Style.Font.Bold = true;
                header.Style.Fill.BackgroundColor = XLColor.LightGray;

                r++;
                foreach (var item in items)
                {
                    for (int c = 0; c < cols.Count; c++)
                    {
                        var value = ReadBoundOrSortValue(cols[c], item);
                        var cell = ws.Cell(r, c + 1);

                        if (value == null) cell.Value = "";
                        else if (value is DateTime dt)
                        {
                            cell.Value = dt;
                            cell.Style.DateFormat.Format = "dd.MM.yyyy";
                        }
                        else if (value is decimal dec)
                        {
                            cell.Value = dec;
                            cell.Style.NumberFormat.Format = "#,##0.00";
                            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        }
                        else if (value is double dbl)
                        {
                            cell.Value = dbl;
                            cell.Style.NumberFormat.Format = "#,##0.00";
                            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        }
                        else if (value is float flt)
                        {
                            cell.Value = (double)flt;
                            cell.Style.NumberFormat.Format = "#,##0.00";
                            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        }
                        else if (value is int i32)
                        {
                            cell.Value = i32;
                            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        }
                        else if (value is long i64)
                        {
                            cell.Value = i64;
                            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        }
                        else if (value is short i16)
                        {
                            cell.Value = (int)i16;
                            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        }
                        else if (value is bool b)
                        {
                            cell.Value = b ? "Evet" : "Hayır";
                        }
                        else cell.Value = value.ToString() ?? "";
                    }
                    r++;
                }

                ws.Columns().AdjustToContents();
                ws.SheetView.FreezeRows(1);
                ws.Range(1, 1, 1, cols.Count).SetAutoFilter();

                wb.SaveAs(dlg.FileName);

                var ask = MessageBox.Show(
                    "Muavin Excel dosyası kaydedildi.\nŞimdi açmak ister misiniz?",
                    "Excel'e Aktar",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (ask == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Excel'e aktarılırken hata oluştu:\n" + ex.Message,
                                "Excel'e Aktar",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }

        // ✅ BoundColumn ise Binding.Path, değilse SortMemberPath üzerinden property oku
        private static object? ReadBoundOrSortValue(DataGridColumn col, object item)
        {
            if (col is DataGridBoundColumn bc && bc.Binding is System.Windows.Data.Binding b)
            {
                var path = b.Path?.Path;
                if (!string.IsNullOrWhiteSpace(path))
                    return GetPropValue(item, path);
            }

            var sortPath = (col.SortMemberPath ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sortPath))
                return GetPropValue(item, sortPath);

            return null;
        }

        private static object? GetPropValue(object obj, string path)
        {
            object? current = obj;
            foreach (var part in path.Split('.'))
            {
                if (current == null) return null;
                var t = current.GetType();
                var p = t.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) return null;
                current = p.GetValue(current);
            }
            return current;
        }

        // =========================
        // ✅ PAN0YA KOPYALAMA (senin kodun aynen)
        // =========================
        private void OnCopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            if (RowsGrid == null)
            {
                e.CanExecute = false;
                return;
            }

            var hasAny =
                (RowsGrid.SelectedItems != null && RowsGrid.SelectedItems.Count > 0) ||
                (RowsGrid.SelectedItem != null);

            e.CanExecute = hasAny;
            e.Handled = true;
        }

        private void OnCopyExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            CopySelectedRowsToClipboard(includeHeader: true);
            e.Handled = true;
        }

        private void CopySelectedRows_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedRowsToClipboard(includeHeader: true);
        }

        private void CopySelectedRowsToClipboard(bool includeHeader)
        {
            if (RowsGrid == null) return;

            var items = new List<object>();

            if (RowsGrid.SelectedItems != null && RowsGrid.SelectedItems.Count > 0)
            {
                foreach (var it in RowsGrid.SelectedItems)
                    if (it != null) items.Add(it);
            }
            else if (RowsGrid.SelectedItem != null)
            {
                items.Add(RowsGrid.SelectedItem);
            }

            if (items.Count == 0) return;

            var cols = RowsGrid.Columns
                .Where(c => c.Visibility == Visibility.Visible)
                .OrderBy(c => c.DisplayIndex)
                .ToList();

            if (cols.Count == 0) return;

            var sb = new StringBuilder(1024);

            if (includeHeader)
            {
                sb.Append(string.Join("\t", cols.Select(c => SanitizeCellText(c.Header?.ToString() ?? ""))));
                sb.AppendLine();
            }

            foreach (var item in items)
            {
                var line = new List<string>(cols.Count);

                foreach (var col in cols)
                {
                    var value = ReadBoundOrSortValue(col, item);
                    line.Add(SanitizeCellText(FormatValue(value)));
                }

                sb.Append(string.Join("\t", line));
                sb.AppendLine();
            }

            var text = sb.ToString();
            if (string.IsNullOrWhiteSpace(text)) return;

            Clipboard.SetText(text);

            if (DataContext is MainViewModel vm)
            {
                vm.UiStatusMessage = $"Panoya kopyalandı: {items.Count} satır";

                _clipboardStatusCts?.Cancel();
                _clipboardStatusCts?.Dispose();
                _clipboardStatusCts = new System.Threading.CancellationTokenSource();
                var token = _clipboardStatusCts.Token;

                _ = Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(3000, token);

                        if (!token.IsCancellationRequested &&
                            DataContext is MainViewModel vm2 &&
                            (vm2.UiStatusMessage?.StartsWith("Panoya kopyalandı") ?? false))
                        {
                            vm2.UiStatusMessage = "";
                        }
                    }
                    catch (System.Threading.Tasks.TaskCanceledException)
                    {
                    }
                }));
            }
        }

        private static string FormatValue(object? value)
        {
            if (value == null) return "";

            if (value is DateTime dt)
                return dt.ToString("dd.MM.yyyy", TR);

            if (value is DateTimeOffset dto)
                return dto.ToString("dd.MM.yyyy", TR);

            if (value is decimal dec)
                return dec.ToString("N2", TR);

            if (value is double dbl)
                return dbl.ToString("N2", TR);

            if (value is float flt)
                return ((double)flt).ToString("N2", TR);

            if (value is int or long or short)
                return Convert.ToString(value, TR) ?? "";

            if (value is bool b)
                return b ? "Evet" : "Hayır";

            return value.ToString() ?? "";
        }

        private static string SanitizeCellText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            s = s.Replace("\t", " ");
            s = s.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            return s.Trim();
        }

        private void RowsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                CopySelectedRowsToClipboard(includeHeader: true);
                e.Handled = true;
            }
        }

        // =========================
        // ✅ QUICK FILTER: ContextMenu opened -> kolon+değer yakala
        // =========================
        

        private static string GetColumnKey(DataGridColumn col)
        {
            // ✅ En sağlam: SortMemberPath
            var sortPath = (col.SortMemberPath ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sortPath))
                return sortPath;

            // Bound column binding path
            if (col is DataGridBoundColumn bc && bc.Binding is System.Windows.Data.Binding b)
            {
                var path = (b.Path?.Path ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }

            return (col.Header?.ToString() ?? "").Trim();
        }



        private void RowsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                _quickFilterColumn = null;
                _quickFilterColumnKey = null;
                _quickFilterCellValue = null;

                if (RowsGrid == null) return;

                var cm = RowsGrid.ContextMenu;
                if (cm == null) return;

                var miHeader = cm.FindName("MiQuickFilterHeader") as MenuItem;
                var miRoot = cm.FindName("MiQuickFilterRoot") as MenuItem;

                if (miHeader != null) miHeader.Header = "Hızlı Filtre";
                if (miRoot != null) miRoot.IsEnabled = false;

                // ✅ Mouse altındaki gerçek hücreyi bul (PlacementTarget/DirectlyOver yerine)
                var pos = Mouse.GetPosition(RowsGrid);
                var hit = RowsGrid.InputHitTest(pos) as DependencyObject;
                if (hit == null) return;

                var cell = FindVisualAncestor<DataGridCell>(hit);
                if (cell?.Column == null) return;

                _quickFilterColumn = cell.Column;

                var rowItem = cell.DataContext;
                if (rowItem == null) return;

                _quickFilterColumnKey = GetColumnKey(_quickFilterColumn);

                var vObj = ReadBoundOrSortValue(_quickFilterColumn, rowItem);
                _quickFilterCellValue = SanitizeCellText(FormatValue(vObj));

                if (string.IsNullOrWhiteSpace(_quickFilterColumnKey) ||
                    string.IsNullOrWhiteSpace(_quickFilterCellValue))
                {
                    if (miHeader != null) miHeader.Header = "Hızlı Filtre (boş hücre)";
                    if (miRoot != null) miRoot.IsEnabled = false;
                    return;
                }

                var colTitle = (_quickFilterColumn.Header?.ToString() ?? _quickFilterColumnKey).Trim();
                if (miHeader != null)
                    miHeader.Header = $"Hızlı Filtre: {colTitle} → \"{_quickFilterCellValue}\"";

                if (miRoot != null) miRoot.IsEnabled = true;
            }
            catch
            {
                // sessiz geç
            }
        }



    }
}
