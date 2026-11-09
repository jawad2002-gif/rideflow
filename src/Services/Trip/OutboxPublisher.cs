using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;

namespace RideFlow.TripService;

public class OutboxPublisher : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(IServiceScopeFactory scopes, ILogger<OutboxPublisher> logger)
    {
        _scopes = scopes;
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
        using var producer = new ProducerBuilder<string, string>(config).Build();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();

                var pending = await db.Outbox
                    .Where(o => o.PublishedAt == null)
                    .OrderBy(o => o.CreatedAt)
                    .Take(50)
                    .ToListAsync(ct);

                foreach (var m in pending)
                {
                    await producer.ProduceAsync(m.Topic,
                        new Message<string, string> { Key = m.Key, Value = m.Payload }, ct);
                    m.PublishedAt = DateTime.UtcNow;
                    _logger.LogInformation("Published {Topic} key={Key}", m.Topic, m.Key);
                }

                if (pending.Count > 0) await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox publish failed, will retry");
            }

            await Task.Delay(500, ct);
        }
    }
}