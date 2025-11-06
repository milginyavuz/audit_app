using Muavin.Desktop.ViewModels;
using Muavin.Xml.Parsing;
using System;
using System.Collections.Generic;
using System.Windows;

namespace Muavin.Desktop
{
    public partial class AgingWindow : Window
    {
        public AgingWindow(IEnumerable<MuavinRow> rows, DateTime? asOfDate = null)
        {
            InitializeComponent();
            DataContext = new AgingViewModel(rows, asOfDate);
        }
    }
}
