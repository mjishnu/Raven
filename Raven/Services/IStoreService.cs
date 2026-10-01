using StoreListings.Library;

namespace Raven.Services
{
    public interface IStoreService
    {
        Task<SearchResult> SearchProducts(
            string query,
            MediaTypeSearch mediaType,
            PriceType priceType,
            int skip,
            int take = 25
        );
    }

    public class SearchResult
    {
        public bool IsSuccess { get; set; }
        public Card[] Cards { get; set; } = [];
        public bool HasMoreItems { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
