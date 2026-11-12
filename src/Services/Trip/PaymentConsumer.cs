using System.Text.Json;
using Confluent.Kafka;

namespace RideFlow.TripService;

public class PaymentConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PaymentConsumer> _logger;

    public PaymentConsumer(IServiceScopeFactory scopes, ILogger<PaymentConsumer> logger)
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
            GroupId = "trip-payments-v2",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(new[] { "payment.processed", "payment.failed" });
        _logger.LogInformation("Trip: consuming payment results");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                using var doc = JsonDocument.Parse(result.Message.Value);
                var type = doc.RootElement.GetProperty("type").GetString();
                var tripId = Guid.Parse(doc.RootElement.GetProperty("data").GetProperty("tripId").GetString()!);
                _logger.LogInformation("Trip: received {Type} for {TripId}", type, tripId);

                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
                var trip = await db.Trips.FindAsync(new object[] { tripId }, ct);

                if (trip is not null &&
                    (trip.Status == TripStatus.Completed || trip.Status == TripStatus.PaymentFailed))
                {
                    var next = type == "payment.processed" ? TripStatus.Paid : TripStatus.PaymentFailed;
                    trip.TransitionTo(next);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Trip {TripId} -> {Status}", tripId, next);
                }
                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing payment result");
            }
        }
        consumer.Close();
    }
}
