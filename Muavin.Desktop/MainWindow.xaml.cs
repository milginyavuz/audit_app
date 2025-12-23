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
using Microsoft.Win32;
using ClosedXML.Excel;
using Muavin.Desktop.ViewModels;
using Muavin.Xml.Parsing;

namespace Muavin.Desktop
{
    public partial class MainWindow : Window
    {
        public MainWindow() => InitializeComponent();

        private void RowsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.OpenEntryDetailsCommand.Execute(null);
        }

        protected override void OnPreviewMouseDoubleClick(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDoubleClick(e);
            if (e.OriginalSource is DependencyObject d)
            {
                var header = FindAncestor<DataGridColumnHeader>(d);
                if (header?.Column?.SortMemberPath is string path && DataContext is MainViewModel vm)
                {
                    vm.ToggleSort(path);
                    e.Handled = true;
                }
            }
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t) return t;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void RowsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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

            var win = new MizanWindow(list)
            {
                Owner = this
            };
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
                        var value = ReadBoundValue(cols[c], item);
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

        private static object? ReadBoundValue(DataGridColumn col, object item)
        {
            if (col is DataGridBoundColumn bc && bc.Binding is System.Windows.Data.Binding b)
            {
                var path = b.Path?.Path;
                if (!string.IsNullOrWhiteSpace(path))
                    return GetPropValue(item, path);
            }
            return item;
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
