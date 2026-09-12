namespace YemenDrive.Application.Promotions;

public sealed record PromotionModel(
    int? Id = null,
    string? Code = null,
    string? Name = null,
    decimal? FixedDiscount = null,
    decimal? PercentageDiscount = null,
    decimal? MaximumDiscount = null,
    DateTime? StartsAtUtc = null,
    DateTime? EndsAtUtc = null,
    int? UsageLimit = null,
    bool? IsActive = null);
