// Muavin.Desktop/EntryDetailsWindow.xaml.cs
using Muavin.Xml.Parsing;
using System.Collections.Generic;
using System.Windows;

namespace Muavin.Desktop
{
    public partial class EntryDetailsWindow : Window
    {
        public EntryDetailsWindow(List<MuavinRow> rows)
        {
            InitializeComponent();
            DataContext = rows;
        }
    }
}
