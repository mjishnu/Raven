using Microsoft.UI.Xaml.Data;

namespace Raven.Helpers;

public sealed partial class ProgressToPercentTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double d)
            return $"{d:0}%";
        if (value is float f)
            return $"{f:0}%";

        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
