using System.Globalization;
using System.Windows.Data;

namespace Shredder.App.Converters;

/// <summary>
/// IsDirectory 布尔值 -> "目录" / "文件" 文案。
/// </summary>
public sealed class BoolToDirTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? "目录" : "文件";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
