namespace AStockWidget.Models;

/// <summary>
/// User settings persisted in %AppData%\AStockWidget\settings.json.
/// </summary>
public sealed class AppSettings
{
    public List<string> WatchCodes { get; set; } = new();
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
}