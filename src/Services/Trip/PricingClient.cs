namespace RideFlow.TripService;

public static class PricingClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    // If Pricing is down or slow, fall back to 1.0 so riders can still book.
    public static async Task<double> GetSurgeAsync(double lat, double lng)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://localhost:5006/surge?lat={lat}&lng={lng}");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("surge").GetDouble();
        }
        catch { return 1.0; }
    }
}
