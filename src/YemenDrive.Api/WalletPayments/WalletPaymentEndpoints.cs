using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api.WalletPayments;

public static class WalletPaymentEndpoints
{
    public sealed record StartWalletPayment(int PaymentMethodId);
    public sealed record ConfirmWalletPayment(bool Approved);
    public sealed record CompleteWalletPayment(string? Code);

    public static void MapWalletPaymentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/rides/{rideId:int}/wallet-payment");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var user = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserContext>();
            if (user.UserId is null) return Results.Unauthorized();
            try { return await next(context); }
            catch (ServiceException ex) { return Results.BadRequest(ApiResult.Fail(ex.Code, ex.Message)); }
        });
        group.MapGet("", async (int rideId, WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.GetAsync(rideId, token))));
        group.MapPost("", async (int rideId, StartWalletPayment request, WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.StartAsync(rideId, request.PaymentMethodId, token))));
        group.MapPost("/{paymentId:int}/confirm", async (int rideId, int paymentId, ConfirmWalletPayment request,
            WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.ConfirmAsync(rideId, paymentId, request.Approved, token))));
        group.MapPost("/{paymentId:int}/complete", async (int rideId, int paymentId, CompleteWalletPayment request,
            WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.CompleteAsync(rideId, paymentId, request.Code, token))));
    }
}
