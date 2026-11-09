using YemenDrive.Application.Maps;

namespace YemenDrive.Api;

public static class GoogleMapsRegistration
{
    public static IHttpClientBuilder AddConfiguredGoogleMaps(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<GoogleMapsOptions>().Bind(configuration.GetSection("GoogleMaps"));
        // The Geocoding API requires its key in the request URI. Its default
        // HttpClient loggers would record that URI; the gateway logs only safe
        // provider/status summaries and normalizes transport failures.
        return services.AddHttpClient<GoogleMapsGateway>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
        }).RemoveAllLoggers();
    }
}
