using System.Text.Json;
using Confluent.Kafka;

namespace RideFlow.Payment;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private const double FailureRate = 0.0;

    public Worker(ILogger<Worker> logger) => _logger = logger;

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Run(() => Run(ct), ct);

    private async Task Run(CancellationToken ct)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = "payment-service-v2",
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
        consumer.Subscribe("trip.completed");
        var rnd = new Random();
        _logger.LogInformation("Payment: consuming trip.completed");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                using var doc = JsonDocument.Parse(result.Message.Value);
                var data = doc.RootElement.GetProperty("data");
                var tripId = data.GetProperty("tripId").GetString()!;
                var fare = data.GetProperty("extra").GetProperty("fare").GetDecimal();
                _logger.LogInformation("Payment: received trip.completed for {TripId}", tripId);

                var ok = rnd.NextDouble() >= FailureRate;
                var type = ok ? "payment.processed" : "payment.failed";

                var evt = new
                {
                    eventId = Guid.NewGuid().ToString(),
                    type,
                    version = 1,
                    occurredAt = DateTime.UtcNow,
                    correlationId = $"trip-{tripId}",
                    data = new { tripId, amount = fare, reason = ok ? null : "card_declined" }
                };

                await producer.ProduceAsync(type, new Message<string, string>
                {
                    Key = tripId,
                    Value = JsonSerializer.Serialize(evt)
                }, ct);

                _logger.LogInformation("Trip {TripId}: {Type} ({Fare})", tripId, type, fare);
                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment failed for a message");
            }
        }
        consumer.Close();
    }
}
