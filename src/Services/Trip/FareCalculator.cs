namespace RideFlow.TripService;

public static class FareCalculator
{
    public static double DistanceKm(double lat1, double lng1, double lat2, double lng2)
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

    // fare = base + per_km * km, with a minimum fare
    public static decimal Fare(double km) =>
        Math.Max(4m, Math.Round(2.5m + 1.2m * (decimal)km, 2));
}
