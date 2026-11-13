
using System.Collections.Concurrent;
using System.Text.Json;
using Confluent.Kafka;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SurgeEngine>();
builder.Services.AddHostedService<PricingConsumer>();
builder.Services.AddHostedService<SurgePublisher>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

app.MapGet("/health/live", () => Results.Ok("alive"));

// Calculate the ride quote
app.MapGet("/quote",
    (double pickupLat,
     double pickupLng,
     double dropoffLat,
     double dropoffLng,
     SurgeEngine engine) =>
{
    if (!IsValidCoordinates(pickupLat, pickupLng) ||
        !IsValidCoordinates(dropoffLat, dropoffLng))
    {
        return Results.BadRequest(new
        {
            error = "Invalid pickup or drop-off coordinates."
        });
    }

    var distanceKm = Haversine(
        pickupLat,
        pickupLng,
        dropoffLat,
        dropoffLng);

    decimal baseFare = Math.Max(
        4m,
        Math.Round(2.5m + 1.2m * (decimal)distanceKm, 2));

    double surge = engine.SurgeFor(pickupLat, pickupLng);

    decimal finalFare = Math.Round(
        baseFare * (decimal)surge,
        2,
        MidpointRounding.AwayFromZero);

    return Results.Ok(new
    {
        distanceKm = Math.Round(distanceKm, 2),
        baseFare,
        surge,
        fare = finalFare,
        zone = SurgeEngine.ZoneOf(pickupLat, pickupLng),
        isSurgeActive = surge > 1.0
    });
});

// Get surge multiplier for a pickup location
app.MapGet("/surge",
    (double lat, double lng, SurgeEngine engine) =>
{
    if (!IsValidCoordinates(lat, lng))
    {
        return Results.BadRequest(new
        {
            error = "Invalid coordinates."
        });
    }

    var multiplier = engine.SurgeFor(lat, lng);

    return Results.Ok(new
    {
        surge = multiplier,
        isSurgeActive = multiplier > 1.0,
        zone = SurgeEngine.ZoneOf(lat, lng)
    });
});

// Get active surge zones
app.MapGet("/surge/zones",
    (SurgeEngine engine) => Results.Ok(engine.Zones()));

// Manual surge override
// POST {"multiplier": 2}
// POST {"multiplier": 1}
// POST {"multiplier": null} to clear the override

app.MapPost("/surge/override",
    (OverrideRequest request, SurgeEngine engine) =>
{
    if (request.Multiplier is { } multiplier &&
        (double.IsNaN(multiplier) ||
         double.IsInfinity(multiplier)))
    {
        return Results.BadRequest(new
        {
            error = "Multiplier must be a valid number."
        });
    }

    engine.Override = request.Multiplier is null
        ? null
        : Math.Clamp(request.Multiplier.Value, 1.0, 3.0);

    return Results.Ok(new
    {
        overrideMultiplier = engine.Override,
        message = engine.Override is null
            ? "Surge override cleared."
            : $"Surge override set to {engine.Override}x."
    });
});

app.Run("http://localhost:5006");

static bool IsValidCoordinates(double lat, double lng)
{
    return double.IsFinite(lat) &&
           double.IsFinite(lng) &&
           lat >= -90 && lat <= 90 &&
           lng >= -180 && lng <= 180;
}

static double Haversine(
    double lat1,
    double lng1,
    double lat2,
    double lng2)
{
    const double EarthRadiusKm = 6371;

    double ToRad(double degrees) =>
        degrees * Math.PI / 180.0;

    var dLat = ToRad(lat2 - lat1);
    var dLng = ToRad(lng2 - lng1);

    var a =
        Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
        Math.Cos(ToRad(lat1)) *
        Math.Cos(ToRad(lat2)) *
        Math.Sin(dLng / 2) *
        Math.Sin(dLng / 2);

    // Protect against floating-point rounding errors
    a = Math.Clamp(a, 0.0, 1.0);

    return EarthRadiusKm *
           2 *
           Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
}

record OverrideRequest(double? Multiplier);

public class SurgeEngine
{
    private const double CellDegrees = 0.02;

    private static readonly TimeSpan DemandWindow =
        TimeSpan.FromMinutes(5);

    private static readonly TimeSpan DriverFreshness =
        TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<
        string, (string Zone, DateTime At)> _open = new();

    private readonly ConcurrentDictionary<
        string, (string Zone, DateTime Seen)> _drivers = new();

    private readonly object _overrideLock = new();

    private double? _override;

    public double? Override
    {
        get
        {
            lock (_overrideLock)
            {
                return _override;
            }
        }
        set
        {
            lock (_overrideLock)
            {
                _override = value;
            }
        }
    }

    public static string ZoneOf(double lat, double lng)
    {
        return $"{(int)Math.Floor(lat / CellDegrees)}:" +
               $"{(int)Math.Floor(lng / CellDegrees)}";
    }

    public void DriverSeen(string id, double lat, double lng)
    {
        _drivers[id] = (ZoneOf(lat, lng), DateTime.UtcNow);
    }

    public void RequestOpened(string tripId, double lat, double lng)
    {
        _open[tripId] = (ZoneOf(lat, lng), DateTime.UtcNow);
    }

    public void RequestClosed(string tripId)
    {
        _open.TryRemove(tripId, out _);
    }

    public double SurgeFor(double lat, double lng)
    {
        return Compute(ZoneOf(lat, lng));
    }

    public double Compute(string zone)
    {
        // Manual override takes priority
        var manualOverride = Override;

        if (manualOverride.HasValue)
        {
            return manualOverride.Value;
        }

        var now = DateTime.UtcNow;

        var demand = _open.Values.Count(request =>
            request.Zone == zone &&
            now - request.At < DemandWindow);

        if (demand == 0)
        {
            return 1.0;
        }

        var supply = _drivers.Values.Count(driver =>
            driver.Zone == zone &&
            now - driver.Seen < DriverFreshness);

        var ratio = (double)demand / Math.Max(supply, 1);

        var multiplier = 1.0 + 0.5 * ratio;

        return Math.Round(
            Math.Clamp(multiplier, 1.0, 3.0),
            1);
    }

    public IEnumerable<string> ActiveZones()
    {
        return _open.Values
            .Select(request => request.Zone)
            .Distinct()
            .ToList();
    }

    public object Zones()
    {
        return ActiveZones()
            .Select(zone => new
            {
                zone,
                surge = Compute(zone),
                isSurgeActive = Compute(zone) > 1.0
            })
            .ToList();
    }
}

public class PricingConsumer : BackgroundService
{
    private readonly SurgeEngine _engine;
    private readonly ILogger<PricingConsumer> _logger;

    public PricingConsumer(
        SurgeEngine engine,
        ILogger<PricingConsumer> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        return Task.Run(() => Run(ct), ct);
    }

    private void Run(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = "pricing-v1",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = true
        };

        using var consumer =
            new ConsumerBuilder<string, string>(config).Build();

        consumer.Subscribe(new[]
        {
            "driver.location",
            "ride.requested",
            "ride.matched",
            "ride.match.failed"
        });

        _logger.LogInformation(
            "Pricing Service consuming demand and supply events.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);

                using var document =
                    JsonDocument.Parse(result.Message.Value);

                var data = document.RootElement
                    .GetProperty("data");

                switch (result.Topic)
                {
                    case "driver.location":
                    {
                        var driverId =
                            data.GetProperty("driverId").GetString();

                        if (string.IsNullOrWhiteSpace(driverId))
                        {
                            break;
                        }

                        _engine.DriverSeen(
                            driverId,
                            data.GetProperty("lat").GetDouble(),
                            data.GetProperty("lng").GetDouble());

                        break;
                    }

                    case "ride.requested":
                    {
                        var pickup =
                            data.GetProperty("pickup");

                        var tripId =
                            data.GetProperty("tripId").GetString();

                        if (string.IsNullOrWhiteSpace(tripId))
                        {
                            break;
                        }

                        _engine.RequestOpened(
                            tripId,
                            pickup.GetProperty("lat").GetDouble(),
                            pickup.GetProperty("lng").GetDouble());

                        break;
                    }

                    case "ride.matched":
                    case "ride.match.failed":
                    {
                        var tripId =
                            data.GetProperty("tripId").GetString();

                        if (!string.IsNullOrWhiteSpace(tripId))
                        {
                            _engine.RequestClosed(tripId);
                        }

                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Pricing failed while processing a Kafka message.");
            }
        }

        consumer.Close();
    }
}

public class SurgePublisher : BackgroundService
{
    private readonly SurgeEngine _engine;
    private readonly ILogger<SurgePublisher> _logger;

    private readonly Dictionary<string, double> _last =
        new();

    public SurgePublisher(
        SurgeEngine engine,
        ILogger<SurgePublisher> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = "localhost:9092",
            EnableIdempotence = true,
            Acks = Acks.All
        };

        using var producer =
            new ProducerBuilder<string, string>(config).Build();

        _logger.LogInformation(
            "Surge Publisher started.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);

                var zones = _engine.ActiveZones()
                    .Union(_last.Keys)
                    .Distinct()
                    .ToList();

                foreach (var zone in zones)
                {
                    var multiplier = _engine.Compute(zone);

                    if (_last.TryGetValue(zone, out var previous) &&
                        previous == multiplier)
                    {
                        continue;
                    }

                    var evt = new
                    {
                        eventId = Guid.NewGuid().ToString(),
                        type = "surge.updated",
                        version = 1,
                        occurredAt = DateTime.UtcNow,
                        correlationId = $"zone-{zone}",
                        data = new
                        {
                            zoneId = zone,
                            multiplier,
                            isSurgeActive = multiplier > 1.0
                        }
                    };

                    await producer.ProduceAsync(
                        "surge.updated",
                        new Message<string, string>
                        {
                            Key = zone,
                            Value = JsonSerializer.Serialize(evt)
                        },
                        ct);

                    _last[zone] = multiplier;

                    _logger.LogInformation(
                        "Zone {Zone}: surge {Multiplier}x",
                        zone,
                        multiplier);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Surge publishing failed.");
            }
        }
    }
}
