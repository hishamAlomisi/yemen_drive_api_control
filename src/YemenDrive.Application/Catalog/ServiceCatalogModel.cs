namespace YemenDrive.Application.Catalog;

public sealed record ServiceCatalogModel(
    int? Id = null,
    string? Code = null,
    string? NameAr = null,
    int? ServiceKindId = null,
    int ArrivalMinutes = 0,
    decimal BasePrice = 0,
    decimal Rating = 0,
    int Seats = 0,
    string? DescriptionAr = null,
    string? ImageUrl = null,
    bool IsRecommended = false,
    bool IsActive = true,
    int SortOrder = 0);
