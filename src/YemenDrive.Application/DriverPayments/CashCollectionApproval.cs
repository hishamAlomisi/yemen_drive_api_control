using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using CashCollectionApprovalEntity = YemenDrive.Database.Entities.CashCollectionApproval;

namespace YemenDrive.Application.DriverPayments;

/// <summary>Customer decision for a wallet-funded cash shortfall. No debit is made before approval.</summary>
public sealed class CashCollectionApproval(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<CashCollectionApprovalModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "accept", "reject"];

    protected override async Task<object?> GetAsync(CashCollectionApprovalModel model, CancellationToken token)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف طلب الموافقة مطلوب.");
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        var approval = await db.CashCollectionApprovals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.Id && (x.CustomerId == userId || x.DriverId == userId), token)
            ?? throw new ServiceException("cash_approval_not_found", "طلب الموافقة غير موجود.");
        return ToResult(approval);
    }

    protected override Task<object?> AcceptAsync(CashCollectionApprovalModel model, CancellationToken token) => DecideAsync(model, true, token);
    protected override Task<object?> RejectAsync(CashCollectionApprovalModel model, CancellationToken token) => DecideAsync(model, false, token);

    private async Task<object?> DecideAsync(CashCollectionApprovalModel model, bool approve, CancellationToken token)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف طلب الموافقة مطلوب.");
        var customerId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كعميل.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var approval = await db.CashCollectionApprovals.SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("cash_approval_not_found", "طلب الموافقة غير موجود.");
        if (approval.CustomerId != customerId) throw new ServiceException("cash_approval_access_denied", "لا تملك صلاحية اتخاذ هذا القرار.");
        if (approval.Status != CashCollectionApprovalStatus.Pending) return ToResult(approval);
        if (!approve)
        {
            approval.Status = CashCollectionApprovalStatus.Rejected;
            approval.DecisionNote = string.IsNullOrWhiteSpace(model.Note) ? "رفض العميل خصم الفرق من محفظته." : model.Note.Trim();
            approval.DecidedAtUtc = DateTime.UtcNow;
            db.Notifications.Add(new Notification { UserId = approval.DriverId, Type = NotificationType.Payment, Title = "رفض العميل تغطية الفرق", Body = "لم يوافق العميل على خصم الفرق من محفظته؛ لم تُسجل عملية التحصيل." });
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return ToResult(approval);
        }
        var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == customerId, token)
            ?? throw new ServiceException("wallet_not_found", "محفظة العميل غير موجودة.");
        if (wallet.Balance < approval.WalletDebitAmount)
        {
            approval.Status = CashCollectionApprovalStatus.InsufficientBalance;
            approval.DecisionNote = "رصيد المحفظة لم يعد كافياً عند الموافقة.";
            approval.DecidedAtUtc = DateTime.UtcNow;
            db.Notifications.Add(new Notification { UserId = approval.DriverId, Type = NotificationType.Payment, Title = "تعذر إكمال التحصيل", Body = "رصيد محفظة العميل لا يكفي لتغطية الفرق؛ لم تُسجل عملية التحصيل." });
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return ToResult(approval);
        }
        // The payment posting itself is completed by the driver's resubmission after approval.
        // This preserves one financial entry point and prevents a hidden debit in the approval action.
        approval.Status = CashCollectionApprovalStatus.Approved;
        approval.DecisionNote = string.IsNullOrWhiteSpace(model.Note) ? "وافق العميل على تغطية الفرق من المحفظة." : model.Note.Trim();
        approval.DecidedAtUtc = DateTime.UtcNow;
        db.Notifications.Add(new Notification { UserId = approval.DriverId, Type = NotificationType.Payment, Title = "وافق العميل على تغطية الفرق", Body = "يمكنك الآن إعادة تسجيل التحصيل بالمبلغ نفسه لإكمال الرحلة." });
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return ToResult(approval);
    }

    internal static object ToResult(CashCollectionApprovalEntity x) => new { x.Id, x.RideId, x.DriverId, x.CustomerId, x.CashReceived, x.WalletDebitAmount, x.Currency, x.Status, x.DecidedAtUtc, x.DecisionNote, x.CreatedAtUtc };
}
