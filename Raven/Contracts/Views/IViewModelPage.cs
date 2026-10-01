namespace Raven.Contracts.Views;

/// <summary>
/// A page that exposes its view model, so navigation can reach it without reflection
/// (which trimming/NativeAOT would break). Covariant, so any page can be read as
/// <c>IViewModelPage&lt;object&gt;</c>.
/// </summary>
public interface IViewModelPage<out TViewModel>
{
    TViewModel ViewModel { get; }
}
