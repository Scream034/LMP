using LMP.Core.Models;
using LMP.Core.Helpers;

namespace LMP.Core.Youtube.Utils;

public static class YoutubeClientUtils
{
    public static YoutubeClientProfile CurrentProfile { get; set; } = YoutubeClientProfile.WebRemix;

    // User-Agents
    public const string UaVr = "com.google.android.apps.youtube.vr.oculus/1.61.48 (Linux; U; Android 12; en_US; Quest 3; Build/SQ3A.220605.009.A1; Cronet/132.0.6808.3)";
    public const string UaTv = "Mozilla/5.0 (PlayStation; PlayStation 4/12.02) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15";
    public const string UaWeb = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    public const string UaWebRemix = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    public const string UaAndroidMusic = "com.google.android.apps.youtube.music/7.27.52 (Linux; U; Android 14; en_US; Pixel 8 Pro; Build/AP2A.240805.005)";
    public const string UaIos = "com.google.ios.youtube/19.29.1 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)";

    public static string UserAgent => CurrentProfile switch
    {
        YoutubeClientProfile.AndroidVR => UaVr,
        YoutubeClientProfile.TV => UaTv,
        YoutubeClientProfile.Web => UaWeb,
        YoutubeClientProfile.WebRemix => UaWebRemix,
        _ => UaWebRemix
    };

    public static bool RequiresAuth => CurrentProfile is YoutubeClientProfile.Web or YoutubeClientProfile.WebRemix;

    /// <summary>
    /// Порядок клиентов для стримов.
    /// 
    /// <para><b>ANDROID_VR первый</b> — не требует PO Token (pot), нет лимита на Range requests.
    /// WEB_REMIX требует pot для скачивания более ~1MB без авторизации.</para>
    /// 
    /// <para><b>WEB_REMIX</b> используется как fallback только если пользователь авторизован
    /// (с siu=1 лимит снимается). См. <see cref="GetStreamFallbackClients"/>.</para>
    /// </summary>
    public static readonly string[] StreamFallbackClientsDefault =
    [
        "ANDROID_VR",     // Основной — без pot, без sig, без лимита
        "ANDROID_MUSIC",  // Fallback
    ];

    /// <summary>
    /// Клиенты для авторизованных пользователей.
    /// WEB_REMIX включён потому что с авторизацией pot не нужен.
    /// </summary>
    public static readonly string[] StreamFallbackClientsAuth =
    [
        "ANDROID_VR",     // Основной — без pot, без sig, без лимита
        "WEB_REMIX",      // С авторизацией — без лимита (siu=1)
        "ANDROID_MUSIC",  // Fallback
    ];

    /// <summary>
    /// Возвращает список клиентов для fallback в зависимости от состояния авторизации.
    /// </summary>
    /// <param name="isAuthenticated">Авторизован ли пользователь.</param>
    public static string[] GetStreamFallbackClients(bool isAuthenticated)
    {
        return isAuthenticated ? StreamFallbackClientsAuth : StreamFallbackClientsDefault;
    }

    /// <summary>
    /// Обратная совместимость — используется где не передаётся auth state.
    /// Без авторизации по умолчанию.
    /// </summary>
    public static string[] StreamFallbackClients => StreamFallbackClientsDefault;

    /// <summary>
    /// Клиенты для получения HLS.
    /// </summary>
    public static readonly string[] HlsFallbackClients =
    [
        "IOS",
        "ANDROID_VR",
        "WEB_REMIX"
    ];

    public static string GeneratePlayerContext(string videoId, string? visitorData)
    {
        return GeneratePlayerContextForClient(
            CurrentProfile.ToString().ToUpperInvariant().Replace("WEBREMIX", "WEB_REMIX"),
            videoId, visitorData);
    }

    /// <summary>
    /// Генерирует контекст для конкретного клиента.
    /// </summary>
    public static string GeneratePlayerContextForClient(string clientName, string videoId, string? visitorData, string? signatureTimestamp = null)
    {
        var hl = YoutubeHttpHandler.GetHl();
        var gl = YoutubeHttpHandler.GetGl();

        var vidJson = Json.Serialize(videoId);
        var vdJson = Json.Serialize(visitorData);
        var hlJson = Json.Serialize(hl);
        var glJson = Json.Serialize(gl);

        return clientName switch
        {
            "WEB_REMIX" => $$"""
            {
              "videoId": {{vidJson}},
              "contentCheckOk": true,
              "racyCheckOk": true,
              "context": {
                "client": {
                  "clientName": "WEB_REMIX",
                  "clientVersion": "1.20260209.03.00",
                  "visitorData": {{vdJson}},
                  "hl": {{hlJson}},
                  "gl": {{glJson}},
                  "utcOffsetMinutes": 0
                }
              }{{(signatureTimestamp != null ? $$"""
              ,
              "playbackContext": {
                "contentPlaybackContext": {
                  "signatureTimestamp": {{Json.Serialize(signatureTimestamp)}}
                }
              }
              """ : "")}}
            }
            """,

            "ANDROID_VR" => $$"""
            {
              "videoId": {{vidJson}},
              "contentCheckOk": true,
              "racyCheckOk": true,
              "context": {
                "client": {
                  "clientName": "ANDROID_VR",
                  "clientVersion": "1.61.48",
                  "deviceMake": "Oculus",
                  "deviceModel": "Quest 3",
                  "osName": "Android",
                  "osVersion": "12",
                  "androidSdkVersion": "32",
                  "platform": "MOBILE",
                  "visitorData": {{vdJson}},
                  "hl": "en",
                  "gl": "US",
                  "utcOffsetMinutes": 0
                }
              }
            }
            """,

            "ANDROID_MUSIC" => $$"""
            {
              "videoId": {{vidJson}},
              "contentCheckOk": true,
              "racyCheckOk": true,
              "context": {
                "client": {
                  "clientName": "ANDROID_MUSIC",
                  "clientVersion": "7.27.52",
                  "androidSdkVersion": "34",
                  "osName": "Android",
                  "osVersion": "14",
                  "platform": "MOBILE",
                  "visitorData": {{vdJson}},
                  "hl": {{hlJson}},
                  "gl": {{glJson}},
                  "utcOffsetMinutes": 0
                }
              }
            }
            """,

            "WEB" => $$"""
            {
              "videoId": {{vidJson}},
              "contentCheckOk": true,
              "racyCheckOk": true,
              "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "2.20250120.01.00",
                  "visitorData": {{vdJson}},
                  "hl": {{hlJson}},
                  "gl": {{glJson}},
                  "utcOffsetMinutes": 0
                }
              }
            }
            """,

            "IOS" => $$"""
            {
              "videoId": {{vidJson}},
              "contentCheckOk": true,
              "racyCheckOk": true,
              "context": {
                "client": {
                  "clientName": "IOS",
                  "clientVersion": "19.29.1",
                  "deviceMake": "Apple",
                  "deviceModel": "iPhone16,2",
                  "osName": "iOS",
                  "osVersion": "17.5.1",
                  "platform": "MOBILE",
                  "visitorData": {{vdJson}},
                  "hl": {{hlJson}},
                  "gl": {{glJson}},
                  "utcOffsetMinutes": 0
                }
              }
            }
            """,

            "TVHTML5_SIMPLY_EMBEDDED_PLAYER" => $$"""
            {
              "videoId": {{vidJson}},
              "context": {
                "client": {
                  "clientName": "TVHTML5_SIMPLY_EMBEDDED_PLAYER",
                  "clientVersion": "2.0",
                  "visitorData": {{vdJson}},
                  "hl": {{hlJson}},
                  "gl": {{glJson}},
                  "utcOffsetMinutes": 0,
                  "platform": "TV"
                },
                "thirdParty": {
                  "embedUrl": "https://www.youtube.com"
                }
              }{{(signatureTimestamp != null ? $$"""
              ,
              "playbackContext": {
                "contentPlaybackContext": {
                  "signatureTimestamp": {{Json.Serialize(signatureTimestamp)}}
                }
              }
              """ : "")}}
            }
            """,

            _ => GeneratePlayerContextForClient("WEB_REMIX", videoId, visitorData)
        };
    }

    /// <summary>
    /// Возвращает User-Agent для конкретного клиента.
    /// </summary>
    public static string GetUserAgentForClient(string clientName) => clientName switch
    {
        "WEB_REMIX" => UaWebRemix,
        "ANDROID_VR" => UaVr,
        "ANDROID_MUSIC" => UaAndroidMusic,
        "WEB" => UaWeb,
        "IOS" => UaIos,
        "TVHTML5_SIMPLY_EMBEDDED_PLAYER" => UaTv,
        _ => UaWebRemix
    };
}