using Microsoft.EntityFrameworkCore;

namespace RideFlow.TripService;

public class TripDbContext : DbContext
{
    public TripDbContext(DbContextOptions<TripDbContext> options) : base(options) { }

    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Trip>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Status).HasConversion<string>();
            e.Property(t => t.Version)
                .HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.PublishedAt);
        });
    }
}