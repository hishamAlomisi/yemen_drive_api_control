using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Configuration;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Drivers;

public sealed class DriverRatingReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    ICurrentUserContext currentUser) : ReportsService<DriverRatingReportModel>(configuration)
{
    protected override async Task<object?> ListAsync(DriverRatingReportModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        var drivers = await db.DriverProfiles.AsNoTracking().OrderBy(x => x.User.DisplayName).Select(x => new
        {
            driverUserId = x.UserId,
            driverName = x.User.DisplayName,
            driverPhone = x.User.PhoneNumber,
            x.VehicleModel,
            x.PlateNumber,
            currentRating = x.Rating,
            customerRatingCount = db.DriverRatings.Count(r => r.DriverUserId == x.UserId && r.Source == DriverRatingSource.Customer),
            customerAverage = db.DriverRatings.Where(r => r.DriverUserId == x.UserId && r.Source == DriverRatingSource.Customer)
                .Select(r => (decimal?)r.Score).Average() ?? 0m,
            adminRatingCount = db.DriverRatings.Count(r => r.DriverUserId == x.UserId && r.Source == DriverRatingSource.Administrator),
            adminAverage = db.DriverRatings.Where(r => r.DriverUserId == x.UserId && r.Source == DriverRatingSource.Administrator)
                .Select(r => (decimal?)r.Score).Average() ?? 0m
        }).Take(500).ToListAsync(token);

        var ratingsQuery = db.DriverRatings.AsNoTracking().AsQueryable();
        if (model.DriverUserId is int selectedDriverId)
            ratingsQuery = ratingsQuery.Where(x => x.DriverUserId == selectedDriverId);
        var ratings = await ratingsQuery.OrderByDescending(x => x.CreatedAtUtc).Take(500)
            .Select(x => new
            {
                x.Id, x.DriverUserId, driverName = x.Driver.DisplayName, driverPhone = x.Driver.PhoneNumber,
                x.CustomerUserId, customerName = x.Customer == null ? null : x.Customer.DisplayName,
                customerPhone = x.Customer == null ? null : x.Customer.PhoneNumber,
                x.AdminUserId, adminName = x.Admin == null ? null : x.Admin.DisplayName,
                x.RideId, x.Score, x.Comment, x.Source, x.CreatedAtUtc
            }).ToListAsync(token);

        return new { drivers, ratings };
    }

    protected override Task<object?> SearchAsync(DriverRatingReportModel model, CancellationToken token) =>
        ListAsync(model, token);
}
