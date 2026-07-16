using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Raven.Views.Shared;

public sealed partial class ErrorPanelControl : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(ErrorPanelControl),
        new PropertyMetadata("\uE894")
    );

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(ErrorPanelControl),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle),
        typeof(string),
        typeof(ErrorPanelControl),
        new PropertyMetadata(string.Empty)
    );

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public ErrorPanelControl()
    {
        InitializeComponent();
    }
}
