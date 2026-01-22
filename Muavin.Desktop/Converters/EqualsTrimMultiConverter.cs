using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Globalization;

namespace Muavin.Desktop.Converters
{
    public sealed class EqualsTrimMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var a = (values.ElementAtOrDefault(0)?.ToString() ?? "").Trim();
            var b = (values.ElementAtOrDefault(1)?.ToString() ?? "").Trim();

            return string.Equals(a, b, StringComparison.Ordinal);
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
