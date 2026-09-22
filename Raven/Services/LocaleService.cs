using System.Globalization;
using StoreListings.Library;
using Raven.Contracts.Services;

namespace Raven.Services;

public class LocaleService : ILocaleService
{
    private const string MarketSettingsKey = "AppMarket";
    private const string LanguageSettingsKey = "AppLanguage";

    private readonly ILocalSettingsService _localSettingsService;

    public Market Market { get; private set; } = Market.US;

    public Lang Language { get; private set; } = Lang.en;

    public event EventHandler? LocaleChanged;

    public LocaleService(ILocalSettingsService localSettingsService)
    {
        _localSettingsService = localSettingsService;
    }

    public async Task InitializeAsync()
    {
        var savedMarket = await _localSettingsService.ReadSettingAsync<string>(MarketSettingsKey);
        var savedLang = await _localSettingsService.ReadSettingAsync<string>(LanguageSettingsKey);

        var hasExistingSettings = savedMarket != null || savedLang != null;

        if (savedMarket != null && Enum.TryParse<Market>(savedMarket, out var market))
            Market = market;
        else if (!hasExistingSettings)
            Market = DetectMarketFromSystem();

        if (savedLang != null && Enum.TryParse<Lang>(savedLang, out var lang))
            Language = lang;
        else if (!hasExistingSettings)
            Language = DetectLanguageFromSystem();

        ApplyLanguageOverride(Language);
    }

    public async Task SetMarketAsync(Market market)
    {
        Market = market;
        await _localSettingsService.SaveSettingAsync(MarketSettingsKey, market.ToString());
        ApplyLanguageOverride(Language);
        LocaleChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetLanguageAsync(Lang language)
    {
        Language = language;
        await _localSettingsService.SaveSettingAsync(LanguageSettingsKey, language.ToString());
        ApplyLanguageOverride(Language);
        LocaleChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetToDefaultAsync()
    {
        Market = DetectMarketFromSystem();
        Language = DetectLanguageFromSystem();

        await _localSettingsService.SaveSettingAsync(MarketSettingsKey, Market.ToString());
        await _localSettingsService.SaveSettingAsync(LanguageSettingsKey, Language.ToString());

        ApplyLanguageOverride(Language);
        LocaleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Overrides the app's preferred language so WinUI 3's <c>ResourceLoader</c> loads the
    /// correct <c>Resources.resw</c> file. The UI resource language is deliberately independent
    /// from the Store market: changing the market must not change the app language. For
    /// languages with a shipped regional resource, use that stable resource tag; otherwise
    /// fall back to the PRI's default language (<c>en-US</c>).
    /// </summary>
    private static void ApplyLanguageOverride(Lang lang)
    {
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
                GetResourceLanguage(lang);
            ApplyDotNetCulture(lang);
        }
        catch { }
    }

    private static string GetResourceLanguage(Lang lang) =>
        lang switch
        {
            Lang.zh => "zh-cn",
            Lang.ko => "ko-kr",
            Lang.hu => "hu-HU",
            _ => "en-us",
        };

    private static void ApplyDotNetCulture(Lang lang)
    {
        var culture = TryGetCulture(lang.ToString());
        if (culture is null)
            return;

        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private static CultureInfo? TryGetCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static Market DetectMarketFromSystem()
    {
        try
        {
            var region = Windows.System.UserProfile.GlobalizationPreferences.HomeGeographicRegion;
            if (!string.IsNullOrEmpty(region) && Enum.TryParse<Market>(region, true, out var market))
                return market;
        }
        catch { }
        return Market.US;
    }

    private static Lang DetectLanguageFromSystem()
    {
        try
        {
            var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            if (languages?.Count > 0)
            {
                var tag = languages[0];
                var code = tag.Contains('-') ? tag.Split('-')[0].ToLowerInvariant() : tag.ToLowerInvariant();
                if (Enum.TryParse<Lang>(code, true, out var lang))
                    return lang;
            }
        }
        catch { }
        return Lang.en;
    }
}
