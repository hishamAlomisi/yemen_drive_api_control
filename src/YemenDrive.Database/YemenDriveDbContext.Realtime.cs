using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using YemenDrive.Database.Entities;

namespace YemenDrive.Database;

public sealed partial class YemenDriveDbContext
{
    public DbSet<RealtimeStateStream> RealtimeStateStreams => Set<RealtimeStateStream>();
    public DbSet<RealtimeStateEvent> RealtimeStateEvents => Set<RealtimeStateEvent>();
    public DbSet<RealtimeStateRecipient> RealtimeStateRecipients => Set<RealtimeStateRecipient>();
    private bool realtimeCommitPending;

    public bool ConsumeRealtimeCommitSignal()
    {
        var pending = realtimeCommitPending;
        realtimeCommitPending = false;
        return pending;
    }

    private static void ConfigureRealtimeJournal(ModelBuilder model)
    {
        model.Entity<RealtimeStateStream>().HasKey(x => x.StreamKey);
        model.Entity<RealtimeStateStream>().Property(x => x.StreamKey).HasMaxLength(120);
        model.Entity<RealtimeStateEvent>().HasKey(x => x.Id);
        // Empty identifies legacy rows and keeps older writers compatible during rollout.
        model.Entity<RealtimeStateEvent>().Property(x => x.OperationId).HasDefaultValue(Guid.Empty);
        model.Entity<RealtimeStateEvent>().Property(x => x.StreamKey).HasMaxLength(120);
        model.Entity<RealtimeStateEvent>().Property(x => x.Resource).HasMaxLength(32);
        model.Entity<RealtimeStateEvent>().Property(x => x.Changes).HasMaxLength(1000);
        model.Entity<RealtimeStateEvent>().HasIndex(x => new { x.StreamKey, x.Version }).IsUnique();
        model.Entity<RealtimeStateEvent>().HasIndex(x => new { x.PublishedAtUtc, x.LeaseUntilUtc, x.CreatedAtUtc });
        model.Entity<RealtimeStateEvent>().HasIndex(x => x.CreatedAtUtc);
        model.Entity<RealtimeStateRecipient>().HasKey(x => new { x.EventId, x.UserId });
        model.Entity<RealtimeStateRecipient>().HasIndex(x => new { x.UserId, x.EventId });
        model.Entity<RealtimeStateRecipient>().HasOne(x => x.Event).WithMany(x => x.Recipients).HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
    }

    private async Task<int> SaveWithRealtimeJournalAsync(bool acceptChanges, CancellationToken token)
    {
        EnforceJournalImmutability();
        ChangeTracker.DetectChanges();
        var changes = ChangeTracker.Entries().Where(IsRelevantChange).ToDictionary(x => x.Entity, x => x, ReferenceEqualityComparer.Instance);
        // Save interceptors can create notifications after the initial scan. Capture those
        // when EF accepts their persisted changes, without journalling materialized reads.
        void Accepted(object? _, EntityStateChangedEventArgs change)
        {
            if (change.OldState is EntityState.Added or EntityState.Modified or EntityState.Deleted && IsRelevantEntity(change.Entry.Entity))
                changes[change.Entry.Entity] = change.Entry;
        }
        ChangeTracker.StateChanged += Accepted;
        IDbContextTransaction? ownedTransaction = null;
        try {
            // This also protects a SaveChanges which did not originally have an explicit
            // transaction: business rows and their journal always commit together.
            if (Database.CurrentTransaction == null)
                ownedTransaction = await Database.BeginTransactionAsync(token);
            // Reuse the actual transaction identity across resources and SaveChanges calls.
            var operationId = Database.CurrentTransaction!.TransactionId;
            var count = await base.SaveChangesAsync(acceptChanges, token);
            if (!acceptChanges)
                foreach (var entry in ChangeTracker.Entries().Where(IsRelevantChange)) changes[entry.Entity] = entry;
            if (changes.Count > 0) {
                var signals = await ResolveRealtimeSignalsAsync(changes.Values, token);
                foreach (var signal in signals.OrderBy(x => x.Key, StringComparer.Ordinal))
                    await AppendRealtimeSignalAsync(signal, operationId, token);
                realtimeCommitPending |= signals.Count > 0;
            }
            if (ownedTransaction != null) await ownedTransaction.CommitAsync(token);
            return count;
        }
        catch {
            // A caller must never commit domain rows after journal persistence failed.
            if (Database.CurrentTransaction is { } transaction)
                await transaction.RollbackAsync(CancellationToken.None);
            realtimeCommitPending = false;
            throw;
        }
        finally {
            ChangeTracker.StateChanged -= Accepted;
            if (ownedTransaction != null) await ownedTransaction.DisposeAsync();
        }
    }

    private static bool IsRelevantChange(EntityEntry entry) =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && IsRelevantEntity(entry.Entity);

    private static bool IsRelevantEntity(object entity) => entity is
        Ride or RideOffer or RideCancellationRequest or RideWaitingSession or RideRouteChange or RideFinancialCase or
        PaymentTransaction or PaymentWalletOperation or CashPaymentRequest or CashCollectionApproval or PickupContactAttempt or
        Wallet or WalletTransaction or WalletTransfer or Notification or CommunicationMessage or RideSearchView or
        DriverProfile or User or DriverRating or SavedPlace or SupportTicket or AccountDeletionRequest or
        ServiceKind or RideServiceCatalogItem or PricingRule or WalletTransferFeeRule or OperationalPolicy or
        SafetyIncident or EmergencyRecording or RideSafetyShare or PaymentMethod or ServiceArea or Promotion or
        JournalEntry or JournalLine or LedgerAccount or FinancialParty or DriverSettlement or DriverSettlementPayment or
        AdministratorPermission or AdminNotificationCampaign or SupportTicketEntry or PaymentProviderAccount;

    private sealed class StateSignal(string resource, int id)
    {
        public string Resource { get; } = resource;
        public int Id { get; } = id;
        public string Key => $"{Resource}:{Id}";
        public HashSet<int> Users { get; } = [];
        public HashSet<string> Changes { get; } = new(StringComparer.Ordinal);
    }

    private async Task<List<StateSignal>> ResolveRealtimeSignalsAsync(IEnumerable<EntityEntry> entries, CancellationToken token)
    {
        var signals = new Dictionary<string, StateSignal>(StringComparer.Ordinal);
        StateSignal Add(string resource, int id, string change, params int[] users)
        {
            var key = $"{resource}:{id}";
            if (!signals.TryGetValue(key, out var signal)) signals[key] = signal = new(resource, id);
            signal.Changes.Add(change);
            foreach (var user in users.Where(x => x >= 0)) signal.Users.Add(user);
            return signal;
        }
        foreach (var entry in entries) {
            var entity = entry.Entity;
            var change = entry.Metadata.ClrType.Name;
            if (entity is Ride ride) Add("ride", ride.Id, change, ride.CustomerId, ride.DriverId ?? 0, 0);
            else if (entity is CommunicationMessage message) Add("messages", message.RideId, change, message.SenderId, message.RecipientId);
            else if (entity is Wallet wallet) Add("wallet", wallet.Id, change, wallet.UserId, 0);
            else if (entity is WalletTransaction movement) Add("wallet", movement.WalletId, change, 0);
            else if (entity is WalletTransfer transfer) Add("account", transfer.SenderUserId, change, transfer.SenderUserId);
            else if (entity is PaymentWalletOperation payment) Add("account", payment.CustomerId, change, payment.CustomerId, 0);
            else if (entity is PaymentTransaction paymentTransaction) Add("account", paymentTransaction.UserId, change, paymentTransaction.UserId, 0);
            else if (entity is DriverSettlement settlement) Add("account", settlement.DriverId, change, settlement.DriverId, 0);
            else if (entity is DriverSettlementPayment settlementPayment) Add("account", settlementPayment.DriverId, change, settlementPayment.DriverId, 0);
            else if (entity is JournalEntry journal) Add("ledger", journal.Id, change, 0);
            else if (entity is JournalLine line) Add("ledger", line.JournalEntryId, change, 0);
            else if (entity is LedgerAccount ledger) Add("ledgerAccount", ledger.Id, change, 0);
            else if (entity is FinancialParty party) Add("financialParty", party.Id, change, 0);
            else if (entity is AdministratorPermission permission) Add("permissions", permission.UserId, change, permission.UserId, 0);
            else if (entity is AdminNotificationCampaign campaign) Add("campaign", campaign.Id, change, 0);
            else if (entity is PaymentProviderAccount providerAccount) Add("providerAccount", providerAccount.PaymentMethodId, change, 0);
            else if (entity is SafetyIncident incident) Add("account", incident.UserId, change, incident.UserId, 0);
            else if (entity is EmergencyRecording recording && recording.EndedAtUtc != null) Add("account", recording.UserId, change, recording.UserId, 0);
            else if (entity is SupportTicketEntry ticketEntry) {
                var owner = await SupportTickets.AsNoTracking().Where(x => x.Id == ticketEntry.SupportTicketId).Select(x => (int?)x.UserId).SingleOrDefaultAsync(token);
                if (owner is int ownerId) Add("account", ownerId, change, ownerId, 0);
            }
            else if (entity is Notification notification) {
                Add("notifications", notification.UserId, change, notification.UserId);
                // New search recipients are established by the persisted notifications.
                try {
                    using var data = JsonDocument.Parse(notification.DataJson ?? "{}");
                    if (data.RootElement.ValueKind == JsonValueKind.Object && data.RootElement.TryGetProperty("rideId", out var id) &&
                        int.TryParse(id.ToString(), out var rideId) && rideId > 0)
                        Add("ride", rideId, change, notification.UserId, 0);
                } catch (JsonException) { /* Ordinary notifications need not have structured data. */ }
            }
            else if (entity is User user) Add("profile", user.Id, change, user.Id, 0);
            else if (entity is DriverProfile profile) Add("profile", profile.UserId, change, profile.UserId, 0);
            else if (entity is DriverRating rating) Add("profile", rating.DriverUserId, change, rating.DriverUserId, 0);
            else if (entity is SavedPlace place) Add("places", place.UserId, change, place.UserId, 0);
            else if (entity is SupportTicket ticket) Add("account", ticket.UserId, change, ticket.UserId, 0);
            else if (entity is AccountDeletionRequest deletion) Add("account", deletion.UserId, change, deletion.UserId, 0);
            else if (entity is ServiceKind or RideServiceCatalogItem or PricingRule or WalletTransferFeeRule or OperationalPolicy or PaymentMethod or ServiceArea or Promotion)
                Add("catalog", 0, change).Users.Add(-1);
            // Ride-affecting child rows share the ride's transactionally ordered stream.
            if (entity is not CommunicationMessage && entry.Metadata.FindProperty("RideId") != null && entry.Property("RideId").CurrentValue is int relatedRide && relatedRide > 0) {
                var signal = Add("ride", relatedRide, change, 0);
                if (entry.Metadata.FindProperty("DriverId") != null && entry.Property("DriverId").CurrentValue is int driverId) signal.Users.Add(driverId);
                if (entry.Metadata.FindProperty("CustomerId") != null && entry.Property("CustomerId").CurrentValue is int customerId) signal.Users.Add(customerId);
            }
        }
        var rideIds = signals.Values.Where(x => x.Resource == "ride").Select(x => x.Id).ToArray();
        foreach (var rideId in rideIds.Order()) {
            // Collection parameters translate to OPENJSON with a broad cardinality
            // estimate. Under a caller's SERIALIZABLE transaction that can scan
            // other rides already locked by independent writers and deadlock them.
            var ride = await Rides.FromSqlInterpolated($"SELECT * FROM [Rides] WITH (FORCESEEK) WHERE [Id] = {rideId}")
                .AsNoTracking().Select(x => new { x.CustomerId, x.DriverId }).SingleOrDefaultAsync(token);
            var signal = signals[$"ride:{rideId}"];
            if (ride is not null) {
                signal.Users.Add(ride.CustomerId);
                if (ride.DriverId is int driver) signal.Users.Add(driver);
            }
            var offerDrivers = await RideOffers.FromSqlInterpolated($"SELECT * FROM [RideOffers] WITH (FORCESEEK) WHERE [RideId] = {rideId}")
                .AsNoTracking().Select(x => x.DriverId).Distinct().ToListAsync(token);
            foreach (var driver in offerDrivers) signal.Users.Add(driver);
        }
        var walletIds = signals.Values.Where(x => x.Resource == "wallet").Select(x => x.Id).ToArray();
        foreach (var walletId in walletIds.Order()) {
            var owner = await Wallets.FromSqlInterpolated($"SELECT * FROM [Wallets] WITH (FORCESEEK) WHERE [Id] = {walletId}")
                .AsNoTracking().Select(x => (int?)x.UserId).SingleOrDefaultAsync(token);
            if (owner is int userId) signals[$"wallet:{walletId}"].Users.Add(userId);
        }
        return signals.Values.Where(x => x.Users.Count > 0).ToList();
    }

    private async Task AppendRealtimeSignalAsync(StateSignal signal, Guid operationId, CancellationToken token)
    {
        const string sql = """
            DECLARE @resourceLock nvarchar(255) = N'YemenDrive.State.' + @key;
            DECLARE @lockResult int;
            EXEC @lockResult = sys.sp_getapplock @Resource=@resourceLock,
                @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
            IF @lockResult < 0 THROW 51001, 'Could not serialize the state resource.', 1;
            -- The resource lock protects creation without a SERIALIZABLE missing-key
            -- range lock which could block an entirely different new stream.
            IF NOT EXISTS (SELECT 1 FROM [RealtimeStateStreams] WITH (READUNCOMMITTED) WHERE [StreamKey]=@key)
              INSERT INTO [RealtimeStateStreams] ([StreamKey],[Version]) VALUES (@key,0);
            DECLARE @version TABLE ([Value] bigint NOT NULL);
            UPDATE [RealtimeStateStreams] WITH (ROWLOCK)
              SET [Version] = [Version] + 1
              OUTPUT INSERTED.[Version] INTO @version
              WHERE [StreamKey] = @key;
            INSERT INTO [RealtimeStateEvents]
              ([Id],[OperationId],[StreamKey],[Version],[Resource],[ResourceId],[Changes],[CreatedAtUtc],[Attempts])
              SELECT @id,@operationId,@key,[Value],@resource,@resourceId,@changes,SYSUTCDATETIME(),0 FROM @version;
            DECLARE @recipients TABLE ([UserId] int PRIMARY KEY);
            INSERT INTO @recipients SELECT DISTINCT CONVERT(int,[value]) FROM OPENJSON(@users);
            -- Retain previous ride audiences only while holding this stream's
            -- transaction lock. Point seeks and statement-level committed reads
            -- avoid SERIALIZABLE missing-range locks spanning unrelated streams;
            -- the stream lock serializes writers adding recipients to this history.
            IF @resource = N'ride'
              INSERT INTO @recipients
              SELECT DISTINCT recipient.[UserId]
              FROM [RealtimeStateEvents] AS previousEvent WITH (READCOMMITTEDLOCK, FORCESEEK)
              INNER JOIN [RealtimeStateRecipients] AS recipient WITH (READCOMMITTEDLOCK, FORCESEEK)
                ON recipient.[EventId] = previousEvent.[Id]
              WHERE previousEvent.[StreamKey] = @key
                AND NOT EXISTS (SELECT 1 FROM @recipients AS known WHERE known.[UserId] = recipient.[UserId])
              OPTION (FORCE ORDER, LOOP JOIN);
            INSERT INTO [RealtimeStateRecipients] ([EventId],[UserId])
              -- Foreign-key validation must not scan unrelated uncommitted events.
              SELECT @id,[UserId] FROM @recipients OPTION (LOOP JOIN);
            """;
        var parameters = new List<object>();
        void Parameter(string name, object value, DbType type)
        {
            parameters.Add(new SqlParameter(name, value) { DbType = type });
        }
        Parameter("@id", Guid.NewGuid(), DbType.Guid);
        Parameter("@operationId", operationId, DbType.Guid);
        Parameter("@key", signal.Key, DbType.String);
        Parameter("@resource", signal.Resource, DbType.String);
        Parameter("@resourceId", signal.Id, DbType.Int32);
        Parameter("@changes", string.Join(',', signal.Changes.Order(StringComparer.Ordinal)), DbType.String);
        Parameter("@users", JsonSerializer.Serialize(signal.Users.Order()), DbType.String);
        await Database.ExecuteSqlRawAsync(sql, parameters, token);
    }
}
