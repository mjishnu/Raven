using Microsoft.UI.Xaml.Data;

namespace Raven.Helpers;

public partial class OneDecimalPlaceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double doubleValue)
        {
            return doubleValue.ToString("F1"); // Format with one decimal place
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is string strValue && double.TryParse(strValue, out double result))
        {
            return result;
        }
        return 0.0;
    }
}
