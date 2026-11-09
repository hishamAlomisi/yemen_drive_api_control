using System.Text.Json;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Rides;

public sealed record RideWaitingResumeRequest(Guid RequestKey, int RequestedByUserId,
    DateTime RequestedAtUtc, DateTime? DecidedAtUtc = null, string? Decision = null,
    int? DecidedByUserId = null);

public static class RideWaitingResumeRequests
{
    public const int MaximumRequestsPerStop = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static List<RideWaitingResumeRequest> Read(RideWaitingSession session)
    {
        if (string.IsNullOrWhiteSpace(session.ResumeRequestsJson)) return [];
        try
        {
            var requests = JsonSerializer.Deserialize<List<RideWaitingResumeRequest>>(session.ResumeRequestsJson, JsonOptions);
            if (requests is null || requests.Count > MaximumRequestsPerStop ||
                requests.Any(x => x.RequestKey == Guid.Empty || x.RequestedByUserId <= 0 ||
                    x.Decision is not (null or "accepted" or "rejected" or "waiting_ended")) ||
                requests.Select(x => x.RequestKey).Distinct().Count() != requests.Count ||
                requests.Count(x => x.Decision is null) > 1)
                throw new JsonException();
            return requests;
        }
        catch (JsonException)
        {
            throw new ServiceException("waiting_resume_state_invalid", "تعذر قراءة حالة طلب الاستئناف؛ أعد تحميل الرحلة.");
        }
    }

    public static void Write(RideWaitingSession session, List<RideWaitingResumeRequest> requests) =>
        session.ResumeRequestsJson = JsonSerializer.Serialize(requests, JsonOptions);

    public static void FinishPending(RideWaitingSession session, DateTime at, string decision, int? actorId = null)
    {
        var requests = Read(session);
        var index = requests.FindIndex(x => x.Decision is null);
        if (index < 0) return;
        requests[index] = requests[index] with { Decision = decision, DecidedAtUtc = at, DecidedByUserId = actorId };
        Write(session, requests);
    }
}
