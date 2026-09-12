using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using SupportTicketEntity = YemenDrive.Database.Entities.SupportTicket;

namespace YemenDrive.Application.Support;

public sealed class SupportTicket(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<SupportTicketModel>(config)
{

    protected override async Task<object?> AddAsync(SupportTicketModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? model.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول لإرسال البلاغ.");
        if (string.IsNullOrWhiteSpace(model.Message))
            throw new ServiceException("message_required", "رسالة البلاغ مطلوبة.");

        var entity = new SupportTicketEntity
        {
            UserId = userId,
            Category = string.IsNullOrWhiteSpace(model.Category) ? "General" : model.Category.Trim(),
            Message = model.Message.Trim(),
            Status = "Open"
        };
        db.SupportTickets.Add(entity);
        await db.SaveChangesAsync(token);
        return ToResult(entity);
    }

    protected override async Task<object?> GetAsync(SupportTicketModel model, CancellationToken token) =>
        ToResult(await FindAsync(model.Id, token));

    protected override async Task<object?> UpdateAsync(SupportTicketModel model, CancellationToken token)
    {
        var entity = await FindAsync(model.Id, token);
        if (model.Status is not null) entity.Status = model.Status.Trim();
        if (model.AdminReply is not null) entity.AdminReply = model.AdminReply.Trim();
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (entity.Status.Equals("Resolved", StringComparison.OrdinalIgnoreCase)) entity.ResolvedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        return ToResult(entity);
    }

    protected override async Task<object?> DeleteAsync(SupportTicketModel model, CancellationToken token)
    {
        var entity = await FindAsync(model.Id, token);
        db.SupportTickets.Remove(entity);
        await db.SaveChangesAsync(token);
        return new { entity.Id };
    }

    private async Task<SupportTicketEntity> FindAsync(int? id, CancellationToken token)
    {
        if (id is null) throw new ServiceException("id_required", "معرف البلاغ مطلوب.");
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        return await db.SupportTickets.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, token)
            ?? throw new ServiceException("ticket_not_found", "البلاغ غير موجود.");
    }

    private static object ToResult(SupportTicketEntity x) => new
    {
        x.Id, x.UserId, x.Category, x.Message, x.Status, x.AdminReply, x.CreatedAtUtc, x.UpdatedAtUtc, x.ResolvedAtUtc
    };
}

public sealed class SupportTicketReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<SupportTicketModel>(config)
{
    protected override async Task<object?> ListAsync(SupportTicketModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? model.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        return await db.SupportTickets.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(200)
            .Select(x => new { x.Id, x.UserId, x.Category, x.Message, x.Status, x.AdminReply, x.CreatedAtUtc, x.UpdatedAtUtc, x.ResolvedAtUtc })
            .ToListAsync(token);
    }
}
