using System.Globalization;
using System.Windows.Data;

namespace Shredder.App.Converters;

/// <summary>
/// 把 null 或空字符串转成 false，否则 true。常用于按钮的 IsEnabled 绑定。
/// </summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            null => false,
            string s => !string.IsNullOrEmpty(s),
            _ => true,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
