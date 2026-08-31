using System.Text.Json;
using System.Text.Json.Serialization;

namespace AStockWidget.Services;

/// <summary>
/// App configuration read from <c>config.json</c> next to the executable.
/// </summary>
public sealed class AppConfig
{
    public const string DefaultWebSocketUrl = "wss://api.tickdb.ai/v1/realtime";

    [JsonPropertyName("ApiKey")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("WebSocketUrl")]
    public string WebSocketUrl { get; set; } = DefaultWebSocketUrl;

    public static AppConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.WebSocketUrl))
                    {
                        loaded.WebSocketUrl = DefaultWebSocketUrl;
                    }
                    return loaded;
                }
            }
            catch
            {
                // Fall back to defaults; the UI will show a config warning.
            }
        }

        var config = new AppConfig();
        config.Save();
        return config;
    }

    public void Save()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignore if the output directory is not writable.
        }
    }
}