namespace AStockWidget.Models;

/// <summary>
/// A stock entry in the built-in A-share symbol database.
/// </summary>
public sealed class StockInfo
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Market { get; set; } = "";   // SH / SZ / BJ
    public string Exchange { get; set; } = ""; // SSE / SZSE / BSE
}