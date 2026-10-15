using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Configuration;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Drivers;

public sealed class DriverRating(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    ICurrentUserContext currentUser) : OperationsService<DriverRatingModel>(configuration)
{
    protected override async Task<object?> AddAsync(DriverRatingModel model, CancellationToken token)
    {
        var actorId = currentUser.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول لإرسال التقييم.");
        if (model.Score is not decimal score || score is < 1 or > 5)
            throw new ServiceException("invalid_rating", "التقييم يجب أن يكون من نجمة إلى خمس نجوم.");
        var comment = string.IsNullOrWhiteSpace(model.Comment) ? null : model.Comment.Trim();
        if (comment?.Length > 2000)
            throw new ServiceException("rating_comment_too_long", "التعليق أطول من الحد المسموح.");

        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorId && x.IsActive, token)
            ?? throw new ServiceException("user_not_found", "الحساب غير موجود أو موقوف.");

        int driverUserId;
        int? customerUserId = null;
        int? adminUserId = null;
        int? rideId = null;
        DriverRatingSource source;

        if (actor.Role == UserRole.Admin)
        {
            if (model.DriverUserId is not int requestedDriverId)
                throw new ServiceException("driver_required", "اختر السائق المراد تقييمه.");
            driverUserId = requestedDriverId;
            adminUserId = actorId;
            source = DriverRatingSource.Administrator;
            if (!await db.DriverProfiles.AnyAsync(x => x.UserId == driverUserId, token))
                throw new ServiceException("driver_not_found", "ملف السائق غير موجود.");
        }
        else if (actor.Role == UserRole.Customer)
        {
            if (model.RideId is not int requestedRideId)
                throw new ServiceException("ride_required", "معرف الرحلة مطلوب لإرسال التقييم.");
            var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestedRideId, token)
                ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
            if (ride.CustomerId != actorId || ride.Status != RideStatus.Completed || ride.DriverId is null)
                throw new ServiceException("ride_not_rateable", "يمكن تقييم السائق بعد اكتمال رحلة مسجلة على حسابك.");

            var existing = await db.DriverRatings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RideId == ride.Id && x.Source == DriverRatingSource.Customer, token);
            if (existing is not null) return ToResult(existing, true);

            driverUserId = ride.DriverId.Value;
            customerUserId = actorId;
            rideId = ride.Id;
            source = DriverRatingSource.Customer;
        }
        else
        {
            throw new ServiceException("rating_not_allowed", "إرسال تقييم السائق متاح للعميل والإدارة فقط.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var profile = await db.DriverProfiles.SingleOrDefaultAsync(x => x.UserId == driverUserId, token)
            ?? throw new ServiceException("driver_not_found", "ملف السائق غير موجود.");
        var rating = new YemenDrive.Database.Entities.DriverRating
        {
            DriverUserId = driverUserId,
            CustomerUserId = customerUserId,
            AdminUserId = adminUserId,
            RideId = rideId,
            Score = score,
            Comment = comment,
            Source = source
        };
        db.DriverRatings.Add(rating);
        await db.SaveChangesAsync(token);

        profile.Rating = await db.DriverRatings.Where(x => x.DriverUserId == driverUserId)
            .AverageAsync(x => (decimal?)x.Score, token) ?? 0m;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ToResult(rating, false);
    }

    protected override async Task<object?> GetAsync(DriverRatingModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        if (model.Id is not int id)
            throw new ServiceException("rating_id_required", "معرف التقييم مطلوب.");
        var rating = await db.DriverRatings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ServiceException("rating_not_found", "التقييم غير موجود.");
        return ToResult(rating, false);
    }

    private static object ToResult(YemenDrive.Database.Entities.DriverRating x, bool alreadySubmitted) => new
    {
        x.Id, x.DriverUserId, x.CustomerUserId, x.AdminUserId, x.RideId,
        x.Score, x.Comment, x.Source, x.CreatedAtUtc, alreadySubmitted
    };
}
