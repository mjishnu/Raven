using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Raven.Helpers;

namespace Raven.Services.FilePermissions;

public sealed class PersistentFileStore : PersistentJsonStoreBase<Dictionary<string, string>>, IPersistentFileStore
{
    public PersistentFileStore(string filePath)
        : base(filePath)
    {
    }

    public async Task<T?> ReadAsync<T>(string key)
    {
        var cache = await LoadCacheAsync();
        return cache.TryGetValue(key, out var json) ? JsonSerializer.Deserialize(json, GetTypeInfo<T>()) : default;
    }

    public async Task WriteAsync<T>(string key, T value)
    {
        var serialized = JsonSerializer.Serialize(value, GetTypeInfo<T>());
        await UpdateCacheAsync(cache => cache[key] = serialized);
    }

    public async Task<IDictionary<string, string>> LoadAllAsync()
    {
        var cache = await LoadCacheAsync();
        return new Dictionary<string, string>(cache);
    }

    public Task SaveAllAsync(IDictionary<string, string> data) =>
        SaveCacheAsync(new Dictionary<string, string>(data));

    protected override Dictionary<string, string> CreateEmptyCache() => [];

    protected override Dictionary<string, string> CloneCache(Dictionary<string, string> cache) => new(cache);

    protected override Dictionary<string, string>? DeserializeCache(string json) =>
        JsonSerializer.Deserialize(json, RavenJsonContext.Default.DictionaryStringString);

    protected override string SerializeCache(Dictionary<string, string> cache) =>
        JsonSerializer.Serialize(cache, RavenJsonContext.Default.DictionaryStringString);

    // Values use source-generated metadata (reflection-based JSON is unavailable when trimmed),
    // so every value type stored here must be registered on RavenJsonContext.
    private static JsonTypeInfo<T> GetTypeInfo<T>() =>
        RavenJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException(
            $"'{typeof(T)}' is not registered on {nameof(RavenJsonContext)}. Add [JsonSerializable(typeof(...))] for it.");
}
