using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Administration;

public sealed class AdministratorAccess(YemenDriveDbContext db, ICurrentUserContext user) : IModelRequestAuthorizer
{
    public static string Scope(string model) => model.ToLowerInvariant() switch
    {
        "administratorpermissionmodel" => "permissions",
        "savedplaceadminmodel" => "personal_data",
        "ridechatauditmodel" => "operations",
        "drivermovementreportmodel" or "ridemovementhistorymodel" => "operations",
        "ridecancellationreportmodel" => "refunds",
        "ridefinancialcasemodel" or "ridecancellationrequestmodel" => "refunds",
        "supportticketmodel" => "support",
        "accountdeletionmodel" => "personal_data",
        "operationalreviewmodel" => "finance",
        "operationalpolicymodel" => "policy",
        _ when model.Contains("Wallet", StringComparison.OrdinalIgnoreCase) || model.Contains("Payment", StringComparison.OrdinalIgnoreCase) || model.Contains("Settlement", StringComparison.OrdinalIgnoreCase) || model.Contains("Financial", StringComparison.OrdinalIgnoreCase) || model.Contains("Ledger", StringComparison.OrdinalIgnoreCase) || model.Contains("Accounting", StringComparison.OrdinalIgnoreCase) => "finance",
        _ when model.Contains("Pricing", StringComparison.OrdinalIgnoreCase) || model.Contains("Service", StringComparison.OrdinalIgnoreCase) => "policy",
        _ when model.Contains("Safety", StringComparison.OrdinalIgnoreCase) => "safety",
        _ when model.Contains("User", StringComparison.OrdinalIgnoreCase) || model.Contains("Customer", StringComparison.OrdinalIgnoreCase) || model.Contains("Driver", StringComparison.OrdinalIgnoreCase) || model.Contains("Place", StringComparison.OrdinalIgnoreCase) => "personal_data",
        _ => "operations"
    };
    public async Task AuthorizeAsync(ApiRequest request, CancellationToken ct)
    {
        var read = request.Operation?.ToLowerInvariant() is "get" or "list" or "report" or "search" or "mine";
        if (request.Model is "AdministrativeLookupModel" or "AdminRecordIdentityModel") {
            await RequireActiveAdministratorAsync(db, user.RequireUserId(), ct);
            return; // These models authorize the requested lookup/creation scope themselves.
        }
        if (user.UserId is not int id || !await db.Users.AnyAsync(x => x.Id == id && x.IsActive && x.Role == UserRole.Admin, ct))
        {
            // Catalog discovery is public; writes and financial administration are not.
            var restricted = new[] { "PricingModel", "PricingRuleModel", "PromotionModel", "ServiceKindModel", "ServiceCatalogModel", "ServiceCatalogItemModel", "ServiceAreaModel", "PaymentMethodModel", "DriverModel" };
            var adminOnly = new[] { "RideChatAuditModel", "OperationalReviewModel", "OperationalPolicyModel", "AdministratorPermissionModel", "FinancialOperationModel", "FinancialReportModel", "FinancialStatementsModel", "AccountingSetupModel", "AccountStatementModel", "SavedPlaceAdminModel", "AdminNotificationCampaignModel", "RideSearchRecipientModel", "LedgerAccountModel", "JournalEntryModel", "DriverSettlementPaymentModel", "DashboardModel", "CustomerModel" };
            if (request.Model.ToLowerInvariant() is "driverlocationadminmodel" or "drivermovementreportmodel" or "ridemovementhistorymodel" ||
                adminOnly.Contains(request.Model, StringComparer.OrdinalIgnoreCase) || !read && restricted.Contains(request.Model, StringComparer.OrdinalIgnoreCase))
                throw new ServiceException("administrator_required", "هذا الإجراء متاح للإدارة المفوضة فقط.");
            return;
        }
        if (request.Model == "AdministratorPermissionModel" && request.Operation == "mine") return;
        await RequireAsync(db, id, Scope(request.Model) + (read ? ".read" : ".write"), ct);
    }
    public static async Task RequireAsync(YemenDriveDbContext db, int id, string permission, CancellationToken ct)
    {
        var administrator = await db.Users.AsNoTracking().Where(x => x.Id == id && x.IsActive && x.Role == UserRole.Admin)
            .Select(x => new { x.IsSystemAdministrator }).SingleOrDefaultAsync(ct);
        if (administrator is null || !administrator.IsSystemAdministrator &&
            !await db.AdministratorPermissions.AnyAsync(x => x.UserId == id && (x.Permission == "*" || x.Permission == permission), ct))
            throw new ServiceException("administrator_permission_required", "لا تملك التفويض الإداري اللازم لهذا الإجراء.");
    }
    private static async Task RequireActiveAdministratorAsync(YemenDriveDbContext db, int id, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(x => x.Id == id && x.IsActive && x.Role == UserRole.Admin, ct))
            throw new ServiceException("administrator_required", "هذه العملية متاحة للإدارة فقط.");
    }
}
