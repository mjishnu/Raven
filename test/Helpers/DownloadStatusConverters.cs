using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using test.Models;

namespace test.Helpers;

public class DownloadStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            // Show progress bar only for downloading/pending states
            return status == DownloadStatus.Downloading || status == DownloadStatus.Pending
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class CompletedStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            return status == DownloadStatus.Completed
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Shows the menu button for non-downloading states (Completed, Failed, Cancelled)
/// </summary>
public class NotDownloadingStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            return status != DownloadStatus.Downloading && status != DownloadStatus.Pending
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
