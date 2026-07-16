using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.Models;
using Raven.Services;
using Raven.Views;

namespace Raven.Helpers;

class Utils
{
    public static bool IsNetworkError(Exception ex)
    {
        if (ex is System.Net.Http.HttpRequestException || ex is System.Net.Sockets.SocketException)
            return true;
        if (ex.InnerException != null)
            return IsNetworkError(ex.InnerException);
        return false;
    }
    /// <summary>
    /// Returns the architecture priority order based on system architecture and installer type.
    /// </summary>
    public static string[] GetArchPriorities(string archRid, bool isPackaged)
    {
        var arch = archRid?.ToLowerInvariant() ?? "x86";

        if (isPackaged)
        {
            return arch switch
            {
                "arm64" => new[] { "arm64", "x64", "x86", "neutral" },
                "x64" => new[] { "x64", "x86", "neutral" },
                "x86" => new[] { "x86", "neutral" },
                _ => new[] { arch, "neutral" },
            };
        }
        else
        {
            return arch switch
            {
                "arm64" => new[] { "arm64", "x64", "x86" },
                "x64" => new[] { "x64", "x86" },
                "x86" => new[] { "x86" },
                _ => new[] { arch },
            };
        }
    }

    /// <summary>
    /// Parses filename or architecture string to a standardized architecture name.
    /// </summary>
    public static string ParseArchString(string? name, bool isPackaged)
    {
        if (string.IsNullOrWhiteSpace(name))
            return isPackaged ? "neutral" : "x86";

        var lower = name.ToLowerInvariant();

        // arm64 must be checked before the bare "arm" token below, since "arm64" contains "arm".
        if (lower.Contains("arm64") || lower.Contains("aarch64"))
            return "arm64";
        if (lower.Contains("x64") || lower.Contains("amd64"))
            return "x64";
        if (lower.Contains("x86") || lower.Contains("x32"))
            return "x86";
        if (ContainsArchToken(lower, "arm"))
            return "arm";
        if (isPackaged && lower.Contains("neutral"))
            return "neutral";

        return isPackaged ? "neutral" : "x86";
    }

    /// <summary>
    /// Returns true if <paramref name="token"/> appears in <paramref name="value"/> bounded by
    /// non-alphanumeric characters (or string boundaries), so it represents a standalone
    /// architecture token rather than an incidental substring of a longer word.
    /// </summary>
    private static bool ContainsArchToken(string value, string token)
    {
        var idx = 0;
        while ((idx = value.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            var leftOk = idx == 0 || !char.IsLetterOrDigit(value[idx - 1]);
            var end = idx + token.Length;
            var rightOk = end == value.Length || !char.IsLetterOrDigit(value[end]);

            if (leftOk && rightOk)
                return true;

            idx = end;
        }

        return false;
    }

    public static async Task<Product> ProductOrBundle(
        string productId,
        InstallerType installerType,
        CancellationToken cancellationToken = default,
        Market market = Market.US,
        Lang language = Lang.en,
        bool skipVersionCheck = false
    )
    {
        if (installerType == InstallerType.Packaged)
        {
            var dcatResult = await DCATPackage.GetPackagesAsync(
                productId,
                market,
                language,
                includeNeutral: true,
                cancellationToken
            );

            if (!dcatResult.IsSuccess)
                throw dcatResult.Exception;

            var packages = dcatResult.Value.Where(p => p != null).ToList();

            if (packages.Count == 0)
                throw new Exception("No packages found for this product.");

            // Remember this catalog response so an Install/Update click on the page we are
            // about to open can skip re-fetching the identical DCAT data (5-minute TTL).
            DcatPrefetchCache.Store(productId, market, language, packages);

            // ---------------------------------------------------------
            // Architecture Selection
            // ---------------------------------------------------------
            var archRid = SystemInfo.GetOsArchRid();
            var priorities = GetArchPriorities(archRid, isPackaged: true);
            DCATPackage? best = null;

            foreach (var priority in priorities)
            {
                var matches = packages
                    .Where(p =>
                        ParseArchString(
                            p.PackageFullName ?? p.PackageIdentityName,
                            isPackaged: true
                        ) == priority
                    )
                    .OrderByDescending(p => p.AppVersion)
                    .ToList();

                if (matches.Any())
                {
                    best = matches.First();
                    break;
                }
            }

            if (best == null)
            {
                best = packages.OrderByDescending(p => p.AppVersion).FirstOrDefault();
            }

            if (best == null)
                throw new Exception("Unable to determine a valid package for this product.");

            // ---------------------------------------------------------
            // Product Construction
            // ---------------------------------------------------------
            var productData = ProductData.FromDCAT(best);

            if (best.IsBundle)
            {
                var bundlesResult = await StoreEdgeFDQuery.GetBundles(
                    productId,
                    DeviceFamily.Desktop,
                    market,
                    language,
                    cancellationToken
                );

                if (bundlesResult.IsSuccess)
                {
                    return new Product(productData, bundlesResult.Value);
                }
                else
                {
                    throw bundlesResult.Exception;
                }
            }
            else
            {
                if (!skipVersionCheck)
                {
                    await ConfirmInstalledUpdateVersionAsync(
                        productData,
                        packages,
                        market,
                        language,
                        cancellationToken
                    );
                }

                return new Product(productData, null);
            }
        }
        else
        {
            var pageResult = await StoreEdgeFDPage.GetProductAsync(
                productId,
                SystemInfo.GetStoreEdgeFDArch(),
                market,
                language,
                cancellationToken
            );

            if (!pageResult.IsSuccess)
                throw pageResult.Exception;

            var productData = ProductData.FromStoreEdgeFDPage(pageResult.Value);
            return new Product(productData, null);
        }
    }

    /// <summary>
    /// For an installed packaged app whose catalog (DCAT) version looks newer than what's installed,
    /// confirm the update against FE3 
    /// </summary>
    private static async Task ConfirmInstalledUpdateVersionAsync(
        ProductData productData,
        IReadOnlyList<DCATPackage> prefetchedPackages,
        Market market,
        Lang language,
        CancellationToken cancellationToken
    )
    {
        var installedVersion = PackagedAppDiscovery.GetInstalledVersion(productData.PackageFamilyName);

        if (!VersionComparison.IsStoreNewer(productData.Version, installedVersion))
            return;

        var verifiedVersion = await VersionCheckService.GetLatestVersionAsync(
            productData.ProductId,
            InstallerType.Packaged,
            cancellationToken,
            prefetchedPackages: prefetchedPackages,
            market: market,
            language: language
        );

        productData.Version = verifiedVersion;
    }

    public static int GetComboBoxItemIndexByTag(ComboBox comboBox, object tag)
    {
        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem item && item.Tag.Equals(tag))
            {
                return i;
            }
        }
        return 0;
    }

    public static void HandleCardTapped(
        FrameworkElement? sender,
        Frame navigationFrame,
        UIElement displayItem,
        UIElement errorIcon,
        UIElement loadingOverlay,
        TextBlock errorIconText,
        CancellationToken cancellationToken = default
    )
    {
        if (sender is FrameworkElement element && element.Tag is Card card)
        {
            NavigateToProductOrBundle(
                card.ProductId,
                card.InstallerType,
                navigationFrame,
                displayItem,
                errorIcon,
                loadingOverlay,
                errorIconText,
                cancellationToken
            );
        }
        else
        {
            Debug.WriteLine("Failed to get card for navigation");
        }
    }

    public static async void NavigateToProductOrBundle(
        string productId,
        InstallerType installerType,
        Frame navigationFrame,
        UIElement displayItem,
        UIElement errorIcon,
        UIElement loadingOverlay,
        TextBlock errorIconText,
        CancellationToken cancellationToken = default
    )
    {
        displayItem.Visibility = Visibility.Collapsed;
        errorIcon.Visibility = Visibility.Collapsed;
        loadingOverlay.Visibility = Visibility.Visible;
        try
        {
            var localeService = App.GetService<ILocaleService>();
            var product = await Utils.ProductOrBundle(productId, installerType, cancellationToken, market: localeService.Market, language: localeService.Language);

            // The token only guards the network awaits; a cancel landing after they complete
            // resumes here without an OCE. Don't touch the dead page's UI or stale-Navigate.
            if (cancellationToken.IsCancellationRequested)
                return;

            loadingOverlay.Visibility = Visibility.Collapsed;

            if (product.IsBundle)
            {
                navigationFrame.Navigate(
                    typeof(BundlesPage),
                    (product.ProductInfo, product.BundleInfo)
                );
            }
            else
            {
                navigationFrame.Navigate(typeof(AppPage), product.ProductInfo);
            }
        }
        catch (OperationCanceledException)
        {
            // Caller navigated away mid-fetch: nothing to show, nothing to navigate to.
        }
        catch (Exception ex)
        {
            loadingOverlay.Visibility = Visibility.Collapsed;
            errorIcon.Visibility = Visibility.Visible;
            errorIconText.Text = ex.Message;
            Debug.WriteLine($"Failed to load product: {ex.Message}");
        }
    }

    public static void CreateOrUpdateSpringAnimation(
        ref SpringVector3NaturalMotionAnimation? springAnimation,
        Compositor compositor,
        float finalValue
    )
    {
        if (springAnimation == null)
        {
            springAnimation = compositor.CreateSpringVector3Animation();
            springAnimation.Target = "Scale";
        }

        springAnimation.FinalValue = new Vector3(finalValue);
    }

    public static void HandlePointerEntered(
        object sender,
        PointerRoutedEventArgs e,
        ref SpringVector3NaturalMotionAnimation? springAnimation,
        Compositor compositor
    )
    {
        // Scale up to 1.025
        CreateOrUpdateSpringAnimation(ref springAnimation, compositor, 1.025f);
        if (sender is FrameworkElement element)
        {
            element.CenterPoint = new Vector3(
                (float)(element.ActualWidth / 2.0),
                (float)(element.ActualHeight / 2.0),
                1f
            );
            element.StartAnimation(springAnimation);
        }
    }

    public static void HandlePointerExited(
        object sender,
        PointerRoutedEventArgs e,
        ref SpringVector3NaturalMotionAnimation? springAnimation,
        Compositor compositor
    )
    {
        // Scale back down to 1.0
        CreateOrUpdateSpringAnimation(ref springAnimation, compositor, 1.0f);
        if (sender is FrameworkElement element)
        {
            element.CenterPoint = new Vector3(
                (float)(element.ActualWidth / 2.0),
                (float)(element.ActualHeight / 2.0),
                1f
            );
            element.StartAnimation(springAnimation);
        }
    }

    internal static void HandlePointerEntered(
        object sender,
        PointerRoutedEventArgs e,
        ref object springAnimation,
        object compositor
    ) => throw new NotImplementedException();
}
