using System.Text.Json;
using Confluent.Kafka;
using StackExchange.Redis;

namespace RideFlow.Matching;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IDatabase _redis;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _redis = ConnectionMultiplexer.Connect("localhost:6379").GetDatabase();
    }

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Run(() => Run(ct), ct);

    private async Task Run(CancellationToken ct)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = "matching-service",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = "localhost:9092",
            EnableIdempotence = true,
            Acks = Acks.All
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        using var producer = new ProducerBuilder<string, string>(producerConfig).Build();
        consumer.Subscribe("ride.requested");
        _logger.LogInformation("Matching: consuming ride.requested");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                using var doc = JsonDocument.Parse(result.Message.Value);
                var data = doc.RootElement.GetProperty("data");

                var tripId = data.GetProperty("tripId").GetString()!;
                var pickup = data.GetProperty("pickup");
                var lat = pickup.GetProperty("lat").GetDouble();
                var lng = pickup.GetProperty("lng").GetDouble();

                var found = await _redis.GeoSearchAsync(
                    "drivers:geo",
                    lng, lat,
                    new GeoSearchCircle(5, GeoUnit.Kilometers),
                    count: 1,
                    order: Order.Ascending);

                if (found.Length == 0)
                {
                    _logger.LogWarning("Trip {TripId}: no drivers within 5 km", tripId);
                }
                else
                {
                    var driverId = found[0].Member.ToString();
                    var offer = new
                    {
                        eventId = Guid.NewGuid().ToString(),
                        type = "ride.offered",
                        version = 1,
                        occurredAt = DateTime.UtcNow,
                        correlationId = $"trip-{tripId}",
                        data = new
                        {
                            tripId,
                            driverId,
                            pickup = new { lat, lng },
                            expiresAt = DateTime.UtcNow.AddSeconds(15)
                        }
                    };

                    await producer.ProduceAsync("ride.offered", new Message<string, string>
                    {
                        Key = tripId,
                        Value = JsonSerializer.Serialize(offer)
                    }, ct);

                    _logger.LogInformation("Trip {TripId}: offered to {DriverId}", tripId, driverId);
                }

                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Matching failed for a message");
            }
        }
        consumer.Close();
    }
}