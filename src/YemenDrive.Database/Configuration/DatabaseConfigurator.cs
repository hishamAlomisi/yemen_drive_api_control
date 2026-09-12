using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Database.Providers;
using YemenDrive.Shared.Security;

namespace YemenDrive.Database.Configuration;

public sealed class DatabaseConfigurator(
    DatabaseConfigurationStore store,
    IEnumerable<IDatabaseProvider> providers)
{
    private static readonly string[] RequiredTables =
    [
        "Users", "DriverProfiles", "DriverLiveLocations", "SavedPlaces", "Rides",
        "RideOffers", "Wallets", "WalletTransactions", "LocationUpdates", "Notifications",
        "PaymentTransactions", "PaymentCardTokens", "Promotions", "SupportTickets", "ReferralRedemptions", "PricingRules",
        "LedgerAccounts", "JournalEntries", "JournalLines", "DriverSettlements",
        "CommunicationMessages", "EmergencyRecordings", "RideServiceCatalogItems", "ServiceKinds"
    ];

    private readonly IReadOnlyDictionary<string, IDatabaseProvider> _providers = providers
        .ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<DatabaseSetupResult> TestAsync(
        DatabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return new(false, validationError);
        }

        try
        {
            await using var context = CreateContext(settings);
            return await context.Database.CanConnectAsync(cancellationToken)
                ? new(true, "تم الاتصال بقاعدة البيانات بنجاح.")
                : new(false, "تعذر الاتصال بقاعدة البيانات.");
        }
        catch (Exception exception)
        {
            return new(false, $"فشل الاتصال: {exception.Message}");
        }
    }

    public async Task<DatabaseSetupResult> SaveAsync(
        DatabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(settings);
        if (validationError is not null)
        {
            return new(false, validationError);
        }

        try
        {
            await using var context = CreateContext(settings);
            if (settings.RecreateSchema)
            {
                await context.Database.EnsureDeletedAsync(cancellationToken);
            }
            await context.Database.MigrateAsync(cancellationToken);
            await EnsureAdminUserAsync(context, cancellationToken);

            if (!await context.Database.CanConnectAsync(cancellationToken))
            {
                return new(false, $"تعذر التحقق من قاعدة '{settings.Database}' على الخادم '{settings.Server}'.");
            }

            var existingTables = await context.Database.SqlQueryRaw<string>(
                    "SELECT [name] AS [Value] FROM sys.tables")
                .ToListAsync(cancellationToken);
            var missingTables = RequiredTables
                .Except(existingTables, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (missingTables.Length > 0)
            {
                return new(
                    false,
                    $"تم الوصول إلى القاعدة، لكنها ليست قاعدة جديدة فارغة ولم يتم إنشاء كل الجداول. الجداول الناقصة: {string.Join(", ", missingTables)}. استخدم اسماً جديداً للقاعدة.");
            }

            await store.SaveAsync(settings, cancellationToken);
            return new(
                true,
                $"تم إنشاء وتجهيز قاعدة '{settings.Database}' على الخادم '{settings.Server}' وبداخلها {RequiredTables.Length} جدولاً.");
        }
        catch (Exception exception)
        {
            return new(false, $"تم الاتصال لكن تعذر تجهيز القاعدة: {exception.Message}");
        }
    }

    private YemenDriveDbContext CreateContext(DatabaseSettings settings)
    {
        var optionsBuilder = new DbContextOptionsBuilder<YemenDriveDbContext>();
        GetProvider(settings.Provider).Configure(optionsBuilder, settings);

        return new YemenDriveDbContext(optionsBuilder.Options);
    }

    private string? Validate(DatabaseSettings settings)
    {
        if (!_providers.ContainsKey(settings.Provider))
        {
            return "نوع قاعدة البيانات غير مدعوم حالياً.";
        }

        if (string.IsNullOrWhiteSpace(settings.Server) || string.IsNullOrWhiteSpace(settings.Database))
        {
            return "اسم الخادم واسم قاعدة البيانات مطلوبان.";
        }

        if (!settings.IntegratedSecurity &&
            (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password)))
        {
            return "اسم المستخدم وكلمة المرور مطلوبان عند تعطيل مصادقة Windows.";
        }

        return null;
    }

    private IDatabaseProvider GetProvider(string name) =>
        _providers.TryGetValue(name, out var provider)
            ? provider
            : throw new NotSupportedException($"Database provider '{name}' is not supported.");

    private static async Task EnsureAdminUserAsync(YemenDriveDbContext context, CancellationToken cancellationToken)
    {
        const string phone = "700000001";
        var existing = await context.Users.Include(x => x.Wallet)
            .SingleOrDefaultAsync(x => x.PhoneNumber == phone, cancellationToken);
        if (existing is not null)
        {
            var changed = false;
            if (existing.Role != UserRole.Admin) { existing.Role = UserRole.Admin; changed = true; }
            if (!existing.IsActive) { existing.IsActive = true; changed = true; }
            if (existing.Wallet is null)
            {
                existing.Wallet = new Wallet { Currency = "YER" };
                changed = true;
            }
            if (changed) await context.SaveChangesAsync(cancellationToken);
            return;
        }

        context.Users.Add(new User
        {
            PhoneNumber = phone,
            DisplayName = "YemenDrive Administrator",
            PasswordHash = PasswordHash.Create("Admin1234!"),
            Role = UserRole.Admin,
            IsActive = true,
            Wallet = new Wallet { Currency = "YER" }
        });
        await context.SaveChangesAsync(cancellationToken);
    }

}

public sealed record DatabaseSetupResult(bool Success, string Message);
