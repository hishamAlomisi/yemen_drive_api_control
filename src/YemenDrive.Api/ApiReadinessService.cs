using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Shared.Api;

namespace YemenDrive.Api;

public interface IApiReadinessProbe
{
    bool IsConfigured { get; }
    Task<bool> CheckAsync(CancellationToken token);
}

public sealed class DatabaseReadinessProbe(IServiceScopeFactory scopes, DatabaseConfigurationStore configuration)
    : IApiReadinessProbe
{
    public bool IsConfigured => configuration.IsConfigured;

    public async Task<bool> CheckAsync(CancellationToken token)
    {
        if (!IsConfigured) return false;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        db.Database.SetCommandTimeout(1);
        // Connection and one scalar command only: no service catalog, sessions,
        // tracking data, application joins, or result materialization.
        await db.Database.ExecuteSqlRawAsync("SELECT 1", token);
        return true;
    }
}

/// <summary>One shared probe per process, with a one-second freshness/wait bound.
/// A slow database attempt stays shared until it actually exits, even if callers
/// time out, so retries cannot accumulate additional connection attempts.</summary>
public sealed class ApiReadinessService(IApiReadinessProbe probe, TimeProvider? timeProvider = null)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan freshness = TimeSpan.FromSeconds(1);
    private Task<bool>? pending;
    private DateTimeOffset freshUntil;
    private bool cached;

    public async Task<bool> CheckAsync(CancellationToken token)
    {
        if (!probe.IsConfigured) return false;
        Task<bool> shared;
        lock (gate)
        {
            if (pending is null && clock.GetUtcNow() < freshUntil) return cached;
            shared = pending ??= ProbeAsync();
        }
        try { return await shared.WaitAsync(freshness, token) && probe.IsConfigured; }
        catch (TimeoutException) { return false; }
    }

    private async Task<bool> ProbeAsync()
    {
        // Publish the shared task before even a synchronously completing probe
        // can update the cache or clear the in-flight reference.
        await Task.Yield();
        using var timeout = new CancellationTokenSource(freshness);
        var ready = false;
        try { ready = await probe.CheckAsync(timeout.Token) && !timeout.IsCancellationRequested; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        lock (gate)
        {
            cached = ready && probe.IsConfigured;
            freshUntil = clock.GetUtcNow() + freshness;
            pending = null;
            return cached;
        }
    }
}

public static class ApiReadinessEndpoints
{
    public static void MapApiReadiness(this WebApplication app) =>
        app.MapGet("/api/health/ready", async (HttpContext context, ApiReadinessService readiness) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await readiness.CheckAsync(context.RequestAborted)
                ? Results.Ok(ApiResult.Ok(new { ready = true }))
                : Results.Json(ApiResult.Fail("service_unavailable", "الخدمة غير متاحة مؤقتًا."), statusCode: 503);
        });
}
