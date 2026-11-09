using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>Never reacts to sessionRevoked; only a server close can end this connection.</summary>
sealed class IgnoringSignalRClient : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim sends = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> calls = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? reader, keepAlive;
    private long nextId;
    public ConcurrentQueue<(string Name, JsonElement Value)> Events { get; } = new();
    public Task Closed => closed.Task;

    public static async Task<IgnoringSignalRClient> ConnectAsync(string url, string token)
    {
        var client = new IgnoringSignalRClient();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(url) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.PostAsync("hubs/tracking/negotiate?negotiateVersion=1", null);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var id = json.RootElement.GetProperty("connectionToken").GetString()!;
            var uri = new UriBuilder(url) { Scheme = "ws", Path = "/hubs/tracking",
                // Match the browser SignalR client: no transport query hint and
                // no Authorization header are present on its WebSocket upgrade.
                Query = $"id={Uri.EscapeDataString(id)}&access_token={Uri.EscapeDataString(token)}" };
            await client.socket.ConnectAsync(uri.Uri, client.stop.Token);
            await client.SendAsync(new { protocol = "json", version = 1 });
            client.reader = client.ReadAsync();
            client.keepAlive = client.KeepAliveAsync();
            await client.InvokeAsync("SetForeground", true);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task InvokeAsync(string method, params object[] arguments)
    {
        var id = Interlocked.Increment(ref nextId).ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls[id] = completion;
        try
        {
            await SendAsync(new { type = 1, invocationId = id, target = method, arguments });
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (result.TryGetProperty("error", out _)) throw new IOException("Hub invocation rejected.");
        }
        finally { calls.TryRemove(id, out _); }
    }
    private async Task SendAsync(object frame)
    {
        await sends.WaitAsync(stop.Token);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame) + '\u001e'), WebSocketMessageType.Text, true, stop.Token); }
        finally { sends.Release(); }
    }
    private async Task KeepAliveAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stop.Token)) await SendAsync(new { type = 6 });
        }
        catch (Exception) when (stop.IsCancellationRequested || Closed.IsCompleted) { }
    }
    private async Task ReadAsync()
    {
        var bytes = new byte[8192];
        using var frame = new MemoryStream();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var response = await socket.ReceiveAsync(bytes, stop.Token);
                if (response.MessageType == WebSocketMessageType.Close) break;
                for (var i = 0; i < response.Count; i++)
                {
                    if (bytes[i] != 0x1e) { frame.WriteByte(bytes[i]); continue; }
                    using var json = JsonDocument.Parse(frame.ToArray()); frame.SetLength(0);
                    var root = json.RootElement;
                    if (!root.TryGetProperty("type", out var type)) continue;
                    if (type.GetInt32() == 7) return;
                    if (type.GetInt32() == 3 && root.TryGetProperty("invocationId", out var id) && calls.TryGetValue(id.GetString()!, out var call))
                        call.TrySetResult(root.Clone());
                    if (type.GetInt32() == 1)
                    {
                        var arguments = root.GetProperty("arguments");
                        Events.Enqueue((root.GetProperty("target").GetString()!, arguments.GetArrayLength() > 0 ? arguments[0].Clone() : default));
                    }
                }
            }
        }
        catch (Exception) { /* This deliberately uncooperative client observes transport closure only. */ }
        finally
        {
            closed.TrySetResult();
            foreach (var call in calls.Values) call.TrySetException(new IOException("Server closed socket."));
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); socket.Abort();
        if (reader is not null) await reader;
        if (keepAlive is not null) await keepAlive;
        socket.Dispose(); stop.Dispose(); sends.Dispose();
    }
}
