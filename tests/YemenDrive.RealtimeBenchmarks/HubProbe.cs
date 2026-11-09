using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace YemenDrive.RealtimeBenchmarks;

/// <summary>A real authenticated SignalR JSON/WebSocket client, not an in-process Hub mock.</summary>
internal sealed class HubProbe : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource stop = new();
    private Task? reader;
    private Task? keepAlive;
    private long nextId;
    internal Action<string, JsonElement>? OnEvent;
    internal Exception? Failure;

    internal async Task ConnectAsync(string url, string token)
    {
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var negotiation = await http.PostAsync("hubs/tracking/negotiate?negotiateVersion=1", null);
        negotiation.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await negotiation.Content.ReadAsStringAsync());
        var connectionToken = json.RootElement.GetProperty("connectionToken").GetString();
        var uri = new UriBuilder(url) { Scheme = "ws", Path = "/hubs/tracking",
            Query = $"id={Uri.EscapeDataString(connectionToken!)}&transport=WebSockets&access_token={Uri.EscapeDataString(token)}" };
        await socket.ConnectAsync(uri.Uri, stop.Token);
        await SendAsync(new { protocol = "json", version = 1 });
        reader = ReadAsync();
        keepAlive = KeepAliveAsync();
    }

    private async Task KeepAliveAsync()
    {
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stop.Token)) await SendAsync(new { type = 6 });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { Failure = error; }
    }

    internal async Task<JsonElement> InvokeAsync(string method, params object[] arguments)
    {
        var id = Interlocked.Increment(ref nextId).ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try {
            await SendAsync(new { type = 1, invocationId = id, target = method, arguments });
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            return result.TryGetProperty("result", out var value) ? value.Clone() : default;
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(object frame)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame) + '\u001e');
        await sendGate.WaitAsync(stop.Token);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, stop.Token); }
        finally { sendGate.Release(); }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[32768];
        using var frame = new MemoryStream();
        try {
            while (!stop.IsCancellationRequested) {
                var read = await socket.ReceiveAsync(buffer, stop.Token);
                if (read.MessageType == WebSocketMessageType.Close) throw new IOException("SignalR peer closed.");
                for (var i = 0; i < read.Count; i++) {
                    if (buffer[i] != 0x1e) { frame.WriteByte(buffer[i]); continue; }
                    using var json = JsonDocument.Parse(frame.ToArray());
                    frame.SetLength(0);
                    var root = json.RootElement;
                    if (!root.TryGetProperty("type", out var type)) continue; // handshake
                    if (type.GetInt32() == 3 && root.TryGetProperty("invocationId", out var id) && pending.TryGetValue(id.GetString()!, out var tcs))
                        tcs.TrySetResult(root.Clone());
                    else if (type.GetInt32() == 1) OnEvent?.Invoke(root.GetProperty("target").GetString()!, root.GetProperty("arguments")[0].Clone());
                    else if (type.GetInt32() == 7) throw new IOException("SignalR closed the protocol.");
                }
            }
        }
        catch (Exception error) when (stop.IsCancellationRequested) { _ = error; }
        catch (Exception error) { Failure = error; foreach (var item in pending.Values) item.TrySetException(error); }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); socket.Abort();
        if (reader != null) await reader;
        if (keepAlive != null) await keepAlive;
        socket.Dispose(); stop.Dispose(); sendGate.Dispose();
    }
}
