using System.Text.Json;
using Confluent.Kafka;
using StackExchange.Redis;

namespace RideFlow.Matching;

public class Worker : BackgroundService
{
    private const int MaxAttempts = 3;
    private const int OfferSeconds = 15;
    private const int TotalSeconds = 60;

    private readonly ILogger<Worker> _logger;
    private readonly IDatabase _redis;
    private readonly OfferStore _store;
    private readonly IProducer<string, string> _producer;

    public Worker(ILogger<Worker> logger, OfferStore store, IProducer<string, string> producer)
    {
        _logger = logger;
        _store = store;
        _producer = producer;
        _redis = ConnectionMultiplexer.Connect("localhost:6379").GetDatabase();
    }

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(Task.Run(() => ConsumeRequests(ct), ct), Task.Run(() => WatchTimeouts(ct), ct));

    private async Task ConsumeRequests(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = "matching-service-v2",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe("ride.requested");
        _logger.LogInformation("Matching: consuming ride.requested");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                using var doc = JsonDocument.Parse(result.Message.Value);
                var data = doc.RootElement.GetProperty("data");
                var pickup = data.GetProperty("pickup");

                var offer = new PendingOffer
                {
                    TripId = data.GetProperty("tripId").GetString()!,
                    Lat = pickup.GetProperty("lat").GetDouble(),
                    Lng = pickup.GetProperty("lng").GetDouble()
                };
                _store.Pending[offer.TripId] = offer;
                await OfferToNext(offer, ct);
                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Matching failed for a message"); }
        }
        consumer.Close();
    }

    private async Task WatchTimeouts(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                foreach (var offer in _store.Pending.Values.ToList())
                    if (DateTime.UtcNow >= offer.ExpiresAt)
                    {
                        _logger.LogInformation("Trip {TripId}: {Driver} did not answer", offer.TripId, offer.CurrentDriver);
                        await OfferToNext(offer, ct);
                    }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Timeout watcher failed"); }
        }
    }

    // called by the decline endpoint too
    public async Task OfferToNext(PendingOffer offer, CancellationToken ct)
    {
        if (offer.CurrentDriver is not null) offer.Tried.Add(offer.CurrentDriver);

        var expired = (DateTime.UtcNow - offer.StartedAt).TotalSeconds > TotalSeconds;
        if (offer.Attempt >= MaxAttempts || expired)
        {
            await Fail(offer, ct);
            return;
        }

        var found = await _redis.GeoSearchAsync("drivers:geo", offer.Lng, offer.Lat,
            new GeoSearchCircle(5, GeoUnit.Kilometers), count: 10, order: Order.Ascending);
        var next = found.Select(f => f.Member.ToString()).FirstOrDefault(d => !offer.Tried.Contains(d));

        if (next is null)
        {
            await Fail(offer, ct);
            return;
        }

        offer.Attempt++;
        offer.CurrentDriver = next;
        offer.ExpiresAt = DateTime.UtcNow.AddSeconds(OfferSeconds);

        var evt = new
        {
            eventId = Guid.NewGuid().ToString(),
            type = "ride.offered",
            version = 1,
            occurredAt = DateTime.UtcNow,
            correlationId = $"trip-{offer.TripId}",
            data = new
            {
                tripId = offer.TripId,
                driverId = next,
                pickup = new { lat = offer.Lat, lng = offer.Lng },
                expiresAt = offer.ExpiresAt
            }
        };
        await _producer.ProduceAsync("ride.offered",
            new Message<string, string> { Key = offer.TripId, Value = JsonSerializer.Serialize(evt) }, ct);
        _logger.LogInformation("Trip {TripId}: offered to {DriverId} (attempt {Attempt})", offer.TripId, next, offer.Attempt);
    }

    private async Task Fail(PendingOffer offer, CancellationToken ct)
    {
        _store.Pending.TryRemove(offer.TripId, out _);
        var evt = new
        {
            eventId = Guid.NewGuid().ToString(),
            type = "ride.match.failed",
            version = 1,
            occurredAt = DateTime.UtcNow,
            correlationId = $"trip-{offer.TripId}",
            data = new { tripId = offer.TripId, reason = "NO_DRIVER_FOUND" }
        };
        await _producer.ProduceAsync("ride.match.failed",
            new Message<string, string> { Key = offer.TripId, Value = JsonSerializer.Serialize(evt) }, ct);
        _logger.LogWarning("Trip {TripId}: NO_DRIVER_FOUND", offer.TripId);
    }
}
