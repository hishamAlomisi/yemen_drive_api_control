using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Users;

public sealed class CustomerAssessmentReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    ICurrentUserContext currentUser) : ReportsService<CustomerAssessmentModel>(configuration)
{
    protected override async Task<object?> ListAsync(CustomerAssessmentModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        var customersQuery = db.Users.AsNoTracking().Where(x => x.Role == UserRole.Customer);
        var term = model.Query?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
            customersQuery = customersQuery.Where(x =>
                (x.DisplayName != null && x.DisplayName.Contains(term)) ||
                x.PhoneNumber.Contains(term) ||
                (x.Email != null && x.Email.Contains(term)));

        var customers = await customersQuery.OrderByDescending(x => x.CreatedAtUtc).Take(500)
            .Select(x => new
            {
                x.Id, x.DisplayName, x.PhoneNumber, x.Email, x.Gender,
                x.City, x.Street, x.District, x.Role, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc,
                walletId = x.Wallet == null ? (int?)null : x.Wallet.Id,
                walletBalance = x.Wallet == null ? 0m : x.Wallet.Balance
            }).ToListAsync(token);

        if (customers.Count == 0) return Array.Empty<object>();
        var customerIds = customers.Select(x => x.Id).ToArray();
        var rides = await db.Rides.AsNoTracking().Where(x => customerIds.Contains(x.CustomerId))
            .GroupBy(x => x.CustomerId)
            .Select(group => new
            {
                CustomerId = group.Key,
                RequestedCount = group.Count(x => x.Status != RideStatus.Draft),
                CompletedCount = group.Count(x => x.Status == RideStatus.Completed),
                CancelledCount = group.Count(x => x.Status == RideStatus.Cancelled),
                LastRequestAtUtc = group.Where(x => x.Status != RideStatus.Draft)
                    .Max(x => (DateTime?)x.CreatedAtUtc)
            }).ToDictionaryAsync(x => x.CustomerId, token);

        var revenues = await (
            from line in db.JournalLines.AsNoTracking()
            join ride in db.Rides.AsNoTracking() on line.RideId equals (int?)ride.Id
            where customerIds.Contains(ride.CustomerId) &&
                  line.JournalEntry.IsPosted && line.JournalEntry.Status == JournalEntryStatus.Posted &&
                  (line.LedgerAccount.Purpose == LedgerAccountPurpose.ServiceFee ||
                   line.LedgerAccount.Purpose == LedgerAccountPurpose.ServiceCollection)
            group line by ride.CustomerId into customerLines
            select new
            {
                CustomerId = customerLines.Key,
                NetRevenue = customerLines.Sum(line => line.Credit - line.Debit)
            }).ToDictionaryAsync(x => x.CustomerId, token);

        var safetyCounts = await db.SafetyIncidents.AsNoTracking()
            .Where(x => customerIds.Contains(x.UserId))
            .GroupBy(x => x.UserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.UserId, token);

        var results = customers.Select(customer =>
        {
            var rideStats = rides.GetValueOrDefault(customer.Id);
            var requested = rideStats?.RequestedCount ?? 0;
            var completed = rideStats?.CompletedCount ?? 0;
            var cancelled = rideStats?.CancelledCount ?? 0;
            var safetyReports = safetyCounts.GetValueOrDefault(customer.Id)?.Count ?? 0;
            var revenue = revenues.GetValueOrDefault(customer.Id)?.NetRevenue ?? 0m;
            var cancellationRate = requested == 0 ? 0m : (decimal)cancelled / requested;

            // The score is a transparent operational indicator, not a judgement of intent.
            var reliabilityPoints = Math.Max(0m, 1m - cancellationRate) * 40m;
            var activityPoints = Math.Min(25m, completed * 2.5m);
            var revenuePoints = Math.Min(20m, Math.Max(0m, revenue) / 10_000m);
            var walletPoints = Math.Min(10m, Math.Max(0m, customer.walletBalance) / 2_000m);
            var safetyPoints = Math.Max(0m, 5m - safetyReports * 1.5m);
            var score = Math.Round(reliabilityPoints + activityPoints + revenuePoints + walletPoints + safetyPoints, 1);
            var label = requested == 0 ? "لا يوجد سجل كافٍ"
                : requested >= 4 && cancellationRate > 0.5m ? "معدل إلغاء مرتفع"
                : safetyReports >= 3 ? "بلاغات متكررة"
                : score >= 80m ? "موثوق جدًا"
                : score >= 60m ? "جيد"
                : score >= 40m ? "متوسط"
                : "بحاجة إلى متابعة";

            return new CustomerAssessmentResult(
                customer.Id, customer.DisplayName, customer.PhoneNumber, customer.Email, customer.Gender,
                customer.City, customer.Street, customer.District, customer.Role, customer.IsActive,
                customer.CreatedAtUtc, customer.UpdatedAtUtc, customer.walletId, customer.walletBalance,
                requested, completed, cancelled, Math.Round(cancellationRate * 100m, 1),
                Math.Max(0m, revenue), safetyReports, score, label,
                rideStats?.LastRequestAtUtc);
        }).ToList();

        var descending = model.Descending;
        var sorted = (model.SortBy?.Trim().ToLowerInvariant()) switch
        {
            "rating" => descending ? results.OrderByDescending(x => x.Score) : results.OrderBy(x => x.Score),
            "orders" => descending ? results.OrderByDescending(x => x.RequestedRideCount) : results.OrderBy(x => x.RequestedRideCount),
            "name" => descending ? results.OrderByDescending(x => x.DisplayName) : results.OrderBy(x => x.DisplayName),
            "region" => descending
                ? results.OrderByDescending(x => x.City).ThenByDescending(x => x.District)
                : results.OrderBy(x => x.City).ThenBy(x => x.District),
            "lastrequest" => descending ? results.OrderByDescending(x => x.LastRequestAtUtc) : results.OrderBy(x => x.LastRequestAtUtc),
            "createdat" => descending ? results.OrderByDescending(x => x.CreatedAtUtc) : results.OrderBy(x => x.CreatedAtUtc),
            _ => descending ? results.OrderByDescending(x => x.CreatedAtUtc) : results.OrderBy(x => x.CreatedAtUtc)
        };
        return sorted.ThenBy(x => x.Id).Cast<object>().ToArray();
    }

    private sealed record CustomerAssessmentResult(
        int Id,
        string? DisplayName,
        string PhoneNumber,
        string? Email,
        string? Gender,
        string? City,
        string? Street,
        string? District,
        UserRole Role,
        bool IsActive,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc,
        int? WalletId,
        decimal WalletBalance,
        int RequestedRideCount,
        int CompletedRideCount,
        int CancelledRideCount,
        decimal CancellationRate,
        decimal CollectedRideRevenue,
        int SafetyReportCount,
        decimal Score,
        string Label,
        DateTime? LastRequestAtUtc);

    protected override Task<object?> SearchAsync(CustomerAssessmentModel model, CancellationToken token) =>
        ListAsync(model, token);
}
