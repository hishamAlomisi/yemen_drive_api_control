using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>Makes both refresh calls observe the old token before either can claim it.</summary>
sealed class RefreshReadBarrier : DbCommandInterceptor
{
    private TaskCompletionSource? release;
    private int readers;
    public void Arm() { readers = 0; release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Disarm() => release = null;
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
        DbDataReader result, CancellationToken token = default)
    {
        var barrier = release;
        if (barrier is not null && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal) &&
            command.CommandText.Contains("[RefreshTokenHash] =", StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref readers) == 2) barrier.TrySetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        return result;
    }
}
