using System.Text;
using System.Xml.Linq;

namespace Raven.Helpers;

public sealed record BundlePackageRef(string FileName, string Architecture, string Version);

/// <summary>
/// Pure (I/O-free) helpers for inspecting loose appx/msix packages and bundles.
/// </summary>
public static class LoosePackageInspector
{
    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "App";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (ch == ' ')
                sb.Append('_');
            else if (!invalid.Contains(ch))
                sb.Append(ch);
        }

        var result = sb.ToString().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "App" : result;
    }

    public static string ExtractAppName(string appManifestXml)
    {
        var doc = XDocument.Parse(appManifestXml);

        var display = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value?.Trim();
        if (!string.IsNullOrEmpty(display)
            && !display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return display;

        var identityName = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value?.Trim();
        return string.IsNullOrEmpty(identityName) ? "App" : identityName;
    }

    public static IReadOnlyList<BundlePackageRef> ParseBundleApplicationPackages(string bundleManifestXml)
    {
        var doc = XDocument.Parse(bundleManifestXml);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Package")
            .Where(e => string.Equals(
                (string?)e.Attribute("Type"), "application", StringComparison.OrdinalIgnoreCase))
            .Select(e => new BundlePackageRef(
                FileName: (string?)e.Attribute("FileName") ?? string.Empty,
                Architecture: (string?)e.Attribute("Architecture") ?? string.Empty,
                Version: (string?)e.Attribute("Version") ?? "0.0.0.0"))
            .Where(p => p.FileName.Length > 0)
            .ToList();
    }

    public static BundlePackageRef? SelectApplicationPackage(
        IReadOnlyList<BundlePackageRef> packages, string archRid)
    {
        if (packages.Count == 0)
            return null;

        foreach (var pref in Utils.GetArchPriorities(archRid, isPackaged: true))
        {
            var match = packages.FirstOrDefault(p =>
                Utils.ParseArchString(
                    // The bundle manifest's Architecture attribute is authoritative;
                    // fall back to the file name only for malformed (arch-less) entries.
                    string.IsNullOrEmpty(p.Architecture) ? p.FileName : p.Architecture,
                    isPackaged: true) == pref);
            if (match is not null)
                return match;
        }

        // Final fallback (mirrors Utils.ProductOrBundle): highest-version application package.
        return packages
            .OrderByDescending(p =>
                Version.TryParse(p.Version, out var v) ? v : new Version(0, 0))
            .First();
    }
}
