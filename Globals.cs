using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace LMP;

public static class G
{
    public const string AppId = "LMP";
    public const string AppName = "Lite Music Player";
    public const string GitHubUrl = "https://github.com/Scream034/LMP";

    public static string DisplayGithubUrl => GitHubUrl[8..];

    public static class Build
    {
        private static readonly Lazy<BuildInfo> _info = new(LoadBuildInfo);
        
        public static BuildInfo Info => _info.Value;
        public static string Version => Info.Version;
        public static string GitHash => Info.GitHash;
        public static int CommitCount => Info.CommitCount;
        public static DateTime BuildDate => Info.BuildDate;
        
        public static bool IsDebug =>
#if DEBUG
            true;
#else
            false;
#endif

        public static int MinSplashTimeMs =>
#if DEBUG
            1000;
#else
            2000;
#endif

        public static string DisplayVersion => Info.DisplayVersion;
        public static string FullVersionString => Info.FullVersionString;

        private static BuildInfo LoadBuildInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version ?? new Version(1, 0, 0);
            
            var infoVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? version.ToString();
            
            // Парсим Git hash из "1.0.172+abc1234"
            var gitHash = "local";
            
            if (infoVersion.Contains('+'))
            {
                var hashPart = infoVersion.Split('+')[1].Trim();
                // Берём только первые 7 символов
                gitHash = hashPart.Length > 7 ? hashPart[..7] : hashPart;
            }
            
            // CommitCount = version.Build (третий компонент версии)
            var commitCount = version.Build;

            var buildDate = DateTime.Now;
            try
            {
                var location = assembly.Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                {
                    buildDate = File.GetLastWriteTime(location);
                }
            }
            catch { }

            // ═══ УПРОЩЁННАЯ ВЕРСИЯ: только коммиты ═══
            var displayVersion = IsDebug 
                ? $"#{commitCount}-dev" 
                : $"#{commitCount}";
            
            return new BuildInfo
            {
                Version = commitCount.ToString(),
                GitHash = gitHash,
                CommitCount = commitCount,
                BuildDate = buildDate,
                IsDebug = IsDebug,
                DisplayVersion = displayVersion,
                FullVersionString = $"{displayVersion} ({gitHash}) • {buildDate:yyyy-MM-dd}"
            };
        }
    }

    public sealed class BuildInfo
    {
        public required string Version { get; init; }
        public required string GitHash { get; init; }
        public required int CommitCount { get; init; }
        public required DateTime BuildDate { get; init; }
        public required bool IsDebug { get; init; }
        public required string DisplayVersion { get; init; }
        public required string FullVersionString { get; init; }
    }

    public static class SystemInfo
    {
        public static string DetectSystemLanguage()
        {
            try
            {
                var culture = CultureInfo.CurrentUICulture;
                var lang = culture.TwoLetterISOLanguageName.ToLowerInvariant();
                
                return lang switch
                {
                    "ru" => "ru",
                    "en" => "en",
                    _ => "en"
                };
            }
            catch
            {
                return "en";
            }
        }
    }

    public static class Folder
    {
        public static readonly string Data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppId);
        
        public static readonly string Cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppId);
        
        public static readonly string Logs = Path.Combine(Data, "Logs");
        public static readonly string Downloads = Path.Combine(Data, "Downloads");
        public static readonly string ImageCache = Path.Combine(Cache, "ImageCache");
        public static readonly string StreamCache = Path.Combine(Cache, "StreamCache");
        public static readonly string SearchCache = Path.Combine(Cache, "SearchCache");
        public static readonly string AudioCache = Path.Combine(Cache, "AudioCache");
        public static readonly string NTokenCache = Path.Combine(Cache, "NToken");
        public static readonly string SigCipherCache = Path.Combine(Cache, "SigCipher");

        public static void Create()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Cache);
            Directory.CreateDirectory(Downloads);
            Directory.CreateDirectory(ImageCache);
            Directory.CreateDirectory(StreamCache);
            Directory.CreateDirectory(SearchCache);
            Directory.CreateDirectory(AudioCache);
            Directory.CreateDirectory(NTokenCache);
            Directory.CreateDirectory(SigCipherCache);
            Directory.CreateDirectory(Logs);
        }
    }

    public static class FilePath
    {
        public static readonly string Bootstrap = Path.Combine(Folder.Data, "bootstrap.json");
        public static readonly string Cookie = Path.Combine(Folder.Data, "auth_cookies.txt");
        public static readonly string Library = Path.Combine(Folder.Data, "library.json");
        public static readonly string Database = Path.Combine(Folder.Data, "library.db");
        public static readonly string Theme = Path.Combine(Folder.Data, "theme.json");
        public static readonly string NTokenCache = Path.Combine(Folder.NTokenCache, "tokens.json");
        public static readonly string NTokenScript = Path.Combine(Folder.NTokenCache, "ntoken_override.js");
        public static readonly string SigCipherCache = Path.Combine(Folder.SigCipherCache, "sigcache.json");
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Beautiful = new()
        {
            WriteIndented = true,
        };

        public static readonly JsonSerializerOptions Compact = new()
        {
            WriteIndented = false,
        };
    }
}