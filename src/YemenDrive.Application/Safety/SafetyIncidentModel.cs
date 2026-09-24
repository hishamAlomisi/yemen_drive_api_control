using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Safety;

public sealed record SafetyIncidentModel(int? Id = null, int? RideId = null, string? Message = null, string? Note = null);

public sealed class SafetyIncident(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext currentUser)
    : OperationsService<SafetyIncidentModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["add", "get", "accept", "adminApprove"];
    protected override async Task<object?> AddAsync(SafetyIncidentModel model, CancellationToken token)
    {
        var userId = currentUser.RequireUserId();
        if (model.RideId is not int rideId) throw new ServiceException("ride_required", "معرف الرحلة مطلوب لبلاغ السلامة.");
        var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rideId, token) ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.CustomerId != userId && ride.DriverId != userId) throw new ServiceException("ride_access_denied", "لا تملك صلاحية إرسال بلاغ لهذه الرحلة.");
        if (ride.Status is RideStatus.Completed or RideStatus.Cancelled) throw new ServiceException("ride_not_active", "لا يمكن إرسال بلاغ لرحلة منتهية.");
        var message = model.Message?.Trim(); if (message?.Length > 1000) throw new ServiceException("invalid_safety_message", "تفاصيل البلاغ أطول من المسموح.");
        var last = await db.LocationUpdates.AsNoTracking().Where(x => x.RideId == rideId).OrderByDescending(x => x.ObservedAtUtc).FirstOrDefaultAsync(token);
        var item = new YemenDrive.Database.Entities.SafetyIncident { UserId = userId, RideId = rideId, Message = string.IsNullOrWhiteSpace(message) ? "طلب مساعدة عاجل من داخل الرحلة." : message, Latitude = last?.Latitude ?? (userId == ride.CustomerId ? ride.PickupLatitude : null), Longitude = last?.Longitude ?? (userId == ride.CustomerId ? ride.PickupLongitude : null), LocationObservedAtUtc = last?.ObservedAtUtc };
        db.SafetyIncidents.Add(item);
        await db.SaveChangesAsync(token);
        var admins = await db.Users.AsNoTracking().Where(x => x.Role == UserRole.Admin && x.IsActive).Select(x => x.Id).ToListAsync(token);
        foreach (var adminId in admins) db.Notifications.Add(new Notification { UserId = adminId, Type = NotificationType.Safety, Title = "بلاغ سلامة عاجل", Body = $"ورد بلاغ سلامة للرحلة #{rideId}. راجع الموقع والتفاصيل فوراً.", DataJson = $"{{\"safetyIncidentId\":{item.Id},\"rideId\":{rideId}}}" });
        await db.SaveChangesAsync(token); return Result(item);
    }
    protected override async Task<object?> GetAsync(SafetyIncidentModel model, CancellationToken token)
    { var user = currentUser.RequireUserId(); var item = await db.SafetyIncidents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.Id && x.UserId == user, token) ?? throw new ServiceException("safety_incident_not_found", "بلاغ السلامة غير موجود."); return Result(item); }
    protected override Task<object?> AcceptAsync(SafetyIncidentModel model, CancellationToken token) => DecideAsync(model, false, token);
    protected override Task<object?> AdminApproveAsync(SafetyIncidentModel model, CancellationToken token) => DecideAsync(model, true, token);
    private async Task<object?> DecideAsync(SafetyIncidentModel model, bool resolve, CancellationToken token)
    { var admin = currentUser.RequireUserId(); if (!await db.Users.AnyAsync(x => x.Id == admin && x.Role == UserRole.Admin && x.IsActive, token)) throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط."); var item = await db.SafetyIncidents.SingleOrDefaultAsync(x => x.Id == model.Id, token) ?? throw new ServiceException("safety_incident_not_found", "بلاغ السلامة غير موجود."); if (item.Status == SafetyIncidentStatus.Resolved || (!resolve && item.Status != SafetyIncidentStatus.Open)) return Result(item); item.Status = resolve ? SafetyIncidentStatus.Resolved : SafetyIncidentStatus.Acknowledged; item.AdminUserId = admin; item.AcknowledgedAtUtc ??= DateTime.UtcNow; if (resolve) item.ResolvedAtUtc = DateTime.UtcNow; item.AdminNote = model.Note?.Trim(); item.UpdatedAtUtc = DateTime.UtcNow; db.Notifications.Add(new Notification { UserId = item.UserId, Type = NotificationType.Safety, Title = resolve ? "أُغلق بلاغ السلامة" : "تم استلام بلاغ السلامة", Body = resolve ? "راجعت الإدارة البلاغ وأغلقته. يمكنك التواصل مع الدعم إن احتجت مساعدة أخرى." : "استلمت الإدارة بلاغك وتراجعه الآن.", DataJson = $"{{\"safetyIncidentId\":{item.Id}}}" }); await db.SaveChangesAsync(token); return Result(item); }
    private static object Result(YemenDrive.Database.Entities.SafetyIncident x) => new { x.Id, x.RideId, x.Message, x.Latitude, x.Longitude, x.LocationObservedAtUtc, x.Status, x.AdminUserId, x.AcknowledgedAtUtc, x.ResolvedAtUtc, x.AdminNote, x.CreatedAtUtc };
}
