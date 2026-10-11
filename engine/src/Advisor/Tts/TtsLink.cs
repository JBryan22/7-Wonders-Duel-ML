using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SevenWondersDuel.Advisor.Tts;

/// <summary>Tabletop Simulator could not be reached, refused the script, or didn't answer; the message is for the owner.</summary>
public sealed class TtsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The client side of TTS's External Editor API (spec A9, ADR 0004): it sends <c>tts/snapshot.lua</c> to TTS as
/// "Execute Lua Code" (message 3, Global, on <see cref="TtsPort"/> 39999) and listens on <see cref="ListenPort"/>
/// (39998), where TTS connects to deliver what the Lua passes to <c>sendExternalMessage</c> (message 4). Execute
/// Lua's own return value is not used: without <c>returnID</c> it never came back (research ticket 03).
/// <para>
/// Only one program can listen on 39998, so TTS's code-editor plugins (VS Code, Atom) can't run while this does.
/// Nothing is sent but the read-only snapshot script.
/// </para>
/// </summary>
public sealed class TtsLink : IDisposable
{
    public const int DefaultTtsPort = 39999;
    public const int DefaultListenPort = 39998;

    readonly TcpListener _listener;
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    readonly Task _accepting;
    int _next;

    /// <summary>Starts listening. Throws <see cref="TtsException"/> if the listen port is taken (an editor plugin or a second Advisor).</summary>
    public TtsLink(int ttsPort = DefaultTtsPort, int listenPort = DefaultListenPort)
    {
        TtsPort = ttsPort;
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        try
        {
            _listener.Start();
        }
        catch (SocketException e)
        {
            throw new TtsException(
                $"Port {listenPort} is busy: close any TTS code-editor plugin (VS Code, Atom) or another Advisor, then try again.", e);
        }
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptLoop);
    }

    public int TtsPort { get; }
    public int ListenPort { get; }

    /// <summary>The last Lua error TTS reported (message 3), if any; useful when a request times out.</summary>
    public string? LastTtsError { get; private set; }

    /// <summary>Set when TTS says it loaded a game (message 1): a new or reloaded table.</summary>
    public DateTime? GameLoadedAt { get; private set; }

    /// <summary>Asks TTS for one Table Snapshot and returns its JSON text.</summary>
    public async Task<string> RequestSnapshotTextAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        var request = $"r{Interlocked.Increment(ref _next)}-{Environment.ProcessId}";
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request] = answer;
        try
        {
            await SendAsync(TtsData.ScriptFor(request), cancel);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(timeout);
            try
            {
                return await answer.Task.WaitAsync(timer.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new TtsException(LastTtsError is { } error
                    ? $"Tabletop Simulator didn't answer; its last Lua error was: {error}"
                    : $"Tabletop Simulator didn't answer within {timeout.TotalSeconds:0.#} s. Is a game loaded, and is nothing else using port {ListenPort}?");
            }
        }
        finally
        {
            _pending.TryRemove(request, out _);
        }
    }

    /// <summary>Asks TTS for one Table Snapshot.</summary>
    public async Task<TableSnapshot> RequestSnapshotAsync(TimeSpan timeout, CancellationToken cancel = default) =>
        TableSnapshot.Parse(await RequestSnapshotTextAsync(timeout, cancel));

    async Task SendAsync(string script, CancellationToken cancel)
    {
        var message = new JsonObject { ["messageID"] = 3, ["guid"] = "-1", ["script"] = script }.ToJsonString();
        using var client = new TcpClient();
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            connect.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, TtsPort, connect.Token);
            var bytes = Encoding.UTF8.GetBytes(message);
            await client.GetStream().WriteAsync(bytes, cancel);
        }
        catch (Exception e) when (e is SocketException or IOException || (e is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            throw new TtsException(
                $"Could not reach Tabletop Simulator on port {TtsPort}. Is TTS running with the 7 Wonders Duel game loaded?", e);
        }
    }

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => Receive(client));
        }
    }

    /// <summary>TTS opens one connection per message and closes it; JSON values are handled as soon as they are complete.</summary>
    async Task Receive(TcpClient client)
    {
        using var _ = client;
        var buffer = new List<byte>();
        var chunk = new byte[64 * 1024];
        try
        {
            var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var n = await stream.ReadAsync(chunk, timeout.Token);
                if (n <= 0) break;
                buffer.AddRange(chunk.AsSpan(0, n));
                Drain(buffer);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        Drain(buffer);
    }

    /// <summary>Handles every complete JSON value at the front of the buffer and removes it.</summary>
    void Drain(List<byte> buffer)
    {
        while (buffer.Count > 0)
        {
            var bytes = buffer.ToArray();
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true, CommentHandling = JsonCommentHandling.Skip });
            JsonDocument doc;
            try
            {
                if (!JsonDocument.TryParseValue(ref reader, out var parsed)) return;
                doc = parsed;
            }
            catch (JsonException)
            {
                // Incomplete (more bytes are coming) or garbage: wait for more, or give up at the end of the connection.
                return;
            }
            buffer.RemoveRange(0, (int)reader.BytesConsumed);
            using (doc) Handle(doc.RootElement);
        }
    }

    void Handle(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("messageID", out var id) || !id.TryGetInt32(out var kind)) return;
        switch (kind)
        {
            case 1:
                GameLoadedAt = DateTime.UtcNow;
                break;
            case 3:
                LastTtsError = message.TryGetProperty("error", out var error) ? error.GetString() : "unknown error";
                break;
            case 4 when message.TryGetProperty("customMessage", out var custom) && custom.ValueKind == JsonValueKind.Object:
                var request = custom.TryGetProperty("request", out var r) ? r.GetString() : null;
                if (request is null || !_pending.TryGetValue(request, out var answer)) return;
                var type = custom.TryGetProperty("tts7wd", out var t) ? t.GetString() : null;
                if (type == "snapshot" && custom.TryGetProperty("snapshot", out var snapshot))
                    answer.TrySetResult(snapshot.GetString() ?? "");
                else if (type == "error")
                    answer.TrySetException(new TtsException(
                        $"The snapshot script failed inside TTS: {(custom.TryGetProperty("error", out var e) ? e.GetString() : "unknown error")}"));
                break;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _accepting.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
