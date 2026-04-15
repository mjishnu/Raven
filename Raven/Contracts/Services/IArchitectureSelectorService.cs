using StoreListings.Library;

namespace Raven.Contracts.Services;

public interface IArchitectureSelectorService
{
    string SelectedArchRid { get; }

    StoreEdgeFDArch SelectedStoreEdgeArchitecture { get; }

    Task InitializeAsync();

    Task SetSelectedArchitectureAsync(StoreEdgeFDArch architecture);

    Task ResetToDefaultAsync();
}
