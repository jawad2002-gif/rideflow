using System.Collections.Concurrent;

namespace RideFlow.Matching;

public class PendingOffer
{
    public string TripId = "";
    public double Lat, Lng;
    public int Attempt;
    public string? CurrentDriver;
    public DateTime ExpiresAt;
    public DateTime StartedAt = DateTime.UtcNow;
    public HashSet<string> Tried = new();
}

public class OfferStore
{
    public ConcurrentDictionary<string, PendingOffer> Pending { get; } = new();
}
