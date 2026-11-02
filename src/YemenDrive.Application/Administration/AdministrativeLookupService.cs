using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Administration;

public sealed record AdministrativeLookupModel(string Kind = "administrators", string Scope = "permissions");
public sealed class AdministrativeLookupService(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ModelService<AdministrativeLookupModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["list"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, AdministrativeLookupModel model, CancellationToken ct)
    {
        var scopes = new[] { "permissions", "refunds", "support", "personal_data", "finance", "policy", "safety", "operations" };
        if (!scopes.Contains(model.Scope)) throw new ServiceException("invalid_lookup_scope", "قسم الاختيار غير صالح.");
        await AdministratorAccess.RequireAsync(db, user.RequireUserId(), model.Scope + ".read", ct);
        if (model.Kind == "rides") return await db.Rides.AsNoTracking().OrderByDescending(x => x.Id).Take(2000)
            .Select(x => new { x.Id, x.CustomerId, x.DriverId, customerName = x.Customer.DisplayName,
                driverName = x.Driver != null ? x.Driver.DisplayName : null, x.Status,
                x.PickupLabel, x.PickupAddress, x.DestinationLabel, x.DestinationAddress, x.CreatedAtUtc }).ToListAsync(ct);
        if (model.Kind is not ("administrators" or "users")) throw new ServiceException("invalid_lookup_kind", "قائمة غير معروفة.");
        var requiredPermission = model.Scope + ".write";
        return await db.Users.AsNoTracking().Where(x => model.Kind == "users" || x.Role == UserRole.Admin && x.IsActive &&
            (x.IsSystemAdministrator || db.AdministratorPermissions.Any(p => p.UserId == x.Id && (p.Permission == "*" || p.Permission == requiredPermission))))
            .OrderBy(x => x.DisplayName).Select(x => new { x.Id, x.DisplayName, x.IsSystemAdministrator }).ToListAsync(ct);
    }
}

public sealed record AdminRecordIdentityModel(string? EntityModel = null);
public sealed class AdminRecordIdentityService(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ModelService<AdminRecordIdentityModel>(config)
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.OrdinalIgnoreCase) {
        ["UserModel"] = "Users", ["DriverModel"] = "DriverProfiles", ["RideModel"] = "Rides",
        ["ServiceKindModel"] = "ServiceKinds", ["ServiceCatalogModel"] = "RideServiceCatalogItems",
        ["PricingRuleModel"] = "PricingRules", ["DriverSettlementPaymentModel"] = "DriverSettlementPayments",
        ["PaymentMethodModel"] = "PaymentMethods", ["ServiceAreaModel"] = "ServiceAreas",
        ["DriverRatingModel"] = "DriverRatings", ["AdminNotificationCampaignModel"] = "AdminNotificationCampaigns"
    };
    public override IReadOnlyCollection<string> Operations { get; } = ["get"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, AdminRecordIdentityModel model, CancellationToken ct)
    {
        if (model.EntityModel is null || !Tables.TryGetValue(model.EntityModel, out var table))
            throw new ServiceException("unsupported_identity", "هذا النموذج لا ينشئ سجلاً جديداً.");
        await AdministratorAccess.RequireAsync(db, user.RequireUserId(), AdministratorAccess.Scope(model.EntityModel) + ".write", ct);
        // Table comes exclusively from the fixed map. This is a preview, never a reservation.
        var qualifiedTable = "dbo." + table;
        var nextId = await db.Database.SqlQuery<int>($"SELECT CONVERT(int, COALESCE(CONVERT(bigint,last_value) + CONVERT(bigint,increment_value), CONVERT(bigint,seed_value))) AS [Value] FROM sys.identity_columns WHERE object_id = OBJECT_ID({qualifiedTable})").SingleAsync(ct);
        return new { nextId, automatic = true, notice = "يولد الرقم تلقائياً عند الحفظ؛ قد يتغير إذا أضاف مستخدم آخر سجلاً قبلك." };
    }
}
