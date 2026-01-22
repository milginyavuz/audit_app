// Muavin.Desktop/MainWindow.xaml.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ClosedXML.Excel;
using Muavin.Desktop.ViewModels;
using Muavin.Xml.Parsing;

namespace Muavin.Desktop
{
    public partial class MainWindow : Window
    {
        // ComboBox init sırasında tetiklenmesin diye küçük koruma
        private bool _fisTuruChangeGuard;

        public MainWindow()
        {
            InitializeComponent();
        }

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

            // Sadece "Fiş Türü" edit edilebilir
            if (!header.Equals("Fiş Türü", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                return;
            }

            // Edit’e girince guard aç
            _fisTuruChangeGuard = true;

            // Bir tick sonra kapat (WPF edit template kurulumunda SelectionChanged tetiklenebiliyor)
            Dispatcher.BeginInvoke(new Action(() => _fisTuruChangeGuard = false));
        }

        // ✅ Fiş Numarası hücresine tıklayınca: aynı fiş no focus (toggle)
        private void RowsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Sadece "Fiş Numarası" hücresine tıklanınca çalışsın
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

        // ✅ ComboBox seçim değişince: VM prompt'lu akış (kaç satır etkilenecek + kaydetme sor)
        private async void FisTuruCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fisTuruChangeGuard) return;

            if (e.AddedItems == null || e.AddedItems.Count == 0) return;
            if (sender is not ComboBox cb) return;
            if (cb.DataContext is not MuavinRow row) return;

            // Kullanıcı gerçekten değiştirdiyse devam (init tetiklerini ele)
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

                // ✅ Tek kaynak: prompt’lu metot
                await vm.SetFisTuruForSelectedFisWithPromptFromUiAsync(newType);

                // UI senkron (iptal/undo olursa geri döner)
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

                // Header
                for (int i = 0; i < cols.Count; i++)
                    ws.Cell(r, i + 1).Value = cols[i].Header?.ToString() ?? "";

                var header = ws.Range(r, 1, r, cols.Count);
                header.Style.Font.Bold = true;
                header.Style.Fill.BackgroundColor = XLColor.LightGray;

                // Rows
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
            // 1) Bound column
            if (col is DataGridBoundColumn bc && bc.Binding is System.Windows.Data.Binding b)
            {
                var path = b.Path?.Path;
                if (!string.IsNullOrWhiteSpace(path))
                    return GetPropValue(item, path);
            }

            // 2) Template column vb. -> SortMemberPath ile çöz (Fiş Türü burada yakalanır)
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

        


    }
}
