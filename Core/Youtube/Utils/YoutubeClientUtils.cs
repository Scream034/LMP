using LMP.Core.Models;

namespace LMP.Core.Youtube.Utils;

public static class YoutubeClientUtils
{
    // === STATE ===
    // Это значение мы будем менять из Настроек или при старте приложения
    public static YoutubeClientProfile CurrentProfile { get; set; } = YoutubeClientProfile.AndroidVR;

    // === CONSTANTS ===
    public const string UaVr = "com.google.android.apps.youtube.vr.oculus/1.61.48 (Linux; U; Android 12; en_US; Quest 3; Build/SQ3A.220605.009.A1; Cronet/132.0.6808.3)";
    public const string UaTv = "Mozilla/5.0 (PlayStation; PlayStation 4/12.02) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15";
    public const string UaWeb = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    // === PROPERTIES (Computed based on State) ===

    public static string UserAgent => CurrentProfile switch
    {
        YoutubeClientProfile.AndroidVR => UaVr,
        YoutubeClientProfile.TV => UaTv,
        YoutubeClientProfile.Web => UaWeb,
        _ => UaVr
    };

    /// <summary>
    /// Нужно ли отправлять заголовок Authorization (SAPISIDHASH) и Cookie?
    /// VR и TV клиенты обычно работают без них (или ломаются с ними).
    /// </summary>
    public static bool RequiresAuth => CurrentProfile == YoutubeClientProfile.Web;

    // === METHODS ===

    public static string GeneratePlayerContext(string videoId, string? visitorData)
    {
        var hl = YoutubeHttpHandler.GetHl();
        var gl = YoutubeHttpHandler.GetGl();
        
        // Сериализуем строки заранее, чтобы избежать ошибок JSON-формата
        var vidJson = Json.Serialize(videoId);
        var vdJson = Json.Serialize(visitorData);
        
        return CurrentProfile switch
        {
            YoutubeClientProfile.AndroidVR => $$"""
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

            YoutubeClientProfile.TV => $$"""
            {
              "videoId": {{vidJson}},
              "context": {
                "client": {
                  "clientName": "TVHTML5_SIMPLY_EMBEDDED_PLAYER",
                  "clientVersion": "2.0",
                  "visitorData": {{vdJson}},
                  "hl": "en",
                  "gl": "US",
                  "utcOffsetMinutes": 0,
                  "platform": "TV"
                },
                "thirdParty": {
                  "embedUrl": "https://www.youtube.com/watch?v={{videoId}}"
                }
              }
            }
            """,

            _ => $$"""
            {
              "videoId": {{vidJson}},
              "contentCheckOk": true,
              "racyCheckOk": true,
              "context": {
                "client": {
                  "clientName": "WEB",
                  "clientVersion": "2.20240105.01.00",
                  "visitorData": {{vdJson}},
                  "hl": {{Json.Serialize(hl)}},
                  "gl": {{Json.Serialize(gl)}},
                  "utcOffsetMinutes": 0
                }
              }
            }
            """
        };
    }
}