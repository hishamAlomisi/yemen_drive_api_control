using Microsoft.EntityFrameworkCore;
using YemenDrive.Api;
using YemenDrive.Api.Tracking;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using Ride = YemenDrive.Database.Entities.Ride;

static class VisualTransportScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, User driver, User customer, ServiceKind kind,
        RideServiceCatalogItem catalog, IRideRealtimePublisher publisher, RecordingProxy recorder, TestHubContext hub)
    {
        var subscriptions = new NearbyDriverSubscriptionStore();
        var transport = new DriverLocationVisualTransport(db, publisher, hub, subscriptions);
        var profile = await db.DriverProfiles.SingleAsync(x => x.UserId == driver.Id);
        profile.IsAvailable = true;
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var queued = new DriverVisualUpdate(new(driver.Id, null, null, 15.36, 44.19, null, 2, 5, now, now, null),
            new(kind.Id, catalog.Id, true, "test", null, null, null, 0, catalog.Code));
        void Known() { hub.Proxy.Messages.Clear(); subscriptions.Replace("nearby", new(kind.Id, catalog.Id, 15.36, 44.19, 1000, [driver.Id])); }
        bool RemovalOnly() => hub.Proxy.Messages.Count == 1 && hub.Proxy.Messages.Single().Method == "nearbyDriverAvailabilityChanged";

        Known();
        Check(await transport.SendAsync(queued, default) && hub.Proxy.Messages.Single().Method == "nearbyDriverLocationUpdated",
            "visual transport publishes a current authorized available driver");
        Known();
        profile.AcceptsNewRequests = false; await db.SaveChangesAsync();
        await transport.SendAsync(queued, default);
        Check(RemovalOnly(), "a queued available profile cannot resurrect a driver who stopped accepting requests");

        Known();
        driver.IsActive = false; await db.SaveChangesAsync();
        Check(!await transport.SendAsync(queued, default) && RemovalOnly(),
            "revoked driver access removes a known nearby marker without sending coordinates");

        Known();
        driver.IsActive = true; profile.AcceptsNewRequests = true;
        var assigned = new Ride { CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind.Id,
            ServiceCatalogItemId = catalog.Id, Status = RideStatus.DriverAssigned };
        db.Rides.Add(assigned); await db.SaveChangesAsync();
        await transport.SendAsync(queued, default);
        Check(RemovalOnly(), "assignment after enqueue prevents the old idle sample from advertising nearby availability");

        var onRide = queued with { Location = queued.Location with { RideId = assigned.Id, CustomerId = -1 } };
        Check(await transport.SendAsync(onRide, default) && recorder.Locations.Last().CustomerId == customer.Id,
            "delayed ride delivery resolves its current recipient and status from the database");
        var count = recorder.Locations.Count;
        assigned.Status = RideStatus.Completed; await db.SaveChangesAsync();
        Check(!await transport.SendAsync(onRide, default) && recorder.Locations.Count == count,
            "a ride completed while queued cannot receive its driver's later visual update");
    }

    private static void Check(bool valid, string description)
    {
        if (!valid) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }
}
