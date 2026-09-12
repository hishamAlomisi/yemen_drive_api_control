namespace YemenDrive.Application.Communication;

public sealed record RideMessageModel(
    int? Id = null,
    int? RideId = null,
    int? SenderId = null,
    int? RecipientId = null,
    string MessageType = "Text",
    string? Content = null,
    bool? MarkRead = null);
