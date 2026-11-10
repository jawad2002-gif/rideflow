using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RideFlow.TripService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<TripDbContext>(o => o.UseNpgsql(
    "Host=localhost;Port=5432;Database=trips;Username=rideflow;Password=rideflow"));
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<MatchedConsumer>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// For now the schema is created automatically. EF migrations come later.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<TripDbContext>().Database.EnsureCreated();

app.MapGet("/health/live", () => Results.Ok("alive"));

app.MapPost("/trips", async (CreateTripRequest req, TripDbContext db) =>
{
    // Placeholder fare until the Pricing service exists
    var km = Haversine(req.PickupLat, req.PickupLng, req.DropoffLat, req.DropoffLng);
    var fare = Math.Max(4m, Math.Round(2.5m + 1.2m * (decimal)km, 2));

    var trip = new Trip
    {
        RiderId = req.RiderId,
        PickupLat = req.PickupLat,
        PickupLng = req.PickupLng,
        DropoffLat = req.DropoffLat,
        DropoffLng = req.DropoffLng,
        EstimatedFare = fare,
        Surge = 1.0
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

    // One SaveChanges = one transaction: the trip and its event are saved together or not at all
    await db.SaveChangesAsync();

    return Results.Created($"/trips/{trip.Id}", new
    {
        trip.Id, trip.Status, trip.EstimatedFare, trip.Surge, distanceKm = Math.Round(km, 2)
    });
});

app.MapGet("/trips/{id:guid}", async (Guid id, TripDbContext db) =>
    await db.Trips.FindAsync(id) is { } t ? Results.Ok(t) : Results.NotFound());

app.Run("http://localhost:5003");

static double Haversine(double lat1, double lng1, double lat2, double lng2)
{
    const double R = 6371;
    double ToRad(double d) => d * Math.PI / 180;
    var dLat = ToRad(lat2 - lat1);
    var dLng = ToRad(lng2 - lng1);
    var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
            Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
    return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
}

record CreateTripRequest(string RiderId, double PickupLat, double PickupLng,
                         double DropoffLat, double DropoffLng);