using System.Threading.Tasks;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.Services;

namespace Raven.Services
{
    public class StoreService : IStoreService
    {
        private readonly ILocaleService _localeService;

        public StoreService(ILocaleService localeService)
        {
            _localeService = localeService;
        }

        public async Task<SearchResult> SearchProducts(
            string query,
            MediaTypeSearch mediaType,
            PriceType priceType,
            int skip,
            int take = 25
        )
        {
            try
            {
                var result = await StoreEdgeFDQuery.GetSearchProduct(
                    query,
                    DeviceFamily.Desktop,
                    _localeService.Market,
                    _localeService.Language,
                    skip,
                    mediaType
                );

                return new SearchResult
                {
                    IsSuccess = result.IsSuccess,
                    Cards = result.Value?.Cards.ToArray() ?? new Card[0],
                    HasMoreItems = result.Value?.Cards.Count > 0,
                    ErrorMessage = result.Exception?.Message,
                };
            }
            catch (System.Exception ex)
            {
                return new SearchResult { IsSuccess = false, ErrorMessage = ex.Message };
            }
        }
    }
}
