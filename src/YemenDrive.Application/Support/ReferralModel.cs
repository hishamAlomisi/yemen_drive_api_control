namespace YemenDrive.Application.Support;

public sealed record ReferralModel(
    int? Id = null,
    int? UserId = null,
    string? Code = null,
    string? Status = null);
