namespace AStockWidget.Models;

/// <summary>
/// A parsed TickDB WebSocket ticker message for A-share stocks.
/// </summary>
public sealed class TickerQuote
{
    public string Symbol { get; set; } = "";
    public string LastPrice { get; set; } = "";
    public string? PriceChange24h { get; set; }
    public string? PriceChangePercent24h { get; set; }
    public string? Volume24h { get; set; }
    public string? High24h { get; set; }
    public string? Low24h { get; set; }
    public long Timestamp { get; set; }
}