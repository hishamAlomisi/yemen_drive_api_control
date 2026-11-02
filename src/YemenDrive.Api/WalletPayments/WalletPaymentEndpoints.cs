using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api.WalletPayments;

public static class WalletPaymentEndpoints
{
    public sealed record StartWalletPayment(int PaymentMethodId);
    public sealed record ConfirmWalletPayment(bool Approved);
    public sealed record CompleteWalletPayment(string? Code);
    public sealed record StartWalletTopUp(int PaymentMethodId, decimal Amount, Guid RequestId);
    public sealed record ReviewProviderOutcome(string Outcome, decimal Amount, string ProviderReference, string EvidenceReference, string Reason);

    public static void MapWalletPaymentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/wallet-payments/{paymentId:int}/review", async (int paymentId, ReviewProviderOutcome request,
            WalletPaymentService service, CancellationToken token) =>
        {
            try { return Results.Ok(ApiResult.Ok(await service.ReviewOutcomeAsync(paymentId, request.Outcome, request.Amount, request.ProviderReference, request.EvidenceReference, request.Reason, token))); }
            catch (ServiceException ex) { return Results.BadRequest(ApiResult.Fail(ex.Code, ex.Message)); }
        });
        var topUps = app.MapGroup("/api/wallet/top-ups");
        topUps.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (context.HttpContext.RequestServices.GetRequiredService<ICurrentUserContext>().UserId is null) return Results.Unauthorized();
            try { return await next(context); }
            catch (ServiceException ex) { return Results.BadRequest(ApiResult.Fail(ex.Code, ex.Message)); }
        });
        topUps.MapGet("/current", async (WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.GetTopUpAsync(null, token))));
        topUps.MapGet("/by-reference/{requestId:guid}", async (Guid requestId, WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.GetTopUpAsync(null, token, requestId))));
        topUps.MapGet("/{paymentId:int}", async (int paymentId, WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.GetTopUpAsync(paymentId, token))));
        topUps.MapPost("", async (StartWalletTopUp request, WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.StartTopUpAsync(request.PaymentMethodId, request.Amount, request.RequestId, token))));
        topUps.MapPost("/{paymentId:int}/confirm", async (int paymentId, ConfirmWalletPayment request, WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.ConfirmAsync(null, paymentId, request.Approved, token))));
        topUps.MapPost("/{paymentId:int}/complete", async (int paymentId, CompleteWalletPayment request, WalletPaymentService service, CancellationToken token) =>
            Results.Ok(ApiResult.Ok(await service.CompleteAsync(null, paymentId, request.Code, token))));
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
