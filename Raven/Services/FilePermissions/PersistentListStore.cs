using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Raven.Services.FilePermissions;

public sealed class PersistentListStore<T> : PersistentJsonStoreBase<List<T>>, IPersistentListStore<T>
{
    private readonly JsonTypeInfo<List<T>> _typeInfo;

    /// <param name="typeInfo">
    /// Source-generated metadata for the list (e.g. <c>RavenJsonContext.Indented.ListDownloadItem</c>);
    /// its options also decide formatting such as indentation.
    /// </param>
    public PersistentListStore(string filePath, JsonTypeInfo<List<T>> typeInfo)
        : base(filePath)
    {
        _typeInfo = typeInfo;
    }

    public Task<List<T>> LoadAsync() => LoadCacheAsync();

    public Task SaveAsync(List<T> items) => SaveCacheAsync(items);

    protected override List<T> CreateEmptyCache() => [];

    protected override List<T> CloneCache(List<T> cache) => new(cache);

    protected override List<T>? DeserializeCache(string json) =>
        JsonSerializer.Deserialize(json, _typeInfo);

    protected override string SerializeCache(List<T> cache) =>
        JsonSerializer.Serialize(cache, _typeInfo);
}
