using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api.Sms;

public static class SmsManualEndpoints
{
    public static void MapSmsManualEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/sms-send");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var actor = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserContext>();
            if (actor.UserId is not int id) return Results.Unauthorized();
            try
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<YemenDriveDbContext>();
                await AdministratorAccess.RequireAsync(db, id, "operations.write", context.HttpContext.RequestAborted);
                return await next(context);
            }
            catch (ServiceException exception)
            {
                return Results.Json(ApiResult.Fail(exception.Code, exception.Message), statusCode: exception.Code == "administrator_permission_required" ? 403 : 400);
            }
        });
        group.MapGet("", async (SmsProviderRuntime runtime, CancellationToken token) => Results.Ok(ApiResult.Ok(await runtime.ConnectAsync(token))));
        group.MapPost("", async (ManualSmsInput input, SmsDeliveryService delivery, ICurrentUserContext actor, CancellationToken token) =>
        {
            var result = await delivery.SendManualAsync(actor.RequireUserId(), input, token);
            return Results.Ok(ApiResult.Ok(result, result.Message));
        });
    }
}
