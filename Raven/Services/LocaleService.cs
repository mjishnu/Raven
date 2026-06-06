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

        ApplyLanguageOverride(Language, Market);
    }

    public async Task SetMarketAsync(Market market)
    {
        Market = market;
        await _localSettingsService.SaveSettingAsync(MarketSettingsKey, market.ToString());
        ApplyLanguageOverride(Language, Market);
        LocaleChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetLanguageAsync(Lang language)
    {
        Language = language;
        await _localSettingsService.SaveSettingAsync(LanguageSettingsKey, language.ToString());
        ApplyLanguageOverride(Language, Market);
        LocaleChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetToDefaultAsync()
    {
        Market = DetectMarketFromSystem();
        Language = DetectLanguageFromSystem();

        await _localSettingsService.SaveSettingAsync(MarketSettingsKey, Market.ToString());
        await _localSettingsService.SaveSettingAsync(LanguageSettingsKey, Language.ToString());

        ApplyLanguageOverride(Language, Market);
        LocaleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets <see cref="Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride"/> to
    /// the best-matching language tag derived from the current <paramref name="lang"/> and
    /// <paramref name="market"/> values so that WinUI 3's <c>ResourceLoader</c> loads the
    /// correct <c>Resources.resw</c> file.
    ///
    /// Resolution order (case-insensitive against <c>ManifestLanguages</c>):
    ///   1. Full tag  – e.g. <c>en-US</c>
    ///   2. First manifest entry whose language subtag matches  – e.g. <c>en-GB</c>
    ///   3. Empty string – resets to system default
    /// </summary>
    private static void ApplyLanguageOverride(Lang lang, Market market)
    {
        try
        {
            var fullTag = $"{lang.ToString().ToLowerInvariant()}-{market.ToString().ToUpperInvariant()}";
            var langPrefix = lang.ToString().ToLowerInvariant() + "-";

            var manifest = Windows.Globalization.ApplicationLanguages.ManifestLanguages;

            // 1. Exact match: "en-US"
            var resolved = manifest.FirstOrDefault(s =>
                string.Equals(s, fullTag, StringComparison.OrdinalIgnoreCase));

            // 2. First entry whose language subtag matches: "en-GB", "en-AU", …
            resolved ??= manifest.FirstOrDefault(s =>
                s.StartsWith(langPrefix, StringComparison.OrdinalIgnoreCase));

            // 3. No usable match – fall back to system default
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
                resolved ?? string.Empty;
        }
        catch { }
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
