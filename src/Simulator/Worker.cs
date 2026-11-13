using System.Text.Json;
using Confluent.Kafka;

namespace RideFlow.Simulator;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private const string Topic = "driver.location";
    private const double CenterLat = 52.5200;
    private const double CenterLng = 13.4050;
    private const int FakeDrivers = 0;   // set to 20 later for a busy map

    public Worker(ILogger<Worker> logger) => _logger = logger;

    private class Driver { public string Id = ""; public double Lat, Lng, Heading, Speed; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = "localhost:9092", EnableIdempotence = true, Acks = Acks.All
        }).Build();
        var rnd = new Random();

        var drivers = Enumerable.Range(1, FakeDrivers).Select(i => new Driver
        {
            Id = $"driver-{i}",
            Lat = CenterLat + (rnd.NextDouble() - 0.5) * 0.05,
            Lng = CenterLng + (rnd.NextDouble() - 0.5) * 0.05,
            Heading = rnd.NextDouble() * 360,
            Speed = 8 + rnd.NextDouble() * 8
        }).ToList();

        var file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "real-drivers.txt"));
        if (File.Exists(file))
            foreach (var id in File.ReadAllLines(file).Where(l => l.Trim().Length > 0))
                drivers.Add(new Driver { Id = id.Trim(), Lat = CenterLat, Lng = CenterLng, Heading = 0, Speed = 1 });

        _logger.LogInformation("Simulating {Count} drivers (file: {File})", drivers.Count, file);

        while (!ct.IsCancellationRequested)
        {
            foreach (var d in drivers)
            {
                d.Heading = (d.Heading + (rnd.NextDouble() - 0.5) * 30 + 360) % 360;
                var rad = d.Heading * Math.PI / 180;
                d.Lat += d.Speed * 2 * Math.Cos(rad) / 111_320.0;
                d.Lng += d.Speed * 2 * Math.Sin(rad) / (111_320.0 * Math.Cos(d.Lat * Math.PI / 180));

                var evt = new
                {
                    eventId = Guid.NewGuid().ToString(), type = "driver.location", version = 1,
                    occurredAt = DateTime.UtcNow, correlationId = d.Id,
                    data = new { driverId = d.Id, lat = Math.Round(d.Lat, 6), lng = Math.Round(d.Lng, 6),
                                 heading = Math.Round(d.Heading, 1), speed = Math.Round(d.Speed, 1), timestamp = DateTime.UtcNow }
                };
                await producer.ProduceAsync(Topic, new Message<string, string> { Key = d.Id, Value = JsonSerializer.Serialize(evt) }, ct);
            }
            await Task.Delay(2000, ct);
        }
    }
}
