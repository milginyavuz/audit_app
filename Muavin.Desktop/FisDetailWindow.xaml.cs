using System;
using System.Collections.Generic;
using System.Windows;
using Muavin.Desktop.ViewModels;
using Muavin.Xml.Parsing;

namespace Muavin.Desktop
{
    public partial class FisDetailWindow : Window
    {
        public FisDetailWindow(
            IEnumerable<MuavinRow> sourceRows,
            string entryNumber,
            DateTime postingDate)
        {
            InitializeComponent();

            DataContext = new FisDetailViewModel(
                sourceRows,
                entryNumber,
                postingDate);
        }
    }
}
