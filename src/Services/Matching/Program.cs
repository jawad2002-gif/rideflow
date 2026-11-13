using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using RideFlow.Matching;

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
builder.Services.AddAuthorization(o => o.AddPolicy("Driver", p => p.RequireRole("Driver")));

builder.Services.AddSingleton<OfferStore>();
builder.Services.AddSingleton(_ => new ProducerBuilder<string, string>(new ProducerConfig
{
    BootstrapServers = "localhost:9092",
    EnableIdempotence = true,
    Acks = Acks.All
}).Build());
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok("alive"));

app.MapPost("/offers/{tripId}/accept", async (string tripId, ClaimsPrincipal user,
    IProducer<string, string> producer, OfferStore store) =>
{
    var driverId = user.FindFirst("sub")?.Value;
    if (!store.Pending.TryGetValue(tripId, out var offer)) return Results.NotFound();
    if (offer.CurrentDriver != driverId) return Results.Forbid();

    store.Pending.TryRemove(tripId, out _);
    var evt = new
    {
        eventId = Guid.NewGuid().ToString(),
        type = "ride.matched",
        version = 1,
        occurredAt = DateTime.UtcNow,
        correlationId = $"trip-{tripId}",
        data = new { tripId, driverId }
    };
    await producer.ProduceAsync("ride.matched", new Message<string, string>
    {
        Key = tripId,
        Value = JsonSerializer.Serialize(evt)
    });
    return Results.Accepted();
}).RequireAuthorization("Driver");

app.MapPost("/offers/{tripId}/decline", async (string tripId, ClaimsPrincipal user, Worker worker, OfferStore store) =>
{
    var driverId = user.FindFirst("sub")?.Value;
    if (!store.Pending.TryGetValue(tripId, out var offer)) return Results.NotFound();
    if (offer.CurrentDriver != driverId) return Results.Forbid();

    await worker.OfferToNext(offer, CancellationToken.None);
    return Results.Accepted();
}).RequireAuthorization("Driver");

app.Run("http://localhost:5004");
