namespace YemenDrive.Application.Catalog;

public sealed record ServiceKindModel(
    int? Id = null,
    string? Code = null,
    string? NameAr = null,
    string? ImageUrl = null,
    bool IsActive = true,
    bool IsDefault = false,
    int SortOrder = 0);
