using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using eqsWallets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YemenDrive.Api.WalletPayments;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;
using MethodEntity = YemenDrive.Database.Entities.PaymentMethod;

foreach (var phone in new[] { "777123456", "+967777123456", "00967777123456", "967777123456", "0777123456", "٧٧٧١٢٣٤٥٦" })
{
    Check(WalletPaymentService.NormalizeMobile(phone) == "967777123456", "Floosak phone uses country prefix");
    Check(WalletPaymentService.NormalizeMobile(phone, "Jaib") == "777123456", "Jaib phone is local nine digits");
}
foreach (var phone in new[] { "966777123456", "123", "777123456junk", "+967967777123456" })
{
    try { WalletPaymentService.NormalizeMobile(phone); throw new InvalidOperationException("Invalid mobile accepted"); }
    catch (ServiceException) { }
}
foreach (var unsafeReceipt in new TransactionReceipt?[] { null,
    new(new TransactionReference(new ClientRequestId("receipt-test"), new ProviderTransactionId("ref")),
        new Money(999m, CurrencyCode.YER), null, null),
    new(new TransactionReference(new ClientRequestId("wrong-request"), new ProviderTransactionId("ref")),
        new Money(1000m, CurrencyCode.YER), null, null) })
{
    var operation = new PaymentWalletOperation { Amount = 1000m, RequestId = "receipt-test", Stage = "Complete" };
    WalletPaymentService.ApplyResult(operation, new WalletStepResult { Status = OperationStatus.Succeeded, Receipt = unsafeReceipt });
    Check(operation.Status == "ReviewRequired" && operation.IsOpen, "missing or mismatched receipt cannot mark financial success");
}
var pendingOperation = new PaymentWalletOperation { Amount = 1000m, RequestId = "pending-test", Stage = "Complete" };
WalletPaymentService.ApplyResult(pendingOperation, new WalletStepResult { Status = OperationStatus.Pending,
    Receipt = new TransactionReceipt(new TransactionReference(new ClientRequestId("pending-test"), new ProviderTransactionId("pending-ref")),
        new Money(1000m, CurrencyCode.YER), null, null) });
Check(pendingOperation.Status == "ReviewRequired" && pendingOperation.IsOpen && pendingOperation.ProviderReference == "pending-ref",
    "pending provider receipt remains reserved with reference retained for reconciliation");
Check(ProviderText("Insufficient balance", WalletErrorCode.Rejected).Contains("رصيد حسابك لا يكفي"),
    "specific provider balance message refines generic Rejected without any local balance request");
Check(ProviderText("INSUFFICIENT_BALANCE", WalletErrorCode.Rejected).Contains("رصيد حسابك لا يكفي"), "provider balance code translated");
Check(ProviderText("Agent has insufficient funds", WalletErrorCode.InsufficientFunds).Contains("رصيد حساب الوكيل"),
    "agent balance is not misreported as customer balance");
Check(ProviderText("Customer has insufficient balance to pay merchant", WalletErrorCode.InsufficientFunds).Contains("رصيد حسابك") &&
    ProviderText("رصيد العميل غير كاف للتحويل إلى الوكيل", WalletErrorCode.InsufficientFunds).Contains("رصيد حسابك"),
    "mentioning recipient merchant does not misattribute customer balance");
Check(ProviderText("يسمح بسحب 100000 ريال في المرة الواحدة", WalletErrorCode.Rejected).Contains("100000 ريال"),
    "business amount is retained when currency is explicit");
Check(ProviderText("رمز التحقق غير صحيح", WalletErrorCode.InvalidRequest).Contains("رمز التحقق غير صحيح"), "Arabic OTP reason preserved");
Check(ProviderText("Your daily withdrawal limit was exceeded", WalletErrorCode.Rejected).Contains("الحد اليومي"), "daily provider limit translated");
var codeLimit = WalletProviderMessageFormatter.Format(new WalletStepResult { Status = OperationStatus.Failed,
    ErrorCode = WalletErrorCode.LimitExceeded, Message = "جيب: لقد تجاوز عدد مرات إرسال رمز التأكيد. الرجاء المحاولة لاحقًا. لم يُرسل طلب تأكيد السحب." }, "Failed", providerCode: "Jaib")!;
Check(codeLimit.Contains("إرسال رمز التأكيد") && !codeLimit.Contains("حدود السحب"), "Jaib code-send limit is not a money withdrawal limit");
var originalProviderName = WalletProviderMessageFormatter.Format(new WalletStepResult { Status = OperationStatus.Failed,
    ErrorCode = WalletErrorCode.Rejected, Message = "الطلب يحتاج موافقة فرع فلوسك" }, "Failed", providerCode: "Jaib")!;
Check(originalProviderName.Contains("أفادت جيب") && originalProviderName.Contains("فرع فلوسك"), "provider template does not rewrite names inside original business text");
var expiredUnknown = WalletProviderMessageFormatter.Format(new WalletStepResult { Status = OperationStatus.Unknown,
    ErrorCode = WalletErrorCode.OutcomeUnknown, Message = "OTP expired" }, "ReviewRequired", providerCode: "Jaib")!;
Check(!expiredUnknown.Contains("ابدأ طلب دفع جديد"), "unknown result never advises starting another withdrawal");
Check(ProviderText("Account not found", WalletErrorCode.Rejected).Contains("حساباً مسجلاً"), "unregistered provider account translated");
Check(ProviderText("{\"message\":\"Account suspended\",\"access_token\":\"hidden-token-value\"}", WalletErrorCode.Rejected).Contains("الحساب موقوف"),
    "JSON extracts only a display message");
Check(!ProviderText("{\"message\":\"Account suspended\",\"access_token\":\"hidden-token-value\"}", WalletErrorCode.Rejected).Contains("hidden-token-value"),
    "JSON credentials never displayed");
Check(ProviderText("الطلب يحتاج إلى موافقة فرع المحفظة", WalletErrorCode.Rejected).Contains("موافقة فرع المحفظة"),
    "unknown Arabic business reason retained");
Check(ProviderText("The beneficiary requires branch approval", WalletErrorCode.Rejected).Contains("branch approval"),
    "unknown English business detail not silently discarded");
var protectedText = ProviderText("تعذر اعتماد الطلب abc123 password=hidden-password secret=hidden-secret", WalletErrorCode.Rejected, "abc123");
Check(!protectedText.Contains("abc123") && !protectedText.Contains("hidden-password") && !protectedText.Contains("hidden-secret"),
    "unlabeled submitted code and credential values redacted");
Check(!ProviderText("System.InvalidOperationException: stack-payload-secret at Wallet.Provider.Send()", WalletErrorCode.InvalidProviderResponse).Contains("stack-payload-secret"),
    "technical traces not displayed");
Check(ProviderText(string.Concat(Enumerable.Repeat("تفاصيل الطلب تحتاج مراجعة إضافية. ", 80)), WalletErrorCode.Rejected).Length <= 600,
    "formatted provider text is bounded including ellipsis");
var misleading = new PaymentWalletOperation { Stage = "Complete", Amount = 1000m, RequestId = "misleading" };
WalletPaymentService.ApplyResult(misleading, new WalletStepResult { Status = OperationStatus.Unknown,
    ErrorCode = WalletErrorCode.OutcomeUnknown, Message = "تم الدفع بنجاح" });
Check(misleading.Status == "ReviewRequired" && misleading.IsOpen && !WalletPaymentService.View(misleading).Message.Contains("تم الدفع بنجاح"),
    "provider wording cannot override unknown financial status");
Console.WriteLine("PASS: provider messages translated/rephrased, unknown business reasons retained, sensitive values removed, financial status authoritative.");

var databaseName = "YemenDrive_WalletPaymentTests_" + Guid.NewGuid().ToString("N");
var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
var gateway = new FakeGateway();
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddDbContext<YemenDriveDbContext>(o => o.UseSqlServer(connection));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, TestUser>();
builder.Services.AddSingleton<IWalletPaymentGateway>(gateway);
builder.Services.AddScoped<AccountingPostingService>();
builder.Services.AddScoped<RideAccountingPostingService>();
builder.Services.AddScoped<ExternalWalletPaymentSettlement>();
builder.Services.AddScoped<WalletPaymentService>();
await using var app = builder.Build();
app.MapWalletPaymentEndpoints();
try
{
    await using var seed = new YemenDriveDbContext(options);
    await seed.Database.MigrateAsync();
    var rider = new User { PhoneNumber = "777123456", DisplayName = "local profile name", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "777123457", Role = UserRole.Driver };
    var outsider = new User { PhoneNumber = "777123458", Role = UserRole.Customer };
    var kind = new ServiceKind { Code = "wallet-test", NameAr = "اختبار", IsActive = true };
    var method = new MethodEntity { Code = "floosak", NameAr = "فلوسك", ProviderCode = "Floosak", IsActive = true,
        IsAvailableForRidePayment = true, Kind = PaymentMethodKind.ExternalWallet };
    var jaibMethod = new MethodEntity { Code = "jaib", NameAr = "جيب", ProviderCode = "Jaib", IsActive = true,
        IsAvailableForRidePayment = true, Kind = PaymentMethodKind.ExternalWallet };
    seed.AddRange(rider, driver, outsider, kind, method, jaibMethod);
    await seed.SaveChangesAsync();
    var catalog = new RideServiceCatalogItem { Code = "wallet-test", NameAr = "اختبار", ServiceKindId = kind.Id };
    seed.AddRange(catalog, new Wallet { UserId = rider.Id, Currency = "YER", Balance = 50m },
        new Wallet { UserId = driver.Id, Currency = "YER" }, new Wallet { UserId = outsider.Id, Currency = "YER" },
        new PaymentProviderAccount { PaymentMethodId = method.Id, ProviderCode = "Floosak", IsEnabled = true,
            ConfigurationVersion = gateway.Version, CredentialsEncrypted = "fake-unused", LoginStatus = "Ready" },
        new PaymentProviderAccount { PaymentMethodId = jaibMethod.Id, ProviderCode = "Jaib", IsEnabled = true,
            ConfigurationVersion = gateway.Version, CredentialsEncrypted = "fake-unused-jaib", LoginStatus = "Ready" });
    await seed.SaveChangesAsync();
    await new FinancialAccountProvisioningService(seed).ProvisionExistingAsync(CancellationToken.None);
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    client.DefaultRequestHeaders.Add("X-Test-User", rider.Id.ToString());
    var ride = await NewRide();
    using var unauthenticated = new HttpClient { BaseAddress = new Uri(address) };
    Check((await unauthenticated.GetAsync(Path(ride))).StatusCode == HttpStatusCode.Unauthorized, "anonymous blocked");
    using var other = new HttpClient { BaseAddress = new Uri(address) };
    other.DefaultRequestHeaders.Add("X-Test-User", outsider.Id.ToString());
    Check((await other.GetAsync(Path(ride))).StatusCode == HttpStatusCode.BadRequest, "other rider blocked");
    var empty = await Get(ride);
    Check(empty.ValueKind == JsonValueKind.Null, "GET initially null");
    var start = await Post(Path(ride), new { paymentMethodId = method.Id, mobile = "777999999", amount = 1, currency = "SAR" });
    var id = start.GetProperty("id").GetInt32();
    Check(Status(start) == "AwaitingConfirmation" && gateway.LastMobile == "967777123456" && gateway.LastAmount == 1000m,
        "server phone and fare override untrusted client fields");
    Check(start.GetProperty("customerName").GetString() == "اسم موثق من المزود" && start.GetProperty("recipient").GetString() == "يمن درايف",
        "provider verified name and fixed recipient");
    Check((await Post(Path(ride), new { paymentMethodId = method.Id })).GetProperty("id").GetInt32() == id && gateway.RequestCount == 1,
        "start retry reuses workflow");
    Check(Status(await Post(Path(ride) + $"/{id}/complete", new { code = "654321" })) == "AwaitingConfirmation" && gateway.CompleteCount == 0,
        "cannot submit OTP before confirmation");
    var confirm = await Post(Path(ride) + $"/{id}/confirm", new { approved = true });
    Check(Status(confirm) == "AwaitingCode" && gateway.ConfirmCount == 1, "provider sends OTP after confirmation");
    await Post(Path(ride) + $"/{id}/confirm", new { approved = true });
    Check(gateway.ConfirmCount == 1, "repeated confirmation does not resend");
    await using (var checkDb = new YemenDriveDbContext(options))
        Check(!await checkDb.PaymentTransactions.AnyAsync(x => x.RideId == ride), "no paid transaction after successful step");

    gateway.BlockComplete = true;
    var completing = Post(Path(ride) + $"/{id}/complete", new { code = "654321" });
    await gateway.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(Status(await Post(Path(ride) + $"/{id}/complete", new { code = "654321" })) == "Processing", "concurrent complete only observes");
    gateway.Release.TrySetResult();
    var paid = await completing;
    Check(Status(paid) == "Succeeded" && gateway.CompleteCount == 1, "one external debit under concurrent complete");
    Check(paid.GetProperty("paidAtUtc").ValueKind == JsonValueKind.String, "paid receipt date");
    await Post(Path(ride) + $"/{id}/complete", new { code = "654321" });
    Check(gateway.CompleteCount == 1 && Status(await Get(ride)) == "Succeeded", "success replay and GET are local only");
    await using (var checkDb = new YemenDriveDbContext(options))
    {
        Check(await checkDb.PaymentTransactions.CountAsync(x => x.RideId == ride && x.Status == PaymentStatus.Paid) == 1, "one financial payment");
        Check(await checkDb.Wallets.Where(x => x.UserId == rider.Id).Select(x => x.Balance).SingleAsync() == 50m, "internal rider balance unchanged");
        Check(await checkDb.Wallets.Where(x => x.UserId == driver.Id).Select(x => x.Balance).SingleAsync() == 850m, "driver credited once");
        var json = JsonSerializer.Serialize(await checkDb.PaymentWalletOperations.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.RequestId, x.ProviderOperationId, x.ErrorCode, x.CustomerName, x.ProviderReference }).SingleAsync());
        Check(!json.Contains("654321"), "OTP absent from persistence");
    }
    Console.WriteLine("PASS: endpoint auth/ownership, authoritative fare and phone, confirmation/OTP, concurrent exactly-once completion, accounting and receipt.");

    gateway.BlockComplete = false;
    var cancelledRide = await NewRide();
    var cancelStart = await Post(Path(cancelledRide), new { paymentMethodId = method.Id });
    var cancelId = cancelStart.GetProperty("id").GetInt32();
    Check(Status(await Post(Path(cancelledRide) + $"/{cancelId}/confirm", new { approved = false })) == "Cancelled", "cancel before debit");
    Check((await Post(Path(cancelledRide), new { paymentMethodId = method.Id })).GetProperty("id").GetInt32() != cancelId,
        "cancel releases reservation");

    var disabledRide = await NewRide();
    var disabledStart = await Post(Path(disabledRide), new { paymentMethodId = method.Id });
    await using (var changeDb = new YemenDriveDbContext(options))
        await changeDb.PaymentProviderAccounts.Where(x => x.PaymentMethodId == method.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
    var beforeConfirm = gateway.ConfirmCount;
    Check(Status(await Post(Path(disabledRide) + $"/{disabledStart.GetProperty("id").GetInt32()}/confirm", new { approved = true })) == "Cancelled" &&
        gateway.ConfirmCount == beforeConfirm, "immediate admin disable cancels before cached runtime refresh");
    await using (var changeDb = new YemenDriveDbContext(options))
        await changeDb.PaymentProviderAccounts.Where(x => x.PaymentMethodId == method.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, true));

    gateway.RequestFailure = true;
    var lowBalanceRide = await NewRide();
    var failed = await Post(Path(lowBalanceRide), new { paymentMethodId = method.Id });
    Check(Status(failed) == "Failed" && !failed.GetRawText().Contains("SECRET_PROVIDER_PAYLOAD"), "insufficient funds safely exposed");
    gateway.RequestFailure = false;
    gateway.RequestRejectionMessage = "الطلب يحتاج إلى موافقة فرع المحفظة";
    var messageRide = await NewRide();
    var providerRejected = await Post(Path(messageRide), new { paymentMethodId = method.Id });
    Check(Status(providerRejected) == "Failed" && providerRejected.GetProperty("message").GetString()!.Contains("موافقة فرع المحفظة"),
        "unknown provider business reason reaches HTTP response");
    var providerRecovered = await Get(messageRide);
    Check(providerRecovered.GetProperty("message").GetString() == providerRejected.GetProperty("message").GetString(),
        "GET retains provider rejection after new request scope");
    await using (var checkDb = new YemenDriveDbContext(options))
        Check((await checkDb.PaymentWalletOperations.Where(x => x.RideId == messageRide).Select(x => x.ProviderMessageAr).SingleAsync())!
            .Contains("موافقة فرع المحفظة"), "only formatted provider message persisted");
    gateway.RequestRejectionMessage = null;
    gateway.BadCustomer = true;
    Check(Status(await Post(Path(await NewRide()), new { paymentMethodId = method.Id })) == "Failed", "provider amount mismatch blocks confirmation");
    gateway.BadCustomer = false;

    var unknownRide = await NewRide();
    var unknownId = await AwaitCode(unknownRide);
    gateway.UnknownComplete = true;
    var unknown = await Post(Path(unknownRide) + $"/{unknownId}/complete", new { code = "654321" });
    Check(Status(unknown) == "ReviewRequired", "ambiguous provider result never releases money reservation");
    var requestCount = gateway.RequestCount;
    Check(Status(await Post(Path(unknownRide), new { paymentMethodId = method.Id })) == "ReviewRequired" && gateway.RequestCount == requestCount,
        "cannot start another debit after unknown outcome");
    gateway.UnknownComplete = false;

    var restartRide = await NewRide();
    await AwaitCode(restartRide);
    gateway.InstanceId = Guid.NewGuid();
    Check(Status(await Get(restartRide)) == "Cancelled", "awaiting OTP safely cancelled after process restart");
    var interruptedRide = await NewRide();
    var interruptedId = await AwaitCode(interruptedRide);
    await using (var changeDb = new YemenDriveDbContext(options))
        await changeDb.PaymentWalletOperations.Where(x => x.Id == interruptedId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, "Processing").SetProperty(x => x.Stage, "Complete").SetProperty(x => x.RuntimeInstanceId, Guid.NewGuid()));
    Check(Status(await Get(interruptedRide)) == "ReviewRequired", "interrupted dispatched debit stays reserved after restart");
    Console.WriteLine("PASS: cancellation, provider disable, insufficient funds, verified-data mismatch, unknown outcome and restart recovery.");

    var recoverRide = await NewRide();
    var recoverId = await AwaitCode(recoverRide);
    gateway.AfterComplete = async () =>
    {
        await using var changeDb = new YemenDriveDbContext(options);
        await changeDb.LedgerAccounts.Where(x => x.Code == $"1200-PM-{method.Id}").ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
    };
    var recover = await Post(Path(recoverRide) + $"/{recoverId}/complete", new { code = "654321" });
    Check(Status(recover) == "Processing", "accounting failure preserves provider evidence without success receipt");
    var beforeRecoveryCalls = gateway.CompleteCount;
    await using (var changeDb = new YemenDriveDbContext(options))
    {
        Check(await changeDb.PaymentWalletOperations.Where(x => x.Id == recoverId).Select(x => x.Status).SingleAsync() == "ProviderSucceeded", "durable provider evidence");
        Check(!await changeDb.PaymentTransactions.AnyAsync(x => x.RideId == recoverRide), "failed accounting rolled back");
        await changeDb.LedgerAccounts.Where(x => x.Code == $"1200-PM-{method.Id}").ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true));
    }
    gateway.AfterComplete = null;
    Check(Status(await Get(recoverRide)) == "Succeeded" && gateway.CompleteCount == beforeRecoveryCalls, "GET retries only local posting");
    Console.WriteLine("PASS: accounting failure rolls back money, persists provider success and recovers without another external withdrawal.");

    var jaibRide = await NewRide();
    var jaibStart = await Post(Path(jaibRide), new { paymentMethodId = jaibMethod.Id, mobile = "967999999999" });
    var jaibId = jaibStart.GetProperty("id").GetInt32();
    Check(Status(jaibStart) == "AwaitingConfirmation" && jaibStart.GetProperty("providerCode").GetString() == "Jaib" &&
        jaibStart.GetProperty("providerName").GetString() == "جيب" && jaibStart.GetProperty("customerIsProviderVerified").GetBoolean() &&
        gateway.LastMobile == "777123456" && gateway.LastProviderCode == "Jaib" && gateway.LastMethodId == jaibMethod.Id,
        "Jaib routes selected agent account and local phone with verified metadata");
    var duplicateProviderStart = await Post(Path(jaibRide), new { paymentMethodId = method.Id });
    Check(duplicateProviderStart.GetProperty("id").GetInt32() == jaibId && duplicateProviderStart.GetProperty("providerCode").GetString() == "Jaib",
        "cannot switch to Floosak while Jaib reserved the same ride");
    var jaibConfirm = await Post(Path(jaibRide) + $"/{jaibId}/confirm", new { approved = true });
    Check(Status(jaibConfirm) == "AwaitingCode" && jaibConfirm.GetProperty("message").GetString()!.Contains("جيب") &&
        !jaibConfirm.GetProperty("message").GetString()!.Contains("فلوسك"), "Jaib challenge uses correct provider name");
    var jaibPaid = await Post(Path(jaibRide) + $"/{jaibId}/complete", new { code = "876543" });
    Check(Status(jaibPaid) == "Succeeded", "Jaib completion settles verified receipt");
    await using (var checkDb = new YemenDriveDbContext(options))
    {
        Check((await checkDb.PaymentWalletOperations.SingleAsync(x => x.Id == jaibId)).ProviderCode == "Jaib", "Jaib provider metadata persisted");
        Check(await checkDb.PaymentTransactions.CountAsync(x => x.RideId == jaibRide && x.Provider == "Jaib" && x.Status == PaymentStatus.Paid) == 1,
            "Jaib creates one canonical financial payment");
    }
    gateway.JaibPreliminaryAction = WalletStepNextAction.Voucher;
    var voucherRide = await NewRide();
    var voucherStart = await Post(Path(voucherRide), new { paymentMethodId = jaibMethod.Id });
    var voucherId = voucherStart.GetProperty("id").GetInt32();
    Check(Status(voucherStart) == "AwaitingCode" && voucherStart.GetProperty("nextAction").GetString() == "Voucher" &&
        !voucherStart.GetProperty("customerIsProviderVerified").GetBoolean() && voucherStart.GetProperty("customerName").ValueKind == JsonValueKind.Null,
        "provider challenge before confirmation does not invent verified customer data");
    var afterVoucher = await Post(Path(voucherRide) + $"/{voucherId}/complete", new { code = "VOUCHER-TEST" });
    Check(Status(afterVoucher) == "AwaitingConfirmation" && afterVoucher.GetProperty("customerIsProviderVerified").GetBoolean(),
        "code-before-confirmation follows provider next action");
    await using (var checkDb = new YemenDriveDbContext(options))
        Check(!await checkDb.PaymentTransactions.AnyAsync(x => x.RideId == voucherRide), "preliminary challenge completion is not financial success");
    await Post(Path(voucherRide) + $"/{voucherId}/confirm", new { approved = true });
    Check(Status(await Post(Path(voucherRide) + $"/{voucherId}/complete", new { code = "876543" })) == "Succeeded", "generic challenge flow eventually settles once");
    gateway.JaibPreliminaryAction = null;
    gateway.RequestRejectionMessage = "Insufficient balance";
    var jaibRejected = await Post(Path(await NewRide()), new { paymentMethodId = jaibMethod.Id });
    Check(jaibRejected.GetProperty("message").GetString()!.Contains("أفادت جيب") && !jaibRejected.GetProperty("message").GetString()!.Contains("فلوسك"),
        "provider message translation attributes Jaib rejection correctly");
    gateway.RequestRejectionMessage = null;
    gateway.UnknownRequest = true;
    var unknownJaibRide = await NewRide();
    var unknownJaib = await Post(Path(unknownJaibRide), new { paymentMethodId = jaibMethod.Id });
    Check(Status(unknownJaib) == "ReviewRequired", "Jaib preparation unknown outcome is not downgraded to retryable failure");
    var beforeUnknownRetry = gateway.RequestCount;
    Check(Status(await Post(Path(unknownJaibRide), new { paymentMethodId = method.Id })) == "ReviewRequired" && gateway.RequestCount == beforeUnknownRetry,
        "unknown initial dispatch prevents all provider recharges");
    gateway.UnknownRequest = false;
    Console.WriteLine("PASS: Jaib local-phone/account routing, provider metadata, canonical settlement, cross-provider reservation and challenge-before-confirmation.");

    foreach (var earlyStatus in new[] { RideStatus.DriverAssigned, RideStatus.DriverEnRoute, RideStatus.DriverArrived })
    {
        var earlyRide = await NewRide(earlyStatus, jaibMethod.Code);
        var beforeWrongMethod = gateway.RequestCount;
        Check((await client.PostAsJsonAsync(Path(earlyRide), new { paymentMethodId = method.Id })).StatusCode == HttpStatusCode.BadRequest &&
            gateway.RequestCount == beforeWrongMethod, "prestart mismatched method rejected before provider dispatch");
        var earlyStart = await Post(Path(earlyRide), new { paymentMethodId = jaibMethod.Id });
        var earlyId = earlyStart.GetProperty("id").GetInt32();
        await Post(Path(earlyRide) + $"/{earlyId}/confirm", new { approved = true });
        Check(Status(await Post(Path(earlyRide) + $"/{earlyId}/complete", new { code = "876543" })) == "Succeeded",
            "selected external wallet can settle at every prestart stage");
        await using var earlyDb = new YemenDriveDbContext(options);
        var savedEarly = await earlyDb.Rides.AsNoTracking().SingleAsync(x => x.Id == earlyRide);
        Check(savedEarly.Status == earlyStatus && savedEarly.StartedAtUtc == null && !savedEarly.PaymentRequiredBeforeStart &&
            await earlyDb.PaymentTransactions.CountAsync(x => x.RideId == earlyRide && x.Status == PaymentStatus.Paid) == 1,
            "confirmed early receipt clears gate without starting ride or duplicate payment");
    }
    var cashEarlyRide = await NewRide(RideStatus.DriverAssigned, "cash");
    var beforeCashDispatch = gateway.RequestCount;
    Check((await client.PostAsJsonAsync(Path(cashEarlyRide), new { paymentMethodId = method.Id })).StatusCode == HttpStatusCode.BadRequest &&
        gateway.RequestCount == beforeCashDispatch, "cash booking does not dispatch an unchosen early debit");
    Console.WriteLine("PASS: early selected-wallet HTTP payment, provider mismatch guard and authoritative prestart requirement clearing.");

    async Task<int> NewRide(RideStatus status = RideStatus.InProgress, string paymentMethodCode = "cash")
    {
        await using var fresh = new YemenDriveDbContext(options);
        var next = new RideEntity { CustomerId = rider.Id, DriverId = driver.Id, ServiceKindId = kind.Id,
            ServiceCatalogItemId = catalog.Id, Status = status, CustomerPaymentEnabled = status == RideStatus.InProgress,
            PaymentMethodCode = paymentMethodCode, PaymentRequiredBeforeStart = status != RideStatus.InProgress && paymentMethodCode != "cash",
            CustomerPrice = 900m, TotalAmount = 1000m, ServiceFee = 100m, DriverCommissionAmount = 50m,
            DriverShare = 850m, PlatformShare = 150m, StartedAtUtc = status == RideStatus.InProgress ? DateTime.UtcNow : null };
        fresh.Rides.Add(next);
        await fresh.SaveChangesAsync();
        return next.Id;
    }
    async Task<int> AwaitCode(int targetRide)
    {
        var created = await Post(Path(targetRide), new { paymentMethodId = method.Id });
        var createdId = created.GetProperty("id").GetInt32();
        var verified = await Post(Path(targetRide) + $"/{createdId}/confirm", new { approved = true });
        Check(Status(verified) == "AwaitingCode", "fixture reaches OTP");
        return createdId;
    }
    async Task<JsonElement> Get(int targetRide) => await Data(await client.GetAsync(Path(targetRide)));
    async Task<JsonElement> Post(string path, object body) => await Data(await client.PostAsJsonAsync(path, body));
}
finally
{
    await app.StopAsync();
    await using var cleanup = new YemenDriveDbContext(options);
    await cleanup.Database.EnsureDeletedAsync();
}

static string Path(int ride) => $"/api/rides/{ride}/wallet-payment";
static string? Status(JsonElement value) => value.GetProperty("status").GetString();
static string ProviderText(string message, WalletErrorCode error, string? code = null) =>
    WalletProviderMessageFormatter.Format(new WalletStepResult { Status = OperationStatus.Failed, Message = message, ErrorCode = error }, "Failed", code) ?? "";
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task<JsonElement> Data(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {response.StatusCode}: {body}");
    Check(response.Headers.CacheControl?.NoStore == true, "payment response must not be cached");
    using var document = JsonDocument.Parse(body);
    Check(document.RootElement.GetProperty("success").GetBoolean(), "successful API envelope");
    return document.RootElement.GetProperty("data").Clone();
}

file sealed class TestUser(IHttpContextAccessor http) : ICurrentUserContext
{
    public int? UserId => int.TryParse(http.HttpContext?.Request.Headers["X-Test-User"].FirstOrDefault(), out var id) ? id : null;
}

file sealed class FakeGateway : IWalletPaymentGateway
{
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public Guid Version { get; } = Guid.NewGuid();
    public int RequestCount, ConfirmCount, CompleteCount;
    public string? LastMobile;
    public string? LastProviderCode;
    public int LastMethodId;
    public decimal LastAmount;
    public bool BlockComplete, RequestFailure, BadCustomer, UnknownComplete, UnknownRequest;
    public string? RequestRejectionMessage;
    public WalletStepNextAction? JaibPreliminaryAction;
    public Func<Task>? AfterComplete;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, (string RequestId, string Mobile, decimal Amount, string ProviderCode, int MethodId)> requests = new();
    private readonly ConcurrentDictionary<string, bool> preliminaryCompleted = new();
    public bool IsReady(int methodId, Guid version) => version == Version;
    public Task<WalletStepResult> RequestAsync(int methodId, Guid version, string providerCode, string requestId, string mobile, decimal amount, CancellationToken token)
    {
        Interlocked.Increment(ref RequestCount);
        LastMobile = mobile; LastAmount = amount; LastProviderCode = providerCode; LastMethodId = methodId;
        var id = Guid.NewGuid().ToString("N");
        requests[id] = (requestId, mobile, amount, providerCode, methodId);
        if (UnknownRequest) return Task.FromResult(new WalletStepResult { Status = OperationStatus.Unknown,
            ErrorCode = WalletErrorCode.OutcomeUnknown, Message = "Insufficient balance" });
        if (RequestRejectionMessage is not null) return Task.FromResult(new WalletStepResult { Status = OperationStatus.Failed,
            ErrorCode = WalletErrorCode.Rejected, Message = RequestRejectionMessage });
        if (RequestFailure) return Task.FromResult(new WalletStepResult { Status = OperationStatus.Failed,
            ErrorCode = WalletErrorCode.InsufficientFunds, Message = "SECRET_PROVIDER_PAYLOAD" });
        if (providerCode == "Jaib" && JaibPreliminaryAction is { } preliminary)
            return Task.FromResult(new WalletStepResult { Status = OperationStatus.RequiresAction, OperationId = id,
                RequestId = requestId, NextAction = preliminary, Message = "أدخل رقم القسيمة لمتابعة الطلب" });
        return Task.FromResult(Confirmation(id));
    }
    private WalletStepResult Confirmation(string id)
    {
        var request = requests[id];
        return new WalletStepResult { Status = OperationStatus.RequiresAction,
            OperationId = id, RequestId = request.RequestId, NextAction = WalletStepNextAction.Confirmation,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), Customer = new WalletCustomerData {
                IsProviderVerified = true, Name = "اسم موثق من المزود", Mobile = request.Mobile, Amount = BadCustomer ? request.Amount + 1 : request.Amount,
                Currency = "YER", Fees = 10m, FeesKnown = true, FeeCurrency = "YER", Account = "NEVER_EXPOSE_ACCOUNT" } };
    }
    public Task<WalletStepResult> ConfirmAsync(int methodId, Guid version, string providerCode, string operationId, bool approved, CancellationToken token)
    {
        if (requests[operationId].ProviderCode != providerCode || requests[operationId].MethodId != methodId)
            throw new InvalidOperationException("wrong provider/account");
        Interlocked.Increment(ref ConfirmCount);
        return Task.FromResult(new WalletStepResult { Status = OperationStatus.RequiresAction,
            OperationId = operationId, RequestId = requests[operationId].RequestId,
            NextAction = WalletStepNextAction.OneTimePassword, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
    }
    public async Task<WalletStepResult> CompleteAsync(int methodId, Guid version, string providerCode, string operationId, string code, CancellationToken token)
    {
        if (requests[operationId].ProviderCode != providerCode || requests[operationId].MethodId != methodId)
            throw new InvalidOperationException("wrong provider/account");
        Interlocked.Increment(ref CompleteCount);
        if (providerCode == "Jaib" && JaibPreliminaryAction is not null && preliminaryCompleted.TryAdd(operationId, true))
            return Confirmation(operationId);
        if (BlockComplete) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
        if (UnknownComplete) return new WalletStepResult { Status = OperationStatus.Unknown, ErrorCode = WalletErrorCode.OutcomeUnknown };
        if (AfterComplete is not null) await AfterComplete();
        var request = requests[operationId];
        var reference = "provider-" + operationId;
        return new WalletStepResult { Status = OperationStatus.Succeeded, RequestId = request.RequestId,
            ProviderReference = reference, Receipt = new TransactionReceipt(new TransactionReference(new ClientRequestId(request.RequestId),
                new ProviderTransactionId(reference)), new Money(request.Amount, CurrencyCode.YER), new Money(10m, CurrencyCode.YER), DateTimeOffset.UtcNow) };
    }
}
