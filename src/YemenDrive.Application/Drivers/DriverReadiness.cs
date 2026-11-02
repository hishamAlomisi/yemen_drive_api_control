using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Drivers;

public sealed record DriverReadinessModel(bool? AcceptsNewRequests = null);
public sealed class DriverReadiness(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ModelService<DriverReadinessModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "update"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, DriverReadinessModel model, CancellationToken ct)
    {
        var id = user.RequireUserId();
        var profile = await db.DriverProfiles.SingleOrDefaultAsync(x => x.UserId == id, ct)
            ?? throw new ServiceException("driver_required", "هذا الخيار متاح للسائق فقط.");
        if (operation == "update")
        {
            profile.AcceptsNewRequests = model.AcceptsNewRequests ?? throw new ServiceException("choice_required", "حدد حالة استقبال الطلبات.");
            profile.UpdatedAtUtc = DateTime.UtcNow;
            db.OperationalDecisionAudits.Add(new() { ActorUserId = id, Subject = "DriverReadiness", SubjectId = profile.Id,
                Action = profile.AcceptsNewRequests ? "online" : "offline", Reason = "اختيار السائق استقبال الطلبات الجديدة" });
            await db.SaveChangesAsync(ct);
        }
        var busy = await db.Rides.AnyAsync(x => x.DriverId == id && x.ServiceEndedAtUtc == null &&
            (x.Status == RideStatus.DriverAssigned || x.Status == RideStatus.DriverEnRoute || x.Status == RideStatus.DriverArrived || x.Status == RideStatus.InProgress || x.Status == RideStatus.CancellationPending), ct);
        return new { profile.AcceptsNewRequests, profile.IsApproved, enabledByCompany = profile.IsAvailable,
            busy, canReceiveRequests = profile.AcceptsNewRequests && profile.IsApproved && profile.IsAvailable && !busy && profile.ApprovedPassengerSeats > 0 &&
                await db.Users.AnyAsync(x => x.Id == id && x.IsActive, ct) && await db.DriverLiveLocations.AnyAsync(x => x.DriverId == id && x.IsOnline && x.ObservedAtUtc >= DateTime.UtcNow.AddMinutes(-5), ct) };
    }
}
