namespace Raven.Services.FilePermissions;

/// <summary>
/// Robust file persistence module for JSON-based data storage.
/// 
/// Features:
/// - Thread-safe access with lazy initialization
/// - Automatic corruption detection and recovery from backup
/// - Atomic file writes using temp file + move pattern
/// - Temp files written to system temp directory for automatic cleanup on crash
/// - Backup preservation during writes
/// - Single-instance protection (designed for single-app usage)
/// - Trimming/NativeAOT safe: values are serialized with source-generated metadata, so every
///   value type passed to ReadAsync/WriteAsync must be registered on <c>RavenJsonContext</c>
///
/// Usage Example:
/// <code>
/// var filePath = Path.Combine(appDataFolder, "download.json");
/// var fileStore = new PersistentFileStore(filePath);
/// 
/// // Read single value
/// var settings = await fileStore.ReadAsync&lt;MySettings&gt;("key");
/// 
/// // Write single value
/// await fileStore.WriteAsync("key", myObject);
/// 
/// // Load/Save entire dictionary
/// var all = await fileStore.LoadAllAsync();
/// await fileStore.SaveAllAsync(all);
/// </code>
/// 
/// Recovery Strategy:
/// 1. Attempts to read main file
/// 2. On JSON error, attempts to restore from .backup file
/// 3. If backup also fails or doesn't exist, starts fresh with empty dictionary
/// 4. Corrupted main file is moved to .corrupted for analysis
/// </summary>
public interface IPersistentFileStore
{
    /// <summary>
    /// Reads a single deserialized value by key.
    /// </summary>
    Task<T?> ReadAsync<T>(string key);

    /// <summary>
    /// Writes a single serialized value by key.
    /// </summary>
    Task WriteAsync<T>(string key, T value);

    /// <summary>
    /// Loads entire settings dictionary.
    /// </summary>
    Task<IDictionary<string, string>> LoadAllAsync();

    /// <summary>
    /// Saves entire settings dictionary.
    /// </summary>
    Task SaveAllAsync(IDictionary<string, string> data);
}
