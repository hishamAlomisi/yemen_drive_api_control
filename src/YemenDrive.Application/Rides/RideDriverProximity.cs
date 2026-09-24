using YemenDrive.Database.Entities;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

/// <summary>
/// Determines the radius in which an open ride is offered. The driver app
/// polls open rides, so a request expands automatically without creating a
/// second ride or resending it to every driver at once.
/// </summary>
internal static class RideDriverProximity
{
    public static int RadiusMeters(RideEntity ride, DateTime utcNow)
    {
        var age = utcNow - ride.CreatedAtUtc;
        if (age < TimeSpan.FromMinutes(1)) return 3000;
        if (age < TimeSpan.FromMinutes(2)) return 6000;
        return 10000;
    }

    public static bool IsWithinRadius(RideEntity ride, double latitude, double longitude, DateTime utcNow) =>
        HaversineMeters(ride.PickupLatitude, ride.PickupLongitude, latitude, longitude) <= RadiusMeters(ride, utcNow);

    public static double HaversineMeters(double firstLatitude, double firstLongitude,
        double secondLatitude, double secondLongitude)
    {
        const double earthRadiusMeters = 6371000d;
        var latitudeDelta = DegreesToRadians(secondLatitude - firstLatitude);
        var longitudeDelta = DegreesToRadians(secondLongitude - firstLongitude);
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
                Math.Cos(DegreesToRadians(firstLatitude)) * Math.Cos(DegreesToRadians(secondLatitude)) *
                Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return 2 * earthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
