namespace YemenDrive.Database;

public sealed partial class YemenDriveDbContext
{
    private bool notificationPushPending;
    public void MarkNotificationPushPending() => notificationPushPending = true;
    public bool ConsumeNotificationPushSignal()
    {
        var pending = notificationPushPending;
        notificationPushPending = false;
        return pending;
    }
}
