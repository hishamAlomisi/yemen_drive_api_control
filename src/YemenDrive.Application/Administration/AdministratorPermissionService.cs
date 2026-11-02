using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Administration;

public sealed record AdministratorPermissionModel(int? UserId = null, string[]? Permissions = null, string? Reason = null);
public sealed class AdministratorPermissionService(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ModelService<AdministratorPermissionModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "update", "mine", "list"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, AdministratorPermissionModel model, CancellationToken ct)
    {
        var actor = user.RequireUserId();
        if (operation == "mine") {
            return await ReadAsync(actor, actor, ct);
        }
        await AdministratorAccess.RequireAsync(db, actor, operation is "get" or "list" ? "permissions.read" : "permissions.write", ct);
        if (operation == "list") return await db.Users.AsNoTracking().Where(x => x.Role == UserRole.Admin && x.IsActive)
            .OrderBy(x => x.DisplayName).Select(x => new { x.Id, x.DisplayName, x.IsSystemAdministrator }).ToListAsync(ct);
        if (model.UserId == null || !await db.Users.AnyAsync(x => x.Id == model.UserId && x.Role == UserRole.Admin, ct))
            throw new ServiceException("administrator_required", "اختر حساباً إدارياً.");
        if (operation == "get") return await ReadAsync(model.UserId.Value, actor, ct);
        if (await db.Users.AnyAsync(x => x.Id == model.UserId && x.IsSystemAdministrator, ct))
            throw new ServiceException("system_administrator_permissions_protected", "صلاحيات مسؤول النظام الأصلية ممنوحة تلقائياً ولا تقبل التعديل.");
        if (model.UserId == actor) throw new ServiceException("self_permission_edit_denied", "تعديل تفويضك بنفسك غير متاح؛ يراجعه مدير مفوض آخر.");
        if (model.Permissions == null || string.IsNullOrWhiteSpace(model.Reason)) throw new ServiceException("permission_reason_required", "حدد التفويض وسبب التغيير.");
        var scopes = new[] { "permissions", "refunds", "support", "personal_data", "finance", "policy", "safety", "operations" };
        var allowed = scopes.SelectMany(x => new[] { x + ".read", x + ".write" }).Append("*").ToHashSet();
        if (model.Permissions.Any(x => !allowed.Contains(x))) throw new ServiceException("invalid_permission", "تفويض غير معروف.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        db.AdministratorPermissions.RemoveRange(await db.AdministratorPermissions.Where(x => x.UserId == model.UserId).ToListAsync(ct));
        foreach (var permission in model.Permissions.Distinct()) db.AdministratorPermissions.Add(new() { UserId = model.UserId.Value, Permission = permission });
        db.OperationalDecisionAudits.Add(new() { ActorUserId = actor, Subject = "AdministratorPermission", SubjectId = model.UserId.Value, Action = "update", Reason = model.Reason, ResultJson = System.Text.Json.JsonSerializer.Serialize(model.Permissions) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { model.UserId, model.Permissions };
    }
    private async Task<object> ReadAsync(int id, int actor, CancellationToken ct)
    {
        var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsActive && x.Role == UserRole.Admin, ct)
            ?? throw new ServiceException("administrator_required", "اختر حساب إدارة نشطاً.");
        var permissions = account.IsSystemAdministrator ? new List<string> { "*" }
            : await db.AdministratorPermissions.Where(x => x.UserId == id).Select(x => x.Permission).ToListAsync(ct);
        return new { userId = id, permissions, account.IsSystemAdministrator, canEdit = !account.IsSystemAdministrator && id != actor };
    }
}
