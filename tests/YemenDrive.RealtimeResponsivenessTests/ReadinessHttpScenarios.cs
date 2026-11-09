using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YemenDrive.Api;

static class ReadinessHttpScenarios
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var probe = new TestProbe();
        var clock = new ManualClock();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new ApiReadinessService(probe, clock));
        await using var app = builder.Build();
        app.MapApiReadiness();
        await app.StartAsync();
        try
        {
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(url) };
            using var unconfigured = await http.GetAsync("api/health/ready");
            check(unconfigured.StatusCode == HttpStatusCode.ServiceUnavailable && probe.Calls == 0,
                "HTTP readiness returns 503 without database configuration");
            probe.IsConfigured = true;
            using var ready = await http.GetAsync("api/health/ready");
            var readyBody = await ready.Content.ReadFromJsonAsync<JsonElement>();
            check(ready.IsSuccessStatusCode && readyBody.GetProperty("success").GetBoolean() &&
                readyBody.GetProperty("data").GetProperty("ready").GetBoolean() && ready.Headers.CacheControl?.NoStore == true,
                "HTTP readiness returns the client contract with no downstream cache");
            clock.Advance();
            probe.Handler = _ => throw new IOException("database-host-and-private-details");
            using var outage = await http.GetAsync("api/health/ready");
            var outageText = await outage.Content.ReadAsStringAsync();
            using var outageBody = JsonDocument.Parse(outageText);
            check(outage.StatusCode == HttpStatusCode.ServiceUnavailable && !outageBody.RootElement.GetProperty("success").GetBoolean() &&
                !outageText.Contains("database-host-and-private-details"), "HTTP database outage has a stable 503 envelope without exception details");
            clock.Advance();
            probe.Handler = _ => Task.FromResult(true);
            using var recovered = await http.GetAsync("api/health/ready");
            check(recovered.IsSuccessStatusCode, "HTTP readiness succeeds automatically after database recovery");
        }
        finally { await app.StopAsync(); }
    }
}
