using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Administration;

/// <summary>
/// Protects parent records from being removed while an operational, financial,
/// or child record still depends on them. These checks supplement database
/// foreign keys so the administrator receives a useful domain message instead
/// of a generic database error.
/// </summary>
internal static class DeletionGuards
{
    public static Task EnsureUserCanBeDeletedAsync(
        YemenDriveDbContext db,
        int userId,
        CancellationToken token) =>
        EnsureNoRelatedRecordsAsync("المستخدم", new (string Relationship, Func<CancellationToken, Task<bool>> Exists)[]
        {
            ("رحلات مرتبطة به", ct => db.Rides.AnyAsync(x => x.CustomerId == userId || x.DriverId == userId, ct)),
            ("عروض رحلات", ct => db.RideOffers.AnyAsync(x => x.DriverId == userId, ct)),
            ("ملف سائق", ct => db.DriverProfiles.AnyAsync(x => x.UserId == userId, ct)),
            ("موقع السائق المباشر", ct => db.DriverLiveLocations.AnyAsync(x => x.DriverId == userId, ct)),
            ("سجل تحديثات المواقع", ct => db.LocationUpdates.AnyAsync(x => x.ActorId == userId, ct)),
            ("محفظة أو حساب مالي", ct => db.Wallets.AnyAsync(x => x.UserId == userId, ct)),
            ("حركات دفع", ct => db.PaymentTransactions.AnyAsync(x => x.UserId == userId, ct)),
            ("طلبات دفع نقدي", ct => db.CashPaymentRequests.AnyAsync(x => x.CustomerId == userId || x.DriverId == userId, ct)),
            ("طلبات اعتماد تحصيل نقدي", ct => db.CashCollectionApprovals.AnyAsync(x => x.CustomerId == userId || x.DriverId == userId, ct)),
            ("طلبات إلغاء", ct => db.RideCancellationRequests.AnyAsync(x => x.CustomerId == userId || x.DriverId == userId || x.AdminUserId == userId, ct)),
            ("مديونيات أو تحصيلات سائق", ct => HasAnyAsync(ct,
                inner => db.DriverSettlements.AnyAsync(x => x.DriverId == userId, inner),
                inner => db.DriverSettlementPayments.AnyAsync(x => x.DriverId == userId || x.SettledByUserId == userId, inner))),
            ("رسائل داخل الرحلات", ct => db.CommunicationMessages.AnyAsync(x => x.SenderId == userId || x.RecipientId == userId, ct)),
            ("بلاغات أو تسجيلات سلامة", ct => HasAnyAsync(ct,
                inner => db.EmergencyRecordings.AnyAsync(x => x.UserId == userId, inner),
                inner => db.EmergencyContacts.AnyAsync(x => x.UserId == userId, inner),
                inner => db.SafetyIncidents.AnyAsync(x => x.UserId == userId || x.AdminUserId == userId, inner),
                inner => db.RideSafetyShares.AnyAsync(x => x.OwnerUserId == userId, inner))),
            ("أماكن محفوظة أو تذاكر دعم", ct => HasAnyAsync(ct,
                inner => db.SavedPlaces.AnyAsync(x => x.UserId == userId, inner),
                inner => db.SupportTickets.AnyAsync(x => x.UserId == userId, inner),
                inner => db.ReferralRedemptions.AnyAsync(x => x.UserId == userId, inner))),
            ("إشعارات أو أجهزة مسجلة", ct => HasAnyAsync(ct,
                inner => db.Notifications.AnyAsync(x => x.UserId == userId, inner),
                inner => db.DevicePushInstallations.AnyAsync(x => x.UserId == userId, inner))),
            ("جلسات أو أجهزة موثوقة", ct => HasAnyAsync(ct,
                inner => db.AuthSessions.AnyAsync(x => x.UserId == userId, inner),
                inner => db.TrustedDevices.AnyAsync(x => x.UserId == userId, inner))),
            ("بطاقات دفع محفوظة", ct => db.PaymentCardTokens.AnyAsync(x => x.UserId == userId, ct)),
            ("قيود محاسبية", ct => HasAnyAsync(ct,
                inner => db.JournalEntries.AnyAsync(x => x.CreatedByUserId == userId || x.PostedByUserId == userId, inner),
                inner => db.JournalLines.AnyAsync(x => x.UserId == userId, inner))),
            ("طرف مالي مرتبط", ct => db.FinancialParties.AnyAsync(x => x.Type == FinancialPartyType.User && x.EntityId == userId, ct))
        }, token);

    public static Task EnsureDriverProfileCanBeDeletedAsync(
        YemenDriveDbContext db,
        int driverUserId,
        CancellationToken token) =>
        EnsureNoRelatedRecordsAsync("ملف السائق", new (string Relationship, Func<CancellationToken, Task<bool>> Exists)[]
        {
            ("رحلات مسندة إلى السائق", ct => db.Rides.AnyAsync(x => x.DriverId == driverUserId, ct)),
            ("عروض رحلات للسائق", ct => db.RideOffers.AnyAsync(x => x.DriverId == driverUserId, ct)),
            ("موقع مباشر للسائق", ct => db.DriverLiveLocations.AnyAsync(x => x.DriverId == driverUserId, ct)),
            ("سجل تحديثات مواقع السائق", ct => db.LocationUpdates.AnyAsync(x => x.ActorId == driverUserId, ct)),
            ("طلبات دفع أو تحصيل نقدي", ct => HasAnyAsync(ct,
                inner => db.CashPaymentRequests.AnyAsync(x => x.DriverId == driverUserId, inner),
                inner => db.CashCollectionApprovals.AnyAsync(x => x.DriverId == driverUserId, inner))),
            ("طلبات إلغاء", ct => db.RideCancellationRequests.AnyAsync(x => x.DriverId == driverUserId, ct)),
            ("مديونيات أو تحصيلات", ct => HasAnyAsync(ct,
                inner => db.DriverSettlements.AnyAsync(x => x.DriverId == driverUserId, inner),
                inner => db.DriverSettlementPayments.AnyAsync(x => x.DriverId == driverUserId, inner)))
        }, token);

    public static Task EnsureRideCanBeDeletedAsync(
        YemenDriveDbContext db,
        int rideId,
        CancellationToken token) =>
        EnsureNoRelatedRecordsAsync("الرحلة", new (string Relationship, Func<CancellationToken, Task<bool>> Exists)[]
        {
            ("عروض سائقين", ct => db.RideOffers.AnyAsync(x => x.RideId == rideId, ct)),
            ("تحديثات موقع", ct => db.LocationUpdates.AnyAsync(x => x.RideId == rideId, ct)),
            ("حركات محفظة", ct => db.WalletTransactions.AnyAsync(x => x.RideId == rideId, ct)),
            ("حركات دفع", ct => db.PaymentTransactions.AnyAsync(x => x.RideId == rideId, ct)),
            ("طلبات دفع نقدي", ct => HasAnyAsync(ct,
                inner => db.CashPaymentRequests.AnyAsync(x => x.RideId == rideId, inner),
                inner => db.CashCollectionApprovals.AnyAsync(x => x.RideId == rideId, inner))),
            ("طلب إلغاء", ct => db.RideCancellationRequests.AnyAsync(x => x.RideId == rideId, ct)),
            ("رسائل داخل الرحلة", ct => db.CommunicationMessages.AnyAsync(x => x.RideId == rideId, ct)),
            ("بلاغات أو تسجيلات سلامة", ct => HasAnyAsync(ct,
                inner => db.EmergencyRecordings.AnyAsync(x => x.RideId == rideId, inner),
                inner => db.SafetyIncidents.AnyAsync(x => x.RideId == rideId, inner),
                inner => db.RideSafetyShares.AnyAsync(x => x.RideId == rideId, inner))),
            ("إشعارات مرتبطة بالرحلة", ct => db.Notifications.AnyAsync(x =>
                x.DataJson != null &&
                (x.DataJson.Contains($"\"rideId\":{rideId},") ||
                 x.DataJson.Contains($"\"rideId\":{rideId}}}")), ct)),
            ("قيد محاسبي", ct => db.JournalLines.AnyAsync(x => x.RideId == rideId, ct))
        }, token);

    public static Task EnsureServiceKindCanBeDeletedAsync(
        YemenDriveDbContext db,
        int serviceKindId,
        CancellationToken token) =>
        EnsureNoRelatedRecordsAsync("نوع الخدمة", new (string Relationship, Func<CancellationToken, Task<bool>> Exists)[]
        {
            ("خدمات ضمن هذا النوع", ct => db.ServiceCatalogItems.AnyAsync(x => x.ServiceKindId == serviceKindId, ct)),
            ("ملفات سائقين", ct => db.DriverProfiles.AnyAsync(x => x.ServiceKindId == serviceKindId, ct)),
            ("رحلات", ct => db.Rides.AnyAsync(x => x.ServiceKindId == serviceKindId, ct)),
            ("قواعد تسعير", ct => db.PricingRules.AnyAsync(x => x.ServiceKindId == serviceKindId, ct)),
            ("حسابات مالية مرتبطة", ct => db.FinancialParties.AnyAsync(x => x.Type == FinancialPartyType.ServiceKind && x.EntityId == serviceKindId, ct))
        }, token);

    public static Task EnsureServiceCatalogItemCanBeDeletedAsync(
        YemenDriveDbContext db,
        int serviceCatalogItemId,
        CancellationToken token) =>
        EnsureNoRelatedRecordsAsync("الخدمة", new (string Relationship, Func<CancellationToken, Task<bool>> Exists)[]
        {
            ("ملفات سائقين", ct => db.DriverProfiles.AnyAsync(x => x.ServiceCatalogItemId == serviceCatalogItemId, ct)),
            ("رحلات", ct => db.Rides.AnyAsync(x => x.ServiceCatalogItemId == serviceCatalogItemId, ct)),
            ("قواعد تسعير", ct => db.PricingRules.AnyAsync(x => x.ServiceCatalogItemId == serviceCatalogItemId, ct))
        }, token);

    public static Task EnsurePaymentMethodCanBeDeletedAsync(
        YemenDriveDbContext db,
        string code,
        string providerCode,
        CancellationToken token)
    {
        var normalizedCode = code.Trim().ToUpperInvariant();
        var normalizedProviderCode = providerCode.Trim().ToUpperInvariant();
        return
        EnsureNoRelatedRecordsAsync("طريقة الدفع", new (string Relationship, Func<CancellationToken, Task<bool>> Exists)[]
        {
            ("حركات دفع مسجلة", ct => db.PaymentTransactions.AnyAsync(
                x => x.Provider.ToUpper() == normalizedCode || x.Provider.ToUpper() == normalizedProviderCode, ct)),
            ("بطاقات دفع محفوظة", ct => db.PaymentCardTokens.AnyAsync(
                x => x.Provider.ToUpper() == normalizedCode || x.Provider.ToUpper() == normalizedProviderCode, ct))
        }, token);
    }

    private static async Task EnsureNoRelatedRecordsAsync(
        string entityName,
        IEnumerable<(string Relationship, Func<CancellationToken, Task<bool>> Exists)> checks,
        CancellationToken token)
    {
        foreach (var (relationship, exists) in checks)
        {
            if (!await exists(token)) continue;

            throw new ServiceException(
                "delete_blocked_by_related_records",
                $"لا يمكن حذف {entityName} لأنه مرتبط بـ{relationship}. أزل الارتباط أولاً أو أوقف السجل بدلاً من حذفه.");
        }
    }

    private static async Task<bool> HasAnyAsync(
        CancellationToken token,
        params Func<CancellationToken, Task<bool>>[] checks)
    {
        foreach (var check in checks)
        {
            if (await check(token)) return true;
        }

        return false;
    }
}
