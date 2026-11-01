using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Payments;

public sealed class PaymentMethodReport(YemenDriveDbContext db, DatabaseConfigurationStore config,
    IWalletPaymentAvailability? availability = null) : ReportsService<PaymentMethodModel>(config)
{
    protected override async Task<object?> ListAsync(PaymentMethodModel model, CancellationToken token)
    {
        var accounts = await db.PaymentProviderAccounts.AsNoTracking().ToDictionaryAsync(x => x.PaymentMethodId, token);
        var methods = await db.PaymentMethods.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.NameAr).ToListAsync(token);
        return methods.Select(x => {
            var external = x.Kind == YemenDrive.Database.Entities.PaymentMethodKind.ExternalWallet;
            var connected = external && accounts.TryGetValue(x.Id, out var account) && account.IsEnabled &&
                account.ProviderCode == x.ProviderCode && availability?.IsReady(x.Id, account.ConfigurationVersion) == true;
            var available = x.IsActive && (!external || connected && availability?.Supports(x.ProviderCode) == true);
            return new { x.Id, x.Code, x.NameAr, x.DescriptionAr, x.ImageUrl, x.Kind, x.ProviderCode,
                x.PublicInstructionsAr, x.IsActive, x.IsAvailableForRidePayment, x.IsAvailableForWalletTopUp,
                x.SortOrder, x.CreatedAtUtc, x.UpdatedAtUtc, IsProviderConnected = connected,
                IsServiceAvailable = available, AvailabilityMessage = available ? "" : "الخدمة غير متاحة" };
        }).OrderByDescending(x => x.IsServiceAvailable).ThenBy(x => x.SortOrder).ToList();
    }
}
