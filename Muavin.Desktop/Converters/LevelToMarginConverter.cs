using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Muavin.Desktop.Converters
{
    /// <summary>
    /// MizanRow.Level (0,1,2...) değerini sola margin (Thickness) çevirir.
    /// </summary>
    public class LevelToMarginConverter : IValueConverter
    {
        /// <summary>
        /// Her seviye için kaç piksel içerden başlasın?
        /// XAML'de  IndentSize="20"  diye ayarlıyoruz.
        /// </summary>
        public double IndentSize { get; set; } = 20.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int level = 0;

            if (value is int i)
                level = i;
            else if (value != null && int.TryParse(value.ToString(), out var parsed))
                level = parsed;

            double left = level * IndentSize;
            return new Thickness(left, 0, 0, 0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Geri dönüşe ihtiyaç yok, binding tek yönlü.
            return Binding.DoNothing;
        }
    }
}
