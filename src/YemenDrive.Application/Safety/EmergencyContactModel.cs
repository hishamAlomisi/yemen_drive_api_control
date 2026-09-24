using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Safety;

public sealed record EmergencyContactModel(int? Id = null, string? Name = null, string? PhoneNumber = null, string? Relationship = null, bool? IsActive = null);

public sealed class EmergencyContact(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext currentUser)
    : OperationsService<EmergencyContactModel>(config)
{
    protected override async Task<object?> AddAsync(EmergencyContactModel model, CancellationToken token)
    {
        var userId = currentUser.RequireUserId();
        var name = Required(model.Name, "اسم جهة الطوارئ مطلوب.", 150);
        var phone = Required(model.PhoneNumber, "رقم جهة الطوارئ مطلوب.", 30);
        if (await db.EmergencyContacts.AnyAsync(x => x.UserId == userId && x.PhoneNumber == phone, token))
            throw new ServiceException("emergency_contact_exists", "رقم جهة الطوارئ محفوظ مسبقاً.");
        var item = new YemenDrive.Database.Entities.EmergencyContact { UserId = userId, Name = name, PhoneNumber = phone, Relationship = Optional(model.Relationship, 80), IsActive = model.IsActive ?? true };
        db.EmergencyContacts.Add(item); await db.SaveChangesAsync(token); return Result(item);
    }
    protected override async Task<object?> UpdateAsync(EmergencyContactModel model, CancellationToken token)
    {
        var item = await OwnedAsync(model.Id, token);
        if (model.Name is not null) item.Name = Required(model.Name, "اسم جهة الطوارئ مطلوب.", 150);
        if (model.PhoneNumber is not null)
        {
            var phone = Required(model.PhoneNumber, "رقم جهة الطوارئ مطلوب.", 30);
            if (await db.EmergencyContacts.AnyAsync(x => x.UserId == item.UserId && x.PhoneNumber == phone && x.Id != item.Id, token)) throw new ServiceException("emergency_contact_exists", "رقم جهة الطوارئ محفوظ مسبقاً.");
            item.PhoneNumber = phone;
        }
        if (model.Relationship is not null) item.Relationship = Optional(model.Relationship, 80);
        if (model.IsActive is not null) item.IsActive = model.IsActive.Value;
        item.UpdatedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(token); return Result(item);
    }
    protected override async Task<object?> DeleteAsync(EmergencyContactModel model, CancellationToken token)
    { var item = await OwnedAsync(model.Id, token); db.EmergencyContacts.Remove(item); await db.SaveChangesAsync(token); return new { item.Id }; }
    protected override async Task<object?> GetAsync(EmergencyContactModel model, CancellationToken token)
    { if (model.Id is null) return (await db.EmergencyContacts.AsNoTracking().Where(x => x.UserId == currentUser.RequireUserId()).OrderByDescending(x => x.IsActive).ThenBy(x => x.Name).ToListAsync(token)).Select(Result).ToList(); return Result(await OwnedAsync(model.Id, token, true)); }
    private async Task<YemenDrive.Database.Entities.EmergencyContact> OwnedAsync(int? id, CancellationToken token, bool noTracking = false)
    { if (id is not int value) throw new ServiceException("emergency_contact_id_required", "معرف جهة الطوارئ مطلوب."); IQueryable<YemenDrive.Database.Entities.EmergencyContact> q = db.EmergencyContacts; if (noTracking) q = q.AsNoTracking(); return await q.SingleOrDefaultAsync(x => x.Id == value && x.UserId == currentUser.RequireUserId(), token) ?? throw new ServiceException("emergency_contact_not_found", "جهة الطوارئ غير موجودة."); }
    private static string Required(string? value, string error, int max) { var result = value?.Trim(); if (string.IsNullOrWhiteSpace(result) || result.Length > max) throw new ServiceException("invalid_emergency_contact", error); return result; }
    private static string? Optional(string? value, int max) { var result = value?.Trim(); if (result?.Length > max) throw new ServiceException("invalid_emergency_contact", "بيانات جهة الطوارئ أطول من المسموح."); return string.IsNullOrWhiteSpace(result) ? null : result; }
    private static object Result(YemenDrive.Database.Entities.EmergencyContact x) => new { x.Id, x.Name, x.PhoneNumber, x.Relationship, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc };
}
