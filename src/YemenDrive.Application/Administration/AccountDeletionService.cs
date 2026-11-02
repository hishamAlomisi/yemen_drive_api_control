using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Administration;

public sealed record AccountDeletionModel(int? Id = null, string? Reason = null, string? Decision = null);
public sealed class AccountDeletionService(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ModelService<AccountDeletionModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "list", "add", "update"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, AccountDeletionModel model, CancellationToken ct)
    {
        var actor = user.RequireUserId();
        var admin = await db.Users.AnyAsync(x => x.Id == actor && x.IsActive && x.Role == UserRole.Admin, ct);
        if (admin && operation != "list") await AdministratorAccess.RequireAsync(db, actor, operation == "get" ? "personal_data.read" : "personal_data.write", ct);
        if (operation == "list")
        {
            if (admin) await AdministratorAccess.RequireAsync(db, actor, "personal_data.read", ct);
            return await db.AccountDeletionRequests.AsNoTracking().Where(x => admin || x.UserId == actor).OrderByDescending(x => x.Id).Take(200).Select(x => new { x.Id, x.UserId, x.Status, x.Reason, x.AdminNote, x.DecidedByUserId, x.DecidedAtUtc, x.CreatedAtUtc }).ToListAsync(ct);
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        AccountDeletionRequest? item = null;
        if (operation == "add")
        {
            if (string.IsNullOrWhiteSpace(model.Reason)) throw new ServiceException("deletion_reason_required", "أكد فهمك لأثر الحذف واذكر سبب الطلب.");
            item = await db.AccountDeletionRequests.SingleOrDefaultAsync(x => x.UserId == actor && x.Status == "Pending", ct);
            if (item == null) { item = new() { UserId = actor, Reason = model.Reason.Trim() }; db.AccountDeletionRequests.Add(item); }
        }
        else item = await db.AccountDeletionRequests.SingleOrDefaultAsync(x => x.Id == model.Id && (admin || x.UserId == actor), ct);
        if (item == null) throw new ServiceException("deletion_request_not_found", "طلب الحذف غير موجود.");
        var balance = await db.Wallets.Where(x => x.UserId == item.UserId).Select(x => (decimal?)x.Balance).SingleOrDefaultAsync(ct) ?? 0m;
        var openFinance = await db.RideFinancialCases.AnyAsync(x => (x.CustomerId == item.UserId || x.DriverId == item.UserId) && x.Status != "Resolved", ct);
        var openRide = await db.Rides.AnyAsync(x => (x.CustomerId == item.UserId || x.DriverId == item.UserId) && x.ServiceEndedAtUtc == null && x.Status != RideStatus.Cancelled && x.Status != RideStatus.Completed && x.Status != RideStatus.Draft, ct);
        var providerPending = await db.PaymentWalletOperations.AnyAsync(x => x.CustomerId == item.UserId && x.IsOpen, ct);
        var debt = await db.DriverSettlements.AnyAsync(x => x.DriverId == item.UserId && x.Status == PaymentStatus.Pending && x.NetPayable < 0, ct);
        var refundPending = await db.RideCancellationRequests.AnyAsync(x => (x.CustomerId == item.UserId || x.DriverId == item.UserId) && x.Status != RideCancellationStatus.AdminApproved && x.Status != RideCancellationStatus.DriverCancelled && x.Status != RideCancellationStatus.CustomerApproved, ct);
        if (operation == "update")
        {
            if (!admin) throw new ServiceException("administrator_required", "قرار طلب الحذف للإدارة المفوضة.");
            await AdministratorAccess.RequireAsync(db, actor, "personal_data.write", ct);
            if (item.Status != "Pending") throw new ServiceException("deletion_decision_closed", "سبق اتخاذ القرار في هذا الطلب.");
            if (string.IsNullOrWhiteSpace(model.Reason) || model.Decision is not ("Approve" or "Reject")) throw new ServiceException("deletion_decision_required", "حدد القرار والأساس النظامي للاحتفاظ بالسجلات أو تسويتها.");
            if (model.Decision == "Approve")
            {
                if (balance != 0 || openFinance || openRide || providerPending || debt || refundPending) throw new ServiceException("deletion_financial_review_required", "توجد أموال أو التزامات أو خدمة أو استرداد مفتوح. سوِّها مع صاحب الحساب قبل تعطيله.");
                var account = await db.Users.SingleAsync(x => x.Id == item.UserId, ct);
                if (account.Role == UserRole.Admin) throw new ServiceException("administrator_deletion_requires_review", "تحويل صلاحيات الإدارة وحذف حسابها يحتاج مراجعة مستقلة.");
                account.IsActive = false;
                foreach (var session in await db.AuthSessions.Where(x => x.UserId == item.UserId && x.RevokedAtUtc == null).ToListAsync(ct)) session.RevokedAtUtc = DateTime.UtcNow;
                foreach (var device in await db.TrustedDevices.Where(x => x.UserId == item.UserId && x.RevokedAtUtc == null).ToListAsync(ct)) device.RevokedAtUtc = DateTime.UtcNow;
                item.Status = "AccountDisabledRetentionReview";
            }
            else item.Status = "Rejected";
            item.AdminNote = model.Reason.Trim(); item.DecidedByUserId = actor; item.DecidedAtUtc = DateTime.UtcNow;
            db.OperationalDecisionAudits.Add(new() { ActorUserId = actor, Subject = "AccountDeletion", SubjectId = item.Id, Action = model.Decision, Reason = item.AdminNote });
            db.Notifications.Add(new() { UserId = item.UserId, Type = NotificationType.System, Title = "قرار طلب حذف الحساب", Body = model.Decision == "Approve" ? "عُطّل حسابك وأُلغيت جلساته. تحفظ السجلات المالية والتاريخية وفق سياسة الاحتفاظ المعتمدة؛ تراجع الإدارة حذف البيانات الأخرى." : "راجعت الإدارة طلب الحذف. يمكنك قراءة سبب القرار في طلبك." });
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { item.Id, item.UserId, item.Status, item.Reason, item.AdminNote, walletBalance = balance,
            financialReviewRequired = balance != 0 || openFinance || openRide || providerPending || debt || refundPending,
            notice = "تراجع الإدارة أموالك والتزاماتك قبل التعطيل، وتُحفظ الأدلة المالية والتاريخية وفق سياسة الشركة المعتمدة. لا يعد الطلب إقراراً بمسح السجلات." };
    }
}
