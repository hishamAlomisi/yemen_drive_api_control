using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Application.Accounting;
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
        if (!isCreate && currentUser.UserId is null && model.Id is null)
            errors.Add(new("authentication", "يجب تسجيل الدخول لهذه العملية."));

        return ValueTask.FromResult<IReadOnlyCollection<ValidationError>>(errors);
    }

    protected override async Task<object?> AddAsync(UserModel model, CancellationToken cancellationToken)
    {
        var phone = model.PhoneNumber!.Trim();
        if (await dbContext.Users.AnyAsync(x => x.PhoneNumber == phone, cancellationToken))
            throw new ServiceException("user_exists", "رقم الهاتف مسجل مسبقاً.");

        var entity = new UserEntity
        {
            PhoneNumber = phone,
            DisplayName = model.DisplayName!.Trim(),
            Email = NormalizeEmail(model.Email),
            PasswordHash = PasswordHash.Create(model.Password!),
            Role = model.Role,
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
        var entity = await dbContext.Users.FindAsync([currentUser.UserId ?? model.Id!.Value], cancellationToken)
            ?? throw new ServiceException("user_not_found", "المستخدم غير موجود.");

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
        entity.Role = model.Role;
        entity.IsActive = model.IsActive;
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
        var entity = await dbContext.Users.FindAsync([currentUser.UserId ?? model.Id!.Value], cancellationToken)
            ?? throw new ServiceException("user_not_found", "المستخدم غير موجود.");
        dbContext.Users.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new { entity.Id };
    }

    protected override async Task<object?> GetAsync(UserModel model, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == (currentUser.UserId ?? model.Id!.Value), cancellationToken)
            ?? throw new ServiceException("user_not_found", "المستخدم غير موجود.");
        return ToResponse(entity);
    }

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static UserResponse ToResponse(UserEntity entity) => new(
        entity.Id, entity.PhoneNumber, entity.DisplayName, entity.Email, entity.Role,
        entity.IsActive, entity.Gender, entity.Street, entity.City, entity.District,
        entity.CreatedAtUtc, entity.UpdatedAtUtc);
}
