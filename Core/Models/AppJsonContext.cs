using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Cache;
using LMP.Core.Services;

namespace LMP.Core.Models;

/// <summary>
/// JSON Source Generator для оптимизации памяти и производительности.
/// Убирает накладные расходы рефлексии при работе с моделями.
/// </summary>
[JsonSerializable(typeof(TrackInfo))]
[JsonSerializable(typeof(List<TrackInfo>))]
[JsonSerializable(typeof(Playlist))]
[JsonSerializable(typeof(List<Playlist>))]
[JsonSerializable(typeof(CacheEntry))]
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(BootstrapSettings))]
[JsonSourceGenerationOptions(
    WriteIndented = false, 
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class AppJsonContext : JsonSerializerContext
{
    public static AppJsonContext DefaultCompact { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
}