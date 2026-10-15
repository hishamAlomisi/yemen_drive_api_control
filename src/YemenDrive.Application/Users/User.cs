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
using UserEntity = YemenDrive.Database.Entities.User;

namespace YemenDrive.Application.Users;

public sealed class User(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser,
    FinancialAccountProvisioningService financialAccounts) : OperationsService<UserModel>(configurationStore)
{
    protected override string GetSuccessMessage(string operation) => operation.ToLowerInvariant() switch
    {
        "add" or "create" => "تم إنشاء المستخدم بنجاح.",
        "update" => "تم تعديل المستخدم بنجاح.",
        "delete" => "تم حذف المستخدم بنجاح.",
        "get" => "تم جلب بيانات المستخدم بنجاح.",
        _ => base.GetSuccessMessage(operation)
    };

    protected override ValueTask<IReadOnlyCollection<ValidationError>> ValidateAsync(
        string operation, UserModel model, CancellationToken cancellationToken)
    {
        List<ValidationError> errors = [];
        var isCreate = operation.Equals("add", StringComparison.OrdinalIgnoreCase) ||
                       operation.Equals("create", StringComparison.OrdinalIgnoreCase);

        if (isCreate && string.IsNullOrWhiteSpace(model.DisplayName))
            errors.Add(new("displayName", "اسم المستخدم مطلوب."));
        if (isCreate && (string.IsNullOrWhiteSpace(model.PhoneNumber) || model.PhoneNumber.Trim().Length < 7))
            errors.Add(new("phoneNumber", "رقم الهاتف غير صحيح."));
        if (isCreate && (string.IsNullOrWhiteSpace(model.Password) || model.Password.Length < 8))
            errors.Add(new("password", "كلمة المرور يجب ألا تقل عن 8 أحرف."));
        if (!isCreate && currentUser.UserId is null)
            errors.Add(new("authentication", "يجب تسجيل الدخول لهذه العملية."));

        return ValueTask.FromResult<IReadOnlyCollection<ValidationError>>(errors);
    }

    protected override async Task<object?> AddAsync(UserModel model, CancellationToken cancellationToken)
    {
        var isAdmin = await IsAdministratorAsync(cancellationToken);
        if (!isAdmin && model.Role != UserRole.Customer)
            throw new ServiceException("admin_required", "تعيين دور سائق أو مدير متاح للإدارة فقط.");

        var phone = model.PhoneNumber!.Trim();
        if (await dbContext.Users.AnyAsync(x => x.PhoneNumber == phone, cancellationToken))
            throw new ServiceException("user_exists", "رقم الهاتف مسجل مسبقاً.");

        var entity = new UserEntity
        {
            PhoneNumber = phone,
            DisplayName = model.DisplayName!.Trim(),
            Email = NormalizeEmail(model.Email),
            PasswordHash = PasswordHash.Create(model.Password!),
            Role = isAdmin ? model.Role : UserRole.Customer,
            IsActive = model.IsActive,
            Gender = model.Gender,
            Street = model.Street,
            City = model.City,
            District = model.District,
            Wallet = new() { Currency = "YER" }
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Users.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await financialAccounts.ProvisionUserAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(entity);
    }

    protected override async Task<object?> UpdateAsync(UserModel model, CancellationToken cancellationToken)
    {
        var (entity, requesterId, isAdmin) = await FindTargetUserAsync(model, cancellationToken, includeDriverProfile: true);

        if (!string.IsNullOrWhiteSpace(model.PhoneNumber) && model.PhoneNumber.Trim().Length < 7)
            throw new ServiceException("invalid_phone_number", "رقم الهاتف غير صحيح.");
        if (!string.IsNullOrWhiteSpace(model.Password) && model.Password.Length < 8)
            throw new ServiceException("invalid_password", "كلمة المرور يجب ألا تقل عن 8 أحرف.");

        if (!string.IsNullOrWhiteSpace(model.PhoneNumber) && model.PhoneNumber.Trim() != entity.PhoneNumber)
        {
            var phone = model.PhoneNumber.Trim();
            if (await dbContext.Users.AnyAsync(x => x.Id != entity.Id && x.PhoneNumber == phone, cancellationToken))
                throw new ServiceException("user_exists", "رقم الهاتف مسجل مسبقاً.");
            entity.PhoneNumber = phone;
        }

        if (!string.IsNullOrWhiteSpace(model.DisplayName)) entity.DisplayName = model.DisplayName.Trim();
        if (model.Email is not null) entity.Email = NormalizeEmail(model.Email);
        if (!string.IsNullOrWhiteSpace(model.Password)) entity.PasswordHash = PasswordHash.Create(model.Password);
        if (isAdmin)
        {
            await EnsureAdminCanChangeAccessAsync(entity, model, requesterId, cancellationToken);
            entity.Role = model.Role;
            entity.IsActive = model.IsActive;
        }
        entity.Gender = model.Gender ?? entity.Gender;
        entity.Street = model.Street ?? entity.Street;
        entity.City = model.City ?? entity.City;
        entity.District = model.District ?? entity.District;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await financialAccounts.ProvisionUserAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(entity);
    }

    protected override async Task<object?> DeleteAsync(UserModel model, CancellationToken cancellationToken)
    {
        var (entity, requesterId, isAdmin) = await FindTargetUserAsync(model, cancellationToken);
        if (!isAdmin)
            throw new ServiceException("admin_required", "حذف المستخدمين متاح للإدارة فقط.");
        if (entity.Id == requesterId)
            throw new ServiceException("cannot_delete_current_admin", "لا يمكن حذف حساب المدير الذي تستخدمه حالياً.");
        if (entity.Role == UserRole.Admin &&
            await dbContext.Users.CountAsync(x => x.Role == UserRole.Admin && x.IsActive, cancellationToken) <= 1)
            throw new ServiceException("last_admin_required", "لا يمكن حذف آخر حساب مدير نشط في النظام.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await DeletionGuards.EnsureUserCanBeDeletedAsync(dbContext, entity.Id, cancellationToken);
        await DeletionGuards.DeleteAutomaticallyCreatedUserChildrenAsync(dbContext, entity.Id, cancellationToken);
        dbContext.Users.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new { entity.Id };
    }

    protected override async Task<object?> GetAsync(UserModel model, CancellationToken cancellationToken)
    {
        var (entity, _, _) = await FindTargetUserAsync(model, cancellationToken, noTracking: true);
        return ToResponse(entity);
    }

    private async Task<(UserEntity Entity, int RequesterId, bool IsAdmin)> FindTargetUserAsync(
        UserModel model,
        CancellationToken cancellationToken,
        bool includeDriverProfile = false,
        bool noTracking = false)
    {
        var requesterId = currentUser.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول لهذه العملية.");
        var isAdmin = await IsAdministratorAsync(cancellationToken);
        if (!isAdmin && model.Id is int requestedId && requestedId != requesterId)
            throw new ServiceException("user_access_denied", "لا تملك صلاحية الوصول إلى بيانات مستخدم آخر.");

        var targetId = isAdmin ? model.Id ?? requesterId : requesterId;
        IQueryable<UserEntity> query = dbContext.Users;
        if (includeDriverProfile) query = query.Include(x => x.DriverProfile);
        if (noTracking) query = query.AsNoTracking();
        var entity = await query.SingleOrDefaultAsync(x => x.Id == targetId, cancellationToken)
            ?? throw new ServiceException("user_not_found", "المستخدم غير موجود.");
        return (entity, requesterId, isAdmin);
    }

    private Task<bool> IsAdministratorAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is not int userId
            ? Task.FromResult(false)
            : dbContext.Users.AsNoTracking().AnyAsync(
                x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive,
                cancellationToken);

    private async Task EnsureAdminCanChangeAccessAsync(
        UserEntity entity,
        UserModel model,
        int requesterId,
        CancellationToken cancellationToken)
    {
        if (entity.Id == requesterId && (model.Role != UserRole.Admin || !model.IsActive))
            throw new ServiceException("cannot_disable_current_admin", "لا يمكن خفض صلاحية أو إيقاف حساب المدير الذي تستخدمه حالياً.");

        if (entity.Role == UserRole.Admin && (model.Role != UserRole.Admin || !model.IsActive) &&
            await dbContext.Users.CountAsync(x => x.Role == UserRole.Admin && x.IsActive, cancellationToken) <= 1)
            throw new ServiceException("last_admin_required", "يجب أن يبقى مدير نشط واحد على الأقل في النظام.");

        if (entity.DriverProfile is not null && model.Role != UserRole.Driver)
            throw new ServiceException(
                "driver_profile_role_conflict",
                "لا يمكن تغيير دور سائق له ملف سائق. أزل ملف السائق أولاً بعد التأكد من عدم وجود سجلات مرتبطة.");
    }

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static UserResponse ToResponse(UserEntity entity) => new(
        entity.Id, entity.PhoneNumber, entity.DisplayName, entity.Email, entity.Role,
        entity.IsActive, entity.Gender, entity.Street, entity.City, entity.District,
        entity.CreatedAtUtc, entity.UpdatedAtUtc);
}
