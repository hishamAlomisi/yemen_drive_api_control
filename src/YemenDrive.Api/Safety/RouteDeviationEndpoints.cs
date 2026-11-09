using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api.Safety;

public static class RouteDeviationEndpoints
{
    public static void MapRouteDeviation(this WebApplication app)
    {
        app.MapGet("/api/rides/{rideId:int}/route-deviation", (int rideId, ICurrentUserContext user,
            RouteDeviationService service, HttpContext context, CancellationToken token) =>
            Handle(rideId, user, service, context, false, token));
        app.MapPost("/api/rides/{rideId:int}/route-deviation/report", (int rideId, ICurrentUserContext user,
            RouteDeviationService service, HttpContext context, CancellationToken token) =>
            Handle(rideId, user, service, context, true, token));
    }

    private static async Task<IResult> Handle(int rideId, ICurrentUserContext user,
        RouteDeviationService service, HttpContext context, bool report, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (user.UserId is not int userId) return Results.Unauthorized();
        try
        {
            var status = report ? await service.ReportAsync(rideId, userId, token) :
                await service.GetAsync(rideId, userId, token);
            return Results.Ok(ApiResult.Ok(status, status.Message));
        }
        catch (ServiceException exception)
        {
            return Results.Json(ApiResult.Fail(exception.Code, exception.Message), statusCode:
                exception.Code == "ride_access_denied" ? 403 : exception.Code == "ride_not_found" ? 404 : 400);
        }
    }
}
