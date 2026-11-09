using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using Ticket = YemenDrive.Database.Entities.SupportTicket;

namespace YemenDrive.Application.Support;

public sealed class SupportTicket(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : OperationsService<SupportTicketModel>(config)
{
    protected override async Task<object?> AddAsync(SupportTicketModel model, CancellationToken ct)
    {
        var actor = user.RequireUserId();
        Validate(model.Message);
        if (model.RideId != null && !await db.Rides.AnyAsync(x => x.Id == model.RideId && (x.CustomerId == actor || x.DriverId == actor), ct))
            throw new ServiceException("ride_access_denied", "هذه الرحلة لا تخصك.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var owner = await db.Users.Where(x => x.IsActive && x.Role == UserRole.Admin && (x.IsSystemAdministrator || db.AdministratorPermissions.Any(p => p.UserId == x.Id && (p.Permission == "*" || p.Permission == "support.write")))).OrderBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
        var item = new Ticket { UserId = actor, RideId = model.RideId, Category = (model.Category ?? "General").Trim(), Message = model.Message!.Trim(), Status = "Open", AssignedAdminId = owner };
        if (item.Category.Length > 100) throw new ServiceException("invalid_category", "نوع البلاغ أطول من الحد المسموح.");
        db.SupportTickets.Add(item); await db.SaveChangesAsync(ct);
        await RecordAsync(item, actor, item.Message, model.EvidenceReference, owner, ct);
        await tx.CommitAsync(ct); return View(item);
    }
    protected override async Task<object?> GetAsync(SupportTicketModel model, CancellationToken ct)
    {
        var item = await FindAsync(model.Id, false, ct);
        return new { ticket = View(item), entries = await db.SupportTicketEntries.AsNoTracking().Where(x => x.SupportTicketId == item.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.ActorUserId, x.Message, x.EvidenceReference, x.Status, x.CreatedAtUtc }).ToListAsync(ct) };
    }
    protected override async Task<object?> UpdateAsync(SupportTicketModel model, CancellationToken ct)
    {
        var actor = user.RequireUserId();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var item = await FindAsync(model.Id, true, ct);
        var admin = await db.Users.AnyAsync(x => x.Id == actor && x.Role == UserRole.Admin && x.IsActive, ct);
        string message;
        if (admin)
        {
            if (model.Status != null && model.Status is not ("Open" or "Assigned" or "Investigating" or "AwaitingCustomer" or "Resolved" or "Closed")) throw new ServiceException("invalid_ticket_status", "حالة البلاغ غير معروفة.");
            if (model.AssignedAdminId != null) await AdministratorAccess.RequireAsync(db, model.AssignedAdminId.Value, "support.write", ct);
            message = model.AdminReply ?? model.Message ?? throw new ServiceException("reply_required", "سجل الرد أو أساس القرار.");
            Validate(message);
            item.AssignedAdminId = model.AssignedAdminId ?? actor;
            item.Status = model.Status ?? "Investigating"; item.AdminReply = message.Trim();
            item.LastDecisionByUserId = actor;
            db.OperationalDecisionAudits.Add(new() { ActorUserId = actor, Subject = "SupportTicket", SubjectId = item.Id, Action = item.Status, Reason = message.Trim() });
        }
        else
        {
            if (model.Status != null || model.AdminReply != null || model.AssignedAdminId != null) throw new ServiceException("support_admin_decision_required", "قرار البلاغ والرد الإداري متاحان للموظف المفوض فقط.");
            Validate(model.Message); message = model.Message!.Trim(); item.Status = "Open";
        }
        item.UpdatedAtUtc = DateTime.UtcNow;
        item.ResolvedAtUtc = item.Status is "Resolved" or "Closed" ? DateTime.UtcNow : null;
        await RecordAsync(item, actor, message, model.EvidenceReference, admin ? item.UserId : item.AssignedAdminId, ct);
        await tx.CommitAsync(ct); return View(item);
    }
    protected override Task<object?> DeleteAsync(SupportTicketModel model, CancellationToken ct) => throw new ServiceException("support_history_preserved", "يحفظ سجل البلاغ وقراراته؛ يمكن إغلاقه بعد المعالجة.");
    private async Task RecordAsync(Ticket item, int actor, string message, string? evidence, int? recipient, CancellationToken ct)
    {
        db.SupportTicketEntries.Add(new() { SupportTicketId = item.Id, ActorUserId = actor, Message = message.Trim(), EvidenceReference = evidence, Status = item.Status });
        if (recipient != null) db.Notifications.Add(new() { UserId = recipient.Value, Type = NotificationType.System, Title = item.Category == "Safety" ? "بلاغ سلامة يحتاج متابعة" : "تحديث البلاغ", Body = $"البلاغ #{item.Id}: {item.Status}. يمكنك قراءة الرد وسجل المعالجة من الدعم." });
        await db.SaveChangesAsync(ct);
    }
    private async Task<Ticket> FindAsync(int? id, bool write, CancellationToken ct)
    {
        var actor = user.RequireUserId();
        var admin = await db.Users.AnyAsync(x => x.Id == actor && x.IsActive && x.Role == UserRole.Admin, ct);
        if (admin) await AdministratorAccess.RequireAsync(db, actor, write ? "support.write" : "support.read", ct);
        return await db.SupportTickets.SingleOrDefaultAsync(x => x.Id == id && (admin || x.UserId == actor), ct) ?? throw new ServiceException("ticket_not_found", "البلاغ غير موجود.");
    }
    private static void Validate(string? message)
    { if (string.IsNullOrWhiteSpace(message) || message.Length > 10000) throw new ServiceException("message_required", "اكتب رسالة واضحة لا تتجاوز 10000 حرف."); }
    internal static object View(Ticket x) => new { x.Id, x.UserId, x.RideId, x.Category, x.Message, x.Status, x.AssignedAdminId, x.AdminReply, x.LastDecisionByUserId, x.CreatedAtUtc, x.UpdatedAtUtc, x.ResolvedAtUtc };
}
public sealed class SupportTicketReport(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ReportsService<SupportTicketModel>(config)
{
    protected override async Task<object?> ListAsync(SupportTicketModel model, CancellationToken ct)
    {
        var actor = user.RequireUserId();
        var admin = await db.Users.AnyAsync(x => x.Id == actor && x.IsActive && x.Role == UserRole.Admin, ct);
        if (admin) await AdministratorAccess.RequireAsync(db, actor, "support.read", ct);
        var items = await db.SupportTickets.AsNoTracking().Where(x => (admin || x.UserId == actor) && (model.RideId == null || x.RideId == model.RideId) && (model.Status == null || x.Status == model.Status) && (model.AssignedAdminId == null || x.AssignedAdminId == model.AssignedAdminId)).OrderByDescending(x => x.Id).Take(200).ToListAsync(ct);
        return items.Select(SupportTicket.View).ToList();
    }
}
