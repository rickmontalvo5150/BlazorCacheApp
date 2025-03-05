using System.Text.Json;
using Microsoft.JSInterop;
using Microsoft.Extensions.Options;

namespace BlazorCacheApp.Services.CacheService;

public class LocalStorageCacheService : ICacheService, IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly CacheConfiguration _config;
    private readonly PeriodicTimer? _cleanupTimer;
    private DateTime _lastCleanupTime = DateTime.UtcNow;
    private readonly CancellationTokenSource? _cleanupCts;

    public LocalStorageCacheService(
        IJSRuntime jsRuntime, 
        IOptions<CacheConfiguration> config)
    {
        _jsRuntime = jsRuntime;
        _config = config.Value;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        if (!_config.AutomaticExpirationCleanup)
        {
            return;
        }
        _cleanupTimer = new PeriodicTimer(_config.CleanupInterval);
        _cleanupCts = new CancellationTokenSource();

        // Start the cleanup timer with proper cancellation support
        _ = StartCleanupTimer();
    }

    private static string GetPrefixedKey(string key) => $"{CacheConfiguration.KeyPrefix}{key}";
    
    private static string GetOriginalKey(string prefixedKey) => 
        prefixedKey.StartsWith(CacheConfiguration.KeyPrefix) 
            ? prefixedKey[CacheConfiguration.KeyPrefix.Length..] 
            : prefixedKey;

    private async Task StartCleanupTimer()
    {
        try
        {
            try
            {
                while (await _cleanupTimer!.WaitForNextTickAsync(_cleanupCts!.Token))
                {
                    await CleanExpiredItemsAsync();
                    _lastCleanupTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log the error but don't stop the timer
                System.Diagnostics.Debug.WriteLine($"Error during scheduled cleanup: {ex.Message}");
            }
            
        }
        catch (OperationCanceledException)
        {
            // Timer was cancelled, this is expected
            System.Diagnostics.Debug.WriteLine("Cleanup timer was cancelled");
        }
        catch (Exception ex)
        {
            // Log unexpected exceptions
            System.Diagnostics.Debug.WriteLine($"Unexpected error in cleanup timer: {ex.Message}");
        }
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Cache key cannot be null or empty", nameof(key));
        
        try
        {
            var prefixedKey = GetPrefixedKey(key);
            var json = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", prefixedKey);
        
            if (string.IsNullOrEmpty(json))
                return default;

            var cacheItem = JsonSerializer.Deserialize<CacheItem<T>>(json, _jsonOptions);

            if (cacheItem == null)
                return default;

            if (!cacheItem.IsExpired)
                return cacheItem.Value;
            
            // Item is expired - remove it
            System.Diagnostics.Debug.WriteLine($"Cache item '{key}' was accessed but found expired");
            await RemoveAsync(key);
            return default;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error deserializing cache item '{key}': {ex.Message}");
            return default;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unexpected error accessing cache item '{key}': {ex.Message}");
            return default;
        }
    }

    async Task ICacheService.SetAsync<T>(string key, T value, TimeSpan? expirationTime)
    {
        try
        {
            var effectiveExpiration = expirationTime ?? _config.DefaultExpirationTime;
            
            var cacheItem = new CacheItem<T>(
                value,
                effectiveExpiration.HasValue ? DateTime.UtcNow.Add(effectiveExpiration.Value) : null
            );

            var json = JsonSerializer.Serialize(cacheItem, _jsonOptions);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", GetPrefixedKey(key), json);
        }
        catch (Exception ex)
        {
            throw new Exception($"Error setting cache item: {ex.Message}", ex);
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", GetPrefixedKey(key));
        }
        catch (Exception ex)
        {
            throw new Exception($"Error removing cache item: {ex.Message}", ex);
        }
    }    

    async Task ICacheService.ClearAsync()
    {
        try
        {
            var keys = await GetPrefixedKeysAsync();
            foreach (var key in keys)
            {
                await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", key);
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error clearing cache: {ex.Message}", ex);
        }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Cache key cannot be null or empty", nameof(key));
        
        try
        {
            var prefixedKey = GetPrefixedKey(key);
            var json = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", prefixedKey);
        
            if (string.IsNullOrEmpty(json))
                return false;
            
            // Use JsonDocument for lightweight parsing to check expiration
            try
            {
                using var document = JsonDocument.Parse(json);

                if (!document.RootElement.TryGetProperty("expirationTime", out var expirationElement) ||
                    expirationElement.ValueKind != JsonValueKind.String ||
                    !DateTime.TryParse(expirationElement.GetString(), out var expirationTime))
                {
                    return true;
                }

                if (DateTime.UtcNow <= expirationTime)
                {
                    return true;
                }

                System.Diagnostics.Debug.WriteLine($"Cache item '{key}' exists but is expired");
                // Remove expired item asynchronously without awaiting
                _ = RemoveAsync(key);
                return false;

            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing cache item '{key}': {ex.Message}");
                return true; // If we can't parse it, assume it exists but may not be our format
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking existence of cache item '{key}': {ex.Message}");
            return false;
        }
    }

    public async Task CleanExpiredItemsAsync()
    {
        try
        {
            _lastCleanupTime = DateTime.UtcNow;
            
            // Get all keys that start with our prefix
            var prefixedKeys = await GetPrefixedKeysAsync();
            var removedCount = 0;
            var totalCount = prefixedKeys.Count;
            
            System.Diagnostics.Debug.WriteLine($"Starting cleanup of {totalCount} potential cache items");
            
            foreach (var prefixedKey in prefixedKeys)
            {
                try
                {
                    var json = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", prefixedKey);
                    if (string.IsNullOrEmpty(json))
                    {
                        continue;
                    }
                    
                    using var document = JsonDocument.Parse(json);

                    if (!document.RootElement.TryGetProperty("expirationTime", out var expirationElement) ||
                        expirationElement.ValueKind != JsonValueKind.String ||
                        !DateTime.TryParse(expirationElement.GetString(), out var expirationTime))
                    {
                        continue;
                    }

                    if (DateTime.UtcNow <= expirationTime)
                    {
                        continue;
                    }
                    await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", prefixedKey);
                    removedCount++;
                }
                catch (JsonException ex)
                {
                    // Skip items that aren't valid JSON or don't match our format
                    System.Diagnostics.Debug.WriteLine($"Error parsing cache item during cleanup: {prefixedKey}, {ex.Message}");
                }
                catch (JSException ex)
                {
                    // Handle JavaScript errors separately
                    System.Diagnostics.Debug.WriteLine($"JavaScript error during cleanup: {prefixedKey}, {ex.Message}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Log other errors but continue with next items
                    System.Diagnostics.Debug.WriteLine($"Unexpected error cleaning cache item: {prefixedKey}, {ex.Message}");
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"Cleanup completed: removed {removedCount} of {totalCount} items");
        }
        catch (OperationCanceledException)
        {
            // Cleanup was cancelled
            System.Diagnostics.Debug.WriteLine("Cache cleanup was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during cache cleanup: {ex.Message}");
        }
    }
    
    private async Task<List<string>> GetPrefixedKeysAsync()
    {
        try
        {
            var allKeys = await _jsRuntime.InvokeAsync<string[]>("getCacheKeys");
            return allKeys.Where(k => k.StartsWith(CacheConfiguration.KeyPrefix)).ToList();
        }
        catch (OperationCanceledException)
        {
            // Operation was canceled
            throw;
        }
        catch (JSException ex)
        {
            System.Diagnostics.Debug.WriteLine($"JavaScript error getting prefixed keys: {ex.Message}");
            return [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting prefixed keys: {ex.Message}");
            return [];
        }
    }

    async Task<IEnumerable<string>> ICacheService.GetAllKeysAsync()
    {
        var prefixedKeys = await GetPrefixedKeysAsync();
        return prefixedKeys.Select(GetOriginalKey);
    }

    public async Task<CacheStatistics> GetStatisticsAsync()
    {
        var stats = new CacheStatistics
        {
            TotalItems = 0,
            ExpiredItems = 0,
            EstimatedSizeBytes = 0,
            LastCleanupTime = _lastCleanupTime
        };
        
        try 
        {
            var prefixedKeys = await GetPrefixedKeysAsync();
            stats.TotalItems = prefixedKeys.Count;
            
            foreach (var key in prefixedKeys)
            {
                var json = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", key);
                if (string.IsNullOrEmpty(json)) continue;
                stats.EstimatedSizeBytes += json.Length * 2; // Rough estimate (2 bytes per char)
                    
                try
                {
                    using var document = JsonDocument.Parse(json);
                        
                    if (document.RootElement.TryGetProperty("expirationTime", out var expirationElement))
                    {
                        if (expirationElement.ValueKind == JsonValueKind.String)
                        {
                            var expirationString = expirationElement.GetString();
                            if (!string.IsNullOrEmpty(expirationString) && 
                                DateTime.TryParse(expirationString, out var expirationTime))
                            {
                                // Ensure we're comparing UTC times
                                var expirationTimeUtc = expirationTime.ToUniversalTime();
                                var nowUtc = DateTime.UtcNow;
            
                                if (nowUtc > expirationTimeUtc)
                                {
                                    stats.ExpiredItems++;
                                }
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    // Skip invalid items, if any
                    System.Diagnostics.Debug.WriteLine($"Error parsing cache item during statistics gathering: {key}, {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // Handle errors, possibly log them
            System.Diagnostics.Debug.WriteLine($"Error gathering cache statistics: {ex.Message}");
        }
        
        return stats;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Cancel the cleanup task first
            if (_cleanupCts != null)
            {
                await _cleanupCts.CancelAsync();
                _cleanupCts.Dispose();
            }
        
            // Then dispose the timer
            _cleanupTimer?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during cache service disposal: {ex.Message}");
        }
    
        GC.SuppressFinalize(this);
    }
}

// Extension method for service registration
public static class CacheServiceExtensions
{
    public static IServiceCollection AddLocalStorageCache(this IServiceCollection services,
        Action<CacheConfiguration>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<CacheConfiguration>(_ => { });
        }

        return services.AddScoped<ICacheService, LocalStorageCacheService>();
    }
}