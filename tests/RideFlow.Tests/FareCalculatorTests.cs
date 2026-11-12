using FluentAssertions;
using RideFlow.TripService;
using Xunit;

namespace RideFlow.Tests;

public class FareCalculatorTests
{
    [Fact]
    public void Minimum_fare_applies_to_very_short_trips()
    {
        FareCalculator.Fare(0).Should().Be(4m);
    }

    [Fact]
    public void Fare_follows_base_plus_per_km()
    {
        // 2.5 + 1.2 * 10 = 14.5
        FareCalculator.Fare(10).Should().Be(14.5m);
    }

    [Fact]
    public void Fare_matches_the_value_seen_in_the_live_system()
    {
        // the 1.89 km test trip was priced 4.77
        FareCalculator.Fare(1.89).Should().Be(4.77m);
    }

    [Fact]
    public void Distance_between_same_point_is_zero()
    {
        FareCalculator.DistanceKm(52.52, 13.405, 52.52, 13.405).Should().BeApproximately(0, 0.001);
    }

    [Fact]
    public void Distance_for_the_demo_trip_is_about_1_89_km()
    {
        FareCalculator.DistanceKm(52.52, 13.405, 52.5163, 13.3777)
            .Should().BeApproximately(1.89, 0.02);
    }
}
