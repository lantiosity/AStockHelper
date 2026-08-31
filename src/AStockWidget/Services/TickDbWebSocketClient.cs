using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AStockWidget.Models;

namespace AStockWidget.Services;

/// <summary>
/// Client for TickDB realtime WebSocket API.
/// Supports ping/heartbeat, subscribe/unsubscribe, reconnection and ticker parsing.
/// </summary>
public sealed class TickDbWebSocketClient : IDisposable
{
    private readonly string _apiKey;
    private readonly string _url;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _symbols = new();
    private ClientWebSocket? _socket;
    private System.Threading.Timer? _pingTimer;
    private Task? _loop;
    private bool _disposed;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public event Action<TickerQuote>? QuoteReceived;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? ErrorOccurred;

    public TickDbWebSocketClient(string apiKey, string url)
    {
        _apiKey = apiKey ?? string.Empty;
        _url = string.IsNullOrWhiteSpace(url) ? AppConfig.DefaultWebSocketUrl : url;
    }

    public void SetSymbols(IEnumerable<string> symbols)
    {
        List<string> added;
        List<string> removed;

        lock (_gate)
        {
            var next = symbols.Distinct().ToList();
            added = next.Except(_symbols).ToList();
            removed = _symbols.Except(next).ToList();
            _symbols.Clear();
            _symbols.AddRange(next);
        }

        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        // Apply incremental changes when connected.
        _ = Task.Run(async () =>
        {
            try
            {
                if (IsConnected)
                {
                    if (removed.Count > 0)
                    {
                        await SendCommandAsync("unsubscribe", "ticker", removed);
                    }
                    if (added.Count > 0)
                    {
                        await SendCommandAsync("subscribe", "ticker", added);
                    }
                }
            }
            catch
            {
                // Re-connection will resubscribe to the full list.
            }
        });
    }

    public Task StartAsync()
    {
        lock (_gate)
        {
            _loop ??= ConnectLoopAsync(_cts.Token);
        }
        return _loop;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var retryDelayMs = 2000;

        while (!ct.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            try
            {
                var uri = BuildUri();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await ws.ConnectAsync(uri, ct);

                lock (_gate)
                {
                    var old = _socket;
                    _socket = ws;
                    TryDispose(old);
                }

                retryDelayMs = 2000;
                StartPingTimer();
                ConnectionChanged?.Invoke(true);

                await SubscribeAllAsync(ws, ct);
                await ReceiveLoopAsync(ws, ct);

                // Socket closed cleanly or reached a terminal state; reconnect.
                ConnectionChanged?.Invoke(false);
                StopPingTimer();
                lock (_gate)
                {
                    if (ReferenceEquals(_socket, ws))
                    {
                        _socket = null;
                    }
                }
                TryDispose(ws);

                try
                {
                    await Task.Delay(retryDelayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDispose(ws);
                break;
            }
            catch (Exception ex)
            {
                ConnectionChanged?.Invoke(false);
                ErrorOccurred?.Invoke(ex.Message);
                StopPingTimer();

                lock (_gate)
                {
                    if (ReferenceEquals(_socket, ws))
                    {
                        _socket = null;
                    }
                }
                TryDispose(ws);

                try
                {
                    await Task.Delay(retryDelayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                retryDelayMs = Math.Min(retryDelayMs * 2, 30_000);
            }
        }

        StopPingTimer();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            ProcessMessage(text);
        }
    }

    private void ProcessMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var cmd = GetString(root, "cmd");
            if (cmd == "ticker" && root.TryGetProperty("data", out var data))
            {
                var symbol = GetString(data, "symbol");
                var lastPrice = GetString(data, "last_price");
                if (!string.IsNullOrEmpty(symbol) && !string.IsNullOrEmpty(lastPrice))
                {
                    var quote = new TickerQuote
                    {
                        Symbol = symbol,
                        LastPrice = lastPrice,
                        PriceChange24h = GetString(data, "price_change_24h"),
                        PriceChangePercent24h = GetString(data, "price_change_percent_24h"),
                        Volume24h = GetString(data, "volume_24h"),
                        High24h = GetString(data, "high_24h"),
                        Low24h = GetString(data, "low_24h"),
                        Timestamp = GetInt64(data, "timestamp")
                    };
                    QuoteReceived?.Invoke(quote);
                }
            }
        }
        catch
        {
            // Ignore malformed messages.
        }
    }

    private async Task SubscribeAllAsync(ClientWebSocket ws, CancellationToken ct)
    {
        List<string> symbols;
        lock (_gate)
        {
            symbols = _symbols.ToList();
        }

        if (symbols.Count == 0)
        {
            return;
        }

        await SendCommandAsync("subscribe", "ticker", symbols);
    }

    private Task SendCommandAsync(string cmd, string channel, IReadOnlyList<string> symbols)
    {
        var data = new Dictionary<string, object?>
        {
            ["channel"] = channel,
            ["symbols"] = symbols
        };

        // "type" is documented as optional and mainly relevant to subscribe.
        if (cmd == "subscribe")
        {
            data["type"] = "stock";
        }

        var payload = new Dictionary<string, object?>
        {
            ["cmd"] = cmd,
            ["data"] = data
        };

        return SendStringAsync(JsonSerializer.Serialize(payload));
    }

    private async Task SendStringAsync(string json)
    {
        await _sendGate.WaitAsync();
        try
        {
            var ws = _socket;
            if (ws?.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private Uri BuildUri()
    {
        var builder = new UriBuilder(_url);
        var apiParam = "api_key=" + Uri.EscapeDataString(_apiKey);

        if (string.IsNullOrEmpty(builder.Query))
        {
            builder.Query = apiParam;
        }
        else
        {
            builder.Query = builder.Query.TrimStart('?') + "&" + apiParam;
        }

        return builder.Uri;
    }

    private void StartPingTimer()
    {
        StopPingTimer();
        _pingTimer = new System.Threading.Timer(_ =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (IsConnected)
                    {
                        await SendStringAsync("{\"cmd\":\"ping\"}");
                    }
                }
                catch
                {
                    // Ignore; reconnect will take over if the socket is dead.
                }
            });
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopPingTimer()
    {
        _pingTimer?.Dispose();
        _pingTimer = null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetRawText();
        }

        return null;
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;
    }

    private static void TryDispose(ClientWebSocket? ws)
    {
        try
        {
            ws?.Dispose();
        }
        catch
        {
            // Ignore.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        StopPingTimer();
        TryDispose(_socket);
        _sendGate.Dispose();
    }
}