using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using SavedPlaceEntity = YemenDrive.Database.Entities.SavedPlace;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Places;

public sealed class SavedPlace(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<SavedPlaceModel>(config)
{
    protected override async Task<object?> AddAsync(SavedPlaceModel model, CancellationToken token)
    {
        return await SaveAsync(model, false, token);
    }
    protected override async Task<object?> UpdateAsync(SavedPlaceModel model, CancellationToken token)
    {
        return await SaveAsync(model, true, token);
    }
    protected override async Task<object?> DeleteAsync(SavedPlaceModel model, CancellationToken token)
    { var item = await FindAsync(model, token); db.SavedPlaces.Remove(item); await db.SaveChangesAsync(token); return new { item.Id }; }
    protected override async Task<object?> GetAsync(SavedPlaceModel model, CancellationToken token) => Result(await FindAsync(model, token, true));
    private async Task<SavedPlaceEntity> FindAsync(SavedPlaceModel model, CancellationToken token, bool noTracking = false)
    { if (model.Id is null) throw new ServiceException("place_identity_required", "معرف المكان مطلوب."); var userId = currentUser.RequireUserId(); IQueryable<SavedPlaceEntity> query = db.SavedPlaces; if (noTracking) query = query.AsNoTracking(); return await query.SingleOrDefaultAsync(x => x.Id == model.Id && x.UserId == userId, token) ?? throw new ServiceException("place_not_found", "المكان المحفوظ غير موجود."); }
    private async Task<object?> SaveAsync(SavedPlaceModel m, bool editing, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, ct)) throw new ServiceException("user_not_found", "المستخدم غير متاح.");
        // Lock the user's indexed range so concurrent saves cannot both create the same named shortcut.
        var places = await db.SavedPlaces.Where(x => x.UserId == userId).OrderByDescending(x => x.UpdatedAtUtc ?? x.CreatedAtUtc).ThenByDescending(x => x.Id).ToListAsync(ct);
        var original = editing ? places.SingleOrDefault(x => x.Id == m.Id) ?? throw new ServiceException("place_not_found", "المكان المحفوظ غير موجود.") : null;
        var kind = (m.Kind ?? original?.Kind ?? "place").Trim().ToLowerInvariant();
        var label = Clean(m.Label ?? original?.Label ?? "");
        var address = (m.Address ?? original?.Address ?? "").Trim();
        var latitude = m.Latitude ?? original?.Latitude; var longitude = m.Longitude ?? original?.Longitude;
        var standard = kind is "home" or "work" or "park" or "mall" or "market";
        if (kind == "unnamed") label = "";
        else if (standard) label = kind switch { "home" => "المنزل", "work" => "العمل", "park" => "الحديقة", "mall" => "المول", _ => "السوق" };
        if ((kind != "unnamed" && label.Length == 0) || label.Length > 200 || address.Length == 0 || address.Length > 1000 || kind.Length == 0 || kind.Length > 32)
            throw new ServiceException("place_invalid", "أدخل اسم المكان أو اختر بدون اسم، وعنواناً صحيحاً.");
        if (latitude is not double lat || longitude is not double lng || !double.IsFinite(lat) || !double.IsFinite(lng) || lat < -90 || lat > 90 || lng < -180 || lng > 180 || lat == 0 && lng == 0)
            throw new ServiceException("coordinates_required", "حدد إحداثيات مكان صحيحة أولاً.");
        var key = SavedPlaceLocationKey.Create(lat, lng);
        var matches = places.Where(x => kind == "unnamed"
            ? (x.Kind == "unnamed" || string.IsNullOrWhiteSpace(x.Label)) && x.LocationKey == key
            : string.Equals(Clean(x.Label), label, StringComparison.OrdinalIgnoreCase) || standard && x.Kind == kind).ToList();
        var item = original ?? matches.FirstOrDefault() ?? new SavedPlaceEntity { UserId = userId };
        db.SavedPlaces.RemoveRange(matches.Where(x => x.Id != item.Id));
        item.Label = label; item.Kind = kind; item.Address = address; item.Latitude = lat; item.Longitude = lng; item.LocationKey = key; item.UpdatedAtUtc = DateTime.UtcNow;
        if (item.Id == 0) db.SavedPlaces.Add(item);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Result(item);
    }
    private static string Clean(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ");
    private static object Result(SavedPlaceEntity x) => new { x.Id, x.UserId, x.Label, x.Kind, x.Address, x.Latitude, x.Longitude, x.CreatedAtUtc, x.UpdatedAtUtc };
}
