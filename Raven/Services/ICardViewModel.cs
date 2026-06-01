using System.Collections.ObjectModel;
using StoreListings.Library;

public interface ICardViewModel
{
    int FirstVisibleIndex { get; set; }
    bool HasMoreItems { get; }
    bool HasCachedResults { get; }
    string HeaderText { get; }
    int CurrentSkipItem { get; set; }
    object Filter1 { get; set; }
    object Filter2 { get; set; }
    ObservableCollection<Card> Cards { get; }
}
