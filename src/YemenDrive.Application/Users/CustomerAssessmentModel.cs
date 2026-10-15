namespace YemenDrive.Application.Users;

public sealed record CustomerAssessmentModel(
    string? Query = null,
    string? SortBy = null,
    bool Descending = true);
