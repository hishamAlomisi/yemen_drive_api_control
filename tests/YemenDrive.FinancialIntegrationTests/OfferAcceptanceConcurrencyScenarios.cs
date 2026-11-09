using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using OfferService = YemenDrive.Application.Rides.RideOffer;

internal static class OfferAcceptanceConcurrencyScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int kind, int catalog)
    {
        foreach (var compatibility in new[] { true, false })
        foreach (var firstOperation in new[] { "refresh", "accept", "reject" })
        {
            var refresh = firstOperation == "refresh";
            var reject = firstOperation == "reject";
            var suffix = firstOperation switch
            {
                "refresh" => compatibility ? "41" : "42",
                "accept" => compatibility ? "43" : "44",
                _ => compatibility ? "45" : "46"
            };
            var customer = new User { PhoneNumber = "+9677008810" + suffix, DisplayName = "Concurrent offer passenger", Role = UserRole.Customer };
            var driver = new User { PhoneNumber = "+9677008820" + suffix, DisplayName = "Concurrent offer driver", Role = UserRole.Driver };
            db.Users.AddRange(customer, driver); await db.SaveChangesAsync();
            db.DriverProfiles.Add(new DriverProfile { UserId = driver.Id, ServiceKindId = kind, ServiceCatalogItemId = catalog,
                IsAvailable = true, AcceptsNewRequests = true, IsApproved = true, ApprovedPassengerSeats = 4, PlateNumber = "RACE-" + suffix });
            db.DriverLiveLocations.Add(new DriverLiveLocation { DriverId = driver.Id, Latitude = 15, Longitude = 44, IsOnline = true, ObservedAtUtc = DateTime.UtcNow });
            var ride = new YemenDrive.Database.Entities.Ride { CustomerId = customer.Id, ServiceKindId = kind, ServiceCatalogItemId = catalog,
                Status = RideStatus.Searching, PickupLatitude = 15, PickupLongitude = 44, DestinationLatitude = 15.01, DestinationLongitude = 44.01,
                PickupAddress = "pickup", DestinationAddress = "destination", CustomerPrice = 1839, PaymentMethodCode = "cash" };
            db.Rides.Add(ride); await db.SaveChangesAsync();
            var initial = await Call(new OfferService(db, config, new Actor(driver.Id)), "add", new RideOfferModel(RideId: ride.Id, Amount: 1839));
            Check(initial.Success, initial.Code + ": " + initial.Message);
            var offerId = JsonSerializer.SerializeToElement(initial.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("id").GetInt32();
            var gate = new RaceGate();
            var failures = new ConcurrentBag<string>();
            var addOptions = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(db.Database.GetConnectionString())
                .AddInterceptors(new PauseBeforeCommand(gate, !refresh, failures, firstParticipant: true, reject: reject)).Options;
            var acceptOptions = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(db.Database.GetConnectionString())
                .AddInterceptors(new PauseBeforeCommand(gate, true, failures)).Options;
            await using var addDb = new YemenDriveDbContext(addOptions, RealtimeScenarioMode.Settings);
            await using var acceptDb = new YemenDriveDbContext(acceptOptions, RealtimeScenarioMode.Settings);
            await acceptDb.Database.OpenConnectionAsync();
            await acceptDb.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY LOW");
            EventHandler<FirstChanceExceptionEventArgs> capture = (_, observed) =>
            {
                if (observed.Exception is Microsoft.Data.SqlClient.SqlException sql)
                    failures.Add($"SQL {sql.Number}: {sql.Message}");
            };
            AppDomain.CurrentDomain.FirstChanceException += capture;
            ApiResult[] result;
            try
            {
                Task<ApiResult> Accept(YemenDriveDbContext context) => compatibility
                    ? Call(new RideOfferAction(context, config, new Actor(customer.Id)), "accept", new RideOfferActionModel(ride.Id, offerId))
                    : Call(new OfferService(context, config, new Actor(customer.Id)), "accept", new RideOfferModel(Id: offerId));
                var firstTask = refresh
                    ? Call(new OfferService(addDb, config, new Actor(driver.Id)), "add", new RideOfferModel(RideId: ride.Id, Amount: 1840))
                    : reject ? compatibility
                        ? Call(new RideOfferAction(addDb, config, new Actor(customer.Id)), "reject", new RideOfferActionModel(ride.Id, offerId))
                        : Call(new OfferService(addDb, config, new Actor(customer.Id)), "update", new RideOfferModel(Id: offerId, Status: OfferStatus.Rejected))
                    : Accept(addDb);
                await gate.AddReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                result = await Task.WhenAll(firstTask, Accept(acceptDb)).WaitAsync(TimeSpan.FromSeconds(50));
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= capture; }
            Console.WriteLine($"INFO: {(compatibility ? "compatibility" : "general")} concurrent {firstOperation}/accept codes={string.Join(',', result.Select(x => x.Code))}; errors={string.Join(" | ", failures.Distinct())}");
            Check(result.All(x => x.Code != "unexpected_error") && failures.IsEmpty, "concurrent offer operations must not deadlock");
            if (reject)
            {
                Check(result[0].Success && !result[1].Success && result[1].Code == "offer_not_pending", "rejection finishes first and acceptance returns the pending-state conflict");
                Check(await db.Rides.AsNoTracking().AnyAsync(x => x.Id == ride.Id && x.DriverId == null), "rejected offer is not assigned");
                Check(await db.RideOffers.AsNoTracking().AnyAsync(x => x.Id == offerId && x.Status == OfferStatus.Rejected), "rejection remains authoritative");
                continue;
            }
            if (refresh)
            {
                Check(result.All(x => x.Success), "refresh completes and acceptance succeeds afterwards");
                Check(await db.Rides.AsNoTracking().AnyAsync(x => x.Id == ride.Id && x.CustomerPrice == 1840), "acceptance uses the refreshed fare read after taking the lock");
            }
            else Check(result.Count(x => x.Success) == 1 && result.Single(x => !x.Success).Code is "offer_not_pending" or "ride_not_open_for_offers", "duplicate acceptance returns a precise conflict after one winner");
            Check(await db.Rides.AsNoTracking().AnyAsync(x => x.Id == ride.Id && x.DriverId == driver.Id && x.Status == RideStatus.DriverAssigned), "one authoritative assignment is persisted");
            Check(await db.RideOffers.AsNoTracking().CountAsync(x => x.RideId == ride.Id && x.Status == OfferStatus.Accepted) == 1, "one offer is accepted");
        }
        Console.WriteLine("PASS: concurrent offer refresh, rejection and acceptance use consistent lock order.");
    }

    private sealed class RaceGate
    {
        public TaskCompletionSource AddReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;
        public Task ArriveAsync(bool accept, CancellationToken token)
        {
            if (!accept) AddReady.TrySetResult();
            if (Interlocked.Increment(ref count) == 2) bothReady.TrySetResult();
            return bothReady.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        }
    }

    private sealed class PauseBeforeCommand(RaceGate gate, bool accept, ConcurrentBag<string> failures, bool firstParticipant = false, bool reject = false) : DbCommandInterceptor
    {
        private int paused;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var matches = reject ? command.CommandText.Contains("FROM [Rides]") && !command.CommandText.Contains("UPDLOCK,HOLDLOCK")
                : accept ? command.CommandText.Contains("[Rides] WITH (UPDLOCK,HOLDLOCK)")
                : command.CommandText.Contains("FROM [DriverProfiles]");
            if (matches && Interlocked.Exchange(ref paused, 1) == 0)
            {
                await gate.ArriveAsync(!firstParticipant, cancellationToken);
                // With the old order the competing accept acquires the ride
                // while rejection retains the offer's shared lock. With the
                // fixed order it waits on rejection's ride update lock.
                if (reject) await Task.Delay(200, cancellationToken);
            }
            return result;
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            failures.Add(eventData.Exception.GetType().Name + ": " + eventData.Exception.Message);
            return Task.CompletedTask;
        }
    }
    private static Task<ApiResult> Call<T>(ModelService<T> service, string operation, T model) where T : class =>
        service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), CancellationToken.None);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
}
