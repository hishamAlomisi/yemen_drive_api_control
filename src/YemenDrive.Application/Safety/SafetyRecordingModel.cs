using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Safety;

public sealed record SafetyRecordingModel(
    int? Id = null,
    int? RideId = null,
    bool? Consent = null,
    long? DurationMilliseconds = null);

public sealed class SafetyRecording(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<SafetyRecordingModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["add", "create", "update"];

    protected override async Task<object?> AddAsync(SafetyRecordingModel model, CancellationToken token)
    {
        var userId = RequireUserId();
        if (model.Consent != true)
            throw new ServiceException("safety_recording_consent_required", "يلزم الحصول على موافقة صريحة قبل بدء تسجيل السلامة.");
        if (model.RideId is not int rideId)
            throw new ServiceException("ride_required", "معرف الرحلة مطلوب لتسجيل السلامة.");

        var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.CustomerId != userId && ride.DriverId != userId)
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية تسجيل هذه الرحلة.");
        if (ride.Status is RideStatus.Completed or RideStatus.Cancelled)
            throw new ServiceException("ride_not_recordable", "لا يمكن بدء تسجيل سلامة لرحلة منتهية أو ملغاة.");

        var active = await db.EmergencyRecordings
            .SingleOrDefaultAsync(x => x.UserId == userId && x.RideId == rideId && x.EndedAtUtc == null, token);
        // A prior client can disappear while offline. Seal that partial session
        // before starting another one so chunk sequence numbers can never mix.
        if (active is not null)
            active.EndedAtUtc = DateTime.UtcNow;

        var now = DateTime.UtcNow;
        var entity = new EmergencyRecording
        {
            UserId = userId,
            RideId = rideId,
            StorageKey = $"safety/{Guid.NewGuid():N}",
            ConsentAtUtc = now,
            StartedAtUtc = now,
        };
        db.EmergencyRecordings.Add(entity);
        await db.SaveChangesAsync(token);
        return ToResult(entity);
    }

    protected override Task<object?> UpdateAsync(SafetyRecordingModel model, CancellationToken token) =>
        FinalizeAsync(model, token);

    private async Task<object?> FinalizeAsync(SafetyRecordingModel model, CancellationToken token)
    {
        var entity = await FindOwnedAsync(model.Id, token);
        if (entity.EndedAtUtc is not null) return ToResult(entity);
        entity.DurationMilliseconds = Math.Max(0, model.DurationMilliseconds ?? 0);
        entity.EndedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        return ToResult(entity);
    }

    private async Task<EmergencyRecording> FindOwnedAsync(int? id, CancellationToken token)
    {
        if (id is not int recordingId)
            throw new ServiceException("recording_id_required", "معرف تسجيل السلامة مطلوب.");
        var userId = RequireUserId();
        return await db.EmergencyRecordings.SingleOrDefaultAsync(x => x.Id == recordingId && x.UserId == userId, token)
            ?? throw new ServiceException("safety_recording_not_found", "تسجيل السلامة غير موجود أو غير متاح.");
    }

    private int RequireUserId() => currentUser.UserId
        ?? throw new ServiceException("authentication_required", "يلزم تسجيل الدخول لاستخدام تسجيل السلامة.");

    private static object ToResult(EmergencyRecording x) => new
    {
        x.Id,
        x.RideId,
        x.UploadedBytes,
        x.DurationMilliseconds,
        x.StartedAtUtc,
        x.LastChunkAtUtc,
        x.EndedAtUtc
    };
}
