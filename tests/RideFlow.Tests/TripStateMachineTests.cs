using FluentAssertions;
using RideFlow.TripService;
using Xunit;

namespace RideFlow.Tests;

public class TripStateMachineTests
{
    [Fact]
    public void New_trip_starts_as_Requested()
    {
        new Trip().Status.Should().Be(TripStatus.Requested);
    }

    [Fact]
    public void Happy_path_reaches_Paid()
    {
        var trip = new Trip();
        foreach (var s in new[]
        {
            TripStatus.Matching, TripStatus.DriverAssigned, TripStatus.DriverArrived,
            TripStatus.InProgress, TripStatus.Completed, TripStatus.Paid
        })
            trip.TransitionTo(s);

        trip.Status.Should().Be(TripStatus.Paid);
    }

    [Fact]
    public void Cannot_skip_states()
    {
        var trip = new Trip();
        var act = () => trip.TransitionTo(TripStatus.Completed);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Driver_cancel_returns_trip_to_Matching()
    {
        var trip = new Trip();
        trip.TransitionTo(TripStatus.Matching);
        trip.TransitionTo(TripStatus.DriverAssigned);
        trip.TransitionTo(TripStatus.Matching);
        trip.Status.Should().Be(TripStatus.Matching);
    }

    [Fact]
    public void Failed_payment_can_be_retried()
    {
        var trip = new Trip();
        foreach (var s in new[]
        {
            TripStatus.Matching, TripStatus.DriverAssigned, TripStatus.DriverArrived,
            TripStatus.InProgress, TripStatus.Completed, TripStatus.PaymentFailed, TripStatus.Paid
        })
            trip.TransitionTo(s);

        trip.Status.Should().Be(TripStatus.Paid);
    }

    [Theory]
    [InlineData(TripStatus.Paid)]
    [InlineData(TripStatus.NoDriverFound)]
    [InlineData(TripStatus.CancelledByRider)]
    public void Final_states_cannot_change(TripStatus finalState)
    {
        var trip = NewTripIn(finalState);
        var act = () => trip.TransitionTo(TripStatus.Matching);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Matching_without_driver_ends_as_NoDriverFound()
    {
        var trip = new Trip();
        trip.TransitionTo(TripStatus.Matching);
        trip.TransitionTo(TripStatus.NoDriverFound);
        trip.Status.Should().Be(TripStatus.NoDriverFound);
    }

    private static Trip NewTripIn(TripStatus target)
    {
        var trip = new Trip();
        switch (target)
        {
            case TripStatus.Paid:
                foreach (var s in new[] { TripStatus.Matching, TripStatus.DriverAssigned, TripStatus.DriverArrived,
                    TripStatus.InProgress, TripStatus.Completed, TripStatus.Paid })
                    trip.TransitionTo(s);
                break;
            case TripStatus.NoDriverFound:
                trip.TransitionTo(TripStatus.Matching);
                trip.TransitionTo(TripStatus.NoDriverFound);
                break;
            case TripStatus.CancelledByRider:
                trip.TransitionTo(TripStatus.CancelledByRider);
                break;
        }
        return trip;
    }
}
