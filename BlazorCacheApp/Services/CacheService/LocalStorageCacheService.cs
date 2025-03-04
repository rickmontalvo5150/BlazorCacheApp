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

        if (!_config.AutomaticExpirationCleanup) return;
        _cleanupTimer = new PeriodicTimer(_config.CleanupInterval);
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
            while (await _cleanupTimer!.WaitForNextTickAsync())
            {
                await CleanExpiredItemsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Timer was disposed
        }
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        try
        {
            var json = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", GetPrefixedKey(key));
            
            if (string.IsNullOrEmpty(json))
                return default;

            var cacheItem = JsonSerializer.Deserialize<CacheItem<T>>(json, _jsonOptions);

            if (cacheItem == null)
                return default;

            if (!cacheItem.IsExpired) return cacheItem.Value;
            await RemoveAsync(key);
            return default;

        }
        catch
        {
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
        try
        {
            var json = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", GetPrefixedKey(key));
            if (string.IsNullOrEmpty(json))
                return false;
                
            // Try to parse and check if expired
            try
            {
                using var document = JsonDocument.Parse(json);
                
                if (document.RootElement.TryGetProperty("expirationTime", out var expirationElement))
                {
                    if (expirationElement.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(expirationElement.GetString(), out var expirationTime))
                    {
                        if (DateTime.UtcNow > expirationTime)
                        {
                            await RemoveAsync(key);
                            return false;
                        }
                    }
                }
            }
            catch
            {
                // If we can't parse it, or it doesn't match our format, just return true
            }
            
            return true;
        }
        catch
        {
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
            
            foreach (var prefixedKey in prefixedKeys)
            {
                var json = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", prefixedKey);
                if (string.IsNullOrEmpty(json)) continue;
                try
                {
                    using var document = JsonDocument.Parse(json);
                        
                    if (document.RootElement.TryGetProperty("expirationTime", out var expirationElement))
                    {
                        if (expirationElement.ValueKind == JsonValueKind.String &&
                            DateTime.TryParse(expirationElement.GetString(), out var expirationTime))
                        {
                            if (DateTime.UtcNow > expirationTime)
                            {
                                await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", prefixedKey);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip items that aren't valid JSON or don't match our format
                    System.Diagnostics.Debug.WriteLine($"Error cleaning cache item: {prefixedKey}");
                }
            }
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
                catch
                {
                    // Skip invalid items, if any
                }
            }
        }
        catch
        {
            // Handle errors, possibly log them
        }
        
        return stats;
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_cleanupTimer != null)
        {
            await ValueTask.CompletedTask; // Just to maintain the async signature
            _cleanupTimer.Dispose();
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