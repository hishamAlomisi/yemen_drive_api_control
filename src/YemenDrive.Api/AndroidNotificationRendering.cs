namespace YemenDrive.Api;

public static class AndroidNotificationRendering
{
    public const string AppRenderer = "app";
    // Stored in the existing platform descriptor, within its 24-character limit.
    // Older installations retain "android" and their system-rendered envelopes.
    public const string LocalPlatform = "android-local";
    public static string Platform(string? renderer) =>
        renderer == AppRenderer ? LocalPlatform : "android";
}
