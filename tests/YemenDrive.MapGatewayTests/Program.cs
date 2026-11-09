using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YemenDrive.Api;
using YemenDrive.Application.Maps;
using YemenDrive.Shared.Api;

var checks = 0;
var logs = new CapturedLogs();
using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
var logger = loggerFactory.CreateLogger<GoogleMapsGateway>();
const string sensitiveText = "test-only-sensitive-key-never-log";
var options = new GoogleMapsOptions
{
    RoutesApiKey = "  route-test-key  ",
    PlacesApiKey = "places-test-key",
    GeocodingApiKey = sensitiveText
};
const string routeJson = """
{"routes":[{"distanceMeters":1500,"duration":"185.1s","polyline":{"encodedPolyline":"test-polyline"},"legs":[{"steps":[{"distanceMeters":500,"staticDuration":"60s","navigationInstruction":{"instructions":"تابع السير","maneuver":"STRAIGHT"}}]}]}]}
""";
const string placesJson = """
{"places":[{"displayName":{"text":"موقع تجريبي"},"formattedAddress":"شارع تجريبي، صنعاء، اليمن","location":{"latitude":15.36,"longitude":44.20},"addressComponents":[{"longText":"شارع تجريبي","types":["route"]}]}]}
""";
const string geocodeJson = """
{"status":"OK","results":[{"formatted_address":"شارع تجريبي، صنعاء، اليمن","address_components":[{"long_name":"شارع تجريبي","types":["route"]}]}]}
""";
var operations = new[]
{
    new MapOperation("routes", (gateway, token) => gateway.ComputeRouteAsync(15.36, 44.20, 15.38, 44.22, false, token)),
    new MapOperation("places", (gateway, token) => gateway.SearchAsync("موقع تجريبي", token)),
    new MapOperation("geocoding", (gateway, token) => gateway.ReverseGeocodeAsync(15.36, 44.20, token))
};

using (var http = new HttpClient(new StubHandler(async (request, _) =>
{
    Check(request.Headers.GetValues("X-Goog-Api-Key").Single() == "route-test-key", "route key is trimmed before the provider request");
    Check(request.Headers.GetValues("X-Goog-FieldMask").Single().Contains("navigationInstruction"), "route steps use the explicit provider field mask");
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
    Check(body.RootElement.GetProperty("travelMode").GetString() == "DRIVE", "route quote requests driving distance");
    return Json(routeJson);
})))
{
    var result = await new GoogleMapsGateway(http, options, logger).ComputeRouteAsync(15.36, 44.20, 15.38, 44.22, true, default);
    Check(result.DistanceMeters == 1500 && result.DurationSeconds == 186 && result.Steps.Count == 1,
        "valid route distance duration and instructions are preserved");
}
using (var http = new HttpClient(new StubHandler((request, _) =>
{
    Check(request.Headers.GetValues("X-Goog-Api-Key").Single() == "places-test-key", "place search uses its dedicated server key");
    return Task.FromResult(Json(placesJson));
})))
{
    var result = await new GoogleMapsGateway(http, options, logger).SearchAsync("موقع تجريبي", default);
    Check(result.Count == 1 && result[0].Latitude == 15.36 && result[0].Title == "موقع تجريبي", "valid place search result is preserved");
}
using (var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(geocodeJson)))))
{
    var result = await new GoogleMapsGateway(http, options, logger).ReverseGeocodeAsync(15.36, 44.20, default);
    Check(result is { Latitude: 15.36, Longitude: 44.20, Street: "شارع تجريبي" }, "valid geocoding result is preserved");
}

foreach (var operation in operations)
{
    var sends = 0;
    using (var http = new HttpClient(new StubHandler((_, _) => { sends++; throw new InvalidOperationException("Unexpected network call."); })))
    {
        await Error(operation.Run(new GoogleMapsGateway(http, new GoogleMapsOptions(), logger), default), operation.Code + "_not_configured",
            operation.Code + " missing configuration retains its explicit error");
        Check(sends == 0, operation.Code + " missing configuration does not call the provider");
    }
    foreach (var failure in new[] { "network", "timeout", "invalid-json", "array-root", "null-root", "wrong-field-type", "http-rejection" })
    {
        using var http = new HttpClient(new StubHandler((_, _) => failure switch
        {
            "network" => Task.FromException<HttpResponseMessage>(new HttpRequestException("https://example.invalid/?key=" + sensitiveText)),
            "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("https://example.invalid/?key=" + sensitiveText)),
            "invalid-json" => Task.FromResult(Json("not-json " + sensitiveText)),
            "array-root" => Task.FromResult(Json("[]")),
            "null-root" => Task.FromResult(Json("null")),
            "wrong-field-type" => Task.FromResult(Json(operation.Code switch
            {
                "routes" => "{\"routes\":[{\"polyline\":{\"encodedPolyline\":\"line\"},\"distanceMeters\":\"wrong\",\"duration\":\"5s\"}]}",
                "places" => "{\"places\":\"wrong\"}",
                _ => "{\"status\":\"OK\",\"results\":\"wrong\"}"
            })),
            _ => Task.FromResult(Json("{\"error_message\":\"" + sensitiveText + "\"}", HttpStatusCode.Forbidden))
        }));
        await Error(operation.Run(new GoogleMapsGateway(http, options, logger), default), operation.Code + "_unavailable",
            operation.Code + " normalizes " + failure);
    }
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    using (var http = new HttpClient(new StubHandler((_, token) => Task.FromCanceled<HttpResponseMessage>(token))))
    {
        try
        {
            await operation.Run(new GoogleMapsGateway(http, options, logger), cancelled.Token);
            throw new InvalidOperationException("Caller cancellation should propagate.");
        }
        catch (OperationCanceledException) { Check(true, operation.Code + " preserves caller cancellation"); }
    }
}

foreach (var duration in new[] { "1e100s", "2147483648s", "NaNs", "-1s" })
{
    using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
    {
        routes = new[] { new { distanceMeters = 1, duration, polyline = new { encodedPolyline = "line" } } }
    })))));
    await Error(new GoogleMapsGateway(http, options, logger).ComputeRouteAsync(15.36, 44.20, 15.38, 44.22, false, default), "routes_unavailable",
        "invalid or overflowing route duration is rejected");
}

foreach (var status in new[] { "REQUEST_DENIED", "OVER_QUERY_LIMIT", "OVER_DAILY_LIMIT", "UNKNOWN_ERROR" })
{
    using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(
        JsonSerializer.Serialize(new { status, error_message = sensitiveText, results = Array.Empty<object>() })))));
    await Error(new GoogleMapsGateway(http, options, logger).ReverseGeocodeAsync(15.36, 44.20, default), "geocoding_unavailable",
        "geocoding HTTP 200 " + status + " is reported as unavailable");
}
using (var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json("{\"status\":\"ZERO_RESULTS\",\"results\":[]}")))))
{
    Check(await new GoogleMapsGateway(http, options, logger).ReverseGeocodeAsync(15.36, 44.20, default) is null,
        "geocoding ZERO_RESULTS remains a successful empty result");
}
using (var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json("{\"routes\":[]}")))))
{
    await Error(new GoogleMapsGateway(http, options, logger).ComputeRouteAsync(15.36, 44.20, 15.38, 44.22, false, default), "route_not_found",
        "no provider route does not become fabricated straight-line pricing");
}
using (var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json("{\"routes\":false}")))))
{
    await Error(new GoogleMapsGateway(http, options, logger).ComputeRouteAsync(15.36, 44.20, 15.38, 44.22, false, default), "routes_unavailable",
        "invalid route collection is a provider failure rather than a missing road");
}

// Exercise the actual API registration, including constructor selection, reload
// on an already-created gateway, and absence of default URI-bearing HTTP logs.
var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
var seenRouteKeys = new List<string>();
var reloadHandler = new StubHandler((request, _) =>
{
    if (request.RequestUri!.Host == "routes.googleapis.com")
    {
        seenRouteKeys.Add(request.Headers.GetValues("X-Goog-Api-Key").Single());
        return Task.FromResult(Json(routeJson));
    }
    return Task.FromResult(Json(geocodeJson));
});
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
services.AddConfiguredGoogleMaps(configuration).ConfigurePrimaryHttpMessageHandler(() => reloadHandler);
await using (var provider = services.BuildServiceProvider())
{
    var gateway = provider.GetRequiredService<GoogleMapsGateway>();
    await Error(gateway.ComputeRouteAsync(15.36, 44.20, 15.38, 44.22, false, default), "routes_not_configured", "registered gateway initially reports absent configuration");
    configuration["GoogleMaps:RoutesApiKey"] = "reload-first";
    configuration["GoogleMaps:GeocodingApiKey"] = sensitiveText;
    configuration.Reload();
    await gateway.ComputeRouteAsync(15.36, 44.20, 15.38, 44.22, false, default);
    configuration["GoogleMaps:RoutesApiKey"] = "reload-second";
    configuration.Reload();
    await gateway.ComputeRouteAsync(15.36, 44.20, 15.38, 44.22, false, default);
    await gateway.ReverseGeocodeAsync(15.36, 44.20, default);
    Check(seenRouteKeys.SequenceEqual(new[] { "reload-first", "reload-second" }), "existing gateway uses newly loaded and rotated server keys");
}

var settingsFile = Path.GetTempFileName();
try
{
    await File.WriteAllTextAsync(settingsFile, JsonSerializer.Serialize(new
    {
        GoogleMaps = new { RoutesApiKey = "persisted-test-key", PlacesApiKey = "persisted-test-key", GeocodingApiKey = "persisted-test-key" }
    }));
    var persistedKeyUses = 0;
    for (var start = 0; start < 2; start++)
    {
        using var persistedConfiguration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(settingsFile, optional: false, reloadOnChange: false).Build();
        var startupServices = new ServiceCollection();
        startupServices.AddLogging();
        startupServices.AddConfiguredGoogleMaps(persistedConfiguration).ConfigurePrimaryHttpMessageHandler(() => new StubHandler((request, _) =>
        {
            if (request.Headers.GetValues("X-Goog-Api-Key").Single() == "persisted-test-key") persistedKeyUses++;
            return Task.FromResult(Json(placesJson));
        }));
        await using var startup = startupServices.BuildServiceProvider();
        await startup.GetRequiredService<GoogleMapsGateway>().SearchAsync("موقع تجريبي", default);
    }
    Check(persistedKeyUses == 2, "fresh registrations reload persisted server settings without Development user-secrets");
}
finally { File.Delete(settingsFile); }

Check(logs.Messages.All(message => !message.Contains(sensitiveText, StringComparison.Ordinal) &&
    !message.Contains("https://", StringComparison.Ordinal)), "transport and provider failures never log keys or request URIs");
Console.WriteLine($"PASS: {checks} Google Maps gateway checks; fake HTTP only, no database or real credentials used.");

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine("PASS: " + description);
}
async Task Error(Task operation, string code, string description)
{
    try { await operation; throw new InvalidOperationException("Expected map error: " + code); }
    catch (ServiceException error)
    {
        Check(error.Code == code && error.InnerException is null && !error.Message.Contains(sensitiveText, StringComparison.Ordinal), description);
    }
}
static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};

sealed record MapOperation(string Code, Func<GoogleMapsGateway, CancellationToken, Task> Run);
sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
sealed class CapturedLogs : ILoggerProvider, ILogger
{
    public List<string> Messages { get; } = [];
    public ILogger CreateLogger(string categoryName) => this;
    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Dispose() { }
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
