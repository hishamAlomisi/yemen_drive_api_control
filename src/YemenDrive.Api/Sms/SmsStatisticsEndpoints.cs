using YemenDrive.Application.Administration;
using YemenDrive.Application.Communication;
using YemenDrive.Database;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api.Sms;

public static class SmsStatisticsEndpoints
{
    public static void MapSmsStatisticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/sms-statistics", async (HttpContext context, ICurrentUserContext actor,
            YemenDriveDbContext db, SmsStatisticsService statistics, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (actor.UserId is not int id) return Results.Unauthorized();
            try
            {
                await AdministratorAccess.RequireAsync(db, id, "operations.read", token);
                return Results.Ok(ApiResult.Ok(await statistics.GetAsync(token)));
            }
            catch (ServiceException exception)
            {
                return Results.Json(ApiResult.Fail(exception.Code, exception.Message), statusCode: 403);
            }
        });
    }
}
