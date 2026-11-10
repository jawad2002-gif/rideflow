using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;

namespace RideFlow.Realtime;

public class OfferConsumer : BackgroundService
{
    private readonly IHubContext<RideHub> _hub;
    private readonly ILogger<OfferConsumer> _logger;

    public OfferConsumer(IHubContext<RideHub> hub, ILogger<OfferConsumer> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Run(() => Consume(ct), ct);

    private async Task Consume(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = "realtime-offers",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe("ride.offered");
        _logger.LogInformation("Consuming ride.offered");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                using var doc = JsonDocument.Parse(result.Message.Value);
                var data = doc.RootElement.GetProperty("data");

                var driverId = data.GetProperty("driverId").GetString()!;
                var pickup = data.GetProperty("pickup");

                var payload = new
                {
                    tripId = data.GetProperty("tripId").GetString(),
                    driverId,
                    pickupLat = pickup.GetProperty("lat").GetDouble(),
                    pickupLng = pickup.GetProperty("lng").GetDouble(),
                    expiresAt = data.GetProperty("expiresAt").GetDateTime()
                };

                await _hub.Clients.Groups($"user-{driverId}", "debug-offers")
                    .SendAsync("RideOffered", payload, ct);

                _logger.LogInformation("RideOffered pushed to {DriverId}", driverId);
                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing offer");
            }
        }
        consumer.Close();
    }
}