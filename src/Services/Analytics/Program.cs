using System.Collections.Concurrent;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Metrics>();
builder.Services.AddSignalR();
builder.Services.AddHostedService<EventConsumer>();
builder.Services.AddHostedService<MetricsPusher>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseCors();
app.MapHub<MetricsHub>("/hubs/metrics");
app.MapGet("/health/live", () => Results.Ok("alive"));
app.MapGet("/metrics/live", (Metrics m) => m.Snapshot());
app.Run("http://localhost:5005");

public class MetricsHub : Hub { }

public class MetricsPusher : BackgroundService
{
    private readonly Metrics _m;
    private readonly IHubContext<MetricsHub> _hub;
    public MetricsPusher(Metrics m, IHubContext<MetricsHub> hub) { _m = m; _hub = hub; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hub.Clients.All.SendAsync("DashboardMetrics", _m.Snapshot(), ct);
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}

public class Metrics
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, DateTime> _requestedAt = new();
    private readonly List<DateTime> _requestTimes = new();
    private readonly List<double> _waitSeconds = new();
    private readonly ConcurrentDictionary<string, DateTime> _drivers = new();
    private int _requested, _matched, _noDriver, _completed, _paid, _paymentFailed;
    private decimal _revenue;

    public void Requested(string tripId)
    {
        lock (_lock) { _requested++; _requestTimes.Add(DateTime.UtcNow); }
        _requestedAt[tripId] = DateTime.UtcNow;
    }

    public void Matched(string tripId)
    {
        lock (_lock)
        {
            _matched++;
            if (_requestedAt.TryRemove(tripId, out var t))
                _waitSeconds.Add((DateTime.UtcNow - t).TotalSeconds);
        }
    }

    public void NoDriver() { lock (_lock) _noDriver++; }
    public void Completed() { lock (_lock) _completed++; }
    public void Paid(decimal amount) { lock (_lock) { _paid++; _revenue += amount; } }
    public void PaymentFailed() { lock (_lock) _paymentFailed++; }
    public void DriverSeen(string id) => _drivers[id] = DateTime.UtcNow;

    public object Snapshot()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _requestTimes.RemoveAll(t => (now - t).TotalSeconds > 60);
            var activeDrivers = _drivers.Count(d => (now - d.Value).TotalSeconds < 10);
            var decided = _matched + _noDriver;
            return new
            {
                activeDrivers,
                activeTrips = Math.Max(0, _matched - _completed),
                tripsPerMinute = _requestTimes.Count,
                avgWaitSeconds = _waitSeconds.Count == 0 ? 0 : Math.Round(_waitSeconds.Average(), 1),
                matchRate = decided == 0 ? 0 : Math.Round(100.0 * _matched / decided, 1),
                requested = _requested,
                completed = _completed,
                noDriver = _noDriver,
                paymentFailed = _paymentFailed,
                revenue = Math.Round(_revenue, 2),
                at = now
            };
        }
    }
}

public class EventConsumer : BackgroundService
{
    private readonly Metrics _m;
    private readonly ILogger<EventConsumer> _log;
    public EventConsumer(Metrics m, ILogger<EventConsumer> log) { _m = m; _log = log; }

    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(() => Run(ct), ct);

    private void Run(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = "analytics-v1",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = true
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(new[] { "driver.location", "ride.requested", "ride.matched",
            "ride.match.failed", "trip.completed", "payment.processed", "payment.failed" });
        _log.LogInformation("Analytics: consuming business topics");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = consumer.Consume(ct);
                using var doc = JsonDocument.Parse(r.Message.Value);
                var data = doc.RootElement.GetProperty("data");
                switch (r.Topic)
                {
                    case "driver.location": _m.DriverSeen(data.GetProperty("driverId").GetString()!); break;
                    case "ride.requested": _m.Requested(data.GetProperty("tripId").GetString()!); break;
                    case "ride.matched": _m.Matched(data.GetProperty("tripId").GetString()!); break;
                    case "ride.match.failed": _m.NoDriver(); break;
                    case "trip.completed": _m.Completed(); break;
                    case "payment.processed": _m.Paid(data.GetProperty("amount").GetDecimal()); break;
                    case "payment.failed": _m.PaymentFailed(); break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Analytics failed for a message"); }
        }
        consumer.Close();
    }
}
