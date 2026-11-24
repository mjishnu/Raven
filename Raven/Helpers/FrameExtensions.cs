using Microsoft.UI.Xaml.Controls;
using Raven.Contracts.Views;

namespace Raven.Helpers;

public static class FrameExtensions
{
    public static object? GetPageViewModel(this Frame frame) => (frame?.Content as IViewModelPage<object>)?.ViewModel;
}
