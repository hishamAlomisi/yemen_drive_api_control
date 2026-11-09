using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;

namespace YemenDrive.Api;

/// <summary>Local socket revocation is enforced by the server, independently of client event handlers.</summary>
public sealed class TrackingConnectionRegistry
{
    private sealed record Connection(int UserId, Guid FamilyId, Action Abort);
    private readonly ConcurrentDictionary<string, Connection> connections = new();
    public int Count => connections.Count;

    public void Register(string connectionId, AuthenticatedSession session, Action abort) =>
        connections[connectionId] = new(session.UserId, session.FamilyId, abort);

    public void Remove(string connectionId) => connections.TryRemove(connectionId, out _);
    public void RevokeUser(int userId) => Revoke(x => x.UserId == userId);
    public void RevokeFamily(Guid familyId) => Revoke(x => x.FamilyId == familyId);
    private void Revoke(Func<Connection, bool> applies)
    {
        foreach (var pair in connections)
            if (applies(pair.Value) && connections.TryRemove(pair.Key, out var connection)) connection.Abort();
    }
}

public sealed class TrackingSessionFilter(TrackingConnectionRegistry connections) : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var session = GetSession(context.Context);
        if (session is null) { context.Context.Abort(); return; }
        // Register first: a concurrent commit either aborts this registration or
        // the following persisted validation sees its revoked session.
        connections.Register(context.Context.ConnectionId, session, context.Context.Abort);
        try
        {
            if (!await IsValidAsync(context.ServiceProvider, session, context.Context.ConnectionAborted))
            { context.Context.Abort(); connections.Remove(context.Context.ConnectionId); return; }
            await next(context);
        }
        catch { connections.Remove(context.Context.ConnectionId); throw; }
    }

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var session = GetSession(context.Context);
        if (session is null || !await IsValidAsync(context.ServiceProvider, session, context.Context.ConnectionAborted))
        {
            context.Context.Abort();
            throw new HubException("انتهت جلسة الاتصال؛ سجل الدخول من جديد.");
        }
        return await next(context);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        connections.Remove(context.Context.ConnectionId);
        await next(context, exception);
    }

    private static AuthenticatedSession? GetSession(HubCallerContext context) =>
        context.GetHttpContext()?.Items[AuthSessionAuthenticationMiddleware.SessionItem] as AuthenticatedSession;

    private static Task<bool> IsValidAsync(IServiceProvider services, AuthenticatedSession session, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        if (session.AccessExpiresAtUtc <= now) return Task.FromResult(false);
        return services.GetRequiredService<YemenDriveDbContext>().AuthSessions.AsNoTracking()
            .AnyAsync(x => x.UserId == session.UserId && x.FamilyId == session.FamilyId &&
                x.RevokedAtUtc == null && x.SessionExpiresAtUtc > now && x.User.IsActive, token);
    }
}
