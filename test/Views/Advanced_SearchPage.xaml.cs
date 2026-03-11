using Microsoft.UI.Xaml.Controls;

using test.ViewModels;

namespace test.Views;

public sealed partial class Advanced_SearchPage : Page
{
    public Advanced_SearchViewModel ViewModel
    {
        get;
    }

    public Advanced_SearchPage()
    {
        ViewModel = App.GetService<Advanced_SearchViewModel>();
        InitializeComponent();
    }
}
