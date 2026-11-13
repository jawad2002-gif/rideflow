
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RideFlow.TripService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<TripDbContext>(o => o.UseNpgsql(
    "Host=localhost;Port=5432;Database=trips;Username=rideflow;Password=rideflow"));

builder.Services.AddHttpClient();

builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<MatchFailedConsumer>();
builder.Services.AddHostedService<MatchedConsumer>();
builder.Services.AddHostedService<PaymentConsumer>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

// Create database schema if it does not exist.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider
        .GetRequiredService<TripDbContext>()
        .Database.EnsureCreated();
}

app.MapGet("/health/live", () => Results.Ok("alive"));

// Create a trip using the actual Pricing Service quote.
app.MapPost("/trips", async (
    CreateTripRequest req,
    TripDbContext db,
    HttpClient http) =>
{
    var quoteUrl =
        "http://localhost:5006/quote" +
        $"?pickupLat={req.PickupLat.ToString(CultureInfo.InvariantCulture)}" +
        $"&pickupLng={req.PickupLng.ToString(CultureInfo.InvariantCulture)}" +
        $"&dropoffLat={req.DropoffLat.ToString(CultureInfo.InvariantCulture)}" +
        $"&dropoffLng={req.DropoffLng.ToString(CultureInfo.InvariantCulture)}";

    PricingQuote? quote;

    try
    {
        quote = await http.GetFromJsonAsync<PricingQuote>(quoteUrl);
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            title: "Pricing Service unavailable",
            detail: "Could not retrieve the fare from Pricing Service.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (TaskCanceledException)
    {
        return Results.Problem(
            title: "Pricing Service timeout",
            detail: "Pricing Service did not respond in time.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (JsonException)
    {
        return Results.Problem(
            title: "Invalid pricing response",
            detail: "Pricing Service returned an invalid quote.",
            statusCode: StatusCodes.Status502BadGateway);
    }

    if (quote is null)
    {
        return Results.Problem(
            title: "Empty pricing response",
            statusCode: StatusCodes.Status502BadGateway);
    }

    var trip = new Trip
    {
        RiderId = req.RiderId,
        PickupLat = req.PickupLat,
        PickupLng = req.PickupLng,
        DropoffLat = req.DropoffLat,
        DropoffLng = req.DropoffLng,
        EstimatedFare = quote.Fare,
        Surge = quote.Surge
    };

    db.Trips.Add(trip);

    var envelope = new
    {
        eventId = Guid.NewGuid().ToString(),
        type = "ride.requested",
        version = 1,
        occurredAt = DateTime.UtcNow,
        correlationId = $"trip-{trip.Id}",
        data = new
        {
            tripId = trip.Id,
            riderId = trip.RiderId,
            pickup = new { lat = trip.PickupLat, lng = trip.PickupLng },
            dropoff = new { lat = trip.DropoffLat, lng = trip.DropoffLng },
            estimatedFare = trip.EstimatedFare,
            surge = trip.Surge
        }
    };

    db.Outbox.Add(new OutboxMessage
    {
        Topic = "ride.requested",
        Key = trip.Id.ToString(),
        Payload = JsonSerializer.Serialize(envelope)
    });

    await db.SaveChangesAsync();

    return Results.Created($"/trips/{trip.Id}", new
    {
        trip.Id,
        trip.Status,
        trip.EstimatedFare,
        trip.Surge,
        distanceKm = quote.DistanceKm
    });
});

app.MapGet("/trips/{id:guid}", async (Guid id, TripDbContext db) =>
    await db.Trips.FindAsync(id) is { } t
        ? Results.Ok(t)
        : Results.NotFound());

static void AddEvent(
    TripDbContext db,
    string type,
    Trip trip,
    object extra)
{
    var envelope = new
    {
        eventId = Guid.NewGuid().ToString(),
        type,
        version = 1,
        occurredAt = DateTime.UtcNow,
        correlationId = $"trip-{trip.Id}",
        data = new
        {
            tripId = trip.Id,
            riderId = trip.RiderId,
            driverId = trip.DriverId,
            extra
        }
    };

    db.Outbox.Add(new OutboxMessage
    {
        Topic = type,
        Key = trip.Id.ToString(),
        Payload = JsonSerializer.Serialize(envelope)
    });
}

app.MapPost("/trips/{id:guid}/arrived", async (
    Guid id,
    TripDbContext db) =>
{
    var trip = await db.Trips.FindAsync(id);

    if (trip is null)
        return Results.NotFound();

    try
    {
        trip.TransitionTo(TripStatus.DriverArrived);
    }
    catch (InvalidOperationException e)
    {
        return Results.Conflict(e.Message);
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { trip.Id, trip.Status });
});

app.MapPost("/trips/{id:guid}/start", async (
    Guid id,
    TripDbContext db) =>
{
    var trip = await db.Trips.FindAsync(id);

    if (trip is null)
        return Results.NotFound();

    try
    {
        trip.TransitionTo(TripStatus.InProgress);
    }
    catch (InvalidOperationException e)
    {
        return Results.Conflict(e.Message);
    }

    AddEvent(db, "trip.started", trip, new { });

    await db.SaveChangesAsync();

    return Results.Ok(new { trip.Id, trip.Status });
});

app.MapPost("/trips/{id:guid}/complete", async (
    Guid id,
    TripDbContext db) =>
{
    var trip = await db.Trips.FindAsync(id);

    if (trip is null)
        return Results.NotFound();

    try
    {
        trip.TransitionTo(TripStatus.Completed);
    }
    catch (InvalidOperationException e)
    {
        return Results.Conflict(e.Message);
    }

    AddEvent(db, "trip.completed", trip, new
    {
        fare = trip.EstimatedFare
    });

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        trip.Id,
        trip.Status,
        fare = trip.EstimatedFare
    });
});

app.Run("http://localhost:5003");

// Matches the JSON returned by Pricing Service.
record PricingQuote(
    [property: JsonPropertyName("distanceKm")] double DistanceKm,
    [property: JsonPropertyName("baseFare")] decimal BaseFare,
    [property: JsonPropertyName("surge")] double Surge,
    [property: JsonPropertyName("fare")] decimal Fare,
    [property: JsonPropertyName("zone")] string Zone,
    [property: JsonPropertyName("isSurgeActive")] bool IsSurgeActive
);

record CreateTripRequest(
    string RiderId,
    double PickupLat,
    double PickupLng,
    double DropoffLat,
    double DropoffLng
);
