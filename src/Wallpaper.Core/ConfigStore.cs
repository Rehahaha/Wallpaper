using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallpaper.Core;

/// <summary>config.json 读写，位于 %APPDATA%\Wallpaper\config.json。</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string RootDir { get; }

    public ConfigStore(string? rootDir = null)
    {
        RootDir = rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Wallpaper");
    }

    public string ConfigPath => Path.Combine(RootDir, "config.json");

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            // 配置损坏时不要拦死启动，退化为默认配置。
            Log.Warn("config load failed: " + ex.Message);
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(RootDir);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}
