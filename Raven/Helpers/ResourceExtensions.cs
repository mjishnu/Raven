using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace Raven.Helpers;

public static class ResourceExtensions
{
    // The PRI's default language; used as the fallback when a translated template is malformed.
    private const string FallbackLanguage = "en-US";

    private static readonly ResourceManager _resourceManager = new();
    private static readonly ResourceMap _resourceMap = _resourceManager.MainResourceMap.GetSubtree("Resources");

    // The default ResourceContext resolves against the *system* language, not the app's
    // PrimaryLanguageOverride. So a context whose Language qualifier is pinned to the override is
    // cached and reused; without this, GetLocalized() strings ignore the in-app language setting
    // (XAML x:Uid honours the override, code-behind lookups would not). Rebuilt when it changes
    private static readonly object _contextLock = new();
    private static ResourceContext? _context;
    private static string? _contextLanguage;

    public static string GetLocalized(this string resourceKey)
    {
        try
        {
            return _resourceMap.GetValue(resourceKey, GetContext())?.ValueAsString ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Localizes <paramref name="resourceKey"/> and applies <see cref="string.Format(string, object?[])"/>
    /// with <paramref name="args"/>. If the localized template has malformed placeholders (a bad
    /// translation, e.g. an unbalanced brace), this falls back to the English template so callers
    /// never crash with a <see cref="FormatException"/> on a single bad string.
    /// </summary>
    public static string GetLocalizedFormat(this string resourceKey, params object[] args)
    {
        var template = resourceKey.GetLocalized();
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            try
            {
                return string.Format(GetForLanguage(resourceKey, FallbackLanguage), args);
            }
            catch
            {
                // Even the fallback template is unusable — return it raw rather than throw.
                return template;
            }
        }
    }

    private static string GetForLanguage(string resourceKey, string language)
    {
        var context = _resourceManager.CreateResourceContext();
        context.QualifierValues["Language"] = language;
        return _resourceMap.GetValue(resourceKey, context)?.ValueAsString ?? string.Empty;
    }

    private static ResourceContext GetContext()
    {
        var language = NormalizeLanguageTag(ApplicationLanguages.PrimaryLanguageOverride);

        lock (_contextLock)
        {
            if (_context is null || _contextLanguage != language)
            {
                var context = _resourceManager.CreateResourceContext();
                if (!string.IsNullOrEmpty(language))
                    context.QualifierValues["Language"] = language;

                _context = context;
                _contextLanguage = language;
            }

            return _context;
        }
    }

    private static string? NormalizeLanguageTag(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        // Keep the tag shape used by the generated PRI resource folders. MRT's
        // language qualifier is case-insensitive in principle, but using the
        // exact shipped tag avoids falling back to the default candidate on
        // unpackaged WinUI apps.
        return language.ToLowerInvariant() switch
        {
            "zh-cn" => "zh-cn",
            "ko-kr" => "ko-kr",
            "hu-hu" => "hu-HU",
            "en-us" => "en-us",
            _ => language,
        };
    }
}
