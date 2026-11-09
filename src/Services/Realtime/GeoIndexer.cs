using StackExchange.Redis;

namespace RideFlow.Realtime;

public class GeoIndexer
{
    private const string Key = "drivers:geo";
    private readonly IDatabase _db;

    public GeoIndexer(IConnectionMultiplexer redis) => _db = redis.GetDatabase();

    public Task UpdateAsync(string driverId, double lat, double lng) =>
        _db.GeoAddAsync(Key, new GeoEntry(lng, lat, driverId));
}