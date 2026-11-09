using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YemenDrive.Application.Payments;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;

internal static class WaitingControlScenarios
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly WaitingClock Clock = new();

    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config,
        int customer, int driver, int admin, int kind, int catalog)
    {
        // Keep active/unpaid waiting fixtures separate from later booking and
        // settlement scenarios that intentionally reuse the suite participants.
        var phoneSuffix = Random.Shared.NextInt64(1_000_000, 9_999_999).ToString();
        var waitingCustomer = new User { PhoneNumber = "+9677040" + phoneSuffix, Role = UserRole.Customer };
        var waitingDriver = new User { PhoneNumber = "+9677041" + phoneSuffix, Role = UserRole.Driver };
        db.Users.AddRange(waitingCustomer, waitingDriver); await db.SaveChangesAsync();
        customer = waitingCustomer.Id; driver = waitingDriver.Id;
        var ride = await NewRide(db, customer, driver, kind, catalog);
        var rider = Service(db, config, customer);
        var captain = Service(db, config, driver);
        var model = new RideWaitingModel(ride.Id, RequestKey: Guid.NewGuid());
        var riderState = Data(await Call(rider, "get", model));
        Check(riderState.GetProperty("controlMode").GetString() == "driver" && !Flag(riderState, "canStart") &&
            !Flag(riderState, "canRequestResume") && !Flag(riderState, "canDecide"), "rider has no stop approval/request controls");
        Check(Flag(Data(await Call(captain, "get", model)), "canStart"), "stationary assigned driver can start");
        Code(await Call(rider, "add", model), "driver_action_required");
        Code(await Call(rider, "start", model), "driver_action_required");
        Code(await Call(Service(db, config, admin), "start", model), "ride_access_denied");
        var beforeStart = Clock.UtcNow;
        var started = Data(await Call(captain, "start", model));
        var sessionId = started.GetProperty("session").GetProperty("id").GetInt32();
        var action = model with { SessionId = sessionId };
        var session = await db.RideWaitingSessions.SingleAsync(x => x.Id == sessionId);
        Check(session.IsOpen && session.StartedAtUtc >= beforeStart && session.ApprovedByUserId is null &&
            Flag(started, "waitingStarted") && Flag(started, "canResume"), "start begins clock immediately without approval");
        await BothNotified(db, ride.Id, customer, driver, "بدأ السائق التوقف والانتظار", 1);
        Data(await Call(captain, "add", model));
        Check(await db.RideWaitingSessions.CountAsync(x => x.RideId == ride.Id) == 1, "start aliases and retries share one stop");
        await BothNotified(db, ride.Id, customer, driver, "بدأ السائق التوقف والانتظار", 1);
        Code(await Call(rider, "accept", action), "driver_action_required");
        Code(await Call(rider, "reject", action), "driver_action_required");
        Code(await Call(rider, "cancel", action), "waiting_stop_approval_removed");
        Code(await Call(rider, "update", action), "driver_action_required");
        Code(await Call(captain, "accept", action), "waiting_resume_request_required");

        session.StartedAtUtc = Clock.UtcNow.AddMinutes(-5);
        await RefreshLocation(db, driver); await db.SaveChangesAsync();
        var key1 = Guid.NewGuid();
        var request = action with { RequestKey = key1 };
        var requested = Data(await Call(rider, "request_resume", request));
        Check(Flag(requested, "waitingStarted") && Flag(requested, "resumeRequestPending") &&
            !Flag(requested, "canResume") && !Flag(requested, "canRequestResume") && requested.GetProperty("waitingFee").GetDecimal() >= 150,
            "rider resume request leaves timer and fee accrual running");
        Check(session.IsOpen && ride.WaitingFee == 0 && session.Fee == 0, "resume request never settles money");
        Data(await Call(rider, "request_resume", request));
        Check(RideWaitingResumeRequests.Read(session).Count == 1, "resume request retry creates one durable record");
        await BothNotified(db, ride.Id, customer, driver, "طلب الراكب استئناف الرحلة", 1);
        Code(await Call(rider, "request_resume", request with { RequestKey = Guid.NewGuid() }), "waiting_resume_already_pending");
        Code(await Call(captain, "request_resume", request), "rider_action_required");
        Code(await Call(rider, "accept_resume", action with { ResumeRequestKey = key1 }), "driver_action_required");
        Data(await Call(captain, "reject_resume", action with { ResumeRequestKey = key1 }));
        Data(await Call(captain, "reject", action with { ResumeRequestKey = key1 }));
        Check(session.IsOpen && Flag(Data(await Call(rider, "get", action)), "canRequestResume"), "rejection keeps stop active and permits a new rider request");
        await BothNotified(db, ride.Id, customer, driver, "لم يقبل السائق طلب الاستئناف", 1);
        var key2 = Guid.NewGuid();
        Data(await Call(rider, "request_resume", action with { RequestKey = key2 }));
        Data(await Call(rider, "request_resume", request));
        Check(RideWaitingResumeRequests.Read(session).Count == 2 &&
            RideWaitingResumeRequests.Read(session).Last().RequestKey == key2, "old rider retry cannot replace latest request");
        Code(await Call(captain, "accept_resume", action with { ResumeRequestKey = key1 }), "waiting_resume_request_mismatch");
        Check(session.IsOpen && RideWaitingResumeRequests.Read(session).Last().Decision is null, "stale driver decision cannot close newer request");
        var closed = Data(await Call(captain, "accept_resume", action with { ResumeRequestKey = key2 }));
        var settled = ride.WaitingFee;
        Check(!session.IsOpen && session.EndReason == "rider_resume_accepted" && settled == 150 &&
            !Flag(closed, "waitingStarted") && !Flag(closed, "resumeRequestPending"), "driver accepts request and settles agreed fee once");
        Data(await Call(captain, "accept", action with { ResumeRequestKey = key2 }));
        Data(await Call(captain, "resume", action));
        Data(await Call(rider, "request_resume", action with { RequestKey = key2 }));
        Check(ride.WaitingFee == settled && session.Fee == settled, "all closed retries preserve settled fee");
        Code(await Call(rider, "request_resume", action with { RequestKey = Guid.NewGuid() }), "waiting_not_active");
        await BothNotified(db, ride.Id, customer, driver, "انتهى الانتظار", 1);

        // A driver never needs a rider request to resume, including at the fee cap.
        var directRide = await NewRide(db, customer, driver, kind, catalog);
        var direct = Data(await Call(captain, "add", new(directRide.Id, RequestKey: Guid.NewGuid())));
        var directId = direct.GetProperty("session").GetProperty("id").GetInt32();
        var directSession = await db.RideWaitingSessions.SingleAsync(x => x.Id == directId);
        directSession.StartedAtUtc = Clock.UtcNow.AddMinutes(-5); await db.SaveChangesAsync();
        Data(await Call(captain, "update", new(directRide.Id, directId)));
        Data(await Call(captain, "resume", new(directRide.Id, directId)));
        Check(!directSession.IsOpen && directSession.EndReason == "driver_resumed" && directRide.WaitingFee == 150,
            "direct driver resume closes without any rider request");

        var legacyRide = await NewRide(db, customer, driver, kind, catalog);
        var legacy = new RideWaitingSession { RideId = legacyRide.Id, RequestedByUserId = customer,
            RequestKey = Guid.NewGuid(), FreeMinutes = 2, PricePerMinute = 50, MaxFee = 500 };
        db.RideWaitingSessions.Add(legacy); await db.SaveChangesAsync();
        Data(await Call(captain, "get", new(legacyRide.Id)));
        Check(!legacy.IsOpen && legacy.StartedAtUtc is null && legacy.Fee == 0 && legacy.EndReason == "stop_consent_removed",
            "legacy pending stop is closed without starting timer or fees");
        Data(await Call(captain, "start", new(legacyRide.Id, RequestKey: Guid.NewGuid())));
        Check(await db.RideWaitingSessions.CountAsync(x => x.RideId == legacyRide.Id && x.IsOpen) == 1,
            "driver replaces legacy pending stop atomically");

        var staleRide = await NewRide(db, customer, driver, kind, catalog);
        var staleLocation = await db.DriverLiveLocations.SingleAsync(x => x.DriverId == driver);
        staleLocation.ObservedAtUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        Check(!Flag(Data(await Call(captain, "get", new(staleRide.Id))), "canStart"), "stale GPS disables driver start");
        Code(await Call(captain, "start", new(staleRide.Id, RequestKey: Guid.NewGuid())), "waiting_location_required");
        Check(!await db.RideWaitingSessions.AnyAsync(x => x.RideId == staleRide.Id), "invalid start cannot generate a charge interval");
        await RefreshLocation(db, driver); await db.SaveChangesAsync();
        staleRide.ServiceEndedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync();
        Code(await Call(captain, "start", new(staleRide.Id, RequestKey: Guid.NewGuid())), "waiting_not_available");

        await RaceScenarios(db, config, customer, driver, kind, catalog);
        Console.WriteLine("PASS: immediate driver stop, rider resume-only permissions, both-party notices, request identity/retries, direct/approved resume, legacy compatibility and concurrent fee safeguards.");
    }

    private static async Task RaceScenarios(YemenDriveDbContext db, DatabaseConfigurationStore config,
        int customer, int driver, int kind, int catalog)
    {
        foreach (var race in new[] { "duplicate_start", "duplicate_resume_request", "different_resume_requests", "duplicate_resume", "start_resume", "request_resume", "accept_resume", "worker_resume" })
        {
            var ride = await NewRide(db, customer, driver, kind, catalog);
            var captain = Service(db, config, driver);
            var rider = Service(db, config, customer);
            var start = new RideWaitingModel(ride.Id, RequestKey: Guid.NewGuid());
            var sessionId = 0;
            var resumeKey = Guid.NewGuid();
            if (race != "duplicate_start")
            {
                sessionId = Data(await Call(captain, "start", start)).GetProperty("session").GetProperty("id").GetInt32();
                var session = await db.RideWaitingSessions.SingleAsync(x => x.Id == sessionId);
                session.StartedAtUtc = Clock.UtcNow.AddMinutes(-5); await RefreshLocation(db, driver); await db.SaveChangesAsync();
                if (race == "accept_resume") Data(await Call(rider, "request_resume", new(ride.Id, sessionId, resumeKey)));
                if (race == "worker_resume")
                {
                    session.MaxFee = 100; await db.SaveChangesAsync();
                }
            }
            var gate = new LockGate();
            DbContextOptions<YemenDriveDbContext> RaceOptions() => new DbContextOptionsBuilder<YemenDriveDbContext>()
                .UseSqlServer(db.Database.GetConnectionString()).AddInterceptors(new BeforeRideLock(gate)).Options;
            await using var firstDb = new YemenDriveDbContext(RaceOptions(), RealtimeScenarioMode.Settings);
            await using var secondDb = new YemenDriveDbContext(RaceOptions(), RealtimeScenarioMode.Settings);
            var action = new RideWaitingModel(ride.Id, sessionId, resumeKey, resumeKey);
            Task<ApiResult> first = race switch
            {
                "duplicate_start" => Call(Service(firstDb, config, driver), "start", start),
                "duplicate_resume_request" or "different_resume_requests" or "request_resume" => Call(Service(firstDb, config, customer), "request_resume", action),
                "start_resume" => Call(Service(firstDb, config, driver), "start", start with { RequestKey = Guid.NewGuid() }),
                "accept_resume" => Call(Service(firstDb, config, driver), "accept_resume", action),
                "worker_resume" => Worker(firstDb, ride.Id),
                _ => Call(Service(firstDb, config, driver), "resume", action)
            };
            Task<ApiResult> second = race switch
            {
                "duplicate_start" => Call(Service(secondDb, config, driver), "add", start),
                "duplicate_resume_request" => Call(Service(secondDb, config, customer), "request_resume", action),
                "different_resume_requests" => Call(Service(secondDb, config, customer), "request_resume", action with { RequestKey = Guid.NewGuid() }),
                _ => Call(Service(secondDb, config, driver), "resume", action)
            };
            var result = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine($"INFO: waiting race {race}: {string.Join(',', result.Select(x => x.Code))}");
            Check(result.All(x => x.Code != "unexpected_error"), "ride locks avoid deadlock: " + race);
            if (race == "duplicate_start")
            {
                Check(result.All(x => x.Success) && await db.RideWaitingSessions.AsNoTracking().CountAsync(x => x.RideId == ride.Id) == 1,
                    "concurrent same start request creates one interval");
                await BothNotified(db, ride.Id, customer, driver, "بدأ السائق التوقف والانتظار", 1);
            }
            else if (race is "duplicate_resume_request" or "different_resume_requests")
            {
                var state = await db.RideWaitingSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId);
                Check(state.IsOpen && state.Fee == 0 && RideWaitingResumeRequests.Read(state).Count == 1,
                    "concurrent rider requests leave one pending record and timer active");
                Check(race == "duplicate_resume_request" ? result.All(x => x.Success) :
                    result.Count(x => x.Success) == 1 && result.Any(x => x.Code == "waiting_resume_already_pending"),
                    "concurrent rider request has deterministic retry/conflict semantics");
                await BothNotified(db, ride.Id, customer, driver, "طلب الراكب استئناف الرحلة", 1);
            }
            else
            {
                var state = await db.RideWaitingSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId);
                var currentRide = await db.Rides.AsNoTracking().SingleAsync(x => x.Id == ride.Id);
                Check(!state.IsOpen && state.Fee == (race == "worker_resume" ? 100 : 150) && currentRide.WaitingFee == state.Fee,
                    "racing resume settles fee once: " + race);
                if (race is "duplicate_resume" or "accept_resume" or "worker_resume") Check(result.All(x => x.Success), "duplicate resume operations are idempotent");
                if (race == "start_resume") Check(result[1].Success && (result[0].Success || result[0].Code == "waiting_already_open"), "new stop follows a serial start/resume order");
                if (race == "request_resume") Check(result[1].Success && (result[0].Success || result[0].Code == "waiting_not_active"), "rider request never reopens a resumed stop");
                await BothNotified(db, ride.Id, customer, driver, "انتهى الانتظار", 1);
            }
        }
    }

    private static async Task<ApiResult> Worker(YemenDriveDbContext db, int rideId)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await RidePaymentGuard.LockAsync(db, rideId, default);
        var ride = await db.Rides.SingleAsync(x => x.Id == rideId);
        await new RideWaitingCoordinator(db).ReconcileAsync(ride, DateTime.UtcNow, default);
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return ApiResult.Ok(null);
    }

    private static async Task<RideEntity> NewRide(YemenDriveDbContext db, int customer, int driver, int kind, int catalog)
    {
        await RefreshLocation(db, driver);
        Clock.UtcNow = DateTime.UtcNow;
        var ride = new RideEntity { CustomerId = customer, DriverId = driver, ServiceKindId = kind, ServiceCatalogItemId = catalog,
            Status = RideStatus.InProgress, CustomerPrice = 1000, ServiceFee = 100, TotalAmount = 1100,
            WaitingFreeMinutes = 2, WaitingPricePerMinute = 50, WaitingMaxFee = 500, StartedAtUtc = DateTime.UtcNow.AddMinutes(-10) };
        db.Rides.Add(ride); await db.SaveChangesAsync(); return ride;
    }

    private static async Task RefreshLocation(YemenDriveDbContext db, int driver)
    {
        var location = await db.DriverLiveLocations.SingleOrDefaultAsync(x => x.DriverId == driver);
        if (location is null) { location = new() { DriverId = driver }; db.DriverLiveLocations.Add(location); }
        location.Latitude = 15.36; location.Longitude = 44.20; location.Speed = 0; location.IsOnline = true; location.ObservedAtUtc = DateTime.UtcNow;
    }

    private static async Task BothNotified(YemenDriveDbContext db, int rideId, int customer, int driver, string title, int count)
    {
        var records = await db.Notifications.AsNoTracking().Where(x => x.Title == title).ToListAsync();
        records = records.Where(x => x.DataJson is not null && JsonDocument.Parse(x.DataJson).RootElement.GetProperty("rideId").GetInt32() == rideId).ToList();
        Check(records.Count(x => x.UserId == customer) == count && records.Count(x => x.UserId == driver) == count,
            "notify both participants once per transition: " + title);
    }

    private sealed class LockGate
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;
        public Task Arrive(CancellationToken ct)
        {
            if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult();
            return ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
    }

    private sealed class BeforeRideLock(LockGate gate) : DbCommandInterceptor
    {
        private int paused;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[Rides] WITH (UPDLOCK,HOLDLOCK)") && Interlocked.Exchange(ref paused, 1) == 0)
                await gate.Arrive(cancellationToken);
            return result;
        }
    }

    private static RideWaiting Service(YemenDriveDbContext db, DatabaseConfigurationStore config, int actor) => new(db, config, new Actor(actor), new RideWaitingCoordinator(db), Clock);
    private static Task<ApiResult> Call(RideWaiting service, string operation, RideWaitingModel model) => service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), default);
    private static JsonElement Data(ApiResult result) { Check(result.Success, result.Code + ": " + result.Message); return JsonSerializer.SerializeToElement(result.Data, JsonOptions); }
    private static bool Flag(JsonElement data, string name) => data.GetProperty(name).GetBoolean();
    private static void Code(ApiResult result, string expected) => Check(!result.Success && result.Code == expected, "expected " + expected + ", got " + result.Code);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
    private sealed class WaitingClock : TimeProvider
    {
        public DateTime UtcNow { get; set; } = DateTime.UtcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }
}
