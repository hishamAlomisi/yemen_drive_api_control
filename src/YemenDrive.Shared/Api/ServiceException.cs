namespace YemenDrive.Shared.Api;

public sealed class ServiceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
