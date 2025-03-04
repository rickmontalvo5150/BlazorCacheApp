namespace BlazorCacheApp.Services.CacheService;

public class CacheConfiguration
{
    public TimeSpan? DefaultExpirationTime { get; set; }
    public bool AutomaticExpirationCleanup { get; set; } = true;
    public static string KeyPrefix => "blazor_cache_";
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}