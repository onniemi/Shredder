using System.Globalization;
using System.Windows.Data;

namespace Shredder.App.Converters;

/// <summary>
/// 反转布尔值。常用于 IsEnabled 与 IsBusy 反向绑定。
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
