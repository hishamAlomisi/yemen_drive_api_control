namespace YemenDrive.Database.Realtime;

/// <summary>Additive rollout switch. Existing installs keep their transport until consumers are verified.</summary>
public sealed class RealtimeJournalSettings
{
    public bool Enabled { get; init; }
    public int PublishedHistoryDays { get; init; } = 7;
}
