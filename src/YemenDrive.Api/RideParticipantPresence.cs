using System.Collections.Concurrent;
using YemenDrive.Application.Rides;

namespace YemenDrive.Api;
public sealed class RideParticipantPresence : IRideParticipantPresence
{
    private readonly ConcurrentDictionary<string, (int UserId, bool Foreground)> connections = new();
    public void Connect(string id, int userId) => connections[id] = (userId, true);
    public void Remove(string id) => connections.TryRemove(id, out _);
    public void SetForeground(string id, bool foreground)
    {
        if (connections.TryGetValue(id, out var state)) connections[id] = (state.UserId, foreground);
    }
    public bool IsForeground(int userId) => connections.Values.Any(x => x.UserId == userId && x.Foreground);
}
