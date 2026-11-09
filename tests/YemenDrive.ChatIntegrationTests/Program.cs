using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Api;
using YemenDrive.Application.Administration;
using YemenDrive.Application.Communication;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Database.Realtime;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

var databaseName = "YemenDrive_ChatTests_" + Guid.NewGuid().ToString("N");
var options = new DbContextOptionsBuilder<YemenDriveDbContext>()
    .UseSqlServer($"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True").Options;
var settingsFile = Path.Combine(Path.GetTempPath(), databaseName + ".json");
var config = new DatabaseConfigurationStore(settingsFile);
await config.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB", Database = databaseName, IntegratedSecurity = true, TrustServerCertificate = true }, default);
var settings = new RealtimeJournalSettings { Enabled = true };
var count = 0;
try
{
    await using var db = new YemenDriveDbContext(options, settings);
    await db.GetService<IMigrator>().MigrateAsync("20261103200000_RideOfferRealtimeAudienceIndex");
    await db.Database.ExecuteSqlRawAsync("INSERT INTO CommunicationMessages (RideId, SenderId, RecipientId, MessageType, Content, CreatedAtUtc, ReadAtUtc) VALUES (999, 998, 997, N'Text', N'legacy read', '2026-11-01', '2026-11-02'), (999, 998, 997, N'Text', N'legacy unread', '2026-11-01', NULL)");
    await db.Database.MigrateAsync();
    Check(!db.Database.HasPendingModelChanges(), "migration snapshot matches model");
    var legacy = await db.CommunicationMessages.AsNoTracking().OrderBy(message => message.Id).ToListAsync();
    Check(legacy[0].DeliveredAtUtc == legacy[0].ReadAtUtc && legacy[1].DeliveredAtUtc == null, "migration preserves known receipts without inventing delivery");
    await db.Database.ExecuteSqlRawAsync("DELETE FROM CommunicationMessages WHERE RideId = 999");
    var rider = new User { PhoneNumber = "967700771001", DisplayName = "راكب الاختبار", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "967700771002", DisplayName = "سائق الاختبار", Role = UserRole.Driver };
    var outsider = new User { PhoneNumber = "967700771003", DisplayName = "طرف آخر", Role = UserRole.Customer };
    var admin = new User { PhoneNumber = "967700771004", DisplayName = "إدارة مفوضة", Role = UserRole.Admin };
    var limited = new User { PhoneNumber = "967700771005", DisplayName = "إدارة دون تفويض", Role = UserRole.Admin };
    var kind = new ServiceKind { Code = "chat-test", NameAr = "اختبار" };
    db.AddRange(rider, driver, outsider, admin, limited, kind);
    await db.SaveChangesAsync();
    var service = new RideServiceCatalogItem { Code = "chat-car", NameAr = "سيارة", DescriptionAr = "اختبار", ServiceKindId = kind.Id };
    db.Add(service); await db.SaveChangesAsync();
    db.AdministratorPermissions.Add(new() { UserId = admin.Id, Permission = "operations.read" });
    var ride = new Ride { CustomerId = rider.Id, DriverId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id, Status = RideStatus.DriverAssigned, PickupLabel = "الانطلاق", DestinationLabel = "الوجهة" };
    var secondRide = new Ride { CustomerId = rider.Id, DriverId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id, Status = RideStatus.InProgress };
    db.AddRange(ride, secondRide); await db.SaveChangesAsync();
    var transport = new RecordingHub<TrackingHub>();
    var publisher = new SignalRRideRealtimePublisher(transport, new RecordingHub<SharedRideHub>(), db, NullLogger<SignalRRideRealtimePublisher>.Instance);
    var riderMessages = new RideMessage(db, config, new Actor(rider.Id), publisher);
    var driverMessages = new RideMessage(db, config, new Actor(driver.Id), publisher);
    var driverReport = new RideMessageReport(db, config, new Actor(driver.Id));
    var sent = Ok(await Run(riderMessages, "add", new { rideId = ride.Id, content = "أنا في نقطة الانطلاق" }));
    var messageId = sent.GetProperty("id").GetInt32();
    Check(sent.GetProperty("readAtUtc").ValueKind == JsonValueKind.Null && sent.GetProperty("deliveredAtUtc").ValueKind == JsonValueKind.Null, "send records sent only, no delivery/read invention");
    var created = transport.Events.Single(item => item.Name == "rideMessageCreated");
    Check(created.Groups.Order().SequenceEqual(new[] { $"user:{rider.Id}", $"user:{driver.Id}" }.Order()), "new messages target both personal groups independently of ride subscriptions");
    var notification = await db.Notifications.AsNoTracking().SingleAsync(item => item.UserId == driver.Id);
    Check(JsonDocument.Parse(notification.DataJson!).RootElement.GetProperty("messageId").GetInt32() == messageId, "notification routes to persisted message id");
    var passiveList = Ok(await Run(driverReport, "list", new { rideId = ride.Id }));
    Check(passiveList.GetArrayLength() == 1 && passiveList[0].GetProperty("readAtUtc").ValueKind == JsonValueKind.Null, "fetching messages does not mark them read");
    Check(passiveList[0].GetProperty("createdAtUtc").GetString()!.EndsWith('Z'), "stored message timestamps reload with explicit UTC");
    Check(Ok(await Run(driverReport, "report", new { rideId = ride.Id })).GetProperty("unreadCount").GetInt32() == 1, "recipient has one unread message");
    var delivered = Ok(await Run(driverMessages, "update", new { id = messageId, markDelivered = true, markRead = false }));
    Check(delivered.GetProperty("deliveredAtUtc").ValueKind == JsonValueKind.String && delivered.GetProperty("readAtUtc").ValueKind == JsonValueKind.Null, "delivery ACK does not mark read");
    Check(transport.Events.Last().Name == "rideMessageUpdated", "receipt emits immediate update event");
    var read = Ok(await Run(driverMessages, "update", new { id = messageId, markRead = true }));
    var readAt = read.GetProperty("readAtUtc").GetDateTime();
    var deliveredAt = read.GetProperty("deliveredAtUtc").GetDateTime();
    var storedReceipt = Ok(await Run(driverReport, "list", new { rideId = ride.Id }))[0];
    Check(storedReceipt.GetProperty("deliveredAtUtc").GetString()!.EndsWith('Z') && storedReceipt.GetProperty("readAtUtc").GetString()!.EndsWith('Z'), "stored receipt timestamps retain UTC on reload");
    var receiptEventCount = transport.Events.Count;
    var repeated = Ok(await Run(driverMessages, "update", new { id = messageId, markRead = false, markDelivered = true }));
    Check(repeated.GetProperty("readAtUtc").GetDateTime() == readAt && repeated.GetProperty("deliveredAtUtc").GetDateTime() == deliveredAt && transport.Events.Count == receiptEventCount, "late and repeated ACKs preserve first receipts and emit no duplicate event");
    Check(Ok(await Run(driverReport, "report", new { rideId = ride.Id })).GetProperty("unreadCount").GetInt32() == 0, "read ACK clears unread badge");
    Fail(await Run(riderMessages, "update", new { id = messageId, markRead = true }), "message_access_denied", "sender cannot self-acknowledge");
    Fail(await Run(new RideMessageReport(db, config, new Actor(outsider.Id)), "list", new { rideId = ride.Id }), "ride_access_denied", "outsider cannot read participant chat");
    Fail(await Run(new RideMessageReport(db, config, new Actor(outsider.Id)), "report", new { rideId = ride.Id }), "ride_access_denied", "outsider cannot inspect unread metadata");
    Fail(await Run(new RideMessage(db, config, new Actor(outsider.Id)), "add", new { rideId = ride.Id, content = "unauthorized" }), "ride_access_denied", "outsider cannot post messages");
    var beforeBatchCount = await db.RealtimeStateEvents.CountAsync(item => item.Resource == "messages");
    var timestamp = DateTime.UtcNow;
    var bulk = Enumerable.Range(0, 125).Select(index => new CommunicationMessage { RideId = ride.Id, SenderId = rider.Id, RecipientId = driver.Id, Content = "رسالة " + index, CreatedAtUtc = timestamp }).ToArray();
    db.AddRange(bulk);
    var otherMessage = new CommunicationMessage { RideId = secondRide.Id, SenderId = driver.Id, RecipientId = rider.Id, Content = "رحلة ثانية" };
    db.Add(otherMessage); await db.SaveChangesAsync();
    Check(await db.RealtimeStateEvents.CountAsync(item => item.Resource == "messages") > beforeBatchCount, "chat changes are durable for reconnect recovery");
    var allUnread = Ok(await Run(driverReport, "report", new { }));
    Check(allUnread.GetProperty("unreadCount").GetInt32() == 125 && allUnread.GetProperty("rides").GetArrayLength() == 1, "unread total includes full history beyond a message page and remains user scoped");
    var deniedBatch = new { rideId = ride.Id, messageIds = new[] { bulk[0].Id, otherMessage.Id }, markRead = true };
    Fail(await Run(driverMessages, "update", deniedBatch), "message_access_denied", "cross-ride batch is rejected atomically");
    Check((await db.CommunicationMessages.AsNoTracking().SingleAsync(item => item.Id == bulk[0].Id)).ReadAtUtc == null, "invalid batch leaves allowed rows unchanged");
    var batch = Ok(await Run(driverMessages, "update", new { rideId = ride.Id, messageIds = bulk.Take(100).Select(item => item.Id).ToArray(), markDelivered = true, markRead = false }));
    Check(batch.GetArrayLength() == 100 && batch.EnumerateArray().All(item => item.GetProperty("readAtUtc").ValueKind == JsonValueKind.Null), "batch ACK handles 100 delivered messages without reading them");
    Fail(await Run(driverMessages, "update", new { rideId = ride.Id, messageIds = bulk.Select(item => item.Id).ToArray(), markRead = true }), "invalid_message_receipt", "oversized batch is rejected");
    var concurrentId = bulk[120].Id;
    await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
    {
        await using var isolated = new YemenDriveDbContext(options, settings);
        Ok(await Run(new RideMessage(isolated, config, new Actor(driver.Id)), "update", new { id = concurrentId, markDelivered = true, markRead = index % 2 == 0 }));
    }));
    var concurrent = await db.CommunicationMessages.AsNoTracking().SingleAsync(item => item.Id == concurrentId);
    Check(concurrent.ReadAtUtc != null && concurrent.DeliveredAtUtc <= concurrent.ReadAtUtc, "parallel delivery/read ACKs cannot regress receipts");
    var audit = new RideChatAudit(db, config, new Actor(admin.Id));
    Fail(await Run(new RideChatAudit(db, config, new Actor(outsider.Id)), "list", new { }), "administrator_permission_required", "customer cannot use admin audit directly");
    Fail(await Run(new RideChatAudit(db, config, new Actor(limited.Id)), "list", new { }), "administrator_permission_required", "admin without operations.read cannot inspect chats");
    var drivers = Ok(await Run(audit, "list", new { }));
    Check(drivers.GetArrayLength() == 1 && drivers[0].GetProperty("id").GetInt32() == driver.Id, "driver selector contains only drivers with conversations");
    var riders = Ok(await Run(audit, "search", new { driverId = driver.Id }));
    Check(riders.GetArrayLength() == 1 && riders[0].GetProperty("id").GetInt32() == rider.Id, "customer selector is scoped to selected driver");
    var grouped = Ok(await Run(audit, "report", new { driverId = driver.Id, customerId = rider.Id, pageSize = 1 }));
    Check(grouped.GetProperty("hasMore").GetBoolean() && grouped.GetProperty("rides")[0].GetProperty("rideId").GetInt32() == secondRide.Id, "audit groups and paginates conversations by ride");
    var older = Ok(await Run(audit, "report", new { driverId = driver.Id, customerId = rider.Id, beforeRideId = grouped.GetProperty("nextBeforeRideId").GetInt32() }));
    var firstRide = older.GetProperty("rides")[0];
    Check(firstRide.GetProperty("messageCount").GetInt32() == 126 && firstRide.GetProperty("messages").GetArrayLength() == 50 && firstRide.GetProperty("hasMoreMessages").GetBoolean(), "audit does not silently truncate long transcripts");
    Check(firstRide.GetProperty("nextBeforeCreatedAtUtc").GetString()!.EndsWith('Z'), "admin transcript cursor preserves UTC");
    var cursorInstant = firstRide.GetProperty("nextBeforeCreatedAtUtc").GetDateTime();
    var offsetCursor = new DateTimeOffset(cursorInstant).ToOffset(TimeSpan.FromHours(3)).ToString("O");
    var offsetPage = Ok(await Run(audit, "get", new { driverId = driver.Id, customerId = rider.Id, rideId = ride.Id, beforeId = firstRide.GetProperty("nextBeforeId").GetInt32(), beforeCreatedAtUtc = offsetCursor, pageSize = 50 }));
    Check(offsetPage.GetProperty("messages")[0].GetProperty("sequence").GetInt32() == 27 && offsetPage.GetProperty("messages")[49].GetProperty("sequence").GetInt32() == 76, "timezone-offset cursor is normalized before pagination");
    var accumulated = new List<JsonElement>(firstRide.GetProperty("messages").EnumerateArray());
    var cursor = firstRide;
    while (cursor.GetProperty("hasMoreMessages").GetBoolean())
    {
        cursor = Ok(await Run(audit, "get", new { driverId = driver.Id, customerId = rider.Id, rideId = ride.Id, beforeId = cursor.GetProperty("nextBeforeId").GetInt32(), beforeCreatedAtUtc = cursor.GetProperty("nextBeforeCreatedAtUtc").GetDateTime(), pageSize = 50 }));
        accumulated.InsertRange(0, cursor.GetProperty("messages").EnumerateArray());
    }
    Check(accumulated.Count == 126 && accumulated.Select(item => item.GetProperty("id").GetInt32()).Distinct().Count() == 126 && accumulated.Select(item => item.GetProperty("sequence").GetInt32()).SequenceEqual(Enumerable.Range(1, 126)), "timestamp ties paginate once each with absolute chronological sequence");
    Check(accumulated.All(item => item.GetProperty("senderRole").GetString() == "customer") && grouped.GetProperty("rides")[0].GetProperty("messages")[0].GetProperty("senderRole").GetString() == "driver", "audit distinguishes sender roles without inventing priority");
    Fail(await Run(audit, "get", new { driverId = outsider.Id, customerId = rider.Id, rideId = ride.Id }), "conversation_not_found", "admin get enforces selected pair");
    Check((await db.CommunicationMessages.AsNoTracking().SingleAsync(item => item.Id == bulk[124].Id)).ReadAtUtc == null, "admin audit does not read messages on behalf of recipient");
    ride.Status = RideStatus.Completed; await db.SaveChangesAsync();
    Fail(await Run(riderMessages, "add", new { rideId = ride.Id, content = "after completion" }), "chat_not_available", "completed ride chat rejects new sends");
    await ChatHttpScenarios.RunAsync(options, databaseName, rider, driver, outsider, admin, limited, secondRide.Id, Check);
    Console.WriteLine($"PASS: {count} chat integration checks; isolated LocalDB with durable realtime journal.");
}
finally
{
    if (!databaseName.StartsWith("YemenDrive_ChatTests_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test database name.");
    await using var cleanup = new YemenDriveDbContext(options);
    await cleanup.Database.EnsureDeletedAsync();
    File.Delete(settingsFile);
}

void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
void Fail(ApiResult result, string code, string message) => Check(!result.Success && result.Code == code, message + " [" + result.Code + "]");
JsonElement Ok(ApiResult result) { if (!result.Success) throw new Exception(result.Code + ": " + result.Message); return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
static Task<ApiResult> Run(IModelService service, string operation, object model) => service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model, new JsonSerializerOptions(JsonSerializerDefaults.Web)), default);
sealed record Actor(int? UserId) : ICurrentUserContext;
sealed record SentEvent(string Name, string[] Groups, object?[] Arguments);
sealed class RecordingHub<T> : IHubContext<T> where T : Hub
{
    public List<SentEvent> Events { get; } = [];
    public IHubClients Clients => new RecordingClients(Events);
    public IGroupManager Groups => throw new NotSupportedException();
}
sealed class RecordingProxy(List<SentEvent> events, string[] groups) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    { events.Add(new(method, groups, args)); return Task.CompletedTask; }
}
sealed class RecordingClients(List<SentEvent> events) : IHubClients
{
    public IClientProxy All => new RecordingProxy(events, ["all"]);
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
    public IClientProxy Client(string connectionId) => new RecordingProxy(events, [connectionId]);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingProxy(events, connectionIds.ToArray());
    public IClientProxy Group(string groupName) => new RecordingProxy(events, [groupName]);
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Group(groupName);
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingProxy(events, groupNames.ToArray());
    public IClientProxy User(string userId) => new RecordingProxy(events, [userId]);
    public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingProxy(events, userIds.ToArray());
}
