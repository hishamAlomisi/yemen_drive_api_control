using System.Net;
using System.Text;
using System.Text.Json;
using eqsWallets;
using eqsWallets.Providers.Floosak;
using YemenDrive.Api.WalletPayments;
using YemenDrive.Database.Entities;

internal static class FloosakPreparationScenarios
{
    private const string RequestId = "YD-00000000000000000000000000000001";
    private const string Mobile = "967777000001";
    private const string Prepared = """{"is_success":true,"data":{"id":900,"net":1250,"name":"Synthetic Customer","identity_number":"PRIVATE-IDENTITY","fee_currency_id":17,"fee_currency_amount":5}}""";

    public static async Task RunAsync()
    {
        using var server = new PreparationHandler(Prepared);
        var observed = new List<string>();
        await using var client = CreateClient(server);
        Check(await client.LoginAsync(Options(observed)), "mock Floosak login");
        var result = await client.Floosak.RequestWithdrawFromCustomerAsync(RequestId, Mobile, 1250m, CurrencyCode.YER, CurrencyCode.YER);
        var operation = Operation();
        WalletPaymentService.ApplyResult(operation, result);
        var view = WalletPaymentService.View(operation);
        Check(server.Calls == 2 && server.PreparationCalls == 1, "preparation is the only operation after login; OTP was not requested");
        Check(view.Status == "AwaitingConfirmation" && view.NextAction == "Confirmation" &&
            view.CustomerName == "Synthetic Customer" && view.CustomerIsProviderVerified && view.Fees == 5m,
            "real DLL customer quote reaches the API before OTP");
        Check(observed.Count == 1 && observed[0].Contains("\"data.name\":\"String\"") &&
            !observed[0].Contains("Synthetic") && !observed[0].Contains("PRIVATE"), "quote diagnostic contains schema only");

        using var rejectedServer = new PreparationHandler("""{"statusCode":2,"message":"PRIVATE-MESSAGE","errors":{"target_phone":["PRIVATE-PHONE"],"PRIVATE-FIELD":["PRIVATE-VALUE"]}}""", HttpStatusCode.UnprocessableEntity);
        var rejectedObserved = new List<string>();
        await using var rejectedClient = CreateClient(rejectedServer);
        Check(await rejectedClient.LoginAsync(Options(rejectedObserved)), "mock rejected-response login");
        var rejected = await rejectedClient.Floosak.RequestWithdrawFromCustomerAsync(RequestId, Mobile, 1250m, CurrencyCode.YER, CurrencyCode.YER);
        var rejectedOperation = Operation();
        WalletPaymentService.ApplyResult(rejectedOperation, rejected);
        Check(rejectedOperation.Status == "ReviewRequired" && rejectedOperation.IsOpen,
            "unrecognized provider response cannot release reservation or claim financial failure");
        Check(rejectedObserved.Count == 1 && rejectedObserved[0].Contains("\"httpStatus\":422") &&
            rejectedObserved[0].Contains("\"is_success\":\"Missing\"") && rejectedObserved[0].Contains("\"errors.target_phone\":\"Array\"") &&
            !rejectedObserved[0].Contains("PRIVATE"), "HTTP rejection and missing contract are diagnosable without provider values");

        using var validationServer = new PreparationHandler("""{"statusCode":2,"message":"رقم مرجع الطلب غير صالح","errors":{"request_id":["Invalid reference"]}}""", HttpStatusCode.UnprocessableEntity);
        await using var validationClient = CreateClient(validationServer);
        Check(await validationClient.LoginAsync(Options(new List<string>())), "validation response login");
        var validation = await FloosakPreparationValidation.RequestAsync(() =>
            validationClient.Floosak.RequestWithdrawFromCustomerAsync(RequestId, Mobile, 1250m, CurrencyCode.YER, CurrencyCode.YER));
        var validationOperation = Operation();
        WalletPaymentService.ApplyResult(validationOperation, validation);
        Check(validationOperation.Status == "Failed" && !validationOperation.IsOpen &&
            WalletPaymentService.View(validationOperation).Message.Contains("رقم مرجع الطلب غير صالح") && validationServer.Calls == 2,
            "HTTP 422 validation before OTP displays the provider cause and permits a fresh reference without replay");

        var invalid = WalletProviderResponseDiagnostics.DescribeFloosakPreparation(502, "<html>PRIVATE-TOKEN</html>");
        Check(invalid.Contains("InvalidJson") && !invalid.Contains("PRIVATE"), "invalid response body is never logged");
        var large = WalletProviderResponseDiagnostics.DescribeFloosakPreparation(200, new string('x', 65537));
        Check(large.Contains("TooLarge"), "response inspection is bounded");
        var wrongType = WalletProviderResponseDiagnostics.DescribeFloosakPreparation(200,
            """{"is_success":"PRIVATE","statusCode":"PRIVATE-CODE","data":{"net":"PRIVATE-AMOUNT","name":null,"password":"PRIVATE-SECRET"}}""");
        Check(wrongType.Contains("\"data.net\":\"String\"") && wrongType.Contains("\"data.name\":\"Null\"") &&
            !wrongType.Contains("PRIVATE") && !wrongType.Contains("password"), "invalid expected field types are exposed without values or arbitrary names");
        Console.WriteLine("PASS: actual Floosak DLL returns verified customer before OTP; rejected preparation remains reserved; response diagnostics contain only fixed schema and HTTP status.");
    }

    private static WalletClient CreateClient(PreparationHandler server) => new(new WalletFactoryBuilder().AddFloosak()
        .UseHttpClientFactory(options => new HttpClient(server, disposeHandler: false) { BaseAddress = options.BaseAddress }).Build(), null);

    private static FloosakOptions Options(List<string> observed) => new()
    {
        BaseAddress = new Uri("https://floosak.test"), PhoneNumber = "967777000000", Password = "synthetic-password",
        ResponseReceived = response =>
        {
            FloosakPreparationValidation.Observe(response.Path, response.StatusCode, response.Body, "synthetic-password");
            if (response.Path == "/api/v1/agent/offline_cashout")
                observed.Add(WalletProviderResponseDiagnostics.DescribeFloosakPreparation(response.StatusCode, response.Body));
        }
    };

    private static PaymentWalletOperation Operation() => new()
    {
        Stage = "Request", ProviderCode = "Floosak", RequestId = RequestId, Mobile = Mobile, Amount = 1250m
    };

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Floosak preparation: " + description);
    }

    private sealed class PreparationHandler(string preparation, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int PreparationCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri?.AbsolutePath == "/api/v1/auth/login")
                return Json(HttpStatusCode.OK, """{"is_success":true,"data":{"token":"synthetic-token","wallets":[{"id":101,"currency":{"id":17,"symbol":{"en":"YER"}},"balance":5000}]}}""");
            Check(request.RequestUri?.AbsolutePath == "/api/v1/agent/offline_cashout", "no Save/Confirm endpoint may be invoked before approval");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            Check(root.GetProperty("request_id").GetString() == RequestId && root.GetProperty("target_phone").GetString() == Mobile &&
                root.GetProperty("amount").GetDecimal() == 1250m && root.GetProperty("fee_currency_id").GetInt32() == 17 &&
                !root.TryGetProperty("otp", out _), "API reference, international phone, amount, and login currency metadata are sent unchanged");
            PreparationCalls++;
            return Json(status, preparation);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
