using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Raven.Models;

namespace Raven.Helpers;

public partial class DownloadStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            // Show progress UI for active states
            return status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Installing or DownloadStatus.Cancelling
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

public sealed partial class DownloadingOnlyStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            return status == DownloadStatus.Downloading ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public partial class CompletedStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            return status == DownloadStatus.Completed ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Shows the menu button for non-active states (Completed, Failed, Cancelled)
/// </summary>
public partial class NotDownloadingStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            return status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Installing or DownloadStatus.Cancelling
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public sealed partial class InstallingStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            return status == DownloadStatus.Installing ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public sealed partial class DownloadDetailsStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            // Right-side details are only for the actual download transfer phase.
            return status == DownloadStatus.Downloading ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>
/// Converts DownloadStatus enum to display text.
/// Used in DownloadsPage to avoid binding to StatusText which changes frequently due to animation.
/// </summary>
public sealed partial class DownloadStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            return status switch
            {
                DownloadStatus.Pending => "Status_Pending".GetLocalized() + "...",
                DownloadStatus.Downloading => "Status_Downloading".GetLocalized() + "...",
                DownloadStatus.Installing => "Status_Installing".GetLocalized() + "...",
                DownloadStatus.Completed => "Status_Completed".GetLocalized(),
                DownloadStatus.Failed => "Status_Failed".GetLocalized(),
                DownloadStatus.Cancelling => "Status_Cancelling".GetLocalized() + "...",
                DownloadStatus.Cancelled => "Status_Cancelled".GetLocalized(),
                _ => "Status_Unknown".GetLocalized()
            };
        }
        return "Status_Unknown".GetLocalized();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public sealed partial class DownloadStatusToIndeterminateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadStatus status)
        {
            return status is DownloadStatus.Pending or DownloadStatus.Cancelling;
        }

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
