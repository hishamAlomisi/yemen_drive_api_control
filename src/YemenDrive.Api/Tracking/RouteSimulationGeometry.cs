using YemenDrive.Shared.Api;

namespace YemenDrive.Api.Tracking;

public readonly record struct RoutePoint(double Latitude, double Longitude);
public sealed record RouteAdvance(RoutePoint Point, double Bearing, double DistanceMeters, double SnapDistanceMeters);

public static class RouteSimulationGeometry
{
    public static IReadOnlyList<RoutePoint> Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 1_000_000) return [];
        var points = new List<RoutePoint>();
        long latitude = 0, longitude = 0;
        var index = 0;
        try
        {
            while (index < encoded.Length)
            {
                latitude += Value(); longitude += Value();
                var point = new RoutePoint(latitude / 1e5, longitude / 1e5);
                if (Math.Abs(point.Latitude) > 90 || Math.Abs(point.Longitude) > 180) return [];
                if (points.Count == 0 || points[^1] != point) points.Add(point);
            }
        }
        catch (FormatException) { return []; }
        return points;

        long Value()
        {
            long result = 0; var shift = 0;
            while (true)
            {
                if (index >= encoded.Length || shift > 30) throw new FormatException();
                var value = encoded[index++] - 63;
                if (value is < 0 or > 63) throw new FormatException();
                result |= (long)(value & 31) << shift; shift += 5;
                if (value < 32) return (result & 1) == 0 ? result >> 1 : ~(result >> 1);
            }
        }
    }

    public static RouteAdvance Advance(IReadOnlyList<RoutePoint> route, RoutePoint current, double metres = 1)
    {
        if (route.Count < 2 || !double.IsFinite(metres) || metres <= 0)
            throw new ServiceException("simulation_route_missing", "لا يتوفر مسار قيادة صالح لهذه الرحلة.");
        var bestDistance = double.MaxValue; var segment = 0; var fraction = 0d;
        for (var i = 0; i < route.Count - 1; i++)
        {
            var cosine = Math.Max(0.000001, Math.Cos(current.Latitude * Math.PI / 180));
            var ax = (route[i].Longitude - current.Longitude) * cosine;
            var ay = route[i].Latitude - current.Latitude;
            var dx = (route[i + 1].Longitude - route[i].Longitude) * cosine;
            var dy = route[i + 1].Latitude - route[i].Latitude;
            var denominator = dx * dx + dy * dy;
            var t = denominator <= 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / denominator, 0, 1);
            var projection = Interpolate(route[i], route[i + 1], t);
            var distance = Distance(current, projection);
            if (distance < bestDistance) { bestDistance = distance; segment = i; fraction = t; }
        }
        if (bestDistance > 25)
            throw new ServiceException("simulation_off_route", "ضع السائق على مسار القيادة المرسوم ثم احفظ الموقع قبل تجربة التحرك.");
        var start = Interpolate(route[segment], route[segment + 1], fraction);
        var point = start; var remaining = metres; var travelled = 0d; var bearing = 0d;
        for (var i = segment; i < route.Count - 1; i++)
        {
            var end = route[i + 1]; var length = Distance(point, end);
            if (length < 0.00001) { point = end; continue; }
            bearing = Bearing(point, end);
            if (remaining <= length)
                return new(Interpolate(point, end, remaining / length), bearing, travelled + remaining, bestDistance);
            remaining -= length; travelled += length; point = end;
        }
        if (travelled < 0.001)
            throw new ServiceException("simulation_route_complete", "وصل السائق إلى نهاية المسار.");
        return new(point, bearing, travelled, bestDistance);
    }

    public static double Distance(RoutePoint a, RoutePoint b)
    {
        var lat = (b.Latitude - a.Latitude) * Math.PI / 180;
        var lng = (b.Longitude - a.Longitude) * Math.PI / 180;
        var h = Math.Pow(Math.Sin(lat / 2), 2) + Math.Cos(a.Latitude * Math.PI / 180) *
            Math.Cos(b.Latitude * Math.PI / 180) * Math.Pow(Math.Sin(lng / 2), 2);
        return 12_742_000 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
    }
    private static RoutePoint Interpolate(RoutePoint a, RoutePoint b, double fraction) =>
        new(a.Latitude + (b.Latitude - a.Latitude) * fraction, a.Longitude + (b.Longitude - a.Longitude) * fraction);
    private static double Bearing(RoutePoint a, RoutePoint b)
    {
        var aLat = a.Latitude * Math.PI / 180; var bLat = b.Latitude * Math.PI / 180;
        var delta = (b.Longitude - a.Longitude) * Math.PI / 180;
        return (Math.Atan2(Math.Sin(delta) * Math.Cos(bLat), Math.Cos(aLat) * Math.Sin(bLat) -
            Math.Sin(aLat) * Math.Cos(bLat) * Math.Cos(delta)) * 180 / Math.PI + 360) % 360;
    }
}
