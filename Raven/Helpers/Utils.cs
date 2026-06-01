using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.Models;
using Raven.Views;

namespace Raven.Helpers;

class Utils
{
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
        Lang language = Lang.en
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
        TextBlock errorIconText
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
                errorIconText
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
        TextBlock errorIconText
    )
    {
        displayItem.Visibility = Visibility.Collapsed;
        errorIcon.Visibility = Visibility.Collapsed;
        loadingOverlay.Visibility = Visibility.Visible;
        try
        {
            var localeService = App.GetService<ILocaleService>();
            var product = await Utils.ProductOrBundle(productId, installerType, market: localeService.Market, language: localeService.Language);

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
