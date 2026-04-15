using System.Runtime.InteropServices;
using Raven.Contracts.Services;
using StoreListings.Library;
using Windows.System.Profile;

namespace Raven.Helpers;

static class SystemInfo
{
    // Prefer exact Windows build (works well in packaged WinUI 3):
    public static StoreListings.Library.Version GetExactWindowsVersion()
    {
        // AnalyticsInfo.VersionInfo.DeviceFamilyVersion is a ulong encoded as:
        // major (16b), minor (16b), build (16b), revision (16b).
        var vStr = AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
        if (ulong.TryParse(vStr, out var v))
        {
            var major = (uint)((v >> 48) & 0xFFFFu);
            var minor = (uint)((v >> 32) & 0xFFFFu);
            var build = (uint)((v >> 16) & 0xFFFFu);
            var rev = (uint)(v & 0xFFFFu);
            return new StoreListings.Library.Version(major, minor, build, rev);
        }
        // Fallback if parsing fails:
        return new StoreListings.Library.Version(10u, 0u, 19045u, 0u);
    }

    public static string GetSystemArchRid()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };
    }

    public static string ToArchRid(StoreEdgeFDArch architecture)
    {
        return architecture switch
        {
            StoreEdgeFDArch.ARM64 => "arm64",
            StoreEdgeFDArch.ARM => "arm",
            StoreEdgeFDArch.X86 => "x86",
            _ => "x64",
        };
    }

    public static StoreEdgeFDArch ToStoreEdgeFDArch(string archRid)
    {
        return archRid.ToLowerInvariant() switch
        {
            "arm64" => StoreEdgeFDArch.ARM64,
            "arm" => StoreEdgeFDArch.ARM,
            "x86" => StoreEdgeFDArch.X86,
            _ => StoreEdgeFDArch.X64,
        };
    }

    public static string GetOsArchRid()
    {
        try
        {
            return App.GetService<IArchitectureSelectorService>().SelectedArchRid;
        }
        catch
        {
            return GetSystemArchRid();
        }
    }

    public static StoreEdgeFDArch GetStoreEdgeFDArch()
    {
        return ToStoreEdgeFDArch(GetOsArchRid());
    }
}
