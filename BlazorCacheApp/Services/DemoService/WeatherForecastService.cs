namespace BlazorCacheApp.Services.DemoService;

public class WeatherForecast
{
    public DateTime Date { get; init; }
    public int TemperatureC { get; init; }
    public string? Summary { get; init; }
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

public class WeatherForecastService
{
    private static readonly string[] Summaries =
    [
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    ];

    // Added to track API calls for demo purposes
    public int ApiCallCount { get; private set; }
    
    public async Task<WeatherForecast[]> GetForecastAsync(DateTime startDate)
    {
        // Simulate API latency
        await Task.Delay(1000);
        
        // Increment counter
        ApiCallCount++;
        
        var rng = new Random();
        return Enumerable.Range(1, 5).Select(index => new WeatherForecast
        {
            Date = startDate.AddDays(index),
            TemperatureC = rng.Next(-20, 55),
            Summary = Summaries[rng.Next(Summaries.Length)]
        }).ToArray();
    }
}