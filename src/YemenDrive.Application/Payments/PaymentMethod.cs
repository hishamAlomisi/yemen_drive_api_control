using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Administration;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Payments;

public sealed class PaymentMethod(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser,
    FinancialAccountProvisioningService financialAccounts,
    IWalletLoginConfiguration? walletLogin = null)
    : OperationsService<PaymentMethodModel>(config)
{
    protected override async Task<object?> AddAsync(PaymentMethodModel model, CancellationToken token)
    {
        await RequireAdminAsync(token); Validate(model);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (await db.PaymentMethods.AnyAsync(x => x.Code == model.Code!.Trim(), token))
            throw new ServiceException("payment_method_exists", "رمز طريقة الدفع مستخدم مسبقاً.");
        var entity = new YemenDrive.Database.Entities.PaymentMethod();
        Apply(entity, model);
        await ConfigureWalletAsync(entity, model, token);
        db.PaymentMethods.Add(entity);
        await db.SaveChangesAsync(token);
        await financialAccounts.ProvisionPaymentMethodAsync(entity, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Result(entity);
    }
    protected override async Task<object?> UpdateAsync(PaymentMethodModel model, CancellationToken token)
    {
        await RequireAdminAsync(token); Validate(model);
        if (model.Id is null) throw new ServiceException("id_required", "معرف طريقة الدفع مطلوب.");
        var entity = await db.PaymentMethods.SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("payment_method_not_found", "طريقة الدفع غير موجودة.");
        if (await db.PaymentMethods.AnyAsync(x => x.Id != entity.Id && x.Code == model.Code!.Trim(), token))
            throw new ServiceException("payment_method_exists", "رمز طريقة الدفع مستخدم مسبقاً.");
        var existingCode = entity.Code.Trim().ToUpperInvariant();
        var existingProviderCode = entity.ProviderCode.Trim().ToUpperInvariant();
        var hasRecordedPayments = await db.PaymentWalletOperations.AnyAsync(x => x.PaymentMethodId == entity.Id, token) ||
            await db.PaymentTransactions.AnyAsync(
            x => x.Provider.ToUpper() == existingCode || x.Provider.ToUpper() == existingProviderCode,
            token);
        if (hasRecordedPayments &&
            (!string.Equals(entity.Code, model.Code!.Trim(), StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(entity.ProviderCode, model.ProviderCode!.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ServiceException(
                "payment_method_identifier_locked",
                "لا يمكن تغيير رمز طريقة الدفع أو رمز مزودها بعد تسجيل حركات دفع مرتبطة بها.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        Apply(entity, model);
        await ConfigureWalletAsync(entity, model, token);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await financialAccounts.ProvisionPaymentMethodAsync(entity, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Result(entity);
    }
    protected override async Task<object?> DeleteAsync(PaymentMethodModel model, CancellationToken token)
    {
        await RequireAdminAsync(token);
        if (model.Id is null) throw new ServiceException("id_required", "معرف طريقة الدفع مطلوب.");
        var entity = await db.PaymentMethods.SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("payment_method_not_found", "طريقة الدفع غير موجودة.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (await db.PaymentWalletOperations.AnyAsync(x => x.PaymentMethodId == entity.Id, token))
            throw new ServiceException("payment_method_in_use", "لا يمكن حذف طريقة دفع مرتبطة بعمليات محافظ؛ يمكن تعطيلها.");
        await DeletionGuards.EnsurePaymentMethodCanBeDeletedAsync(db, entity.Code, entity.ProviderCode, token);
        await financialAccounts.RemovePaymentMethodAccountsAsync(entity, token);
        db.PaymentMethods.Remove(entity);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new { entity.Id };
    }
    private Task ConfigureWalletAsync(YemenDrive.Database.Entities.PaymentMethod entity, PaymentMethodModel model, CancellationToken token)
    {
        if (walletLogin is not null) return walletLogin.ConfigureAsync(db, entity, model, token);
        if (!string.IsNullOrWhiteSpace(model.ProviderOptionsJson) || model.ProviderLoginEnabled == true)
            throw new ServiceException("wallet_login_unavailable", "خدمة تسجيل دخول المحافظ غير مهيأة.");
        return Task.CompletedTask;
    }
    private async Task RequireAdminAsync(CancellationToken token) { var id=currentUser.RequireUserId(); if (!await db.Users.AnyAsync(x=>x.Id==id&&x.Role==UserRole.Admin&&x.IsActive,token)) throw new ServiceException("admin_required","هذه العملية للإدارة فقط."); }
    private static void Validate(PaymentMethodModel m) { if (string.IsNullOrWhiteSpace(m.Code)||string.IsNullOrWhiteSpace(m.NameAr)||string.IsNullOrWhiteSpace(m.DescriptionAr)||string.IsNullOrWhiteSpace(m.ProviderCode)) throw new ServiceException("invalid_payment_method","الرمز والاسم والوصف ورمز المزود مطلوبة."); if (!m.IsAvailableForRidePayment&&!m.IsAvailableForWalletTopUp) throw new ServiceException("invalid_payment_method","اختر موضع ظهور واحداً على الأقل."); }
    private static void Apply(YemenDrive.Database.Entities.PaymentMethod e, PaymentMethodModel m) { e.Code=m.Code!.Trim(); e.NameAr=m.NameAr!.Trim(); e.DescriptionAr=m.DescriptionAr!.Trim(); e.ImageUrl=string.IsNullOrWhiteSpace(m.ImageUrl)?null:m.ImageUrl.Trim(); e.Kind=m.Kind; e.ProviderCode=m.ProviderCode!.Trim(); e.PublicInstructionsAr=string.IsNullOrWhiteSpace(m.PublicInstructionsAr)?null:m.PublicInstructionsAr.Trim(); e.IsActive=m.IsActive; e.IsAvailableForRidePayment=m.IsAvailableForRidePayment; e.IsAvailableForWalletTopUp=m.IsAvailableForWalletTopUp; e.SortOrder=m.SortOrder; }
    internal static object Result(YemenDrive.Database.Entities.PaymentMethod x)=>new{x.Id,x.Code,x.NameAr,x.DescriptionAr,x.ImageUrl,x.Kind,x.ProviderCode,x.PublicInstructionsAr,x.IsActive,x.IsAvailableForRidePayment,x.IsAvailableForWalletTopUp,x.SortOrder,x.CreatedAtUtc,x.UpdatedAtUtc};
}
