using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api.Sms;

public static class SmsProviderEndpoints
{
    public static void MapSmsProviderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/sms-provider");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var actor = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserContext>();
            if (actor.UserId is not int id) return Results.Unauthorized();
            var db = context.HttpContext.RequestServices.GetRequiredService<YemenDriveDbContext>();
            try
            {
                await AdministratorAccess.RequireAsync(db, id,
                    context.HttpContext.Request.Method == "GET" ? "policy.read" : "policy.write",
                    context.HttpContext.RequestAborted);
                return await next(context);
            }
            catch (ServiceException exception)
            {
                return Results.Json(ApiResult.Fail(exception.Code, exception.Message), statusCode:
                    exception.Code == "administrator_permission_required" ? 403 : 400);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Json(ApiResult.Fail("sms_configuration_storage_error",
                    "تعذر الوصول إلى ملف إعدادات مزود الرسائل. تحقق من صلاحيات مجلد الخادم."), statusCode: 503);
            }
        });
        group.MapGet("", async (HttpContext context, SmsProviderConfigurationStore store, CancellationToken token) =>
        {
            var saved = await store.GetAsync(token);
            var runtime = context.RequestServices.GetService<SmsProviderRuntime>();
            var connection = runtime is null ? null : await runtime.ConnectAsync(token);
            return Results.Ok(ApiResult.Ok(new { saved.Url, saved.Username, saved.DeviceNumber, saved.HasPassword, saved.UpdatedAtUtc, connection }));
        });
        group.MapPost("", async (HttpContext context, SmsProviderInput input, SmsProviderConfigurationStore store, CancellationToken token) =>
        {
            var saved = await store.SaveAsync(input, token);
            var runtime = context.RequestServices.GetService<SmsProviderRuntime>();
            var connection = runtime is null ? null : await runtime.ConnectAsync(token, force: true);
            return Results.Ok(ApiResult.Ok(new { saved.Url, saved.Username, saved.DeviceNumber, saved.HasPassword, saved.UpdatedAtUtc, connection }, "تم حفظ بيانات مزود الرسائل النصية."));
        });
        group.MapGet("/connection", async (HttpContext context, CancellationToken token) => Results.Ok(ApiResult.Ok(await context.RequestServices.GetRequiredService<SmsProviderRuntime>().ConnectAsync(token))));
        group.MapPost("/connect", async (HttpContext context, CancellationToken token) => Results.Ok(ApiResult.Ok(await context.RequestServices.GetRequiredService<SmsProviderRuntime>().ConnectAsync(token, force: true))));
        group.MapPost("/activate", async (ActivationInput input, HttpContext context, CancellationToken token) => Results.Ok(ApiResult.Ok(await context.RequestServices.GetRequiredService<SmsProviderRuntime>().ActivateAsync(input.Code, token))));
    }

    public sealed record ActivationInput(string? Code);
}
