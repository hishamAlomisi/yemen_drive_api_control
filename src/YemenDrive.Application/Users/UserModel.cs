using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Users;

public sealed record UserModel(
    int? Id = null,
    string? DisplayName = null,
    string? Email = null,
    string? PhoneNumber = null,
    string? Password = null,
    UserRole Role = UserRole.Customer,
    bool IsActive = true,
    string? Gender = null,
    string? Street = null,
    string? City = null,
    string? District = null);

public sealed record UserResponse(
    int Id,
    string PhoneNumber,
    string? DisplayName,
    string? Email,
    UserRole Role,
    bool IsActive,
    string? Gender,
    string? Street,
    string? City,
    string? District,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);
