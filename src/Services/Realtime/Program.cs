using RideFlow.Realtime;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect("localhost:6379"));
builder.Services.AddSingleton<GeoIndexer>();
builder.Services.AddHostedService<LocationConsumer>();
builder.Services.AddHostedService<OfferConsumer>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.MapHub<RideHub>("/hubs/ride");
app.MapGet("/health/live", () => Results.Ok("alive"));

app.Run("http://localhost:5002");
