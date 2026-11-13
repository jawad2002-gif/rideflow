using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RideFlow.TripService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = "rideflow-auth",
            ValidateAudience = true, ValidAudience = "rideflow",
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("rideflow-dev-secret-key-change-me-0123456789abcdef")),
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddDbContext<TripDbContext>(o => o.UseNpgsql(
    "Host=localhost;Port=5432;Database=trips;Username=rideflow;Password=rideflow"));
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<MatchedConsumer>();
builder.Services.AddHostedService<PaymentConsumer>();
builder.Services.AddHostedService<MatchFailedConsumer>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<TripDbContext>().Database.EnsureCreated();

app.MapGet("/health/live", () => Results.Ok("alive"));

app.MapPost("/trips", async (CreateTripRequest req, TripDbContext db, ClaimsPrincipal user) =>
{
    var riderId = user.FindFirst("sub")?.Value ?? req.RiderId;
    var km = FareCalculator.DistanceKm(req.PickupLat, req.PickupLng, req.DropoffLat, req.DropoffLng);
    var surge = await PricingClient.GetSurgeAsync(req.PickupLat, req.PickupLng);
    var fare = Math.Round(FareCalculator.Fare(km) * (decimal)surge, 2);

    var trip = new Trip
    {
        RiderId = riderId,
        PickupLat = req.PickupLat,
        PickupLng = req.PickupLng,
        DropoffLat = req.DropoffLat,
        DropoffLng = req.DropoffLng,
        EstimatedFare = fare,
        Surge = surge
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
        trip.Id, trip.Status, trip.EstimatedFare, trip.Surge, distanceKm = Math.Round(km, 2)
    });
}).RequireAuthorization();

app.MapGet("/trips/{id:guid}", async (Guid id, TripDbContext db) =>
    await db.Trips.FindAsync(id) is { } t ? Results.Ok(t) : Results.NotFound());

app.MapPost("/trips/{id:guid}/arrived", async (Guid id, TripDbContext db) =>
{
    var trip = await db.Trips.FindAsync(id);
    if (trip is null) return Results.NotFound();
    try { trip.TransitionTo(TripStatus.DriverArrived); }
    catch (InvalidOperationException e) { return Results.Conflict(e.Message); }
    await db.SaveChangesAsync();
    return Results.Ok(new { trip.Id, trip.Status });
});

app.MapPost("/trips/{id:guid}/start", async (Guid id, TripDbContext db) =>
{
    var trip = await db.Trips.FindAsync(id);
    if (trip is null) return Results.NotFound();
    try { trip.TransitionTo(TripStatus.InProgress); }
    catch (InvalidOperationException e) { return Results.Conflict(e.Message); }
    AddEvent(db, "trip.started", trip, new { });
    await db.SaveChangesAsync();
    return Results.Ok(new { trip.Id, trip.Status });
});

app.MapPost("/trips/{id:guid}/complete", async (Guid id, TripDbContext db) =>
{
    var trip = await db.Trips.FindAsync(id);
    if (trip is null) return Results.NotFound();
    try { trip.TransitionTo(TripStatus.Completed); }
    catch (InvalidOperationException e) { return Results.Conflict(e.Message); }
    AddEvent(db, "trip.completed", trip, new { fare = trip.EstimatedFare });
    await db.SaveChangesAsync();
    return Results.Ok(new { trip.Id, trip.Status, fare = trip.EstimatedFare });
});

app.Run("http://localhost:5003");

static void AddEvent(TripDbContext db, string type, Trip trip, object extra)
{
    var envelope = new
    {
        eventId = Guid.NewGuid().ToString(),
        type,
        version = 1,
        occurredAt = DateTime.UtcNow,
        correlationId = $"trip-{trip.Id}",
        data = new { tripId = trip.Id, riderId = trip.RiderId, driverId = trip.DriverId, extra }
    };
    db.Outbox.Add(new OutboxMessage
    {
        Topic = type,
        Key = trip.Id.ToString(),
        Payload = JsonSerializer.Serialize(envelope)
    });
}

record CreateTripRequest(string RiderId, double PickupLat, double PickupLng,
                         double DropoffLat, double DropoffLng);
