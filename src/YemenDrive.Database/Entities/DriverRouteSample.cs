namespace YemenDrive.Database.Entities;

/// <summary>A durable receipt for an actual device sample, independent of map delivery.</summary>
public sealed class DriverRouteSample : Entity
{
    public int DriverId { get; set; }
    public int RideId { get; set; }
    public Guid SessionId { get; set; }
    public Guid SampleId { get; set; }
    public long Sequence { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Bearing { get; set; }
    public double? Speed { get; set; }
    public double Accuracy { get; set; }
    public DateTime ObservedAtUtc { get; set; }
}

public sealed class DriverRouteSession : Entity
{
    public int DriverId { get; set; }
    public int RideId { get; set; }
    public Guid SessionId { get; set; }
    public long LastContiguousSequence { get; set; }
    public long HighestSequence { get; set; }
}
