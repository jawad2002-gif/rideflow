using System.Text.Json;
using Confluent.Kafka;

namespace RideFlow.TripService;

public class MatchFailedConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MatchFailedConsumer> _logger;

    public MatchFailedConsumer(IServiceScopeFactory scopes, ILogger<MatchFailedConsumer> logger)
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
            GroupId = "trip-match-failed",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe("ride.match.failed");
        _logger.LogInformation("Trip: consuming ride.match.failed");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                using var doc = JsonDocument.Parse(result.Message.Value);
                var tripId = Guid.Parse(doc.RootElement.GetProperty("data").GetProperty("tripId").GetString()!);

                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
                var trip = await db.Trips.FindAsync(new object[] { tripId }, ct);

                if (trip is not null && trip.Status is TripStatus.Requested or TripStatus.Matching)
                {
                    if (trip.Status == TripStatus.Requested) trip.TransitionTo(TripStatus.Matching);
                    trip.TransitionTo(TripStatus.NoDriverFound);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Trip {TripId} -> NoDriverFound", tripId);
                }
                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Failed processing ride.match.failed"); }
        }
        consumer.Close();
    }
}
