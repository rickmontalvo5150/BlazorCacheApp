using System.Text.Json.Serialization;

namespace BlazorCacheApp.Services.CacheService;

public class CacheItem<T>
{
    [JsonPropertyName("value")]
    public T Value { get; init; } = default!;
    
    // Store as string to preserve exact format
    [JsonPropertyName("expirationTime")]
    public string? ExpirationTimeString { get; init; }
    
    [JsonIgnore]
    public DateTime? ExpirationTime 
    { 
        get => string.IsNullOrEmpty(ExpirationTimeString) 
            ? null 
            : DateTime.Parse(ExpirationTimeString).ToUniversalTime();
        init => ExpirationTimeString = value?.ToUniversalTime().ToString("o");
    }
    
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }
    
    [JsonIgnore]
    public bool IsExpired => ExpirationTime.HasValue && DateTime.UtcNow > ExpirationTime.Value;
    
    public CacheItem(T value, DateTime? expirationTime = null)
    {
        Value = value;
        ExpirationTime = expirationTime;
        CreatedAt = DateTime.UtcNow;
    }
    
    public CacheItem() 
    {
        CreatedAt = DateTime.UtcNow;
    }
}