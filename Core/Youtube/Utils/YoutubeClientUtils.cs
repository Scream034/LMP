using LMP.Core.Models;

namespace LMP.Core.Youtube.Utils;

public static class YoutubeClientUtils
{
    public static YoutubeClientProfile CurrentProfile { get; set; } = YoutubeClientProfile.AndroidVR;

    // User-Agents
    public const string UaVr = "com.google.android.apps.youtube.vr.oculus/1.61.48 (Linux; U; Android 12; en_US; Quest 3; Build/SQ3A.220605.009.A1; Cronet/132.0.6808.3)";
    public const string UaTv = "Mozilla/5.0 (PlayStation; PlayStation 4/12.02) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15";
    public const string UaWeb = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    public const string UaAndroidMusic = "com.google.android.apps.youtube.music/7.27.52 (Linux; U; Android 14; en_US; Pixel 8 Pro; Build/AP2A.240805.005)";
    public const string UaIos = "com.google.ios.youtube/19.29.1 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)";

    public static string UserAgent => CurrentProfile switch
    {
        YoutubeClientProfile.AndroidVR => UaVr,
        YoutubeClientProfile.TV => UaTv,
        YoutubeClientProfile.Web => UaWeb,
        _ => UaVr
    };

    public static bool RequiresAuth => CurrentProfile == YoutubeClientProfile.Web;

    /// <summary>
    /// Порядок клиентов для fallback при ошибках воспроизведения.
    /// </summary>
    public static readonly string[] FallbackClients = 
    [
        "ANDROID_VR",
        "ANDROID_MUSIC", 
        "WEB",
        "IOS",
        "TVHTML5_SIMPLY_EMBEDDED_PLAYER"
    ];

    public static string GeneratePlayerContext(string videoId, string? visitorData)
    {
        return GeneratePlayerContextForClient(CurrentProfile.ToString().ToUpperInvariant(), videoId, visitorData);
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

            _ => GeneratePlayerContextForClient("ANDROID_VR", videoId, visitorData)
        };
    }

    /// <summary>
    /// Возвращает User-Agent для конкретного клиента.
    /// </summary>
    public static string GetUserAgentForClient(string clientName) => clientName switch
    {
        "ANDROID_VR" => UaVr,
        "ANDROID_MUSIC" => UaAndroidMusic,
        "WEB" => UaWeb,
        "IOS" => UaIos,
        "TVHTML5_SIMPLY_EMBEDDED_PLAYER" => UaTv,
        _ => UaVr
    };
}