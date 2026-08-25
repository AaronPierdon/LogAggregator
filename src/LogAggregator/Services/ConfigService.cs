using System;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using LogAggregator.Models;

namespace LogAggregator.Services;

/// <summary>
/// Loads/saves sources.config.json in the application's base directory (next to the exe),
/// so all source cards, names, patterns, colors, and file paths survive an app restart.
/// </summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string ConfigPath { get; }

    public ConfigService(string? overridePath = null)
    {
        ConfigPath = overridePath ?? Path.Combine(AppContext.BaseDirectory, "sources.config.json");
    }

    public async Task<AppConfig> LoadAsync()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();

            using var stream = File.OpenRead(ConfigPath);
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions).ConfigureAwait(false);
            return config ?? new AppConfig();
        }
        catch
        {
            // A corrupt or unreadable config shouldn't prevent the app from starting.
            return new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath) ?? AppContext.BaseDirectory);

        var tempPath = ConfigPath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions).ConfigureAwait(false);
        }

        File.Copy(tempPath, ConfigPath, overwrite: true);
        File.Delete(tempPath);
    }
}
