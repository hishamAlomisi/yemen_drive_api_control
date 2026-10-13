using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;

public sealed record RideAvailableRecipient(
    int UserId,
    IReadOnlyDictionary<string, object?> Ride);

public sealed record RideOfferRealtimeEvent(
    int Id,
    int RideId,
    int DriverId,
    string? DriverName,
    string? VehicleModel,
    string? PhotoUrl,
    decimal Amount,
    OfferStatus Status,
    DateTime ExpiresAtUtc,
    DateTime CreatedAtUtc,
    string? Note,
    RideStatus RideStatus,
    int? AssignedDriverId,
    IReadOnlyDictionary<string, object?>? RideSnapshot = null);

public sealed record RideStatusRealtimeEvent(
    int RideId,
    int CustomerId,
    int? DriverId,
    RideStatus Status,
    bool CustomerPaymentEnabled,
    DateTime? UpdatedAtUtc);

public sealed record DriverLocationRealtimeEvent(
    int DriverId,
    int? RideId,
    int? CustomerId,
    double Latitude,
    double Longitude,
    double? Bearing,
    double? Speed,
    double Accuracy,
    DateTime ObservedAtUtc,
    DateTime ReceivedAtUtc,
    RideStatus? RideStatus);

public sealed record SafetyIncidentRealtimeEvent(
    int Id,
    int? RideId,
    SafetyIncidentStatus Status,
    double? Latitude,
    double? Longitude,
    DateTime? LocationObservedAtUtc,
    string LocationSource,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SafetyRecordingRealtimeEvent(
    int Id,
    int? RideId,
    long DurationMilliseconds,
    long UploadedBytes,
    DateTime CompletedAtUtc);

public sealed record RideMessageRealtimeEvent(
    int Id,
    int RideId,
    int SenderId,
    int RecipientId,
    string MessageType,
    string Content,
    DateTime CreatedAtUtc);

/// <summary>
/// A minimal invalidation event. Clients reload authoritative payment state
/// through the API instead of trusting financial data carried by the Hub.
/// </summary>
public sealed record PaymentRealtimeEvent(
    int RideId,
    string Change,
    DateTime UpdatedAtUtc,
    int? PaymentId = null,
    int? CashPaymentRequestId = null,
    int? CashCollectionApprovalId = null);

/// <summary>
/// Minimal notification metadata. Clients reload notification content from the API.
/// </summary>
public sealed record NotificationRealtimeEvent(
    int UserId,
    int NotificationId,
    NotificationType Type,
    DateTime CreatedAtUtc);

public interface IRideRealtimePublisher
{
    Task PublishRideAvailableAsync(
        IReadOnlyCollection<RideAvailableRecipient> recipients,
        CancellationToken cancellationToken);

    Task PublishRideUnavailableAsync(
        int rideId,
        int customerUserId,
        IReadOnlyCollection<int> driverUserIds,
        CancellationToken cancellationToken);

    Task PublishOfferReceivedAsync(
        int customerUserId,
        RideOfferRealtimeEvent offer,
        CancellationToken cancellationToken);

    Task PublishOfferUpdatesAsync(
        int customerUserId,
        IReadOnlyCollection<RideOfferRealtimeEvent> offers,
        CancellationToken cancellationToken);

    Task PublishRideStatusUpdatedAsync(
        RideStatusRealtimeEvent rideStatus,
        CancellationToken cancellationToken);

    Task PublishDriverLocationAsync(
        DriverLocationRealtimeEvent location,
        CancellationToken cancellationToken);

    Task PublishRideMessageCreatedAsync(
        RideMessageRealtimeEvent message,
        CancellationToken cancellationToken);

    Task PublishPaymentUpdatedAsync(
        IReadOnlyCollection<int> userIds,
        PaymentRealtimeEvent payment,
        CancellationToken cancellationToken);

    Task PublishCashApprovalUpdatedAsync(
        IReadOnlyCollection<int> userIds,
        PaymentRealtimeEvent approval,
        CancellationToken cancellationToken);

    Task PublishNotificationsCreatedAsync(
        IReadOnlyCollection<NotificationRealtimeEvent> notifications,
        CancellationToken cancellationToken);

    Task PublishSafetyIncidentCreatedAsync(
        SafetyIncidentRealtimeEvent incident,
        CancellationToken cancellationToken);

    Task PublishSafetyIncidentUpdatedAsync(
        SafetyIncidentRealtimeEvent incident,
        CancellationToken cancellationToken);

    Task PublishSafetyRecordingAvailableAsync(
        SafetyRecordingRealtimeEvent recording,
        CancellationToken cancellationToken);

    Task PublishRideShareRevokedAsync(
        int shareId,
        int rideId,
        CancellationToken cancellationToken);
}

internal static class RideRealtimeEventFactory
{
    public static RideStatusRealtimeEvent Status(
        YemenDrive.Database.Entities.Ride ride) => new(
            ride.Id,
            ride.CustomerId,
            ride.DriverId,
            ride.Status,
            ride.CustomerPaymentEnabled,
            ride.UpdatedAtUtc);

    public static async Task<IReadOnlyList<RideOfferRealtimeEvent>> LoadOffersAsync(
        YemenDriveDbContext db,
        IEnumerable<int> offerIds,
        CancellationToken cancellationToken)
    {
        var ids = offerIds.Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<RideOfferRealtimeEvent>();

        var offers = await db.RideOffers.AsNoTracking()
            .Where(offer => ids.Contains(offer.Id))
            .Include(offer => offer.Driver)
                .ThenInclude(driver => driver.DriverProfile)
            .Include(offer => offer.Ride)
                .ThenInclude(ride => ride.Customer)
            .Include(offer => offer.Ride)
                .ThenInclude(ride => ride.ServiceKind)
            .Include(offer => offer.Ride)
                .ThenInclude(ride => ride.ServiceCatalogItem)
            .ToListAsync(cancellationToken);

        return offers.Select(offer => new RideOfferRealtimeEvent(
            offer.Id,
            offer.RideId,
            offer.DriverId,
            offer.Driver.DisplayName,
            offer.Driver.DriverProfile?.VehicleModel,
            offer.Driver.DriverProfile?.PhotoUrl,
            offer.Amount,
            offer.Status,
            offer.ExpiresAtUtc,
            offer.CreatedAtUtc,
            offer.Note,
            offer.Ride.Status,
            offer.Ride.DriverId,
            offer.Ride.DriverId == offer.DriverId
                ? AssignedRideSnapshot(offer)
                : null)).ToArray();
    }

    private static IReadOnlyDictionary<string, object?> AssignedRideSnapshot(
        YemenDrive.Database.Entities.RideOffer offer)
    {
        var ride = offer.Ride;
        var pickupName = RideLocationText.DisplayName(
            ride.PickupLabel, ride.PickupAddress, "نقطة الانطلاق");
        var destinationName = RideLocationText.DisplayName(
            ride.DestinationLabel, ride.DestinationAddress, "الوجهة");
        return new Dictionary<string, object?>
        {
            ["id"] = ride.Id,
            ["customerId"] = ride.CustomerId,
            ["customerName"] = ride.Customer.DisplayName,
            ["customerPhone"] = ride.Customer.PhoneNumber,
            ["driverId"] = ride.DriverId,
            ["driverName"] = offer.Driver.DisplayName,
            ["status"] = (int)ride.Status,
            ["serviceKindId"] = ride.ServiceKindId,
            ["serviceCatalogItemId"] = ride.ServiceCatalogItemId,
            ["serviceKindCode"] = ride.ServiceKind.Code,
            ["serviceKindNameAr"] = ride.ServiceKind.NameAr,
            ["serviceCode"] = ride.ServiceCatalogItem.Code,
            ["serviceNameAr"] = ride.ServiceCatalogItem.NameAr,
            ["pickupDisplayName"] = pickupName,
            ["destinationDisplayName"] = destinationName,
            ["pickupAddress"] = pickupName,
            ["destinationAddress"] = destinationName,
            ["pickupLatitude"] = ride.PickupLatitude,
            ["pickupLongitude"] = ride.PickupLongitude,
            ["destinationLatitude"] = ride.DestinationLatitude,
            ["destinationLongitude"] = ride.DestinationLongitude,
            ["destinationAvailable"] = true,
            ["customerPrice"] = ride.CustomerPrice,
            ["serviceFee"] = ride.ServiceFee,
            ["totalAmount"] = ride.TotalAmount,
            ["driverCommissionAmount"] = ride.DriverCommissionAmount,
            ["driverShare"] = ride.DriverShare,
            ["platformShare"] = ride.PlatformShare,
            ["createdAtUtc"] = ride.CreatedAtUtc,
            ["customerPaymentEnabled"] = ride.CustomerPaymentEnabled,
            ["paymentCompleted"] = false,
            ["driverOfferAmount"] = offer.Amount,
            ["driverOfferExpiresAtUtc"] = offer.ExpiresAtUtc,
            ["driverOfferStatus"] = (int)offer.Status,
            ["cashPaymentRequestId"] = null,
            ["cashPaymentRequestStatus"] = null
        };
    }
}
