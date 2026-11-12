using System.Text.Json;
using Confluent.Kafka;
using RideFlow.Matching;

var builder = WebApplication.CreateBuilder(args);

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

app.MapGet("/health/live", () => Results.Ok("alive"));

app.MapPost("/offers/{tripId}/accept", async (string tripId, AcceptRequest req,
    IProducer<string, string> producer, OfferStore store) =>
{
    store.Pending.TryRemove(tripId, out _);
    var evt = new
    {
        eventId = Guid.NewGuid().ToString(),
        type = "ride.matched",
        version = 1,
        occurredAt = DateTime.UtcNow,
        correlationId = $"trip-{tripId}",
        data = new { tripId, driverId = req.DriverId }
    };
    await producer.ProduceAsync("ride.matched", new Message<string, string>
    {
        Key = tripId,
        Value = JsonSerializer.Serialize(evt)
    });
    return Results.Accepted();
});

app.MapPost("/offers/{tripId}/decline", async (string tripId, Worker worker, OfferStore store) =>
{
    if (!store.Pending.TryGetValue(tripId, out var offer)) return Results.NotFound();
    await worker.OfferToNext(offer, CancellationToken.None);
    return Results.Accepted();
});

app.Run("http://localhost:5004");

record AcceptRequest(string DriverId);
