using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Cache;
using LMP.Core.Data;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Models;

/// <summary>
/// JSON Source Generator для оптимизации памяти и производительности.
/// Убирает накладные расходы рефлексии при работе с моделями.
/// </summary>
[JsonSerializable(typeof(LegacyLibraryData))]
[JsonSerializable(typeof(TrackInfo))]
[JsonSerializable(typeof(List<TrackInfo>))]
[JsonSerializable(typeof(Playlist))]
[JsonSerializable(typeof(List<Playlist>))]
[JsonSerializable(typeof(AudioCacheManager.AudioCacheIndexEnvelope))]
[JsonSerializable(typeof(AudioCacheEntry))]
[JsonSerializable(typeof(List<AudioCacheEntry>))]
[JsonSerializable(typeof(CachedSearchResult))]
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(BootstrapSettings))]
[JsonSerializable(typeof(AuthState))]
[JsonSerializable(typeof(NotificationService.AttemptDto))]
[JsonSerializable(typeof(List<NotificationService.AttemptDto>))]
[JsonSerializable(typeof(DecryptorCache.DecryptorCacheData))]
[JsonSerializable(typeof(YoutubeAccountItem))]
[JsonSerializable(typeof(List<YoutubeAccountItem>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
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