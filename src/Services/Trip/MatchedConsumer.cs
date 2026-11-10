using System.Text.Json;
using Confluent.Kafka;

namespace RideFlow.TripService;

public class MatchedConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MatchedConsumer> _logger;

    public MatchedConsumer(IServiceScopeFactory scopes, ILogger<MatchedConsumer> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Run(() => Consume(ct), ct);

    private async Task Consume(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = "trip-matched",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe("ride.matched");
        _logger.LogInformation("Trip: consuming ride.matched");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                using var doc = JsonDocument.Parse(result.Message.Value);
                var data = doc.RootElement.GetProperty("data");
                var tripId = Guid.Parse(data.GetProperty("tripId").GetString()!);
                var driverId = data.GetProperty("driverId").GetString()!;

                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
                var trip = await db.Trips.FindAsync(new object[] { tripId }, ct);

                if (trip is null)
                    _logger.LogWarning("Trip {TripId} not found", tripId);
                else
                {
                    if (trip.Status == TripStatus.Requested)
                        trip.TransitionTo(TripStatus.Matching);
                    trip.DriverId = driverId;
                    trip.TransitionTo(TripStatus.DriverAssigned);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Trip {TripId} -> DriverAssigned ({DriverId})", tripId, driverId);
                }
                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing ride.matched");
            }
        }
        consumer.Close();
    }
}