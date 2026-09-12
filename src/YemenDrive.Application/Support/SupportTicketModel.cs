namespace YemenDrive.Application.Support;

public sealed record SupportTicketModel(
    int? Id = null,
    int? UserId = null,
    string? Category = null,
    string? Message = null,
    string? Status = null,
    string? AdminReply = null);
