using YemenDrive.Api.Tracking;

namespace YemenDrive.Api.Safety;

public sealed record RouteDeviationMeasurement(double DistanceMeters, double DirectionDegrees,
    string Direction, RoutePoint NearestPoint);

/// <summary>Distance to the complete agreed road route, never a freshly rerouted line from the driver.</summary>
public static class RouteDeviationGeometry
{
    public const double ThresholdMeters = 20;

    public static RouteDeviationMeasurement? Measure(string? polyline, double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180) return null;
        var route = RouteSimulationGeometry.Decode(polyline);
        if (route.Count < 2) return null;
        var point = new RoutePoint(latitude, longitude);
        var best = double.MaxValue;
        var nearest = route[0];
        // The local projection is used only to find a fraction of each road
        // segment; the final distance is a spherical great-circle distance.
        var cosine = Math.Max(0.000001, Math.Cos(latitude * Math.PI / 180));
        for (var index = 0; index < route.Count - 1; index++)
        {
            var a = route[index]; var b = route[index + 1];
            var ax = Wrap(a.Longitude - longitude) * cosine;
            var ay = a.Latitude - latitude;
            var dx = Wrap(b.Longitude - a.Longitude) * cosine;
            var dy = b.Latitude - a.Latitude;
            var length = dx * dx + dy * dy;
            var fraction = length <= 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / length, 0, 1);
            var projected = new RoutePoint(a.Latitude + fraction * dy,
                Wrap(a.Longitude + fraction * Wrap(b.Longitude - a.Longitude)));
            var distance = RouteSimulationGeometry.Distance(point, projected);
            if (distance < best) { best = distance; nearest = projected; }
        }
        var direction = Bearing(nearest, point);
        string[] names = ["الشمال", "الشمال الشرقي", "الشرق", "الجنوب الشرقي", "الجنوب", "الجنوب الغربي", "الغرب", "الشمال الغربي"];
        return new(best, direction, names[(int)Math.Floor((direction + 22.5) / 45) % 8], nearest);
    }

    public static bool IsDeviating(double distanceMeters) =>
        double.IsFinite(distanceMeters) && distanceMeters >= ThresholdMeters;

    private static double Wrap(double longitude) => (longitude + 540) % 360 - 180;
    private static double Bearing(RoutePoint a, RoutePoint b)
    {
        var aLat = a.Latitude * Math.PI / 180; var bLat = b.Latitude * Math.PI / 180;
        var delta = Wrap(b.Longitude - a.Longitude) * Math.PI / 180;
        return (Math.Atan2(Math.Sin(delta) * Math.Cos(bLat), Math.Cos(aLat) * Math.Sin(bLat) -
            Math.Sin(aLat) * Math.Cos(bLat) * Math.Cos(delta)) * 180 / Math.PI + 360) % 360;
    }
}
