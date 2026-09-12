namespace YemenDrive.Database.Configuration;

public sealed record DatabaseSettings
{
    public string Provider { get; init; } = "SqlServer";
    public string Server { get; init; } = string.Empty;
    public string Database { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public bool IntegratedSecurity { get; init; }
    public bool TrustServerCertificate { get; init; } = true;
    public bool Encrypt { get; init; }
    public bool RecreateSchema { get; init; }
}
