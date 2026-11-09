using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Maps;

public sealed class GoogleMapsGateway
{
    private readonly HttpClient httpClient;
    private readonly Func<GoogleMapsOptions> currentOptions;
    private readonly ILogger<GoogleMapsGateway> logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [ActivatorUtilitiesConstructor]
    public GoogleMapsGateway(HttpClient httpClient, IOptionsMonitor<GoogleMapsOptions> options,
        ILogger<GoogleMapsGateway> logger)
        : this(httpClient, () => options.CurrentValue, logger) { }

    // Explicit options remain useful for isolated consumers and provider tests.
    public GoogleMapsGateway(HttpClient httpClient, GoogleMapsOptions options,
        ILogger<GoogleMapsGateway> logger)
        : this(httpClient, () => options, logger) { }

    private GoogleMapsGateway(HttpClient httpClient, Func<GoogleMapsOptions> currentOptions,
        ILogger<GoogleMapsGateway> logger)
    {
        this.httpClient = httpClient;
        this.currentOptions = currentOptions;
        this.logger = logger;
    }

    public Task<MapRouteQuoteResult> ComputeRouteAsync(double originLatitude, double originLongitude,
        double destinationLatitude, double destinationLongitude, bool includeSteps, CancellationToken token) =>
        ExecuteProviderAsync(() => ComputeRouteCoreAsync(originLatitude, originLongitude,
            destinationLatitude, destinationLongitude, includeSteps, token), "Routes",
            "routes_unavailable", "تعذر حساب مسار القيادة حالياً.", token);

    private async Task<MapRouteQuoteResult> ComputeRouteCoreAsync(
        double originLatitude,
        double originLongitude,
        double destinationLatitude,
        double destinationLongitude,
        bool includeSteps,
        CancellationToken token)
    {
        var key = RequireKey(currentOptions().RoutesApiKey, "routes_not_configured",
            "لم يُجهز خادم المسارات بعد.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://routes.googleapis.com/directions/v2:computeRoutes")
        {
            Content = JsonContent.Create(new
            {
                origin = new { location = new { latLng = new { latitude = originLatitude, longitude = originLongitude } } },
                destination = new { location = new { latLng = new { latitude = destinationLatitude, longitude = destinationLongitude } } },
                travelMode = "DRIVE",
                routingPreference = "TRAFFIC_AWARE",
                polylineQuality = "HIGH_QUALITY",
                polylineEncoding = "ENCODED_POLYLINE",
                languageCode = "ar"
            }, options: JsonOptions)
        };
        request.Headers.Add("X-Goog-Api-Key", key);
        var fieldMask = "routes.distanceMeters,routes.duration,routes.polyline.encodedPolyline";
        if (includeSteps)
        {
            fieldMask += ",routes.legs.steps.distanceMeters,routes.legs.steps.staticDuration," +
                         "routes.legs.steps.navigationInstruction.instructions," +
                         "routes.legs.steps.navigationInstruction.maneuver";
        }
        request.Headers.Add("X-Goog-FieldMask", fieldMask);
        using var response = await httpClient.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Google Routes rejected a route quote with HTTP {StatusCode}.", (int)response.StatusCode);
            throw new ServiceException("routes_unavailable", "تعذر حساب مسار القيادة حالياً.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        if (!document.RootElement.TryGetProperty("routes", out var routes))
            throw new ServiceException("route_not_found", "لم يُعثر على مسار قيادة بين النقطتين.");
        if (routes.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid route result collection.");
        if (routes.GetArrayLength() == 0)
            throw new ServiceException("route_not_found", "لم يُعثر على مسار قيادة بين النقطتين.");
        var route = routes[0];
        var encoded = route.TryGetProperty("polyline", out var polyline) &&
                      polyline.TryGetProperty("encodedPolyline", out var value)
            ? value.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(encoded))
            throw new ServiceException("route_not_found", "لم يصل خط المسار من مزود الخرائط.");
        if (!route.TryGetProperty("distanceMeters", out var distanceValue) ||
            !distanceValue.TryGetInt32(out var distance) || distance < 0 ||
            !route.TryGetProperty("duration", out var durationValue) ||
            string.IsNullOrWhiteSpace(durationValue.GetString()))
            throw new JsonException("Invalid route measurements.");
        var duration = ParseDurationSeconds(durationValue.GetString());
        var steps = includeSteps ? ParseSteps(route) : Array.Empty<MapRouteStep>();
        logger.LogInformation(
            "Google Routes returned a driving route with {DistanceMeters} meters and {DurationSeconds} seconds.",
            distance,
            duration);
        return new MapRouteQuoteResult(distance, duration, encoded, steps);
    }

    private static IReadOnlyList<MapRouteStep> ParseSteps(JsonElement route)
    {
        if (!route.TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array)
            return Array.Empty<MapRouteStep>();

        var result = new List<MapRouteStep>();
        foreach (var leg in legs.EnumerateArray())
        {
            if (!leg.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var step in steps.EnumerateArray())
            {
                var instruction = step.TryGetProperty("navigationInstruction", out var navigation) &&
                                  navigation.TryGetProperty("instructions", out var instructionValue)
                    ? instructionValue.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(instruction)) continue;
                var maneuver = navigation.TryGetProperty("maneuver", out var maneuverValue)
                    ? maneuverValue.GetString() ?? string.Empty
                    : string.Empty;
                var distance = step.TryGetProperty("distanceMeters", out var distanceValue) &&
                               distanceValue.TryGetInt32(out var parsedDistance)
                    ? parsedDistance
                    : 0;
                var duration = step.TryGetProperty("staticDuration", out var durationValue)
                    ? ParseDurationSeconds(durationValue.GetString())
                    : 0;
                result.Add(new MapRouteStep(instruction, maneuver, distance, duration));
            }
        }
        return result;
    }

    public Task<IReadOnlyList<MapPlace>> SearchAsync(string query, CancellationToken token) =>
        ExecuteProviderAsync(() => SearchCoreAsync(query, token), "Places",
            "places_unavailable", "تعذر البحث عن الأماكن حالياً.", token);

    private async Task<IReadOnlyList<MapPlace>> SearchCoreAsync(string query, CancellationToken token)
    {
        var key = RequireKey(currentOptions().PlacesApiKey, "places_not_configured",
            "لم يُجهز بحث الأماكن في الخادم بعد.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://places.googleapis.com/v1/places:searchText")
        {
            Content = JsonContent.Create(new
            {
                textQuery = query,
                languageCode = "ar",
                regionCode = "YE",
                maxResultCount = 8
            }, options: JsonOptions)
        };
        request.Headers.Add("X-Goog-Api-Key", key);
        request.Headers.Add("X-Goog-FieldMask",
            "places.displayName,places.formattedAddress,places.location,places.addressComponents");
        using var response = await httpClient.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Google Places rejected a text search with HTTP {StatusCode}.", (int)response.StatusCode);
            throw new ServiceException("places_unavailable", "تعذر البحث عن الأماكن حالياً.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        if (!document.RootElement.TryGetProperty("places", out var places))
            return Array.Empty<MapPlace>();
        if (places.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid places result collection.");
        var parsedPlaces = places.EnumerateArray()
            .Select(ParsePlace)
            .Where(place => place is not null)
            .Cast<MapPlace>()
            .ToArray();
        logger.LogInformation("Google Places returned {ResultCount} usable search result(s).", parsedPlaces.Length);
        return parsedPlaces;
    }

    public Task<MapPlace?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken token) =>
        ExecuteProviderAsync(() => ReverseGeocodeCoreAsync(latitude, longitude, token), "Geocoding",
            "geocoding_unavailable", "تعذر قراءة عنوان الموقع حالياً.", token);

    private async Task<MapPlace?> ReverseGeocodeCoreAsync(double latitude, double longitude, CancellationToken token)
    {
        var key = RequireKey(currentOptions().GeocodingApiKey, "geocoding_not_configured",
            "لم يُجهز تحويل الإحداثيات إلى عنوان في الخادم بعد.");
        var endpoint = string.Create(CultureInfo.InvariantCulture,
            $"https://maps.googleapis.com/maps/api/geocode/json?latlng={latitude},{longitude}&language=ar&key={Uri.EscapeDataString(key)}");
        using var response = await httpClient.GetAsync(endpoint, token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Google Geocoding rejected a reverse-geocode request with HTTP {StatusCode}.", (int)response.StatusCode);
            throw new ServiceException("geocoding_unavailable", "تعذر قراءة عنوان الموقع حالياً.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var status = document.RootElement.TryGetProperty("status", out var statusValue) &&
                     statusValue.ValueKind == JsonValueKind.String ? statusValue.GetString() : null;
        if (status == "ZERO_RESULTS") return null;
        if (status != "OK")
        {
            // Geocoding reports authorization/quota failures inside HTTP 200.
            // Never log its error_message or the URI, which contains the key.
            logger.LogWarning("Google Geocoding returned a non-success provider status.");
            throw new ServiceException("geocoding_unavailable", "تعذر قراءة عنوان الموقع حالياً.");
        }
        if (!document.RootElement.TryGetProperty("results", out var results))
            throw new JsonException("Missing geocoding result collection.");
        if (results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid geocoding result collection.");
        if (results.GetArrayLength() == 0)
            return null;
        var result = results[0];
        var street = FindComponent(result, "route") ?? string.Empty;
        var area = FindCityComponent(result);
        var formatted = result.TryGetProperty("formatted_address", out var address)
            ? address.GetString() ?? string.Empty
            : string.Empty;
        return new MapPlace(
            Title: FindComponent(result, "premise", "point_of_interest", "establishment", "neighborhood", "sublocality", "route") ?? "موقع",
            FormattedAddress: BuildLocalAddress(street, area, formatted),
            Street: street,
            Area: area,
            Latitude: latitude,
            Longitude: longitude);
    }

    private static string RequireKey(string key, string code, string message) =>
        string.IsNullOrWhiteSpace(key)
            ? throw new ServiceException(code, message)
            : key.Trim();

    private async Task<T> ExecuteProviderAsync<T>(Func<Task<T>> send, string provider,
        string code, string message, CancellationToken token)
    {
        try { return await send(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("Google {Provider} request timed out.", provider);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Google {Provider} request could not reach the provider.", provider);
        }
        catch (JsonException)
        {
            logger.LogWarning("Google {Provider} returned an invalid response.", provider);
        }
        catch (InvalidOperationException)
        {
            // JsonElement accessors use this exception for valid JSON with
            // unexpected object/field types. Keep it inside the map contract.
            logger.LogWarning("Google {Provider} returned an unexpected response shape.", provider);
        }
        // No original exception/URI is attached: geocoding carries a server key
        // in its query string, and provider messages can echo request details.
        throw new ServiceException(code, message);
    }

    private static int ParseDurationSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var secondsText = value.EndsWith('s') ? value[..^1] : value;
        if (!double.TryParse(secondsText, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > int.MaxValue)
            throw new JsonException("Invalid provider duration.");
        return checked((int)Math.Ceiling(seconds));
    }

    private static MapPlace? ParsePlace(JsonElement place)
    {
        if (!place.TryGetProperty("location", out var location) ||
            !location.TryGetProperty("latitude", out var latitude) ||
            !location.TryGetProperty("longitude", out var longitude) ||
            !latitude.TryGetDouble(out var lat) || !longitude.TryGetDouble(out var lng) ||
            !double.IsFinite(lat) || !double.IsFinite(lng) ||
            lat is < -90 or > 90 || lng is < -180 or > 180)
            return null;
        var title = place.TryGetProperty("displayName", out var displayName) &&
                    displayName.TryGetProperty("text", out var text)
            ? text.GetString() ?? "موقع"
            : "موقع";
        var street = FindComponent(place, "route") ?? string.Empty;
        var area = FindCityComponent(place);
        var formatted = place.TryGetProperty("formattedAddress", out var address)
            ? address.GetString() ?? string.Empty
            : string.Empty;
        return new MapPlace(
            title,
            BuildLocalAddress(street, area, formatted),
            street,
            area,
            lat,
            lng);
    }

    /// The operational UI is limited to Sana'a. Repeating the city and
    /// country in every search row consumes the useful space needed for the
    /// street and neighbourhood, so expose a concise local address instead.
    private static string BuildLocalAddress(string street, string area, string fallback)
    {
        var parts = new[] { street, area }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parts.Length > 0)
            return string.Join("، ", parts);

        return string.Join("، ", fallback
            .Split(new[] { ',', '،' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !IsSanaaOrYemen(part)));
    }

    private static bool IsSanaaOrYemen(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "اليمن" or "yemen" or "صنعاء" or "sanaa" or "sana'a";
    }

    private static string FindCityComponent(JsonElement item) =>
        FindComponent(item, "locality", "administrative_area_level_2", "administrative_area_level_1", "postal_town")
        ?? string.Empty;

    private static string? FindComponent(JsonElement item, params string[] wanted)
    {
        if ((!item.TryGetProperty("addressComponents", out var components) &&
             !item.TryGetProperty("address_components", out components)) ||
            components.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var component in components.EnumerateArray())
        {
            if (!component.TryGetProperty("types", out var types) ||
                types.ValueKind != JsonValueKind.Array ||
                !types.EnumerateArray().Any(type => wanted.Contains(type.GetString(), StringComparer.Ordinal)))
                continue;
            if (component.TryGetProperty("longText", out var longText))
                return longText.GetString();
            if (component.TryGetProperty("long_name", out var longName))
                return longName.GetString();
        }
        return null;
    }
}
