using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Api;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using Ride = YemenDrive.Database.Entities.Ride;

static class DirectVisualCancellationScenarios
{
    public static async Task RunAsync(DbContextOptions<YemenDriveDbContext> options, int customerId,
        int kindId, int catalogId, IRideRealtimePublisher publisher)
    {
        await using var seed = new YemenDriveDbContext(options);
        var driver = new User { PhoneNumber = "direct-slow-client-driver", Role = UserRole.Driver };
        var ride = new Ride { CustomerId = customerId, Driver = driver, ServiceKindId = kindId,
            ServiceCatalogItemId = catalogId, Status = RideStatus.InProgress };
        seed.Rides.Add(ride);
        seed.DriverProfiles.Add(new DriverProfile { User = driver, ServiceKindId = kindId,
            ServiceCatalogItemId = catalogId, IsAvailable = true, IsApproved = true });
        await seed.SaveChangesAsync();
        var subscriptions = new NearbyDriverSubscriptionStore();
        subscriptions.Replace("slow-viewer", new(kindId, catalogId, 15.36, 44.19, 1000, [driver.Id]));
        using var stopping = new CancellationTokenSource();
        using var rescue = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationReachedTransport = false;
        var hub = new TestHubContext();
        hub.Proxy.OnSend = token =>
        {
            cancellationReachedTransport = token.CanBeCanceled;
            entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, token).WaitAsync(rescue.Token);
        };
        var at = DateTime.UtcNow.AddSeconds(-1);
        var ingestion = new DriverLocationIngestionService(seed, publisher, subscriptions, hub,
            new TestLifetime(stopping.Token), NullLogger<DriverLocationIngestionService>.Instance);
        var save = ingestion.SaveAsync(driver.Id, new(ride.Id, 15.36, 44.19, null, 1, 5, at));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stopping.Cancel();
        try { await save.WaitAsync(TimeSpan.FromSeconds(1)); throw new InvalidOperationException("Slow transport ignored cancellation"); }
        catch (OperationCanceledException) { }
        if (!cancellationReachedTransport) throw new InvalidOperationException("Direct nearby transport lost its cancellation token");
        await using var verify = new YemenDriveDbContext(options);
        if (!await verify.LocationUpdates.AnyAsync(x => x.ActorId == driver.Id && x.ObservedAtUtc == at) ||
            !await verify.DriverLiveLocations.AnyAsync(x => x.DriverId == driver.Id && x.ObservedAtUtc == at))
            throw new InvalidOperationException("Cancelling a viewer rolled back an already committed GPS point");
        Console.WriteLine("PASS: direct slow nearby send honors cancellation while its committed live point and route archive remain saved.");
        var next = new DriverLocationIngestionService(verify, publisher, new(), new TestHubContext(),
            new TestLifetime(), NullLogger<DriverLocationIngestionService>.Instance);
        await next.SaveAsync(driver.Id, new(ride.Id, 15.36001, 44.19, null, 1, 5, at.AddSeconds(1))).WaitAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine("PASS: cancelled direct viewer releases the per-driver gate for the next GPS observation.");
    }
}
