namespace YemenDrive.Application.Notifications;

public sealed record AdminNotificationCampaignModel(
    int? Id = null,
    string? Body = null,
    int? Target = null,
    int? SourceCampaignId = null,
    string? Query = null);
