using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Payments;

public sealed class PaymentMethod(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext currentUser)
    : OperationsService<PaymentMethodModel>(config)
{
    protected override async Task<object?> AddAsync(PaymentMethodModel model, CancellationToken token)
    {
        await RequireAdminAsync(token); Validate(model);
        if (await db.PaymentMethods.AnyAsync(x => x.Code == model.Code!.Trim(), token))
            throw new ServiceException("payment_method_exists", "رمز طريقة الدفع مستخدم مسبقاً.");
        var entity = new YemenDrive.Database.Entities.PaymentMethod(); Apply(entity, model);
        db.PaymentMethods.Add(entity); await db.SaveChangesAsync(token); return Result(entity);
    }
    protected override async Task<object?> UpdateAsync(PaymentMethodModel model, CancellationToken token)
    {
        await RequireAdminAsync(token); Validate(model);
        if (model.Id is null) throw new ServiceException("id_required", "معرف طريقة الدفع مطلوب.");
        var entity = await db.PaymentMethods.SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("payment_method_not_found", "طريقة الدفع غير موجودة.");
        if (await db.PaymentMethods.AnyAsync(x => x.Id != entity.Id && x.Code == model.Code!.Trim(), token))
            throw new ServiceException("payment_method_exists", "رمز طريقة الدفع مستخدم مسبقاً.");
        Apply(entity, model); entity.UpdatedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(token); return Result(entity);
    }
    protected override async Task<object?> DeleteAsync(PaymentMethodModel model, CancellationToken token)
    {
        await RequireAdminAsync(token);
        if (model.Id is null) throw new ServiceException("id_required", "معرف طريقة الدفع مطلوب.");
        var entity = await db.PaymentMethods.SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("payment_method_not_found", "طريقة الدفع غير موجودة.");
        entity.IsActive = false; entity.IsAvailableForRidePayment = false; entity.IsAvailableForWalletTopUp = false; entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token); return Result(entity);
    }
    private async Task RequireAdminAsync(CancellationToken token) { var id=currentUser.RequireUserId(); if (!await db.Users.AnyAsync(x=>x.Id==id&&x.Role==UserRole.Admin&&x.IsActive,token)) throw new ServiceException("admin_required","هذه العملية للإدارة فقط."); }
    private static void Validate(PaymentMethodModel m) { if (string.IsNullOrWhiteSpace(m.Code)||string.IsNullOrWhiteSpace(m.NameAr)||string.IsNullOrWhiteSpace(m.DescriptionAr)||string.IsNullOrWhiteSpace(m.ProviderCode)) throw new ServiceException("invalid_payment_method","الرمز والاسم والوصف ورمز المزود مطلوبة."); if (!m.IsAvailableForRidePayment&&!m.IsAvailableForWalletTopUp) throw new ServiceException("invalid_payment_method","اختر موضع ظهور واحداً على الأقل."); }
    private static void Apply(YemenDrive.Database.Entities.PaymentMethod e, PaymentMethodModel m) { e.Code=m.Code!.Trim(); e.NameAr=m.NameAr!.Trim(); e.DescriptionAr=m.DescriptionAr!.Trim(); e.ImageUrl=string.IsNullOrWhiteSpace(m.ImageUrl)?null:m.ImageUrl.Trim(); e.Kind=m.Kind; e.ProviderCode=m.ProviderCode!.Trim(); e.PublicInstructionsAr=string.IsNullOrWhiteSpace(m.PublicInstructionsAr)?null:m.PublicInstructionsAr.Trim(); e.IsActive=m.IsActive; e.IsAvailableForRidePayment=m.IsAvailableForRidePayment; e.IsAvailableForWalletTopUp=m.IsAvailableForWalletTopUp; e.SortOrder=m.SortOrder; }
    internal static object Result(YemenDrive.Database.Entities.PaymentMethod x)=>new{x.Id,x.Code,x.NameAr,x.DescriptionAr,x.ImageUrl,x.Kind,x.ProviderCode,x.PublicInstructionsAr,x.IsActive,x.IsAvailableForRidePayment,x.IsAvailableForWalletTopUp,x.SortOrder,x.CreatedAtUtc,x.UpdatedAtUtc};
}
