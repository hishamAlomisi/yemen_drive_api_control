using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using eqsWallets;
using eqsWallets.Providers.Cash;
using eqsWallets.Providers.Floosak;
using eqsWallets.Providers.Jaib;
using eqsWallets.Providers.Jawali;
using eqsWallets.Providers.MobileMoney;
using eqsWallets.Providers.Speed;
using eqsWallets.Providers.YeahMoney;
using YemenDrive.Shared.Api;

namespace YemenDrive.Api.WalletLogin;

public sealed record WalletProviderDefinition(string Code, string NameAr, string TemplateJson);

/// <summary>Maps the supported DLL providers to their actual login option models.</summary>
public sealed class WalletProviderCatalog
{
    private const int MaximumJsonBytes = 32 * 1024;
    private const int MaximumJsonDepth = 16;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly (string Code, string NameAr, Type OptionsType, string[] TemplateFields)[] Registrations =
    [
        ("Jaib", "جيب", typeof(JaibOptions),
            ["BaseAddress", "Environment", "AgentCode", "ClientId", "ClientSecret", "UserName", "Password",
                "CertificatePath", "ProviderGrant", "AgentClientSecret", "ExpiryUnit", "RemittanceSendRoute"]),
        ("Jawali", "جوالي", typeof(JawaliOptions),
            ["BaseAddress", "Environment", "ApiUserName", "ApiPassword", "AgentWallet", "WalletPassword",
                "OrganizationId", "ExternalUser", "PointOfServiceId", "MemberId", "StatementEmail",
                "OAuthClientId", "OAuthClientSecret", "ServiceClientId"]),
        ("Floosak", "فلوسك", typeof(FloosakOptions),
            ["BaseAddress", "Environment", "PhoneNumber", "Password", "CashInRoute"]),
        ("Cash", "كاش", typeof(CashOptions),
            ["BaseAddress", "Environment", "UserName", "Password"]),
        ("Speed", "سبيد", typeof(SpeedOptions),
            ["BaseAddress", "Environment", "ClientId", "ClientSecret", "AgentCode", "BranchCode", "UserName",
                "Password", "AgentUserName", "Language"]),
        ("MobileMoney", "موبايل موني", typeof(MobileMoneyOptions),
            ["BaseAddress", "Environment", "UserName", "Password", "ClientId"]),
        ("YeahMoney", "ياه موني", typeof(YeahMoneyOptions),
            ["BaseAddress", "Environment", "ClientId", "ClientSecret", "AgentCode", "BranchCode", "UserName",
                "Password", "AgentUserName", "Language"])
    ];

    public IReadOnlyList<WalletProviderDefinition> Providers { get; } =
        Array.AsReadOnly(Registrations.Select(x =>
            new WalletProviderDefinition(x.Code, x.NameAr, CreateTemplate(x.OptionsType, x.TemplateFields))).ToArray());

    public bool Contains(string? providerCode) => Registrations.Any(x =>
        string.Equals(x.Code, providerCode?.Trim(), StringComparison.OrdinalIgnoreCase));

    public WalletProviderDefinition Find(string? providerCode) => Providers.FirstOrDefault(x =>
        string.Equals(x.Code, providerCode?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ServiceException("wallet_provider_invalid", "اختر محفظة مدعومة من قائمة المحافظ.");

    public WalletOptions Parse(string? providerCode, string? json)
    {
        var provider = Find(providerCode);
        var registration = Registrations.Single(x => x.Code == provider.Code);
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw InvalidOptions();

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = MaximumJsonDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw InvalidOptions();
            RejectDuplicateProperties(document.RootElement);

            var options = JsonSerializer.Deserialize(json, registration.OptionsType, JsonOptions) as WalletOptions;
            if (OperatingSystem.IsWindows() && options is JaibOptions jaib &&
                !document.RootElement.EnumerateObject().Any(property =>
                    string.Equals(property.Name, nameof(JaibOptions.CertificateKeyStorageFlags), StringComparison.OrdinalIgnoreCase)))
            {
                // The DLL defaults to a persistent machine key, which requires permissions
                // ordinary API accounts may not have. Schannel can use a current-user key;
                // let certificate disposal release it and honor any explicit deployment choice.
                jaib.CertificateKeyStorageFlags = X509KeyStorageFlags.UserKeySet;
            }
            if (options is null || options.Validate().Any())
                throw InvalidOptions();
            return options;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or
            InvalidOperationException or NotSupportedException or OverflowException)
        {
            // Neither the submitted JSON nor provider exception messages may appear in API errors.
            throw InvalidOptions();
        }
    }

    private static ServiceException InvalidOptions() => new("wallet_options_invalid",
        "إعدادات المحفظة غير صالحة. تحقق من الحقول المطلوبة وأنواع القيم وعنوان HTTPS، واستخدم قالب المحفظة المحددة.");

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw InvalidOptions();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static string CreateTemplate(Type optionsType, IEnumerable<string> templateFields)
    {
        var options = (WalletOptions)Activator.CreateInstance(optionsType)!;
        foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite && property.PropertyType == typeof(string) && property.GetValue(options) is null)
                property.SetValue(options, string.Empty);
        }
        var defaults = JsonSerializer.SerializeToNode(options, optionsType, JsonOptions)!.AsObject();
        defaults["BaseAddress"] = string.Empty;
        // Keep the entry form focused on login fields; omitted options retain the DLL defaults when parsed.
        var template = new JsonObject();
        foreach (var field in templateFields)
        {
            if (!defaults.TryGetPropertyValue(field, out var value))
                throw new InvalidOperationException($"Missing wallet template field {optionsType.Name}.{field}.");
            template.Add(field, value?.DeepClone());
        }
        return template.ToJsonString(JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (!typeof(WalletOptions).IsAssignableFrom(typeInfo.Type)) return;
            for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
            {
                var property = typeInfo.Properties[index];
                if (property.Set is null || typeof(Delegate).IsAssignableFrom(property.PropertyType) ||
                    property.Name == "WorkflowStore")
                    typeInfo.Properties.RemoveAt(index);
            }
        });
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = MaximumJsonDepth,
            WriteIndented = true,
            TypeInfoResolver = resolver
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.Converters.Add(new WalletCurrencyCodeJsonConverter());
        options.Converters.Add(new WalletReferenceJsonConverter());
        options.Converters.Add(new FloosakIdJsonConverter());
        return options;
    }
}
