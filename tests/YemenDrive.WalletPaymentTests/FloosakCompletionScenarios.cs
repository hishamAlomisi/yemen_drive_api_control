using System.Net;
using System.Text;
using System.Text.Json;
using eqsWallets;
using eqsWallets.Providers.Floosak;
using YemenDrive.Api.WalletPayments;
using YemenDrive.Database.Entities;

internal static class FloosakCompletionScenarios
{
    private const string Mobile = "967777000001";
    private const string Rejection = """{"message":"OTP number is not match with transaction id","errors":{"OTP":["OTP number is not match with transaction id"]},"statusCode":null}""";

    public static async Task RunAsync()
    {
        var raw = await RunWithdrawalAsync(Rejection, normalize: false);
        Check(raw.Status == OperationStatus.Unknown && raw.ErrorCode == WalletErrorCode.InvalidProviderResponse,
            "actual DLL reproduces the reported unrecognized HTTP 422 envelope");
        var rejected = await RunWithdrawalAsync(Rejection);
        Check(rejected.Status == OperationStatus.Failed && rejected.ErrorCode == WalletErrorCode.InvalidRequest,
            "observed HTTP 422 OTP rejection is a definitive rejected confirmation");
        Check((await RunWithdrawalAsync(Rejection.Replace("[\"OTP number is not match with transaction id\"]", "\"OTP number is not match with transaction id\""))).Status == OperationStatus.Failed,
            "observed validation error accepts a string as well as an array");
        foreach (var response in new[]
        {
            """{"message":"OTP expired","errors":{"OTP":["OTP expired"]},"statusCode":null}""",
            """{"message":"رمز التحقق غير صحيح","errors":{"OTP":"Invalid"},"statusCode":422}""",
            """{"message":"قيمة otp المحددة غير صحيحة.","errors":{"otp":"[hidden]"},"statusCode":null}""",
            """{"message":"ليس لديك رصيد كافي","errors":{"amount":["Insufficient balance"]},"statusCode":null}""",
            """{"message":"طلب السحب مرفوض لهذا الحساب","errors":{"phone":["Rejected"]},"statusCode":null}"""
        })
        {
            var validation = await RunWithdrawalAsync(response);
            var failure = new PaymentWalletOperation { Stage = "Complete", ProviderCode = "Floosak", Amount = 1000m,
                RequestId = validation.RequestId! };
            WalletPaymentService.ApplyResult(failure, validation, "654321");
            Check(validation.Status == OperationStatus.Failed && failure.Status == "Failed" && !failure.IsOpen &&
                !WalletPaymentService.View(failure).Message.Contains("تعذر تحديد") && failure.ProviderSucceededAtUtc is null,
                "each structured provider rejection displays its reason and releases payment-method choices");
        }
        var operation = new PaymentWalletOperation { Stage = "Complete", ProviderCode = "Floosak", Amount = 1000m,
            RequestId = rejected.RequestId! };
        WalletPaymentService.ApplyResult(operation, rejected, "654321");
        var view = WalletPaymentService.View(operation);
        Check(view.Status == "Failed" && !operation.IsOpen && operation.ProviderSucceededAtUtc is null &&
            view.PaidAtUtc is null && view.Message.Contains("لا يطابق رقم معاملة السحب") && !view.Message.Contains("654321"),
            "rejected OTP releases only this attempt, creates no payment evidence, and displays the specific Arabic cause");

        foreach (var response in new[]
        {
            """{"message":"OTP number is not match with transaction id","errors":{"OTP":"synthetic-value"},"is_success":true,"statusCode":null}""",
            """{"message":"OTP number is not match with transaction id","errors":{"OTP":null},"statusCode":null}""",
            """{"message":"OTP number is not match with transaction id","errors":{"OTP":["Invalid"]},"statusCode":200}""",
            """{"message":"OTP number is not match with transaction id","errors":{"OTP":["Invalid"]},"data":{"status":"Completed"}}""",
            """{"message":"OTP expired","statusCode":null}""",
            """{"message":"OTP expired","errors":{},"statusCode":null}""",
            "<html>PRIVATE-SECRET</html>"
        })
        {
            var unknown = await RunWithdrawalAsync(response);
            Check(unknown.Status == OperationStatus.Unknown, "unrecognized, conflicting, or malformed rejection stays unresolved");
        }
        Check((await RunWithdrawalAsync(Rejection, status: HttpStatusCode.InternalServerError)).Status == OperationStatus.Unknown,
            "HTTP 500 with the same text cannot establish a validation rejection");
        var confirmed = await RunWithdrawalAsync("""{"is_success":true,"data":{"transaction_id":900,"status":"Completed","amount":1000,"currency":"YER","customer_phone":"967777000001"},"statusCode":null}""",
            status: HttpStatusCode.OK);
        Check(confirmed.Status == OperationStatus.Succeeded && confirmed.Receipt?.Amount.Amount == 1000m,
            "confirmed payment continues to require the library's validated receipt");

        var results = await Task.WhenAll(RunWithdrawalAsync(Rejection), RunWithdrawalAsync("{}"));
        Check(results[0].Status == OperationStatus.Failed && results[1].Status == OperationStatus.Unknown,
            "response evidence belongs to one async attempt, without cross-call leakage");
        var untouched = new WalletStepResult { Status = OperationStatus.Unknown, ErrorCode = WalletErrorCode.OutcomeUnknown };
        Check(ReferenceEquals(untouched, await FloosakOtpValidation.CompleteAsync(() => Task.FromResult(untouched))),
            "a call without response evidence keeps its original outcome");
        var conflicting = new WalletStepResult { Status = OperationStatus.Unknown, ErrorCode = WalletErrorCode.InvalidProviderResponse };
        Check(ReferenceEquals(conflicting, await FloosakOtpValidation.CompleteAsync(() =>
        {
            FloosakOtpValidation.Observe("/api/v1/agent/offline_cashout/confirm", 422, Rejection);
            FloosakOtpValidation.Observe("/api/v1/agent/offline_cashout/confirm", 422, Rejection);
            return Task.FromResult(conflicting);
        })), "multiple responses cannot be reduced to a single rejected confirmation");
        Console.WriteLine("PASS: actual Floosak DLL sends the approved transaction ID and stable request reference across prepare/save/OTP; observed 422 is handled; other outcomes and parallel calls remain isolated.");
    }

    private static async Task<WalletStepResult> RunWithdrawalAsync(string response, bool normalize = true,
        HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var reference = Guid.NewGuid().ToString("N");
        using var server = new WithdrawalHandler(reference, response, status);
        await using var client = new WalletClient(new WalletFactoryBuilder().AddFloosak()
            .UseHttpClientFactory(options => new HttpClient(server, disposeHandler: false) { BaseAddress = options.BaseAddress }).Build(), null);
        Check(await client.LoginAsync(new FloosakOptions
        {
            BaseAddress = new Uri("https://floosak.test"), PhoneNumber = "967777000000", Password = "synthetic-password",
            ResponseReceived = observed => FloosakOtpValidation.Observe(observed.Path, observed.StatusCode, observed.Body)
        }), "login with multiple provider wallets and null statusCode");
        var prepared = await client.Floosak.RequestWithdrawFromCustomerAsync(reference, Mobile, 1000m, CurrencyCode.YER, CurrencyCode.YER);
        Check(prepared.Status == OperationStatus.RequiresAction && prepared.NextAction == WalletStepNextAction.Confirmation &&
            prepared.Customer?.Name == "Synthetic Customer", "customer quote before approval");
        var saved = await client.Floosak.ConfirmAsync(prepared.OperationId!, true);
        Check(saved.Status == OperationStatus.RequiresAction && saved.NextAction == WalletStepNextAction.OneTimePassword,
            "save requests OTP after approval");
        var invalidCode = await client.Floosak.CompleteAsync(saved.OperationId!, "invalid-code");
        Check(invalidCode.Status == OperationStatus.Failed && invalidCode.NextAction == WalletStepNextAction.OneTimePassword && server.Calls == 3,
            "library rejects nonnumeric OTP locally without losing the pending workflow or sending HTTP");
        var retryOperation = new PaymentWalletOperation { Stage = "Complete", ProviderCode = "Floosak", RequestId = reference,
            ProviderOperationId = saved.OperationId, Mobile = Mobile, Amount = 1000m, CustomerName = prepared.Customer!.Name };
        WalletPaymentService.ApplyResult(retryOperation, invalidCode, "invalid-code");
        Check(retryOperation.Status == "AwaitingCode" && retryOperation.IsOpen && retryOperation.NextAction == "OneTimePassword" &&
            retryOperation.ProviderReference is null && retryOperation.ProviderSucceededAtUtc is null,
            "local OTP syntax error keeps the same attempt and customer data available for correction");
        Func<Task<WalletStepResult>> complete = () => client.Floosak.CompleteAsync(saved.OperationId!, "654321");
        var result = normalize ? await FloosakOtpValidation.CompleteAsync(complete, "654321") : await complete();
        Check(server.Calls == 4 && server.ConfirmCalls == 1, "single prepare, save, and confirmation without replay");
        Check(result.RequestId == reference, "library continuation retains the original client reference");
        return result;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Floosak completion: " + message);
    }

    private sealed class WithdrawalHandler(string reference, string finalResponse, HttpStatusCode finalStatus) : HttpMessageHandler
    {
        public int Calls;
        public int ConfirmCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            await Task.Yield();
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/api/v1/auth/login")
                return Json(HttpStatusCode.OK, """{"data":{"token":"synthetic-token","wallets":[{"id":101,"currency":{"id":1,"symbol":{"en":"YER"}},"balance":62000},{"id":102,"currency":{"id":2,"symbol":{"en":"$"}},"balance":0},{"id":103,"currency":{"id":3,"symbol":{"en":"SAR"}},"balance":0},{"id":104,"currency":{"id":4,"symbol":{"en":"21G"}},"balance":0}]},"message":"Successfully","is_success":true,"statusCode":null}""");
            if (path == "/api/v1/agent/offline_cashout")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var root = body.RootElement;
                Check(root.GetProperty("request_id").GetString() == reference &&
                    root.GetProperty("source_wallet_id").GetInt32() == 101 && root.GetProperty("fee_currency_id").GetInt32() == 1 &&
                    root.GetProperty("target_phone").GetString() == Mobile && root.GetProperty("amount").GetDecimal() == 1000m,
                    "preparation uses customer phone, amount, fresh reference and YER login wallet");
                return Json(HttpStatusCode.OK, """{"data":{"net":1000,"id":900,"name":"Synthetic Customer","identity_type":null,"identity_number":null,"commission":0,"currency":{"id":1,"symbol":{"en":"YER"}},"group_id":"Synthetic Group","fee_currency_id":1,"fee_currency_amount":80},"message":"Successfully","is_success":true,"statusCode":null}""");
            }
            if (path == "/api/v1/agent/offline_cashout/save/900")
            {
                Check(request.Content is null, "save targets the approved provider transaction with no extra payload");
                return Json(HttpStatusCode.OK, """{"data":{"net":1000,"id":900,"name":"Synthetic Customer","commission":0,"currency":{"id":1,"symbol":{"en":"YER"}},"group_id":"Synthetic Group"},"message":"Successfully","is_success":true,"statusCode":null}""");
            }
            Check(path == "/api/v1/agent/offline_cashout/confirm", "expected confirmation endpoint");
            ConfirmCalls++;
            using var confirmation = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var payload = confirmation.RootElement;
            Check(payload.GetProperty("transaction_id").GetInt32() == 900 && payload.GetProperty("request_id").GetString() == reference &&
                payload.GetProperty("source_wallet_id").GetInt32() == 101 && payload.GetProperty("amount").GetDecimal() == 1000m &&
                payload.GetProperty("phone").GetInt64().ToString() == Mobile && payload.GetProperty("otp").GetInt32() == 654321,
                "OTP confirmation retains provider transaction ID, client reference, wallet, customer and amount");
            return Json(finalStatus, finalResponse);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
