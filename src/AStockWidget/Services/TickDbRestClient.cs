using System.Net.Http;
using System.Text.Json;
using AStockWidget.Models;

namespace AStockWidget.Services;

/// <summary>
/// Client for the TickDB REST market ticker snapshot API.
/// Uses <c>X-API-Key</c> header, supports batching (max 50 symbols per request).
/// </summary>
public sealed class TickDbRestClient : IDisposable
{
    public const string DefaultTickerUrl = "https://api.tickdb.ai/v1/market/ticker";
    private const int BatchSize = 50;

    private readonly HttpClient _http;
    private readonly string _tickerUrl;
    private readonly string _apiKey;

    public TickDbRestClient(string apiKey, string tickerUrl = DefaultTickerUrl)
    {
        _tickerUrl = string.IsNullOrWhiteSpace(tickerUrl) ? DefaultTickerUrl : tickerUrl;
        _apiKey = apiKey ?? string.Empty;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", _apiKey);
        }
    }

    public async Task<IReadOnlyList<TickerQuote>> GetQuotesAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var distinct = symbols.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0 || string.IsNullOrWhiteSpace(_apiKey))
        {
            return Array.Empty<TickerQuote>();
        }

        var result = new List<TickerQuote>();
        for (var i = 0; i < distinct.Length; i += BatchSize)
        {
            var batch = distinct.Skip(i).Take(BatchSize).ToArray();
            var quotes = await FetchBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            result.AddRange(quotes);
        }

        return result;
    }

    private async Task<IReadOnlyList<TickerQuote>> FetchBatchAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        var symbolParam = string.Join(",", symbols.Select(Uri.EscapeDataString));
        var separator = _tickerUrl.Contains('?') ? "&" : "?";
        var url = _tickerUrl + separator + "symbols=" + symbolParam + "&type=stock";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"TickDB REST snapshot failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var code) && code != 0)
        {
            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            throw new HttpRequestException($"TickDB REST snapshot error {code}: {message}");
        }

        var list = new List<TickerQuote>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in data.EnumerateArray())
        {
            var symbol = GetString(item, "symbol");
            var lastPrice = GetString(item, "last_price");
            if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(lastPrice))
            {
                continue;
            }

            list.Add(new TickerQuote
            {
                Symbol = symbol!,
                LastPrice = lastPrice!,
                PriceChange24h = GetString(item, "price_change_24h"),
                PriceChangePercent24h = GetString(item, "price_change_percent_24h"),
                Volume24h = GetString(item, "volume_24h"),
                High24h = GetString(item, "high_24h"),
                Low24h = GetString(item, "low_24h"),
                Timestamp = GetInt64(item, "timestamp")
            });
        }

        return list;
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

    public void Dispose()
    {
        _http.Dispose();
    }
}