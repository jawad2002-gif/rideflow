using System.Text.Json;
using Confluent.Kafka;

namespace RideFlow.Simulator;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private const int DriverCount = 20;
    private const string Topic = "driver.location";

    // Center of Berlin, as in the spec example
    private const double CenterLat = 52.5200;
    private const double CenterLng = 13.4050;

    public Worker(ILogger<Worker> logger) => _logger = logger;

    private class Driver
    {
        public string Id = "";
        public double Lat, Lng, Heading, Speed;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = "localhost:9092",
            EnableIdempotence = true,
            Acks = Acks.All
        };

        using var producer = new ProducerBuilder<string, string>(config).Build();
        var rnd = new Random();

        var drivers = Enumerable.Range(1, DriverCount).Select(i => new Driver
        {
            Id = $"driver-{i}",
            Lat = CenterLat + (rnd.NextDouble() - 0.5) * 0.05,
            Lng = CenterLng + (rnd.NextDouble() - 0.5) * 0.05,
            Heading = rnd.NextDouble() * 360,
            Speed = 8 + rnd.NextDouble() * 8 // m/s
        }).ToList();

        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "real-drivers.txt");
        if (File.Exists(file))
            foreach (var id in File.ReadAllLines(file).Where(l => l.Trim().Length > 0))
                drivers.Add(new Driver { Id = id.Trim(), Lat = CenterLat, Lng = CenterLng, Heading = 0, Speed = 3 });
        _logger.LogInformation("Simulating {Count} drivers", drivers.Count);

        while (!ct.IsCancellationRequested)
        {
            foreach (var d in drivers)
            {
                // wander: small random turn, then move forward 2 seconds worth
                d.Heading = (d.Heading + (rnd.NextDouble() - 0.5) * 30 + 360) % 360;
                var meters = d.Speed * 2;
                var rad = d.Heading * Math.PI / 180;
                d.Lat += meters * Math.Cos(rad) / 111_320.0;
                d.Lng += meters * Math.Sin(rad) / (111_320.0 * Math.Cos(d.Lat * Math.PI / 180));

                var envelope = new
                {
                    eventId = Guid.NewGuid().ToString(),
                    type = "driver.location",
                    version = 1,
                    occurredAt = DateTime.UtcNow,
                    correlationId = d.Id,
                    data = new
                    {
                        driverId = d.Id,
                        lat = Math.Round(d.Lat, 6),
                        lng = Math.Round(d.Lng, 6),
                        heading = Math.Round(d.Heading, 1),
                        speed = Math.Round(d.Speed, 1),
                        timestamp = DateTime.UtcNow
                    }
                };

                // key = driverId keeps one driver's pings ordered on one partition
                await producer.ProduceAsync(Topic, new Message<string, string>
                {
                    Key = d.Id,
                    Value = JsonSerializer.Serialize(envelope)
                }, ct);
            }

            _logger.LogInformation("Published {Count} pings", drivers.Count);
            await Task.Delay(2000, ct); // one ping every 2 seconds, as in the spec
        }
    }
}
