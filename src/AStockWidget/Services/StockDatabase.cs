using System.Text.Json;
using AStockWidget.Models;

namespace AStockWidget.Services;

/// <summary>
/// Loads and searches the bundled A-share stock database.
/// </summary>
public sealed class StockDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly List<StockInfo> _stocks;

    public StockDatabase()
    {
        _stocks = LoadFromDisk();
    }

    public IReadOnlyList<StockInfo> All => _stocks;

    private static List<StockInfo> LoadFromDisk()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "ashare_stocks.json");
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<List<StockInfo>>(File.ReadAllText(path), JsonOptions) ?? new();
            }
        }
        catch
        {
            // Return an empty list; the UI will report that the database is missing.
        }

        return new List<StockInfo>();
    }

    public StockInfo? FindByCode(string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return _stocks.FirstOrDefault(s => s.Code == normalized);
    }

    public List<StockInfo> Search(string query, int limit = 20)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return new List<StockInfo>();
        }

        var codeQuery = trimmed.ToUpperInvariant();
        var nameQuery = trimmed;

        return _stocks
            .Where(s => s.Code.Contains(codeQuery, StringComparison.Ordinal)
                        || s.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }
}