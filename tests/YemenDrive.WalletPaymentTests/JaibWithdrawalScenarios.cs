using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using eqsWallets;
using eqsWallets.Providers.Jaib;
using YemenDrive.Api.WalletPayments;
using YemenDrive.Database.Entities;

internal static class JaibWithdrawalScenarios
{
    private const string Mobile = "777000001";
    private const string ExactOtpRejection = """
        {"result":null,"targetUrl":null,"success":false,"error":{"code":-1005,"message":"الكود المدخل غير صحيح","details":null,"validationErrors":null},"unAuthorizedRequest":false,"__abp":true}
        """;
    private const string ExactInsufficientFunds = """
        {"result":null,"targetUrl":null,"success":false,"error":{"code":49,"message":"رصيد المحفظة رقم 782782177 غير كافي","details":null,"validationErrors":null},"unAuthorizedRequest":false,"__abp":true}
        """;

    public static async Task RunAsync()
    {
        var certificatePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jaib-withdrawal-" + Guid.NewGuid().ToString("N") + ".pfx");
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=Jaib Synthetic Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx, "synthetic-grant-secret"));
        try
        {
            using var server = new WithdrawalHandler();
            await using var client = new WalletClient(new WalletFactoryBuilder().AddJaib()
                .UseHttpClientFactory(options => new HttpClient(server, disposeHandler: false) { BaseAddress = options.BaseAddress }).Build(), null);
            Check(await client.LoginAsync(new JaibOptions
            {
                BaseAddress = new Uri("https://jaib.test"), AgentCode = "synthetic-agent", ClientId = "synthetic-client",
                ClientSecret = "synthetic-secret", UserName = "synthetic-user", Password = "synthetic-password",
                CertificatePath = certificatePath, ProviderGrant = "synthetic-grant-", AgentClientSecret = "secret",
                CertificateKeyStorageFlags = X509KeyStorageFlags.EphemeralKeySet,
                ResponseReceived = response => JaibWithdrawalValidation.Observe(response.Path, response.StatusCode,
                    response.Body, "synthetic-password")
            }), "login with a synthetic certificate and mocked HTTP");

            var reference = Guid.NewGuid().ToString("N");
            var prepared = await client.Jaib.RequestWithdrawalAsync(reference, Mobile, 1000m, CurrencyCode.YER);
            Check(prepared.Success && prepared.Status == OperationStatus.RequiresAction && prepared.NextAction == WalletStepNextAction.Confirmation,
                "ordinary customer withdrawal returns confirmation first");
            Check(prepared.Customer is { IsProviderVerified: true, Name: "Synthetic Jaib Customer", Mobile: Mobile, Amount: 1000m,
                FeesKnown: true, Fees: 80m, Currency: "YER", FeeCurrency: "YER" }, "verified customer quote is returned from PCashoutInitial");
            Check(server.InitialCalls == 1 && server.ConfirmCalls == 0, "preparation creates no financial confirmation");
            var operation = new PaymentWalletOperation { ProviderCode = "Jaib", RequestId = reference, Mobile = Mobile, Amount = 1000m, Stage = "Request" };
            WalletPaymentService.ApplyResult(operation, prepared);
            Check(operation.Status == "AwaitingConfirmation" && operation.CustomerName == "Synthetic Jaib Customer" && operation.Fees == 80m &&
                operation.ProviderSucceededAtUtc is null, "API exposes Jaib quote without marking the ride paid");

            var duplicate = await client.Jaib.RequestWithdrawalAsync(reference, Mobile, 1000m, CurrencyCode.YER);
            Check(duplicate.OperationId == prepared.OperationId && server.InitialCalls == 1, "same reference resumes without another preparation");
            var approved = await client.Jaib.ConfirmAsync(prepared.OperationId!, true);
            Check(approved.Success && approved.NextAction == WalletStepNextAction.OneTimePassword && server.InitialCalls == 1 && server.ConfirmCalls == 0,
                "customer approval is local and opens OTP without another HTTP or SMS request");
            operation.Stage = "Confirm";
            WalletPaymentService.ApplyResult(operation, approved);
            Check(operation.Status == "AwaitingCode" && operation.CustomerName == "Synthetic Jaib Customer" && operation.ProviderSucceededAtUtc is null,
                "customer identity persists while waiting for OTP");
            server.ExpectedReference = reference;
            var completed = await client.Jaib.CompleteAsync(approved.OperationId!, "654321");
            Check(completed.Status == OperationStatus.Succeeded && completed.RequestId == reference &&
                completed.Receipt?.Reference.ProviderTransactionId?.Value == "JAIB-SYNTHETIC-RECEIPT" && server.ConfirmCalls == 1,
                "OTP confirms the approved transaction with its saved request reference");
            operation.Stage = "Complete";
            WalletPaymentService.ApplyResult(operation, completed, "654321");
            Check(operation.Status == "ProviderSucceeded" && operation.ProviderReference == "JAIB-SYNTHETIC-RECEIPT",
                "confirmed receipt can proceed to the shared accounting settlement");
            await client.Jaib.CompleteAsync(approved.OperationId!, "654321");
            Check(server.ConfirmCalls == 1, "consumed continuation cannot send a second debit");

            var cancelled = await client.Jaib.RequestWithdrawalAsync(Guid.NewGuid().ToString("N"), Mobile, 1000m, CurrencyCode.YER);
            var cancellation = await client.Jaib.ConfirmAsync(cancelled.OperationId!, false);
            Check(!cancellation.Success && server.InitialCalls == 2 && server.ConfirmCalls == 1,
                "new reference creates a new preparation and rejecting the quote never confirms a debit");
            server.RejectPreparation = true;
            var rejected = await client.Jaib.RequestWithdrawalAsync(Guid.NewGuid().ToString("N"), Mobile, 1000m, CurrencyCode.YER);
            Check(!rejected.Success && rejected.NextAction == WalletStepNextAction.None && server.ConfirmCalls == 1,
                "provider preparation rejection does not open OTP or confirm a debit");
            server.RejectPreparation = false;
            server.ConfirmationBody = """{"success":false,"error":{"code":-199999,"message":"الكود المدخل غير صحيح"}}""";
            var rawReference = Guid.NewGuid().ToString("N");
            var rawQuote = await client.Jaib.RequestWithdrawalAsync(rawReference, Mobile, 1000m, CurrencyCode.YER);
            var rawApproval = await client.Jaib.ConfirmAsync(rawQuote.OperationId!, true);
            server.ExpectedReference = rawReference;
            var rawRejection = await client.Jaib.CompleteAsync(rawApproval.OperationId!, "654321");
            Check(!rawRejection.Success && rawRejection.Status == OperationStatus.Unknown,
                "actual DLL treats an unrecognized negative rejection code as Unknown");

            var rejectedReference = Guid.NewGuid().ToString("N");
            server.ConfirmationBody = ExactOtpRejection;
            server.ConfirmationStatus = HttpStatusCode.InternalServerError;
            var rejectedQuote = await client.Jaib.RequestWithdrawalAsync(rejectedReference, Mobile, 1000m, CurrencyCode.YER);
            var rejectedApproval = await client.Jaib.ConfirmAsync(rejectedQuote.OperationId!, true);
            server.ExpectedReference = rejectedReference;
            var otpRejection = await JaibWithdrawalValidation.RunAsync(JaibWithdrawalValidation.ConfirmationPath,
                () => client.Jaib.CompleteAsync(rejectedApproval.OperationId!, "654321"), "654321");
            Check(otpRejection.Status == OperationStatus.Failed && !otpRejection.Success,
                "exact -1005 ABP rejection from the actual DLL becomes a definitive failed payment");
            var rejectedOperation = new PaymentWalletOperation
            { ProviderCode = "Jaib", RequestId = rejectedReference, Mobile = Mobile, Amount = 1000m, Stage = "Complete" };
            WalletPaymentService.ApplyResult(rejectedOperation, otpRejection, "654321");
            Check(rejectedOperation.Status == "Failed" && !rejectedOperation.IsOpen &&
                rejectedOperation.ProviderSucceededAtUtc is null &&
                WalletPaymentService.View(rejectedOperation).Message.Contains("رمز التحقق غير صحيح"),
                "OTP rejection releases other payment methods and creates no payment success");
            Check(rejectedReference != rawReference && rejectedReference != reference,
                "a new withdrawal uses a new request reference");

            server.InitialAmount = 1500000m;
            server.InitialBody = ExactInsufficientFunds;
            server.InitialStatus = HttpStatusCode.InternalServerError;
            var insufficientRaw = await client.Jaib.RequestWithdrawalAsync(Guid.NewGuid().ToString("N"), Mobile, 1500000m, CurrencyCode.YER);
            Check(insufficientRaw.Status == OperationStatus.Unknown && insufficientRaw.ErrorCode == WalletErrorCode.Rejected &&
                insufficientRaw.ProviderCode == "49", "actual DLL reproduces HTTP 500/code 49 as Unknown/Rejected");
            var insufficientReference = Guid.NewGuid().ToString("N");
            var confirmationsBeforeInsufficient = server.ConfirmCalls;
            var initialCallsBeforeInsufficient = server.InitialCalls;
            var insufficient = await JaibWithdrawalValidation.RunAsync(JaibWithdrawalValidation.PreparationPath,
                () => client.Jaib.RequestWithdrawalAsync(insufficientReference, Mobile, 1500000m, CurrencyCode.YER));
            Check(insufficient.Status == OperationStatus.Failed && insufficient.ErrorCode == WalletErrorCode.InsufficientFunds &&
                insufficient.ProviderCode == "49" && insufficient.NextAction == WalletStepNextAction.None,
                "HTTP 500/code 49 rejects preparation before identity confirmation or OTP");
            var insufficientOperation = new PaymentWalletOperation
            { ProviderCode = "Jaib", RequestId = insufficientReference, Mobile = Mobile, Amount = 1500000m, Stage = "Request" };
            WalletPaymentService.ApplyResult(insufficientOperation, insufficient);
            Check(insufficientOperation.Status == "Failed" && !insufficientOperation.IsOpen &&
                insufficientOperation.CustomerName is null && insufficientOperation.ProviderSucceededAtUtc is null &&
                WalletPaymentService.View(insufficientOperation).Message.Contains("رصيد حسابك لا يكفي"),
                "preparation rejection releases payment choices and exposes the customer balance reason without inventing identity");
            Check(server.InitialCalls == initialCallsBeforeInsufficient + 1 && server.ConfirmCalls == confirmationsBeforeInsufficient,
                "normalization never retries preparation or submits a confirmation");

            var conflictingReceipt = await JaibWithdrawalValidation.RunAsync(JaibWithdrawalValidation.ConfirmationPath, () =>
            {
                JaibWithdrawalValidation.Observe(JaibWithdrawalValidation.ConfirmationPath, 500, ExactOtpRejection);
                return Task.FromResult(new WalletStepResult { Status = OperationStatus.Unknown,
                    Receipt = new TransactionReceipt(completed.Receipt!.Reference, completed.Receipt.Amount,
                        completed.Receipt.Fees, DateTimeOffset.UtcNow) });
            });
            Check(conflictingReceipt.Status == OperationStatus.Unknown && conflictingReceipt.Receipt?.CompletedAt is not null,
                "a completed receipt cannot be overridden by a conflicting rejection");
            await CheckRejectionContractsAsync();
            Console.WriteLine("PASS: actual Jaib DLL quote/approval/OTP, HTTP 500 code 49 and -1005 rejection, stable references, no duplicate debit and conflicting-receipt protection.");
        }
        finally { File.Delete(certificatePath); }
    }

    private static async Task CheckRejectionContractsAsync()
    {
        const string rejection = """{"success":false,"error":{"code":-199999,"message":"الكود المدخل غير صحيح"}}""";
        foreach (var body in new[]
        {
            """{"success":true,"error":{"code":-199999,"message":"الكود المدخل غير صحيح"}}""",
            """{"success":false,"result":{"referenceID":"possible-payment"},"error":{"code":-199999,"message":"خطأ"}}""",
            """{"success":false,"success":true,"error":{"code":-199999,"message":"خطأ"}}""",
            """{"success":false,"error":{"code":0,"message":"خطأ"}}""",
            """{"success":false,"error":{"code":-199999,"code":-1,"message":"خطأ"}}""",
            """{"success":false,"error":{"code":"-199999","message":"خطأ"}}""",
            """{"message":"الكود المدخل غير صحيح"}""",
            "not-json",
        })
        {
            var result = await ObserveAsync(body);
            Check(result.Status == OperationStatus.Unknown, "malformed or conflicting rejection cannot release the reservation");
        }
        Check((await ObserveAsync(rejection, httpStatus: 503)).Status == OperationStatus.Unknown,
            "server errors remain unknown");
        foreach (var body in new[]
        {
            rejection,
            ExactOtpRejection.Replace("\"__abp\":true", "\"__abp\":false"),
            ExactOtpRejection.Replace("\"unAuthorizedRequest\":false", "\"unAuthorizedRequest\":true"),
            ExactOtpRejection.Replace("\"targetUrl\":null", "\"targetUrl\":\"/login\""),
            ExactOtpRejection.Replace("\"result\":null,", ""),
        })
            Check((await ObserveAsync(body, httpStatus: 500)).Status == OperationStatus.Unknown,
                "HTTP 500 without the consistent ABP business failure envelope remains unknown");
        Check((await ObserveAsync(rejection, responseCount: 0)).Status == OperationStatus.Unknown,
            "no provider reply remains unknown");
        Check((await ObserveAsync(rejection, responseCount: 2)).Status == OperationStatus.Unknown,
            "multiple responses cannot establish which call was rejected");
        Check((await ObserveAsync(rejection, path: JaibWithdrawalValidation.PreparationPath)).Status == OperationStatus.Unknown,
            "a preparation response cannot decide the completion outcome");
        Check((await ObserveAsync(rejection)).Status == OperationStatus.Failed,
            "an explicit negative provider code needs no message matching to establish rejection");
        foreach (var httpStatus in new[] { 200, 400, 422, 500 })
            Check((await ObserveAsync(ExactOtpRejection, httpStatus)).Status == OperationStatus.Failed,
                "observed -1005 response with null result and ABP fields rejects the confirmation");
        var insufficient = await ObserveAsync(ExactInsufficientFunds, 500);
        Check(insufficient.Status == OperationStatus.Failed && insufficient.ErrorCode == WalletErrorCode.InsufficientFunds,
            "positive provider code 49 maps to insufficient funds independent of message matching");
        var sanitized = await ObserveAsync("""{"success":false,"error":{"code":-199999,"message":"رفض الطلب otp=654321 password=synthetic-password"}}""");
        Check(!sanitized.Message.Contains("654321") && !sanitized.Message.Contains("synthetic-password"),
            "submitted code and provider password are excluded from exposed rejection text");
    }

    private static Task<WalletStepResult> ObserveAsync(string body, int httpStatus = 200, int responseCount = 1,
        string path = JaibWithdrawalValidation.ConfirmationPath) =>
        JaibWithdrawalValidation.RunAsync(JaibWithdrawalValidation.ConfirmationPath, () =>
        {
            for (var i = 0; i < responseCount; i++) JaibWithdrawalValidation.Observe(path, httpStatus, body, "synthetic-password");
            return Task.FromResult(new WalletStepResult { Status = OperationStatus.Unknown, ErrorCode = WalletErrorCode.InvalidProviderResponse });
        }, "654321");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Jaib withdrawal: " + message);
    }

    private sealed class WithdrawalHandler : HttpMessageHandler
    {
        public int InitialCalls;
        public int ConfirmCalls;
        public string? ExpectedReference;
        public bool RejectPreparation;
        public string? ConfirmationBody;
        public string? InitialBody;
        public decimal InitialAmount = 1000m;
        public HttpStatusCode InitialStatus = HttpStatusCode.OK;
        public HttpStatusCode ConfirmationStatus = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/api/v1/TokenAuth/PLogin")
                return Json("""{"success":true,"result":{"accessToken":"synthetic-token","pinApi":"synthetic-pin","expire":43200}}""");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = body.RootElement;
            Check(root.GetProperty("mobile").GetString() == Mobile && root.GetProperty("amount").GetDecimal() == InitialAmount &&
                root.GetProperty("currencyCode").GetString() == "YER", "HTTP sends the local phone and agreed amount/currency");
            if (path == "/api/v1/PJCash/PCashoutInitial")
            {
                InitialCalls++;
                Check(!root.TryGetProperty("otp", out _) && !root.TryGetProperty("trnID", out _), "ordinary withdrawal has no pre-existing reference or OTP");
                if (RejectPreparation) return Json("""{"success":false,"error":{"code":-1030,"message":"لقد تجاوز عدد مرات إرسال رمز التأكيد"}}""");
                if (InitialBody is not null) return Json(InitialBody, InitialStatus);
                return Json("""{"success":true,"result":{"trnID":"JAIB-SYNTHETIC-INITIAL","walletName":"Synthetic Jaib Customer","walletMobile":"777000001","fee":80,"agentFee":0,"currencyCodeFee":"YER"}}""");
            }
            Check(path == "/api/v1/PJCash/PCashoutConfirm", "only documented ordinary withdrawal endpoints are used");
            ConfirmCalls++;
            Check(root.GetProperty("trnID").GetString() == "JAIB-SYNTHETIC-INITIAL" && root.GetProperty("requestID").GetString() == ExpectedReference &&
                root.GetProperty("customerName").GetString() == "Synthetic Jaib Customer" && root.GetProperty("otp").GetString() == "654321",
                "financial confirmation sends the quote transaction and same client reference with OTP");
            return Json(ConfirmationBody ?? """{"success":true,"result":{"referenceID":"JAIB-SYNTHETIC-RECEIPT","publicNumber":"SYNTHETIC-PUBLIC-NUMBER","agentFee":0,"currencyCodeFee":"YER"}}""", ConfirmationStatus);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
