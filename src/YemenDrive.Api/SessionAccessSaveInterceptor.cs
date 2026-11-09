using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

/// <summary>Access changes and their session revocations share the user's SQL transaction.</summary>
public sealed class SessionAccessSaveInterceptor(SessionRevocationCoordinator coordinator) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
    {
        if (data.Context is YemenDriveDbContext db) CaptureAsync(db, false, default).GetAwaiter().GetResult();
        return result;
    }
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
        InterceptionResult<int> result, CancellationToken token = default)
    {
        if (data.Context is YemenDriveDbContext db) await CaptureAsync(db, true, token);
        return result;
    }

    private async Task CaptureAsync(YemenDriveDbContext db, bool asynchronous, CancellationToken token)
    {
        db.ChangeTracker.DetectChanges();
        var users = db.ChangeTracker.Entries<User>().Where(entry => entry.State == EntityState.Deleted ||
                entry.State == EntityState.Modified &&
                (entry.Property(x => x.Role).IsModified || entry.Property(x => x.IsActive).IsModified ||
                 entry.Property(x => x.PasswordHash).IsModified))
            .Select(entry => entry.Entity.Id).Where(id => id > 0).Distinct().ToArray();
        if (users.Length > 0)
        {
            var query = db.AuthSessions.Where(x => users.Contains(x.UserId) && x.RevokedAtUtc == null);
            var sessions = asynchronous ? await query.ToListAsync(token) : query.ToList();
            var now = DateTime.UtcNow;
            foreach (var session in sessions) { session.RevokedAtUtc = now; session.UpdatedAtUtc = now; }
            db.ChangeTracker.DetectChanges();
        }
        var batch = new SessionRevocationCoordinator.Batch();
        batch.Users.UnionWith(users);
        batch.Families.UnionWith(db.ChangeTracker.Entries<AuthSession>()
            .Where(entry => entry.State == EntityState.Deleted || entry.State == EntityState.Modified &&
                entry.Property(x => x.RevokedAtUtc).IsModified && entry.Entity.RevokedAtUtc is not null)
            .Select(entry => entry.Entity.FamilyId));
        coordinator.BeginSave(db, batch);
    }

    public override int SavedChanges(SaveChangesCompletedEventData data, int result)
    { if (data.Context is { } db) coordinator.Saved(db); return result; }
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
    { if (data.Context is { } db) coordinator.Saved(db); return ValueTask.FromResult(result); }
    public override void SaveChangesFailed(DbContextErrorEventData data)
    { if (data.Context is { } db) coordinator.SaveFailed(db); }
    public override Task SaveChangesFailedAsync(DbContextErrorEventData data, CancellationToken token = default)
    { SaveChangesFailed(data); return Task.CompletedTask; }
    public override void SaveChangesCanceled(DbContextEventData data)
    { if (data.Context is { } db) coordinator.SaveFailed(db); }
    public override Task SaveChangesCanceledAsync(DbContextEventData data, CancellationToken token = default)
    { SaveChangesCanceled(data); return Task.CompletedTask; }
}

public static class TrackingSessionRevocationRegistration
{
    public static IServiceCollection AddTrackingSessionRevocation(this IServiceCollection services)
    {
        services.AddSingleton<TrackingConnectionRegistry>();
        services.AddSingleton<SessionRevocationCoordinator>();
        services.AddSingleton<SessionAccessSaveInterceptor>();
        services.AddSingleton<SessionRevocationTransactionInterceptor>();
        services.AddSingleton<TrackingSessionFilter>();
        services.AddSignalR().AddHubOptions<TrackingHub>(options => options.AddFilter<TrackingSessionFilter>());
        return services;
    }
    public static DbContextOptionsBuilder AddTrackingSessionRevocation(this DbContextOptionsBuilder options, IServiceProvider services) =>
        options.AddInterceptors(services.GetRequiredService<SessionAccessSaveInterceptor>(),
            services.GetRequiredService<SessionRevocationTransactionInterceptor>());
}
