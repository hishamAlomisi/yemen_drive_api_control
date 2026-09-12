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
        Validate(model);
        var userId = currentUser.RequireUserId();
        if (!await db.Users.AnyAsync(x => x.Id == userId, token))
            throw new ServiceException("user_not_found", "المستخدم غير موجود.");
        var item = new SavedPlaceEntity { UserId = userId, Label = model.Label!.Trim(), Kind = string.IsNullOrWhiteSpace(model.Kind) ? "place" : model.Kind.Trim(), Address = model.Address!.Trim(), Latitude = model.Latitude!.Value, Longitude = model.Longitude!.Value };
        db.SavedPlaces.Add(item); await db.SaveChangesAsync(token); return Result(item);
    }
    protected override async Task<object?> UpdateAsync(SavedPlaceModel model, CancellationToken token)
    {
        var item = await FindAsync(model, token);
        if (model.Label is not null) item.Label = model.Label.Trim(); if (model.Kind is not null) item.Kind = model.Kind.Trim(); if (model.Address is not null) item.Address = model.Address.Trim();
        if (model.Latitude is not null) item.Latitude = model.Latitude.Value; if (model.Longitude is not null) item.Longitude = model.Longitude.Value;
        await db.SaveChangesAsync(token); return Result(item);
    }
    protected override async Task<object?> DeleteAsync(SavedPlaceModel model, CancellationToken token)
    { var item = await FindAsync(model, token); db.SavedPlaces.Remove(item); await db.SaveChangesAsync(token); return new { item.Id }; }
    protected override async Task<object?> GetAsync(SavedPlaceModel model, CancellationToken token) => Result(await FindAsync(model, token, true));
    private async Task<SavedPlaceEntity> FindAsync(SavedPlaceModel model, CancellationToken token, bool noTracking = false)
    { if (model.Id is null) throw new ServiceException("place_identity_required", "معرف المكان مطلوب."); var userId = currentUser.RequireUserId(); IQueryable<SavedPlaceEntity> query = db.SavedPlaces; if (noTracking) query = query.AsNoTracking(); return await query.SingleOrDefaultAsync(x => x.Id == model.Id && x.UserId == userId, token) ?? throw new ServiceException("place_not_found", "المكان المحفوظ غير موجود."); }
    private static void Validate(SavedPlaceModel model) { if (string.IsNullOrWhiteSpace(model.Label) || string.IsNullOrWhiteSpace(model.Address)) throw new ServiceException("place_invalid", "اسم وعنوان المكان مطلوبان."); if (model.Latitude is null || model.Longitude is null) throw new ServiceException("coordinates_required", "إحداثيات المكان مطلوبة."); }
    private static object Result(SavedPlaceEntity x) => new { x.Id, x.UserId, x.Label, x.Kind, x.Address, x.Latitude, x.Longitude, x.CreatedAtUtc, x.UpdatedAtUtc };
}
