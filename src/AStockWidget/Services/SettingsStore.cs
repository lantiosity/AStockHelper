using System.Text.Json;
using AStockWidget.Models;

namespace AStockWidget.Services;

/// <summary>
/// Persists watchlist and window placement in %AppData%\AStockWidget.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _directory;
    private readonly string _filePath;

    public SettingsStore()
    {
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AStockWidget");
        _filePath = Path.Combine(_directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath)) ?? new AppSettings();
            }
        }
        catch
        {
            // Fall back to empty settings.
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal: UI still works, it just will not remember the watchlist.
        }
    }
}