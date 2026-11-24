using System.Text.Json;
using System.Text.Json.Serialization;
using Raven.Models;
using Raven.ViewModels;

namespace Raven.Helpers;

/// <summary>
/// Source-generated System.Text.Json metadata for everything Raven persists. Reflection-based
/// serialization is disabled once the app is trimmed/NativeAOT-compiled, so any type passed to
/// <see cref="JsonSerializer"/> (including values stored through <c>ILocalSettingsService</c>)
/// must be registered here.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<DownloadItem>))]
[JsonSerializable(typeof(List<UpdatesViewModel.CompletedUpdateEntry>))]
// Setting values
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateTimeOffset?))]
internal sealed partial class RavenJsonContext : JsonSerializerContext
{
    /// <summary>The same contracts as <see cref="Default"/>, but writing indented JSON.</summary>
    public static RavenJsonContext Indented { get; } = new(new JsonSerializerOptions { WriteIndented = true });
}
