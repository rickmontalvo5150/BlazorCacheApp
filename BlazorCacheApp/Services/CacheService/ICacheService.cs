namespace BlazorCacheApp.Services.CacheService;

public interface ICacheService
{
    /// <summary>
    /// Retrieves a value from the cache by key.
    /// </summary>
    /// <typeparam name="T">The type of the value to retrieve.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <returns>The cached value or default if not found or expired.</returns>
    Task<T?> GetAsync<T>(string key);
    
    /// <summary>
    /// Stores a value in the cache.
    /// </summary>
    /// <typeparam name="T">The type of the value to store.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expirationTime">Optional expiration timespan. If null, uses the default or never expires.</param>
    Task SetAsync<T>(string key, T value, TimeSpan? expirationTime = null);
    
    /// <summary>
    /// Removes an item from the cache.
    /// </summary>
    /// <param name="key">The cache key to remove.</param>
    Task RemoveAsync(string key);
    
    /// <summary>
    /// Clears all items from the cache.
    /// </summary>
    Task ClearAsync();
    
    /// <summary>
    /// Checks if a key exists in the cache and is not expired.
    /// Since the implementations are used, this method can either be removed
    /// or left here for further experimenting
    /// </summary>
    /// <param name="key">The cache key to check.</param>
    /// <returns>True if the key exists and is not expired.</returns>
    Task<bool> ExistsAsync(string key);
    
    /// <summary>
    /// Manually triggers cleanup of expired items.
    /// </summary>
    Task CleanExpiredItemsAsync();
    
    /// <summary>
    /// Gets all cache keys (for debugging/admin purposes).
    /// </summary>
    /// <returns>A list of all cache keys.</returns>
    Task<IEnumerable<string>> GetAllKeysAsync();
    
    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    /// <returns>Information about the cache.</returns>
    Task<CacheStatistics> GetStatisticsAsync();
}

public class CacheStatistics
{
    public int TotalItems { get; set; }
    public int ExpiredItems { get; set; }
    public long EstimatedSizeBytes { get; set; }
    public DateTime LastCleanupTime { get; init; }
}