// Muavin.Desktop/MainWindow.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Muavin.Desktop.ViewModels;

namespace Muavin.Desktop
{
    public partial class MainWindow : Window
    {
        public MainWindow() => InitializeComponent();

        // Satıra çift tık → Detay penceresi
        private void RowsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (vm.OpenEntryDetailsCommand.CanExecute(null))
                vm.OpenEntryDetailsCommand.Execute(null);
        }

        // (Opsiyonel) Kolon başlığına double-click ile sıralama istersen:
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
    }
}
