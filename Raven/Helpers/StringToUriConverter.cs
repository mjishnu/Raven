using Microsoft.UI.Xaml.Data;

namespace Raven.Helpers;

/// <summary>
/// Converts a URL string to an absolute <see cref="Uri"/> for binding to
/// <see cref="Microsoft.UI.Xaml.Media.Imaging.BitmapImage.UriSource"/>.
/// Returns null for null/empty/relative inputs. One-way only.
/// </summary>
public sealed class StringToUriConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string s
            && !string.IsNullOrWhiteSpace(s)
            && Uri.TryCreate(s, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
