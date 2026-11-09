namespace YemenDrive.Api.Tracking;

public sealed class TrackingUpgradeSettings
{
    // Enable only after the additive migration and matching client validation.
    public bool DurableSamplesEnabled { get; set; }
    public bool BufferedVisualDeliveryEnabled { get; set; }
    public int VisualQueueCapacity { get; set; } = 1024;
    public int VisualDeliveryWorkers { get; set; } = 8;
    public int VisualDeliveryTimeoutMilliseconds { get; set; } = 3000;
}

public sealed record RouteSampleBatchRequest(int RideId, Guid SessionId, RouteSampleRequest[] Samples);
public sealed record RouteSampleRequest(Guid SampleId, long Sequence, double Latitude, double Longitude,
    double? Bearing, double? Speed, double Accuracy, DateTime ObservedAtUtc);
public sealed record RouteSampleBatchResult(bool Enabled, Guid[] AcceptedSampleIds,
    long LastContiguousSequence, bool LivePublished);
