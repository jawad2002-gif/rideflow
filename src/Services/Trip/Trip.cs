namespace RideFlow.TripService;

public enum TripStatus
{
    Requested, Matching, DriverAssigned, DriverArrived, InProgress,
    Completed, Paid, NoDriverFound, CancelledByRider, CancelledByDriver, PaymentFailed
}

public class Trip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RiderId { get; set; } = "";
    public string? DriverId { get; set; }
    public double PickupLat { get; set; }
    public double PickupLng { get; set; }
    public double DropoffLat { get; set; }
    public double DropoffLng { get; set; }
    public decimal EstimatedFare { get; set; }
    public double Surge { get; set; } = 1.0;
    public TripStatus Status { get; private set; } = TripStatus.Requested;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // optimistic concurrency token (Postgres xmin)
    public uint Version { get; set; }

    private static readonly Dictionary<TripStatus, TripStatus[]> Allowed = new()
    {
        [TripStatus.Requested]      = [TripStatus.Matching, TripStatus.CancelledByRider],
        [TripStatus.Matching]       = [TripStatus.DriverAssigned, TripStatus.NoDriverFound, TripStatus.CancelledByRider],
        [TripStatus.DriverAssigned] = [TripStatus.DriverArrived, TripStatus.Matching, TripStatus.CancelledByRider],
        [TripStatus.DriverArrived]  = [TripStatus.InProgress, TripStatus.CancelledByRider],
        [TripStatus.InProgress]     = [TripStatus.Completed],
        [TripStatus.Completed]      = [TripStatus.Paid, TripStatus.PaymentFailed],
        [TripStatus.PaymentFailed]  = [TripStatus.Paid],
        [TripStatus.Paid]             = [],
        [TripStatus.NoDriverFound]    = [],
        [TripStatus.CancelledByRider] = [],
        [TripStatus.CancelledByDriver] = [],
    };

    public void TransitionTo(TripStatus next)
    {
        if (!Allowed[Status].Contains(next))
            throw new InvalidOperationException($"Illegal transition {Status} -> {next}");
        Status = next;
    }
}