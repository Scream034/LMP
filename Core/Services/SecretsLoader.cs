using System.Text.Json;
using MyLiteMusicPlayer.Core.Models;

namespace MyLiteMusicPlayer.Core.Services;

public static class SecretsLoader
{
    public const string FileName = "appsecrets.json";

    private static AppSecrets? _cached;

    public static AppSecrets Load()
    {
        if (_cached != null) return _cached;

        var paths = new[]
        {
            FileName,
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                         "MyLiteMusicPlayer", FileName)
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    _cached = JsonSerializer.Deserialize<AppSecrets>(json) ?? new();
                    Log.Info($"[Settings] Loaded from: {path}");
                    return _cached;
                }
                catch (Exception ex)
                {
                    Log.Error($"[Settings] Failed to load {path}: {ex.Message}");
                }
            }
        }

        // Fallback: переменные окружения
        _cached = new AppSecrets
        {
            Google = new GoogleSeсrets
            {
                ClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") ?? "",
                ClientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? ""
            }
        };

        if (string.IsNullOrEmpty(_cached.Google.ClientId))
        {
            Log.Warn("[Settings] No configuration found! Google Auth will not work.");
        }

        return _cached;
    }
}

