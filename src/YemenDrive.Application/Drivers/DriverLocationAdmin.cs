using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Drivers;

public sealed record DriverLocationAdminModel(int DriverId, double? Latitude = null, double? Longitude = null);

public interface IDriverLocationAdministrator
{
    Task<object> ExecuteAsync(string operation, DriverLocationAdminModel model, CancellationToken token);
}

public sealed class DriverLocationAdmin(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user, IDriverLocationAdministrator locations) : ModelService<DriverLocationAdminModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "save", "move"];

    protected override async Task<object?> ExecuteOperationAsync(string operation, DriverLocationAdminModel model, CancellationToken token)
    {
        operation = operation.ToLowerInvariant();
        await AdministratorAccess.RequireAsync(db, user.RequireUserId(),
            operation == "get" ? "personal_data.read" : "personal_data.write", token);
        if (model.DriverId <= 0) throw new ServiceException("driver_required", "اختر السائق أولاً.");
        return await locations.ExecuteAsync(operation, model, token);
    }
}
