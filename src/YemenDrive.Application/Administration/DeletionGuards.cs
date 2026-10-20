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
            ("تقييمات سائق أو تقييمات أُرسلت من هذا الحساب", ct => db.DriverRatings.AnyAsync(
                x => x.DriverUserId == userId || x.CustomerUserId == userId || x.AdminUserId == userId, ct)),
            ("سجلات حملات الإشعارات التي أنشأها", ct => db.AdminNotificationCampaigns.AnyAsync(x => x.CreatedByUserId == userId, ct)),
            ("سجل تحديثات المواقع", ct => db.LocationUpdates.AnyAsync(x => x.ActorId == userId, ct)),
            ("حركات المحفظة", ct => db.WalletTransactions.AnyAsync(x => x.Wallet.UserId == userId, ct)),
            ("رصيد محفظة غير صفري", ct => db.Wallets.AnyAsync(x => x.UserId == userId && x.Balance != 0, ct)),
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
            ("بطاقات دفع محفوظة", ct => db.PaymentCardTokens.AnyAsync(x => x.UserId == userId, ct)),
            ("قيود محاسبية", ct => HasAnyAsync(ct,
                inner => db.JournalEntries.AnyAsync(x => x.CreatedByUserId == userId || x.PostedByUserId == userId, inner),
                inner => db.JournalLines.AnyAsync(x => x.UserId == userId, inner)))
        }, token);

    public static Task EnsureDriverProfileCanBeDeletedAsync(
        YemenDriveDbContext db,
        int driverUserId,
        CancellationToken token) =>
        EnsureNoRelatedRecordsAsync("ملف السائق", new (string Relationship, Func<CancellationToken, Task<bool>> Exists)[]
        {
            ("رحلات مسندة إلى السائق", ct => db.Rides.AnyAsync(x => x.DriverId == driverUserId, ct)),
            ("تقييمات السائق", ct => db.DriverRatings.AnyAsync(x => x.DriverUserId == driverUserId, ct)),
            ("عروض رحلات للسائق", ct => db.RideOffers.AnyAsync(x => x.DriverId == driverUserId, ct)),
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
            ("انتظار أو تغييرات مسار", ct => HasAnyAsync(ct,
                inner => db.RideWaitingSessions.AnyAsync(x => x.RideId == rideId, inner),
                inner => db.RideRouteChanges.AnyAsync(x => x.RideId == rideId, inner))),
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
            ("قواعد تسعير", ct => db.PricingRules.AnyAsync(x => x.ServiceKindId == serviceKindId, ct))
        }, token);

    public static async Task DeleteAutomaticallyCreatedUserChildrenAsync(
        YemenDriveDbContext db,
        int userId,
        CancellationToken token)
    {
        await db.RideSearchViews.Where(x => x.DriverId == userId).ExecuteDeleteAsync(token);
        var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == userId, token);
        if (wallet is not null)
        {
            await DeleteGeneratedFinancialPartyAsync(
                db, FinancialPartyType.Wallet, wallet.Id, $"WLT-{wallet.Id}",
                new Dictionary<string, LedgerAccountPurpose>(StringComparer.OrdinalIgnoreCase)
                {
                    [$"2100-WLT-{wallet.Id}"] = LedgerAccountPurpose.CustomerWallet
                }, "المحفظة", token);
            db.Wallets.Remove(wallet);
        }

        await DeleteGeneratedFinancialPartyAsync(
            db, FinancialPartyType.User, userId, $"USR-{userId}",
            new Dictionary<string, LedgerAccountPurpose>(StringComparer.OrdinalIgnoreCase)
            {
                [$"1100-DRV-{userId}"] = LedgerAccountPurpose.DriverCurrentAccount
            }, "المستخدم", token);

        var installationIds = await db.DevicePushInstallations
            .Where(x => x.UserId == userId).Select(x => x.Id).ToArrayAsync(token);
        var notificationIds = await db.Notifications
            .Where(x => x.UserId == userId).Select(x => x.Id).ToArrayAsync(token);
        await DeleteNotificationTreeAsync(db, notificationIds, installationIds, token);

        await db.DriverLiveLocations.Where(x => x.DriverId == userId).ExecuteDeleteAsync(token);
        await db.DevicePushInstallations.Where(x => x.UserId == userId).ExecuteDeleteAsync(token);
        await db.AuthSessions.Where(x => x.UserId == userId).ExecuteDeleteAsync(token);
        await db.TrustedDevices.Where(x => x.UserId == userId).ExecuteDeleteAsync(token);
    }

    public static async Task DeleteAutomaticallyCreatedDriverProfileChildrenAsync(
        YemenDriveDbContext db,
        int driverUserId,
        CancellationToken token)
    {
        await db.DriverLiveLocations.Where(x => x.DriverId == driverUserId).ExecuteDeleteAsync(token);

        var party = await db.FinancialParties.SingleOrDefaultAsync(
            x => x.Type == FinancialPartyType.User && x.EntityId == driverUserId, token);
        if (party is null) return;

        var driverAccounts = await db.LedgerAccounts
            .Where(x => x.FinancialPartyId == party.Id && x.Purpose == LedgerAccountPurpose.DriverCurrentAccount)
            .ToListAsync(token);
        if (driverAccounts.Count == 0) return;

        var expectedCode = $"1100-DRV-{driverUserId}";
        if (driverAccounts.Count != 1 || driverAccounts[0].IsSystem ||
            !string.Equals(driverAccounts[0].Code, expectedCode, StringComparison.OrdinalIgnoreCase))
            throw GeneratedFinancialRecordsBlockDeletion("ملف السائق", "حسابًا ماليًا غير مولّد تلقائيًا");

        var account = driverAccounts[0];
        var hasJournalLines = await db.JournalLines.AnyAsync(x => x.LedgerAccountId == account.Id, token);
        var hasChildAccounts = await db.LedgerAccounts.AnyAsync(x => x.ParentLedgerAccountId == account.Id, token);
        if (hasJournalLines || hasChildAccounts)
            throw GeneratedFinancialRecordsBlockDeletion("ملف السائق", "حسابًا ماليًا يحتوي على قيود أو حسابات فرعية");

        db.LedgerAccounts.Remove(account);
    }

    public static async Task DeleteAutomaticallyCreatedRideChildrenAsync(
        YemenDriveDbContext db,
        int rideId,
        CancellationToken token)
    {
        await db.LocationUpdates.Where(x => x.RideId == rideId).ExecuteDeleteAsync(token);

        var notificationIds = await db.Notifications
            .Where(x => x.DataJson != null &&
                (x.DataJson.Contains($"\"rideId\":{rideId},") ||
                 x.DataJson.Contains($"\"rideId\":{rideId}}}")))
            .Select(x => x.Id)
            .ToArrayAsync(token);
        await DeleteNotificationTreeAsync(db, notificationIds, [], token);
    }

    public static async Task DeleteAutomaticallyCreatedServiceKindChildrenAsync(
        YemenDriveDbContext db,
        int serviceKindId,
        CancellationToken token) =>
        await DeleteGeneratedFinancialPartyAsync(
            db, FinancialPartyType.ServiceKind, serviceKindId, $"SK-{serviceKindId}",
            new Dictionary<string, LedgerAccountPurpose>(StringComparer.OrdinalIgnoreCase)
            {
                [$"4100-SK-{serviceKindId}"] = LedgerAccountPurpose.ServiceFee,
                [$"4200-SK-{serviceKindId}"] = LedgerAccountPurpose.ServiceCollection
            }, "نوع الخدمة", token);

    private static async Task DeleteGeneratedFinancialPartyAsync(
        YemenDriveDbContext db,
        FinancialPartyType partyType,
        int entityId,
        string generatedPartyCode,
        IReadOnlyDictionary<string, LedgerAccountPurpose> generatedAccounts,
        string ownerLabel,
        CancellationToken token)
    {
        var party = await db.FinancialParties.SingleOrDefaultAsync(
            x => x.Type == partyType && x.EntityId == entityId, token);
        if (party is null) return;

        if (!string.Equals(party.Code, generatedPartyCode, StringComparison.OrdinalIgnoreCase))
            throw GeneratedFinancialRecordsBlockDeletion(ownerLabel, "طرفًا ماليًا غير مولّد تلقائيًا");

        var accounts = await db.LedgerAccounts
            .Where(x => x.FinancialPartyId == party.Id)
            .ToListAsync(token);
        if (accounts.Any(account => account.IsSystem ||
                !generatedAccounts.TryGetValue(account.Code, out var purpose) || account.Purpose != purpose))
            throw GeneratedFinancialRecordsBlockDeletion(ownerLabel, "حسابًا ماليًا أُضيف أو عُدّل يدويًا");

        var accountIds = accounts.Select(x => x.Id).ToArray();
        var hasJournalLines = await db.JournalLines.AnyAsync(
            x => x.FinancialPartyId == party.Id || accountIds.Contains(x.LedgerAccountId), token);
        var hasChildAccounts = accountIds.Length > 0 && await db.LedgerAccounts.AnyAsync(
            x => x.ParentLedgerAccountId != null && accountIds.Contains(x.ParentLedgerAccountId.Value), token);
        if (hasJournalLines || hasChildAccounts)
            throw GeneratedFinancialRecordsBlockDeletion(ownerLabel, "حسابًا ماليًا يحتوي على قيود أو حسابات فرعية");

        db.LedgerAccounts.RemoveRange(accounts);
        db.FinancialParties.Remove(party);
    }

    private static async Task DeleteNotificationTreeAsync(
        YemenDriveDbContext db,
        int[] notificationIds,
        int[] installationIds,
        CancellationToken token)
    {
        var outboxIds = notificationIds.Length == 0
            ? []
            : await db.NotificationPushOutbox
                .Where(x => notificationIds.Contains(x.NotificationId))
                .Select(x => x.Id)
                .ToArrayAsync(token);

        if (outboxIds.Length > 0)
            await db.NotificationPushDeliveries.Where(x => outboxIds.Contains(x.OutboxId)).ExecuteDeleteAsync(token);
        if (installationIds.Length > 0)
            await db.NotificationPushDeliveries.Where(x => installationIds.Contains(x.DevicePushInstallationId)).ExecuteDeleteAsync(token);
        if (outboxIds.Length > 0)
            await db.NotificationPushOutbox.Where(x => outboxIds.Contains(x.Id)).ExecuteDeleteAsync(token);
        if (notificationIds.Length > 0)
            await db.Notifications.Where(x => notificationIds.Contains(x.Id)).ExecuteDeleteAsync(token);
    }

    private static ServiceException GeneratedFinancialRecordsBlockDeletion(string ownerLabel, string reason) =>
        new("delete_blocked_by_related_records",
            $"لا يمكن حذف {ownerLabel} لأن البيانات المالية التابعة تحتوي على {reason}. يجب الاحتفاظ بها لحماية السجلات المحاسبية.");

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
