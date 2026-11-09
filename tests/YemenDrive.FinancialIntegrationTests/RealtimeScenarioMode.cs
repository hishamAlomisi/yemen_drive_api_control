using YemenDrive.Database.Realtime;
internal static class RealtimeScenarioMode
{
    internal static bool Enabled;
    internal static RealtimeJournalSettings? Settings => Enabled ? new() { Enabled = true } : null;
}
