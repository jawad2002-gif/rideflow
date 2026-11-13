using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;

namespace RideFlow.Realtime;

public class LocationConsumer : BackgroundService
{

   private readonly IHubContext<RideHub> _hub;
private readonly GeoIndexer _geo;
private readonly ILogger<LocationConsumer> _logger;

public LocationConsumer(IHubContext<RideHub> hub, GeoIndexer geo, ILogger<LocationConsumer> logger)
{
    _hub = hub;
    _geo = geo;
    _logger = logger;
}
    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Run(() => Consume(ct), ct);

    private async Task Consume(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = "realtime-service",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe("driver.location");
        _logger.LogInformation("Consuming driver.location");

        // throttle: at most one push per driver per second
        var lastSent = new Dictionary<string, DateTime>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                using var doc = JsonDocument.Parse(result.Message.Value);
                var data = doc.RootElement.GetProperty("data");

                var driverId = data.GetProperty("driverId").GetString()!;
                await _geo.UpdateAsync(driverId,
    data.GetProperty("lat").GetDouble(),
    data.GetProperty("lng").GetDouble());
                var now = DateTime.UtcNow;
                if (lastSent.TryGetValue(driverId, out var t) && (now - t).TotalSeconds < 1)
                {
                    consumer.Commit(result);
                    continue;
                }
                lastSent[driverId] = now;

                await _hub.Clients.Group("map").SendAsync("DriverLocationUpdated", new
                {
                    driverId,
                    lat = data.GetProperty("lat").GetDouble(),
                    lng = data.GetProperty("lng").GetDouble(),
                    heading = data.GetProperty("heading").GetDouble(),
                    speed = data.GetProperty("speed").GetDouble()
                }, ct);

                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing message");
            }
        }
        consumer.Close();
    }
}
