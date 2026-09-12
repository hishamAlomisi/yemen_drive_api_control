using Microsoft.EntityFrameworkCore;
using YemenDrive.Database.Entities;

namespace YemenDrive.Database;

public sealed class YemenDriveDbContext(DbContextOptions<YemenDriveDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<DriverProfile> DriverProfiles => Set<DriverProfile>();
    public DbSet<DriverLiveLocation> DriverLiveLocations => Set<DriverLiveLocation>();
    public DbSet<SavedPlace> SavedPlaces => Set<SavedPlace>();
    public DbSet<Ride> Rides => Set<Ride>();
    public DbSet<RideOffer> RideOffers => Set<RideOffer>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<LocationUpdate> LocationUpdates => Set<LocationUpdate>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<PaymentCardToken> PaymentCardTokens => Set<PaymentCardToken>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<ReferralRedemption> ReferralRedemptions => Set<ReferralRedemption>();
    public DbSet<PricingRule> PricingRules => Set<PricingRule>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<DriverSettlement> DriverSettlements => Set<DriverSettlement>();
    public DbSet<CommunicationMessage> CommunicationMessages => Set<CommunicationMessage>();
    public DbSet<EmergencyRecording> EmergencyRecordings => Set<EmergencyRecording>();
    public DbSet<RideServiceCatalogItem> ServiceCatalogItems => Set<RideServiceCatalogItem>();
    public DbSet<ServiceKind> ServiceKinds => Set<ServiceKind>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(x => x.PhoneNumber).IsUnique();
        modelBuilder.Entity<User>().Property(x => x.PhoneNumber).HasMaxLength(30).IsRequired();
        modelBuilder.Entity<User>().Property(x => x.DisplayName).HasMaxLength(150);
        modelBuilder.Entity<User>().Property(x => x.Email).HasMaxLength(254);
        modelBuilder.Entity<User>().Property(x => x.PasswordHash).HasMaxLength(300).IsRequired();
        modelBuilder.Entity<User>().Property(x => x.Gender).HasMaxLength(32);
        modelBuilder.Entity<User>().Property(x => x.Street).HasMaxLength(200);
        modelBuilder.Entity<User>().Property(x => x.City).HasMaxLength(100);
        modelBuilder.Entity<User>().Property(x => x.District).HasMaxLength(100);
        modelBuilder.Entity<SavedPlace>().HasIndex(x => new { x.UserId, x.Label });
        modelBuilder.Entity<SavedPlace>().Property(x => x.Kind).HasMaxLength(32).IsRequired();

        modelBuilder.Entity<DriverProfile>().HasIndex(x => x.UserId).IsUnique();
        modelBuilder.Entity<DriverProfile>().HasIndex(x => new { x.ServiceKindId, x.ServiceCatalogItemId });
        modelBuilder.Entity<DriverLiveLocation>().HasIndex(x => x.DriverId).IsUnique();
        modelBuilder.Entity<Wallet>().HasIndex(x => x.UserId).IsUnique();
        modelBuilder.Entity<Wallet>().Property(x => x.RowVersion).IsRowVersion();
        modelBuilder.Entity<Wallet>().ToTable(table => table.HasCheckConstraint(
            "CK_Wallets_Balance_NonNegative", "[Balance] >= 0"));
        modelBuilder.Entity<WalletTransaction>().HasIndex(x => new { x.WalletId, x.CreatedAtUtc });
        modelBuilder.Entity<WalletTransaction>().ToTable(table => table.HasCheckConstraint(
            "CK_WalletTransactions_Amount_Positive", "[Amount] > 0"));
        modelBuilder.Entity<PaymentTransaction>().HasIndex(x => new { x.UserId, x.RideId, x.CreatedAtUtc });
        modelBuilder.Entity<PaymentTransaction>().HasIndex(x => x.ProviderReference)
            .IsUnique().HasFilter("[ProviderReference] IS NOT NULL");
        modelBuilder.Entity<PaymentTransaction>().Property(x => x.IdempotencyKey).HasMaxLength(128);
        modelBuilder.Entity<PaymentTransaction>().HasIndex(x => new { x.UserId, x.IdempotencyKey })
            .IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        modelBuilder.Entity<PaymentTransaction>().HasIndex(x => x.RideId)
            .IsUnique().HasFilter("[RideId] IS NOT NULL AND [Status] = 2");
        modelBuilder.Entity<PaymentTransaction>().ToTable(table => table.HasCheckConstraint(
            "CK_PaymentTransactions_Amount_Positive", "[Amount] > 0"));
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAtUtc });
        modelBuilder.Entity<CommunicationMessage>().HasIndex(x => new { x.RideId, x.CreatedAtUtc });
        modelBuilder.Entity<CommunicationMessage>().Property(x => x.MessageType).HasMaxLength(32).IsRequired();
        modelBuilder.Entity<CommunicationMessage>().Property(x => x.Content).HasMaxLength(4000).IsRequired();
        modelBuilder.Entity<Promotion>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<SupportTicket>().HasIndex(x => new { x.UserId, x.CreatedAtUtc });
        modelBuilder.Entity<SupportTicket>().Property(x => x.Category).HasMaxLength(100).IsRequired();
        modelBuilder.Entity<SupportTicket>().Property(x => x.Message).HasMaxLength(2000).IsRequired();
        modelBuilder.Entity<SupportTicket>().Property(x => x.Status).HasMaxLength(32).IsRequired();
        modelBuilder.Entity<SupportTicket>().Property(x => x.AdminReply).HasMaxLength(2000);
        modelBuilder.Entity<ReferralRedemption>().HasIndex(x => new { x.UserId, x.Code });
        modelBuilder.Entity<ReferralRedemption>().Property(x => x.Code).HasMaxLength(100).IsRequired();
        modelBuilder.Entity<ReferralRedemption>().Property(x => x.Status).HasMaxLength(32).IsRequired();
        modelBuilder.Entity<LedgerAccount>().HasIndex(x => x.Code).IsUnique();

        modelBuilder.Entity<RideServiceCatalogItem>().ToTable("RideServiceCatalogItems");
        modelBuilder.Entity<RideServiceCatalogItem>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<RideServiceCatalogItem>().HasIndex(x => new { x.ServiceKindId, x.IsActive, x.SortOrder });
        modelBuilder.Entity<RideServiceCatalogItem>().Property(x => x.Code).HasMaxLength(80).IsRequired();
        modelBuilder.Entity<RideServiceCatalogItem>().Property(x => x.NameAr).HasMaxLength(200).IsRequired();
        modelBuilder.Entity<RideServiceCatalogItem>().Property(x => x.DescriptionAr).HasMaxLength(1000).IsRequired();
        modelBuilder.Entity<RideServiceCatalogItem>().Property(x => x.ImageUrl).HasMaxLength(500);

        modelBuilder.Entity<ServiceKind>().ToTable("ServiceKinds");
        modelBuilder.Entity<ServiceKind>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<ServiceKind>().HasIndex(x => new { x.IsActive, x.SortOrder });
        modelBuilder.Entity<ServiceKind>().Property(x => x.Code).HasMaxLength(80).IsRequired();
        modelBuilder.Entity<ServiceKind>().Property(x => x.NameAr).HasMaxLength(200).IsRequired();
        modelBuilder.Entity<ServiceKind>().Property(x => x.ImageUrl).HasMaxLength(500);

        modelBuilder.Entity<Ride>().Property(x => x.RowVersion).IsRowVersion();
        modelBuilder.Entity<Ride>().Property(x => x.IdempotencyKey).HasMaxLength(128);
        modelBuilder.Entity<Ride>().HasIndex(x => new { x.CustomerId, x.IdempotencyKey })
            .IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        modelBuilder.Entity<Ride>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_Rides_ServerPrice_NonNegative",
                "[ServerPrice] IS NULL OR [ServerPrice] >= 0");
            table.HasCheckConstraint("CK_Rides_CustomerPrice_NonNegative",
                "[CustomerPrice] IS NULL OR [CustomerPrice] >= 0");
        });
        modelBuilder.Entity<Ride>().HasIndex(x => new { x.Status, x.ServiceKindId, x.ServiceCatalogItemId, x.CreatedAtUtc });
        modelBuilder.Entity<RideOffer>().HasIndex(x => new { x.RideId, x.DriverId, x.Status })
            .IsUnique().HasFilter("[Status] = 0");
        modelBuilder.Entity<RideOffer>().Property(x => x.Note).HasMaxLength(1000);
        modelBuilder.Entity<PricingRule>().HasIndex(x => new { x.ServiceKindId, x.ServiceCatalogItemId, x.IsActive });

        modelBuilder.Entity<User>()
            .HasOne(x => x.DriverProfile)
            .WithOne(x => x.User)
            .HasForeignKey<DriverProfile>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DriverProfile>()
            .HasOne(x => x.ServiceKind)
            .WithMany()
            .HasForeignKey(x => x.ServiceKindId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DriverProfile>()
            .HasOne(x => x.ServiceCatalogItem)
            .WithMany()
            .HasForeignKey(x => x.ServiceCatalogItemId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<User>()
            .HasOne(x => x.Wallet)
            .WithOne(x => x.User)
            .HasForeignKey<Wallet>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SavedPlace>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupportTicket>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ReferralRedemption>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Ride>()
            .HasOne(x => x.Customer)
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Ride>()
            .HasOne(x => x.Driver)
            .WithMany()
            .HasForeignKey(x => x.DriverId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RideOffer>()
            .HasOne(x => x.Driver)
            .WithMany()
            .HasForeignKey(x => x.DriverId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RideServiceCatalogItem>()
            .HasOne(x => x.ServiceKind)
            .WithMany()
            .HasForeignKey(x => x.ServiceKindId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Ride>()
            .HasOne(x => x.ServiceKind)
            .WithMany()
            .HasForeignKey(x => x.ServiceKindId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Ride>()
            .HasOne(x => x.ServiceCatalogItem)
            .WithMany()
            .HasForeignKey(x => x.ServiceCatalogItemId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PricingRule>()
            .HasOne(x => x.ServiceKind)
            .WithMany()
            .HasForeignKey(x => x.ServiceKindId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PricingRule>()
            .HasOne(x => x.ServiceCatalogItem)
            .WithMany()
            .HasForeignKey(x => x.ServiceCatalogItemId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<JournalLine>()
            .HasOne(x => x.JournalEntry)
            .WithMany(x => x.Lines)
            .HasForeignKey(x => x.JournalEntryId);

        modelBuilder.Entity<EmergencyRecording>()
            .Property(x => x.StorageKey)
            .HasMaxLength(180);
        modelBuilder.Entity<EmergencyRecording>()
            .Property(x => x.ContentHash)
            .HasMaxLength(128);
        modelBuilder.Entity<EmergencyRecording>()
            .HasIndex(x => new { x.UserId, x.RideId, x.EndedAtUtc });
        modelBuilder.Entity<EmergencyRecording>()
            .HasIndex(x => x.StorageKey)
            .IsUnique();

        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(x => x.GetProperties())
                     .Where(x => x.ClrType == typeof(decimal) || x.ClrType == typeof(decimal?)))
        {
            property.SetPrecision(18);
            property.SetScale(2);
        }

        base.OnModelCreating(modelBuilder);
    }
}
